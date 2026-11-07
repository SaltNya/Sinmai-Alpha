using SlideCodeParser = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideCodeParser;
using SlideJudgeProfile = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideJudgeProfile;
using SlideDefinition = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideDefinition;
using System.Numerics;
using SinmaiAlpha.Notes.Libs;
using Segment = SinmaiAlpha.Notes.Libs.ParametricSlidePath.PathSegment;

namespace SlideJudgePrototype;

static class PrototypeTests
{
    static int checks;
    static void Check(bool condition, string message)
    { checks++; if (!condition) throw new Exception(message); }
    static void Near(double actual, double expected, double epsilon, string message) =>
        Check(double.IsFinite(actual) && Math.Abs(actual - expected) <= epsilon,
            $"{message}: actual={actual:R}, expected={expected:R}, tolerance={epsilon}");
    static ParametricSlidePath Line(Complex from, Complex to) => new([new ParametricSlidePath.LineSegment(from, to)]);
    static void Reject(Action action, string message)
    {
        bool rejected = false;
        try { action(); } catch (ArgumentException) { rejected = true; }
        catch (InvalidOperationException) { rejected = true; }
        Check(rejected, message);
    }
    static PrototypeTable Compile(ParametricSlidePath path, string model = RegionModels.Diameter,
        SlideJudgeProfile profile = SlideJudgeProfile.StandardAbc) => IntervalJudge.Build(path, profile, model);

    public static object Run()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        int nativeCalls = Manager.SlideManager.NativeVCalls;
        var line = new ArcLengthPath(Line(new(-10, 0), new(10, 0)));
        var circle = new SensorRegion(16, [new(Complex.Zero, 2)]);
        var visit = RegionIntersections.Find(line, [circle]).Single();
        Near(visit.Enter, 8, 1e-10, "analytic line entry");
        Near(visit.Exit, 12, 1e-10, "analytic line exit");
        Check(RegionIntersections.Find(new ArcLengthPath(Line(new(-10, 2), new(10, 2))), [circle]).Count == 0,
            "tangent point must not create a required region");
        Check(RegionIntersections.Find(new ArcLengthPath(Line(new(-10, 1.999999), new(10, 1.999999))), [circle]).Count == 1,
            "narrow crossing lost between sample points");
        var union = RegionIntersections.Find(line, [new(16, [new(Complex.Zero, 2), new(new(1, 0), 2)])]).Single();
        Near(union.Enter, 8, 1e-10, "same-sensor union entry"); Near(union.Exit, 13, 1e-10, "same-sensor union exit");

        foreach (var direction in new[] { -1, 1 })
        {
            var arc = new ArcLengthPath(new([new ParametricSlidePath.ArcSegment(new(Complex.Zero, 10), 0, direction * Math.Tau * 2)]));
            var hits = RegionIntersections.Find(arc, [new(0, [new(new(10, 0), 2)])]);
            double half = 2 * Math.Asin(.1) * 10;
            Check(hits.Count == 3, "two-turn loop must retain initial, middle and final visits");
            Near(hits[0].Exit, half, 1e-9, "analytic arc exit");
            Near(hits[1].Enter, Math.Tau * 10 - half, 1e-9, "analytic arc second entry");
            Near(hits[2].Exit, Math.Tau * 20, 1e-9, "analytic arc final exit");
            var table = IntervalJudge.Build(arc, [new(0, [new(new(10, 0), 2)])]);
            var replay = new PrototypeReplay(table.Steps);
            replay.Step(1); replay.Step(1);
            Check(!replay.Complete, "holding one sensor must not consume repeated visits");
            replay.Step(0); replay.Step(1); replay.Step(0); replay.Step(1);
            Check(replay.Complete, "release/re-enter should consume the repeated visits");
        }

        var coincident = IntervalJudge.Build(line, [new(0, [new(Complex.Zero, 2)]), new(8, [new(Complex.Zero, 2)])]);
        Check(coincident.Steps.Count == 1 && coincident.Steps[0].Panels.SequenceEqual(new[] { 0, 8 }), "coincident A/B alternatives");
        var overlap = IntervalJudge.Build(line, [new(0, [new(Complex.Zero, 4)]), new(8, [new(new(3, 0), 4)])]);
        Check(overlap.Steps.Count == 2, "partial overlap must not become an arbitrary any-of region");
        Conservation(overlap);

