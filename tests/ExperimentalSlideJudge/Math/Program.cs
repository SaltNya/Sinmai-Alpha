using SlideDataBuilder = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideDataBuilder;
using SlideDefinition = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideDefinition;
using System;
using System.Collections.Generic;
using System.Linq;
using DB;
using Manager;
using SinmaiAlpha.Notes.Libs;
using SinmaiAlpha.Notes.ExperimentalSlideJudge;
using Oracle = SlideJudgePrototype;

static class Program
{
    static int checks, frames;
    static void Check(bool good, string why) { checks++; if (!good) throw new Exception(why); }
    static void Near(double a, double b, string why) => Check(Math.Abs(a - b) < 1e-8, why);

    static void Advance(RuntimeInputState state, ulong pressed, ref int index, ref int sub, ref bool entered)
    {
        state.BeginFrame(pressed);
        bool progress;
        do
        {
            progress = state.CheckTouch(index, entered, ref index, ref sub, ref entered);
            if (!progress && state.NextCheck(index, entered) && index + 1 < state.Frame.Steps.Length)
                progress = state.CheckTouch(index + 1, false, ref index, ref sub, ref entered);
        } while (progress && index < state.Frame.Steps.Length);
    }

    static int Transform(int panel, OptionMirrorID mirror, int star)
    {
        // Independent transform via the existing production table conversion.
        return (int)SlideDataBuilder.ConvertAndRotateHitAreas(
            new[] { new SlideDataBuilder.HitAreaData(1, 0, new[] { panel }) }, star, mirror)[0].HitPoints[0];
    }

    static void Main()
    {
        int nativeCalls = SlideManager.NativeVCalls;
        string[] codes = SlideManager.Cases.Where(c => c.GetProperty("type").GetInt32() != 13)
            .Select(c => c.GetProperty("code").GetString()).Concat(new[] {
                "1A3B6A8K4", "1Q99K5", "1^B5K7", "1K2", "D8D2K4", "E8E2K4",
                "DG2:1Q9K3", "TG1:1>B2>C", "TG1:E8^E2" }).Distinct().ToArray();
        foreach (string code in codes)
        {
            var def = SlideDefinition.Parse(code);
            var expected = Oracle.IntervalJudge.Build(def.Paths[0], def.JudgeProfile, Oracle.RegionModels.Contours);
            var port = IntervalJudge.Build(def.Paths[0], def.JudgeProfile, RegionModels.Contours);
            Check(expected.CanExportDistances == port.CanExportDistances, "exportable " + code);
            Near(expected.Path.Length, port.Path.Length, "port measured length " + code);
            Check(expected.Visits.Count == port.Visits.Count && expected.Steps.Count == port.Steps.Count, "port count " + code);
            for (int i = 0; i < port.Steps.Count; i++)
            {
                var a = expected.Steps[i]; var b = port.Steps[i];
                Check(a.Panels.SequenceEqual(b.Panels) && a.RequireEntry == b.RequireEntry && a.RequireRearmMask == b.RequireRearmMask, "port step " + code);
                Near(a.Enter, b.Enter, "port entry"); Near(a.Exit, b.Exit, "port exit"); Near(a.Release, b.Release, "port release");
            }
            if (!port.CanExportDistances) continue;
            foreach (OptionMirrorID mirror in Enum.GetValues(typeof(OptionMirrorID)))
            {
                var compiled = NativeTableCompiler.Compile(code, mirror);
                for (int star = 0; star < 8; star++)
                {
                    var f = compiled.Frames[star];
                    Check(f.Areas.Count == port.Steps.Count, "native count");
                    Near(f.Areas.Sum(a => a.PushDistance + a.ReleaseDistance), port.Path.Length, "distance denominator");
                    var oracleSteps = expected.Steps.Select(s => s with {
                        Panels = s.Panels.Select(p => Transform(p, mirror, star)).ToArray(),
                        RequireRearmMask = s.Panels.Where(p => (s.RequireRearmMask & (1UL << p)) != 0)
                            .Aggregate(0UL, (mask,p) => mask | (1UL << Transform(p,mirror,star)))
                    }).ToArray();
                    for (int i = 0; i < f.Steps.Length; i++)
                    {
                        Check(f.Areas[i].HitPoints.Select(p => (int)p).SequenceEqual(oracleSteps[i].Panels), "mirrored ordered candidates " + code);
                        Near(f.Areas[i].PushDistance, oracleSteps[i].Push, "mirrored push");
                        Near(f.Areas[i].ReleaseDistance, oracleSteps[i].Release, "mirrored release");
                    }
                    var oracle = new Oracle.PrototypeReplay(oracleSteps);
                    var runtime = new RuntimeInputState(f);
                    int index = 0, sub = 0; bool entered = false;
                    var random = new Random(170 + star + 8 * (int)mirror);
                    for (int n = 0; n < 128; n++)
                    {
                        // Follow every area first, then simultaneous random input.
                        ulong pressed = n < f.Steps.Length * 2 ? (n % 2 == 0 ? 1UL << f.Steps[n / 2].Panels[0] : 0)
                            : ((ulong)(uint)random.Next() << 3) | (uint)random.Next(8);
                        oracle.Step(pressed); Advance(runtime, pressed, ref index, ref sub, ref entered); frames++;
                        var state = oracle.State;
                        Check(state.Index == index && state.Entered == entered &&
                            (index >= f.Steps.Length || !entered || f.Steps[index].Panels[sub] == state.Selected), "manual port " + code);
                    }
                }
            }
        }

        // Consecutive overlapping choices must not consume the same held input
        // twice, including a later frame; releasing re-arms it.
        var repeated = new CompiledJudgeFrame(new[] {
            new JudgeStep(new[] { 0, 8 },0,1,0),
            new JudgeStep(new[] { 8, 9 },1,2,0, RequireRearmMask: 1UL << 8),
            new JudgeStep(new[] { 9 },2,3,0, RequireEntry:true, RequireRearmMask:1UL<<9)
        });
        var repeat = new RuntimeInputState(repeated); int ri=0,rs=0; bool re=false;
        Advance(repeat,1UL<<8,ref ri,ref rs,ref re); Check(ri==0&&re,"held shared sensor cannot skip");
        Advance(repeat,1UL<<8,ref ri,ref rs,ref re); Check(ri==0&&re,"held next frame remains blocked");
        Advance(repeat,0,ref ri,ref rs,ref re); Check(ri==1&&!re,"release re-arms shared input");
        Advance(repeat,1UL<<8,ref ri,ref rs,ref re); Check(ri==1&&re,"press again enters next visit");
        foreach (string invalid in new[] { "1K1", "DF1:1w5" })
        {
            bool rejected=false;try{NativeTableCompiler.Compile(invalid,OptionMirrorID.Normal);}catch(ArgumentException){rejected=true;}
            Check(rejected,"fallback instead of fabricated distances " + invalid);
        }
        var hot = new RuntimeInputState(NativeTableCompiler.Compile("1K5",OptionMirrorID.Normal).Frames[0]);
        int hi=0,hs=0;bool he=false;
        for(int i=0;i<10000;i++)Advance(hot,0,ref hi,ref hs,ref he);
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<200000;i++)Advance(hot,0,ref hi,ref hs,ref he);
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Check(allocated==0,"zero allocations in input loop");
        Check(SlideManager.NativeVCalls==nativeCalls,"math port does not read native hit tables");
        Console.WriteLine($"PASS math port: {codes.Length} paths, 4 mirrors x 8 star frames; {frames} input frames; {checks} checks; hot-path bytes={allocated}");
    }
}
