// Ported from MajdataViewAlpha v0.5.3 SlideCodePathGeometry.cs (GPL-3.0).
using System;
using System.Collections.Generic;
using UnityEngine;
namespace SinmaiAlpha.Notes.Libs;

internal static class MajdataSingleOrbitPath
{
    private const float JudgeRadius = 4.8f;
    private const float SampleSpacing = 0.055f;
    private const float Epsilon = 0.001f;
    private static readonly float Cos22_5 = Mathf.Cos(Mathf.PI / 8f);
    private static readonly float Cos67_5 = Mathf.Cos(3f * Mathf.PI / 8f);

    private readonly struct Circle
    {
        public Circle(int index, Vector2 center, float radius)
        {
            Index = index;
            Center = center;
            Radius = radius;
        }

        public int Index { get; }
        public Vector2 Center { get; }
        public float Radius { get; }
    }

    public static bool AppendSingleOrbit(
        List<Vector3> target,
        Vector3 start,
        Vector3 end,
        int orbitIndex,
        bool counterClockwise)
    {
        var route = new List<Vector3>();
        var circle = Orbit(orbitIndex);
        Add(route, start);
        var direction = counterClockwise ? 1 : -1;
        if (!EnterOrbit(route, start, circle, direction, out var angle) ||
            !ExitOrbit(route, circle, angle, direction, end, out _))
            return false;
        var first = target.Count == 0 ? 0 : 1;
        for (var index = first; index < route.Count; index++)
            Add(target, route[index]);
        return true;
    }

    private static bool EnterOrbit(
        List<Vector3> target,
        Vector2 point,
        Circle circle,
        int direction,
        out float angle)
    {
        var relative = point - circle.Center;
        var distance = relative.magnitude;
        if (distance < circle.Radius - Epsilon)
        {
            angle = 0f;
            return false;
        }
        var pointAngle = Mathf.Atan2(relative.y, relative.x);
        if (Mathf.Abs(distance - circle.Radius) <= Epsilon)
        {
            angle = pointAngle;
            return true;
        }

        var offset = Mathf.Acos(Mathf.Clamp(circle.Radius / distance, -1f, 1f));
        angle = pointAngle + direction * offset;
        AppendLine(target, point, Point(circle, angle));
        return true;
    }

    private static bool ExitOrbit(
        List<Vector3> target,
        Circle circle,
        float currentAngle,
        int direction,
        Vector2 point,
        out float exitAngle)
    {
        var relative = point - circle.Center;
        var distance = relative.magnitude;
        if (distance < circle.Radius - Epsilon)
        {
            exitAngle = 0f;
            return false;
        }
        var pointAngle = Mathf.Atan2(relative.y, relative.x);
        if (Mathf.Abs(distance - circle.Radius) <= Epsilon)
        {
            exitAngle = pointAngle;
        }
        else
        {
            var offset = Mathf.Acos(Mathf.Clamp(circle.Radius / distance, -1f, 1f));
            exitAngle = pointAngle - direction * offset;
        }

        var sweep = DirectedSweep(
            currentAngle, exitAngle, direction, forceFullWhenSame: false);
        AppendArc(target, circle, currentAngle, sweep, direction);
        AppendLine(target, Point(circle, exitAngle), point);
        return true;
    }

    private static Circle Orbit(int index)
    {
        if (index == 0)
            return new Circle(0, Vector2.zero, JudgeRadius * Cos67_5);
        if (index == 9)
            return new Circle(9, Vector2.zero, JudgeRadius);
        var radius = JudgeRadius * Cos22_5 * 0.5f;
        return new Circle(index, Polar(radius, OrbitAngle(index)), radius);
    }

    private static float KeyAngle(int index) =>
        (112.5f - index * 45f) * Mathf.Deg2Rad;

    private static float OrbitAngle(int index) =>
        (135f - index * 45f) * Mathf.Deg2Rad;

    private static Vector2 Polar(float radius, float angle) =>
        new(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);

    private static Vector2 Point(Circle circle, float angle) =>
        circle.Center + Polar(circle.Radius, angle);

    private static void AppendLine(
        List<Vector3> target, Vector2 start, Vector2 end)
    {
        var count = Mathf.Max(
            1, Mathf.CeilToInt(Vector2.Distance(start, end) / SampleSpacing));
        for (var index = 1; index <= count; index++)
            Add(target, Vector2.Lerp(start, end, index / (float)count));
    }

    private static void AppendArc(
        List<Vector3> target,
        Circle circle,
        float startAngle,
        float endAngle,
        int direction,
        bool forceFullWhenSame)
    {
        var sweep = DirectedSweep(
            startAngle, endAngle, direction, forceFullWhenSame);
        AppendArc(target, circle, startAngle, sweep, direction);
    }

    private static void AppendArc(
        List<Vector3> target,
        Circle circle,
        float startAngle,
        float sweep,
        int direction)
    {
        var count = Mathf.Max(
            1, Mathf.CeilToInt(circle.Radius * sweep / SampleSpacing));
        var signedSweep = sweep * direction;
        for (var index = 1; index <= count; index++)
            Add(target, Point(
                circle, startAngle + signedSweep * (index / (float)count)));
    }

    private static float DirectedSweep(
        float startAngle,
        float endAngle,
        int direction,
        bool forceFullWhenSame)
    {
        var delta = direction > 0
            ? Mathf.Repeat(endAngle - startAngle, Mathf.PI * 2f)
            : Mathf.Repeat(startAngle - endAngle, Mathf.PI * 2f);
        if (forceFullWhenSame && delta < Epsilon)
            return Mathf.PI * 2f;
        return delta;
    }

    private static void Add(List<Vector3> target, Vector2 point)
    {
        var value = new Vector3(point.x, point.y, 0f);
        if (target.Count == 0 ||
            (target[target.Count - 1] - value).sqrMagnitude > 0.000001f)
            target.Add(value);
    }
}
