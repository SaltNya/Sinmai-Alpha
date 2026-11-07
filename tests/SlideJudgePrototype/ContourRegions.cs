using SlideJudgeProfile = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideJudgeProfile;
using System.Numerics;
using SinmaiAlpha.Notes.Libs;

namespace SlideJudgePrototype;

// An experimental effective judge contour, not a physical touch-panel outline.
// Calibrate LOCAL boundaries once from elementary straight/ring constraints;
// every arbitrary path sees the same rotated regions. No path-code/type lookup.
public static class ContourRegions
{
    // (radius, half-angle in degrees), symmetric about a sensor's radial axis.
    // A: radial inner limit, 135° chord exit, 90° chord middle/exit,
    //    outer arc half-width (113.344421 / 243.617674 * 45°).
    static readonly (double R, double Degrees)[] A = [
        (323.57875061035156, 0), (338.6130211382679, 10.3520705710377),
        (348.7334807248504, 13.277699748641083), (398.7144748042619, 13.349409784033995),
        (480, 20.936489874878315), (560, 20.936489874878315) ];
    // B: diameter entry/exit and the 135° chord's two B boundary crossings.
    static readonly (double R, double Degrees)[] B = [
        (151.31273651123047, 0), (184.23455329455933, 18.08575053062715),
        (239.35595485764304, 17.376670671341728), (280.3045120239258, 0) ];

    public static IReadOnlyList<SensorRegion> Create(SlideJudgeProfile profile)
    {
        var regions = RegionModels.Create(RegionModels.Diameter, profile).Where(r => r.Panel >= 16).ToList();
        for (int key = 0; key < 8; key++)
        {
            double angle = Math.PI * (3d / 8 - key / 4d);
            regions.Add(new(key, [], [Make(A, angle)]));
            regions.Add(new(key + 8, [], [Make(B, angle)]));
        }
        return regions;
    }

    internal static RegionPolygon Make((double R, double Degrees)[] profile, double angle)
    {
        var points = new List<Complex>();
        foreach (var (r, a) in profile) points.Add(Complex.FromPolarCoordinates(r, angle - a * Math.PI / 180));
        // Tessellated outer cap. The judge-circle intersections with the radial
        // side edges remain analytic, independent of this cap's tessellation.
        var outer = profile[^1];
        for (int i = 1; i < 16; i++)
            points.Add(Complex.FromPolarCoordinates(outer.R, angle + outer.Degrees * (2d * i / 16 - 1) * Math.PI / 180));
        foreach (var (r, a) in profile.Reverse()) points.Add(Complex.FromPolarCoordinates(r, angle + a * Math.PI / 180));
        return new(points);
    }
}

public static class PolygonIntersections
{
    static double Cross(Complex a, Complex b) => a.Real * b.Imaginary - a.Imaginary * b.Real;

    // Even/odd winding supports concave contours. On an edge counts as inside;
    // a point tangency creates no interval because its parameter width is zero.
    static bool Contains(RegionPolygon polygon, Complex p)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Points.Count - 1; i < polygon.Points.Count; j = i++)
        {
            var a = polygon.Points[j]; var b = polygon.Points[i];
            var v = b - a;
            if (v.Magnitude > 1e-12 && Math.Abs(Cross(p - a, v)) <= 1e-8 * v.Magnitude &&
                ArcLengthPath.Dot(p - a, p - b) <= 1e-8) return true;
            if ((a.Imaginary > p.Imaginary) != (b.Imaginary > p.Imaginary) &&
                p.Real < (b.Real - a.Real) * (p.Imaginary - a.Imaginary) / (b.Imaginary - a.Imaginary) + a.Real) inside = !inside;
        }
        return inside;
    }

    public static IEnumerable<(double Lo, double Hi)> Clip(TracePiece path, RegionPolygon polygon)
    {
        if (!path.Bounds.Overlaps(polygon.Bounds)) yield break;
        var roots = new List<double> { 0, 1 };
        for (int i = 0; i < polygon.Points.Count; i++)
        {
            var a = polygon.Points[i]; var b = polygon.Points[(i + 1) % polygon.Points.Count];
            var edge = b - a;
            if (path.Center is not { } center)
            {
                var v = path.End - path.Start;
                double cross = Cross(v, edge);
                if (Math.Abs(cross) <= 1e-12)
                {
                    // Collinear overlapping edges contribute their endpoints.
                    double v2 = ArcLengthPath.Dot(v, v);
                    if (v2 > 1e-12 && Math.Abs(Cross(a - path.Start, v)) <= 1e-8 * v.Magnitude)
                    {
                        Add(ArcLengthPath.Dot(a - path.Start, v) / v2);
                        Add(ArcLengthPath.Dot(b - path.Start, v) / v2);
                    }
                    continue;
                }
                double t = Cross(a - path.Start, edge) / cross;
                double u = Cross(a - path.Start, v) / cross;
                if (u >= -1e-10 && u <= 1 + 1e-10) Add(t);
            }
            else
            {
                double e2 = ArcLengthPath.Dot(edge, edge);
                if (e2 <= 1e-12) continue;
                double mid = -ArcLengthPath.Dot(a - center, edge) / e2;
                double half2 = (path.Radius * path.Radius - ArcLengthPath.Dot(a - center, a - center)) / e2 + mid * mid;
                if (half2 < 0) continue;
                foreach (double u in new[] { mid - Math.Sqrt(half2), mid + Math.Sqrt(half2) })
                {
                    if (u < -1e-10 || u > 1 + 1e-10) continue;
                    double angle = (a + edge * u - center).Phase;
                    double from = Math.Min(path.StartAngle, path.StartAngle + path.Sweep);
                    double to = Math.Max(path.StartAngle, path.StartAngle + path.Sweep);
                    double first = Math.Ceiling((from - angle) / Math.Tau - 1e-10);
                    double last = Math.Floor((to - angle) / Math.Tau + 1e-10);
                    if (!double.IsFinite(first + last) || Math.Abs(first) > 1e8 || Math.Abs(last) > 1e8 || last - first > 100000)
                        throw new InvalidOperationException("Polygon arc intersection budget exceeded.");
                    for (double turn = first; turn <= last; turn++) Add((angle + turn * Math.Tau - path.StartAngle) / path.Sweep);
                }
            }
        }
        roots.Sort();
        for (int i = 1; i < roots.Count; i++)
        {
            double lo = roots[i - 1], hi = roots[i];
            if (hi - lo > 1e-12 && Contains(polygon, path.At((lo + hi) / 2))) yield return (lo, hi);
        }
        void Add(double t) { if (t >= -1e-10 && t <= 1 + 1e-10) roots.Add(Math.Clamp(t, 0, 1)); }
    }
}

