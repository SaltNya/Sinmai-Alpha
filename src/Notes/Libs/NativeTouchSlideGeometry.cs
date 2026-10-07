using System;
using System.Collections.Generic;
using Complex = System.Numerics.Complex;
using UnityEngine;

namespace SinmaiAlpha.Notes.Libs;

// The reference supplies geometry only. Original SlideDataBuilder constructs
// physical HitAreas and native SlideRoot owns input, completion, Miss and score.
public static class NativeTouchSlideGeometry
{
    private sealed class StationaryPoint : ParametricSlidePath.PathSegment
    {
        private readonly Complex point;
        public StationaryPoint(Complex point) { this.point = point; }
        public override bool DoAngleLerp => true;
        public override Complex GetPointAt(double t) => point;
        public override Complex GetTangentAt(double t) => Complex.One;
        // One native hit-area sample, with finite normalized distances. The
        // reference displays one stationary bar/star below its 0.0001 threshold.
        // This internal length moves no pixel and grants no automatic judgment.
        public override double GetSegmentLength() => 10;
    }
    private sealed class Polyline : ParametricSlidePath.PathSegment
    {
        private readonly Vector3[] points;
        private readonly float[] lengths;
        public override bool DoAngleLerp => true;
        public Polyline(Vector3[] points, float[] lengths)
        {
            this.points = points;
            this.lengths = lengths;
            if (points.Length < 2 || GetSegmentLength() <= .001) throw new ArgumentException("Touch slide has no travel distance.");
            ArrowDistance = MajdataSlidePath.BarSpacing * 100;
        }
        private float Distance(double t) => Mathf.Clamp01((float)t) * lengths[lengths.Length - 1];
        public override Complex GetPointAt(double t)
        {
            // Keep reference float distances and duplicate samples. Recomputing
            // the target in pixel doubles can select the other side of a corner.
            var distance = Distance(t);
            var index = Array.BinarySearch(lengths, distance);
            Vector3 point;
            if (index >= 0) point = points[index];
            else
            {
                index = ~index;
                if (index <= 0) point = points[0];
                else if (index >= points.Length) point = points[points.Length - 1];
                else
                {
                    var span = lengths[index] - lengths[index - 1];
                    var local = span <= .0001f ? 0 : (distance - lengths[index - 1]) / span;
                    point = Vector3.Lerp(points[index - 1], points[index], local);
                }
            }
            return new Complex(point.x * 100, point.y * 100);
        }
        public override Complex GetTangentAt(double t)
        {
            var index = Array.BinarySearch(lengths, Distance(t));
            if (index >= 0)
            {
                for (var previous = index - 1; previous >= 0; previous--)
                {
                    var incoming = points[index] - points[previous];
                    if (incoming.sqrMagnitude > .000001f) return Tangent(incoming);
                }
                for (var next = index + 1; next < points.Length; next++)
                {
                    var outgoing = points[next] - points[index];
                    if (outgoing.sqrMagnitude > .000001f) return Tangent(outgoing);
                }
                return Complex.One;
            }
            index = Math.Max(0, Math.Min(points.Length - 2, ~index - 1));
            var vector = points[index + 1] - points[index];
            return vector.sqrMagnitude > .000001f ? Tangent(vector) : Complex.One;
        }
        private static Complex Tangent(Vector3 vector)
        {
            var value = new Complex(vector.x, vector.y);
            return value / value.Magnitude;
        }
        public override double GetSegmentLength() => lengths[lengths.Length - 1] * 100d;
    }
    public static ParametricSlidePath Build(string expression)
    {
        var reference = new MajdataSlidePath(expression, alpha053: true, geometryOnly: true);
        var points = new List<Vector3>();
        var distances = new List<float>();
        for (var i = 0; i < reference.GeometryPoints.Count; i++)
        {
            var p = reference.GeometryPoints[i];
            var point = new Complex(p.x * 100, p.y * 100);
            if (double.IsNaN(point.Real) || double.IsNaN(point.Imaginary) || double.IsInfinity(point.Real) || double.IsInfinity(point.Imaginary))
                throw new ArgumentException("Touch slide has invalid geometric coordinates.");
            points.Add(p); distances.Add(reference.GeometryDistances[i]);
        }
        if (points.Count != 0 && reference.Length <= .0001f)
            return new ParametricSlidePath(new[] { new StationaryPoint(new Complex(points[0].x * 100, points[0].y * 100)) });
        return new ParametricSlidePath(new[] { new Polyline(points.ToArray(), distances.ToArray()) });
    }
    public static List<SlideDataBuilder.ArrowData> BuildArrows(ParametricSlidePath path)
    {
        // Keep endpoints for native star interpolation/GetArrowData, with the
        // same N-1 visible centers and uniform spacing as TouchSlideDrop.
        var length = path.GetPathLength();
        var count = Math.Max(2, Mathf.RoundToInt((float)(length / (MajdataSlidePath.BarSpacing * 100))));
        var result = new List<SlideDataBuilder.ArrowData>(count + 1);
        for (var i = 0; i <= count; i++)
        {
            var progress = (float)i / count;
            result.Add(new SlideDataBuilder.ArrowData(path.GetPointAt(progress), path.GetTangentAt(progress), length * progress));
        }
        return result;
    }
}
