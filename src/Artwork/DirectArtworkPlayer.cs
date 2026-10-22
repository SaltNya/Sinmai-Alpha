using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;
using UnityEngine.Video;

namespace SinmaiAlpha.Artwork
{
    // All existing RawImage/Image/SpriteRenderer consumers share a stable texture.
    // Only visible artwork decodes; a hidden entry retains its last poster but
    // releases the VideoPlayer/render target/GIF buffers after a short grace period.
    public sealed class DirectArtworkPlayer : MonoBehaviour
    {
        private sealed class Entry
        {
            internal string Path;
            internal Texture2D Texture, Fallback;
            internal GifAnimation Gif;
            internal VideoPlayer Video;
            internal RenderTexture Target;
            internal bool IsGif, Visible, Failed;
            internal float LastSeen, Elapsed, PrepareStarted;
            internal long LastFrame = -1;
            internal string PendingError;
        }
        private static DirectArtworkPlayer instance;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<Texture, Entry> textures = new Dictionary<Texture, Entry>();
        private readonly Dictionary<Sprite, Sprite> expandedSprites = new Dictionary<Sprite, Sprite>();
        private readonly HashSet<Sprite> ownedSprites = new HashSet<Sprite>();
        private readonly HashSet<string> rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private float nextVisibilityScan;

        public static Texture2D Load(string path, bool frame, Func<Texture2D> fallback)
        {
            if (instance == null)
            {
                var host = new GameObject("Direct jacket and frame media");
                host.hideFlags = HideFlags.HideInHierarchy;
                if (Application.isPlaying) DontDestroyOnLoad(host);
                instance = host.AddComponent<DirectArtworkPlayer>();
            }
            if (instance.entries.TryGetValue(path, out var cached)) return cached.Texture;
            if (instance.rejected.Contains(path)) return null;
            var entry = new Entry { Path = path, IsGif = string.Equals(System.IO.Path.GetExtension(path), ".gif", StringComparison.OrdinalIgnoreCase), LastSeen = Time.unscaledTime };
            try
            {
                if (entry.IsGif)
                {
                    entry.Gif = ReadGif(path);
                    entry.Texture = NewTexture(entry.Gif.Width, entry.Gif.Height, path);
                    UploadGif(entry);
                }
                else
                {
                    entry.Fallback = fallback != null ? fallback() : null;
                    // Preserve the established artwork canvas; async video prepare
                    // must not resize a texture after callers have created sprites.
                    float aspect = entry.Fallback != null ? (float)entry.Fallback.width / entry.Fallback.height : (frame ? 2f / 7f : 1f);
                    int edge = frame ? 1024 : 512;
                    int width = aspect >= 1 ? edge : Mathf.Max(1, Mathf.RoundToInt(edge * aspect));
                    int height = aspect >= 1 ? Mathf.Max(1, Mathf.RoundToInt(edge / aspect)) : edge;
                    // Match the legacy ARGB32 render target for GPU copies.
                    // GIF uploads remain RGBA32 because their bytes are RGBA.
                    entry.Texture = NewTexture(width, height, path, TextureFormat.ARGB32);
                    // Readable opaque pixels also give runtime-created sprites a
                    // complete mesh before the first video frame becomes available.
                    var pixels = new Color32[width * height];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(32, 32, 32, 255);
                    entry.Texture.SetPixels32(pixels); entry.Texture.Apply(false);
                    if (entry.Fallback != null) CopyFrame(entry, entry.Fallback);
                    ReleaseTarget(entry);
                }
                instance.entries.Add(path, entry);
                instance.textures.Add(entry.Texture, entry);
                instance.nextVisibilityScan = 0;
                return entry.Texture;
            }
            catch (Exception ex)
            {
                instance.rejected.Add(path);
                ReleaseDecoder(entry);
                if (entry.Texture != null) DestroyOwned(entry.Texture);
                Debug.LogWarning("[Sinmai-Alpha/Artwork] Cannot load artwork " + path + ": " + ex.Message);
                return null;
            }
        }

