using System;
using System.Globalization;

namespace SinmaiAlpha.ChartVisuals
{
    // Stored as a composition record: never registered as a judged NoteData.
    public readonly struct NoiseZoneSpec
    {
        public readonly int Sensor;
        public readonly double Duration;
        public readonly float Hs;
        public NoiseZoneSpec(int sensor, double duration, float hs)
        { Sensor = sensor; Duration = duration; Hs = hs; }
        public string Encode() => "NZ1|" + Sensor.ToString(CultureInfo.InvariantCulture) + "|" +
            Duration.ToString("R", CultureInfo.InvariantCulture) + "|" + Hs.ToString("R", CultureInfo.InvariantCulture);
        public static bool TryDecode(string text, out NoiseZoneSpec value)
        {
            value = default;
            if (text == null) return false;
            var p = text.Split('|');
            if (p.Length != 4 || p[0] != "NZ1" || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var sensor) ||
                sensor < 0 || sensor >= 33 || !double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) ||
                double.IsNaN(duration) || double.IsInfinity(duration) || duration < 0 ||
                !TryMultiplier(p[3], out var hs)) return false;
            value = new NoiseZoneSpec(sensor, duration, hs); return true;
        }
        public static bool TryMultiplier(string text, out float value)
        {
            value = 1;
            if (text == null) return false;
            text = text.Trim();
            if (text.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return true;
            var p = text.Split(':');
            if (p.Length > 2 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value)) return false;
            if (p.Length == 2)
            {
                if (!float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) || denominator == 0) return false;
                value /= denominator;
            }
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
        // Native C1 and C2 share one logical noise region. D/E follow both C inputs.
        public static int NativeSensor(int area) => area < 0 || area >= 34 ? -1 : area <= 16 ? area : area == 17 ? 16 : area - 1;
    }
}
