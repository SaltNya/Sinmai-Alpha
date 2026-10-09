using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Notes.Libs;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class VisualPayload { public VisualNote Note; public string Stream; public MajdataRegularPath SlideGeometry; }
    private sealed class VisualTimeline { public readonly Dictionary<string, List<VisualChange>> Streams = new(); }
    private sealed class VisualPart { public SpriteRenderer Native; public bool Guide, Mine, Hint; }
    private sealed class VisualLease
    {
        public int Monitor;
        public VisualPayload Payload;
        public readonly List<VisualPart> Parts = new();
    }
    private sealed class VisualOverlay
    {
        public SpriteRenderer Native, Render;
        public bool Applied, Enabled;
        public readonly MaterialPropertyBlock Properties = new();
        public void Restore()
        {
            if (!Applied) return;
            if (Native != null) Native.enabled = Enabled;
            if (Render != null) Render.gameObject.SetActive(false);
            Applied = false;
        }
    }
    private static readonly ConditionalWeakTable<NoteData, VisualPayload> VisualNotes = new();
    private static readonly ConditionalWeakTable<NotesReader, VisualTimeline> VisualTimelines = new();
    private static readonly Dictionary<Component, VisualLease> VisualOwners = new();
    private static readonly Dictionary<SpriteRenderer, VisualOverlay> VisualOverlays = new();
    private static readonly HashSet<SpriteRenderer> VisualOverlayRenderers = new();
    private static List<(int Bar, int Grid, string Kind, string Text)> PendingVisualCommands => RuntimeCharts.Current.PendingVisualCommands;
    private static string pendingVisualMarker { get => RuntimeCharts.Current.pendingVisualMarker; set => RuntimeCharts.Current.pendingVisualMarker = value; }

    private static void ReadVisualMarker(MA2Record rec)
    {
        pendingVisualMarker = null;
        if (rec?._str == null || rec._str.Count < 5) return;
        var tail = rec._str[rec._str.Count - 1];
        if (!tail.StartsWith("VS|", StringComparison.Ordinal)) return;
        pendingVisualMarker = tail;
        rec._str.RemoveAt(rec._str.Count - 1);
    }
    private static void RestoreVisualMarker(MA2Record rec)
    {
        if (pendingVisualMarker != null) rec._str.Add(pendingVisualMarker);
    }
    private static void ApplyVisualMarker(NoteData note, string stream)
    {
        VisualNotes.Remove(note);
        if (pendingVisualMarker != null)
        {
            try
            {
                var payload = new VisualPayload { Note = VisualNote.Decode(pendingVisualMarker), Stream = stream ?? "" };
                VisualNotes.Add(note, payload);
                if (payload.Note.ReferenceMotion && payload.Note.Family == "slide" && !string.IsNullOrEmpty(payload.Note.ReferenceSlide))
                {
                    try { payload.SlideGeometry = new MajdataRegularPath(payload.Note.ReferenceSlide, alpha053: true, geometryOnly: true); }
                    catch (Exception e) { MelonLogger.Warning("[Note Visual] Keeping native slide motion: " + e.Message); }
                }
            }
            catch (Exception e) { MelonLogger.Warning("[Note Visual] Invalid note metadata: " + e.Message); }
        }
        pendingVisualMarker = null;
    }

    [HarmonyPrefix, HarmonyPatch(typeof(NotesReader), "loadMa2")]
    public static void ResetVisualCommands(NotesReader __instance)
    {
        PendingVisualCommands.Clear();
        ReserveVisualNotes.Remove(__instance);
        VisualTimelines.Remove(__instance);
    }
    [HarmonyPrefix, HarmonyPatch(typeof(NotesRecord), "addRecord", new[] { typeof(string) })]
    public static void ReadVisualCommand(string str)
    {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }

        if (str == null) return;
        var p = str.Split('\t');
        if (p.Length != 4 || !(p[0] == "COLORV" || p[0] == "SIZEV" || p[0] == "ALPHAV")) return;
        if (int.TryParse(p[1], out var bar) && int.TryParse(p[2], out var grid))
            PendingVisualCommands.Add((bar, grid, p[0].ToLowerInvariant(), p[3]));
    }
    [HarmonyPostfix, HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static void BuildVisualCommands(NotesReader __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var timeline = new VisualTimeline();
        foreach (var cmd in PendingVisualCommands)
        {
            var scope = ""; var text = cmd.Text; var separator = text.IndexOf('~');
            if (separator >= 0)
            {
                scope = text.Substring(0, separator);
                if (!IsStreamType(scope)) continue;
                text = text.Substring(separator + 1);
            }
            var time = new NotesTime(); time.init(cmd.Bar, cmd.Grid, __instance);
            if (!VisualState.TryParse(cmd.Kind, text, time.msec, out var changes))
            { MelonLogger.Warning("[Note Visual] Invalid " + cmd.Kind + ": " + text); continue; }
            if (!timeline.Streams.TryGetValue(scope, out var list)) timeline.Streams[scope] = list = new List<VisualChange>();
            list.AddRange(changes);
        }
        foreach (var key in timeline.Streams.Keys.ToArray())
            timeline.Streams[key] = timeline.Streams[key].OrderBy(c => c.Time).ToList(); // stable authored order
        VisualTimelines.Remove(__instance); VisualTimelines.Add(__instance, timeline);
    }

    private static void RestoreVisualLease(Component owner)
    {
        if (!VisualOwners.TryGetValue(owner, out var lease)) return;
        foreach (var part in lease.Parts)
            if (part.Native != null && VisualOverlays.TryGetValue(part.Native, out var overlay)) overlay.Restore();
    }

    [HarmonyPatch]
    public static class VisualInitializePatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => FakeOwnerMethods().Where(m => m.Name == "Initialize"
            && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(NoteData));
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(Component __instance)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            RestoreVisualLease(__instance);
            VisualOwners.Remove(__instance);
        }
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(Component __instance, object[] __args)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            if (IsBorrowed((NoteData)__args[0])) return;
            if (!VisualNotes.TryGetValue((NoteData)__args[0], out var payload)) return;
            bool Empty(VisualValue value) => !value.Color.HasValue && !value.X.HasValue && !value.Alpha.HasValue;
            var reader = NotesManager.Instance(__instance is NoteBase n ? n.MonitorId : ((SlideRoot)__instance).MonitorId).getReader();
            if (Empty(payload.Note.Base) && Empty(payload.Note.Guide) &&
                (!VisualTimelines.TryGetValue(reader, out var timeline) || !timeline.Streams.ContainsKey(payload.Stream))) return;
            var lease = new VisualLease { Payload = payload,
                Monitor = __instance is NoteBase note ? note.MonitorId : ((SlideRoot)__instance).MonitorId };
            var seen = new HashSet<SpriteRenderer>();
            void Add(SpriteRenderer sprite, bool guide = false)
            {
                if (sprite != null && !VisualOverlayRenderers.Contains(sprite) && seen.Add(sprite))
                    lease.Parts.Add(new VisualPart { Native = sprite, Guide = guide, Mine = lease.Payload.Note.Mine });
            }
            void AddObject(GameObject obj, bool guide = false)
            {
                if (obj != null) foreach (var sprite in obj.GetComponentsInChildren<SpriteRenderer>(true)) Add(sprite, guide);
            }
            var fields = Traverse.Create(__instance);
            if (__instance is SlideFan)
            {
                foreach (var sprite in fields.Field("_spriteLines").GetValue<SpriteRenderer[]>()) Add(sprite);
                foreach (var sprite in fields.Field("_effectSprites").GetValue<SpriteRenderer[]>()) Add(sprite);
                foreach (var star in fields.Field("_baseStarObjs").GetValue<GameObject[]>()) AddObject(star, true);
            }
            else if (__instance is SlideRoot)
            {
                foreach (var sprite in fields.Field("_spriteRenders").GetValue<List<SpriteRenderer>>()) Add(sprite);
                foreach (var arrow in fields.Field("_breakSpriteRenders").GetValue<List<BreakSlide>>()) AddObject(arrow?.gameObject);
                AddObject(fields.Field("_baseStarNote").GetValue<GameObject>(), true);
            }
            else
            {
                var debug = fields.Field("SpriteRenderDebug").GetValue<SpriteRenderer>();
                foreach (var sprite in __instance.GetComponentsInChildren<SpriteRenderer>(true))
                    if (sprite != debug && sprite.GetComponentInParent<NoteGuide>() == null) Add(sprite);
                foreach (var hint in __instance.GetComponentsInChildren<NoteGuide>(true))
                {
                    var line = Traverse.Create(hint).Field("_spriteEachRender").GetValue<SpriteRenderer>();
                    if (line != null && seen.Add(line))
                        lease.Parts.Add(new VisualPart { Native = line, Hint = true });
                }
            }
            VisualOwners[__instance] = lease;
        }
    }

    private static void ApplyVisualPart(VisualPart part, VisualValue live, VisualValue baseline)
    {
        var native = part.Native;
        if (native == null || !native.enabled || !native.gameObject.activeInHierarchy || native.sprite == null) return;
        var progress = IsTouchProgressMaterial(native);
        var color = progress ? live.Color : live.Color ?? baseline.Color;
        var alpha = progress ? live.Alpha : live.Alpha ?? baseline.Alpha;
        var x = part.Hint ? null : live.X ?? baseline.X; var y = part.Hint ? null : live.Y ?? baseline.Y;
        if (!color.HasValue && !alpha.HasValue && !x.HasValue) return;
        if (!VisualOverlays.TryGetValue(native, out var overlay) || overlay.Render == null)
        {
            var obj = new GameObject("AquaMai Note Visual"); obj.layer = native.gameObject.layer;
            obj.transform.SetParent(native.transform, false);
            overlay = new VisualOverlay { Native = native, Render = obj.AddComponent<SpriteRenderer>() };
            VisualOverlays[native] = overlay; VisualOverlayRenderers.Add(overlay.Render);
        }
        var render = overlay.Render;
        var source = part.Mine && color.HasValue ? GetMineColorSprite(native.sprite) : native.sprite;
        render.sprite = source;
        render.sharedMaterial = native.sharedMaterial;
        native.GetPropertyBlock(overlay.Properties); render.SetPropertyBlock(overlay.Properties);
        if (progress) ApplyTouchProgressLiveVisual(render, live);
        var gpuTint = !part.Hint && !progress && (color.HasValue || alpha.HasValue) &&
            ApplyNoteTintMaterial(render, source, native.color, color, alpha, native.sharedMaterial);
        if (!progress && !gpuTint && color.HasValue)
            render.sprite = GetVisualTintSprite(source, color.Value, native.color);
        var tinted = color.HasValue && render.sprite != native.sprite;
        render.drawMode = native.drawMode; render.size = native.size;
        render.tileMode = native.tileMode; render.adaptiveModeThreshold = native.adaptiveModeThreshold;
        render.flipX = native.flipX; render.flipY = native.flipY;
        render.sortingLayerID = native.sortingLayerID; render.sortingOrder = native.sortingOrder;
        render.maskInteraction = native.maskInteraction;
        var c = native.color;
        render.color = progress || gpuTint ? c : tinted ? new Color(1, 1, 1, c.a * (alpha ?? 1)) : new Color(c.r, c.g, c.b, c.a * (alpha ?? 1));
        render.transform.localScale = new Vector3(x ?? 1, y ?? 1, 1);
        overlay.Enabled = native.enabled; overlay.Applied = true;
        native.enabled = false; render.gameObject.SetActive(true);
        // Only the additional renderer's geometry scales. Native parents, arrow
        // positions, sibling input queues and the physical slide sensors stay native.
    }

    [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyPatch(typeof(GameCtrl), "UpdateNotes")]
    public static void BeforeVisualFrame(GameCtrl __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        RestoreReserveVisuals(__instance.MonitorIndex);
        foreach (var pair in VisualOwners)
            if (pair.Value.Monitor == __instance.MonitorIndex) RestoreVisualLease(pair.Key);
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(GameCtrl), "UpdateNotes")]
    public static void AfterVisualFrame(GameCtrl __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var reader = NotesManager.Instance(__instance.MonitorIndex).getReader();
        VisualTimelines.TryGetValue(reader, out var timeline);
        ApplyReserveVisuals(__instance.MonitorIndex, reader, timeline);
        foreach (var pair in VisualOwners.ToArray())
        {
            var lease = pair.Value;
            if (lease.Monitor != __instance.MonitorIndex) continue;
            if (pair.Key == null || !pair.Key.gameObject.activeInHierarchy || Traverse.Create(pair.Key).Field("EndFlag").GetValue<bool>())
            { RestoreVisualLease(pair.Key); VisualOwners.Remove(pair.Key); continue; }
            var payload = lease.Payload;
            var live = new VisualValue(); var guide = new VisualValue();
            if (timeline != null && timeline.Streams.TryGetValue(payload.Stream, out var changes))
            {
                live = VisualState.Resolve(changes, NotesManager.GetCurrentMsec(), payload.Note);
                if (payload.Note.Family == "slide") guide = VisualState.Resolve(changes, NotesManager.GetCurrentMsec(), payload.Note, "slidestar");
            }
            foreach (var part in lease.Parts)
                ApplyVisualPart(part, part.Guide ? guide : live, part.Guide ? payload.Note.Guide : payload.Note.Base);
        }
    }

    [HarmonyPrefix, HarmonyPatch(typeof(GameCtrl), "ForceNoteCollect")]
    public static void CollectVisualNotes(GameCtrl __instance)
    {
        RestoreReserveVisuals(__instance.MonitorIndex);
        foreach (var owner in ReserveVisuals.Where(p => p.Value.Monitor == __instance.MonitorIndex).Select(p => p.Key).ToArray()) ReserveVisuals.Remove(owner);
        foreach (var pair in VisualOwners.Where(p => p.Value.Monitor == __instance.MonitorIndex).ToArray())
        { RestoreVisualLease(pair.Key); VisualOwners.Remove(pair.Key); }
        if (VisualOwners.Count == 0) ClearVisualTintCache();
    }
}
