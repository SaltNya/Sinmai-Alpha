using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Manager;
using MelonLoader;
using Monitor;
using SinmaiAlpha.Notes.Libs;
using SlideTable = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideTable;
using UnifiedNote = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.UnifiedSlideNoteData;
using DB;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge;

internal static class RuntimeCache
{
    private sealed class Entry
    {
        internal SlideTable Source;
        internal CompiledJudgeNote Compiled;
        internal string Code;
    }

    private static ConditionalWeakTable<CustomSlideNoteData, Entry> notes = new();
    private static ConditionalWeakTable<SlideRoot, RuntimeInputState> players = new();
    private static readonly HashSet<string> Reported = new();

    private static bool Eligible(CustomSlideNoteData note, SlideTable table) => table != null && !note.IsFan &&
        !table.UsesNativeVTable && (note.child == null || note.child.Count == 0) &&
        (ReferenceEquals(note.type, null) || note.type.getEnum() != NotesTypeID.Def.ConnectSlide);

    internal static void Forget(CustomSlideNoteData note) => notes.Remove(note);

    internal static bool Parse(CustomSlideNoteData note, string code, OptionMirrorID mirror)
    {
        Forget(note);
        if (!UnifiedNote.TryParseInto(note, code, mirror, out var table)) return false;
        var entry = new Entry { Source = table, Code = code };
        notes.Add(note, entry);
        if (!Eligible(note, table)) return true;
        try
        {
            entry.Compiled = NativeTableCompiler.Compile(code, mirror);
        }
        catch (Exception e) when (e is ArgumentException || e is FormatException || e is InvalidOperationException)
        {
            // Failure never mutates the unified table. Limit reporting per
            // reason/chart so a repeated decorative path cannot flood logs.
            lock (Reported)
                if (Reported.Add(e.Message))
                    MelonLogger.Warning("[Experimental Slide Judge] Fallback to unified table (no contour input): " + e.Message +
                        " code=" + (note.SlideCode.Length > 160 ? note.SlideCode.Substring(0, 160) : note.SlideCode));
        }
        return true;
    }

    internal static bool TryFrame(CustomSlideNoteData note, int star, out CompiledJudgeFrame frame)
    {
        frame = null;
        // calcSlide runs after ParseSlideCode. Re-check the final child graph
        // and ConnectSlide type here; a chain uses unified tables throughout,
        // without partially substituting the newer contour intervals.
        if (note == null || (uint)star >= 8 || !notes.TryGetValue(note, out var entry) || entry.Compiled == null ||
            !Eligible(note, entry.Source) || note.SlideCode != entry.Code || note.SlideHitAreasList.Count != 8 ||
            !ReferenceEquals(note.SlideHitAreasList[star], entry.Source.Frames[star].Lanes[0].HitAreas)) return false;
        frame = entry.Compiled.Frames[star];
        return true;
    }

    internal static void Detach(SlideRoot slide) => players.Remove(slide);

    internal static void Attach(SlideRoot slide, NoteData note, List<SlideManager.HitArea> actual)
    {
        Detach(slide);
        if (slide is SlideFan || note is not CustomSlideNoteData custom ||
            !TryFrame(custom, slide.ButtonId, out var frame) || actual == null || actual.Count != frame.Areas.Count) return;
        // Do not apply our input rules to a table replaced by another mod.
        for (int i = 0; i < actual.Count; i++)
            if (!ReferenceEquals(actual[i], frame.Areas[i])) return;
        players.Add(slide, new RuntimeInputState(frame));
    }

    internal static bool TryPlayer(SlideRoot slide, out RuntimeInputState state) => players.TryGetValue(slide, out state);

    internal static void Clear()
    {
        notes = new ConditionalWeakTable<CustomSlideNoteData, Entry>();
        players = new ConditionalWeakTable<SlideRoot, RuntimeInputState>();
        lock (Reported) Reported.Clear();
    }
}
