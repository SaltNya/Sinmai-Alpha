using System.Collections.Generic;
using SinmaiAlpha.ChartVisuals;
using UnityEngine;

namespace SinmaiAlpha.Notes.Libs;

// Distance sampling follows TrajectoryCarrierDrop, independently of the native
// slide route, sensors, judge queues and score. Coordinates remain reference units.
public sealed class BorrowedPath
{
    private readonly List<Vector3> points = new();
    private readonly List<float> distances = new();
    private float total;
    public BorrowedPath(BorrowedTrajectory route)
    {
        foreach (var p in route.Points) points.Add(new Vector3(p.X, p.Y, 0));
        distances.Add(0);
        for (var i = 1; i < points.Count; i++)
        { total += Vector3.Distance(points[i - 1], points[i]); distances.Add(total); }
    }
    public Vector3 Evaluate(float progress)
    {
        if (points.Count == 0) return Vector3.zero;
        if (points.Count == 1 || total <= .0001f) return points[0];
        var target = Mathf.Clamp01(progress) * total;
        var upper = distances.BinarySearch(target); if (upper < 0) upper = ~upper;
        if (upper <= 0) return points[0];
        if (upper >= points.Count) return points[points.Count - 1];
        var lower = upper - 1; var span = distances[upper] - distances[lower];
        return Vector3.Lerp(points[lower], points[upper], span > .0001f ? (target - distances[lower]) / span : 0);
    }
    public Vector3 Tangent(float progress)
    {
        if (points.Count < 2 || total <= .0001f) return Vector3.right;
        var upper = distances.BinarySearch(Mathf.Clamp01(progress) * total); if (upper < 0) upper = ~upper;
        upper = Mathf.Clamp(upper, 1, points.Count - 1);
        var tangent = points[upper] - points[upper - 1];
        return tangent.sqrMagnitude > .000001f ? tangent : Vector3.right;
    }
}
