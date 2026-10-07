using System;
using System.Runtime.CompilerServices;
using DB;
using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public static partial class CustomNoteTypes
{
    // Native NoteData keeps its original note category so break/EX scoring and
    // pools remain native. The extension only records the D-ring position.
    private sealed class DZonePosition
    {
        public int SourceKey;
    }

    private static readonly ConditionalWeakTable<NoteData, DZonePosition> DZonePositions =
        new ConditionalWeakTable<NoteData, DZonePosition>();
    private static readonly ConditionalWeakTable<NoteBase, DZoneLane> DZoneOwners =
        new ConditionalWeakTable<NoteBase, DZoneLane>();
    private static readonly ConditionalWeakTable<Transform, DZoneLane> DZoneLanes =
        new ConditionalWeakTable<Transform, DZoneLane>();
    private static int pendingDZoneKey { get => RuntimeCharts.Current.pendingDZoneKey; set => RuntimeCharts.Current.pendingDZoneKey = value; }
    private static bool removedDZoneMarker { get => RuntimeCharts.Current.removedDZoneMarker; set => RuntimeCharts.Current.removedDZoneMarker = value; }

    private static void ReadDZoneMarker(MA2Record rec)
    {
        pendingDZoneKey = -1;
        removedDZoneMarker = false;
        if (rec?._str == null || rec._str.Count < 5) return;
        var tag = rec._str[0];
        if (!(tag.EndsWith("TAP", StringComparison.Ordinal) ||
              tag.EndsWith("STR", StringComparison.Ordinal) ||
              tag.EndsWith("HLD", StringComparison.Ordinal))) return;
        if (rec._str[rec._str.Count - 1] != "DZ" ||
            !int.TryParse(rec._str[3], out var key) || key < 0 || key > 7) return;
        pendingDZoneKey = key;
        rec._str.RemoveAt(rec._str.Count - 1);
        removedDZoneMarker = true;
    }

    private static void RestoreDZoneMarker(MA2Record rec)
    {
        if (removedDZoneMarker) rec._str.Add("DZ");
        removedDZoneMarker = false;
    }

    private static void ApplyDZonePosition(NoteData note, int playerId)
    {
        DZonePositions.Remove(note);
        if (pendingDZoneKey >= 0)
        {
            DZonePositions.Add(note, new DZonePosition { SourceKey = pendingDZoneKey });
            // D1 lies on the vertical axis. Its reflection table differs from A1.
            var mirror = Singleton<GamePlayManager>.Instance.GetGameScore(playerId).UserOption.MirrorMode;
            note.startButtonPos = MirrorDZoneKey(pendingDZoneKey, mirror);
        }
        pendingDZoneKey = -1;
    }

    internal static int MirrorDZoneKey(int key, OptionMirrorID mirror)
    {
        switch (mirror)
        {
            case OptionMirrorID.LR: return (-key) & 7;
            case OptionMirrorID.UD: return (4 - key) & 7;
            case OptionMirrorID.UDLR: return (key + 4) & 7;
            default: return key;
        }
    }

    internal static bool TryDZoneArea(NoteBase owner, out InputManager.TouchPanelArea area)
    {
        if (owner != null && DZoneOwners.TryGetValue(owner, out var lane) && lane != null)
        {
            area = (InputManager.TouchPanelArea)((int)InputManager.TouchPanelArea.D1 + lane.Key);
            return true;
        }
        area = default;
        return false;
    }

    [HarmonyPatch(typeof(NoteBase), "Initialize")]
    public static class DZoneInitializePatch
    {
        public static void Postfix(NoteBase __instance, NoteData note)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            // Pool reuse must never carry a D input binding into an ordinary note.
            DZoneOwners.Remove(__instance);
            if (!DZonePositions.TryGetValue(note, out _) || __instance.transform.parent == null) return;
            var nativeLauncher = __instance.transform.parent;
            if (!DZoneLanes.TryGetValue(nativeLauncher, out var lane) || lane == null)
            {
                DZoneLanes.Remove(nativeLauncher);
                lane = DZoneLane.Create(nativeLauncher, note.startButtonPos, __instance.MonitorId);
                DZoneLanes.Add(nativeLauncher, lane);
            }
            // Separate launchers retain the native sibling queue. D notes therefore
            // cannot block A notes, and successive D notes still consume input once.
            __instance.transform.SetParent(lane.transform, false);
            __instance.transform.SetAsFirstSibling();
            DZoneOwners.Add(__instance, lane);
        }
    }

    [HarmonyPatch(typeof(NoteBase), "SetJudgeObject")]
    public static class DZoneJudgeObjectsPatch
    {
        public static void Prefix(NoteBase __instance, ref JudgeGrade judgeGrade, ref TouchEffect judgeEffect)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            if (!DZoneOwners.TryGetValue(__instance, out var lane) || lane == null) return;
            judgeGrade = lane.Grade;
            judgeEffect = lane.Effect;
        }
    }

    [HarmonyPatch(typeof(GameCtrl), "UpdateNotes")]
    public static class DZoneEffectsUpdatePatch
    {
        public static void Postfix(GameCtrl __instance)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            DZoneLane.Tick(__instance.MonitorIndex);
        }
    }

    [HarmonyPatch(typeof(GameCtrl), "ForceNoteCollect")]
    public static class DZoneEffectsResetPatch
    {
        public static void Postfix(GameCtrl __instance)
        {
            DZoneLane.ResetEffects(__instance.MonitorIndex);
        }
    }
}
