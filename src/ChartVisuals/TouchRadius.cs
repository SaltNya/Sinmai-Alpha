using System;
using System.Globalization;

namespace SinmaiAlpha.ChartVisuals;

// A radius changes presentation only. The authored touch area remains the
// physical input area, including after the native reader applies mirroring.
public static class TouchRadius
{
    public const float Maximum = 10f;
    public const float Units = 100f;

    public static bool IsArea(string area) => !string.IsNullOrEmpty(area) &&
        area.Length == 2 && "ABDE".IndexOf(area[0]) >= 0 && area[1] >= '1' && area[1] <= '8';

    public static bool TryParse(string text, out float radius)
    {
        radius = 0;
        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out radius) &&
            !float.IsNaN(radius) && !float.IsInfinity(radius) && radius > 0 && radius <= Maximum;
    }

    public static string Encode(float radius)
    {
        if (!TryParse(radius.ToString("R", CultureInfo.InvariantCulture), out _))
            throw new ArgumentOutOfRangeException(nameof(radius));
        return "TR1|" + radius.ToString("R", CultureInfo.InvariantCulture);
    }

    public static string FormatExpression(float radius)
    {
        // Avoid a negative exponent, which Alpha reads as a slide borrow.
        return "~[" + radius.ToString("0." + new string('#', 46), CultureInfo.InvariantCulture) + "]";
    }

    public static bool Decode(string marker, out float radius)
    {
        radius = 0;
        return marker != null && marker.StartsWith("TR1|", StringComparison.Ordinal) &&
            TryParse(marker.Substring(4), out radius);
    }
}
