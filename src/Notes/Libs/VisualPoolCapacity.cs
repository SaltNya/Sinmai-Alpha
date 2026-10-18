using System;
using System.Collections.Generic;

namespace SinmaiAlpha.Notes.Libs;

internal static class VisualPoolCapacity
{
    // Keep 25% spare (at least 32), rounded to blocks of 128. No global
    // maximum: a chart with >1024 concurrent carriers gets what it needs.
    internal static int Reserve(int peak)
    {
        if (peak <= 0) return 0;
        return checked((peak + Math.Max(32, (peak + 3) / 4) + 127) / 128 * 128);
    }

    // Reserve concurrently leased carriers, not the total number in the song.
    internal static int Peak(List<(float Time, int Change)> events)
    {
        events.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : b.Change.CompareTo(a.Change));
        var active = 0;
        var peak = 0;
        foreach (var e in events) { active += e.Change; peak = Math.Max(peak, active); }
        return peak;
    }
}
