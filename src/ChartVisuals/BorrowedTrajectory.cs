using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
#nullable disable
namespace SinmaiAlpha.ChartVisuals;

// A Fake carrier borrows a polyline, never sensors or judge state. Points use
// the reference's 4.8-unit coordinate system; native display uses 100 units/unit.
public sealed class BorrowedTrajectory
{
    public struct Point
    {
        public float X, Y;
        public Point(float x, float y) { X = x; Y = y; }
    }
    public string Source, Route, Family;
    public float Seconds;
    public readonly List<Point> Points = new();
    public bool Renderable => Points.Count >= 2;
    private static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
    private bool Valid() => !string.IsNullOrEmpty(Source) && !string.IsNullOrEmpty(Route) &&
        (Family == "tap" || Family == "star" || Family == "hold" || Family == "touch" || Family == "touchhold") &&
        Finite(Seconds) && Seconds >= .01f && (Points.Count == 0 || Points.Count >= 2) && Points.Count <= 262144 &&
        Points.TrueForAll(p => Finite(p.X) && Finite(p.Y));
    public string Encode()
    {
        if (!Valid()) throw new ArgumentException("Invalid borrowed trajectory");
        using (var stream = new MemoryStream())
        {
            using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), true))
            {
                writer.Write((byte)1); writer.Write(Source); writer.Write(Route); writer.Write(Family);
                writer.Write(Seconds); writer.Write(Points.Count);
                foreach (var point in Points) { writer.Write(point.X); writer.Write(point.Y); }
            }
            return "BT1|" + Convert.ToBase64String(stream.ToArray());
        }
    }
    public static bool Decode(string marker, out BorrowedTrajectory result)
    {
        result = null;
        if (marker == null || !marker.StartsWith("BT1|", StringComparison.Ordinal) || marker.Length > 3000000) return false;
        try
        {
            using (var stream = new MemoryStream(Convert.FromBase64String(marker.Substring(4))))
            using (var reader = new BinaryReader(stream, new UTF8Encoding(false, true)))
            {
                if (reader.ReadByte() != 1) return false;
                var value = new BorrowedTrajectory { Source = reader.ReadString(), Route = reader.ReadString(),
                    Family = reader.ReadString(), Seconds = reader.ReadSingle() };
                var count = reader.ReadInt32();
                if (count < 0 || count == 1 || count > 262144 || stream.Length - stream.Position != count * 8L) return false;
                for (var i = 0; i < count; i++) value.Points.Add(new Point(reader.ReadSingle(), reader.ReadSingle()));
                if (!value.Valid()) return false;
                result = value; return true;
            }
        }
        catch (Exception e) when (e is IOException || e is FormatException || e is ArgumentException) { return false; }
    }
}