public static class CorridorChoices
{
    static readonly SensorRegion[] GapRegions = Enumerable.Range(0, 8).Select(key => new SensorRegion(key, [], [ContourRegions.Make(
        [(280.3045120239258, 13.277699748641083), (355, 13.277699748641083)], Math.PI * (3d / 8 - key / 4d))])).ToArray();

    public static List<ChoiceWindow> FindGapCrossings(ArcLengthPath path, IReadOnlyList<SensorVisit> physical)
    {
        var result = new List<ChoiceWindow>();
        for (int key = 0; key < 8; key++)
        {
            var region = GapRegions[key];
            foreach (var crossing in RegionIntersections.Find(path, [region]))
            {
                double mid = (crossing.Enter + crossing.Exit) / 2;
                var point = path.AtDistance(mid);
                double delta = Math.Min(.01, (crossing.Exit - crossing.Enter) / 4);
                var tangent = path.AtDistance(mid + delta) - path.AtDistance(mid - delta);
                if (tangent.Magnitude <= 1e-10 || point.Magnitude <= 1e-10) continue;
                // The A/B gap is optional only when crossed along the ring.
                // A radial A->B motion must not acquire an extra A/B step.
                double radialFraction = Math.Abs(ArcLengthPath.Dot(tangent, point)) / (tangent.Magnitude * point.Magnitude);
                if (radialFraction > .5 || physical.Any(v => Math.Min(v.Exit, crossing.Exit) - Math.Max(v.Enter, crossing.Enter) > 1e-7)) continue;
                result.Add(new([key, key + 8], crossing.Enter, crossing.Exit, "tangential-A/B-gap"));
            }
        }
        return result;
    }

    public static int[] Expand(ArcLengthPath path, int[] panels, double enter, double exit)
    {
        // Representative point is invariant under how the author split lines.
        // Choices are a separate input allowance, not extra physical visits.
        var point = path.AtDistance((enter + exit) / 2);
        var result = new HashSet<int>(panels);
        double radius = point.Magnitude;
        foreach (int panel in panels)
        {
            if (panel < 8 && Math.Abs(Math.Abs(radius - 440) - Math.Abs(radius - 215.80862426757812)) <= 55)
                result.Add(panel + 8);
            if (panel == 16 && radius >= .8 * 109.31514739990235)
                foreach (var key in ClosestKeys(point)) result.Add(8 + key);
            if (panel is >= 8 and < 16)
            {
                double angle = Math.IEEERemainder(point.Phase - Math.PI * (3d / 8 - (panel - 8) / 4d), Math.Tau);
                // Near an azimuth bisector, permit the neighbouring B sensor.
                if (Math.Abs(angle) >= 13 * Math.PI / 180 && Math.Abs(angle) <= Math.PI / 4)
                    result.Add(8 + ((panel - 8 + (angle > 0 ? -1 : 1) + 8) & 7));
            }
        }
        var ordered = result.Order().ToArray();
        if (ordered.Length == 2 && ordered.All(p => p is >= 8 and < 16))
        {
            double delta = Math.Min(.01, (exit - enter) / 4);
            double mid = (enter + exit) / 2;
            var tangent = path.AtDistance(mid + delta) - path.AtDistance(mid - delta);
            // For two neighbouring B sensors, the one encountered earlier
            // along travel has input priority. Numeric panel-ID sorting would
            // change which held sensor must be released after a mirror.
            double Progress(int panel) => ArcLengthPath.Dot(
                Complex.FromPolarCoordinates(215.80862426757812, Math.PI * (3d / 8 - (panel - 8) / 4d)) - point, tangent);
            if (Progress(ordered[0]) > Progress(ordered[1]) + 1e-9) Array.Reverse(ordered);
        }
        return ordered;
    }
    static IEnumerable<int> ClosestKeys(Complex point)
    {
        var angles = Enumerable.Range(0, 8).Select(key =>
            Math.Abs(Math.IEEERemainder(point.Phase - Math.PI * (3d / 8 - key / 4d), Math.Tau))).ToArray();
        double closest = angles.Min();
        for (int key = 0; key < 8; key++) if (angles[key] <= closest + 1e-9) yield return key;
    }
}
