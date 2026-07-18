#if MONO
using UniverseLib.Runtime;

namespace UnityExplorer.AIBridge
{
    // Texture/sprite pipeline tools: export any texture as PNG, replace a texture
    // in-place from PNG bytes, and dump a sprite-sheet manifest (name/rect/pivot/PPU
    // per sprite). All PNG data travels over the wire (raw bytes on REST, base64 on
    // MCP) — no shared filesystem between agent and game is assumed.
    // All methods must run on the Unity main thread.
    public static class TextureTools
    {
        #region Export

        internal static byte[] ExportTexturePng(int id)
        {
            Texture2D tex = ResolveTexture2D(id, out string error);
            if (tex == null)
                throw new ArgumentException(error);
            return EncodePng(tex);
        }

        // MCP variant: PNG as base64 plus metadata, so the bytes travel inside the JSON-RPC response.
        internal static object ExportTexture(int id)
        {
            Texture2D tex = ResolveTexture2D(id, out string error);
            if (tex == null)
                return Error(error);

            byte[] png = EncodePng(tex);
            return new Dictionary<string, object>
            {
                { "ok", true },
                { "textureId", tex.GetInstanceID() },
                { "name", tex.name },
                { "width", tex.width },
                { "height", tex.height },
                { "format", tex.format.ToString() },
                { "png_base64", Convert.ToBase64String(png) },
            };
        }

        #endregion


        #region Replace

        internal static object ReplaceTexture(int id, byte[] png)
        {
            if (png == null || png.Length == 0)
                return Error("Empty PNG data.");

            Texture2D tex = ResolveTexture2D(id, out string error);
            if (tex == null)
                return Error(error);

            int oldWidth = tex.width, oldHeight = tex.height;
            string method = "LoadImage";

            // LoadImage replaces the texture storage in-place, so every renderer,
            // material and sprite referencing this instance updates immediately.
            // It usually works even on non-readable game textures.
            if (!TryLoadImage(tex, png))
            {
                // Fallback: decode into a temp texture and GPU-copy (needs identical dimensions).
                Texture2D temp = new(2, 2, TextureFormat.RGBA32, false);
                try
                {
                    if (!TryLoadImage(temp, png))
                        return Error("Could not decode the PNG data.");
                    if (temp.width != oldWidth || temp.height != oldHeight)
                        return Error($"LoadImage failed and the PNG is {temp.width}x{temp.height} while the target is " +
                            $"{oldWidth}x{oldHeight}; the GPU-copy fallback requires identical dimensions.");
                    if (!TryGpuCopy(temp, tex))
                        return Error("LoadImage failed and Graphics.CopyTexture is unavailable or failed on this Unity version.");
                    method = "CopyTexture";
                }
                finally
                {
                    UnityEngine.Object.Destroy(temp);
                }
            }

            Dictionary<string, object> result = new()
            {
                { "ok", true },
                { "textureId", tex.GetInstanceID() },
                { "name", tex.name },
                { "method", method },
                { "width", tex.width },
                { "height", tex.height },
            };
            if (tex.width != oldWidth || tex.height != oldHeight)
                result.Add("note", $"Texture was resized from {oldWidth}x{oldHeight}; existing sprite rects still use the old " +
                    "pixel coordinates, so keep replacement sheets at the original size unless you know what you're doing.");
            return result;
        }

        // Unity moved LoadImage from an instance method to the ImageConversion extension
        // class over the years; try both via reflection.
        static MethodInfo loadImageExt, loadImageInstance;
        static bool loadImageResolved;

        static bool TryLoadImage(Texture2D tex, byte[] data)
        {
            if (!loadImageResolved)
            {
                loadImageResolved = true;
                Type conversion = ReflectionUtility.GetTypeByName("UnityEngine.ImageConversion");
                loadImageExt = conversion?.GetMethod("LoadImage", new[] { typeof(Texture2D), typeof(byte[]), typeof(bool) });
                loadImageInstance = typeof(Texture2D).GetMethod("LoadImage", new[] { typeof(byte[]) });
            }

            try
            {
                if (loadImageExt != null)
                    return (bool)loadImageExt.Invoke(null, new object[] { tex, data, false });
                if (loadImageInstance != null)
                    return (bool)loadImageInstance.Invoke(tex, new object[] { data });
            }
            catch
            {
                return false;
            }
            return false;
        }

