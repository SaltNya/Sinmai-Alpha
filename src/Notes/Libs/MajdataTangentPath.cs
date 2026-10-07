using System;
using System.Collections.Generic;
using UnityEngine;
namespace SinmaiAlpha.Notes.Libs;
// Ported from MajdataViewAlpha v0.5.3 SlideDrop.cs (GPL-3.0).
public static class MajdataTangentPath {
    public static Vector3[] Build(
        IReadOnlyList<Vector3> originalRoute,
        Vector3 start,
        Vector3 end,
        bool centerAtOrigin,
        Vector3? requestedCenter = null,
        float maxStep = 0.08f)
    {
        maxStep = Mathf.Max(0.01f, maxStep);
        if (!TryFindCircleSection(
                originalRoute,
                out var circleStart,
                out var circleEnd,
                out var sourceCenter,
                out var radius,
                out var direction,
                centerAtOrigin))
        {
            return BuildSampledLine(start, end, maxStep);
        }

        var center = requestedCenter ?? sourceCenter;
        var centerOffset = center - sourceCenter;
        if (
            !TryGetDirectedTangentPoint(
                center, radius, start,
                originalRoute[circleStart] + centerOffset,
                direction, true, out var entry) ||
            !TryGetDirectedTangentPoint(
                center, radius, end,
                originalRoute[circleEnd] + centerOffset,
                direction, false, out var exit))
        {
            return BuildSampledLine(start, end, maxStep);
        }

        var points = new List<Vector3>();
        AppendSampledLine(points, start, entry, maxStep, includeStart: true);

        var startAngle = Mathf.Atan2(entry.y - center.y, entry.x - center.x);
        var endAngle = Mathf.Atan2(exit.y - center.y, exit.x - center.x);
        var sourceSweep = MeasureRouteSweep(
            originalRoute, circleStart, circleEnd, sourceCenter, direction);
        var sweep = PreserveRouteTurns(
            GetDirectedSweep(startAngle, endAngle, direction),
            sourceSweep,
            direction);
        var arcLength = Mathf.Abs(sweep) * radius;
        var arcSegments = Math.Max(1, Mathf.CeilToInt(arcLength / maxStep));
        for (var i = 1; i <= arcSegments; i++)
        {
            var progress = i / (float)arcSegments;
            var angle = startAngle + sweep * progress;
            points.Add(new Vector3(
                center.x + Mathf.Cos(angle) * radius,
                center.y + Mathf.Sin(angle) * radius,
                Mathf.Lerp(entry.z, exit.z, progress)));
        }

        AppendSampledLine(points, exit, end, maxStep, includeStart: false);
        points[0] = start;
        points[points.Count - 1] = end;
        return points.ToArray();
    }

    private static Vector3[] BuildSampledLine(
        Vector3 start, Vector3 end, float maxStep)
    {
        var points = new List<Vector3>();
        AppendSampledLine(points, start, end, maxStep, includeStart: true);
        return points.ToArray();
    }

    private static void AppendSampledLine(
        ICollection<Vector3> points,
        Vector3 start,
        Vector3 end,
        float maxStep,
        bool includeStart)
    {
        var length = Vector3.Distance(start, end);
        var segments = Math.Max(1, Mathf.CeilToInt(length / maxStep));
        var first = includeStart ? 0 : 1;
        for (var i = first; i <= segments; i++)
            points.Add(Vector3.Lerp(start, end, i / (float)segments));
    }

    private static bool TryFindCircleSection(
        IReadOnlyList<Vector3> route,
        out int sectionStart,
        out int sectionEnd,
        out Vector3 center,
        out float radius,
        out float direction,
        bool centerAtOrigin)
    {
        sectionStart = 0;
        sectionEnd = 0;
        center = Vector3.zero;
        radius = 0f;
        direction = 1f;
        if (route.Count < 6)
            return false;

        var bestStart = -1;
        var bestEnd = -1;
        var bestDirection = 0f;
        var currentStart = -1;
        var currentDirection = 0f;
        for (var i = 1; i < route.Count - 1; i++)
        {
            var incoming = route[i] - route[i - 1];
            var outgoing = route[i + 1] - route[i];
            if (incoming.sqrMagnitude <= 0.0001f || outgoing.sqrMagnitude <= 0.0001f)
                continue;

            var turn = Vector2.SignedAngle(incoming, outgoing);
            var turnDirection = Mathf.Sign(turn);
            var isCircleTurn = Mathf.Abs(turn) >= 4f && Mathf.Abs(turn) <= 30f;
            if (isCircleTurn && (currentStart < 0 || turnDirection == currentDirection))
            {
                if (currentStart < 0)
                {
                    currentStart = i;
                    currentDirection = turnDirection;
                }
                continue;
            }

            if (currentStart >= 0 &&
                (bestStart < 0 || i - 1 - currentStart > bestEnd - bestStart))
            {
                bestStart = currentStart;
                bestEnd = i - 1;
                bestDirection = currentDirection;
            }
            currentStart = isCircleTurn ? i : -1;
            currentDirection = isCircleTurn ? turnDirection : 0f;
        }

        if (currentStart >= 0 &&
            (bestStart < 0 || route.Count - 2 - currentStart > bestEnd - bestStart))
        {
            bestStart = currentStart;
            bestEnd = route.Count - 2;
            bestDirection = currentDirection;
        }

        if (bestStart < 0 || bestEnd - bestStart < 2)
            return false;

        sectionStart = bestStart;
        sectionEnd = bestEnd;
        if (centerAtOrigin)
        {
            center = new Vector3(0f, 0f, route[sectionStart].z);
            radius = 0f;
            for (var i = sectionStart; i <= sectionEnd; i++)
                radius += Vector2.Distance(Vector2.zero, route[i]);
            radius /= sectionEnd - sectionStart + 1f;
        }
        else if (!TryFitCircle(route, sectionStart, sectionEnd, out center, out radius))
        {
            return false;
        }

        direction = bestDirection;
        return true;
    }

