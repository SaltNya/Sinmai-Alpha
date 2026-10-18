using System;
using System.Collections.Generic;
using HarmonyLib;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using SinmaiAlpha.Notes.Libs;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // The reader and final user speed are both available after binding. Account
    // for all leased ring carriers, including mines used as decoration, not only FK.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameCtrl), "SetNoteManager")]
    public static void PrewarmFakeRingCarriers(GameCtrl __instance)
    {
        if (!FeaturesEnabled(__instance)) return;
        var fields = Traverse.Create(__instance);
        var reader = fields.Field("NoteMng").GetValue<NotesManager>()?.getReader();
        if (reader == null) return;
        var scope = RuntimeCharts.Enter(RuntimeCharts.Reader(reader));
        try
        {
            var windows = new Dictionary<(string Pool, bool Mine), List<(float Time, int Change)>>();
            var guides = new List<(float Time, int Change)>();
            var lead = fields.Field("apperMsecTap").GetValue<float>();
            foreach (var note in reader.GetNoteList())
            {
                if (note == null || IsBorrowed(note)) continue;
                var pool = RingPoolName(note);
                if (pool == null) continue;
                var key = (pool, GetNoteKind(note) == CustomNoteKind.Mine);
                if (!windows.TryGetValue(key, out var events)) windows[key] = events = new();
                var start = note.time.msec - (lead > 0 ? SvScaleActivationLead(lead, 2f, note) : 10000f);
                // Include late judgment and next-frame collection; fake carriers
                // actually retire after 150 ms, so this is conservative for them.
                var end = Math.Max(note.time.msec, note.end.msec) + 300f;
                events.Add((start, 1)); events.Add((end, -1));
                guides.Add((start, 1)); guides.Add((end, -1));
            }
            GrowRingGuides(__instance, VisualPoolCapacity.Reserve(VisualPoolCapacity.Peak(guides)));
            foreach (var pair in windows)
            {
                var peak = VisualPoolCapacity.Peak(pair.Value);
                GrowRingPool(__instance, pair.Key.Pool, pair.Key.Mine, VisualPoolCapacity.Reserve(peak));
            }
        }
        finally { RuntimeCharts.Exit(scope); }
    }

    [HarmonyPostfix, HarmonyPatch(typeof(GameCtrl), "ResetOptionSpeed")]
    public static void RefreshRingPoolCapacity(GameCtrl __instance) => PrewarmFakeRingCarriers(__instance);

    private static string RingPoolName(NoteData note) => note.type.getEnum() switch
    {
        NotesTypeID.Def.Tap or NotesTypeID.Def.ExTap => "tap",
        NotesTypeID.Def.Star or NotesTypeID.Def.ExStar => "star",
        NotesTypeID.Def.Hold or NotesTypeID.Def.ExHold => "hold",
        NotesTypeID.Def.Break or NotesTypeID.Def.ExBreakTap => "break",
        NotesTypeID.Def.BreakStar or NotesTypeID.Def.ExBreakStar => "breakStar",
        NotesTypeID.Def.BreakHold or NotesTypeID.Def.ExBreakHold => "breakHold",
        _ => null
    };

    private static void GrowRingGuides(GameCtrl controller, int required)
        => GrowRingComponents(controller, "guide", false, required, GameNotePrefabContainer.Guide, null);

    private static bool GrowRingPool(GameCtrl controller, string name, bool mine, int required)
        => name switch
        {
            "tap" => GrowRingComponents<TapNote>(controller, name, mine, required, GameNotePrefabContainer.Tap,
                p => MineTapNote.CreateFrom(GameNotePrefabContainer.Tap, p)),
            "star" => GrowRingComponents<StarNote>(controller, name, mine, required, GameNotePrefabContainer.Star,
                p => MineStarNote.CreateFrom(GameNotePrefabContainer.Star, p)),
            "hold" => GrowRingComponents<HoldNote>(controller, name, mine, required, GameNotePrefabContainer.Hold,
                p => MineHoldNote.CreateFrom(GameNotePrefabContainer.Hold, p)),
            "break" => GrowRingComponents<BreakNote>(controller, name, mine, required, GameNotePrefabContainer.Break,
                p => MineBreakNote.CreateFrom(GameNotePrefabContainer.Break, p)),
            "breakStar" => GrowRingComponents<BreakStarNote>(controller, name, mine, required, GameNotePrefabContainer.BreakStar,
                p => MineBreakStarNote.CreateFrom(GameNotePrefabContainer.BreakStar, p)),
            "breakHold" => GrowRingComponents<BreakHoldNote>(controller, name, mine, required, GameNotePrefabContainer.BreakHold,
                p => MineBreakHoldNote.CreateFrom(GameNotePrefabContainer.BreakHold, p)),
            _ => false
        };

    private static System.Collections.IList RingPoolList(GameCtrl controller, string name, bool mine)
    {
        var field = "_" + name + "ObjectList";
        if (!mine) return Traverse.Create(controller).Field(field).GetValue<System.Collections.IList>();
        return MinePools.TryGetValue(controller, out var pools) && pools.TryGetValue(field, out var list)
            ? (System.Collections.IList)list : null;
    }

    private static bool GrowRingComponents<T>(GameCtrl controller, string name, bool mine, int required,
        T prefab, Func<Transform, T> mineFactory) where T : Component
    {
        var list = RingPoolList(controller, name, mine);
        if (list == null || list.Count >= required) return false;
        var parent = Traverse.Create(controller).Field("_" + name + "ListParent").GetValue<GameObject>();
        if (parent == null || prefab == null) return false;
        var before = list.Count;
        while (list.Count < required)
        {
            var note = mine ? mineFactory(parent.transform) : Object.Instantiate(prefab, parent.transform);
            note.gameObject.SetActive(false);
            TrySetParentTransform(note, parent.transform);
            if (mine) ApplyMineTexturesToObject(note.gameObject);
            list.Add(note);
        }
        MelonLogger.Msg($"[Visual Pool] {(mine ? "mine " : "")}{name}: {before}->{list.Count}, monitor={controller.MonitorIndex}");
        return true;
    }

    // A failure here normally stops native registration for every later note.
    // Rare fallback for a changed speed/other mod keeping carriers alive longer;
    // normal frames do no extra scanning or allocation.
    [HarmonyPostfix, HarmonyPriority(Priority.First)]
    [HarmonyPatch(typeof(GameCtrl), "RegistNote")]
    public static void RetryFullRingPool(GameCtrl __instance, NoteData note, ref bool __result)
    {
        if (__result || note == null || !FeaturesEnabled(__instance) || IsBorrowed(note)) return;
        var name = RingPoolName(note);
        if (name == null || !RingPoolRetries.Add(__instance)) return;
        try
        {
            var mine = GetNoteKind(note) == CustomNoteKind.Mine;
            var list = RingPoolList(__instance, name, mine);
            if (list == null) return;
            var before = list.Count;
            var target = VisualPoolCapacity.Reserve(before + 1);
            if (!GrowRingPool(__instance, name, mine, target)) return;
            var guides = RingPoolList(__instance, "guide", false);
            if (guides != null) GrowRingGuides(__instance, guides.Count + target - before);
            MelonLogger.Warning($"[Visual Pool] Registration retry: {(mine ? "mine " : "")}{name}, note={note.indexNote}, time={NotesManager.GetCurrentMsec():F1} ms");
            __result = __instance.RegistNote(note);
        }
        finally { RingPoolRetries.Remove(__instance); }
    }
    private static readonly HashSet<GameCtrl> RingPoolRetries = new();
}
