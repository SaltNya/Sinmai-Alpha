using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace SinmaiAlpha.Notes.Libs;
// Geometry from MajdataViewAlpha v0.5.3 SlideDrop.cs (GPL-3.0), preserved separately from MD1.
public static class MajdataDZoneGeometry {
    public static Vector3[] BuildStraightRoute(int pointCount, Vector3 start, Vector3 end)
    {
        var route = new Vector3[pointCount];
        for (var i = 0; i < route.Length; i++)
            route[i] = Vector3.Lerp(start, end, i / Math.Max(1f, route.Length - 1f));
        return route;
    }

    public static Vector3[] BuildViaPointRoute(
        int pointCount,
        Vector3 start,
        Vector3 middle,
        Vector3 end)
    {
        var route = new Vector3[pointCount];
        var firstLength = Vector3.Distance(start, middle);
        var secondLength = Vector3.Distance(middle, end);
        var totalLength = firstLength + secondLength;
        var split = totalLength > 0.0001f
            ? Mathf.Clamp(
                Mathf.RoundToInt((pointCount - 1) * firstLength / totalLength),
                1,
                Math.Max(1, pointCount - 2))
            : Math.Max(1, (pointCount - 1) / 2);
        for (var i = 0; i < pointCount; i++)
        {
            route[i] = i <= split
                ? Vector3.Lerp(start, middle, i / (float)split)
                : Vector3.Lerp(middle, end, (i - split) / (float)Math.Max(1, pointCount - 1 - split));
        }
        return route;
    }

    public static Vector3[] BuildAnchoredMiddleRoute(
        IReadOnlyList<Vector3> originalRoute,
        Vector3 start,
        Vector3 end)
    {
        var route = originalRoute.ToArray();
        var firstAnchor = -1;
        var lastAnchor = -1;
        for (var i = 1; i < originalRoute.Count - 1; i++)
        {
            var incoming = originalRoute[i] - originalRoute[i - 1];
            var outgoing = originalRoute[i + 1] - originalRoute[i];
            if (incoming.sqrMagnitude <= 0.0001f || outgoing.sqrMagnitude <= 0.0001f)
                continue;
            if (Mathf.Abs(Vector2.SignedAngle(incoming, outgoing)) < 30f)
                continue;
            if (firstAnchor < 0)
                firstAnchor = i;
            lastAnchor = i;
        }

        if (firstAnchor < 1 || lastAnchor <= firstAnchor || lastAnchor >= route.Length - 1)
            return BuildStraightRoute(route.Length, start, end);

        for (var i = 0; i <= firstAnchor; i++)
            route[i] = Vector3.Lerp(start, originalRoute[firstAnchor], i / (float)firstAnchor);
        var tailLength = route.Length - 1 - lastAnchor;
        for (var i = lastAnchor; i < route.Length; i++)
            route[i] = Vector3.Lerp(
                originalRoute[lastAnchor], end, (i - lastAnchor) / (float)tailLength);
        route[0] = start;
        route[route.Length - 1] = end;
        return route;
    }

    public static Vector3[] BuildLightningRoute(
        IReadOnlyList<Vector3> originalRoute,
        Vector3 start,
        Vector3 end)
    {
        var firstCorner = -1;
        var lastCorner = -1;
        for (var i = 1; i < originalRoute.Count - 1; i++)
        {
            var incoming = originalRoute[i] - originalRoute[i - 1];
            var outgoing = originalRoute[i + 1] - originalRoute[i];
            if (incoming.sqrMagnitude <= 0.0001f || outgoing.sqrMagnitude <= 0.0001f)
                continue;
            if (Mathf.Abs(Vector2.SignedAngle(incoming, outgoing)) < 30f)
                continue;
            if (firstCorner < 0)
                firstCorner = i;
            lastCorner = i;
        }

        if (firstCorner < 1 || lastCorner <= firstCorner ||
            lastCorner >= originalRoute.Count - 1)
            return BuildStraightRoute(originalRoute.Count, start, end);

        // Preserve the two authored corners themselves. Resampling this polyline
        // here can place one route edge across a corner and creates a fourth,
        // diagonal direction that does not exist in the original s/z route.
        return new[]
        {
            start,
            originalRoute[firstCorner],
            originalRoute[lastCorner],
            end
        };
    }

    public static Vector3[] BuildOuterCircleRoute(
        IReadOnlyList<Vector3> originalRoute,
        Vector3 start,
        Vector3 end)
    {
        var route = new Vector3[originalRoute.Count];
        var originalSweep = 0f;
        for (var i = 1; i < originalRoute.Count; i++)
        {
            var previousAngle = Mathf.Atan2(originalRoute[i - 1].y, originalRoute[i - 1].x);
            var currentAngle = Mathf.Atan2(originalRoute[i].y, originalRoute[i].x);
            originalSweep += Mathf.DeltaAngle(
                previousAngle * Mathf.Rad2Deg,
                currentAngle * Mathf.Rad2Deg) * Mathf.Deg2Rad;
        }

        var startAngle = Mathf.Atan2(start.y, start.x);
        var endAngle = Mathf.Atan2(end.y, end.x);
        var sweep = endAngle - startAngle;
        sweep += Mathf.Round((originalSweep - sweep) / (Mathf.PI * 2f)) * Mathf.PI * 2f;
        if (originalSweep > 0f && sweep <= 0f)
            sweep += Mathf.PI * 2f;
        else if (originalSweep < 0f && sweep >= 0f)
            sweep -= Mathf.PI * 2f;

        var radius = start.magnitude;
        for (var i = 0; i < route.Length; i++)
        {
            var progress = i / Math.Max(1f, route.Length - 1f);
            var angle = startAngle + sweep * progress;
            route[i] = new Vector3(
                Mathf.Cos(angle) * radius,
                Mathf.Sin(angle) * radius,
                Mathf.Lerp(start.z, end.z, progress));
        }
        route[0] = start;
        route[route.Length - 1] = end;
        return route;
    }

