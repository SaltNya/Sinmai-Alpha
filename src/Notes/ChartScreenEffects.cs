using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Notes.Libs;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

// A private image-space frame for one native player/reader. Input, judgment,
// clocks and score remain native. Never sample the other player's viewport.
internal sealed class ChartScreenEffects : IDisposable
{
    private static readonly string[] Kinds = { "gaussian", "neon", "trail", "brightness", "saturation", "contrast", "rainbow", "vignette", "glitch", "tvnoise", "hue", "flash", "tint", "shake" };
    private readonly IDictionary<string, PresentationCommands.Track> tracks;
    private Material effect, copy;
    private RenderTexture input, output;
    private readonly RenderTexture[] history = new RenderTexture[3];
    private bool acquired, failed, trailActive;
    private int trailSeed, trailFrames;
    private double previousTime = double.NegativeInfinity;
    private PresentationChange tintSource;
    private Vector4 tintColor;

    internal ChartScreenEffects(IDictionary<string, PresentationCommands.Track> tracks)
    {
        this.tracks = tracks;
        // Compile and allocate the shared material when the chart is bound,
        // rather than at the first effect, which may be at the song's ending.
        var previous = RenderTexture.active;
        RenderTexture warm = null;
        try
        {
            EnsureMaterials();
            warm = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32);
            effect.SetVector("_PlayfieldRect", new Vector4(0, 0, 1, 1));
            foreach (var filter in ExtraScreenFilters.All) if (tracks.ContainsKey(filter.Kind)) effect.SetFloat(filter.Uniform, 1);
            Graphics.Blit(Texture2D.blackTexture, warm, effect);
            foreach (var filter in ExtraScreenFilters.All) effect.SetFloat(filter.Uniform, 0);
        }
        catch (Exception error)
        { failed = true; MelonLogger.Warning("[Chart Effects] Warmup unavailable: " + error.Message); Dispose(); }
        finally { if (warm != null) RenderTexture.ReleaseTemporary(warm); RenderTexture.active = previous; }
    }
    internal static bool HasTracks(IDictionary<string, PresentationCommands.Track> tracks)
    { if (tracks == null) return false; foreach (var kind in Kinds) if (tracks.ContainsKey(kind)) return true; foreach (var filter in ExtraScreenFilters.All) if (tracks.ContainsKey(filter.Kind)) return true; return false; }
    private float Value(string kind, double now) => tracks.TryGetValue(kind, out var track) ? track.Evaluate(now) : 0;
    private bool Upcoming(double now) => tracks.TryGetValue("trail", out var track) && track.IsUpcoming(now, .5f);

    internal bool TryDraw(RenderTexture source, RenderTexture destination, PresentationRect bounds, PresentationRect visible, PresentationRect playfield, double now)
    {
        if (failed || !bounds.HasArea || !visible.HasArea) return false;
        var previous = RenderTexture.active;
        try
        {
            var seconds = (float)(now * .001); var priorSeconds = (float)(previousTime * .001);
            var advanced = seconds != priorSeconds;
            if (seconds < priorSeconds || seconds - priorSeconds > .5f) ReleaseHistory();
            previousTime = now;
            var blur = Value("gaussian", now); var neon = Value("neon", now);
            var trail = Mathf.Clamp01(Value("trail", now)); var brightness = Value("brightness", now);
            var saturation = Mathf.Clamp01(Value("saturation", now)); var contrast = Value("contrast", now);
            var rainbow = Mathf.Clamp01(Value("rainbow", now)); var vignette = Mathf.Clamp01(Value("vignette", now));
            var glitch = Mathf.Clamp01(Value("glitch", now)); var tv = Mathf.Clamp01(Value("tvnoise", now));
            var hue = Value("hue", now) * Mathf.Deg2Rad; var flash = Mathf.Clamp(Value("flash", now), -1, 1);
            var tint = Tint(now); var offset = Shake(now);
            var active = Mathf.Abs(blur) > .001f || Mathf.Abs(neon) > .001f || trail > .001f || Mathf.Abs(brightness) > .001f || saturation > .001f ||
                Mathf.Abs(contrast) > .001f || rainbow > .001f || vignette > .001f || glitch > .001f || tv > .001f ||
                Mathf.Abs(hue) > .0001f || Mathf.Abs(flash) > .001f || tint.w > .001f ||
                Mathf.Abs(offset.x) > .0001f || Mathf.Abs(offset.y) > .0001f;
            foreach (var filter in ExtraScreenFilters.All) active |= Mathf.Abs(Value(filter.Kind, now)) > .0001f;
            var hasTrail = tracks.ContainsKey("trail");

            EnsureMaterials();
            var width = Math.Max(1, Mathf.RoundToInt(source.width * bounds.Width));
            var height = Math.Max(1, Mathf.RoundToInt(source.height * bounds.Height));
            EnsureTargets(width, height, source.format);
            // Allocate at the initial inactive chart frame, never on the first
            // SHAKE/GLITCH. Keep the same full-resolution rendering semantics.
            if (hasTrail) EnsureHistory();
            if (!active && !hasTrail) { trailActive = false; trailSeed = 0; return false; }
            Capture(source, bounds, visible);
            if (hasTrail)
            {
                EnsureHistory();
                if (trailSeed < 3)
                {
                    for (var i = 0; i < history.Length; i++) Graphics.Blit(input, history[i]);
                    trailSeed = 3; trailFrames = 0;
                }
                trailActive = true;
            }
            else
            {
                trailActive = false; trailSeed = 0;
            }
            // Reference history follows raw frames throughout a chart with Trail,
            // including inactive spans, and advances only when chart time changes.
            if (!active) { AdvanceHistory(advanced); return false; }
            effect.SetFloat("_Blur", blur); effect.SetFloat("_Neon", neon); effect.SetFloat("_Trail", trail);
            effect.SetFloat("_Brightness", brightness); effect.SetFloat("_Saturation", saturation); effect.SetFloat("_Contrast", contrast);
            effect.SetFloat("_Rainbow", rainbow); effect.SetFloat("_Vignette", vignette); effect.SetFloat("_Glitch", glitch);
            PresentationChange vignetteSource = null;
            if (tracks.TryGetValue("vignette", out var vignetteTrack)) vignetteTrack.Evaluate(now, out vignetteSource);
            effect.SetFloat("_VignetteGradient", vignetteSource != null && vignetteSource.Gradient ? 1 : 0);
            effect.SetFloat("_TVNoise", tv); effect.SetFloat("_Hue", hue); effect.SetFloat("_Flash", flash);
            effect.SetFloat("_EffectTime", (float)(now * .001)); effect.SetVector("_TintColor", tint);
            effect.SetFloat("_OffsetX", offset.x); effect.SetFloat("_OffsetY", offset.y);
            // MOVE/ROTATE/ZOOM have already transformed native display objects.
            effect.SetFloat("_Rotate", 0); effect.SetFloat("_Zoom", 0); effect.SetFloat("_Fade", 0);
            effect.SetFloat("_AlphaClipSpace", 0);
            var field = Relative(playfield, bounds);
            effect.SetVector("_PlayfieldRect", new Vector4(field.x, field.y, field.xMax, field.yMax));
            effect.SetVector("_MainTex_TexelSize", new Vector4(1f / width, 1f / height, width, height));
            foreach (var filter in ExtraScreenFilters.All)
            {
                PresentationChange dominant = null;
                var amount = tracks.TryGetValue(filter.Kind, out var filterTrack) ? filterTrack.Evaluate(now, out dominant) : 0;
                effect.SetFloat(filter.Uniform, amount);
                if (filter.Kind == "shatter" || filter.Kind == "ripple")
                    effect.SetFloat(filter.Uniform + "Phase", dominant == null ? 0 : (float)(now * .001) - (float)(dominant.Time * .001));
            }
            for (var i = 0; i < 3; i++) effect.SetTexture("_HistoryTex" + (i == 0 ? "" : (i + 1).ToString()), trailActive ? (Texture)history[i] : Texture2D.blackTexture);
            Graphics.Blit(input, output, effect);
            AdvanceHistory(advanced);
            var uv = Relative(visible, bounds);
            Draw(destination, PixelRect(visible, source.width, source.height), output, uv);
            return true;
        }
        catch (Exception error)
        {
            failed = true;
            MelonLogger.Warning("[Chart Effects] Player frame effects unavailable: " + error.Message);
            Dispose(); return false;
        }
        finally { RenderTexture.active = previous; }
    }

    private void AdvanceHistory(bool advanced)
    {
        if (!trailActive || !advanced || ++trailFrames < 5) return;
        Graphics.Blit(history[1], history[2]); Graphics.Blit(history[0], history[1]); Graphics.Blit(input, history[0]); trailFrames = 0;
    }
    private Vector2 Shake(double now)
    {
        if (!tracks.TryGetValue("shake", out var track)) return Vector2.zero;
        var amount = track.Evaluate(now, out var source);
        if (source == null || Mathf.Abs(amount) <= .0001f) return Vector2.zero;
        var phase = (float)(now * .001) * (source.Frequency > 0 ? source.Frequency : 18);
        var x = (Mathf.PerlinNoise(phase, .37f) - .5f) * 2 * amount * .05f;
        return source.HasDirection ? new Vector2(Mathf.Cos(source.Direction) * x, Mathf.Sin(source.Direction) * x) :
            new Vector2(x, (Mathf.PerlinNoise(.71f, phase) - .5f) * 2 * amount * .05f);
    }
    private Vector4 Tint(double now)
    {
        if (!tracks.TryGetValue("tint", out var track)) return Vector4.zero;
        var amount = Mathf.Clamp01(track.Evaluate(now, out var source));
        if (amount <= .001f || source == null || string.IsNullOrEmpty(source.ColorHex)) return Vector4.zero;
        if (tintSource != source)
        {
            var rgb = int.Parse(source.ColorHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            tintColor = new Vector4(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 0); tintSource = source;
        }
        var result = tintColor; result.w = amount; return result;
    }
    private void EnsureMaterials()
    {
        if (effect != null && copy != null) return;
        var shader = EffectAssets.Acquire(); acquired = true;
        effect = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
        copy = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
    }
    private void EnsureTargets(int width, int height, RenderTextureFormat format)
    {
        if (input != null && input.width == width && input.height == height && input.format == format) return;
        Release(ref input); Release(ref output); ReleaseHistory();
        input = Create(width, height, format); output = Create(width, height, format);
    }
    private static RenderTexture Create(int width, int height, RenderTextureFormat format)
    {
        var texture = new RenderTexture(width, height, 0, format) { antiAliasing = 1, hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        if (!texture.Create()) { Object.Destroy(texture); throw new InvalidOperationException("RenderTexture creation failed"); }
        return texture;
    }
    private void EnsureHistory()
    {
        if (history[0] != null) return;
        for (var i = 0; i < history.Length; i++) { history[i] = Create(input.width, input.height, input.format); history[i].filterMode = FilterMode.Point; history[i].wrapMode = TextureWrapMode.Repeat; Graphics.Blit(Texture2D.blackTexture, history[i]); }
    }
    private void Capture(RenderTexture source, PresentationRect bounds, PresentationRect visible)
    {
        var uv = Relative(visible, bounds);
        var pixels = new Rect(uv.x * input.width, (1 - uv.y - uv.height) * input.height, uv.width * input.width, uv.height * input.height);
        Graphics.SetRenderTarget(input); GL.Clear(false, true, Color.black);
        Draw(input, pixels, source, new Rect(visible.X, visible.Y, visible.Width, visible.Height));
    }
    private static Rect Relative(PresentationRect visible, PresentationRect bounds) =>
        new((visible.X - bounds.X) / bounds.Width, (visible.Y - bounds.Y) / bounds.Height, visible.Width / bounds.Width, visible.Height / bounds.Height);
    private static Rect PixelRect(PresentationRect rect, int width, int height) => new(rect.X * width, (1 - rect.Top) * height, rect.Width * width, rect.Height * height);
    private void Draw(RenderTexture target, Rect pixels, Texture texture, Rect uv)
    {
        Graphics.SetRenderTarget(target); GL.PushMatrix();
        try { GL.LoadPixelMatrix(0, target != null ? target.width : Screen.width, target != null ? target.height : Screen.height, 0); Graphics.DrawTexture(pixels, texture, uv, 0, 0, 0, 0, Color.white, copy); }
        finally { GL.PopMatrix(); }
    }
    private static void Release(ref RenderTexture texture)
    { if (texture != null) { texture.Release(); Object.Destroy(texture); texture = null; } }
    private void ReleaseHistory()
    { for (var i = 0; i < history.Length; i++) Release(ref history[i]); trailSeed = trailFrames = 0; trailActive = false; }
    public void Dispose()
    {
        Release(ref input); Release(ref output); ReleaseHistory();
        if (effect != null) Object.Destroy(effect); if (copy != null) Object.Destroy(copy); effect = copy = null;
        if (acquired) { acquired = false; EffectAssets.Release(); }
    }

    private static class EffectAssets
    {
        private static AssetBundle bundle;
        private static Shader shader;
        private static int owners;
        internal static Shader Acquire()
        {
            if (shader == null)
            {
                using var stream = SinmaiAlpha.Hosting.AssetFiles.Open("Alpha/AlphaScreenEffects.ab");
                if (stream == null) throw new InvalidOperationException("Compiled effect resource missing");
                using var memory = new MemoryStream(); stream.CopyTo(memory);
                bundle = AssetBundle.LoadFromMemory(memory.ToArray());
                shader = bundle != null ? bundle.LoadAsset<Shader>("assets/aquamai/alphascreeneffects.shader") : null;
                if (shader == null || !shader.isSupported) { if (bundle != null) bundle.Unload(true); bundle = null; shader = null; throw new InvalidOperationException("Compiled effect shader unsupported"); }
            }
            owners++; return shader;
        }
        internal static void Release()
        { if (--owners > 0) return; if (bundle != null) bundle.Unload(true); bundle = null; shader = null; owners = 0; }
    }
}
