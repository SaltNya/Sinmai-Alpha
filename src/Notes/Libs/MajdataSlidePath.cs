using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SinmaiAlpha.Notes.Libs;

// Geometry and ordered sensor sampling ported from MajdataViewAlpha TouchSlideDrop.
// Sensor circles below are from its SampleScene, including the 0.15 world-unit margin.
public sealed class MajdataSlidePath
{
    private readonly List<Vector3> path = new List<Vector3>();
    private readonly List<float> pathDistances = new List<float>();
    private readonly string pathExpression;
    private readonly bool alpha053;
    private float totalPathLength;
    public readonly List<int> SensorRoute = new List<int>();
    public float Length => totalPathLength;
    internal IReadOnlyList<Vector3> GeometryPoints => path;
    internal IReadOnlyList<float> GeometryDistances => pathDistances;
    public const float BarSpacing = 0.452604050f;
    public const float ReferenceRotation = 178.337633399f;
    public static readonly Vector4[] Sensors = {
        new Vector4(1.626505436f, 3.938352792f, 1.138251579f, 0),
        new Vector4(3.944851724f, 1.621483534f, 1.134123048f, 1),
        new Vector4(3.947510378f, -1.619415692f, 1.137924982f, 2),
        new Vector4(1.611144324f, -3.936491734f, 1.102744532f, 3),
        new Vector4(-1.640389518f, -3.940125228f, 1.159472012f, 4),
        new Vector4(-3.945147130f, -1.621483534f, 1.152647007f, 5),
        new Vector4(-3.936284950f, 1.643048172f, 1.140026932f, 6),
        new Vector4(-1.652501164f, 3.948101190f, 1.147923900f, 7),
        new Vector4(0.812810052f, 1.961968785f, 0.824066192f, 8),
        new Vector4(1.961968490f, 0.812809609f, 0.824066097f, 9),
        new Vector4(1.962057111f, -0.812573284f, 0.824066097f, 10),
        new Vector4(0.812809609f, -1.961968490f, 0.824066097f, 11),
        new Vector4(-0.812573284f, -1.962057111f, 0.824066097f, 12),
        new Vector4(-1.961968490f, -0.812809609f, 0.824066097f, 13),
        new Vector4(-1.962057111f, 0.812573284f, 0.824066097f, 14),
        new Vector4(-0.812809609f, 1.961968490f, 0.824066097f, 15),
        new Vector4(-0.000000000f, -0.001874381f, 1.366378147f, 16),
        new Vector4(0.000001793f, 4.430203782f, 1.125827933f, 17),
        new Vector4(3.132485224f, 3.132485224f, 1.125827933f, 18),
        new Vector4(4.430203782f, -0.000001803f, 1.125827933f, 19),
        new Vector4(3.132485224f, -3.132485224f, 1.125827933f, 20),
        new Vector4(0.000001803f, -4.430203782f, 1.125827933f, 21),
        new Vector4(-3.132485224f, -3.132485224f, 1.125827933f, 22),
        new Vector4(-4.430201123f, -0.000001803f, 1.125827933f, 23),
        new Vector4(-3.132485224f, 3.132485224f, 1.125827933f, 24),
        new Vector4(0.000001803f, 3.071926994f, 0.665288502f, 25),
        new Vector4(2.172179399f, 2.172179399f, 0.665288502f, 26),
        new Vector4(3.071926994f, -0.000001803f, 0.665288502f, 27),
        new Vector4(2.172179399f, -2.172179399f, 0.665288502f, 28),
        new Vector4(0.000001803f, -3.071926994f, 0.665288502f, 29),
        new Vector4(-2.172179399f, -2.172179399f, 0.665288502f, 30),
        new Vector4(-3.071926994f, -0.000001803f, 0.665288502f, 31),
        new Vector4(-2.172179399f, 2.172179399f, 0.665288502f, 32),
    };

    public MajdataSlidePath(string expression, bool alpha053 = false, bool geometryOnly = false)
    {
        pathExpression = expression;
        this.alpha053 = alpha053;
        if (!alpha053 && Regex.IsMatch(expression, @"V[1-8]d"))
            throw new FormatException("D-zone V turns require MV2: " + expression);
        if (!TryBuildExpressionPath()) throw new FormatException("Invalid Majdata slide expression: " + expression);
        pathDistances.Add(0f);
        for (var i = 1; i < path.Count; i++)
        {
            totalPathLength += Vector3.Distance(path[i - 1], path[i]);
            pathDistances.Add(totalPathLength);
        }
        if (geometryOnly) return; // native adapters never consume preview sensor/judge data
        foreach (var point in path)
        {
            var nearest = -1;
            var nearestDistance = float.MaxValue;
            foreach (var sensor in Sensors)
            {
                var distance = (point - new Vector3(sensor.x, sensor.y)).sqrMagnitude;
                if (distance <= sensor.z * sensor.z && distance < nearestDistance)
                {
                    nearest = (int)sensor.w;
                    nearestDistance = distance;
                }
            }
            if (nearest >= 0 && (SensorRoute.Count == 0 || SensorRoute[SensorRoute.Count - 1] != nearest))
                SensorRoute.Add(nearest);
        }
        var nodes = Regex.Matches(expression, @"[ABDE][1-8]|C1?|[1-8]d?");
        var start = SensorFor(nodes[0].Value);
        var end = SensorFor(nodes[nodes.Count - 1].Value);
        if (SensorRoute.Count == 0 || SensorRoute[0] != start) SensorRoute.Insert(0, start);
        if (SensorRoute[SensorRoute.Count - 1] != end) SensorRoute.Add(end);
    }

