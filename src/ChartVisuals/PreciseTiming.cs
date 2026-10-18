using System;
using System.Globalization;

namespace SinmaiAlpha.ChartVisuals;

// Optional presentation clock. Native MA2 bars/ticks and judgment stay intact.
public static class PreciseTiming
{
    public static string Encode(double milliseconds, string value)
        => "PT1|" + milliseconds.ToString("R", CultureInfo.InvariantCulture) + "|" + value;

    public static double Decode(ref string value, double fallback, double adjustment = 0)
    {
        if (value == null || !value.StartsWith("PT1|", StringComparison.Ordinal)) return fallback;
        var separator = value.IndexOf('|', 4);
        if (separator < 0 || !double.TryParse(value.Substring(4, separator - 4), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var time) || double.IsNaN(time) || double.IsInfinity(time))
            throw new FormatException("Invalid precise presentation time");
        value = value.Substring(separator + 1);
        return time + adjustment;
    }
}