        private static Texture2D NewTexture(int width, int height, string path, TextureFormat format = TextureFormat.RGBA32)
        {
            return new Texture2D(width, height, format, false) {
                name = "DirectArtwork_" + System.IO.Path.GetFileName(path),
                wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear,
                hideFlags = HideFlags.HideAndDontSave
            };
        }
        private static GifAnimation ReadGif(string path)
        {
            if (new FileInfo(path).Length > GifAnimation.MaxFileBytes) throw new InvalidDataException("GIF exceeds 64 MiB.");
            return new GifAnimation(File.ReadAllBytes(path));
        }
        private static void UploadGif(Entry entry)
        {
            entry.Texture.LoadRawTextureData(entry.Gif.Pixels);
            entry.Texture.Apply(false);
        }

        private void Update() { Tick(Time.unscaledTime, Time.unscaledDeltaTime); }

        private void Tick(float now, float delta)
        {
            if (now >= nextVisibilityScan)
            {
                ScanVisibility(now);
                nextVisibilityScan = now + .25f;
            }
            foreach (Entry entry in entries.Values)
            {
                if (entry.Failed) continue;
                if (entry.PendingError != null) { Fail(entry, entry.PendingError); continue; }
                if (!entry.Visible)
                {
                    if (entry.Video != null && entry.Video.isPlaying) entry.Video.Pause();
                    if (now - entry.LastSeen > 2f) ReleaseDecoder(entry);
                    continue;
                }
                try
                {
                    if (entry.IsGif)
                    {
                        if (entry.Gif == null) { entry.Gif = ReadGif(entry.Path); entry.Elapsed = 0; UploadGif(entry); }
                        if (entry.Gif.FrameCount == 1) continue;
                        entry.Elapsed += delta;
                        int advanced = 0;
                        while (entry.Elapsed >= entry.Gif.Delay && advanced < 4)
                        {
                            entry.Elapsed -= entry.Gif.Delay;
                            entry.Gif.Advance(); advanced++;
                        }
                        if (advanced > 0) UploadGif(entry);
                        if (advanced == 4) entry.Elapsed = 0;
                    }
                    else
                    {
                        if (entry.Video == null) StartVideo(entry, now);
                        if (entry.Video == null || entry.Failed) continue;
                        if (!entry.Video.isPrepared)
                        {
                            if (now - entry.PrepareStarted > 15f) Fail(entry, "Video preparation timed out.");
                            continue;
                        }
                        if (!entry.Video.isPlaying) entry.Video.Play();
                        if (entry.Video.texture != null && entry.Video.frame != entry.LastFrame)
                        {
                            CopyFrame(entry, entry.Video.texture);
                            entry.LastFrame = entry.Video.frame;
                        }
                    }
                }
                catch (Exception ex) { Fail(entry, ex.Message); }
            }
        }

        private void StartVideo(Entry entry, float now)
        {
            var host = new GameObject("Artwork video");
            host.hideFlags = HideFlags.HideAndDontSave;
            host.transform.SetParent(transform, false);
            entry.Video = host.AddComponent<VideoPlayer>();
            entry.Video.playOnAwake = false;
            entry.Video.source = VideoSource.Url;
            entry.Video.renderMode = VideoRenderMode.APIOnly;
            entry.Video.audioOutputMode = VideoAudioOutputMode.None;
            entry.Video.isLooping = true;
            entry.Video.waitForFirstFrame = true;
            entry.Video.skipOnDrop = true;
            entry.Video.timeReference = VideoTimeReference.Freerun;
            entry.Video.url = new Uri(entry.Path).AbsoluteUri;
            entry.Video.errorReceived += OnVideoError;
            entry.PrepareStarted = now;
            entry.LastFrame = -1;
            entry.Video.Prepare();
        }

        private void OnVideoError(VideoPlayer player, string message)
        {
            foreach (Entry entry in entries.Values)
                if (entry.Video == player) { entry.PendingError = message; break; }
        }
        private void Fail(Entry entry, string message)
        {
            if (entry.Failed) return;
            entry.Failed = true;
            ReleaseDecoder(entry);
            if (entry.Fallback != null)
            {
                try { CopyFrame(entry, entry.Fallback); }
                catch (Exception) { /* Keep the last valid poster if graphics is unavailable. */ }
                ReleaseTarget(entry);
            }
            Debug.LogWarning("[Sinmai-Alpha/Artwork] Artwork playback failed; keeping poster: " + entry.Path + ": " + message);
        }

