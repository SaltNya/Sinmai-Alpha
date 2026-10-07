using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using SinmaiAlpha.ChartVisuals;
using HarmonyLib;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class TouchRadiusValue { public float Radius; }
    private sealed class TouchRadiusLease { public Vector3 Position; public int Monitor; }
    private static readonly ConditionalWeakTable<NoteData, TouchRadiusValue> TouchRadii = new();
    private static readonly Dictionary<TouchNoteB, TouchRadiusLease> TouchRadiusOwners = new();
    private static string pendingTouchRadius { get => RuntimeCharts.Current.pendingTouchRadius; set => RuntimeCharts.Current.pendingTouchRadius = value; }

    private static void ReadTouchRadiusMarker(MA2Record rec)
    {
        pendingTouchRadius = null;
        if (rec?._str == null || rec._str.Count < 5) return;
        var tail = rec._str[rec._str.Count - 1];
        if (!tail.StartsWith("TR1|", StringComparison.Ordinal)) return;
        pendingTouchRadius = tail;
        rec._str.RemoveAt(rec._str.Count - 1);
    }
    private static void RestoreTouchRadiusMarker(MA2Record rec)
    {
        if (pendingTouchRadius != null) rec._str.Add(pendingTouchRadius);
    }
    private static void ApplyTouchRadiusMarker(NoteData note)
    {
        TouchRadii.Remove(note);
        if (pendingTouchRadius == null) return;
        if (note.type.getEnum() == NotesTypeID.Def.TouchTap && note.touchArea != TouchSensorType.C &&
            TouchRadius.Decode(pendingTouchRadius, out var radius))
            TouchRadii.Add(note, new TouchRadiusValue { Radius = radius });
        else MelonLogger.Warning("[Touch Radius] Invalid radius metadata: " + pendingTouchRadius);
        pendingTouchRadius = null;
    }
    private static void RestoreTouchRadius(TouchNoteB owner)
    {
        if (!TouchRadiusOwners.TryGetValue(owner, out var lease)) return;
        if (owner != null) owner.transform.localPosition = lease.Position;
        TouchRadiusOwners.Remove(owner);
    }

    [HarmonyPatch(typeof(TouchNoteB), "Initialize")]
    public static class TouchRadiusInitializePatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(TouchNoteB __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        RestoreTouchRadius(__instance);
    }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(TouchNoteB __instance, NoteData note)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            // Native Initialize has already selected the mirrored area's
            // launcher. Translate the visual along its local radial axis.
            // ButtonId, TouchArea, StartPos, clock, sibling queue, grade and
            // effects remain native; no input/judgment method is intercepted.
            if (__instance is TouchHoldC || !TouchRadii.TryGetValue(note, out var value)) return;
            var original = __instance.transform.localPosition;
            TouchRadiusOwners[__instance] = new TouchRadiusLease { Position = original, Monitor = __instance.MonitorId };
            __instance.transform.localPosition = original + new Vector3(0, value.Radius * TouchRadius.Units - GameCtrl.TouchStartPos((int)note.touchArea), 0);
        }
    }

    [HarmonyPatch(typeof(GameCtrl), "UpdateNotes")]
    public static class TouchRadiusLifetimePatch
    {
        [HarmonyPostfix]
        public static void Postfix(GameCtrl __instance)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            foreach (var pair in TouchRadiusOwners.Where(p => p.Value.Monitor == __instance.MonitorIndex).ToArray())
                if (pair.Key == null || !pair.Key.gameObject.activeInHierarchy || pair.Key.IsEnd()) RestoreTouchRadius(pair.Key);
        }
    }
    [HarmonyPatch(typeof(GameCtrl), "ForceNoteCollect")]
    public static class TouchRadiusCollectPatch
    {
        [HarmonyPrefix]
        public static void Prefix(GameCtrl __instance)
        {
            foreach (var pair in TouchRadiusOwners.Where(p => p.Value.Monitor == __instance.MonitorIndex).ToArray()) RestoreTouchRadius(pair.Key);
        }
    }
}