        var spiralSource = new ParametricSlidePath.PolarArcSegment(MaiGeometry.PointGroupA(1), MaiGeometry.PointGroupB(5), '>');
        var spiralPath = new ParametricSlidePath([spiralSource]);
        var measured = new ArcLengthPath(spiralPath);
        var fine = new ArcLengthPath(spiralPath, new(Flatness: .0005, LengthDefect: .0005, MaxChord: 4));
        // Independent composite Simpson integration of the analytic speed.
        double Speed(double t)
        {
            double dr = (spiralSource.EndRadius - spiralSource.StartRadius) * 6 * t * (1 - t);
            double r = spiralSource.StartRadius + (spiralSource.EndRadius - spiralSource.StartRadius) * t * t * (3 - 2 * t);
            return Math.Sqrt(dr * dr + r * r * spiralSource.DeltaAngle * spiralSource.DeltaAngle);
        }
        const int samples = 20000;
        double integral = Speed(0) + Speed(1);
        for (int i = 1; i < samples; i++) integral += (i % 2 == 0 ? 2 : 4) * Speed(i / (double)samples);
        integral /= 3 * samples;
        Near(measured.Length, integral, .001, "variable-radius arc length vs independent integration");
        Near(measured.Length, fine.Length, .001, "curve refinement convergence");
        double parameterBias = (spiralSource.GetPointAt(.5) - measured.AtDistance(measured.Length / 2)).Magnitude;
        Check(parameterBias > 10, "test must expose parameter midpoint != length midpoint");
        var normalTable = Compile(spiralPath, profile: SlideJudgeProfile.ExtendedTouch);
        var fineTable = IntervalJudge.Build(fine, RegionModels.Create(RegionModels.Diameter, SlideJudgeProfile.ExtendedTouch));
        Check(normalTable.Queue == fineTable.Queue, "curve refinement changed the sensor sequence");
        for (int i = 0; i < normalTable.Steps.Count; i++)
        {
            Near(normalTable.Steps[i].Enter, fineTable.Steps[i].Enter, .005, "curve entry convergence");
            Near(normalTable.Steps[i].Exit, fineTable.Steps[i].Exit, .005, "curve exit convergence");
        }

        int transforms = 0;
        var cases = Manager.SlideManager.Cases.Where(c => c.GetProperty("type").GetInt32() != 13)
            .Select(c => c.GetProperty("code").GetString()!).Distinct()
            .Concat(new[] { "1P0A5K7", "1Q99K5", "1A3B6A8K4", "D8D2K4", "E8E2K4", "1K2" });
        foreach (var code in cases)
        {
            var definition = SlideDefinition.Parse(code); var path = definition.Paths[0];
            var baseline = Compile(path, profile: definition.JudgeProfile); Conservation(baseline);
            for (int rotation = 0; rotation < 8; rotation++) for (int mirror = 0; mirror < 4; mirror++)
            {
                var transformed = Compile(Transform(path, rotation, mirror), profile: definition.JudgeProfile);
                Conservation(transformed);
                Check(baseline.Steps.Count == transformed.Steps.Count, "transform count " + code);
                for (int i = 0; i < baseline.Steps.Count; i++)
                {
                    var expected = baseline.Steps[i].Panels.Select(p => TransformPanel(p, rotation, mirror)).Order();
                    Check(expected.SequenceEqual(transformed.Steps[i].Panels.Order()), "transform panels " + code);
                    Near(transformed.Steps[i].Enter, baseline.Steps[i].Enter, 1e-6, "transform entry " + code);
                    Near(transformed.Steps[i].Exit, baseline.Steps[i].Exit, 1e-6, "transform exit " + code);
                    Check(transformed.Steps[i].RequireEntry == baseline.Steps[i].RequireEntry, "transform gate " + code);
                }
                transforms++;
            }
        }

