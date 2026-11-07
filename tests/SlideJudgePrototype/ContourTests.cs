using SlideCodeParser = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideCodeParser;
using SlideJudgeProfile = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideJudgeProfile;
using System.Numerics;
using System.Text.Json;
using Manager;
using SinmaiAlpha.Notes.Libs;

namespace SlideJudgePrototype;

static class ContourTests
{
    static int checks;
    static void Check(bool ok, string message) { checks++; if (!ok) throw new Exception(message); }
    static List<SlideManager.HitArea> Native(JsonElement row, int rotation = 0, int mirror = 0) =>
        row.GetProperty("native").EnumerateArray().Select(a => new SlideManager.HitArea {
            PushDistance = a.GetProperty("push").GetDouble(), ReleaseDistance = a.GetProperty("release").GetDouble(),
            // Keep source priority order; sorting here would hide simultaneous
            // press/release differences even when the set of sensors matches.
            HitPoints = a.GetProperty("panels").EnumerateArray().Select(p => (InputManager.TouchPanelArea)
                PrototypeTests.TransformPanel((int)Enum.Parse<InputManager.TouchPanelArea>(p.GetString()!), rotation, mirror)).ToList()
        }).ToList();
    static PrototypeTable Build(ParametricSlidePath path, SlideJudgeProfile profile = SlideJudgeProfile.StandardAbc) =>
        IntervalJudge.Build(path, profile, RegionModels.Contours);

