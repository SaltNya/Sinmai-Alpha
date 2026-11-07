using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Mono.Cecil;
using SinmaiAlpha.Notes;

namespace Manager
{
    public class SlideData { public int type, targetNote; }
    public class NoteData { public SlideData slideData=new SlideData(); public int startButtonPos; public List<NoteData> child; public int customValue; }
    public class SlideManager
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public int GetSlidePath(int type,int start,int end,int star)=>1;
        [MethodImpl(MethodImplOptions.NoInlining)] public int GetSlideHitArea(int type,int start,int end,int star)=>2;
        [MethodImpl(MethodImplOptions.NoInlining)] public float GetSlideLength(int type,int start,int end)=>3;
    }
}
public class Fixture
{
    public static Manager.SlideManager Manager=new Manager.SlideManager();
    public int finallyCount;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int Initialize(Manager.NoteData note)
    {
        int sum=Manager.GetSlidePath(note.slideData.type,note.startButtonPos,note.slideData.targetNote,0);
        sum+=Manager.GetSlideHitArea(note.slideData.type,note.startButtonPos,note.slideData.targetNote,0);
        sum+=(int)Manager.GetSlideLength(note.slideData.type,note.startButtonPos,note.slideData.targetNote);
        var list=note.child;
        try
        {
            while(list!=null)
            {
                var children=list;list=null;
                foreach(var child in children)
                {
                    sum+=Manager.GetSlidePath(child.slideData.type,child.startButtonPos,child.slideData.targetNote,0);
                    sum+=Manager.GetSlideHitArea(child.slideData.type,child.startButtonPos,child.slideData.targetNote,0);
                    sum+=(int)Manager.GetSlideLength(child.slideData.type,child.startButtonPos,child.slideData.targetNote);
                    list=child.child;
                }
            }
        }
        finally { finallyCount++; }
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int GetSlideArrowNum(Manager.NoteData note)
    {
        int sum=Manager.GetSlidePath(note.slideData.type,note.startButtonPos,note.slideData.targetNote,0);
        foreach(var child in note.child ?? new List<Manager.NoteData>())sum+=Manager.GetSlidePath(child.slideData.type,child.startButtonPos,child.slideData.targetNote,0);
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public float StarInitialize(Manager.NoteData note)
    {
        float sum=0;foreach(var child in note.child ?? new List<Manager.NoteData>())sum+=Manager.GetSlideLength(child.slideData.type,child.startButtonPos,child.slideData.targetNote);return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public int FanInitialize(Manager.NoteData note)=>Manager.GetSlideHitArea(13,note.startButtonPos,4,0);
}
static class Program
{
    static void Check(bool ok,string why){if(!ok)throw new Exception(why);}
    public static int Path(Manager.SlideManager m,int type,int start,int end,int star,Manager.NoteData note)=>note.customValue!=0?note.customValue:m.GetSlidePath(type,start,end,star);
    public static int Areas(Manager.SlideManager m,int type,int start,int end,int star,Manager.NoteData note)=>note.customValue!=0?note.customValue*2:m.GetSlideHitArea(type,start,end,star);
    public static float Length(Manager.SlideManager m,int type,int start,int end,Manager.NoteData note)=>note.customValue!=0?note.customValue*3:m.GetSlideLength(type,start,end);
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,ILGenerator generator)=>SlideCallRedirector.Rewrite(instructions,generator,
        AccessTools.Method(typeof(Program),nameof(Path)),AccessTools.Method(typeof(Program),nameof(Areas)),AccessTools.Method(typeof(Program),nameof(Length)));
    static int Main(string[] args)
    {
        try
        {
            foreach(var path in args)Inspect(path);
            var h=new Harmony("sinmai-alpha.slide-owner-test");
            foreach(var name in new[]{"Initialize","GetSlideArrowNum","StarInitialize","FanInitialize"})
                h.Patch(AccessTools.Method(typeof(Fixture),name),transpiler:new HarmonyMethod(typeof(Program),nameof(Transpiler)));
            foreach(int headValue in new[]{0,7})foreach(int childValue in new[]{0,11})
            {
                // All fields deliberately identical: a redirect cannot infer which
                // segment this is from slideType/start/end or from the chain head.
                var tail=new Manager.NoteData{customValue=13};
                var child=new Manager.NoteData{customValue=childValue,child=new List<Manager.NoteData>{tail}};
                var head=new Manager.NoteData{customValue=headValue,child=new List<Manager.NoteData>{child}};
                var f=new Fixture();int a=headValue==0?1:headValue,b=childValue==0?1:childValue;
                Check(f.Initialize(head)==6*(a+b+13),"mixed chain owner");
                Check(f.finallyCount==1,"exception block retained");
                Check(f.GetSlideArrowNum(head)==a+b,"arrow child owner");
                Check(f.StarInitialize(head)==3*b,"star length child owner");
                Check(f.FanInitialize(head)==2*a,"constant-type fan owner");
            }
            var plain=new Manager.NoteData{child=new List<Manager.NoteData>{new Manager.NoteData()}};
            Check(new Fixture().Initialize(plain)==12,"ordinary behavior unchanged");
            h.UnpatchSelf();Console.WriteLine("PASS executed mixed/ordinary chains, arrow counts, star lengths, fan constants, foreach/finally IL");return 0;
        }
        catch(Exception e){Console.WriteLine("FAIL "+e);return 1;}
    }
    static void Inspect(string path)
    {
        var opcodes=typeof(OpCodes).GetFields(BindingFlags.Public|BindingFlags.Static).Where(f=>f.FieldType==typeof(OpCode)).Select(f=>(OpCode)f.GetValue(null)).ToDictionary(o=>o.Value);
        using(var asm=AssemblyDefinition.ReadAssembly(path))
        foreach(var type in new[]{"Monitor.SlideRoot","Monitor.SlideFan","Monitor.StarNote","Monitor.BreakStarNote"})
        foreach(var method in asm.MainModule.GetType(type).Methods.Where(m=>m.Name=="Initialize"||m.Name=="GetSlideArrowNum"&&m.Parameters.Count==1))
        {
            var input=method.Body.Instructions.Select(i=>new CodeInstruction(opcodes[i.OpCode.Value],i.Operand)).ToList();
            int expected=input.Count(i=>i.operand is MethodReference mr && mr.DeclaringType.Name=="SlideManager" && new[]{"GetSlidePath","GetSlideHitArea","GetSlideLength"}.Contains(mr.Name));
            var generator=new DynamicMethod("verify",typeof(void),Type.EmptyTypes).GetILGenerator();
            var output=Transpiler(input,generator).ToList();
            int actual=output.Count(i=>i.operand is MethodInfo mi && mi.DeclaringType==typeof(Program));
            Check(expected==actual&&actual>0,"real IL calls "+type+"."+method.Name);
            // The field receiver must be duplicated/captured at every access,
            // including NoteData locals in compiled child foreach loops.
            for(int i=0;i<output.Count;i++)
                if(output[i].opcode==OpCodes.Ldfld && output[i].operand is FieldReference field && field.DeclaringType.Name=="NoteData"&&(field.Name=="slideData"||field.Name=="startButtonPos"))
                    Check(i>=2&&output[i-2].opcode==OpCodes.Dup&&output[i-1].opcode==OpCodes.Stloc,"real IL field owner");
            Console.WriteLine($"IL {type}.{method.Name}: {actual}/{expected} redirects, receiver capture verified");
        }
    }
}