    private static bool TryFitCircle(
        IReadOnlyList<Vector3> points,
        int start,
        int end,
        out Vector3 center,
        out float radius)
    {
        center = Vector3.zero;
        radius = 0f;
        double xx = 0d, xy = 0d, x = 0d;
        double yy = 0d, y = 0d, count = 0d;
        double xb = 0d, yb = 0d, b = 0d;
        for (var i = start; i <= end; i++)
        {
            var px = (double)points[i].x;
            var py = (double)points[i].y;
            var value = px * px + py * py;
            xx += 4d * px * px;
            xy += 4d * px * py;
            x += 2d * px;
            yy += 4d * py * py;
            y += 2d * py;
            count += 1d;
            xb += 2d * px * value;
            yb += 2d * py * value;
            b += value;
        }

        var determinant = Determinant3(xx, xy, x, xy, yy, y, x, y, count);
        if (Math.Abs(determinant) <= 0.000001d)
            return false;

        var cx = Determinant3(xb, xy, x, yb, yy, y, b, y, count) / determinant;
        var cy = Determinant3(xx, xb, x, xy, yb, y, x, b, count) / determinant;
        var constant = Determinant3(xx, xy, xb, xy, yy, yb, x, y, b) / determinant;
        var radiusSquared = cx * cx + cy * cy + constant;
        if (radiusSquared <= 0.000001d)
            return false;

        center = new Vector3((float)cx, (float)cy, points[start].z);
        radius = Mathf.Sqrt((float)radiusSquared);
        return true;
    }

    private static double Determinant3(
        double a, double b, double c,
        double d, double e, double f,
        double g, double h, double i)
        => a * (e * i - f * h) - b * (d * i - f * g) + c * (d * h - e * g);

    private static bool TryGetDirectedTangentPoint(
        Vector3 center,
        float radius,
        Vector3 point,
        Vector3 preferred,
        float direction,
        bool entering,
        out Vector3 tangent)
    {
        tangent = preferred;
        var delta = point - center;
        var distanceSquared = delta.x * delta.x + delta.y * delta.y;
        var radiusSquared = radius * radius;
        if (distanceSquared < radiusSquared - 0.0001f)
            return false;
        if (Mathf.Abs(distanceSquared - radiusSquared) <= 0.0001f)
        {
            tangent = point;
            tangent.z = preferred.z;
            return true;
        }

        var baseScale = radiusSquared / distanceSquared;
        var offsetScale = radius * Mathf.Sqrt(distanceSquared - radiusSquared) /
                          distanceSquared;
        var basePoint = center + delta * baseScale;
        var perpendicular = new Vector3(-delta.y, delta.x, 0f);
        var first = basePoint + perpendicular * offsetScale;
        var second = basePoint - perpendicular * offsetScale;

        float Continuity(Vector3 candidate)
        {
            var radial = candidate - center;
            var circleDirection = direction >= 0f
                ? new Vector3(-radial.y, radial.x, 0f)
                : new Vector3(radial.y, -radial.x, 0f);
            var lineDirection = entering
                ? candidate - point
                : point - candidate;
            if (circleDirection.sqrMagnitude <= 0.000001f ||
                lineDirection.sqrMagnitude <= 0.000001f)
                return 1f;
            return Vector3.Dot(
                circleDirection.normalized,
                lineDirection.normalized);
        }

        var firstScore = Continuity(first);
        var secondScore = Continuity(second);
        if (Mathf.Abs(firstScore - secondScore) <= 0.0001f)
            tangent = (first - preferred).sqrMagnitude <=
                      (second - preferred).sqrMagnitude
                ? first
                : second;
        else
            tangent = firstScore > secondScore ? first : second;
        tangent.z = preferred.z;
        return true;
    }

    private static float GetDirectedSweep(float start, float end, float direction)
    {
        var sweep = end - start;
        if (direction >= 0f)
        {
            while (sweep < 0f)
                sweep += Mathf.PI * 2f;
        }
        else
        {
            while (sweep > 0f)
                sweep -= Mathf.PI * 2f;
        }
        return sweep;
    }

    private static float MeasureRouteSweep(
        IReadOnlyList<Vector3> route,
        int start,
        int end,
        Vector3 center,
        float direction)
    {
        var sweep = 0f;
        for (var index = start + 1; index <= end; index++)
        {
            var previous = route[index - 1] - center;
            var current = route[index] - center;
            if (previous.sqrMagnitude <= 0.000001f ||
                current.sqrMagnitude <= 0.000001f)
                continue;
            var delta = Vector2.SignedAngle(previous, current) * Mathf.Deg2Rad;
            if (direction >= 0f && delta < 0f)
                delta += Mathf.PI * 2f;
            else if (direction < 0f && delta > 0f)
                delta -= Mathf.PI * 2f;
            sweep += delta;
        }
        return sweep;
    }

    private static float PreserveRouteTurns(
        float sweep,
        float sourceSweep,
        float direction)
    {
        var turn = direction >= 0f ? Mathf.PI * 2f : -Mathf.PI * 2f;
        while (Mathf.Abs(sweep) + Mathf.PI < Mathf.Abs(sourceSweep))
            sweep += turn;
        return sweep;
    }

}
