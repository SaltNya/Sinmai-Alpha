#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using SinmaiAlpha.Notes.Libs;
using Segment = SinmaiAlpha.Notes.Libs.ParametricSlidePath.PathSegment;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge;

public sealed record GeometryOptions(
    double Flatness = .002,
    double LengthDefect = .002,
    double MaxChord = 8,
    int MaxPieces = 131072,
    int MaxEvaluations = 1000000,
    int MaxDepth = 24);

// Distances use the 1080-coordinate canvas, not seconds. Exact primitives stay
// exact. Other segments are adaptively flattened; their accuracy is estimated,
// not a certified bound for arbitrary black-box GetPointAt implementations.
public sealed class TracePiece
{
    public Complex Start { get; }
    public Complex End { get; }
    public double Offset { get; }
    public double Length { get; }
    public Complex? Center { get; }
    public double Radius { get; }
    public double StartAngle { get; }
    public double Sweep { get; }
    public GeometryBounds Bounds { get; }
    public TracePiece(Complex start, Complex end, double offset, double length, Complex? center = null,
        double radius = 0, double startAngle = 0, double sweep = 0)
    {
        Start = start; End = end; Offset = offset; Length = length; Center = center;
        Radius = radius; StartAngle = startAngle; Sweep = sweep;
        Bounds = center is { } c ? GeometryBounds.Circle(c, radius) :
            new(Math.Min(start.Real,end.Real), Math.Min(start.Imaginary,end.Imaginary), Math.Max(start.Real,end.Real), Math.Max(start.Imaginary,end.Imaginary));
    }
    public Complex At(double fraction) => Center is { } c
        ? c + Complex.FromPolarCoordinates(Radius, StartAngle + Sweep * fraction)
        : Start + (End - Start) * fraction;
}

public sealed record PathJoin(double Distance, double Before, double After,
    Complex Position, double TurnDegrees, bool StraightLegs);

public sealed class ArcLengthPath
{
    public IReadOnlyList<TracePiece> Pieces { get; }
    public IReadOnlyList<PathJoin> Joins { get; }
    public Complex Start { get; }
    public Complex End { get; }
    public double Length { get; }
    public int ApproximateSegments { get; }
    public int Evaluations { get; }
    public double EstimatedLengthDefect { get; }

