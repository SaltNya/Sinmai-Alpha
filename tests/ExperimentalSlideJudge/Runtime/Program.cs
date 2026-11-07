using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using DB;
using HarmonyLib;
using Manager;
using Monitor;
using SinmaiAlpha.Hosting;
using SinmaiAlpha.Notes;
using SinmaiAlpha.Notes.Libs;
using SinmaiAlpha.Notes.ExperimentalSlideJudge;

static class Program
{
    static int checks;
    static void Check(bool good,string why){checks++;if(!good)throw new Exception(why);}
    static System.Collections.Generic.List<SlideManager.HitArea> Areas(CustomSlideNoteData n,int star=0)
        => CustomNoteTypes.GetSlideHitAreaRedirect(SlideManager.Instance,n.slideData.type,n.startButtonPos,n.slideData.targetNote,star,n);
    static CustomSlideNoteData Note(string code="curve",int key=0)
    {
        var n=new CustomSlideNoteData{startButtonPos=key};
        n.ParseSlideCode(code,OptionMirrorID.Normal);return n;
    }
    static void ConfigTests(string path)
    {
        int warnings=0;
        foreach(var encoding in new Encoding[]{new UTF8Encoding(false),new UTF8Encoding(true),Encoding.Unicode,Encoding.BigEndianUnicode,Encoding.UTF32})
        {
            File.WriteAllText(path,"{\"ExperimentalSlideJudgment\":true,\"Other\":\"测试\"}",encoding);
            Check(ExperimentalJudgeConfig.Read(path,_=>warnings++),"BOM/config true: "+encoding.WebName);
        }
        foreach(var json in new[]{"{}","{\"ExperimentalSlideJudgment\":false}","{broken","null","","{\"ExperimentalSlideJudgment\":[]}","{\"ExperimentalSlideJudgment\":{}}"})
        {
            File.WriteAllText(path,json);
            Check(!ExperimentalJudgeConfig.Read(path,_=>warnings++),"invalid/missing/false off: "+json);
        }
        File.Delete(path);Check(!ExperimentalJudgeConfig.Read(path,_=>warnings++),"absent config off");
        Check(warnings>=4,"invalid settings diagnosed");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Exercise(bool enabled,string path)
    {
        var n=Note();var root=new SlideRoot();root.Initialize(n);
        Check((root.Areas.Count==3)==enabled,"getter hooked only when enabled");
        Check(ReferenceEquals(Areas(n),n.SlideHitAreasList[0])!=enabled,"published unified table / optional contour lookup");
        if(enabled)
        {
            Check(RuntimeCache.TryPlayer(root,out _),"input attached");
            Check(Math.Abs(root.LastWait-500)<.001,"native Initialize computes new tail wait");
            InputManager.Pressed[0]=1UL<<8;root.NoteCheck();
            Check(root.Index==1&&root.Entered&&root.NativeTouchCalls==0,"new input gate runs");
            root.NoteCheck();Check(root.Index==1,"held input stable");
            InputManager.Pressed[0]=1UL<<4;root.NoteCheck();Check(root.Index==3,"release/finish progresses");
            var second=new SlideRoot{MonitorId=1};second.Initialize(Note(key:3));
            InputManager.Pressed[0]=0;InputManager.Pressed[1]=1UL<<8;second.NoteCheck();
            Check(second.Index==1&&second.Entered,"second monitor and rotated key use physical input");
            // A pooled object reinitializing a native/fallback note loses state.
            root.Initialize(new NoteData());Check(!RuntimeCache.TryPlayer(root,out _),"pooled native clears experiment");
            InputManager.Pressed[0]=1;root.NoteCheck();Check(root.NativeTouchCalls>0,"native input stays native");
            foreach(string code in new[]{"v","fan","reject"})
            {
                var old=Note(code);root.Initialize(old);
                Check(!RuntimeCache.TryPlayer(root,out _)&&ReferenceEquals(Areas(old),old.SlideHitAreasList[0]),"fallback "+code);
            }
            // Parsing occurs before calcSlide assigns final children/types.
            var head=Note();head.child=new(){new NoteData()};root.Initialize(head);
            Check(!RuntimeCache.TryPlayer(root,out _)&&ReferenceEquals(Areas(head),head.SlideHitAreasList[0]),"late connected head stays on unified table");
            var child=Note();child.type.Value=NotesTypeID.Def.ConnectSlide;root.Initialize(child);
            Check(!RuntimeCache.TryPlayer(root,out _)&&ReferenceEquals(Areas(child),child.SlideHitAreasList[0]),"custom chain child stays on unified table");
            head.child=new(){child};
            // Distinguish the child from its parent. Legacy arg1 incorrectly
            // reuses the head's areas; the enabled rewrite must keep the child.
            child.SlideHitAreasList[0][1].HitPoints[0]=InputManager.TouchPanelArea.B8;
            root.Initialize(head);
            Check(root.Areas.Last().HitPoints[0]==InputManager.TouchPanelArea.B8,"actual connected child receiver");
            Check(root.GetSlideArrowNum(head)==6,"connected arrow count includes both segments");
            var reused=Note();reused.ParseSlideCode("reject",OptionMirrorID.Normal);
            Check(!RuntimeCache.TryFrame(reused,0,out _),"failed compile clears stale cache");
            reused.ParseSlideCode("curve",OptionMirrorID.Normal);reused.ParseSlideCode("invalid",OptionMirrorID.Normal);
            Check(!RuntimeCache.TryFrame(reused,0,out _),"failed parse clears stale cache");
            var changed=Note();root.Initialize(changed);RuntimeCache.Attach(root,changed,SlideManager.Legacy());
            Check(!RuntimeCache.TryPlayer(root,out _),"other mod table is not claimed");
            root.Initialize(Note());root.AutoPlay=true;root.NoteCheck();Check(root.Index==3,"native autoplay remains in control");
            new Process.GameProcess().OnRelease();
            Check(!RuntimeCache.TryPlayer(root,out _)&&!RuntimeCache.TryFrame(n,0,out _),"release clears notes and players");
            WeakReference[] weak=CreateWeakOwners();
            for(int i=0;i<3;i++){GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
            Check(weak.All(w=>!w.IsAlive),"weak caches do not retain chart or pooled owner");
        }
        else
        {
            Check(NativeTableCompiler.Calls==0,"disabled never compiles experimental geometry");
            Check(SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified.UnifiedSlideNoteData.Calls==0,"disabled also bypasses earlier unified changes");
            Check(!RuntimeCache.TryPlayer(root,out _),"disabled never attaches input");
            Check(Math.Abs(root.LastWait-1000f/3)<.001,"original distances retained");
        }
        // Config changes take effect next launch, never during this process.
        File.WriteAllText(path,"{\"ExperimentalSlideJudgment\":"+(enabled?"false":"true")+"}");
        Check(ExperimentalJudgeConfig.Enabled==enabled,"startup snapshot fixed");
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    static WeakReference[] CreateWeakOwners()
    {
        var n=Note();var root=new SlideRoot();root.Initialize(n);
        return new[]{new WeakReference(n),new WeakReference(root)};
    }
    static int Main(string[] args)
    {
        bool enabled=args.Contains("on");
        AssetFiles.Root=Path.Combine(Path.GetTempPath(),"Alpha-Experimental-Judge-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(AssetFiles.Root);string path=Path.Combine(AssetFiles.Root,"config.json");
        var harmony=new Harmony("tests.alpha.experimental."+(enabled?"on":"off"));
        try
        {
            ConfigTests(path);
            foreach(string arg in args.Where(a=>a.EndsWith(".dll",StringComparison.OrdinalIgnoreCase)))GameContract.Verify(arg);
            File.WriteAllText(path,enabled?"{\"ExperimentalSlideJudgment\":true}":"{}");
            // Same lifecycle discovery/signature as Plugin.Apply.
            var lifecycle=typeof(CustomNoteTypes).GetMethod("OnBeforePatch",BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic);
            lifecycle.Invoke(null,new object[]{harmony});
            int installed=harmony.GetPatchedMethods().Count();
            Check(installed==(enabled?14:0),"expected experimental patched method count "+installed);
            harmony.PatchAll(typeof(CustomNoteTypes));
            harmony.PatchAll(typeof(CustomNoteTypes.SlideNoteDataHack));
            Exercise(enabled,path);
            Console.WriteLine($"PASS Harmony/config integration ({(enabled?"on":"off")}): {checks} checks, {installed} patches. Fixture game APIs; no Unity playtest.");
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
        finally{harmony.UnpatchSelf();if(File.Exists(path))File.Delete(path);Directory.Delete(AssetFiles.Root);}
    }
}
