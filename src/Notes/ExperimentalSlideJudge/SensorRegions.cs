using SlideJudgeProfile = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideJudgeProfile;
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SinmaiAlpha.Notes.Libs;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge;

public sealed record Disc(Complex Center, double Radius);
public readonly record struct GeometryBounds(double MinX, double MinY, double MaxX, double MaxY)
{
    public bool Overlaps(GeometryBounds other) => MaxX >= other.MinX - 1e-8 && MinX <= other.MaxX + 1e-8 &&
        MaxY >= other.MinY - 1e-8 && MinY <= other.MaxY + 1e-8;
    public static GeometryBounds Circle(Complex center, double radius) => new(center.Real - radius, center.Imaginary - radius, center.Real + radius, center.Imaginary + radius);
    public static GeometryBounds Points(IEnumerable<Complex> points)
    {
        double minX = double.PositiveInfinity, minY = minX, maxX = double.NegativeInfinity, maxY = maxX;
        foreach (var p in points) { minX = Math.Min(minX,p.Real); minY = Math.Min(minY,p.Imaginary); maxX = Math.Max(maxX,p.Real); maxY = Math.Max(maxY,p.Imaginary); }
        return new(minX,minY,maxX,maxY);
    }
}
public sealed class RegionPolygon
{
    public IReadOnlyList<Complex> Points { get; }
    public GeometryBounds Bounds { get; }
    public RegionPolygon(IReadOnlyList<Complex> points)
    {
        Points = Array.AsReadOnly(points.ToArray());
        Bounds = GeometryBounds.Points(Points);
    }
}
public sealed record SensorRegion(int Panel, IReadOnlyList<Disc> Discs,
    IReadOnlyList<RegionPolygon>? Polygons = null);
public sealed record SensorVisit(int Panel, double Enter, double Exit);

public static class RegionModels
{
    public const string Seeds = "legacy-seeds";
    public const string Diameter = "diameter-calibrated";
    public const string Contours = "contours-and-choices";

    public static IReadOnlyList<SensorRegion> Create(string model, SlideJudgeProfile profile)
    {
        if (model == Contours) return ContourRegions.Create(profile);
        if (model is not (Seeds or Diameter)) throw new ArgumentException("Unknown region model.");
        // Candidate only, NOT extracted physical sensor outlines. These three
        // larger radii reproduce the radial boundaries of native 1-5. A is
        // still centred at 440; B's centre follows the midpoint of its native
        // entry/exit. No other shape's table is read by the interval engine.
        double ar = model == Seeds ? 80 : 116.42124938964844;
        double br = model == Seeds ? 45 : 64.49588775634765;
        double bc = model == Seeds ? 210 : 215.80862426757812;
        double cr = model == Seeds ? 55 : 109.31514739990235;
        var regions = new List<SensorRegion>();
        for (int i = 0; i < 8; i++)
        {
            double a = Math.PI * (3d / 8 - i / 4d);
            regions.Add(new(i, [new(Complex.FromPolarCoordinates(440, a), ar)]));
            regions.Add(new(8 + i, [new(Complex.FromPolarCoordinates(bc, a), br)]));
            if (profile != SlideJudgeProfile.ExtendedTouch) continue;
            double de = Math.PI * (.5 - i / 4d);
            // D/E remain explicit experimental regions using the current
            // geometry's anchors. They are not calibrated by an ABC fixture.
            regions.Add(new(18 + i, [new(Complex.FromPolarCoordinates(430.5, de), 45),
                new(Complex.FromPolarCoordinates(480, de), 58)]));
            regions.Add(new(26 + i, [new(Complex.FromPolarCoordinates(315, de), 45)]));
        }
        regions.Add(new(16, [new(Complex.Zero, cr)]));
        return regions;
    }

    public static string Name(int panel) => panel switch
    {
        < 8 => "A" + (panel + 1), < 16 => "B" + (panel - 7),
        16 or 17 => "C", < 26 => "D" + (panel - 17), _ => "E" + (panel - 25)
    };
}

