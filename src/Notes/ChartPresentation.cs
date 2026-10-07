using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Reflection;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Hosting;
using SinmaiAlpha.Compatibility;
using HarmonyLib;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using Process;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;
using SinmaiAlpha.Notes.Libs;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class PresentationTimeline
    {
        public readonly Dictionary<string, PresentationCommands.Track> Tracks = new();
        public ComboDisplayCommands.Track ComboDisplay;
        public float Value(string kind, float time) => Tracks.TryGetValue(kind, out var track) ? track.Evaluate(time) : 0;
    }
    private sealed class CoverLease
    {
        public GameObject Owner;
        public bool Active;
        public bool ToggleOwner;
        public SpriteRenderer[] Sprites;
        public Color[] Colors;
        public bool[] SpriteEnabled;
        public Graphic[] Graphics;
        public Color[] GraphicColors;
        public bool[] GraphicEnabled;
        public void Apply(float alpha)
        {
            if (Owner == null) return;
            alpha = PresentationViewportMath.CoverAlpha(alpha);
            if (ToggleOwner) Owner.SetActive(true);
            for (var i = 0; i < Sprites.Length; i++) if (Sprites[i] != null)
            { var color = Colors[i]; color.a = alpha; Sprites[i].color = color; if (ToggleOwner) Sprites[i].enabled = true; }
            for (var i = 0; i < Graphics.Length; i++) if (Graphics[i] != null)
            { var color = GraphicColors[i]; color.a = alpha; Graphics[i].color = color; if (ToggleOwner) Graphics[i].enabled = true; }
        }
        public void ApplyPlayerBrightness(PresentationCommands.Track track, float time)
        {
            if (Owner == null) return;
            if (!track.IsPlayerBrightnessActive(time)) { Restore(); return; }
            if (ToggleOwner) Owner.SetActive(true);
            for (var i = 0; i < Sprites.Length; i++) if (Sprites[i] != null)
            { var color = Colors[i]; color.a = track.EvaluatePlayerBrightness(time, Colors[i].a); Sprites[i].color = color; if (ToggleOwner) Sprites[i].enabled = true; }
            for (var i = 0; i < Graphics.Length; i++) if (Graphics[i] != null)
            { var color = GraphicColors[i]; color.a = track.EvaluatePlayerBrightness(time, GraphicColors[i].a); Graphics[i].color = color; if (ToggleOwner) Graphics[i].enabled = true; }
        }
        public void Restore()
        {
            if (Owner == null) return;
            for (var i = 0; i < Sprites.Length; i++) if (Sprites[i] != null) { Sprites[i].color = Colors[i]; Sprites[i].enabled = SpriteEnabled[i]; }
            for (var i = 0; i < Graphics.Length; i++) if (Graphics[i] != null) { Graphics[i].color = GraphicColors[i]; Graphics[i].enabled = GraphicEnabled[i]; }
            if (ToggleOwner) Owner.SetActive(Active);
        }
    }
    private static List<(int Bar, int Grid, string Kind, string Text)> PendingPresentation => RuntimeCharts.Current.PendingPresentation;
    private static readonly ConditionalWeakTable<NotesReader, PresentationTimeline> PresentationTimelines = new();
    private static readonly Dictionary<GameMonitor, ReferenceSideStatistics> PresentationPanels = new();
    private static readonly Dictionary<Camera, ChartShakeCamera> PresentationCameras = new();
    private static readonly Dictionary<Camera, ChartFrameMotion> PresentationMotionCameras = new();
    private static readonly List<Transform> MotionCovers = new();
    private static readonly Dictionary<GameObject, CoverLease> PresentationCovers = new();
    private sealed class SharedTiledCover
    {
        public SpriteRenderer Source;
        public bool Enabled;
        public readonly SpriteRenderer[] Sides = new SpriteRenderer[2];
        public readonly Dictionary<int, float> Alpha = new();
        public readonly Dictionary<int, PresentationCommands.Track> Brightness = new();
        public readonly MaterialPropertyBlock Properties = new();
        public void Apply()
        {
            if (Source == null) return;
            Source.GetPropertyBlock(Properties);
            for (var side = 0; side < 2; side++)
            {
                var renderer = Sides[side];
                renderer.sprite = Source.sprite; renderer.sharedMaterial = Source.sharedMaterial;
                renderer.drawMode = Source.drawMode; renderer.size = new Vector2(Source.size.x / 2, Source.size.y);
                renderer.tileMode = Source.tileMode; renderer.adaptiveModeThreshold = Source.adaptiveModeThreshold;
                renderer.flipX = Source.flipX; renderer.flipY = Source.flipY;
                renderer.maskInteraction = Source.maskInteraction; renderer.spriteSortPoint = Source.spriteSortPoint;
                renderer.sortingLayerID = Source.sortingLayerID; renderer.sortingOrder = Source.sortingOrder;
                renderer.transform.position = Source.transform.TransformPoint(new Vector3((side == 0 ? -1 : 1) * Source.size.x / 4, 0, 0));
                renderer.transform.rotation = Source.transform.rotation; renderer.transform.localScale = Source.transform.localScale;
                renderer.SetPropertyBlock(Properties);
                var color = Source.color;
                var authored = Alpha.TryGetValue(side, out var value);
                var brightnessActive = Brightness.TryGetValue(side, out var brightnessTrack) && brightnessTrack.IsPlayerBrightnessActive(NotesManager.GetCurrentMsec());
                if (Brightness.TryGetValue(side, out var track)) color.a = track.EvaluatePlayerBrightness(NotesManager.GetCurrentMsec(), color.a);
                else if (authored) color.a = PresentationViewportMath.CoverAlpha(value);
                renderer.color = color; renderer.enabled = authored || brightnessActive || Enabled && Source.gameObject.activeInHierarchy;
            }
            Source.enabled = false;
        }
        public void Restore()
        {
            if (Source != null) Source.enabled = Enabled;
            foreach (var side in Sides) if (side != null) Object.Destroy(side.gameObject);
        }
    }
    private static readonly Dictionary<SpriteRenderer, SharedTiledCover> PresentationSharedCovers = new();
    private static readonly HashSet<SpriteRenderer> PresentationCoverCopies = new();

    private static void ApplySharedOuterCover(GameObject owner, int monitor, float? alpha, PresentationCommands.Track brightness = null)
    {
        if (owner == null) return;
        foreach (var source in owner.GetComponentsInChildren<SpriteRenderer>(true))
        {
            // The installed 1.70 aperture is a 2160x1920 tiled sprite. Split
            // its repeating geometry so its native material/stencil still runs.
            // Each half follows one monitor; an unbound side keeps native tint.
            if (PresentationCoverCopies.Contains(source) || source.drawMode != SpriteDrawMode.Tiled || source.sprite == null) continue;
            if (!PresentationSharedCovers.TryGetValue(source, out var lease))
            {
                lease = new SharedTiledCover { Source = source, Enabled = source.enabled };
                for (var side = 0; side < 2; side++)
                {
                    var go = new GameObject("ChartOuterCover" + side, typeof(SpriteRenderer));
                    go.layer = source.gameObject.layer;
                    // Siblings also work when the native cover object was
                    // inactive. Generated renderers are excluded from scans.
                    go.transform.SetParent(source.transform.parent, false);
                    lease.Sides[side] = go.GetComponent<SpriteRenderer>();
                    PresentationCoverCopies.Add(lease.Sides[side]);
                }
                PresentationSharedCovers[source] = lease;
            }
            if (brightness != null) lease.Brightness[monitor] = brightness;
            if (alpha.HasValue) lease.Alpha[monitor] = alpha.Value;
            lease.Apply();
        }
    }

    private static MonitorBackgroundTownController BackgroundFor(GameMonitor monitor, CanvasGroup main)
    {
        return ChartFrameMotion.NativeBackgroundFor(monitor);
    }

    public static void ResetPresentationCommands(NotesReader __instance)
    { foreach (var effect in PresentationCameras.Values) if (effect != null) effect.ResetReader(__instance); foreach (var motion in PresentationMotionCameras.Values) if (motion != null) motion.ResetReader(__instance); PendingPresentation.Clear(); PresentationTimelines.Remove(__instance); ResetJudgeLineCommands(__instance); JudgeLineExpansionRuntime.Reset(__instance); ResetSubtitleCommands(__instance); ResetJudgeTextPresentation(__instance); ResetComboPresentation(__instance); ResetMediaPresentation(__instance); ResetBorrowedNotes(__instance); ResetStarHeads(__instance); ResetFireworks(__instance); ResetNoteSkinAssets(__instance); }

    [HarmonyPatch]
    public static class PresentationLoadResetPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => typeof(NotesReader).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == "loadMa2" || m.Name == "loadStr" || m.Name == "loadDLMusicScore");
        [HarmonyPrefix]
        public static void Prefix(NotesReader __instance) => ResetPresentationCommands(__instance);
    }

    private static bool IsIgnoredCabinetPresentation(string kind) => kind == "showjudgeinfo" || kind == "showcomboinfo" || kind == "outerbrightness";
    public static void ReadPresentationCommand(string str)
    {
        ReadSubtitleCommand(str);
        ReadMediaCommand(str);
        if (str == null) return;
        var p = str.Split('\t');
        if (p.Length == 4 && !IsIgnoredCabinetPresentation(p[0].ToLowerInvariant()) && PresentationCommands.IsKind(p[0].ToLowerInvariant()) &&
            int.TryParse(p[1], out var bar) && int.TryParse(p[2], out var grid))
            PendingPresentation.Add((bar, grid, p[0].ToLowerInvariant(), p[3]));
    }
    public static void BuildPresentationCommands(NotesReader __instance)
    {
        BuildSubtitleCommands(__instance);
        BuildMediaCommands(__instance);
        var changes = new List<PresentationChange>();
        foreach (var item in PendingPresentation)
        {
            var time = new NotesTime(); time.init(item.Bar, item.Grid, __instance);
            if (PresentationCommands.Decode(item.Kind, item.Text, time.msec, out var change)) changes.Add(change);
            else MelonLogger.Warning("[Chart Presentation] Invalid " + item.Kind + ": " + item.Text);
        }
        BuildJudgeLineCommands(__instance, changes);
        JudgeLineExpansionRuntime.Build(__instance, changes);
        var timeline = new PresentationTimeline();
        foreach (var kind in changes.Select(c => c.Kind).Distinct())
            if (kind == "combodisplay") timeline.ComboDisplay = new ComboDisplayCommands.Track(changes);
            else if (!JudgeLineCommands.IsKind(kind) && !JudgeLineExpansionCommands.IsKind(kind)) timeline.Tracks[kind] = new PresentationCommands.Track(changes, kind);
        PresentationTimelines.Remove(__instance); PresentationTimelines.Add(__instance, timeline);
        if (changes.Count != 0) MelonLogger.Msg("[Chart Presentation] Loaded " + changes.Count + " display/effect events");
    }
    private static void ApplyPresentationCover(GameObject owner, float alpha, bool toggleOwner = true, bool applyValue = true)
    {
        if (owner == null) return;
        if (!PresentationCovers.TryGetValue(owner, out var lease))
        {
            var sprites = owner.GetComponentsInChildren<SpriteRenderer>(true);
            var graphics = owner.GetComponentsInChildren<Graphic>(true);
            // Inner cover owns the movie child; changing its active state would
            // hide the background itself. Only its own renderer is a cover.
            if (!toggleOwner) { sprites = owner.GetComponents<SpriteRenderer>(); graphics = owner.GetComponents<Graphic>(); }
            lease = new CoverLease { Owner = owner, Active = owner.activeSelf, ToggleOwner = toggleOwner, Sprites = sprites,
                Colors = sprites.Select(s => s.color).ToArray(), SpriteEnabled = sprites.Select(s => s.enabled).ToArray(),
                Graphics = graphics, GraphicColors = graphics.Select(g => g.color).ToArray(), GraphicEnabled = graphics.Select(g => g.enabled).ToArray() };
            PresentationCovers[owner] = lease;
        }
        if (applyValue) lease.Apply(alpha);
    }
    public static void ApplyChartPresentation(GameMonitor __instance)
    {
        ApplyWatermark(__instance);
        ApplyJudgeLinePresentation(__instance);
        JudgeLineExpansionRuntime.Apply(__instance);
        ApplySubtitlePresentation(__instance);
        ApplyMediaPresentation(__instance);
        ApplyComboPresentation(__instance);
        if (GuiSizes.SinglePlayer && __instance.MonitorIndex != 0)
        {
            ReleaseNoisePresentation(__instance);
            foreach (var motion in PresentationMotionCameras.Values) if (motion != null) motion.Unbind(__instance);
            ReleaseJudgeTextPresentation(__instance);
            foreach (var effect in PresentationCameras.Values) if (effect != null) effect.Unbind(__instance);
            return;
        }
        var reader = NotesManager.Instance(__instance.MonitorIndex).getReader();
        ApplyJudgeTextPresentation(__instance, reader);
        var ctrl = Traverse.Create(__instance).Field("GameController").GetValue<GameCtrl>();
        var main = Traverse.Create(__instance).Field("Main").GetValue<CanvasGroup>();
        if (main == null || ctrl == null) return;
        var noise = ApplyNoisePresentation(__instance, reader, ctrl);
        if (!PresentationTimelines.TryGetValue(reader, out var timeline)) timeline = EmptyNoisePresentation;
        if (timeline.Tracks.Count == 0 && noise == null)
        {
            foreach (var motion in PresentationMotionCameras.Values) if (motion != null) motion.Unbind(__instance);
            foreach (var effect in PresentationCameras.Values) if (effect != null) effect.Unbind(__instance);
            return;
        }
        var now = NotesManager.GetCurrentMsec();
        timeline.Tracks.TryGetValue("shake", out var shake);
        timeline.Tracks.TryGetValue("flash", out var flash);
        timeline.Tracks.TryGetValue("tint", out var tint);
        // Frame compositing must precede SHAKE/FLASH/TINT regardless of the
        // order in which Unity created the two components.
        var movingFrame = timeline.Tracks.ContainsKey("move") || timeline.Tracks.ContainsKey("rotate") || timeline.Tracks.ContainsKey("zoom");
        if (noise != null || shake != null || flash != null || tint != null || movingFrame || ChartScreenEffects.HasTracks(timeline.Tracks))
        {
            var canvas = main.GetComponentInParent<Canvas>();
            var adjustedCamera = ScreenPositionAdjust.GameRenderCamera;
            var camera = adjustedCamera != null ? adjustedCamera : canvas != null ? canvas.worldCamera : null;
            if (camera == null) camera = Camera.main;
            if (camera == null) return;
            if (!PresentationCameras.TryGetValue(camera, out var effect))
            { effect = camera.gameObject.AddComponent<ChartShakeCamera>(); PresentationCameras[camera] = effect; }
            // Main is the 1080x1080 note circle; its parent canvas is the full
            // 1080x1920 frame. HideSubMonitor zooms the camera into Main. The
            // screen-position mod renders the full scene to its own texture.
            var mainOnly = GuiSizes.SinglePlayer && SinglePlayer.HideSubMonitor && adjustedCamera == null;
            var bounds = !mainOnly && canvas != null ? canvas.transform : main.transform;
            foreach (var other in PresentationCameras) if (other.Key != camera && other.Value != null) other.Value.Unbind(__instance);
            effect.Bind(__instance, bounds as RectTransform, shake, flash, tint, timeline.Tracks, reader);
            effect.BindNoise(__instance, noise, main.transform as RectTransform);
        }
        else foreach (var effect in PresentationCameras.Values) if (effect != null) effect.Unbind(__instance);
        ApplyFrameMotion(__instance, reader, ctrl, main, timeline, noise);
    }
    private static void ApplyFrameMotion(GameMonitor owner, NotesReader reader, GameCtrl controller, CanvasGroup main, PresentationTimeline timeline, NoiseRenderState noise)
    {
        timeline.Tracks.TryGetValue("move", out var move); timeline.Tracks.TryGetValue("rotate", out var rotate); timeline.Tracks.TryGetValue("zoom", out var zoom);
        if (move == null && rotate == null && zoom == null && noise == null)
        { foreach (var motion in PresentationMotionCameras.Values) if (motion != null) motion.Unbind(owner); return; }
        var canvas = main.GetComponentInParent<Canvas>();
        var adjusted = ScreenPositionAdjust.GameRenderCamera;
        var camera = adjusted != null ? adjusted : canvas != null ? canvas.worldCamera : null;
        if (camera == null) camera = Camera.main;
        if (camera == null) return;
        var bounds = GuiSizes.SinglePlayer && SinglePlayer.HideSubMonitor && adjusted == null || canvas == null ? main.transform : canvas.transform;
        if (!PresentationMotionCameras.TryGetValue(camera, out var effect))
        { effect = camera.gameObject.AddComponent<ChartFrameMotion>(); PresentationMotionCameras[camera] = effect; }
        foreach (var other in PresentationMotionCameras) if (other.Key != camera && other.Value != null) other.Value.Unbind(owner);
        MotionCovers.Clear();
        if (move != null || rotate != null || zoom != null)
        {
            ApplySharedOuterCover(GameObject.Find("Mask"), owner.MonitorIndex, null);
            foreach (var cover in PresentationSharedCovers.Values)
                if (owner.MonitorIndex >= 0 && owner.MonitorIndex < cover.Sides.Length && cover.Sides[owner.MonitorIndex] != null) MotionCovers.Add(cover.Sides[owner.MonitorIndex].transform);
            var background = BackgroundFor(owner, main);
            var bg = background != null ? Traverse.Create(background).Field("_bgObject").GetValue<GameObject>() : null;
            var mask = bg != null ? bg.transform.Find("BG/Monitor_Mask") : null;
            if (mask != null) MotionCovers.Add(mask);
        }
        effect.Bind(owner, reader, controller, main.transform as RectTransform, bounds as RectTransform, move, rotate, zoom, MotionCovers);
        effect.BindNoise(owner, noise);
    }
    public static void ReleaseChartPresentation()
    {
        ReleaseWatermarks();
        ResetTouchProgress();
        ResetNoisePresentation();
        ReleaseSubtitlePresentation();
        ReleaseJudgeLinePresentation();
        JudgeLineExpansionRuntime.ReleaseAll();
        ReleaseComboPresentation();
        ReleaseMediaPresentation();
        ReleaseBorrowedNotes();
        ResetStarHeads();
        ReleaseNoteSkinAssets();
        ReleaseJudgeTextPresentation();
        foreach (var lease in PresentationCovers.Values) lease.Restore();
        foreach (var lease in PresentationSharedCovers.Values) lease.Restore();
        foreach (var panel in PresentationPanels.Values) if (panel != null) Object.Destroy(panel.gameObject);
        foreach (var camera in PresentationCameras.Values) if (camera != null) { camera.Clear(); Object.Destroy(camera); }
        foreach (var camera in PresentationMotionCameras.Values) if (camera != null) { camera.Clear(); Object.Destroy(camera); }
        PresentationMotionCameras.Clear(); MotionCovers.Clear();
        PresentationCovers.Clear(); PresentationSharedCovers.Clear(); PresentationCoverCopies.Clear(); PresentationPanels.Clear(); PresentationCameras.Clear(); PendingPresentation.Clear();
    }
    [HarmonyPatch(typeof(NotesRecord), "addRecord", new[] { typeof(string) })]
    public static class PresentationRecordPatch
    {
        [HarmonyPrefix] public static void Prefix(string str) {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }
        ReadPresentationCommand(str);
    }
    }
    [HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static class PresentationBuildPatch
    {
        [HarmonyPostfix] public static void Postfix(NotesReader __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        BuildPresentationCommands(__instance);
    }
    }
    [HarmonyPatch(typeof(GameMonitor), "ViewUpdate")]
    public static class PresentationViewPatch
    {
        [HarmonyPrefix] public static void Prefix(GameMonitor __instance)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
 foreach (var motion in PresentationMotionCameras.Values) if (motion != null) motion.BeforeNativeUpdate(__instance); }
        [HarmonyPostfix] public static void Postfix(GameMonitor __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        ApplyChartPresentation(__instance);
    }
    }
    [HarmonyPatch(typeof(GameCtrl), "UpdateMovie")]
    public static class PresentationMovieBrightnessPatch
    {
        [HarmonyPostfix]
        public static void Postfix(int ___monitorIndex, SpriteRenderer ____movieMaskSprite)
        {
        if (!(CustomNoteTypes.FeaturesForMonitor(___monitorIndex))) {  return; }

            if (____movieMaskSprite == null || GuiSizes.SinglePlayer && ___monitorIndex != 0) return;
            var reader = NotesManager.Instance(___monitorIndex).getReader();
            if (!PresentationTimelines.TryGetValue(reader, out var timeline) ||
                !timeline.Tracks.TryGetValue("innerbrightness", out var inner)) return;
            // UpdateMovie has just computed this frame's native option opacity
            // and opening fade. Compose once here, never from our previous result.
            var color = ____movieMaskSprite.color;
            color.a = PresentationViewportMath.MovieCoverAlpha(color.a, inner.EvaluatePlayerBrightness(NotesManager.GetCurrentMsec(), 0));
            ____movieMaskSprite.color = color;
        }
    }
    [HarmonyPatch(typeof(GameProcess), "OnRelease")]
    public static class PresentationReleasePatch
    {
        [HarmonyPostfix] public static void Postfix() => ReleaseChartPresentation();
    }
}

