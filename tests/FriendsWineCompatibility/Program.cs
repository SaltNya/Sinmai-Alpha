using SinmaiAlpha.Notes.Libs;
using SinmaiAlpha.ChartVisuals;
using UnityEngine;

void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
foreach (var pair in new[] {
    ("D2VD48", "D2-D4-8"), ("D6VD41", "D6-D4-1"), ("D6VD84", "D6-D8-4"), ("D2VD85", "D2-D8-5"),
    ("1VD45", "1-D4-5"), ("1VB45", "1-B4-5"), ("1VC5", "1-C-5"), ("1V45", "1-4-5"), ("1V4d5", "1-4d-5") })
{
    var corner = new MajdataSlidePath(pair.Item1, true);
    var explicitRoute = new MajdataSlidePath(pair.Item2, true);
    Check(Math.Abs(corner.Length - explicitRoute.Length) < .0001f, "Wrong corner length: " + pair.Item1);
    for (int i = 0; i <= 100; i++)
        Check(Vector3.Distance(corner.EvaluatePath(i / 100f), explicitRoute.EvaluatePath(i / 100f)) < .0001f, "Wrong corner geometry");
    Check(corner.SensorRoute.SequenceEqual(explicitRoute.SensorRoute), "Wrong sensor order: " + pair.Item1);
}
Console.WriteLine("PASS 9 runtime V-turn geometries and sensor routes, 909 path samples");
foreach (var chart in args)
{
    int count = 0;
    foreach (var line in File.ReadLines(chart))
        foreach (var field in line.Trim().Split('\t'))
            if (field.StartsWith("TG1:")) { _ = new MajdataSlidePath(field.Substring(4), true); count++; }
    Console.WriteLine($"PASS actual chart TG1 paths: {Path.GetFileName(chart)} count={count}");
}
var windows = new List<(float, int)>();
for (int burst = 0; burst < 3; burst++) for (int i = 0; i < 600; i++)
{ windows.Add((burst * 10000, 1)); windows.Add((burst * 10000 + 1000, -1)); }
Check(VisualPoolCapacity.Peak(windows) == 600, "Must size by peak, not total 1800");
windows.Add((500, 1)); windows.Add((1500, -1));
Check(VisualPoolCapacity.Peak(windows) == 601, "Overlapping burst accounting");
Check(VisualPoolCapacity.Peak(new()) == 0, "No growth for ordinary charts");
var payload = new VisualNote { Family = "tap", ExactHeadMsec = 38000.0009230774, ExactTailMsec = 38000.0018461548 };
var restored = VisualNote.Decode(payload.Encode());
Check(restored.ExactHeadMsec == payload.ExactHeadMsec && restored.ExactTailMsec == payload.ExactTailMsec, "Runtime precise decode lost time");
var command = PreciseTiming.Encode(12.34567890123, "s3=999");
Check(PreciseTiming.Decode(ref command, 0, 500) == 512.34567890123 && command == "s3=999", "Clock adjustment or stream corrupted");
Console.WriteLine("PASS peak pool sizing and runtime precise metadata decode");

foreach (var peak in new[] { 1, 32, 64, 128, 192, 600, 1024, 1200, 2048, 10000 })
{
    var reserved = VisualPoolCapacity.Reserve(peak);
    Check(reserved >= peak + Math.Max(32, (peak + 3) / 4), "Insufficient spare capacity");
    Check(reserved % 128 == 0, "Unaligned capacity");
}
Check(VisualPoolCapacity.Reserve(0) == 0, "Empty pools must not grow");
Console.WriteLine("PASS progressive capacity 1..10000, including >1024");