        int nodePairs = 0;
        var nodes = Enumerable.Range(1, 8).SelectMany(i => new[] { MaiGeometry.PointGroupA(i), MaiGeometry.PointGroupB(i),
            MaiGeometry.PointGroupD(i), MaiGeometry.PointGroupE(i) }).Append(Complex.Zero).ToArray();
        foreach (var from in nodes) foreach (var to in nodes) foreach (var profile in Enum.GetValues<SlideJudgeProfile>())
        {
            var t = Compile(Line(from, to), profile: profile); Conservation(t);
            Check(t.Visits.All(v => v.Panel < (profile == SlideJudgeProfile.StandardAbc ? 17 : 34)), "D/E profile leakage");
            if (from == to) Check(!t.CanExportDistances, "zero length must not export a denominator");
            nodePairs++;
        }
        var outer = SlideCodeParser.Parse("1Q9K3");
        Check(Compile(outer, profile: SlideJudgeProfile.ExtendedTouch).Visits.Any(v => v.Panel is >= 18 and < 26), "D region should be active for extended outer arcs");
        Check(!Compile(outer).Visits.Any(v => v.Panel >= 18), "ordinary SC should not use D/E");
        var ePath = Line(MaiGeometry.PointGroupE(8), MaiGeometry.PointGroupE(2));
        var eTable = Compile(ePath, profile: SlideJudgeProfile.ExtendedTouch);
        Check(eTable.Visits.Any(v => v.Panel == 33) && eTable.Visits.Any(v => v.Panel == 27), "E8/E2 do not alias");

        foreach (string code in new[] { "TG1:1-4", "TG1:1>B2>C", "TG1:E8^E2", "DG1:1d-5" })
        {
            var d = SlideDefinition.Parse(code);
            var table = Compile(d.Paths[0], profile: d.JudgeProfile); Conservation(table);
            Check(table.Steps.Count > 0, "geometry adapter " + code);
        }
        foreach (string code in new[] { "DF1:1w5", "DF1:1dw5", "DF1:1w5d" })
        {
            var d = SlideDefinition.Parse(code);
            Check(d.Paths.Count == 3, "fan lane count");
            foreach (var lane in d.Paths) Conservation(Compile(lane, profile: d.JudgeProfile));
        }

        var v = Compile(SlideCodeParser.Parse("1A3K5"));
        Check(v.Steps.Count == 5 && v.Steps[1].RequireEntry && v.Steps[3].RequireEntry, "outer V short-leg checkpoints");
        var splitV = new ParametricSlidePath(SlideCodeParser.Parse("1A3K5").Segments
            .Cast<ParametricSlidePath.LineSegment>().SelectMany(s => new Segment[] {
                new ParametricSlidePath.LineSegment(s.StartPoint, s.GetPointAt(.5)),
                new ParametricSlidePath.LineSegment(s.GetPointAt(.5), s.EndPoint) }));
        var splitTable = Compile(splitV);
        Check(v.Queue == splitTable.Queue, "collinear segmentation changed queue");
        for (int i = 0; i < v.Steps.Count; i++)
        {
            Near(splitTable.Steps[i].Enter, v.Steps[i].Enter, 1e-7, "split leg entry");
            Near(splitTable.Steps[i].Exit, v.Steps[i].Exit, 1e-7, "split leg exit");
            Check(splitTable.Steps[i].RequireEntry == v.Steps[i].RequireEntry, "split leg skip gate");
        }
        var smallV = Compile(SlideCodeParser.Parse("1CK3"));
        Check(smallV.Steps.All(s => !s.RequireEntry), "centre v must not inherit outer V gates");
        var shortLine = Compile(Line(MaiGeometry.PointGroupA(1), MaiGeometry.PointGroupA(1) + new Complex(.001, 0)));
        Conservation(shortLine); Check(shortLine.CanExportDistances, "very short finite tail");
        var missing = Compile(Line(new(2000, 0), new(2001, 0)));
        Check(missing.Steps.Count == 0 && !missing.CanExportDistances, "no synthetic C fallback outside regions");
        Reject(() => new ArcLengthPath(Line(new(double.NaN, 0), Complex.Zero)), "reject NaN");
        Reject(() => new ArcLengthPath(new([new ParametricSlidePath.LineSegment(0, 1), new ParametricSlidePath.LineSegment(2, 3)])), "reject discontinuity");
        Reject(() => new ArcLengthPath(spiralPath, new(MaxPieces: 1)), "bounded flattening budget");
        Reject(() => new ArcLengthPath(spiralPath, new(Flatness: double.NaN)), "reject invalid tolerance");