// Image-space UV offset leaves logical sensors, native input queues and timing untouched.
public sealed class ChartShakeCamera : MonoBehaviour
{
    private sealed class Frame
    {
        public RectTransform Bounds;
        public PresentationCommands.Track Track;
        public PresentationCommands.Track Flash, Tint;
        public PresentationChange TintSource;
        public Color TintColor;
        public NotesReader Reader;
        public IDictionary<string, PresentationCommands.Track> AllTracks;
        public ChartScreenEffects ScreenEffects;
        public CustomNoteTypes.NoiseRenderState Noise;
        public RectTransform Playfield;
        public readonly Vector3[] Corners = new Vector3[4];
    }
    private readonly Dictionary<GameMonitor, Frame> frames = new();
    private Material colorMaterial;
    private bool colorMaterialUnavailable;
    internal void Bind(GameMonitor owner, RectTransform bounds, PresentationCommands.Track track)
        => Bind(owner, bounds, track, null, null);
    internal void Bind(GameMonitor owner, RectTransform bounds, PresentationCommands.Track track,
        PresentationCommands.Track flash, PresentationCommands.Track tint)
        => Bind(owner, bounds, track, flash, tint, null, null);
    internal void Bind(GameMonitor owner, RectTransform bounds, PresentationCommands.Track track,
        PresentationCommands.Track flash, PresentationCommands.Track tint, IDictionary<string, PresentationCommands.Track> tracks, NotesReader reader)
    {
        if (!frames.TryGetValue(owner, out var frame)) frames[owner] = frame = new Frame();
        if (frame.AllTracks != tracks || frame.Reader != reader)
        {
            frame.ScreenEffects?.Dispose();
            frame.ScreenEffects = ChartScreenEffects.HasTracks(tracks) ? new ChartScreenEffects(tracks) : null;
            frame.AllTracks = tracks; frame.Reader = reader;
        }
        if (frame.Tint != tint) frame.TintSource = null;
        frame.Bounds = bounds; frame.Track = track; frame.Flash = flash; frame.Tint = tint;
    }
    internal void Unbind(GameMonitor owner) { if (frames.TryGetValue(owner, out var frame)) { frame.Noise?.CaptureField(null); frame.ScreenEffects?.Dispose(); } frames.Remove(owner); }
    internal void BindNoise(GameMonitor owner, CustomNoteTypes.NoiseRenderState noise, RectTransform playfield)
    { if (frames.TryGetValue(owner, out var frame)) { frame.Noise = noise; frame.Playfield = playfield; } }
    internal void ResetReader(NotesReader reader) { foreach (var owner in frames.Where(pair => pair.Value.Reader == reader).Select(pair => pair.Key).ToArray()) Unbind(owner); }
    internal void Clear() { foreach (var frame in frames.Values) { frame.Noise?.CaptureField(null); frame.ScreenEffects?.Dispose(); } frames.Clear(); ReleaseColorMaterial(); }
    private void OnDisable() => Clear();
    private void OnDestroy() => Clear();
    private void ReleaseColorMaterial()
    {
        if (colorMaterial != null) Object.Destroy(colorMaterial);
        colorMaterial = null; colorMaterialUnavailable = false;
    }
    private bool EnsureColorMaterial()
    {
        if (colorMaterial != null) return true;
        if (colorMaterialUnavailable) return false;
        var shader = Shader.Find("UI/Default");
        if (shader != null)
        {
            colorMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            if (colorMaterial.HasProperty("_ColorMask"))
            {
                // Native UI shader uses the supplied vertex color directly.
                // Write RGB only: the reference post effect preserves alpha.
                colorMaterial.SetInt("_ColorMask", (int)(UnityEngine.Rendering.ColorWriteMask.Red |
                    UnityEngine.Rendering.ColorWriteMask.Green | UnityEngine.Rendering.ColorWriteMask.Blue));
                colorMaterial.SetColor("_Color", Color.white);
                colorMaterial.SetVector("_TextureSampleAdd", Vector4.zero);
                colorMaterial.SetInt("unity_GUIZTestMode", (int)UnityEngine.Rendering.CompareFunction.Always);
                colorMaterial.DisableKeyword("UNITY_UI_CLIP_RECT");
                colorMaterial.DisableKeyword("UNITY_UI_ALPHACLIP");
                return true;
            }
            Object.Destroy(colorMaterial); colorMaterial = null;
        }
        colorMaterialUnavailable = true;
        MelonLogger.Warning("[Chart Presentation] Native UI/Default with RGB color mask unavailable; color effects disabled");
        return false;
    }
    private void DrawColor(PresentationRect output, RenderTexture texture, Color color)
    {
        if (color.a <= 0 || !EnsureColorMaterial()) return;
        Graphics.DrawTexture(Pixels(output, texture.width, texture.height), Texture2D.whiteTexture,
            new Rect(0, 0, 1, 1), 0, 0, 0, 0, color, colorMaterial);
    }
    private PresentationRect Bounds(Frame frame, Camera camera)
        => Bounds(frame.Bounds, frame.Corners, camera);
    private PresentationRect Bounds(RectTransform bounds, Vector3[] corners, Camera camera)
    {
        bounds.GetWorldCorners(corners);
        var minX = float.PositiveInfinity; var minY = minX;
        var maxX = float.NegativeInfinity; var maxY = maxX;
        foreach (var corner in corners)
        {
            var point = camera.WorldToViewportPoint(corner);
            minX = Mathf.Min(minX, point.x); minY = Mathf.Min(minY, point.y);
            maxX = Mathf.Max(maxX, point.x); maxY = Mathf.Max(maxY, point.y);
        }
        return new PresentationRect(minX, minY, maxX - minX, maxY - minY);
    }
    private static Rect Pixels(PresentationRect rect, int width, int height)
        => new(rect.X * width, (1 - rect.Top) * height, rect.Width * width, rect.Height * height);
    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        var previous = RenderTexture.active;
        var matrixPushed = false;
        RenderTexture isolated = null;
        RenderTexture screenFrame = null;
        var finalDestination = destination;
        try
        {
            // Unity supplies null for the last image effect targeting the
            // screen. Preserve a readable filtered frame for the later noise
            // pass instead of trying to sample the screen as a texture.
            if (destination == null && frames.Values.Any(frame => frame.Noise != null && frame.Noise.Ready))
            {
                screenFrame = RenderTexture.GetTemporary(source.width, source.height, 0, source.format, RenderTextureReadWrite.Linear);
                destination = screenFrame;
            }
            var motion = GetComponent<ChartFrameMotion>();
            if (motion != null && motion.HasIsolatedFrames)
            {
                isolated = RenderTexture.GetTemporary(source.width, source.height, 0, source.format);
                motion.CompositeIsolation(source, isolated); source = isolated;
            }
            Graphics.Blit(source, destination);
            var camera = GetComponent<Camera>();
            if (camera == null) return;
            var milliseconds = NotesManager.GetCurrentMsec();
            foreach (var pair in frames)
            {
                var frame = pair.Value;
                if (pair.Key == null || frame.Bounds == null || !frame.Bounds.gameObject.activeInHierarchy) continue;
                if (frame.ScreenEffects != null)
                {
                    var shaderBounds = Bounds(frame, camera);
                    var shaderVisible = shaderBounds.Intersect(new PresentationRect(0, 0, 1, 1));
                    var shaderField = frame.Playfield != null ? Bounds(frame.Playfield, frame.Corners, camera) : shaderBounds;
                    if (frame.ScreenEffects.TryDraw(source, destination, shaderBounds, shaderVisible, shaderField, milliseconds)) continue;
                }
                PresentationChange ev = null, tintSource = null;
                var strength = frame.Track?.Evaluate(milliseconds, out ev) ?? 0;
                var shaking = ev != null && Math.Abs(strength) > .0001f;
                var flash = Math.Max(-1, Math.Min(1, frame.Flash?.Evaluate(milliseconds) ?? 0));
                var tint = Math.Max(0, Math.Min(1, frame.Tint?.Evaluate(milliseconds, out tintSource) ?? 0));
                if (tintSource == null || string.IsNullOrEmpty(tintSource.ColorHex)) tint = 0;
                if (!shaking && Math.Abs(flash) <= .001f && tint <= .001f) continue;
                var bounds = Bounds(frame, camera);
                if (!bounds.HasArea) continue;
                var visible = bounds.Intersect(new PresentationRect(0, 0, 1, 1));
                if (!visible.HasArea) continue;
                var x = 0f; var y = 0f;
                if (shaking)
                {
                    var phase = milliseconds * .001f * (ev.Frequency > 0 ? ev.Frequency : 18);
                    x = (Mathf.PerlinNoise(phase, .37f) - .5f) * 2 * strength * .05f;
                    y = ev.HasDirection ? Mathf.Sin(ev.Direction) * x : (Mathf.PerlinNoise(.71f, phase) - .5f) * 2 * strength * .05f;
                    if (ev.HasDirection) x *= Mathf.Cos(ev.Direction);
                }
                if (!matrixPushed)
                {
                    Graphics.SetRenderTarget(destination); GL.PushMatrix(); matrixPushed = true;
                    GL.LoadPixelMatrix(0, source.width, source.height, 0);
                }
                var output = visible;
                if (shaking)
                {
                    Graphics.DrawTexture(Pixels(visible, source.width, source.height), Texture2D.blackTexture);
                    if (!PresentationViewportMath.Shift(bounds, x, y, out output, out var input)) continue;
                    Graphics.DrawTexture(Pixels(output, source.width, source.height), source,
                        new Rect(input.X, input.Y, input.Width, input.Height), 0, 0, 0, 0);
                }
                // Same order as AlphaScreenEffects: flash/fade, then tint,
                // then the inside-frame mask (already represented by output).
                if (flash != 0) DrawColor(output, source, new Color(flash > 0 ? 1 : 0, flash > 0 ? 1 : 0, flash > 0 ? 1 : 0, Math.Abs(flash)));
                if (tint > 0)
                {
                    if (frame.TintSource != tintSource)
                    {
                        var rgb = int.Parse(tintSource.ColorHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        frame.TintColor = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f);
                        frame.TintSource = tintSource;
                    }
                    var color = frame.TintColor; color.a = tint; DrawColor(output, source, color);
                }
            }
            // Latest compiled reference uses filter pass 600, noise pass 601.
            // Keep Trail history raw and composite noise after every filter.
            if (matrixPushed) { GL.PopMatrix(); matrixPushed = false; }
            RenderTexture noiseSource = null;
            try
            {
                foreach (var pair in frames)
                {
                    var frame = pair.Value;
                    if (pair.Key == null || frame.Noise == null || frame.Playfield == null || frame.Bounds == null || !frame.Bounds.gameObject.activeInHierarchy) continue;
                    var bounds = Bounds(frame, camera);
                    var visible = bounds.Intersect(new PresentationRect(0, 0, 1, 1));
                    if (!bounds.HasArea || !visible.HasArea) continue;
                    if (noiseSource == null) noiseSource = RenderTexture.GetTemporary(source.width, source.height, 0, source.format, RenderTextureReadWrite.Linear);
                    Graphics.Blit(destination, noiseSource);
                    var playfield = Bounds(frame.Playfield, frame.Corners, camera);
                    frame.Noise.TryDraw(noiseSource, destination, camera, bounds, visible, playfield);
                }
            }
            finally { if (noiseSource != null) RenderTexture.ReleaseTemporary(noiseSource); }
        }
        finally
        {
            if (matrixPushed) GL.PopMatrix();
            try
            {
                if (screenFrame != null) Graphics.Blit(screenFrame, finalDestination);
            }
            finally
            {
                if (screenFrame != null) RenderTexture.ReleaseTemporary(screenFrame);
                RenderTexture.active = previous;
                if (isolated != null) RenderTexture.ReleaseTemporary(isolated);
            }
        }
    }
}

