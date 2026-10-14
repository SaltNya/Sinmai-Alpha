using System;
using System.Globalization;

namespace SinmaiAlpha.Hosting;

// Fixed-size histogram; no per-frame allocations or sorting.
internal sealed class FrameStatistics
{
    private readonly long[] buckets = new long[1002];
    internal long Count, Over25, Over50, Over100;
    internal double Sum, Max;
    internal void Add(double ms)
    {
        if (double.IsNaN(ms) || double.IsInfinity(ms) || ms < 0) return;
        Count++; Sum += ms; Max = Math.Max(Max, ms);
        buckets[(int)Math.Min(1001, Math.Ceiling(ms))]++;
        if (ms > 25) Over25++;
        if (ms > 50) Over50++;
        if (ms > 100) Over100++;
    }
    internal int Percentile(double fraction)
    {
        if (Count == 0) return 0;
        long target = (long)Math.Ceiling(Count * fraction), n = 0;
        for (int i = 0; i < buckets.Length; i++) { n += buckets[i]; if (n >= target) return i; }
        return 1001;
    }
    internal void Clear() { Array.Clear(buckets, 0, buckets.Length); Count = Over25 = Over50 = Over100 = 0; Sum = Max = 0; }
    internal string Describe() => string.Format(CultureInfo.InvariantCulture,
        "frames={0} avgMs={1:F2} maxMs={2:F2} p95MsCeil={3} p99MsCeil={4} over25={5} over50={6} over100={7}",
        Count, Count == 0 ? 0 : Sum / Count, Max, Percentile(.95), Percentile(.99), Over25, Over50, Over100);
}
