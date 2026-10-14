using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using SinmaiAlpha.ChartVisuals;

// Compare the optimized resolver against the 0.1.5 backwards-scan semantics.
static class Program
{
    static VisualValue Original(IList<VisualChange> changes, double time, VisualNote note, string family = null)
    {
        family ??= note.Family;
        VisualValue Lookup(string kind, string type)
        {
            for (int i = changes.Count - 1; i >= 0; i--)
                if (changes[i].Time <= time + .000001 && changes[i].Kind == kind && changes[i].Type == type) return changes[i].Value;
            return null;
        }
        IEnumerable<string> Priority(string kind)
        {
            if (family == "blockarea") { yield return "blockarea"; yield break; }
            if (note.Mine) { yield return "mine"; if (kind == "color") yield break; }
            if (note.Break) yield return "break";
            if (note.Each) yield return "each";
            yield return family;
            if (kind == "color" && family == "star") yield return "tap";
            if (kind != "size" || family != "slide") yield return "";
        }
        var result = new VisualValue();
        foreach (var type in Priority("color")) { var v = Lookup("color", type); if (v?.Color != null) { result.Color = v.Color; break; } }
        foreach (var type in Priority("size")) { var v = Lookup("size", type); if (v?.X != null) { result.X = v.X; result.Y = v.Y; break; } }
        foreach (var type in Priority("alpha")) { var v = Lookup("alpha", type); if (v?.Alpha != null) { result.Alpha = v.Alpha; break; } }
        return result;
    }
    static void Check(bool ok, string name) { if (!ok) throw new Exception(name); }
    static void Equal(VisualValue a, VisualValue b)
        => Check(a.Color == b.Color && a.X == b.X && a.Y == b.Y && a.Alpha == b.Alpha, "resolver mismatch");
    static void Main()
    {
        var random = new Random(251015);
        string[] types = { "", "tap", "star", "slide", "slidestar", "hold", "each", "mine", "break", "touch", "touchhold", "blockarea", null };
        string[] kinds = { "color", "size", "alpha" };
        var scratch = new VisualValue();
        List<VisualChange> changes = null;
        for (int scenario = 0; scenario < 200; scenario++)
        {
            changes = new List<VisualChange>();
            for (int i = 0; i < 400; i++)
            {
                var kind = kinds[random.Next(3)]; var value = new VisualValue();
                if (random.Next(4) != 0)
                {
                    if (kind == "color") value.Color = random.Next(0x1000000);
                    if (kind == "size") { value.X = random.Next(5); value.Y = random.Next(5); }
                    if (kind == "alpha") value.Alpha = (float)random.NextDouble();
                }
                changes.Add(new VisualChange { Time = i % 47 == 0 ? double.NaN : scenario % 3 == 1 ? random.Next(140) : i / 3,
                    Kind = kind, Type = types[random.Next(types.Length)], Value = value });
            }
            for (int q = 0; q < 300; q++)
            {
                var note = new VisualNote { Family = types[random.Next(types.Length)], Mine = random.Next(2) == 0, Break = random.Next(2) == 0, Each = random.Next(2) == 0 };
                double time = q == 0 ? double.NaN : q == 1 ? double.PositiveInfinity : q == 2 ? double.NegativeInfinity : random.NextDouble() * 150 - 5;
                Equal(Original(changes, time, note), VisualState.Resolve(changes, time, note, null, scratch));
            }
            VisualState.ReleaseIndexes();
        }
        Console.WriteLine("PASS 60000 sorted/unsorted priority, reset, noise and time queries");
        var onlyGlobal = new List<VisualChange> { new() { Time = 0, Kind = "color", Type = "", Value = new() { Color = 0x123456 } } };
        Check(VisualState.Resolve(onlyGlobal, 0, new() { Family = "blockarea" }).Color == null, "global tint leaked into noise");
        Check(VisualState.TryParse("colorv", "noise=E07026", 0, out var noise) && noise[0].Type == "blockarea", "noise alias lost");
        Check(VisualState.TryParse("color", "blockarea=E07026", 0, out _), "noise type lost");
        Equal(Original(noise, 1, new() { Family = "blockarea" }), VisualState.Resolve(noise, 1, new() { Family = "blockarea" }));
        var ordinary = new VisualNote { Family = "tap" };
        onlyGlobal.Add(new() { Time = 1, Kind = "color", Type = "", Value = new() { Color = 42 } });
        Check(VisualState.Resolve(onlyGlobal, 2, ordinary).Color == 42, "append did not rebuild index");
        onlyGlobal[1] = new() { Time = 1, Kind = "color", Type = "", Value = new() { Color = 43 } };
        VisualState.ReleaseIndexes(); // In-place chart edits must explicitly invalidate indexes.
        Check(VisualState.Resolve(onlyGlobal, 2, ordinary).Color == 43, "release retained stale values");
        VisualState.Resolve(new List<VisualChange>(), 0, ordinary, null, scratch);
        Check(scratch.Color == null && scratch.X == null && scratch.Y == null && scratch.Alpha == null, "scratch leaked previous state");
        for (int i = 0; i < 1100; i++) VisualState.Resolve(new List<VisualChange>(), 0, ordinary);
        Check(VisualState.Resolve(onlyGlobal, 2, ordinary).Color == 43, "cache eviction changed result");
        Console.WriteLine("PASS noise syntax, stream isolation, append, release, scratch reset and cache eviction");
        changes = changes.OrderBy(c => c.Time).ToList();
        var star = new VisualNote { Family = "star", Each = true };
        for (int i = 0; i < 10000; i++) { Original(changes, i % 140, star); VisualState.Resolve(changes, i % 140, star, null, scratch); }
        Bench("0.1.5 scan", () => { for (int i = 0; i < 100000; i++) Original(changes, i % 140, star); });
        Bench("0.1.6 indexed/scratch", () => { for (int i = 0; i < 100000; i++) VisualState.Resolve(changes, i % 140, star, null, scratch); });
    }
    static void Bench(string label, Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread(); var sw = Stopwatch.StartNew(); action(); sw.Stop();
        Console.WriteLine(label + ": 100000 queries, " + sw.Elapsed.TotalMilliseconds.ToString("F2") + " ms, " + (GC.GetAllocatedBytesForCurrentThread() - before) + " bytes");
    }
}
