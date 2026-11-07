using CustomSlideNoteData = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.UnifiedSlideNoteData;
using System;
using System.Collections.Generic;
using System.Linq;
using DB;
using Manager;
using SinmaiAlpha.Notes.Libs;

static class NativeVTests
{
    static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    static CustomSlideNoteData Load(string code, int start, int end, OptionMirrorID mirror)
    {
        var data = new CustomSlideNoteData { startButtonPos = MaiGeometry.MirrorInfo[(int)mirror, start] };
        data.slideData.targetNote = MaiGeometry.MirrorInfo[(int)mirror, end];
        Check(data.ParseSlideCode(code, mirror), "V parse " + code);
        return data;
    }
    static SlideType MirrorType(SlideType type, int mirror) => mirror is 1 or 2
        ? type == SlideType.Slide_Skip_L ? SlideType.Slide_Skip_R : type == SlideType.Slide_Skip_R ? SlideType.Slide_Skip_L : type
        : type;

    static void EqualAreas(List<SlideManager.HitArea> expected, List<SlideManager.HitArea> actual, string label)
    {
        Check(expected.Count == actual.Count, "V area count " + label);
        for (var i = 0; i < expected.Count; i++)
            Check(expected[i].HitPoints.SequenceEqual(actual[i].HitPoints) && expected[i].PushDistance == actual[i].PushDistance &&
                expected[i].ReleaseDistance == actual[i].ReleaseDistance, "V native table differs " + label + " area=" + i);
    }

    internal static void Run()
    {
        var cases = 0; var replayFrames = 0; var random = new Random(170);
        foreach (var row in SlideManager.Cases.Where(r => r.GetProperty("type").GetInt32() is 8 or 11 or 12))
            for (var rotation = 0; rotation < 8; rotation++) for (var mirror = 0; mirror < 4; mirror++)
            {
                var code = Program.Rotate(row.GetProperty("code").GetString(), rotation);
                var end = (row.GetProperty("end").GetInt32() - 1 + rotation) & 7;
                var type = MirrorType((SlideType)row.GetProperty("type").GetInt32(), mirror);
                var data = Load(code, rotation, end, (OptionMirrorID)mirror);
                var start = data.startButtonPos;
                end = data.slideData.targetNote;
                Check(data.Table.UsesNativeVTable && data.slideData.type == type, "Native V type lost " + code);
                for (var star = 0; star < 8; star++)
                    EqualAreas(SlideManager.NativeVFixture(type, start, end, star), data.GetSlideHitAreas(star), code);

                var native = SlideManager.NativeVFixture(type, start, end, start);
                var custom = data.GetSlideHitAreas(start);
                foreach (var duration in new[] { 80f, 500f, 1000f, 5000f })
                {
                    // SlideRoot.Initialize derives both lastWaitTime and
                    // lastWaitTimeForJudge from this exact float calculation.
                    float Wait(List<SlideManager.HitArea> h) => duration * (float)(h[^1].PushDistance / h.Sum(a => a.PushDistance + a.ReleaseDistance));
                    Check(Wait(native) == Wait(custom), "Native V tail judgment window differs");
                }
                var expected = new NativeJudgeReplay(type, start, end, native);
                var actual = new NativeJudgeReplay(data.slideData.type, start, end, custom);
                for (var index = 0; index < native.Count; index++) foreach (var entered in new[] { false, true })
                {
                    expected.Reset(index, entered); actual.Reset(index, entered);
                    Check(expected.NextCheck() == actual.NextCheck(), "Native V skip gate differs");
                    for (var frame = 0; frame < 64; frame++)
                    {
                        var pressed = (ulong)random.Next(1 << 17);
                        expected.Step(pressed); actual.Step(pressed); replayFrames++;
                        Check(expected.State == actual.State, "Native V input progression differs");
                    }
                }
                cases++;
            }

        // Demonstrate the prior bug: tagging an official big V as Straight
        // allowed the mandatory A2/B2 checkpoint (and wide-V A4/B4) to be skipped.
        var v = Load("1A3K5", 0, 4, OptionMirrorID.Normal);
        var good = new NativeJudgeReplay(v.slideData.type, 0, 4, v.GetSlideHitAreas(0));
        var old = new NativeJudgeReplay(SlideType.Slide_Straight, 0, 4, v.GetSlideHitAreas(0));
        foreach (var index in new[] { 1, 3 })
        {
            good.Reset(index, false); old.Reset(index, false);
            Check(!good.NextCheck() && old.NextCheck(), "Old V skip regression not exposed");
            var pressed = 1UL << (index == 1 ? 2 : 4);
            good.Step(pressed); old.Step(pressed);
            Check(good.State.Index == index && old.State.Index > index, "Mandatory checkpoint bypassed");
            good.Reset(index, true);
            Check(good.NextCheck(), "Entered checkpoint should allow the next area");
        }

        // Redundant nodes preserve the same physical legs and native rule.
        Check(Load("1B1CK3", 0, 2, OptionMirrorID.Normal).Table.UsesNativeVTable, "Collinear center leg not normalized");
        Check(Load("1A3B3CK7", 0, 6, OptionMirrorID.Normal).Table.UsesNativeVTable, "Collinear big V leg not normalized");
        var before = SlideManager.NativeVCalls;
        foreach (var code in new[] { "1K5", "1CK5", "1A3K2", "1A4K6", "1A3A5K7", "1Q9A3K5",
            "DG2:1A3K5", "DG1:1V35", "TG1:1V35", "B1K5", "DF1:1w5" })
            Check(!Load(code, 0, 4, OptionMirrorID.Normal).Table.UsesNativeVTable, "Non-native/touch path incorrectly replaced " + code);
        Check(SlideManager.NativeVCalls == before, "Non-V path used native tables");

        // The restored return-v is used only if the running game supplies it.
        SlideManager.RestoredReturnV = false;
        try { Check(!Load("1CK1", 0, 0, OptionMirrorID.Normal).Table.UsesNativeVTable, "Missing native V table did not fall back"); }
        finally { SlideManager.RestoredReturnV = true; }
        Console.WriteLine($"PASS {cases} native v/V rotations/mirrors, {cases * 8} exact tables, tail windows, {replayFrames} manual input frames; mandatory checkpoints and non-native opt-out");
    }
}
