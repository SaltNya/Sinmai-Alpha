using CustomSlideNoteData = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.UnifiedSlideNoteData;
using SlideTable = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideTable;
using SlideJudgeProfile = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideJudgeProfile;
using SlideDefinition = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideDefinition;
using System;
using System.Runtime.CompilerServices;
using DB;
using SinmaiAlpha.Notes.Libs;

static class SlideTableTests
{
    static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    static CustomSlideNoteData Load(string code, OptionMirrorID mirror = OptionMirrorID.Normal)
    {
        var note = new CustomSlideNoteData();
        Check(note.ParseSlideCode(code, mirror), "Could not load " + code);
        return note;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static (SlideTable Table, WeakReference Geometry) CompileForLifetime(string code)
    {
        var definition = SlideDefinition.Parse(code);
        return (SlideTable.Build(definition, OptionMirrorID.Normal), new WeakReference(definition.Paths[0]));
    }

    internal static void Run()
    {
        // Ring/touch appearance is a separate capability from sensor selection.
        foreach (var item in new[] {
            ("1K4", false, false, false), ("1B2A6CK7", false, true, false),
            ("B1K5", true, true, false), ("1E2K3", true, true, false),
            ("DG1:1d-5", true, false, false), ("DG2:1K4", true, true, false),
            ("TG1:1-4", true, false, true), ("TG1:1>B2>C", true, true, true),
            ("DF1:1w5", false, false, false), ("DF1:1dw5", true, false, false) })
        {
            var data = Load(item.Item1);
            Check((data.JudgeProfile == SlideJudgeProfile.ExtendedTouch) == item.Item2, "Wrong sensors " + item.Item1);
            Check(data.TouchAppearance == item.Item3, "Appearance changed " + item.Item1);
            Check((data.TouchGeometry != null) == item.Item4, "Touch SV geometry changed " + item.Item1);
        }

        for (var mirror = 0; mirror < 4; mirror++)
            foreach (var code in new[] { "DF1:1w5", "DF1:1dw5", "DF1:1w5d", "DF1:1dw5d" })
            {
                var data = Load(code, (OptionMirrorID)mirror);
                for (var star = 0; star < 8; star++) for (var lane = 0; lane < 3; lane++)
                {
                    var end = (data.slideData.targetNote + lane + 7) & 7;
                    Check(ReferenceEquals(data.GetSlidePath(star, end), data.Table.Frames[star].Lanes[lane].Path), "Fan path lane routing");
                    Check(ReferenceEquals(data.GetSlideHitAreas(star, end), data.Table.Frames[star].Lanes[lane].HitAreas), "Fan sensor lane routing");
                }
            }

        // A native consumer is allowed to keep/mutate its own list. Loading the
        // same code for another note/player must not alias these mutable tables.
        var first = Load("DG2:1K4");
        var second = Load("DG2:1K4");
        first.GetSlideHitAreas(0)[0].HitPoints.Clear();
        first.GetSlidePath(0).Clear();
        Check(second.GetSlideHitAreas(0)[0].HitPoints.Count > 0 && second.GetSlidePath(0).Count > 0, "Tables shared across notes");
        Check(first.GetSlideHitAreas(1)[0].HitPoints.Count > 0 && first.GetSlidePath(1).Count > 0, "Tables shared across star frames");

        var reusable = Load("DF1:1dw5");
        foreach (var invalid in new[] { "DF1:1w4", "TG1:bad", "DG2:bad", "MV2:1-4", "", null })
        {
            Check(!reusable.ParseSlideCode(invalid, OptionMirrorID.LR), "Invalid geometry accepted");
            Check(reusable.Table == null && !reusable.IsFan && reusable.TouchGeometry == null && reusable.SlidePathLength == 0,
                "A failed load retained previous tables");
            Check(reusable.ParseSlideCode("TG1:1>B2>C", OptionMirrorID.UD), "Could not recover after rejected geometry");
            Check(reusable.TouchMirror == OptionMirrorID.UD, "Mirror leaked from previous geometry");
        }
        Check(reusable.ParseSlideCode("1K4", OptionMirrorID.Normal) && !reusable.IsFan && reusable.TouchGeometry == null &&
            reusable.JudgeProfile == SlideJudgeProfile.StandardAbc && !reusable.TouchAppearance, "Touch flags leaked into ordinary SC");

        var lifetimes = new[] { CompileForLifetime("1Q9K3"), CompileForLifetime("DG1:1dpp5d"),
            CompileForLifetime("DF1:1dw5"), CompileForLifetime("TG1:1>B2>C") };
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        for (var i = 0; i < lifetimes.Length; i++)
        {
            Check(lifetimes[i].Geometry.IsAlive == (i == 3), "Unneeded source geometry retained after compilation");
            GC.KeepAlive(lifetimes[i].Table);
        }

        // All tables are prepared at load time: gameplay lookup must allocate
        // no arrays, perform no lazy generation and return stable native lists.
        var fan = Load("DF1:1dw5");
        _ = fan.GetSlidePath(0); _ = fan.GetSlideHitAreas(0);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var count = 0;
        for (var i = 0; i < 100000; i++)
        {
            count += fan.GetSlidePath(i & 7, (fan.slideData.targetNote + i % 3 + 7) & 7).Count;
            count += reusable.GetSlideHitAreas(i & 7).Count;
        }
        Check(GC.GetAllocatedBytesForCurrentThread() == before && count > 0, "Gameplay table lookup allocated memory");
        Console.WriteLine("PASS normalized capabilities, fan lane routing, note/frame isolation, rejected reloads, geometry collection, 200000 allocation-free lookups");
    }
}
