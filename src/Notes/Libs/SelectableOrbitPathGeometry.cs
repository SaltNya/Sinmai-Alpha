// Ported from MajdataViewAlpha v0.5.3 TouchSlideDrop.cs (GPL-3.0).
using System;
using System.Collections.Generic;
using UnityEngine;
namespace SinmaiAlpha.Notes.Libs;
internal static class SelectableOrbitPathGeometry
{
    public static void Append(
        List<Vector3> target,
        char startArea,
        int startPosition,
        bool startIsDZone,
        char orbitArea,
        int orbitPosition,
        bool orbitIsDZone,
        bool orbitIsNumber,
        char endArea,
        int endPosition,
        bool endIsDZone,
        string shape)
    {
        var start = Position(startArea, startPosition, startIsDZone);
        var end = Position(endArea, endPosition, endIsDZone);
        var lowerShape = shape == "P" ? "p" : "q";

        if (orbitIsNumber)
        {
            var orbitIndex = orbitArea == 'C'
                ? 0
                : orbitArea == 'O'
                    ? 9
                    : orbitPosition;
            if (!TryAppendLegacyNumericOrbit(
                    target, start, end, orbitIndex, shape) &&
                !MajdataSingleOrbitPath.AppendSingleOrbit(
                    target, start, end, orbitIndex, shape == "P"))
                AppendLine(target, start, end);
            return;
        }

        if (orbitArea == 'C' || orbitPosition == 0)
        {
            if (!AppendTemplate(
                    target,
                    startPosition, endPosition, lowerShape,
                    start, end, centerAtOrigin: true, null))
                AppendFallbackCircle(
                    target, start, end, lowerShape == "p");
            return;
        }

        var sourceShape = shape == "P" ? "pp" : "qq";
        var sourceStart = shape == "P"
            ? (orbitPosition + 5) % 8 + 1
            : orbitPosition % 8 + 1;
        var sourceEnd = (sourceStart + 3) % 8 + 1;
        var explicitCenter = orbitIsDZone
            ? Position(orbitArea, orbitPosition, true)
            : (Vector3?)null;
        if (!AppendTemplate(
                target,
                sourceStart, sourceEnd, sourceShape,
                start, end, centerAtOrigin: false, explicitCenter))
            AppendLine(target, start, end);
    }

    private static bool TryAppendLegacyNumericOrbit(
        List<Vector3> target,
        Vector3 start,
        Vector3 end,
        int orbitIndex,
        string shape)
    {
        if (orbitIndex is < 1 or > 8 || shape is not ("P" or "Q"))
            return false;

        var sourceShape = shape == "P" ? "pp" : "qq";
        var sourceStart = shape == "P"
            ? (orbitIndex + 5) % 8 + 1
            : orbitIndex % 8 + 1;
        var sourceEnd = (sourceStart + 3) % 8 + 1;
        return AppendTemplate(
            target,
            sourceStart,
            sourceEnd,
            sourceShape,
            start,
            end,
            centerAtOrigin: false,
            explicitCenter: null);
    }

    private static bool AppendTemplate(
        List<Vector3> target,
        int sourceStart,
        int sourceEnd,
        string sourceShape,
        Vector3 actualStart,
        Vector3 actualEnd,
        bool centerAtOrigin,
        Vector3? explicitCenter)
    {
        var originalRoute = MajdataRegularPath.GetTangentTemplate(sourceStart, sourceEnd, sourceShape);
        var route = MajdataTangentPath.Build(originalRoute, actualStart, actualEnd, centerAtOrigin, explicitCenter);
        var first = target.Count == 0 ? 0 : 1;
        for (var i = first; i < route.Length; i++)
            target.Add(route[i]);
        return true;
    }

    private static void AppendFallbackCircle(
        List<Vector3> target,
        Vector3 start,
        Vector3 end,
        bool clockwise)
    {
        const float radius = 2.3f;
        const int samples = 256;
        var startAngle = Mathf.Atan2(start.y, start.x);
        var endAngle = Mathf.Atan2(end.y, end.x);
        var startTangent = TangentAngle(
            start.magnitude, startAngle, clockwise, true);
        var endTangent = TangentAngle(
            end.magnitude, endAngle, clockwise, false);
        var tangentStart = new Vector3(
            Mathf.Cos(startTangent), Mathf.Sin(startTangent)) * radius;
        var tangentEnd = new Vector3(
            Mathf.Cos(endTangent), Mathf.Sin(endTangent)) * radius;
        AppendLine(target, start, tangentStart);
        var sweep = clockwise
            ? -Mathf.Repeat(startTangent - endTangent, Mathf.PI * 2f)
            : Mathf.Repeat(endTangent - startTangent, Mathf.PI * 2f);
        for (var i = 1; i <= samples; i++)
        {
            var angle = startTangent + sweep * (i / (float)samples);
            Add(target, new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
        }
        AppendLine(target, tangentEnd, end);
    }

    private static void AppendLine(
        List<Vector3> target,
        Vector3 start,
        Vector3 end)
    {
        var samples = Math.Max(
            1, Mathf.CeilToInt(Vector3.Distance(start, end) / 0.08f));
        var first = target.Count == 0 ? 0 : 1;
        for (var i = first; i <= samples; i++)
            Add(target, Vector3.Lerp(start, end, i / (float)samples));
    }

    private static void Add(List<Vector3> target, Vector3 point)
    {
        if (target.Count == 0 ||
            (target[target.Count - 1] - point).sqrMagnitude > 0.000001f)
            target.Add(point);
    }

    private static float TangentAngle(
        float radius,
        float pointAngle,
        bool clockwise,
        bool entering)
    {
        if (radius <= 2.3001f)
            return pointAngle;
        var offset = Mathf.Acos(2.3f / radius);
        return pointAngle + (clockwise == entering ? -offset : offset);
    }

    private static Vector3 Position(char area, int index, bool dZone)
    {
        if (area == 'C')
            return Vector3.zero;
        var angleOffset = area is 'A' or 'B' or 'K'
            ? Mathf.PI * 5f / 8f
            : Mathf.PI * 6f / 8f;
        if (area == 'K' && dZone)
            angleOffset += Mathf.PI / 8f;
        var radius = area switch
        {
            'K' => 4.8f,
            'A' or 'D' => 4.1f,
            'B' => 2.3f,
            'E' => 3f,
            _ => 0f
        };
        var angle = -index * Mathf.PI / 4f + angleOffset;
        return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
    }
}
