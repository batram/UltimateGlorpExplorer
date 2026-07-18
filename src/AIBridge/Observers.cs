#if MONO
using System.Collections;
using System.Threading;
using UnityExplorer.CSConsole;

namespace UnityExplorer.AIBridge
{
    // Observer feature: agents register expressions that are sampled every frame
    // in-game; value changes / condition edges are recorded into a cursored event
    // buffer the agent polls with get_events. wait_for is the blocking variant.
    // Observers do not survive a game restart (everything is in-memory).
    public static class Observers
    {
        const int MAX_EVENTS = 5000;

        class Observer
        {
            public int Id;
            public string Expression;
            public string Mode; // "change" or "true"
            public Mono.CSharp.CompiledMethod Compiled;
            public string LastValue;
            public bool FirstSample = true;
            public bool Active = true;
        }

        static readonly List<Observer> observers = new();
        static readonly List<Dictionary<string, object>> events = new();
        static long eventBaseIndex; // index of events[0], monotonic across eviction
        static int nextObserverId = 1;

        // ---- main-thread only ----

        internal static object Create(string expression, string mode)
        {
            mode = mode == "true" ? "true" : "change";

            Mono.CSharp.CompiledMethod compiled = ConsoleController.Evaluator.Compile(expression);
            if (compiled == null)
                return Error("Expression did not compile to a REPL expression (must be a single expression, no trailing ';').");

            Observer obs = new()
            {
                Id = nextObserverId++,
                Expression = expression,
                Mode = mode,
                Compiled = compiled,
            };
            observers.Add(obs);
            RuntimeHelper.StartCoroutine(ObserverCoroutine(obs));

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "id", obs.Id },
                { "mode", mode },
                { "note", "Events are recorded every frame; poll GET /events (get_events) with a 'since' cursor. Observers are lost on game restart." },
            };
        }

        internal static object List()
        {
            List<object> list = new();
            foreach (Observer obs in observers)
            {
                if (!obs.Active)
                    continue;
                list.Add(new Dictionary<string, object>
                {
                    { "id", obs.Id },
                    { "expression", obs.Expression },
                    { "mode", obs.Mode },
                    { "lastValue", obs.LastValue },
                });
            }
            return new Dictionary<string, object> { { "ok", true }, { "observers", list } };
        }

        internal static object Delete(int id)
        {
            Observer obs = observers.Find(it => it.Id == id && it.Active);
            if (obs == null)
                return Error($"No active observer with id {id}.");
            obs.Active = false;
            observers.Remove(obs);
            return new Dictionary<string, object> { { "ok", true } };
        }

        internal static object GetEvents(long since)
        {
            List<object> result = new();
            long total = eventBaseIndex + events.Count;
            for (long i = Math.Max(since, eventBaseIndex); i < total; i++)
                result.Add(events[(int)(i - eventBaseIndex)]);

            return new Dictionary<string, object>
            {
                { "ok", true },
                { "total", total },
                { "oldestAvailable", eventBaseIndex },
                { "events", result },
            };
        }

        static IEnumerator ObserverCoroutine(Observer obs)
        {
            while (obs.Active)
            {
                string value;
                try
                {
                    object ret = null;
                    obs.Compiled.Invoke(ref ret);
                    value = ret?.ToString() ?? "null";
                }
                catch (Exception ex)
                {
                    RecordEvent(obs, "error", $"{ex.GetType().Name}: {ex.Message}", obs.LastValue);
                    obs.Active = false;
                    observers.Remove(obs);
                    yield break;
                }

                bool trigger;
                if (obs.Mode == "true")
                    trigger = value == "True" && obs.LastValue != "True";
                else
                    trigger = !obs.FirstSample && value != obs.LastValue;

                if (trigger)
                    RecordEvent(obs, obs.Mode == "true" ? "condition" : "change", value, obs.LastValue);

                obs.LastValue = value;
                obs.FirstSample = false;
                yield return null;
            }
        }

        static void RecordEvent(Observer obs, string kind, string value, string previous)
        {
            events.Add(new Dictionary<string, object>
            {
                { "index", eventBaseIndex + events.Count },
                { "observerId", obs.Id },
                { "expression", obs.Expression },
                { "kind", kind },
                { "value", value },
                { "previous", previous },
                { "time", Time.time },
                { "frame", Time.frameCount },
                { "utc", DateTime.UtcNow.ToString("o") },
            });

            while (events.Count > MAX_EVENTS)
            {
                events.RemoveAt(0);
                eventBaseIndex++;
            }
        }

        // ---- wait_for: called from the HTTP thread ----

        internal static object WaitFor(string expression, string mode, int timeoutMs)
        {
            mode = mode == "change" ? "change" : "true";
            timeoutMs = Math.Max(1000, Math.Min(timeoutMs, 300000));

            Dictionary<string, object> outcome = null;
            Exception error = null;
            ManualResetEvent done = new(false);

            MainThreadDispatcher.Run(() =>
            {
                Mono.CSharp.CompiledMethod compiled = ConsoleController.Evaluator.Compile(expression);
                if (compiled == null)
                    throw new FormatException("Expression did not compile to a REPL expression (must be a single expression, no trailing ';').");
                RuntimeHelper.StartCoroutine(WaitForCoroutine(compiled, mode, timeoutMs, r => outcome = r, e => error = e, done));
                return null;
            }, AIBridgeServer.DISPATCH_TIMEOUT_MS);

            if (!done.WaitOne(timeoutMs + 5000, false))
                throw new TimeoutException("wait_for coroutine did not report back in time.");
            if (error != null)
                throw new TargetInvocationException(error);

            outcome["ok"] = true;
            outcome["expression"] = expression;
            outcome["mode"] = mode;
            return outcome;
        }

        static IEnumerator WaitForCoroutine(Mono.CSharp.CompiledMethod compiled, string mode, int timeoutMs,
            Action<Dictionary<string, object>> onDone, Action<Exception> onError, ManualResetEvent done)
        {
            float deadline = Time.realtimeSinceStartup + timeoutMs / 1000f;
            string baseline = null;
            bool first = true;
            int framesWaited = 0;

            while (Time.realtimeSinceStartup < deadline)
            {
                string value;
                try
                {
                    object ret = null;
                    compiled.Invoke(ref ret);
                    value = ret?.ToString() ?? "null";
                }
                catch (Exception ex)
                {
                    onError(ex);
                    done.Set();
                    yield break;
                }

                bool triggered = mode == "change"
                    ? !first && value != baseline
                    : value == "True";

                if (triggered)
                {
                    onDone(new Dictionary<string, object>
                    {
                        { "triggered", true },
                        { "value", value },
                        { "previous", baseline },
                        { "framesWaited", framesWaited },
                        { "time", Time.time },
                    });
                    done.Set();
                    yield break;
                }

                if (first || mode == "change")
                    baseline = value;
                first = false;
                framesWaited++;
                yield return null;
            }

            onDone(new Dictionary<string, object>
            {
                { "triggered", false },
                { "framesWaited", framesWaited },
                { "note", "Timeout reached before the condition triggered." },
            });
            done.Set();
        }

        static Dictionary<string, object> Error(string message)
            => new() { { "ok", false }, { "error", message } };
    }
}
#endif