    public static object Run()
    {
        int calls = SlideManager.NativeVCalls, cases = 0, frames = 0;
        double maxOuterArcTailError = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        foreach (var row in SlideManager.Cases.Where(r => r.GetProperty("type").GetInt32() != 13))
        {
            var path = SlideCodeParser.Parse(row.GetProperty("code").GetString()!);
            var baseline = Build(path);
            for (int rotation = 0; rotation < 8; rotation++) for (int mirror = 0; mirror < 4; mirror++)
            {
                var table = Build(PrototypeTests.Transform(path, rotation, mirror));
                var areas = Native(row, rotation, mirror);
                var type = (SlideType)row.GetProperty("type").GetInt32();
                if (mirror is 1 or 2)
                    type = type == SlideType.Slide_Skip_L ? SlideType.Slide_Skip_R : type == SlideType.Slide_Skip_R ? SlideType.Slide_Skip_L : type;
                int start = PrototypeTests.TransformPanel(0, rotation, mirror);
                int end = PrototypeTests.TransformPanel(row.GetProperty("end").GetInt32() - 1, rotation, mirror);
                var native = new NativeJudgeReplay(type, 0, (end - start + 8) & 7, areas);
                var replay = new PrototypeReplay(table.Steps);
                Check(table.Steps.Count == areas.Count, "contour area count " + row.GetProperty("code"));
                for (int i = 0; i < areas.Count; i++)
                {
                    Check(table.Steps[i].Panels.SequenceEqual(areas[i].HitPoints.Select(p => (int)p)), "ordered sensor choices " + row.GetProperty("code"));
                    Check(Math.Abs(baseline.Steps[i].Enter - table.Steps[i].Enter) < 1e-6 &&
                        Math.Abs(baseline.Steps[i].Exit - table.Steps[i].Exit) < 1e-6, "contour transform distance");
                    var panels = areas.Skip(Math.Max(0, i - 1)).Take(3).SelectMany(a => a.HitPoints).Distinct().ToArray();
                    foreach (bool entered in new[] { false, true })
                    for (int selected = 0; selected < (entered ? areas[i].HitPoints.Count : 1); selected++)
                    {
                        native.Reset(i, entered, selected); replay.Reset(i, entered, (int)areas[i].HitPoints[selected]);
                        Check(native.NextCheck() == replay.NextCheck(), "contour skip gate");
                        // Exhaust every relevant simultaneous mask, then release
                        // its currently selected sensor; don't spend most test
                        // frames comparing two already-completed notes.
                        for (int mask = 0; mask < 1 << panels.Length; mask++)
                        {
                            ulong pressed = 0;
                            for (int p = 0; p < panels.Length; p++) if ((mask & (1 << p)) != 0) pressed |= 1UL << (int)panels[p];
                            native.Reset(i, entered, selected); replay.Reset(i, entered, (int)areas[i].HitPoints[selected]);
                            CompareFrame(pressed);
                            CompareFrame(pressed & ~(1UL << (int)areas[i].HitPoints[selected]));
                            CompareFrame(0);
                        }
                    }
                }
                void CompareFrame(ulong pressed)
                {
                    native.Step(pressed); replay.Step(pressed); frames++;
                    var a = native.State; var b = replay.State;
                    Check(a.Index == b.Index && a.Entered == b.Entered && (a.Index >= areas.Count || !a.Entered ||
                        (int)areas[a.Index].HitPoints[a.SubIndex] == b.Selected), "contour manual progression " + row.GetProperty("code"));
                }
                if (type is SlideType.Slide_Circle_L or SlideType.Slide_Circle_R)
                {
                    double error = Math.Abs(table.TailWait(1000)!.Value - 1000 * areas[^1].PushDistance / areas.Sum(a => a.PushDistance + a.ReleaseDistance));
                    maxOuterArcTailError = Math.Max(maxOuterArcTailError, error);
                    Check(error < .0001, "outer arc tail must follow angular boundaries");
                }
                cases++;
            }
        }
        // The new contour must also work outside the reference set.
        int freeCases = 0;
        var nodes = Enumerable.Range(1, 8).SelectMany(i => new[] { MaiGeometry.PointGroupA(i), MaiGeometry.PointGroupB(i),
            MaiGeometry.PointGroupD(i), MaiGeometry.PointGroupE(i) }).Append(Complex.Zero).ToArray();
        foreach (var a in nodes) foreach (var b in nodes) foreach (var profile in Enum.GetValues<SlideJudgeProfile>())
        {
            var t = Build(new([new ParametricSlidePath.LineSegment(a, b)]), profile);
            Check(t.Steps.All(s => s.Push >= 0 && s.Release >= 0 && double.IsFinite(s.Push + s.Release)), "free contour negative distance");
            Check(Math.Abs(t.Lead + t.Steps.Sum(s => s.Push + s.Release) - t.Path.Length) < 1e-6, "free contour distance sum");
            Check(t.Steps.All(s => s.Panels.All(p => p < (profile == SlideJudgeProfile.StandardAbc ? 17 : 34))), "free contour D/E opt-in");
            freeCases++;
        }
        var radial = Build(SlideCodeParser.Parse("1K5"));
        Check(radial.Steps.Count == 5 && radial.Steps.All(s => s.Panels.Length == 1), "A->B radial crossing must not invent an A/B gap step");
        var loop = Build(SlideCodeParser.Parse("1Q99K5"));
        Check(loop.Steps.Count(s => s.Panels.Contains(0)) >= 2, "contour loop visits lost");
        var concave = new SensorRegion(0, [], [new(new Complex[] { new(-2,-2),new(2,-2),new(2,0),new(0,0),new(0,2),new(-2,2) })]);
        var visits = RegionIntersections.Find(new ArcLengthPath(new([new ParametricSlidePath.LineSegment(new(-5,1),new(5,1))])), [concave]);
        Check(visits.Count == 1 && Math.Abs(visits[0].Enter - 3) < 1e-7 && Math.Abs(visits[0].Exit - 5) < 1e-7, "concave line clipping");
        var arcVisits = RegionIntersections.Find(new ArcLengthPath(new([new ParametricSlidePath.CircleSegment(new(0,1), 0, true)])), [concave]);
        Check(Math.Abs(arcVisits.Sum(v => v.Exit - v.Enter) - Math.PI * 1.5) < 1e-7, "concave arc clipping");
        Check(SlideManager.NativeVCalls == calls, "contour compiler consulted a native table");
        return new { assertions = checks, referenceTransforms = cases, targetedInputFrames = frames, freeNodePairs = freeCases,
            maxOuterArcTailErrorMsAt1000 = maxOuterArcTailError, nativeTableLookups = 0, elapsedMs = clock.Elapsed.TotalMilliseconds };
    }