    public ArcLengthPath(ParametricSlidePath source, GeometryOptions? options = null)
    {
        var o = options ?? new();
        if (!(o.Flatness > 0 && o.LengthDefect > 0 && o.MaxChord > 0) ||
            !Scalar.Finite(o.Flatness + o.LengthDefect + o.MaxChord) ||
            o.MaxPieces < 1 || o.MaxEvaluations < 4 || o.MaxDepth < 1)
            throw new ArgumentException("Invalid geometry tolerances/budget.");
        var pieces = new List<TracePiece>();
        var joins = new List<PathJoin>();
        var ranges = new List<(double Start, double End, Segment Source)>();
        double length = 0, defect = 0;
        int evaluations = 0, approximate = 0;

        Complex Point(Segment segment, double t)
        {
            if (++evaluations > o.MaxEvaluations) throw new InvalidOperationException("Curve evaluation budget exceeded.");
            var p = segment.GetPointAt(t);
            if (!Finite(p)) throw new ArgumentException("Non-finite slide coordinate.");
            return p;
        }
        void Add(Complex a, Complex b, double size, Complex? center = null,
            double radius = 0, double angle = 0, double sweep = 0)
        {
            if (!Scalar.Finite(size) || size < 0) throw new ArgumentException("Invalid geometric length.");
            if (size <= 1e-10) return;
            if (pieces.Count >= o.MaxPieces) throw new InvalidOperationException("Curve piece budget exceeded.");
            pieces.Add(new(a, b, length, size, center, radius, angle, sweep));
            length += size;
        }
        void Flatten(Segment segment, double t0, double t1, Complex a, Complex b, int depth)
        {
            var q1 = Point(segment, (3 * t0 + t1) / 4);
            var mid = Point(segment, (t0 + t1) / 2);
            var q3 = Point(segment, (t0 + 3 * t1) / 4);
            double chord = (b - a).Magnitude;
            double polygon = (q1 - a).Magnitude + (mid - q1).Magnitude +
                             (q3 - mid).Magnitude + (b - q3).Magnitude;
            double error = Math.Max(DistanceToLineSegment(q1, a, b),
                Math.Max(DistanceToLineSegment(mid, a, b), DistanceToLineSegment(q3, a, b)));
            // Touch adapters evaluate single-precision Unity polylines. Do not
            // recursively chase their sub-pixel rounding as geometric bends.
            double roundoff = segment is ParametricSlidePath.PolarArcSegment ? 0 : .0002;
            if (chord <= o.MaxChord && error <= o.Flatness &&
                polygon - chord <= o.LengthDefect * (t1 - t0) + roundoff)
            {
                // Keep the quarter points used by the error estimate. Never
                // assign (t1-t0)*sourceLength to a variable-speed segment.
                Add(a, q1, (q1 - a).Magnitude); Add(q1, mid, (mid - q1).Magnitude);
                Add(mid, q3, (q3 - mid).Magnitude); Add(q3, b, (b - q3).Magnitude);
                defect += Math.Max(0, polygon - chord);
                return;
            }
            if (depth >= o.MaxDepth) throw new InvalidOperationException("Curve tolerance did not converge.");
            double t = (t0 + t1) / 2;
            Flatten(segment, t0, t, a, mid, depth + 1);
            Flatten(segment, t, t1, mid, b, depth + 1);
        }

        Start = Point(source.Segments[0], 0);
        Complex previousEnd = Start;
        foreach (var segment in source.Segments)
        {
            var a = Point(segment, 0); var b = Point(segment, 1);
            if ((a - previousEnd).Magnitude > .001)
                throw new ArgumentException("Discontinuous path; a teleport cannot be treated as travel distance.");
            var rangeStart = length;
            switch (segment)
            {
                case ParametricSlidePath.LineSegment:
                    Add(a, b, (b - a).Magnitude); break;
                case ParametricSlidePath.ArcSegment arc:
                    Add(a, b, Math.Abs(arc.EndAngle - arc.StartAngle) * arc.Circle.Radius,
                        arc.Circle.Center, arc.Circle.Radius, arc.StartAngle, arc.EndAngle - arc.StartAngle); break;
                case ParametricSlidePath.CircleSegment circle:
                    var sweep = circle.IsCcw ? Scalar.Tau : -Scalar.Tau;
                    Add(a, b, Scalar.Tau * circle.Circle.Radius, circle.Circle.Center,
                        circle.Circle.Radius, circle.StartAngle, sweep); break;
                case ParametricSlidePath.PolarArcSegment polar when Math.Abs(polar.StartRadius - polar.EndRadius) < 1e-10:
                    Add(a, b, Math.Abs(polar.DeltaAngle) * polar.StartRadius, Complex.Zero,
                        polar.StartRadius, polar.StartAngle, polar.DeltaAngle); break;
                default:
                    approximate++;
                    // Seeding also detects closed loops whose endpoints and
                    // midpoint coincide. Black-box segments have no proven
                    // derivative bound; convergence is checked again in tests.
                    const int seeds = 64;
                    var first = a;
                    for (int i = 0; i < seeds; i++)
                    {
                        var next = Point(segment, (i + 1d) / seeds);
                        Flatten(segment, i / (double)seeds, (i + 1d) / seeds, first, next, 0);
                        first = next;
                    }
                    break;
            }
            if (length > rangeStart)
            {
                // Judge topology must not depend on redundant authored nodes.
                // Merge collinear forward legs for corner/skip analysis while
                // leaving the measured pieces themselves untouched.
                if (ranges.Count > 0 && ranges[ranges.Count - 1].Source is ParametricSlidePath.LineSegment before &&
                    segment is ParametricSlidePath.LineSegment after && SameDirection(before, after))
                    ranges[ranges.Count - 1] = (ranges[ranges.Count - 1].Start, length,
                        new ParametricSlidePath.LineSegment(before.StartPoint, after.EndPoint));
                else ranges.Add((rangeStart, length, segment));
            }
            previousEnd = b;
        }
        for (int i = 1; i < ranges.Count; i++)
        {
            var left = ranges[i - 1]; var right = ranges[i];
            var v1 = left.Source.GetTangentAt(1); var v2 = right.Source.GetTangentAt(0);
            double turn = Finite(v1) && Finite(v2) && v1.Magnitude > 0 && v2.Magnitude > 0
                ? Math.Acos(Scalar.Clamp(Dot(v1, v2) / (v1.Magnitude * v2.Magnitude), -1, 1)) * 180 / Math.PI : 0;
            joins.Add(new(right.Start, left.Start, right.End, right.Source.GetPointAt(0), turn,
                left.Source is ParametricSlidePath.LineSegment && right.Source is ParametricSlidePath.LineSegment));
        }
        Pieces = pieces.AsReadOnly(); Joins = joins.AsReadOnly();
        End = previousEnd; Length = length; ApproximateSegments = approximate;
        Evaluations = evaluations; EstimatedLengthDefect = defect;
    }

    public Complex AtDistance(double distance)
    {
        if (!Scalar.Finite(distance)) throw new ArgumentException("Non-finite distance.");
        if (distance <= 0 || Pieces.Count == 0) return Start;
        if (distance >= Length) return End;
        int low = 0, high = Pieces.Count - 1;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (Pieces[mid].Offset + Pieces[mid].Length < distance) low = mid + 1;
            else high = mid;
        }
        var p = Pieces[low];
        return p.At(Scalar.Clamp((distance - p.Offset) / p.Length, 0, 1));
    }

    internal static double Dot(Complex a, Complex b) => a.Real * b.Real + a.Imaginary * b.Imaginary;
    internal static bool Finite(Complex p) => Scalar.Finite(p.Real) && Scalar.Finite(p.Imaginary);
    static bool SameDirection(ParametricSlidePath.LineSegment a, ParametricSlidePath.LineSegment b)
    {
        var x = a.EndPoint - a.StartPoint; var y = b.EndPoint - b.StartPoint;
        return Dot(x, y) > 0 && Math.Abs(x.Real * y.Imaginary - x.Imaginary * y.Real) <= 1e-10 * x.Magnitude * y.Magnitude;
    }
    static double DistanceToLineSegment(Complex p, Complex a, Complex b)
    {
        double length2 = Dot(b - a, b - a);
        return (p - (a + (b - a) * (length2 > 0 ? Scalar.Clamp(Dot(p - a, b - a) / length2, 0, 1) : 0))).Magnitude;
    }
}
