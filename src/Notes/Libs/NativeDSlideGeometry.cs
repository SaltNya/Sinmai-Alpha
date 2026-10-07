using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

namespace SinmaiAlpha.Notes.Libs;

/// <summary>
/// Geometry-only adapter. The resulting path is consumed by the original SC
/// arrow/hit-area builder and Sinmai SlideRoot; no preview judge or runtime runs.
/// </summary>
public static class NativeDSlideGeometry
{
    private sealed class Polyline : ParametricSlidePath.PathSegment
    {
        private readonly Complex[] points;
        private readonly double[] lengths;
        public override bool DoAngleLerp => true;

        public Polyline(Complex[] points)
        {
            this.points = points;
            lengths = new double[points.Length];
            for (var i = 1; i < points.Length; i++)
                lengths[i] = lengths[i - 1] + (points[i] - points[i - 1]).Magnitude;
            if (GetSegmentLength() < .001) throw new ArgumentException("D slide has no travel distance.");
        }

        private int Interval(double t, out double local)
        {
            var distance = Math.Max(0, Math.Min(1, t)) * GetSegmentLength();
            var index = Array.BinarySearch(lengths, distance);
            if (index < 0) index = ~index;
            index = Math.Max(1, Math.Min(points.Length - 1, index));
            var span = lengths[index] - lengths[index - 1];
            local = span > .0001 ? (distance - lengths[index - 1]) / span : 0;
            return index;
        }

        public override Complex GetPointAt(double t)
        {
            var index = Interval(t, out var local);
            return points[index - 1] + (points[index] - points[index - 1]) * local;
        }

        public override Complex GetTangentAt(double t)
        {
            var index = Interval(t, out _);
            var tangent = points[index] - points[index - 1];
            return tangent / tangent.Magnitude;
        }

        public override double GetSegmentLength() => lengths[lengths.Length - 1];
    }

    public static ParametricSlidePath Build(string expression)
    {
        var start = Regex.Match(expression, @"^[1-8]d?");
        if (!start.Success) throw new FormatException("Invalid D slide start.");
        var segments = Regex.Matches(expression.Substring(start.Length), @"(?<shape>pp|qq|rp|rq|V[1-8]d?|[-<>^vpqsz])(?<end>[1-8]d?)");
        var points = new List<Complex>();
        void Add(Complex converted)
        {
            if (double.IsNaN(converted.Real) || double.IsNaN(converted.Imaginary)
                || double.IsInfinity(converted.Real) || double.IsInfinity(converted.Imaginary))
                throw new ArgumentException("D slide contains invalid geometry.");
            if (points.Count == 0 || (converted - points[points.Count - 1]).Magnitude > .0001)
                points.Add(converted);
        }
        Complex Position(string key)
        {
            var position = key[0] - '0' - (key.EndsWith("d", StringComparison.Ordinal) ? .5 : 0);
            return Complex.FromPolarCoordinates(MaiGeometry.MainRadius, (5.0 / 8.0 - position / 4.0) * Math.PI);
        }
        var current = start.Value;
        var consumed = start.Length;
        foreach (Match segment in segments)
        {
            var shape = segment.Groups["shape"].Value;
            var end = segment.Groups["end"].Value;
            consumed += segment.Length;
            if (shape == "-" || shape == "v" || shape.StartsWith("V", StringComparison.Ordinal))
            {
                // Explicit straight/v/V geometry has no need for a stock prefab
                // (adjacent D positions are also valid straight endpoints).
                Add(Position(current));
                if (shape == "v") Add(Complex.Zero);
                else if (shape.StartsWith("V", StringComparison.Ordinal)) Add(Position(shape.Substring(1)));
                Add(Position(end));
            }
            else
            {
                // Import only geometric points for curved templates. Areas,
                // HideCount/JudgeProgress and Majdata*Judge are never consumed.
                var reference = new MajdataRegularPath(current + shape + end, alpha053: true, geometryOnly: true);
                var samples = Math.Max(64, Math.Min(8192, (int)Math.Ceiling(reference.Length * 100 / 4)));
                for (var i = 0; i <= samples; i++)
                {
                    var point = reference.EvaluatePath(i / (float)samples);
                    Add(new Complex(point.x * 100, point.y * 100));
                }
            }
            current = end;
        }
        if (segments.Count == 0 || consumed != expression.Length) throw new FormatException("Invalid D slide expression.");
        if (points.Count < 2) throw new ArgumentException("D slide has no path.");
        return new ParametricSlidePath(new[] { new Polyline(points.ToArray()) });
    }
}
