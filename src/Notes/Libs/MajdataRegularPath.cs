using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEngine;

namespace SinmaiAlpha.Notes.Libs;

public sealed partial class MajdataRegularPath
{
    public sealed class AreaSpec
    {
        public int Sensor, Partner = -1, HideCount, Group;
        public bool Last, CanSkip = true;
    }
    private sealed class Segment
    {
        public readonly List<Vector3> Positions = new List<Vector3>();
        public readonly List<float> Angles = new List<float>();
        public float Length, Start, Fraction, JudgeProgress;
        public float DisplayLength, DisplayStart, DisplayFraction;
        public int BaseBar, BarCount, LogicalCount;
        public int[] Steps;
    }
    private readonly List<Segment> segments = new List<Segment>();
    public readonly List<Vector3> Bars = new List<Vector3>(); // x/y position, z angle
    public readonly List<bool> BarFlip = new List<bool>();
    public readonly List<AreaSpec> Areas = new List<AreaSpec>();
    public float Length { get; private set; }
    public float JudgeProgress { get; private set; }
    private readonly bool alpha053;
    private readonly bool geometryOnly;

    public MajdataRegularPath(string expression, bool alpha053 = false, bool geometryOnly = false)
    {
        this.alpha053 = alpha053;
        this.geometryOnly = geometryOnly;
        var startMatch = Regex.Match(expression, @"^[1-8]d?");
        var matches = Regex.Matches(expression.Substring(startMatch.Length), alpha053
            ? @"(?<shape>pp|qq|rp|rq|V[1-8]d?|[-<>^vpqszw])(?<end>[1-8]d?)"
            : @"(?<shape>pp|qq|rp|rq|V[1-8]|[-<>^vpqszw])(?<end>[1-8]d?)");
        if (!startMatch.Success || matches.Count == 0 || startMatch.Length + matches.Cast<Match>().Sum(m => m.Length) != expression.Length)
            throw new FormatException("Invalid MD1 slide: " + expression);
        var start = startMatch.Value[0] - '0';
        var startD = startMatch.Value.EndsWith("d", StringComparison.Ordinal);
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var end = m.Groups["end"].Value[0] - '0';
            var endD = (alpha053 || i == matches.Count - 1) && m.Groups["end"].Value.EndsWith("d", StringComparison.Ordinal);
            BuildSegment(start, end, (alpha053 || i == 0) && startD, endD, m.Groups["shape"].Value, matches.Count > 1, i == matches.Count - 1);
            start = end;
            if (alpha053) startD = endD;
        }
        var accumulated = 0f;
        foreach (var segment in segments)
        {
            segment.Start = accumulated / Mathf.Max(0.0001f, Length);
            segment.Fraction = segment.Length / Mathf.Max(0.0001f, Length);
            accumulated += segment.Length;
        }
        var last = segments[segments.Count - 1];
        JudgeProgress = last.Start + last.Fraction * last.JudgeProgress;
        var displayLength = segments.Sum(s => s.DisplayLength);
        accumulated = 0;
        foreach (var segment in segments)
        {
            segment.DisplayStart = accumulated / Mathf.Max(.0001f, displayLength);
            segment.DisplayFraction = segment.DisplayLength / Mathf.Max(.0001f, displayLength);
            accumulated += segment.DisplayLength;
        }
    }

    private static Vector3 KeyPosition(float key)
    {
        var angle = (5f / 8f - key / 4f) * Mathf.PI;
        return new Vector3(Mathf.Cos(angle), Mathf.Sin(angle)) * 4.8f;
    }
    private static Vector3 Rotate(Vector3 p, float angle)
    {
        var r = angle * Mathf.Deg2Rad;
        return new Vector3(p.x * Mathf.Cos(r) - p.y * Mathf.Sin(r), p.x * Mathf.Sin(r) + p.y * Mathf.Cos(r));
    }
    // Same transform order as v0.5.3 JsonDataLoader.TryGetSlideVisualRoute.
    internal static Vector3[] GetTangentTemplate(int startKey, int endKey, string shape)
    {
        var name = detectShapeFromText(startKey + shape.Replace("d", "") + endKey);
        var mirror = name.StartsWith("-", StringComparison.Ordinal);
        if (mirror) name = name.Substring(1);
        var reverse = name.StartsWith("r", StringComparison.Ordinal);
        if (reverse) name = name.Substring(1);
        if (!Prefabs.TryGetValue(name, out var prefab)) throw new FormatException("Unsupported tangent template: " + name);
        var rotationPosition = reverse ? endKey : startKey;
        var rotation = -45f * (mirror ? rotationPosition : rotationPosition - 1);
        var result = new Vector3[prefab.Length + 2];
        result[0] = KeyPosition(startKey);
        result[result.Length - 1] = KeyPosition(endKey);
        for (var i = 0; i < prefab.Length; i++)
        {
            var bar = prefab[reverse ? prefab.Length - 1 - i : i];
            result[i + 1] = Rotate(new Vector3(bar.x * (mirror ? -1f : 1f), bar.y), rotation);
        }
        return result;
    }

    private void BuildSegment(int startKey, int endKey, bool startD, bool endD, string shape, bool connected, bool final)
    {
        Vector3 Tangent(IReadOnlyList<Vector3> points, IReadOnlyList<float> lengths, float distance) => alpha053
            ? MajdataDZoneGeometry.SamplePolylineTangent(points, lengths, distance)
            : SamplePolylineTangent(points, lengths, distance);
        var name = detectShapeFromText(startKey + shape.Replace("d", "") + endKey);
        var mirror = name.StartsWith("-", StringComparison.Ordinal);
        if (mirror) name = name.Substring(1);
        var reverse = name.StartsWith("r", StringComparison.Ordinal);
        if (reverse) name = name.Substring(1);
        if (!Prefabs.TryGetValue(name, out var prefab)) throw new FormatException("Unsupported MD1 prefab: " + name);
        var physicalStart = reverse ? endKey : startKey;
        var physicalEnd = reverse ? startKey : endKey;
        var physicalStartD = reverse ? endD : startD;
        var physicalEndD = reverse ? startD : endD;
        var rotation = -45f * (mirror ? physicalStart : physicalStart - 1);
        var original = new Vector3[prefab.Length + 2];
        var rotations = new float[prefab.Length];
        original[0] = KeyPosition(physicalStart);
        original[original.Length - 1] = KeyPosition(physicalEnd);
        for (var i = 0; i < prefab.Length; i++)
        {
            original[i + 1] = Rotate(new Vector3(prefab[i].x * (mirror ? -1f : 1f), prefab[i].y), rotation);
            rotations[i] = prefab[i].z + rotation;
        }
        var oldDistances = BuildCumulativeDistances(original);
        var oldLength = oldDistances[oldDistances.Length - 1];
        var start = KeyPosition(physicalStart - (physicalStartD ? .5f : 0f));
        var end = KeyPosition(physicalEnd - (physicalEndD ? .5f : 0f));
        var deformed = original;
        var hasD = physicalStartD || physicalEndD;
        var tangentRotation = alpha053 && hasD && (name.StartsWith("v", StringComparison.Ordinal)
            || name.StartsWith("L", StringComparison.Ordinal) || name == "s" || name.StartsWith("circle", StringComparison.Ordinal));
        var firstTangent = Tangent(original, oldDistances, oldLength / (prefab.Length + 1f));
        var spriteOffset = Mathf.DeltaAngle(Mathf.Atan2(firstTangent.y, firstTangent.x) * Mathf.Rad2Deg,
            alpha053 ? Quaternion.Euler(0f, 0f, rotations[0]).eulerAngles.z : rotations[0]);
        if (hasD)
        {
            if (alpha053) deformed = BuildAlphaDRoute(name, shape, original, start, end);
            else if (name.StartsWith("line", StringComparison.Ordinal)) deformed = BuildStraightRoute(original.Length, start, end);
            else if (name.StartsWith("circle", StringComparison.Ordinal)) deformed = BuildOuterCircleRoute(original, start, end);
            else if (name == "s") deformed = BuildAnchoredMiddleRoute(original, start, end);
            else if (name.IndexOf("pq", StringComparison.Ordinal) >= 0) deformed = BuildTangentCircleRoute(original, start, end, name.StartsWith("pq", StringComparison.Ordinal));
            else
            {
                deformed = new Vector3[original.Length];
                for (var i = 0; i < original.Length; i++)
                {
                    var t = oldLength > .0001f ? oldDistances[i] / oldLength : 0f;
                    deformed[i] = original[i] + Vector3.Lerp(start - original[0], end - original[original.Length - 1], t*t*(3f-2f*t));
                }
            }
            deformed[0] = start;
            deformed[deformed.Length - 1] = end;
        }
        var distances = BuildCumulativeDistances(deformed);
        var length = distances[distances.Length - 1];
        var segment = new Segment();
        segment.Positions.Add(start);
        var logicalBars = new List<Vector3>();
        for (var i = 0; i < prefab.Length; i++)
        {
            var progress = (i + 1f) / (prefab.Length + 1f);
            var point = hasD ? SamplePolyline(deformed, distances, progress * length) : original[i + 1];
            var delta = hasD ? Vector2.SignedAngle(Tangent(original, oldDistances, progress * oldLength), Tangent(deformed, distances, progress * length)) : 0f;
            logicalBars.Add(point);
            segment.Positions.Add(point);
            var newTangent = Tangent(deformed, distances, progress * length);
            var spriteAngle = (tangentRotation
                ? Mathf.Atan2(newTangent.y, newTangent.x) * Mathf.Rad2Deg + spriteOffset
                : rotations[i] + delta);
            // Match SlideDrop's quaternion/Euler round trip before AppendRouteEnd
            // reads the last rotation delta, including near-zero deltas on lines.
            var spriteRotation = tangentRotation
                ? Quaternion.Euler(0f, 0f, spriteAngle)
                : Quaternion.AngleAxis(delta, Vector3.forward) * Quaternion.Euler(0f, 0f, rotations[i]);
            segment.Angles.Add(alpha053 && hasD
                ? Quaternion.Euler(spriteRotation.eulerAngles + new Vector3(0f, 0f, 18f)).eulerAngles.z
                : spriteAngle + 18f);
        }
        // SlideDrop.AppendRouteEnd, including the sign of the last rotation delta.
        var previous = segment.Positions[segment.Positions.Count - 1];
        var denominator = previous.magnitude * end.magnitude;
        var tailAngle = Mathf.Acos(denominator > .0001f ? Mathf.Clamp(Vector3.Dot(previous, end) / denominator, -1f, 1f) : 1f) * Mathf.Rad2Deg;
        if (segment.Angles.Count >= 2 && Mathf.DeltaAngle(segment.Angles[segment.Angles.Count - 2], segment.Angles[segment.Angles.Count - 1]) < 0f) tailAngle = -tailAngle;
        segment.Positions.Add(end);
        segment.Angles.Add(segment.Angles[segment.Angles.Count - 1] + tailAngle);
        var visualCount = hasD && oldLength > .0001f ? Mathf.Clamp(Mathf.RoundToInt(length / (oldLength / (prefab.Length + 1f))) - 1, 1, 96) : prefab.Length;
        var visual = new List<Vector3>();
        for (var i = 0; i < visualCount; i++)
        {
            var progress = (i + 1f) / (visualCount + 1f);
            var sourceIndex = Mathf.Clamp(Mathf.RoundToInt(progress * (prefab.Length + 1f)) - 1, 0, prefab.Length - 1);
            var point = hasD ? SamplePolyline(deformed, distances, progress * length) : original[i + 1];
            var delta = hasD ? Vector2.SignedAngle(Tangent(original, oldDistances, progress * oldLength), Tangent(deformed, distances, progress * length)) : 0f;
            var newTangent = Tangent(deformed, distances, progress * length);
            point.z = tangentRotation ? Mathf.Atan2(newTangent.y, newTangent.x) * Mathf.Rad2Deg + spriteOffset
                : rotations[sourceIndex] + delta;
            visual.Add(point);
        }
        if (geometryOnly)
        {
            // Native play imports only the curve. Sensor ordering, hide counts
            // and grade timing are generated by the mod/native SlideRoot.
            if (reverse)
            {
                segment.Positions.Reverse();
                segment.Angles.Reverse();
            }
            var displayFlip = name.StartsWith("pq", StringComparison.Ordinal) && !name.StartsWith("pp", StringComparison.Ordinal)
                ? mirror == (name == "pq7" || name == "pq8") : mirror;
            if (displayFlip) for (var i = 0; i < segment.Angles.Count; i++) segment.Angles[i] += 180f;
            for (var i = 0; i < segment.Positions.Count - 1; i++)
                segment.Length += Vector3.Distance(segment.Positions[i], segment.Positions[i + 1]);
            // SlideDrop.GetSlideLength excludes the final bar-to-endpoint edge.
            // Keep this display clock separate from the native route's full length.
            for (var i = 0; i < segment.Positions.Count - 2; i++)
                segment.DisplayLength += Vector3.Distance(segment.Positions[i], segment.Positions[i + 1]);
            Length += segment.Length;
            segments.Add(segment);
            return;
        }
        var sensors = new List<int>();
        foreach (var point in logicalBars)
        foreach (var sensor in MajdataSlidePath.Sensors)
        {
            var id = (int)sensor.w;
            if (startD == endD && (startD ? id < 16 : id >= 17)) continue;
            var radius = sensor.z - .15f;
            if ((point - new Vector3(sensor.x, sensor.y)).sqrMagnitude <= radius*radius &&
                (sensors.Count == 0 || sensors[sensors.Count - 1] != id))
            { sensors.Add(id); break; }
        }
        if (reverse) { segment.Positions.Reverse(); segment.Angles.Reverse(); visual.Reverse(); sensors.Reverse(); }
        bool specialFlip = name.StartsWith("pq", StringComparison.Ordinal) && !name.StartsWith("pp", StringComparison.Ordinal)
            ? mirror == (name == "pq7" || name == "pq8") : mirror;
        if (specialFlip) for (var i = 0; i < segment.Angles.Count; i++) segment.Angles[i] += 180f;
        var baseBar = Bars.Count;
        segment.BaseBar = baseBar; segment.BarCount = visualCount; segment.LogicalCount = prefab.Length; segment.Steps = AreaSteps[name];
        Bars.AddRange(visual);
        for (var i = 0; i < visual.Count; i++) BarFlip.Add(mirror ^ reverse);
        if (sensors.Count == 0) throw new FormatException("MD1 route has no sensors: " + startKey + shape + endKey);
        var steps = AreaSteps[name];
        for (var i = 0; i < sensors.Count; i++)
        {
            var logicalHide = steps.Length - 1 == sensors.Count ? steps[i + 1] : (prefab.Length / sensors.Count) * (i + 1);
            var area = new AreaSpec { Group = segments.Count, Sensor = sensors[i], Last = final && i == sensors.Count - 1,
                HideCount = baseBar + Mathf.CeilToInt(Mathf.Clamp(logicalHide, 0, prefab.Length) * visualCount / (float)prefab.Length) };
            if ((i == 1 && (name == "line3" || name == "line7" || name == "circle3" || name.StartsWith("L", StringComparison.Ordinal))) || (i == 3 && name == "L5"))
            {
                area.CanSkip = connected;
                if (name != "circle3" && area.Sensor < 8) area.Partner = area.Sensor + 8;
            }
            Areas.Add(area);
        }
        for (var i = 0; i < segment.Positions.Count - 2; i++) segment.Length += Vector3.Distance(segment.Positions[i], segment.Positions[i + 1]);
        segment.JudgeProgress = .9f;
        var lastSensor = MajdataSlidePath.Sensors.First(s => (int)s.w == sensors[sensors.Count - 1]);
        for (var progress = .85f; progress < 1f; progress += .01f)
        {
            var point = SampleSegment(segment, progress);
            var radius = lastSensor.z - .15f;
            if ((point - new Vector3(lastSensor.x, lastSensor.y)).sqrMagnitude <= radius*radius + .763736616f*.763736616f)
            { segment.JudgeProgress = progress; break; }
        }
        Length += segment.Length;
        segments.Add(segment);
    }
    private static Vector3[] BuildAlphaDRoute(string name, string shape, Vector3[] original, Vector3 start, Vector3 end)
    {
        if (name.StartsWith("line", StringComparison.Ordinal)) return MajdataDZoneGeometry.BuildStraightRoute(original.Length, start, end);
        if (name.StartsWith("circle", StringComparison.Ordinal)) return MajdataDZoneGeometry.BuildOuterCircleRoute(original, start, end);
        if (name.StartsWith("v", StringComparison.Ordinal)) return MajdataDZoneGeometry.BuildViaPointRoute(original.Length, start, Vector3.zero, end);
        if (name.StartsWith("L", StringComparison.Ordinal)) return shape.StartsWith("V", StringComparison.Ordinal)
            ? MajdataDZoneGeometry.BuildViaPointRoute(original.Length, start, KeyPosition(shape[1] - '0' - (shape.EndsWith("d", StringComparison.Ordinal) ? .5f : 0f)), end)
            : MajdataDZoneGeometry.BuildAnchoredMiddleRoute(original, start, end);
        if (name == "s") return MajdataDZoneGeometry.BuildLightningRoute(original, start, end);
        if (name.IndexOf("pq", StringComparison.Ordinal) >= 0)
            return MajdataDZoneGeometry.BuildTangentCircleRoute(original, start, end, name.StartsWith("pq", StringComparison.Ordinal));
        var distances = BuildCumulativeDistances(original);
        var length = distances[distances.Length - 1];
        return original.Select((p, i) =>
        {
            var t = length > .0001f ? distances[i] / length : 0f;
            return p + Vector3.Lerp(start - original[0], end - original[original.Length - 1], t * t * (3f - 2f * t));
        }).ToArray();
    }
    private static Vector3 SampleSegment(Segment segment, float progress)
    {
        var indexProgress = (segment.Positions.Count - 1) * Mathf.Clamp01(progress);
        var index = Mathf.Min((int)indexProgress, segment.Positions.Count - 2);
        return Vector3.Lerp(segment.Positions[index], segment.Positions[index + 1], indexProgress - index);
    }
    private Segment At(float progress, out float local)
    {
        var segment = segments[segments.Count - 1];
        foreach (var candidate in segments) if (progress < candidate.Start + candidate.Fraction) { segment = candidate; break; }
        local = Mathf.Clamp01((progress - segment.Start) / Mathf.Max(.0001f, segment.Fraction));
        return segment;
    }
    // Each connected SlideDrop normalizes SV over its own length-weighted time range.
    public float Progress(float start, float duration, float now, Func<float, float, float, float> progress)
    {
        var segment = At(Mathf.Clamp01((now - start) / duration), out _);
        return segment.Start + segment.Fraction * progress(start + duration * segment.Start, duration * segment.Fraction, now);
    }
    public float JudgeTime(float start, float duration, bool hasCurve, Func<float, float, float, float> progress)
    {
        var segment = segments[segments.Count - 1];
        var localStart = start + duration * segment.Start;
        var localDuration = duration * segment.Fraction;
        var best = segment.JudgeProgress;
        if (hasCurve)
        {
            var bestError = float.MaxValue;
            for (var i = 0; i <= 128; i++)
            {
                var time = i / 128f;
                var error = Mathf.Abs(progress(localStart, localDuration, localStart + localDuration * time) - segment.JudgeProgress);
                if (error < bestError) { bestError = error; best = time; }
            }
        }
        return localStart + localDuration * best;
    }
    public int AutoHiddenBars(float progress)
    {
        if (progress >= 1f) return Bars.Count;
        var segment = At(progress, out var local);
        var step = segment.Steps[Mathf.Min((int)(local * (segment.Steps.Length - 1)), segment.Steps.Length - 1)];
        return segment.BaseBar + Mathf.CeilToInt(Mathf.Clamp(step, 0, segment.LogicalCount) * segment.BarCount / (float)segment.LogicalCount);
    }
    public Vector3 EvaluatePath(float progress) { var segment = At(progress, out var local); return SampleSegment(segment, local); }
    public Vector3 DisplayPose(float start, float duration, float now, Func<float, float, float, float> progress)
    {
        if (!geometryOnly) throw new InvalidOperationException("Display pose requires geometry-only data.");
        var clock = duration > 0 ? Mathf.Clamp01((now - start) / duration) : 1;
        var segment = segments[segments.Count - 1];
        foreach (var candidate in segments)
            if (clock < candidate.DisplayStart + candidate.DisplayFraction) { segment = candidate; break; }
        var localStart = start + duration * segment.DisplayStart;
        var localDuration = duration * segment.DisplayFraction;
        var local = now <= start ? 0 : Mathf.Clamp01(progress(localStart, localDuration, now));
        var point = SampleSegment(segment, local);
        var indexProgress = (segment.Positions.Count - 1) * local;
        var index = Mathf.Min((int)indexProgress, segment.Angles.Count - 1);
        point.z = index == segment.Angles.Count - 1 ? segment.Angles[index] :
            Mathf.LerpAngle(segment.Angles[index], segment.Angles[index + 1], indexProgress - index);
        return point;
    }
    public float EvaluateAngle(float progress)
    {
        var segment = At(progress, out var local);
        var indexProgress = (segment.Positions.Count - 1) * local;
        var index = Mathf.Min((int)indexProgress, segment.Angles.Count - 1);
        return index == segment.Angles.Count - 1 ? segment.Angles[index] : Mathf.LerpAngle(segment.Angles[index], segment.Angles[index + 1], indexProgress - index);
    }
}
