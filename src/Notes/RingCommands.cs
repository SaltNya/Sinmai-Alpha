using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SinmaiAlpha.ChartVisuals;
using HarmonyLib;
using Manager;
using MelonLoader;
using Monitor;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private static List<(int Bar, int Grid, string Kind, string Text)> PendingRingSegments => RuntimeCharts.Current.PendingRingSegments;
    private static List<RingChange> RingChanges => RuntimeCharts.Current.RingChanges;
    private static Dictionary<int, RingValue> RingValuesByNoteIndex => RuntimeCharts.Current.RingValuesByNoteIndex;
    private sealed class RingCrossingLease
    {
        public bool HeadKnown, TailKnown;
        public float HeadTime, TailTime;
    }
    private static readonly ConditionalWeakTable<NoteBase, RingCrossingLease> RingCrossings = new();
    private static void ReadRingCommand(string str)
    {
        if (str == null) return;
        var parts = str.Split('\t');
        if (parts.Length != 4) return;
        var kind = parts[0].ToLowerInvariant();
        if (RingState.IsKind(kind) && int.TryParse(parts[1], out var bar) && int.TryParse(parts[2], out var grid))
            PendingRingSegments.Add((bar, grid, kind, parts[3]));
    }
    private static void ResetRingCommands()
    {
        PendingRingSegments.Clear(); RingChanges.Clear(); RingValuesByNoteIndex.Clear();
        SpawnRadiusByNoteIndex.Clear();
    }
    private static void BuildRingCommands(NotesReader reader)
    {
        RingChanges.Clear(); RingValuesByNoteIndex.Clear(); SpawnRadiusByNoteIndex.Clear();
        foreach (var command in PendingRingSegments)
        {
            var time = new NotesTime(); time.init(command.Bar, command.Grid, reader);
            if (RingState.TryParse(command.Kind, command.Text, time.msec, out var changes, allowStreams: true))
                RingChanges.AddRange(changes);
            else MelonLogger.Warning("[Ring Visual] Invalid " + command.Kind + ": " + command.Text);
        }
        var ordered = RingChanges.OrderBy(c => c.Time).ToList();
        RingChanges.Clear(); RingChanges.AddRange(ordered); // stable authored order
        foreach (var note in reader.GetNoteList())
        {
            if (note == null) continue;
            var classification = SpeedClass(note);
            if (!RingState.IsType(classification.Family)) continue;
            var stream = StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var scope) ? scope : "";
            var value = RingState.Resolve(RingChanges, note.time.msec, classification, stream);
            RingValuesByNoteIndex[note.indexNote] = value;
            SpawnRadiusByNoteIndex[note.indexNote] = value.Spawn;
        }
        if (PendingRingSegments.Count != 0)
            MelonLogger.Msg($"[Ring Visual] SPAWN/SPAWNMODE/DESTROY: {RingChanges.Count} changes, {RingValuesByNoteIndex.Count} notes");
    }
    private static RingValue RingSettings(int index, float appear, string type)
        => RingValuesByNoteIndex.TryGetValue(index, out var value) ? value :
            RingState.Resolve(RingChanges, appear, new VisualNote { Family = type }, RingState.IsStream(type) ? type : "");

    // Head and tail cache their own first crossing of the whole loaded history.
    // A clock rewind compares against that first time, rather than retaining a
    // stale boolean from a later frame. This never changes a native judge flag.
    private static bool RingEverCrossed(NoteBase owner, bool tail, float target, float speed, RingValue settings, float now)
    {
        var lease = RingCrossings.GetOrCreateValue(owner);
        var known = tail ? lease.TailKnown : lease.HeadKnown;
        var crossing = tail ? lease.TailTime : lease.HeadTime;
        if (!known)
        {
            crossing = float.NaN;
            if (NoteScrollTableByNoteIndex.TryGetValue(owner.GetNoteIndex(), out var table))
                crossing = ScrollVisualTiming.FirstCrossing(table, target, speed, settings.Spawn, settings.Destroy);
            if (tail) { lease.TailKnown = true; lease.TailTime = crossing; }
            else { lease.HeadKnown = true; lease.HeadTime = crossing; }
        }
        return !float.IsNaN(crossing) && now >= crossing;
    }
    [HarmonyPatch]
    public static class RingCrossingInitializePatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => FakeOwnerMethods().Where(m => m.Name == "Initialize" &&
            typeof(NoteBase).IsAssignableFrom(m.DeclaringType) && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(NoteData));
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(NoteBase __instance) {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }
        RingCrossings.Remove(__instance);
    }
    }
}
