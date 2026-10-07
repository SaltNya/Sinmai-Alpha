using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Notes.Libs;
using DB;
using HarmonyLib;
using Manager;
using MAI2.Util;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    public static void ResetStreamedChartSpeedAndVisuals(NotesReader __instance)
    {
        LoadMa2SvResetPrefix(); ResetVisualCommands(__instance);
    }
    [HarmonyPatch]
    public static class StreamedChartResetPatch
    {
        public static IEnumerable<System.Reflection.MethodBase> TargetMethods() => typeof(NotesReader)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Where(m => m.Name == "loadDLMusicScore" || m.Name == "loadStr");
        [HarmonyPrefix]
        public static void Prefix(NotesReader __instance) => ResetStreamedChartSpeedAndVisuals(__instance);
    }
    private static VisualNote SpeedClass(NoteData note)
    {
        if (VisualNotes.TryGetValue(note, out var payload)) return payload.Note;
        return new VisualNote { Family = ResolveSpeedType(note), Each = note.isEach, Break = note.type.isBreak(),
            Mine = GetNoteKind(note) is CustomNoteKind.Mine or CustomNoteKind.MineTouchBreak or CustomNoteKind.MineTouchStar };
    }
    private static IEnumerable<string> SpeedPriority(VisualNote note)
    {
        if (note.Mine) yield return "mine";
        if (note.Break) yield return "break";
        if (note.Each) yield return "each";
        yield return note.Family;
    }
    private static string ResolvePlayableSvType(NoteData note)
    {
        foreach (var type in SpeedPriority(SpeedClass(note)))
            if (SvCurves.ContainsKey(type) || SvClearTimes.ContainsKey(type)) return type;
        return ResolveSpeedType(note);
    }
    private static float? HsAt(string type, float msec)
    {
        var last = float.NegativeInfinity; float? result = null;
        if (HsCurves.TryGetValue(type, out var curve))
            foreach (var entry in curve) { if (entry.Msec > msec) break; last = entry.Msec; result = entry.Mult; }
        if (HsClearTimes.TryGetValue(type, out var resets))
            foreach (var reset in resets) { if (reset > msec) break; if (reset >= last) { last = reset; result = null; } }
        return result;
    }
    private static float ResolvePlayableHs(NoteData note)
    {
        if (StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var stream)) return HsAt(stream, note.time.msec) ?? 1;
        // Slide HS controls appearance only and does not inherit global/head HS.
        if (note.type.isAllSlide()) return HsAt("slide", note.time.msec) ?? 1;
        foreach (var type in SpeedPriority(SpeedClass(note)))
        { var value = HsAt(type, note.time.msec); if (value.HasValue) return value.Value; }
        return HsAt("", note.time.msec) ?? 1;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(SlideRoot), "Initialize")]
    public static void ApplyNativeSlideAppearanceHs(SlideRoot __instance, NoteData note)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        ApplySlideAppearanceHs(__instance, note);
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(SlideFan), "Initialize")]
    public static void ApplyNativeFanAppearanceHs(SlideFan __instance, NoteData note)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        ApplySlideAppearanceHs(__instance, note);
    }
    private static void ApplySlideAppearanceHs(SlideRoot owner, NoteData note)
    {
        RegisterSlideAppearance(owner, note);
    }

    [HarmonyPostfix, HarmonyPatch(typeof(SlideRoot), "MoveStarLane")]
    public static void ApplyNativeSlideSvMotion(SlideRoot __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        ApplySlideSvMotion(__instance, false);
        ApplySlideStarAppearance(__instance);
    }
    [HarmonyPostfix, HarmonyPatch(typeof(SlideFan), "MoveStarLane")]
    public static void ApplyNativeFanSvMotion(SlideFan __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        ApplySlideSvMotion(__instance, true);
        ApplySlideStarAppearance(__instance);
    }
    private static void ApplySlideSvMotion(SlideRoot owner, bool fan)
    {
        var fields = Traverse.Create(owner);
        var note = fields.Field("NoteData").GetValue<NoteData>();
        if (note == null) return;
        var touch = note as CustomSlideNoteData;
        VisualNotes.TryGetValue(note, out var visual);
        if (touch?.TouchGeometry == null && visual?.SlideGeometry == null && !MajdataHasSlideSvFor(note)) return;
        var start = fields.Field("StarLaunchMsec").GetValue<float>();
        var end = fields.Field("StarArriveMsec").GetValue<float>();
        var now = NotesManager.GetCurrentMsec();
        if (end <= start) return;
        if (!fan && visual?.SlideGeometry != null)
        {
            var star = fields.Field("_baseStarNote").GetValue<GameObject>();
            if (star == null) return;
            var pose = visual.SlideGeometry.DisplayPose(start, end - start, now,
                (localStart, localDuration, time) => MajdataSlideProgress(note, localStart, localDuration, time));
            var point = new System.Numerics.Complex(pose.x * 100, pose.y * 100);
            var direction = System.Numerics.Complex.FromPolarCoordinates(1, pose.z * Math.PI / 180);
            var mirror = Singleton<GamePlayManager>.Instance.GetGameScore(owner.MonitorId).UserOption.MirrorMode;
            System.Numerics.Complex Mirror(System.Numerics.Complex value) => mirror switch
            {
                OptionMirrorID.LR => -System.Numerics.Complex.Conjugate(value),
                OptionMirrorID.UD => System.Numerics.Complex.Conjugate(value),
                OptionMirrorID.UDLR => -value, _ => value
            };
            var rotor = System.Numerics.Complex.FromPolarCoordinates(1, Math.PI / 4 * owner.ButtonId);
            point = Mirror(point) * rotor; direction = Mirror(direction) * rotor;
            star.transform.localPosition = new Vector3((float)point.Real, (float)point.Imaginary, star.transform.localPosition.z);
            star.transform.localRotation = Quaternion.Euler(0, 0, (float)(direction.Phase * 180 / Math.PI));
            return;
        }
        var progress = now <= start ? 0 : MajdataSlideProgress(note, start, end - start, now);
        if (touch?.TouchGeometry != null)
        {
            var star = fields.Field("_baseStarNote").GetValue<GameObject>();
            if (star == null) return;
            var point = touch.TouchGeometry.GetPointAt(progress);
            var tangent = touch.TouchGeometry.GetTangentAt(progress);
            System.Numerics.Complex Mirror(System.Numerics.Complex v) => touch.TouchMirror switch
            {
                OptionMirrorID.LR => -System.Numerics.Complex.Conjugate(v),
                OptionMirrorID.UD => System.Numerics.Complex.Conjugate(v),
                OptionMirrorID.UDLR => -v, _ => v
            };
            var rotor = System.Numerics.Complex.FromPolarCoordinates(1, Math.PI / 4 * owner.ButtonId);
            point = Mirror(point) * rotor; tangent = Mirror(tangent) * rotor;
            star.transform.localPosition = new Vector3((float)point.Real, (float)point.Imaginary, star.transform.localPosition.z);
            star.transform.localRotation = Quaternion.Euler(0, 0, (float)(tangent.Phase * 180 / Math.PI) + MajdataSlidePath.ReferenceRotation + 18);
            return;
        }
        if (now < start) return;
        void Pose(GameObject star, IList<List<Vector4>> paths)
        {
            if (star == null || paths.Count == 0) return;
            var length = 0f;
            foreach (var path in paths) if (path.Count != 0) length += path[path.Count - 1].z;
            var distance = length * progress;
            Vector4 a = default, b = default; var fraction = 0f;
            foreach (var path in paths)
            {
                if (path.Count == 0) continue;
                a = b = path[path.Count - 1];
                if (distance <= a.z)
                {
                    for (var i = 0; i + 1 < path.Count; i++)
                        if (path[i + 1].z >= distance)
                        {
                            a = path[i]; b = path[i + 1];
                            fraction = b.z > a.z ? Mathf.Clamp01((distance - a.z) / (b.z - a.z)) : 0;
                            break;
                        }
                    break;
                }
                distance -= a.z;
            }
            var point = Vector4.LerpUnclamped(a, b, fraction);
            var original = star.transform.localPosition;
            star.transform.localPosition = new Vector3(point.x, point.y, original.z);
            star.transform.localRotation = Quaternion.Euler(0, 0, Mathf.LerpAngle(a.w, b.w, fraction) + 90);
        }
        if (fan)
        {
            var stars = fields.Field("_baseStarObjs").GetValue<GameObject[]>();
            var paths = fields.Field("_slideVecList").GetValue<List<Vector4>[]>();
            for (var i = 0; i < stars.Length; i++) Pose(stars[i], new[] { paths[i] });
        }
        else Pose(fields.Field("_baseStarNote").GetValue<GameObject>(), fields.Field("_slideVecListList").GetValue<List<List<Vector4>>>());
        // A display-only postprocess. No Execute replacement, judge cursor,
        // touch event, automatic result, arrow removal or score mutation.
    }
}