        // Graphics.CopyTexture arrived in Unity 5.4+; the compile-time reference is older,
        // so resolve it at runtime.
        static bool TryGpuCopy(Texture2D src, Texture2D dst)
        {
            try
            {
                MethodInfo copy = typeof(Graphics).GetMethod("CopyTexture", new[] { typeof(Texture), typeof(Texture) });
                if (copy == null)
                    return false;
                copy.Invoke(null, new object[] { src, dst });
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion


        #region Sprite manifest

        // Everything an agent needs to rebuild a sprite sheet: for each sprite on the
        // texture, its name, rect (both Unity bottom-left and PNG top-left origin),
        // pivot, pixels-per-unit and 9-slice border.
        internal static object ExportSprites(int textureId, bool includePng)
        {
            Texture2D tex = ResolveTexture2D(textureId, out string error);
            if (tex == null)
                return Error(error);

            List<object> sprites = new();
            foreach (UnityEngine.Object obj in RuntimeHelper.FindObjectsOfTypeAll(typeof(Sprite)))
            {
                Sprite sprite = obj.TryCast<Sprite>();
                if (sprite == null || sprite.texture == null || sprite.texture.GetInstanceID() != tex.GetInstanceID())
                    continue;

                // textureRect is the actual packed location but throws for tightly-packed
                // atlas sprites; rect is the pre-packing rect.
                Rect rect;
                try { rect = sprite.textureRect; }
                catch { rect = sprite.rect; }

                Vector2 pivot = sprite.pivot; // pixels, relative to rect bottom-left
                Vector4 border = sprite.border;

                sprites.Add(new Dictionary<string, object>
                {
                    { "name", sprite.name },
                    { "id", sprite.GetInstanceID() },
                    // Unity convention: origin bottom-left of the texture.
                    { "rect", new Dictionary<string, object> { { "x", rect.x }, { "y", rect.y }, { "w", rect.width }, { "h", rect.height } } },
                    // Same rect with y measured from the top, i.e. directly usable as PNG pixel coordinates.
                    { "pngRect", new Dictionary<string, object> { { "x", rect.x }, { "y", tex.height - rect.y - rect.height }, { "w", rect.width }, { "h", rect.height } } },
                    { "pivotPixels", new Dictionary<string, object> { { "x", pivot.x }, { "y", pivot.y } } },
                    { "pivotNormalized", new Dictionary<string, object>
                        {
                            { "x", rect.width > 0 ? pivot.x / rect.width : 0f },
                            { "y", rect.height > 0 ? pivot.y / rect.height : 0f },
                        } },
                    { "pixelsPerUnit", sprite.pixelsPerUnit },
                    { "border", new Dictionary<string, object> { { "left", border.x }, { "bottom", border.y }, { "right", border.z }, { "top", border.w } } },
                    { "packed", sprite.packed },
                });
            }

            Dictionary<string, object> result = new()
            {
                { "ok", true },
                { "texture", new Dictionary<string, object>
                    {
                        { "id", tex.GetInstanceID() },
                        { "name", tex.name },
                        { "width", tex.width },
                        { "height", tex.height },
                    } },
                { "spriteCount", sprites.Count },
                { "sprites", sprites },
            };
            if (includePng)
                result.Add("png_base64", Convert.ToBase64String(EncodePng(tex)));
            return result;
        }

        #endregion


        #region Helpers

        // Accepts the id of a Texture2D directly, or of a Sprite / Material / SpriteRenderer /
        // UI.Image — anything from which the underlying sheet texture is obvious.
        static Texture2D ResolveTexture2D(int id, out string error)
        {
            error = null;
            UnityEngine.Object obj = BridgeTools.FindObjectById(id);
            if (obj == null)
            {
                error = $"No UnityEngine.Object found with instance id {id}.";
                return null;
            }

            if (obj.TryCast<Texture2D>() is Texture2D tex)
                return tex;
            if (obj.TryCast<Sprite>() is Sprite sprite && sprite.texture != null)
                return sprite.texture;
            if (obj.TryCast<Material>() is Material mat && mat.mainTexture is Texture2D mainTex)
                return mainTex;
            if (obj.TryCast<SpriteRenderer>() is SpriteRenderer sr && sr.sprite != null && sr.sprite.texture != null)
                return sr.sprite.texture;
            if (obj.TryCast<UnityEngine.UI.Image>() is UnityEngine.UI.Image img && img.sprite != null && img.sprite.texture != null)
                return img.sprite.texture;

            error = $"Object {id} is a {obj.GetActualType().FullName}; expected a Texture2D " +
                "(or a Sprite/Material/SpriteRenderer/Image to take the texture from).";
            return null;
        }

        // Game textures are usually not CPU-readable; TextureHelper.CopyTexture blits
        // through a RenderTexture to produce a readable copy.
        static byte[] EncodePng(Texture2D tex)
        {
            Texture2D readable = TextureHelper.CopyTexture(tex, new Rect(0, 0, tex.width, tex.height));
            try
            {
                string tmp = Path.Combine(Path.GetTempPath(), $"ue_aibridge_texture_{Guid.NewGuid():N}.png");
                try
                {
                    TextureHelper.SaveTextureAsPNG(readable, tmp);
                    return File.ReadAllBytes(tmp);
                }
                finally
                {
                    if (File.Exists(tmp))
                        File.Delete(tmp);
                }
            }
            finally
            {
                if (readable != tex)
                    UnityEngine.Object.Destroy(readable);
            }
        }

        static Dictionary<string, object> Error(string message)
            => new() { { "ok", false }, { "error", message } };

        #endregion
    }
}
#endif