public sealed class ReferenceSideStatistics : MonoBehaviour
{
    private Text judge, combo;
    private int monitor;
    internal void Configure(int monitorIndex, CanvasGroup parent)
    {
        monitor = monitorIndex;
        var font = parent.GetComponentsInChildren<Text>(true).FirstOrDefault(t => t.font != null)?.font;
        if (font == null) font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        Text Make(string name, Vector2 anchor)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Text));
            go.transform.SetParent(transform, false);
            var text = go.GetComponent<Text>(); text.font = font; text.fontSize = 22; text.color = Color.white;
            text.alignment = TextAnchor.UpperLeft; text.raycastTarget = false;
            var rect = text.rectTransform; rect.anchorMin = rect.anchorMax = anchor; rect.pivot = new Vector2(0, 1);
            rect.sizeDelta = new Vector2(220, 160);
            return text;
        }
        var root = (RectTransform)transform; root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
        root.offsetMin = root.offsetMax = Vector2.zero;
        judge = Make("JudgeInfo", new Vector2(.02f, .9f)); combo = Make("ComboInfo", new Vector2(.76f, .9f));
    }
    internal void Apply(float judgeAlpha, float comboAlpha)
    {
        judge.canvasRenderer.SetAlpha(judgeAlpha); combo.canvasRenderer.SetAlpha(comboAlpha);
        if (judgeAlpha <= 0 && comboAlpha <= 0) return;
        var score = MAI2.Util.Singleton<GamePlayManager>.Instance.GetGameScore(monitor);
        if (judgeAlpha > 0) judge.text = "CRITICAL " + score.GetCriticalNum() + "\nPERFECT " + score.GetPerfectNum() +
            "\nGREAT " + score.GetGreatNum() + "\nGOOD " + score.GetGoodNum() + "\nMISS " + score.GetMissNum();
        if (comboAlpha > 0) combo.text = "COMBO " + score.Combo + "\n" + score.GetAchivement().ToString("F4") + "%";
    }
}
