using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Manager;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class RingActivationCacheEntry
    {
        internal NoteData Note;
        internal int NoteIndex, TableCount;
        internal NotesTypeID NoteTypeOwner;
        internal NotesTypeID.Def? NoteType;
        internal float Appear, Duration, Hs, Target, Spawn, Destroy, Value;
        internal string ScrollType;
        internal bool ReferenceMotion, Once;
        internal List<(float Msec, float Scroll, float Max)> Table;
    }

    // A reader's runtime state owns the entries. Fresh loads/reset replace the
    // state; a rebuild replaces each immutable scroll table. Neither can reuse
    // an earlier chart's activation time, including the other player's chart.
    private static readonly ConditionalWeakTable<ChartRuntimeState, Dictionary<int, RingActivationCacheEntry>> RingActivationLeadCaches = new();
    private static readonly ConditionalWeakTable<ChartRuntimeState, Dictionary<int, RingActivationCacheEntry>>.CreateValueCallback RingActivationLeadCacheFactory = CreateRingActivationLeadCache;

    private static Dictionary<int, RingActivationCacheEntry> CreateRingActivationLeadCache(ChartRuntimeState state) => new();

    private static float GetRingFirstVisibleTimeCached(NoteData note, float duration)
    {
        var state = RuntimeCharts.Current;
        if (note == null || duration <= 0 ||
            !state.NoteScrollTableByNoteIndex.TryGetValue(note.indexNote, out var table) || table.Count == 0 ||
            !state.HsMultByNoteIndex.TryGetValue(note.indexNote, out var hs) ||
            !state.SvScrollPosByNoteIndex.TryGetValue(note.indexNote, out var target) ||
            !state.SvTypeByNoteIndex.TryGetValue(note.indexNote, out var type))
            return RingFirstVisibleTime(note, duration);

        var index = note.indexNote;
        var appear = note.time.msec;
        var noteType = note.type?.getEnum();
        var referenceMotion = state.ReferenceMotionByNoteIndex.Contains(index);
        var settings = RingSettings(index, appear, type);
        var cache = RingActivationLeadCaches.GetValue(state, RingActivationLeadCacheFactory);
        if (cache.TryGetValue(index, out var entry) &&
            ReferenceEquals(entry.Note, note) && entry.NoteIndex == index &&
            ReferenceEquals(entry.NoteTypeOwner, note.type) && entry.NoteType == noteType &&
            entry.Appear == appear && entry.Duration == duration &&
            ReferenceEquals(entry.Table, table) && entry.TableCount == table.Count &&
            entry.Hs == hs && entry.Target == target && entry.ScrollType == type &&
            entry.ReferenceMotion == referenceMotion && entry.Spawn == settings.Spawn &&
            entry.Destroy == settings.Destroy && entry.Once == settings.Once)
            return entry.Value;

        // Preserve the original algorithm and its exact float, including NaN.
        // Reusing a per-index entry also avoids allocating when a guard changes.
        var value = RingFirstVisibleTime(note, duration);
        if (entry == null) cache[index] = entry = new RingActivationCacheEntry();
        entry.Note = note; entry.NoteIndex = index; entry.NoteTypeOwner = note.type; entry.NoteType = noteType;
        entry.Appear = appear; entry.Duration = duration; entry.Table = table; entry.TableCount = table.Count;
        entry.Hs = hs; entry.Target = target; entry.ScrollType = type; entry.ReferenceMotion = referenceMotion;
        entry.Spawn = settings.Spawn; entry.Destroy = settings.Destroy; entry.Once = settings.Once;
        entry.Value = value;
        return value;
    }
}
