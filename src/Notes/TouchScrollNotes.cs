using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using DB;
using HarmonyLib;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class TouchMotionLease
    {
        internal float Alpha, LastMsec = float.NaN;
        internal bool GaugeVisible, NoticeVisible, Applied;
        internal SpriteRenderer Gauge;
        internal bool OriginalGaugeEnabled;
        internal float OriginalGaugeAlpha;
    }
    private static readonly ConditionalWeakTable<TouchNoteB, TouchMotionLease> TouchMotionOwners = new();
    private static readonly FieldInfo TouchPetals = AccessTools.Field(typeof(TouchNoteB), "ColorsObject");
    private static readonly FieldInfo TouchPositions = AccessTools.Field(typeof(TouchNoteB), "DefaultCorlsPos");
    private static readonly FieldInfo TouchNotice = AccessTools.Field(typeof(TouchNoteB), "NoticeObject");
    private static readonly FieldInfo TouchGauge = AccessTools.Field(typeof(TouchHoldC), "HoldGaugeObject");
    private static float TouchViewSpeed(float nativeMsec, float multiplier)
    {
        if (multiplier == 0) return 0;
        // Sinmai Touch uses its own speed setting: D to close, plus D/4 fade.
        // Preserve the reference petal curve, but fit its total duration to the
        // native 1.25D/abs(HS) window. Multiplying a preview speed label by HS
        // would instead scale time by HS^0.954962 and miss the native baseline.
        var window = Math.Max(nativeMsec * .00125f, .000001f);
        return Math.Sign(multiplier) * (float)Math.Pow(3.209385682f * Math.Abs(multiplier) / window, 1 / .9549621752f);
    }
    private static float TouchVisualActivationLead(int index, float appear, float nativeMsec, float fallback)
    {
        if (!NoteScrollTableByNoteIndex.TryGetValue(index, out var table) || table.Count == 0 ||
            !HsMultByNoteIndex.TryGetValue(index, out var hs) || !SvScrollPosByNoteIndex.TryGetValue(index, out var target)) return fallback;
        var speed = TouchViewSpeed(nativeMsec, hs);
        if (Math.Abs(speed) <= ScrollVisualTiming.Epsilon) return Math.Min(10000f, Math.Max(fallback, appear - table[0].Msec));
        var threshold = target - Math.Sign(speed) * ScrollVisualTiming.TouchDuration(speed) * 1000f;
        bool Crossed(float scroll) => speed > 0 ? scroll >= threshold : scroll <= threshold;
        for (var i = 0; i < table.Count && table[i].Msec <= appear; i++)
        {
            var point = table[i];
            if (Crossed(point.Scroll)) return Math.Min(10000f, Math.Max(fallback, appear - point.Msec));
            if (i + 1 == table.Count || !Crossed(table[i + 1].Scroll)) continue;
            var next = table[i + 1];
            var delta = next.Scroll - point.Scroll;
            if (Math.Abs(delta) < .00001f) continue;
            var crossing = point.Msec + (next.Msec - point.Msec) * (threshold - point.Scroll) / delta;
            return Math.Min(10000f, Math.Max(fallback, appear - crossing));
        }
        return fallback;
    }
    private static void RestoreTouchMotion(TouchNoteB owner, bool remove)
    {
        if (!TouchMotionOwners.TryGetValue(owner, out var lease)) return;
        if (lease.Applied)
        {
            var petals = (SpriteRenderer[])TouchPetals.GetValue(owner);
            var positions = (Vector3[])TouchPositions.GetValue(owner);
            for (var i = 0; i < petals.Length && i < positions.Length; i++)
                if (petals[i] != null) petals[i].transform.localPosition = positions[i];
            if (lease.Gauge != null)
            {
                lease.Gauge.enabled = lease.OriginalGaugeEnabled;
                var color = lease.Gauge.color; color.a = lease.OriginalGaugeAlpha; lease.Gauge.color = color;
            }
            var notice = (GameObject)TouchNotice.GetValue(owner);
            if (notice != null) notice.SetActive(false);
            lease.Applied = false;
        }
        if (remove) TouchMotionOwners.Remove(owner);
    }
    private static void ApplyTouchMotion(TouchNoteB owner)
    {
        if (owner == null || owner.IsEnd()) return;
        var index = owner.GetNoteIndex();
        if (!SvScrollPosByNoteIndex.TryGetValue(index, out var target) || !HsMultByNoteIndex.TryGetValue(index, out var hs)) return;
        var appear = (float)FAppearMsec.GetValue(owner);
        var nativeMsec = (float)FDefaultMsec.GetValue(owner);
        var now = NotesManager.GetCurrentMsec();
        TouchMotionOwners.TryGetValue(owner, out var lease);
        if (lease == null && !RingVisualActive(index, appear, nativeMsec, SpawnDefaultRadius, now)) return;
        // Once a note has followed SV, keep its reference alpha memory when SV
        // returns to 1. Unaffected notes retain the native animation throughout.
        if (lease == null)
        {
            lease = new TouchMotionLease();
            if (owner is TouchHoldC)
            {
                lease.Gauge = (SpriteRenderer)TouchGauge.GetValue(owner);
                if (lease.Gauge != null)
                { lease.OriginalGaugeEnabled = lease.Gauge.enabled; lease.OriginalGaugeAlpha = lease.Gauge.color.a; }
            }
            TouchMotionOwners.Add(owner, lease);
        }
        if (!float.IsNaN(lease.LastMsec) && now + 1 < lease.LastMsec)
        { lease.Alpha = 0; lease.GaugeVisible = lease.NoticeVisible = false; }
        lease.LastMsec = now;
        var presentation = ScrollVisualTiming.Touch(target, GetNoteScroll(index, now), TouchViewSpeed(nativeMsec, hs), now < appear, lease.Alpha, lease.GaugeVisible);
        lease.Alpha = presentation.Alpha; lease.GaugeVisible = presentation.GaugeVisible;
        var petals = (SpriteRenderer[])TouchPetals.GetValue(owner);
        var positions = (Vector3[])TouchPositions.GetValue(owner);
        for (var i = 0; i < petals.Length && i < positions.Length; i++)
        {
            if (petals[i] == null) continue;
            // Native sprites have a different pivot from the reference sprites.
            // Keep native open/closed poses; apply the reference distance curve.
            petals[i].transform.localPosition = positions[i] * (presentation.Distance / .4f);
            var color = petals[i].color; color.a = presentation.Alpha; petals[i].color = color;
        }
        var point = (SpriteRenderer)RingSprite.GetValue(owner);
        if (point != null) { var color = point.color; color.a = presentation.Alpha; point.color = color; }
        if (lease.Gauge != null)
        {
            lease.Gauge.enabled = lease.OriginalGaugeEnabled && presentation.GaugeVisible;
            var color = lease.Gauge.color; color.a = presentation.Alpha; lease.Gauge.color = color;
            // GaugeMaterial._Amount, hold duration and release accumulation stay native.
        }
        var noticeObject = (GameObject)TouchNotice.GetValue(owner);
        if (!(owner is TouchHoldC) && noticeObject != null)
        {
            if (presentation.Timing > -.02f) lease.NoticeVisible = true;
            noticeObject.SetActive(lease.NoticeVisible && !IsFakeNoteOwner(owner) && ShouldShowMineFeedback(owner));
        }
        lease.Applied = true;
    }

    [HarmonyPatch(typeof(TouchNoteB), "Initialize")]
    public static class TouchMotionInitializePatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(TouchNoteB __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        RestoreTouchMotion(__instance, true);
    }
    }
    [HarmonyPatch(typeof(TouchNoteB), "GetNoteYPosition")]
    public static class TouchReferenceMotionPatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(TouchNoteB __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        RestoreTouchMotion(__instance, false);
    }
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(TouchNoteB __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        ApplyTouchMotion(__instance);
    }
        // Original visual function and NoteCheck execute on the real clock.
        // The postprocess writes only petal positions, alpha and display visibility.
    }
}