public static class RegionIntersections
{
    public static List<SensorVisit> Find(ArcLengthPath path, IReadOnlyList<SensorRegion> regions)
    {
        if (regions.Select(r => r.Panel).Distinct().Count() != regions.Count)
            throw new ArgumentException("Use one region with multiple discs per physical sensor.");
        var result = new List<SensorVisit>();
        foreach (var region in regions)
        {
            if (region.Panel is < 0 or > 33 || region.Panel == 17 || region.Discs.Count == 0 && (region.Polygons?.Count ?? 0) == 0)
                throw new ArgumentException("Invalid physical sensor; C1/C2 normalize to C.");
            var intervals = new List<(double Enter, double Exit)>();
            foreach (var disc in region.Discs)
            {
                if (!ArcLengthPath.Finite(disc.Center) || !Scalar.Finite(disc.Radius) || disc.Radius <= 0)
                    throw new ArgumentException("Invalid region disc.");
                foreach (var piece in path.Pieces)
                {
                    foreach (var (lo, hi) in Clip(piece, disc))
                        intervals.Add((piece.Offset + lo * piece.Length, piece.Offset + hi * piece.Length));
                }
            }
            foreach (var polygon in region.Polygons ?? [])
            {
                if (polygon.Points.Count < 3 || polygon.Points.Any(p => !ArcLengthPath.Finite(p)))
                    throw new ArgumentException("Invalid region polygon.");
                foreach (var piece in path.Pieces)
                    foreach (var (lo, hi) in PolygonIntersections.Clip(piece, polygon))
                        intervals.Add((piece.Offset + lo * piece.Length, piece.Offset + hi * piece.Length));
            }
            // Union only adjacent intervals of the SAME physical sensor. A
            // loop leaving and returning to A1 remains two distinct visits.
            intervals.Sort((a, b) => a.Enter.CompareTo(b.Enter));
            foreach (var interval in intervals)
            {
                if (result.Count > 0 && result[result.Count - 1].Panel == region.Panel && interval.Enter <= result[result.Count - 1].Exit + 1e-7)
                    result[result.Count - 1] = result[result.Count - 1] with { Exit = Math.Max(result[result.Count - 1].Exit, interval.Exit) };
                else result.Add(new(region.Panel, interval.Enter, interval.Exit));
            }
        }
        return result.OrderBy(x => x.Enter).ThenBy(x => x.Exit).ThenBy(x => x.Panel).ToList();
    }

    public static IEnumerable<(double Lo, double Hi)> Clip(TracePiece p, Disc d)
    {
        if (!p.Bounds.Overlaps(GeometryBounds.Circle(d.Center, d.Radius))) yield break;
        if (p.Center is not { } center)
        {
            var v = p.End - p.Start;
            double a = ArcLengthPath.Dot(v, v);
            if (a <= 1e-20) yield break;
            double mid = -ArcLengthPath.Dot(p.Start - d.Center, v) / a;
            double perpendicular2 = ArcLengthPath.Dot(p.Start - d.Center, p.Start - d.Center) - mid * mid * a;
            double half2 = (d.Radius * d.Radius - perpendicular2) / a;
            if (half2 <= 1e-14) yield break; // tangency is not a finite visit
            double half = Math.Sqrt(half2);
            double lo = Math.Max(0, mid - half), hi = Math.Min(1, mid + half);
            if (hi - lo > 1e-12) yield return (lo, hi);
            yield break;
        }

        // Circle/circle intersection, with angles unwrapped over any number of
        // turns. A 720-degree path must not collapse to a single revolution.
        var delta = d.Center - center;
        double distance = delta.Magnitude;
        if (distance < 1e-12)
        {
            if (p.Radius <= d.Radius) yield return (0, 1);
            yield break;
        }
        double cosine = (p.Radius * p.Radius + distance * distance - d.Radius * d.Radius) /
                        (2 * p.Radius * distance);
        if (cosine >= 1) yield break;
        if (cosine <= -1) { yield return (0, 1); yield break; }
        double alpha = Math.Acos(cosine), phi = delta.Phase;
        double from = Math.Min(p.StartAngle, p.StartAngle + p.Sweep);
        double to = Math.Max(p.StartAngle, p.StartAngle + p.Sweep);
        double first = Math.Ceiling((from - phi - alpha) / Scalar.Tau);
        double last = Math.Floor((to - phi + alpha) / Scalar.Tau);
        if (!Scalar.Finite(first + last) || Math.Abs(first) > 1e8 || Math.Abs(last) > 1e8 || last - first > 100000)
            throw new InvalidOperationException("Arc intersection budget exceeded.");
        for (double k = first; k <= last; k++)
        {
            double lo = Math.Max(from, phi - alpha + k * Scalar.Tau);
            double hi = Math.Min(to, phi + alpha + k * Scalar.Tau);
            if (hi - lo <= 1e-12) continue;
            double a = (lo - p.StartAngle) / p.Sweep, b = (hi - p.StartAngle) / p.Sweep;
            yield return (Scalar.Clamp(Math.Min(a, b), 0, 1), Scalar.Clamp(Math.Max(a, b), 0, 1));
        }
    }
}