        private static void CopyFrame(Entry entry, Texture source)
        {
            if (entry.Target == null)
            {
                entry.Target = new RenderTexture(entry.Texture.width, entry.Texture.height, 0,
                    RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB) { hideFlags = HideFlags.HideAndDontSave };
                entry.Target.Create();
            }
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(source, entry.Target);
                if ((SystemInfo.copyTextureSupport & CopyTextureSupport.RTToTexture) != 0)
                    Graphics.CopyTexture(entry.Target, entry.Texture);
                else
                {
                    RenderTexture.active = entry.Target;
                    entry.Texture.ReadPixels(new Rect(0, 0, entry.Texture.width, entry.Texture.height), 0, 0, false);
                    entry.Texture.Apply(false);
                }
            }
            finally { RenderTexture.active = previous; }
        }

        private void ScanVisibility(float now)
        {
            foreach (Entry entry in entries.Values) entry.Visible = false;
            foreach (Canvas canvas in FindObjectsOfType<Canvas>())
            {
                if (!canvas.isActiveAndEnabled) continue;
                var graphics = GraphicRegistry.GetGraphicsForCanvas(canvas);
                for (int i = 0; i < graphics.Count; i++)
                {
                    Graphic graphic = graphics[i];
                    if (graphic == null || !graphic.isActiveAndEnabled || graphic.canvasRenderer.cull || graphic.canvasRenderer.GetInheritedAlpha() <= .001f) continue;
                    if (MarkVisible(graphic.mainTexture, now) && graphic is Image image)
                    {
                        Sprite sprite = image.overrideSprite != null ? image.overrideSprite : image.sprite;
                        Sprite fullRect = ExpandSprite(sprite);
                        if (fullRect != sprite)
                        {
                            if (image.overrideSprite == image.sprite) image.sprite = fullRect;
                            else image.overrideSprite = fullRect;
                        }
                    }
                }
            }
            foreach (SpriteRenderer renderer in FindObjectsOfType<SpriteRenderer>())
                if (renderer.enabled && renderer.isVisible && renderer.color.a > .001f && renderer.sprite != null && MarkVisible(renderer.sprite.texture, now))
                    renderer.sprite = ExpandSprite(renderer.sprite);
        }
        private bool MarkVisible(Texture texture, float now)
        {
            if (texture == null || !textures.TryGetValue(texture, out var entry)) return false;
            entry.Visible = true; entry.LastSeen = now; return true;
        }
        private Sprite ExpandSprite(Sprite sprite)
        {
            if (sprite == null || ownedSprites.Contains(sprite)) return sprite;
            if (expandedSprites.TryGetValue(sprite, out var fullRect) && fullRect != null) return fullRect;
            // A transparent first GIF frame must not cut off later frames through
            // Sprite.Create's default Tight mesh. Retain pivot, PPU and rect.
            Vector2 pivot = new Vector2(sprite.pivot.x / sprite.rect.width, sprite.pivot.y / sprite.rect.height);
            fullRect = Sprite.Create(sprite.texture, sprite.rect, pivot, sprite.pixelsPerUnit, 0, SpriteMeshType.FullRect, sprite.border);
            fullRect.name = "DirectArtworkSprite";
            fullRect.hideFlags = HideFlags.HideAndDontSave;
            expandedSprites[sprite] = fullRect; ownedSprites.Add(fullRect);
            return fullRect;
        }

        private static void ReleaseTarget(Entry entry)
        {
            if (entry.Target == null) return;
            entry.Target.Release(); DestroyOwned(entry.Target); entry.Target = null;
        }
        private static void ReleaseDecoder(Entry entry)
        {
            entry.Gif = null; entry.Elapsed = 0;
            if (entry.Video != null)
            {
                if (instance != null) entry.Video.errorReceived -= instance.OnVideoError;
                entry.Video.Stop(); DestroyOwned(entry.Video.gameObject); entry.Video = null;
            }
            ReleaseTarget(entry);
        }
        private void OnDestroy()
        {
            ReleaseAll();
        }

        internal void ReleaseAll()
        {
            foreach (Entry entry in entries.Values) { ReleaseDecoder(entry); if (entry.Texture != null) DestroyOwned(entry.Texture); }
            foreach (Sprite sprite in ownedSprites) if (sprite != null) DestroyOwned(sprite);
            entries.Clear(); textures.Clear(); expandedSprites.Clear(); ownedSprites.Clear();
            if (instance == this) instance = null;
        }

        private static void DestroyOwned(UnityEngine.Object value)
        {
            if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
        }
    }
}
