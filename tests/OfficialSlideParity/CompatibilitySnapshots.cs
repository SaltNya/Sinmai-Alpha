using CustomSlideNoteData = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.UnifiedSlideNoteData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using DB;
using Manager;
using SinmaiAlpha.Notes.Libs;

// Captured before the table pipeline refactor. Compare every numeric arrow and
// hit-area field, not just a panel count or a hand-picked straight route.
static class CompatibilitySnapshots
{
    internal static Dictionary<string, string> Capture()
    {
        var codes = SlideManager.Cases.Where(r => r.GetProperty("type").GetInt32() != 13)
            .Select(r => r.GetProperty("code").GetString()).Concat(new[] {
                "1P0A5K7", "1A3B6A8K4", "1B2A6CK7", "1Q99K5", "1K2",
                "1E2K3", "E1K5", "D8D2K4", "F1K5", "B1K5", "CK5",
                "DG2:1K4", "DG2:1Q9K3", "DG2:D8D2K4",
                "DG1:1-4", "DG1:1d-5", "DG1:1d^3d", "DG1:1dp5d", "DG1:1dq5",
                "DG1:1dpp5d", "DG1:1dV3d5d", "DG1:1ds5",
                "TG1:1-4", "TG1:1>B2>C", "TG1:D2VD48", "TG1:B1-B5", "TG1:C-5",
                "TG1:E8^E2", "TG1:1>>>B5-C", "TG1:1q5", "TG1:1>1"
            }).Distinct().ToList();
        for (var key = 1; key <= 8; key++)
            foreach (var sd in new[] { "", "d" }) foreach (var ed in new[] { "", "d" })
                codes.Add($"DF1:{key}{sd}w{(key + 3) % 8 + 1}{ed}");

        var snapshots = new Dictionary<string, string>();
        foreach (var code in codes) for (var mirror = 0; mirror < 4; mirror++)
        {
            var data = new CustomSlideNoteData();
            if (!data.ParseSlideCode(code, (OptionMirrorID)mirror))
                throw new Exception("Snapshot parse: " + code);
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream);
            writer.Write((int)data.JudgeProfile);
            writer.Write((int)data.slideData.type);
            writer.Write(data.slideData.targetNote);
            writer.Write(data.SlidePathLength);
            writer.Write(data.IsFan);
            for (var star = 0; star < 8; star++)
            {
                writer.Write(data.Table.Frames[star].VisualRotation);
                for (var lane = 0; lane < (data.IsFan ? 3 : 1); lane++)
                {
                    var arrows = data.Table.Frames[star].Lanes[lane].Path;
                    var hits = data.Table.Frames[star].Lanes[lane].HitAreas;
                    writer.Write(arrows.Count);
                    foreach (var a in arrows)
                    {
                        if (!float.IsFinite(a.x) || !float.IsFinite(a.y) || !float.IsFinite(a.z) || !float.IsFinite(a.w))
                            throw new Exception("Nonfinite arrow: " + code);
                        writer.Write(a.x); writer.Write(a.y); writer.Write(a.z); writer.Write(a.w);
                    }
                    writer.Write(hits.Count);
                    foreach (var h in hits)
                    {
                        writer.Write(h.PushDistance); writer.Write(h.ReleaseDistance); writer.Write(h.HitPoints.Count);
                        foreach (var p in h.HitPoints) writer.Write((int)p);
                    }
                }
            }
            writer.Write(data.TouchGeometry != null);
            if (data.TouchGeometry != null)
                for (var i = 0; i <= 100; i++)
                {
                    var point = data.TouchGeometry.GetPointAt(i / 100.0);
                    var tangent = data.TouchGeometry.GetTangentAt(i / 100.0);
                    writer.Write(point.Real); writer.Write(point.Imaginary);
                    writer.Write(tangent.Real); writer.Write(tangent.Imaginary);
                }
            writer.Flush();
            snapshots.Add(code + "/mirror=" + mirror, Convert.ToHexString(SHA256.HashData(stream.ToArray())));
        }
        return snapshots;
    }

    internal static void Verify(string path)
    {
        var expected = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
        var current = Capture();
        if (current.Count != expected.Count) throw new Exception("Compatibility snapshot coverage changed");
        // This behavior change is intentional and checked against native tables
        // separately. Keep the original fixture; do not bless new output hashes.
        var changedV = SlideManager.Cases.Where(r => r.GetProperty("type").GetInt32() is 8 or 11 or 12)
            .Select(r => r.GetProperty("code").GetString()).ToHashSet();
        var unchanged = 0;
        foreach (var (key, value) in expected)
        {
            if (changedV.Contains(key.Substring(0, key.IndexOf("/mirror=", StringComparison.Ordinal)))) continue;
            if (current[key] != value) throw new Exception("Path/judge table changed: " + key);
            unchanged++;
        }
        Console.WriteLine($"PASS {unchanged} unchanged pre-refactor tables; native v/V changes have separate exact parity checks");
    }
}