    public static object FanTests()
    {
        int frames = 0, combinations = 0;
        var rows = SlideManager.Cases.Where(r => r.GetProperty("type").GetInt32() == 13).ToArray();
        for (int rotation = 0; rotation < 8; rotation++) for (int mirror = 0; mirror < 4; mirror++)
        {
            var areas = rows.Select(r => Native(r, rotation, mirror).Select(a => a.HitPoints.Select(p => (int)p).ToArray()).ToArray()).ToArray();
            var tables = areas.Select(a => (IReadOnlyList<JudgeStep>)a.Select(p => new JudgeStep(p, 0, 1, 0)).ToArray()).ToArray();
            var expected = new NativeFanOracle(areas); var actual = new FanReplay(tables);
            var random = new Random(170);
            for (int run = 0; run < 256; run++)
            {
                for (int lane = 0; lane < 3; lane++)
                {
                    int position = random.Next(areas[lane].Length + 1);
                    bool entered = random.Next(2) == 1;
                    int selected = position < areas[lane].Length ? random.Next(areas[lane][position].Length) : 0;
                    expected.ResetLane(lane, position, entered, selected); actual.ResetLane(lane, position, entered, selected);
                }
                for (int frame = 0; frame < 16; frame++)
                {
                    ulong mask = (ulong)random.NextInt64(1L << 34);
                    expected.Advance(mask); actual.Advance(mask); frames++;
                    for (int lane = 0; lane < 3; lane++) Check(expected.State(lane) == actual.State(lane), "fan lane state");
                    Check(expected.Ready == actual.ReadyToJudge && expected.LateGood == actual.EligibleForLateGood, "fan completion/late-good eligibility");
                }
                combinations++;
            }
        }
        var nativeAreas = rows.Select(r => Native(r).Select(a => a.HitPoints.Select(p => (int)p).ToArray()).ToArray()).ToArray();
        var fixture = nativeAreas.Select(a => (IReadOnlyList<JudgeStep>)a.Select(p => new JudgeStep(p, 0, 1, 0)).ToArray()).ToArray();
        var replay = new FanReplay(fixture);
        replay.ResetLane(1, fixture[1].Count - 2, false);
        replay.Advance(1UL << fixture[1][^2].Panels[0]);
        Check(replay.State(1).Index == fixture[1].Count, "middle fan tail should accept penultimate sensor at the final step");
        Check(!replay.ReadyToJudge, "middle lane alone cannot finish a fan");
        for (int lane = 0; lane < 3; lane++) replay.ResetLane(lane, fixture[lane].Count - 1, false);
        ulong finalMask = fixture.Aggregate(0UL, (m, lane) => m | lane[^1].Mask);
        replay.Advance(finalMask);
        Check(replay.AllLanesComplete && !replay.ReadyToJudge, "fan completion is observed on the following update");
        replay.Advance(0); Check(replay.ReadyToJudge, "all three fan lanes must complete");
        replay.Reset();
        replay.ResetLane(0, fixture[0].Count - 1, false);
        replay.Advance(fixture[0][^1].Mask);
        Check(replay.State(0).Selected == fixture[0][^1].Panels.Length - 1, "fan picks the last simultaneously pressed alternative");
        var custom = fixture.ToArray();
        custom[1] = new[] { new JudgeStep([0],0,1,0), new JudgeStep([16,9],1,2,0),
            new JudgeStep([12],2,3,0), new JudgeStep([4],3,4,0) };
        var customFan = new FanReplay(custom);
        customFan.ResetLane(1, 1, true, 1);
        customFan.Advance((1UL << 9) | (1UL << 12));
        Check(customFan.State(1).Index == 4, "custom fan predecessor must not carry an out-of-range sub-index into the shared tail");
        for (int i = 0; i < 1000; i++) { replay.Reset(); replay.Advance(1); }
        long bytes = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++) { replay.Reset(); replay.Advance(1); _ = replay.EligibleForLateGood; _ = replay.AllLanesComplete; }
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Check(bytes == 0, "fan replay frame allocations");
        return new { randomizedInitialStates = combinations, replayFrames = frames, hotPathAllocatedBytes = bytes,
            scope = "native fixture input policy only; generated fan geometry and full scoring are separate" };
    }
}
