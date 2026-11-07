using CustomSlideNoteData = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.UnifiedSlideNoteData;
using SlideDataBuilder = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideDataBuilder;
using SlideCodeParser = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideCodeParser;
using SlideTable = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideTable;
using SlideJudgeProfile = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideJudgeProfile;
using SlideDefinition = SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.SlideDefinition;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using DB;
using Manager;
using SinmaiAlpha.Notes.Libs;

static class Program
{
    static void Check(bool ok,string why) { if(!ok)throw new Exception(why); }
    internal static string Rotate(string code,int offset)
    {
        var commands=SlideCodeParser.ParseCommands(code);
        return string.Concat(commands.Select((c,i)=>
        {
            int v=c.Value;
            if(c.Type!=SlideCodeParser.CommandType.NodeC && !((c.Type==SlideCodeParser.CommandType.OrbitCCW||c.Type==SlideCodeParser.CommandType.OrbitCW)&&(v==0||v==9)))v=(v-1+offset)%8+1;
            return (c.Shape!='-'?c.Shape.ToString():"")+(i==0&&c.Type==SlideCodeParser.CommandType.NodeA?"":SlideCodeParser.CommandChars[(int)c.Type].ToString())+(c.Type==SlideCodeParser.CommandType.NodeC?"":v.ToString());
        }));
    }
    static CustomSlideNoteData Load(string code,int start=0,int end=2,int mirror=0)
    {
        var d=new CustomSlideNoteData{startButtonPos=MaiGeometry.MirrorInfo[mirror,start]};
        d.slideData.targetNote=MaiGeometry.MirrorInfo[mirror,end];
        Check(d.ParseSlideCode(code,(OptionMirrorID)mirror),"parse "+code);return d;
    }
    static string Panels(IEnumerable<SlideManager.HitArea> areas)=>string.Join(" -> ",areas.Select(h=>string.Join("/",h.HitPoints.Select(x=>x==InputManager.TouchPanelArea.C2?InputManager.TouchPanelArea.C1:x).OrderBy(x=>x))));
    static string NativePanels(JsonElement row)=>string.Join(" -> ",row.GetProperty("native").EnumerateArray().Select(a=>string.Join("/",a.GetProperty("panels").EnumerateArray().Select(x=>Enum.Parse<InputManager.TouchPanelArea>(x.GetString().Replace("C2","C1"))).OrderBy(x=>x))));
    static void Validate(IReadOnlyList<SlideManager.HitArea> areas,SlideJudgeProfile profile)
    {
        Check(areas.Count>0,"empty areas");
        Check(areas.All(a=>double.IsFinite(a.PushDistance)&&double.IsFinite(a.ReleaseDistance)&&a.PushDistance>=-1e-7&&a.ReleaseDistance>=-1e-7),"invalid distances");
        Check(areas.All(a=>a.HitPoints.All(i=>(int)i>=0&&(int)i<(profile==SlideJudgeProfile.StandardAbc?18:34))),"profile panel bounds");
        Check(areas.Sum(a=>a.PushDistance+a.ReleaseDistance)>0,"judgment denominator");
    }
    static void FreePath(ParametricSlidePath p,SlideJudgeProfile profile)
    {
        var areas=SlideDataBuilder.BuildHitAreas(p,profile);
        Validate(SlideDataBuilder.ConvertAndRotateHitAreas(areas,0,OptionMirrorID.Normal),profile);
        Check(Math.Abs(areas.Sum(a=>a.PushDistance+a.ReleaseDistance)-(areas.Count==1?Math.Max(1,p.GetPathLength()):p.GetPathLength()))<1e-6,"distance conservation");
    }
    static int TransformPanel(int panel,int rotation,int mirror,int star)
    {
        if(panel is 16 or 17)return 16;
        var ring=panel<8?0:panel<16?8:panel<26?18:26;
        var key=(panel-ring+rotation)&7;
        key=ring>=18
            ? mirror switch{1=>(-key+8)&7,2=>(4-key+8)&7,3=>(key+4)&7,_=>key}
            : mirror switch{1=>7-key,2=>(3-key+8)&7,3=>(key+4)&7,_=>key};
        return ring+((key-star+8)&7);
    }
    static void RotationParity(List<SlideManager.HitArea> reference,CustomSlideNoteData data,int rotation,int mirror)
    {
        for(int star=0;star<8;star++)
        {
            var current=data.GetSlideHitAreas(star);Validate(current,data.JudgeProfile);
            Check(current.Count==reference.Count,"rotation count "+data.SlideCode);
            for(int i=0;i<current.Count;i++)
            {
                var expected=reference[i].HitPoints.Select(x=>TransformPanel((int)x,rotation,mirror,star)).OrderBy(x=>x);
                Check(expected.SequenceEqual(current[i].HitPoints.Select(x=>(int)x is 16 or 17 ? 16 : (int)x).OrderBy(x=>x)),"rotation panels "+data.SlideCode);
                Check(Math.Abs(reference[i].PushDistance-current[i].PushDistance)<1e-5&&Math.Abs(reference[i].ReleaseDistance-current[i].ReleaseDistance)<1e-5,"rotation distances "+data.SlideCode);
            }
        }
    }
    static int Main(string[] args)
    {
        try
        {
            var watch=Stopwatch.StartNew();int n=0,same=0;
            CompatibilitySnapshots.Verify(Path.Combine(AppContext.BaseDirectory, "CompatibilityBaseline.json"));
            SlideTableTests.Run();
            NativeVTests.Run();
            var report=new List<string>{"Unified SC/SSS geometry audit with native v/V compatibility", "Native v/V uses game tables and type; other paths use the shared geometric generator.",""};
            foreach(var row in SlideManager.Cases.Where(r=>r.GetProperty("type").GetInt32()!=13))
            {
                var code=row.GetProperty("code").GetString();var baseline=Load(code).GetSlideHitAreas(0);
                var native=NativePanels(row);bool equal=native==Panels(baseline);if(equal)same++;
                report.Add($"type={row.GetProperty("type")} code={code} panelsEqual={equal}\n  native: {native}\n  unified: {Panels(baseline)}");
                for(int rotation=0;rotation<8;rotation++)for(int mirror=0;mirror<4;mirror++)
                {
                    var data=Load(Rotate(code,rotation),rotation,(row.GetProperty("end").GetInt32()-1+rotation)&7,mirror);
                    Check(data.JudgeProfile==SlideJudgeProfile.StandardAbc,"stock-shaped code profile");
                    RotationParity(baseline,data,rotation,mirror);n++;
                }
            }
            foreach(var code in new[]{"1P0A5K7","1A3B6A8K4","1B2A6CK7","1Q99K5","1K2"})
            {
                var baseline=Load(code).GetSlideHitAreas(0);
                for(int rot=0;rot<8;rot++)for(int mirror=0;mirror<4;mirror++)
                {RotationParity(baseline,Load(Rotate(code,rot),rot,(code[^1]-'1'+rot)&7,mirror),rot,mirror);n++;}
            }
            var standard=Load("1K4");var touch=Load("DG2:1K4");
            Check(Panels(standard.GetSlideHitAreas(0))=="A1 -> B2 -> B3 -> A4","straight ABC regression");
            Check(touch.JudgeProfile==SlideJudgeProfile.ExtendedTouch&&touch.GetSlideHitAreas(0).Any(h=>h.HitPoints.Any(x=>(int)x>=26)),"DG touch sensors");
            foreach(var code in new[]{"TG1:1-4","DG1:1-4"})
            {
                var data=Load(code);Check(data.JudgeProfile==SlideJudgeProfile.ExtendedTouch&&Panels(data.GetSlideHitAreas(0))==Panels(touch.GetSlideHitAreas(0)),"TG/DG loader wiring");
                if(code.StartsWith("TG"))Check(data.TouchGeometry!=null,"touch SV geometry retained");
            }
            Check(Load("DG2:1Q9K3").GetSlideHitAreas(0).Any(h=>h.HitPoints.Any(x=>(int)x>=18&&(int)x<26)),"outer D sampling");
            foreach(var code in new[]{"1E2K3","E1K5","D8D2K4","F1K5","B1K5","CK5"})
            {
                var d=Load(code);Check(d.JudgeProfile==SlideJudgeProfile.ExtendedTouch,"explicit touch "+code);
                foreach(var frame in d.Table.Frames)Validate(frame.Lanes[0].HitAreas,d.JudgeProfile);
                for(int rot=0;rot<8;rot++)for(int mirror=0;mirror<4;mirror++)
                    RotationParity(d.GetSlideHitAreas(0),Load(Rotate(code,rot),rot,(code[^1]-'1'+rot)&7,mirror),rot,mirror);
            }
            foreach(var code in new[]{"TG1:1-4","DG1:1d-5","DG2:1K4","DF1:1dw5","DF1:1w5d"})
                Check(SlideDefinition.Parse(code).JudgeProfile==SlideJudgeProfile.ExtendedTouch,"prefix policy "+code);
            var orbit=Load("1Q9K3");var polar=Load("1^K3");
            Check(orbit.slideData.type==polar.slideData.type&&Panels(orbit.GetSlideHitAreas(0))==Panels(polar.GetSlideHitAreas(0)),"arc spelling");
            Check(orbit.GetSlideHitAreas(0)[^1].PushDistance==polar.GetSlideHitAreas(0)[^1].PushDistance,"arc tail");
            for(int rotation=0;rotation<8;rotation++)for(int mirror=0;mirror<4;mirror++)
            foreach(bool dZone in new[]{false,true})
            {
                var code=$"DF1:{rotation+1}{(dZone?"d":"")}w{(rotation+4)%8+1}";
                var data=Load(code,rotation,(rotation+4)&7,mirror);
                Check(data.IsFan&&data.slideData.type==SlideType.Slide_Fan,"fan lanes");
                foreach(var frame in data.Table.Frames)foreach(var lane in frame.Lanes)Validate(lane.HitAreas,data.JudgeProfile);
            }
            var nodes=new List<Complex>{Complex.Zero};
            for(int i=1;i<=8;i++)nodes.AddRange(new[]{MaiGeometry.PointGroupA(i),MaiGeometry.PointGroupB(i),MaiGeometry.PointGroupD(i),MaiGeometry.PointGroupE(i)});
            foreach(var from in nodes)foreach(var to in nodes)foreach(var profile in Enum.GetValues<SlideJudgeProfile>())
                FreePath(new ParametricSlidePath(new[]{new ParametricSlidePath.LineSegment(from,to)}),profile);
            report.Add($"\nPanel queues equal to native: {same}/70 non-fan cases (includes restored Finale return-v). Native v/V distances and skip types match; other shapes retain geometric distances.");
            if(args.Length>0)File.WriteAllLines(args[0],report);
            Console.WriteLine($"PASS {n} ABC rotation/mirror cases; 64 normal/D fan cases; 2178 two-profile node-pair checks; explicit sensor opt-in; only native v/V table reads ({watch.ElapsedMilliseconds} ms)");
            Console.WriteLine(report[^1]);return 0;
        }
        catch(Exception e){Console.WriteLine("FAIL "+e);return 1;}
    }
}
