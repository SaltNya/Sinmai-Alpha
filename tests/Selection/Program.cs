using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Manager;
using Process;
using Process.SubSequence;
using SinmaiAlpha.Difficulty;
using SerializedMusic = Manager.MaiStudio.Serialize.MusicData;
using SerializedNotes = Manager.MaiStudio.Serialize.Notes;

internal static partial class Program
{
    private static int checks;
    private static void Check(bool condition, string message)
    { checks++; if (!condition) throw new InvalidOperationException(message); }
    private static void Set(object owner, string name, object value) => AccessTools.Field(owner.GetType(), name).SetValue(owner, value);
    private static Manager.MaiStudio.MusicData Music(int id, params int[] enabled)
    {
        var source = new SerializedMusic(); source.name.id = id;
        for (int i = 0; i < 5; i++) source.notesData.Add(new SerializedNotes { isEnable = enabled.Contains(i), level = i + 7 });
        var music = new Manager.MaiStudio.MusicData(); music.Init(source); return music;
    }
    private static int Audit(System.Type type)
    {
        var targets = type.GetMethod("TargetMethods", BindingFlags.Public | BindingFlags.Static);
        IEnumerable<MethodBase> methods;
        if (targets != null) methods = (IEnumerable<MethodBase>)targets.Invoke(null, null);
        else
        {
            var info = type.GetCustomAttributes(typeof(HarmonyPatch), false).Cast<HarmonyPatch>().Single().info;
            methods = new MethodBase[] { AccessTools.Method(info.declaringType, info.methodName, info.argumentTypes) };
        }
        int count = 0;
        foreach (var method in methods)
        {
            Check(method != null, "Patch target resolves: " + type.Name);
            var transpiler = type.GetMethod("Transpiler", BindingFlags.Public | BindingFlags.Static);
            if (transpiler != null)
            {
                var original = PatchProcessor.GetOriginalInstructions(method);
                var before = original.Select(x => x.opcode + " " + x.operand).ToArray();
                var after = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { original, method })).ToList();
                Check(!before.SequenceEqual(after.Select(x => x.opcode + " " + x.operand)), "Transpiler changes original IL: " + method);
            }
            count++;
        }
        Check(count > 0, "Nonempty patch group: " + type.Name);
        return count;
    }
    private static void AvailabilityChecks()
    {
        for (int mask = 0; mask < 32; mask++)
        {
            int[] enabled = Enumerable.Range(0, 5).Where(d => (mask & (1 << d)) != 0).ToArray();
            Check(DifficultyAvailability.Mask(Music(16050, enabled).notesData) == mask, "Only enabled native charts contribute to mask");
            for (int requested = -1; requested <= 6; requested++)
            {
                int actual = DifficultyAvailability.Resolve(mask, requested);
                Check(mask == 0 ? actual == -1 : enabled.Contains(actual), "Resolved chart exists");
                if (enabled.Contains(requested)) Check(actual == requested, "Existing selection stays selected");
                foreach (int direction in new[] { -1, 1 })
                {
                    int expected = direction > 0 ? enabled.Where(d => d > requested).DefaultIfEmpty(-1).First() : enabled.Where(d => d < requested).DefaultIfEmpty(-1).Last();
                    Check(DifficultyAvailability.Next(mask, requested, direction) == expected, "Adjacent selection skips gaps");
                }
            }
            for (int player = 0; player < 2; player++)
            {
                var proxy = new SelectionProcess(Music(16050, enabled));
                var sequence = new DifficultySelectSequence(player, true, (IMusicSelectProcessProcessing)proxy.GetTransparentProxy());
                proxy.Difficulties[0] = proxy.Difficulties[1] = MusicDifficultyID.Master;
                proxy.Selected[0] = proxy.Selected[1] = -1;
                ExtraDifficulty.AvailableSelectionClampPatch.Prefix(sequence);
                int resolved = DifficultyAvailability.Resolve(mask, 3);
                Check((int)proxy.Difficulties[player] == (resolved < 0 ? 3 : resolved), "Clamp selects an available chart");
                Check(proxy.Difficulties[1 - player] == MusicDifficultyID.Master && proxy.Selected[player] == -1, "Clamp preserves other player and unselected sentinel");
            }
        }
        foreach (int id in new[] { 0, 20001, 29999, 100001 })
        {
            var proxy = new SelectionProcess(Music(id, 2));
            int mask = (int)AccessTools.Method(typeof(ExtraDifficulty), "SongMask").Invoke(null, new object[] { proxy.Row });
            Check(mask == -1, "Native special/utage paths are untouched");
        }
        Check(DifficultyAvailability.Mask(null) == 0, "Null chart list is empty");
    }
    private static int Main()
    {
        try
        {
            int audited = typeof(ExtraDifficulty).GetNestedTypes(BindingFlags.Public)
                .Where(t => t.Name.StartsWith("Available", StringComparison.Ordinal) && t.GetCustomAttributes(typeof(HarmonyPatch), false).Any()).Sum(Audit);
            AvailabilityChecks();
            SelectionSoundChecks();
            System.Console.WriteLine("PASS: " + checks + " assertions; " + audited + " original patch targets/IL audited.");
            System.Console.WriteLine("Native rendering is stubbed; original-game visual validation is still required.");
            return 0;
        }
        catch (Exception error) { System.Console.Error.WriteLine(error); return 1; }
    }
}