    private static int SensorFor(string value)
    {
        var node = ParseAreaPosition(value);
        var group = node.Area == 'K' ? (node.IsDZone ? 'D' : 'A') : node.Area;
        return group == 'C' ? 16 : (group == 'A' ? 0 : group == 'B' ? 8 : group == 'D' ? 17 : 25) + node.Position - 1;
    }
    private bool TryBuildExpressionPath()
    {
        if (string.IsNullOrWhiteSpace(pathExpression))
            return false;

        var durationIndex = pathExpression.IndexOf('[');
        var source = durationIndex >= 0
            ? pathExpression.Substring(0, durationIndex)
            : pathExpression;
        var match = Regex.Match(
            source,
            @"^(?<start>(?:[1-8]d?|[ABDE][1-8]|C[12]?))[bxfm!?]*(?<segments>(?:(?:pp|qq|rp|rq|V[1-8]d?|[PQ](?:[0-9]|[ABDE][1-8]|C[12]?)|<+|>+|[-^vVwWsqzp])(?:[1-8]d?|[ABDE][1-8]|C[12]?)[bxfm]*)+)$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
            return false;

        var current = ParseAreaPosition(match.Groups["start"].Value);
        var segments = Regex.Matches(
            match.Groups["segments"].Value,
            @"(?<shape>pp|qq|rp|rq|V[1-8]d?|[PQ](?:[0-9]|[ABDE][1-8]|C[12]?)|<+|>+|[-^vVwWsqzp])(?<end>(?:[1-8]d?|[ABDE][1-8]|C[12]?))[bxfm]*",
            RegexOptions.CultureInvariant);
        foreach (Match segment in segments)
        {
            var next = ParseAreaPosition(segment.Groups["end"].Value);
            AppendSegment(
                current.Area,
                current.Position,
                current.IsDZone,
                next.Area,
                next.Position,
                next.IsDZone,
                segment.Groups["shape"].Value);
            current = next;
        }
        if (alpha053 && path.Count == 1)
        {
            // v0.5.3 BuildPath falls back to the first shape when the expression
            // samples only one point (e.g. an outer-orbit P/Q returning to itself).
            var head = ParseAreaPosition(match.Groups["start"].Value);
            AppendSegment(head.Area, head.Position, head.IsDZone,
                current.Area, current.Position, current.IsDZone,
                segments[0].Groups["shape"].Value.Substring(0, 1));
        }
        return path.Count >= 2;
    }

    private static (char Area, int Position, bool IsDZone) ParseAreaPosition(string value)
    {
        return char.IsDigit(value[0])
            ? ('K', value[0] - '0', value.EndsWith("d", StringComparison.Ordinal))
            : value[0] == 'C'
            ? ('C', 8, false)
            : (value[0], value[1] - '0', false);
    }

    private void AppendSegment(
        char segmentStartArea,
        int segmentStartPosition,
        bool segmentStartIsDZone,
        char segmentEndArea,
        int segmentEndPosition,
        bool segmentEndIsDZone,
        string segmentShape)
    {
        var loopCount = alpha053 && (segmentShape[0] == '<' || segmentShape[0] == '>') ? segmentShape.Length : 1;
        var segments = 256 * loopCount;
        var start = AreaPosition(segmentStartArea, segmentStartPosition, segmentStartIsDZone);
        var end = AreaPosition(segmentEndArea, segmentEndPosition, segmentEndIsDZone);
        var firstSample = path.Count == 0 ? 0 : 1;
        if (alpha053 && segmentShape.Length > 1 && (segmentShape[0] == 'P' || segmentShape[0] == 'Q'))
        {
            var selector = segmentShape.Substring(1);
            var number = selector.Length == 1 && char.IsDigit(selector[0]);
            var orbit = number
                ? (Area: selector == "0" ? 'C' : selector == "9" ? 'O' : 'B', Position: selector[0] - '0', IsDZone: false)
                : ParseAreaPosition(selector);
            SelectableOrbitPathGeometry.Append(path,
                segmentStartArea, segmentStartPosition, segmentStartIsDZone,
                orbit.Area, orbit.Position, orbit.IsDZone, number,
                segmentEndArea, segmentEndPosition, segmentEndIsDZone,
                segmentShape.Substring(0, 1));
            return;
        }
        if (alpha053 && (segmentShape.Length == 2 || segmentShape.Length == 3) && segmentShape[0] == 'V')
        {
            var middle = segmentShape[1] - '0';
            var middleD = segmentShape.EndsWith("d", StringComparison.Ordinal);
            AppendSegment(segmentStartArea, segmentStartPosition, segmentStartIsDZone, 'K', middle, middleD, "-");
            AppendSegment('K', middle, middleD, segmentEndArea, segmentEndPosition, segmentEndIsDZone, "-");
            return;
        }
        if (segmentShape == "-")
        {
            for (var i = firstSample; i <= segments; i++)
                path.Add(Vector3.Lerp(start, end, i / (float)segments));
            return;
        }

        if (segmentShape is "v" or "V")
        {
            // 折线：v 经中心折返；V 经起点与终点的中间点折返
            var mid = segmentShape == "v" ? Vector3.zero : (start + end) * 0.5f;
            for (var i = firstSample; i <= segments; i++)
            {
                var t = i / (float)segments;
                path.Add(t < 0.5f
                    ? Vector3.Lerp(start, mid, t * 2f)
                    : Vector3.Lerp(mid, end, (t - 0.5f) * 2f));
            }
            return;
        }

        if (alpha053 && (segmentShape is "p" or "q" or "pp" or "qq" or "rp" or "rq"))
        {
            var original = MajdataRegularPath.GetTangentTemplate(segmentStartPosition, segmentEndPosition, segmentShape);
            var route = MajdataTangentPath.Build(original, start, end, segmentShape == "p" || segmentShape == "q");
            for (var i = firstSample; i < route.Length; i++) path.Add(route[i]);
            return;
        }

        if (loopCount == 1 && (segmentStartArea == 'C' || segmentEndArea == 'C'))
        {
            // 二次贝塞尔（中心/起点、终点之间的控制点偏置）
            var direction = alpha053 ? (segmentShape[0] == '<' ? -1f : 1f) :
                segmentShape is "<" or "p" or "pp" or "rq" or "s" or "w" ? -1f : 1f;
            var delta = end - start;
            var control = (start + end) * 0.5f +
                          new Vector3(-delta.y, delta.x) * (0.35f * direction);
            for (var i = firstSample; i <= segments; i++)
            {
                var t = i / (float)segments;
                var inverse = 1f - t;
                path.Add(inverse * inverse * start +
                         2f * inverse * t * control +
                         t * t * end);
            }
            return;
        }

        // The deployed MajdataView ResolveAngleDelta uses each segment's start key
        // for < and > (upper half: 7,8,1,2). Its released binary differs from the
        // supplied source snapshot. Center Bezier segments above keep their own rule.
        var startAngle = Mathf.Atan2(start.y, start.x);
        var endAngle = Mathf.Atan2(end.y, end.x);
        var clockwise = -Mathf.Repeat(startAngle - endAngle, Mathf.PI * 2f);
        var counterclockwise = Mathf.Repeat(endAngle - startAngle, Mathf.PI * 2f);
        var upperHalf = segmentStartPosition == 7 || segmentStartPosition == 8 ||
                        segmentStartPosition == 1 || segmentStartPosition == 2;
        var deltaAngle = segmentShape == "<" ? (upperHalf ? counterclockwise : clockwise)
            : segmentShape == ">" ? (upperHalf ? clockwise : counterclockwise)
            : segmentShape is "^"
            ? (Mathf.Abs(clockwise) <= Mathf.Abs(counterclockwise) ? clockwise : counterclockwise)
            : segmentShape is "<" or "p" or "pp" or "rq" or "s" or "w"
                ? counterclockwise
                : clockwise;
        var startRadius = start.magnitude;
        var endRadius = end.magnitude;
        if (alpha053)
        {
            deltaAngle = (float)TouchSlideDirection.Sweep(startAngle, endAngle, segmentStartPosition, segmentShape[0]);
            if (loopCount > 1) deltaAngle += Mathf.Sign(deltaAngle) * Mathf.PI * 2f * (loopCount - 1);
        }
        for (var i = firstSample; i <= segments; i++)
        {
            var t = i / (float)segments;
            var angle = startAngle + deltaAngle * t;
            var radiusProgress = alpha053 ? t : Mathf.SmoothStep(0f, 1f, t);
            var radius = Mathf.Lerp(startRadius, endRadius, radiusProgress);
            path.Add(new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * radius);
        }
    }

    public Vector3 EvaluatePath(float progress)
    {
        if (path.Count == 0)
            return Vector3.zero;
        if (path.Count == 1 || totalPathLength <= 0.0001f)
            return path[0];

        var target = Mathf.Clamp01(progress) * totalPathLength;
        var upper = pathDistances.BinarySearch(target);
        if (upper >= 0)
            return path[upper];
        upper = ~upper;
        if (upper <= 0)
            return path[0];
        if (upper >= path.Count)
            return path[path.Count - 1];
        var lower = upper - 1;
        var span = pathDistances[upper] - pathDistances[lower];
        var amount = span <= 0.0001f
            ? 0f
            : (target - pathDistances[lower]) / span;
        return Vector3.Lerp(path[lower], path[upper], amount);
    }

    public Vector3 EvaluateTangent(float progress)
    {
        const float sample = 0.01f;
        return EvaluatePath(Mathf.Min(1f, progress + sample)) -
               EvaluatePath(Mathf.Max(0f, progress - sample));
    }

    private static Vector3 AreaPosition(char area, int index, bool dZone)
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
