using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
namespace SinmaiAlpha.Notes.Libs;
// Ported from the reference SlideDrop; keep its prefab deformation and sampling rules.
public sealed partial class MajdataRegularPath {
    private static Vector3[] BuildStraightRoute(int pointCount, Vector3 start, Vector3 end)
    {
        var route = new Vector3[pointCount];
        for (var i = 0; i < route.Length; i++)
            route[i] = Vector3.Lerp(start, end, i / Math.Max(1f, route.Length - 1f));
        return route;
    }

    private static Vector3[] BuildAnchoredMiddleRoute(
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

    private static Vector3[] BuildOuterCircleRoute(
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

    private static Vector3[] BuildTangentCircleRoute(
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
            !TryGetTangentPoint(center, radius, start, originalRoute[circleStart], out var entry) ||
            !TryGetTangentPoint(center, radius, end, originalRoute[circleEnd], out var exit))
        {
            for (var i = 0; i < route.Length; i++)
                route[i] = Vector3.Lerp(start, end, i / (float)Math.Max(1, route.Length - 1));
            return route;
        }

        var startAngle = Mathf.Atan2(entry.y - center.y, entry.x - center.x);
        var endAngle = Mathf.Atan2(exit.y - center.y, exit.x - center.x);
        var sweep = GetDirectedSweep(startAngle, endAngle, direction);
        var entryLength = Vector3.Distance(start, entry);
        var arcLength = Mathf.Abs(sweep) * radius;
        var exitLength = Vector3.Distance(exit, end);
        var totalLength = entryLength + arcLength + exitLength;
        if (totalLength <= 0.0001f)
            return route;

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

    private static bool TryGetTangentPoint(
        Vector3 center,
        float radius,
        Vector3 point,
        Vector3 preferred,
        out Vector3 tangent)
    {
        tangent = preferred;
        var delta = point - center;
        var distanceSquared = delta.x * delta.x + delta.y * delta.y;
        var radiusSquared = radius * radius;
        if (distanceSquared <= radiusSquared + 0.0001f)
            return false;

        var baseScale = radiusSquared / distanceSquared;
        var offsetScale = radius * Mathf.Sqrt(distanceSquared - radiusSquared) / distanceSquared;
        var basePoint = center + delta * baseScale;
        var perpendicular = new Vector3(-delta.y, delta.x, 0f);
        var first = basePoint + perpendicular * offsetScale;
        var second = basePoint - perpendicular * offsetScale;
        tangent = (first - preferred).sqrMagnitude <= (second - preferred).sqrMagnitude
            ? first
            : second;
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

    private static float[] BuildCumulativeDistances(IReadOnlyList<Vector3> points)
    {
        var distances = new float[points.Count];
        for (var i = 1; i < points.Count; i++)
            distances[i] = distances[i - 1] + Vector3.Distance(points[i - 1], points[i]);
        return distances;
    }

    private static Vector3 SamplePolyline(
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

    private static Vector3 SamplePolylineTangent(
        IReadOnlyList<Vector3> points,
        IReadOnlyList<float> distances,
        float targetDistance)
    {
        if (points.Count < 2)
            return Vector3.right;
        var sampleDistance = Mathf.Max(0.01f, distances[distances.Count - 1] * 0.005f);
        return SamplePolyline(points, distances, targetDistance + sampleDistance) -
               SamplePolyline(points, distances, targetDistance - sampleDistance);
    }

    private static double Determinant3(double a,double b,double c,double d,double e,double f,double g,double h,double i)
        => a*(e*i-f*h)-b*(d*i-f*g)+c*(d*h-e*g);
    private static string detectShapeFromText(string content)
    {
        int getRelativeEndPos(int startPos, int endPos)
        {
            endPos = endPos - startPos;
            endPos = endPos < 0 ? endPos + 8 : endPos;
            endPos = endPos > 8 ? endPos - 8 : endPos;
            return endPos + 1;
        }

        //print(content);
        if ((content.IndexOf('-') >= 0))
        {
            // line
            var str = content.Substring(0, 3); //something like "8-6"
            var digits = str.Split('-');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(startPos, endPos);
            if (endPos < 3 || endPos > 7) throw new Exception("-星星至少隔开一键\n-スライドエラー");
            return "line" + endPos;
        }

        if ((content.IndexOf('>') >= 0))
        {
            // Circle defaults to clockwise
            var str = content.Substring(0, 3);
            var digits = str.Split('>');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(startPos, endPos);
            if (isUpperHalf(startPos))
            {
                return "circle" + endPos;
            }

            endPos = MirrorKeys(endPos);
            return "-circle" + endPos; //Mirror
        }

        if ((content.IndexOf('<') >= 0))
        {
            // Circle defaults to clockwise
            var str = content.Substring(0, 3);
            var digits = str.Split('<');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(startPos, endPos);
            if (!isUpperHalf(startPos))
            {
                return "circle" + endPos;
            }

            endPos = MirrorKeys(endPos);
            return "-circle" + endPos; //Mirror
        }

        if ((content.IndexOf('^') >= 0))
        {
            var str = content.Substring(0, 3);
            var digits = str.Split('^');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(startPos, endPos);

            if (endPos == 1 || endPos == 5)
            {
                throw new Exception("^星星不合法\n^スライドエラー");
            }

            if (endPos < 5)
            {
                return "circle" + endPos;
            }
            if (endPos > 5)
            {
                return "-circle" + MirrorKeys(endPos);
            }
        }

        if ((content.IndexOf('v') >= 0))
        {
            // v
            var str = content.Substring(0, 3);
            var digits = str.Split('v');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(startPos, endPos);
            if (endPos == 5) throw new Exception("v星星不合法\nvスライドエラー");
            return "v" + endPos;
        }

        if (content.Contains("rp"))
        {
            // rp: same arc geometry as (endPos pp startPos), traversed start->end (isReverse handles star direction)
            var str = content.Substring(0, 4);
            var digits = str.Split(new string[] { "rp" }, StringSplitOptions.None);
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(endPos, startPos);
            return "rppqq" + endPos;
        }

        if (content.Contains("rq"))
        {
            // rq: same arc geometry as (endPos qq startPos), traversed start->end (isReverse handles star direction)
            var str = content.Substring(0, 4);
            var digits = str.Split(new string[] { "rq" }, StringSplitOptions.None);
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(endPos, startPos);
            endPos = MirrorKeys(endPos);
            return "-rppqq" + endPos;
        }

        if (content.Contains("pp"))
        {
            // ppqq defaults to pp
            var str = content.Substring(0, 4);
            var digits = str.Split('p');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[2]);
            endPos = getRelativeEndPos(startPos, endPos);
            return "ppqq" + endPos;
        }

        if (content.Contains("qq"))
        {
            // ppqq defaults to pp
            var str = content.Substring(0, 4);
            var digits = str.Split('q');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[2]);
            endPos = getRelativeEndPos(startPos, endPos);
            endPos = MirrorKeys(endPos);
            return "-ppqq" + endPos;
        }

        if ((content.IndexOf('p') >= 0))
        {
            // pq defaults to p
            var str = content.Substring(0, 3);
            var digits = str.Split('p');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(startPos, endPos);
            return "pq" + endPos;
        }

        if ((content.IndexOf('q') >= 0))
        {
            // pq defaults to p
            var str = content.Substring(0, 3);
            var digits = str.Split('q');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(startPos, endPos);
            endPos = MirrorKeys(endPos);
            return "-pq" + endPos;
        }

        if ((content.IndexOf('s') >= 0))
        {
            // s
            var str = content.Substring(0, 3);
            var digits = str.Split('s');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(startPos, endPos);
            if (endPos != 5) throw new Exception("s星星尾部错误\nsスライドエラー");
            return "s";
        }

        if ((content.IndexOf('z') >= 0))
        {
            // Mirrored s
            var str = content.Substring(0, 3);
            var digits = str.Split('z');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(startPos, endPos);
            if (endPos != 5) throw new Exception("z星星尾部错误\nzスライドエラー");
            return "-s";
        }

        if ((content.IndexOf('V') >= 0))
        {
            // L
            var str = content.Substring(0, 4);
            var digits = str.Split('V');
            var startPos = int.Parse(digits[0]);
            var turnPos = int.Parse(digits[1][0].ToString());
            var endPos = int.Parse(digits[1][1].ToString());

            turnPos = getRelativeEndPos(startPos, turnPos);
            endPos = getRelativeEndPos(startPos, endPos);
            if (turnPos == 7)
            {
                if (endPos < 2 || endPos > 5) throw new Exception("V星星终点不合法\nVスライドエラー");
                return "L" + endPos;
            }

            if (turnPos == 3)
            {
                if (endPos < 5) throw new Exception("V星星终点不合法\nVスライドエラー");
                return "-L" + MirrorKeys(endPos);
            }

            throw new Exception("V星星拐点只能隔开一键\nVスライドエラー");
        }

        if ((content.IndexOf('w') >= 0))
        {
            // wifi
            var str = content.Substring(0, 3);
            var digits = str.Split('w');
            var startPos = int.Parse(digits[0]);
            var endPos = int.Parse(digits[1]);
            endPos = getRelativeEndPos(startPos, endPos);
            if (endPos != 5) throw new Exception("w星星尾部错误\nwスライドエラー");
            return "wifi";
        }

        return "";
    }
    private static bool isUpperHalf(int key)
    {
        if (key == 7) return true;
        if (key == 8) return true;
        if (key == 1) return true;
        if (key == 2) return true;

        return false;
    }
    private static int MirrorKeys(int key)
    {
        if (key == 1) return 1;
        if (key == 2) return 8;
        if (key == 3) return 7;
        if (key == 4) return 6;

        if (key == 5) return 5;
        if (key == 6) return 4;
        if (key == 7) return 3;
        if (key == 8) return 2;
        throw new Exception("Keys out of range: " + key);
    }
}
