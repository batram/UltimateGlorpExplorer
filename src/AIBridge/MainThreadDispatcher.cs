#if MONO
using System.Threading;

namespace UnityExplorer.AIBridge
{
    // Marshals work from the AI bridge's HTTP thread onto the Unity main thread.
    // Drain() is called every frame from ExplorerCore.Update().
    public static class MainThreadDispatcher
    {
        class WorkItem
        {
            public Func<object> Func;
            public readonly ManualResetEvent Done = new(false);
            public object Result;
            public Exception Error;
        }

        static readonly Queue<WorkItem> queue = new();

        // Called from the HTTP thread. Blocks until the main thread has run func.
        public static object Run(Func<object> func, int timeoutMs)
        {
            WorkItem item = new() { Func = func };
            lock (queue)
                queue.Enqueue(item);

            if (!item.Done.WaitOne(timeoutMs, false))
                throw new TimeoutException($"Main thread did not process the request within {timeoutMs}ms. Is the game paused or frozen?");

            if (item.Error != null)
                throw new TargetInvocationException(item.Error);

            return item.Result;
        }

        // Called from the Unity main thread once per frame.
        public static void Drain()
        {
            while (true)
            {
                WorkItem item;
                lock (queue)
                {
                    if (queue.Count == 0)
                        return;
                    item = queue.Dequeue();
                }

                try
                {
                    item.Result = item.Func();
                }
                catch (Exception ex)
                {
                    item.Error = ex;
                }

                item.Done.Set();
            }
        }
    }
}
#endif