    public static Vector3[] BuildTangentCircleRoute(
        IReadOnlyList<Vector3> originalRoute,
        Vector3 start,
        Vector3 end,
        bool centerAtOrigin)
    {
        var route = new Vector3[originalRoute.Count];
        if (!TryFindCircleSection(
                originalRoute,
                out var circleStart,
                out var circleEnd,
                out var center,
                out var radius,
                out var direction,
                centerAtOrigin) ||
            !TryGetDirectedTangentPoint(
                center, radius, start, originalRoute[circleStart],
                direction, true, out var entry) ||
            !TryGetDirectedTangentPoint(
                center, radius, end, originalRoute[circleEnd],
                direction, false, out var exit))
        {
            for (var i = 0; i < route.Length; i++)
                route[i] = Vector3.Lerp(start, end, i / (float)Math.Max(1, route.Length - 1));
            return route;
        }

        var startAngle = Mathf.Atan2(entry.y - center.y, entry.x - center.x);
        var endAngle = Mathf.Atan2(exit.y - center.y, exit.x - center.x);
        var sourceSweep = MeasureRouteSweep(
            originalRoute, circleStart, circleEnd, center, direction);
        var sweep = PreserveRouteTurns(
            GetDirectedSweep(startAngle, endAngle, direction),
            sourceSweep,
            direction);
        var entryLength = Vector3.Distance(start, entry);
        var arcLength = Mathf.Abs(sweep) * radius;
        var exitLength = Vector3.Distance(exit, end);
        var totalLength = entryLength + arcLength + exitLength;
        // Returning the array as allocated would hand back a route of all zeros,
        // collapsing the whole slide onto the origin, which reads on screen as the
        // slide simply not being there. Degenerate geometry falls back to the
        // straight line, same as when the tangent solve fails above.
        if (totalLength <= 0.0001f)
        {
            for (var i = 0; i < route.Length; i++)
                route[i] = Vector3.Lerp(start, end, i / (float)Math.Max(1, route.Length - 1));
            return route;
        }

        for (var i = 0; i < route.Length; i++)
        {
            var distance = totalLength * i / Math.Max(1f, route.Length - 1f);
            if (distance <= entryLength)
            {
                route[i] = Vector3.Lerp(
                    start,
                    entry,
                    entryLength > 0.0001f ? distance / entryLength : 1f);
            }
            else if (distance < entryLength + arcLength)
            {
                var arcProgress = arcLength > 0.0001f
                    ? (distance - entryLength) / arcLength
                    : 1f;
                var angle = startAngle + sweep * arcProgress;
                route[i] = new Vector3(
                    center.x + Mathf.Cos(angle) * radius,
                    center.y + Mathf.Sin(angle) * radius,
                    Mathf.Lerp(start.z, end.z, distance / totalLength));
            }
            else
            {
                route[i] = Vector3.Lerp(
                    exit,
                    end,
                    exitLength > 0.0001f
                        ? (distance - entryLength - arcLength) / exitLength
                        : 1f);
            }
        }

        route[0] = start;
        route[route.Length - 1] = end;
        return route;
    }

    public static bool TryFindCircleSection(
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

    public static bool TryFitCircle(
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

    public static bool TryGetDirectedTangentPoint(
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

    public static float MeasureRouteSweep(
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

    public static float PreserveRouteTurns(
        float sweep,
        float sourceSweep,
        float direction)
    {
        var turn = direction >= 0f ? Mathf.PI * 2f : -Mathf.PI * 2f;
        while (Mathf.Abs(sweep) + Mathf.PI < Mathf.Abs(sourceSweep))
            sweep += turn;
        return sweep;
    }

    public static float GetDirectedSweep(float start, float end, float direction)
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

    public static float[] BuildCumulativeDistances(IReadOnlyList<Vector3> points)
    {
        var distances = new float[points.Count];
        for (var i = 1; i < points.Count; i++)
            distances[i] = distances[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        return distances;
    }

    public static Vector3 SamplePolyline(
        IReadOnlyList<Vector3> points,
        IReadOnlyList<float> distances,
        float targetDistance)
    {
        if (points.Count == 0)
            return Vector3.zero;
        if (points.Count == 1 || distances[distances.Count - 1] <= 0.0001f)
            return points[0];

        targetDistance = Mathf.Clamp(targetDistance, 0f, distances[distances.Count - 1]);
        var upper = 1;
        while (upper < distances.Count - 1 && distances[upper] < targetDistance)
            upper++;
        var lower = upper - 1;
        var segmentLength = distances[upper] - distances[lower];
        var amount = segmentLength > 0.0001f
            ? (targetDistance - distances[lower]) / segmentLength
            : 0f;
        return Vector3.Lerp(points[lower], points[upper], amount);
    }

    public static Vector3 SamplePolylineTangent(
        IReadOnlyList<Vector3> points,
        IReadOnlyList<float> distances,
        float targetDistance)
    {
        if (points.Count < 2)
            return Vector3.right;

        targetDistance = Mathf.Clamp(targetDistance, 0f, distances[distances.Count - 1]);
        var upper = 1;
        while (upper < distances.Count - 1 && distances[upper] < targetDistance)
            upper++;
        var tangent = points[upper] - points[upper - 1];
        return tangent.sqrMagnitude > 0.0001f ? tangent : Vector3.right;
    }
    private static double Determinant3(double a,double b,double c,double d,double e,double f,double g,double h,double i)
        => a*(e*i-f*h)-b*(d*i-f*g)+c*(d*h-e*g);

}
