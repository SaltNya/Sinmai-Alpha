using System;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mono.Cecil;
using SinmaiAlpha.Notes;

// Only this locally defined fixture is executed. External game DLLs are read as IL data.
namespace Monitor
{
    public class SlideRoot
    {
        public float lastWaitTime;
        public int grade;
        public bool mine, ended;
        [MethodImpl(MethodImplOptions.NoInlining)] public int GetJudgeResult() => grade;
        [MethodImpl(MethodImplOptions.NoInlining)] public void NoteCheck()
        {
            if (lastWaitTime <= 0f || GetJudgeResult() == 14)
            {
                if (GetJudgeResult() == 14) grade = 13;
                ended = true;
            }
            lastWaitTime -= 1;
        }
    }
    public class SlideFan : SlideRoot { }
}
namespace SinmaiAlpha.Notes
{
    public partial class CustomNoteTypes
    {
        public static bool IsMineSlideEndWaitDone(Monitor.SlideRoot s) =>
            s.lastWaitTime <= 0 || s.grade == 14 || s.mine && s.grade == 7;
    }
}
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            foreach (var path in args) Inspect(path);
            var h = new Harmony("slide-end-wait-regression");
            var method = AccessTools.Method(typeof(Monitor.SlideRoot), "NoteCheck");
            var baseline = Enumerable.Range(0, 15).Select(Run).ToArray();
            h.Patch(method, transpiler: new HarmonyMethod(typeof(CustomNoteTypes.SlideEndWaitTranspiler), "Transpiler"));
            for (int grade = 0; grade < 15; grade++)
            {
                var result = Run(grade);
                if (result != baseline[grade]) throw new Exception($"Finalization grade={grade}: native={baseline[grade]}, patched={result}");
            }
            var mine = new Monitor.SlideRoot { mine = true, grade = 7, lastWaitTime = 10 };
            mine.NoteCheck();
            if (!mine.ended || mine.grade != 7) throw new Exception("Mine Critical must finish immediately without entering LateGood fallback");
            h.UnpatchSelf();
            Console.WriteLine("PASS all 15 ordinary grades through finalization; mine Critical early finish preserved");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL " + e); return 1; }
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static string Run(int grade)
    {
        var slide = new Monitor.SlideRoot { grade = grade, lastWaitTime = 1 };
        slide.NoteCheck();
        var first = slide.grade + "/" + slide.ended;
        if (!slide.ended) slide.NoteCheck();
        return first + " -> " + slide.grade + "/" + slide.ended;
    }
    static void Inspect(string path)
    {
        var opcodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)).ToDictionary(o => o.Value);
        using (var asm = AssemblyDefinition.ReadAssembly(path))
        foreach (var type in new[] { "Monitor.SlideRoot", "Monitor.SlideFan" })
        {
            var method = asm.MainModule.GetType(type).Methods.Single(m => m.Name == "NoteCheck");
            var input = method.Body.Instructions.Select(i => new CodeInstruction(opcodes[i.OpCode.Value], i.Operand)).ToList();
            var output = CustomNoteTypes.SlideEndWaitTranspiler.Transpiler(input).ToList();
            int matches = output.Count(i => i.operand is MethodInfo m && m.Name == "IsMineSlideEndWaitDone");
            Console.WriteLine($"IL {asm.MainModule.Mvid} {type}: replacements={matches}");
            if (matches != 1) throw new Exception("Must replace only outer end-wait test, never inner TooLate test");
        }
    }
}