        var replayTest = new PrototypeReplay(v.Steps);
        for (int i = 0; i < 1000; i++) { replayTest.Reset(); replayTest.Step(1); replayTest.Step(0); }
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++) { replayTest.Reset(); replayTest.Step(1); replayTest.Step(0); }
        allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check(allocated == 0, "replay hot path allocates: " + allocated);
        Check(Manager.SlideManager.NativeVCalls == nativeCalls, "prototype must not consult native judge tables");
        return new { assertions = checks, rotationMirrorCases = transforms, nodePairCases = nodePairs,
            curveLengthError = Math.Abs(measured.Length - integral), parameterMidpointBias = parameterBias,
            numericPieces = measured.Pieces.Count, replayFrames = 200000, replayAllocatedBytes = allocated, nativeTableLookups = 0,
            elapsedMs = watch.Elapsed.TotalMilliseconds };
    }

    static void Conservation(PrototypeTable table)
    {
        Check(table.Steps.All(s => double.IsFinite(s.Enter + s.Exit + s.Release) && s.Enter >= -1e-7 &&
            s.Exit >= s.Enter && s.Release >= 0 && s.Exit <= table.Path.Length + 1e-7), "invalid interval");
        Near(table.Lead + table.Steps.Sum(s => s.Push + s.Release), table.Path.Length, 1e-6, "distance conservation");
    }

    internal static ParametricSlidePath Transform(ParametricSlidePath path, int rotate, int mirror)
    {
        Complex TransformPoint(Complex p)
        {
            p *= Complex.FromPolarCoordinates(1, -rotate * Math.PI / 4);
            return mirror switch { 1 => -Complex.Conjugate(p), 2 => Complex.Conjugate(p), 3 => -p, _ => p };
        }
        double flip = mirror is 1 or 2 ? -1 : 1;
        Segment TransformSegment(Segment s) => s switch
        {
            ParametricSlidePath.LineSegment l => new ParametricSlidePath.LineSegment(TransformPoint(l.StartPoint), TransformPoint(l.EndPoint)),
            ParametricSlidePath.ArcSegment a => new ParametricSlidePath.ArcSegment(new(TransformPoint(a.Circle.Center), a.Circle.Radius),
                (TransformPoint(a.GetPointAt(0)) - TransformPoint(a.Circle.Center)).Phase,
                (TransformPoint(a.GetPointAt(0)) - TransformPoint(a.Circle.Center)).Phase + flip * (a.EndAngle - a.StartAngle)),
            ParametricSlidePath.CircleSegment c => new ParametricSlidePath.CircleSegment(new(TransformPoint(c.Circle.Center), c.Circle.Radius),
                (TransformPoint(c.GetPointAt(0)) - TransformPoint(c.Circle.Center)).Phase, c.IsCcw == (flip > 0)),
            _ => throw new InvalidOperationException("Transform fixture needs an exact primitive.")
        };
        return new(path.Segments.Select(TransformSegment));
    }
    internal static int TransformPanel(int panel, int rotation, int mirror)
    {
        if (panel is 16 or 17) return 16;
        int ring = panel < 8 ? 0 : panel < 16 ? 8 : panel < 26 ? 18 : 26;
        int key = (panel - ring + rotation) & 7;
        key = ring >= 18 ? mirror switch { 1 => -key & 7, 2 => (4 - key) & 7, 3 => (key + 4) & 7, _ => key }
            : mirror switch { 1 => 7 - key, 2 => (3 - key) & 7, 3 => (key + 4) & 7, _ => key };
        return ring + key;
    }
}
