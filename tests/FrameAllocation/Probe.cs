using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(PerformanceProbe), "Tint and frame allocation regression", "1.0", "Codex")]
[assembly: MelonGame("sega-interactive", "Sinmai")]
[assembly: HarmonyDontPatchAll]

// Empty-scene Unity 2018 harness. Set SINMAI_PROBE_TARGET and SINMAI_PROBE_OUTPUT.
// Use separate processes for baseline/candidate DLLs; compare PIXELS lines.
public class PerformanceProbe : MelonMod
{
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;
    readonly StringBuilder report = new StringBuilder();
    System.Type custom; Assembly asm; int frames; bool done;
    static NotesReader reader;
    object Call(string name, params object[] args) => AccessTools.Method(custom, name).Invoke(null, args);
    object Get(object obj, string name) => AccessTools.Field(obj.GetType(), name).GetValue(obj);
    void Set(object obj, string name, object value) => AccessTools.Field(obj.GetType(), name).SetValue(obj, value);
    void Check(bool pass, string message) { if (!pass) throw new Exception(message); report.AppendLine("PASS " + message); }
    public override void OnUpdate()
    {
        if (done || ++frames < 30) return;
        done = true;
        try { Run(); report.AppendLine("PASS ALL"); } catch (Exception e) { report.AppendLine("FAIL " + e); }
        File.WriteAllText(Environment.GetEnvironmentVariable("SINMAI_PROBE_OUTPUT"), report.ToString());
        Application.Quit();
    }
    void Run()
    {
        Application.logMessageReceived += (message, stack, kind) => { if (kind != LogType.Log) report.AppendLine("UNITY " + kind + " " + message); };
        asm = Assembly.LoadFrom(Environment.GetEnvironmentVariable("SINMAI_PROBE_TARGET"));
        custom = asm.GetType("SinmaiAlpha.Notes.CustomNoteTypes");
        report.AppendLine("ASSEMBLY " + asm.Location + " MVID=" + asm.ManifestModule.ModuleVersionId);
        Tint(); NativeEachBar(); Frames();
    }
    void Pixels(Sprite output, string label)
    {
        var data = Call("ReadVisualTexture", output);
        var values = (Color32[])Get(data, "Pixels"); var bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        { bytes[4*i] = values[i].r; bytes[4*i+1] = values[i].g; bytes[4*i+2] = values[i].b; bytes[4*i+3] = values[i].a; }
        using (var sha = SHA256.Create()) report.AppendLine("PIXELS " + label + " " + BitConverter.ToString(sha.ComputeHash(bytes)));
    }
    void Geometry(Sprite source, Sprite output)
    {
        Check(source != output && source.vertices.SequenceEqual(output.vertices) && source.triangles.SequenceEqual(output.triangles)
            && source.pivot == output.pivot && source.border == output.border && source.pixelsPerUnit == output.pixelsPerUnit,
            "generated sprite preserves source geometry");
    }
    void Tint()
    {
        var large = new Texture2D(1080, 1080, TextureFormat.RGBA32, false);
        large.SetPixels32(Enumerable.Repeat(new Color32(230,190,30,255),1080*1080).ToArray()); large.Apply(false,true);
        var largeSprite = Sprite.Create(large,new Rect(0,0,1080,1080),new Vector2(.5f,.5f),100,0,SpriteMeshType.FullRect);
        var watch = Stopwatch.StartNew(); Call("GetVisualTintSprite",largeSprite,0xE07026,Color.white); watch.Stop();
        report.AppendLine("COLD_MS " + watch.Elapsed.TotalMilliseconds.ToString("F3"));
        foreach (var rgb in new[]{0x666666,0xFF4444,0x888888,0xBBBBBB,0x444444,0xFFEEDD,0xFF7733,0xFFFFFF})
        { watch.Restart(); Call("GetVisualTintSprite",largeSprite,rgb,Color.white); watch.Stop(); report.AppendLine("BAKE_MS "+rgb.ToString("X6")+" "+watch.Elapsed.TotalMilliseconds.ToString("F3")); }
        var pixels = new Color32[128*128]; var random = new System.Random(56);
        for (int i=0;i<pixels.Length;i++) pixels[i]=new Color32((byte)random.Next(256),(byte)random.Next(256),(byte)random.Next(256),(byte)random.Next(256));
        var texture = new Texture2D(128,128,TextureFormat.RGBA32,false); texture.SetPixels32(pixels); texture.Apply(false,true);
        for (int mesh=0;mesh<3;mesh++)
        {
            var source=Sprite.Create(texture,mesh==2?new Rect(16,24,64,80):new Rect(0,0,128,128),new Vector2(.3f,.6f),80,0,mesh==1?SpriteMeshType.Tight:SpriteMeshType.FullRect,mesh==2?new Vector4(2,3,4,5):Vector4.zero);
            // OverrideGeometry takes pixel coordinates, not world-space vertices.
            if(mesh==1) { source.OverrideGeometry(new[]{new Vector2(0,0),new Vector2(128,0),new Vector2(64,128)},new ushort[]{0,1,2}); Check(source.vertices.Length==3,"triangle fixture accepted"); }
            foreach(var rgb in new[]{0xE07026,0xFF4444,0x888888,0x000000,0xFFFFFF})
            {
                var output=(Sprite)Call("GetVisualTintSprite",source,rgb,new Color(.7f,.9f,.5f,.6f));
                Geometry(source,output); Pixels(output,mesh+" "+rgb.ToString("X6"));
                Check(ReferenceEquals(output,Call("GetVisualTintSprite",source,rgb,new Color(.7f,.9f,.5f,.6f))),"tint cache hit");
            }
            var gray=(Sprite)Call("GetBorrowedGraySprite",source); Geometry(source,gray); Pixels(gray,mesh+" gray");
        }
        Call("ClearVisualTintCache");
    }
    void NativeEachBar()
    {
        var source=Resources.Load<Sprite>("process/game/sprites/guide/eachbar");
        Check(source!=null,"native EachBar loaded");
        report.AppendLine("NATIVE_EACHBAR rect="+source.rect+" vertices="+source.vertices.Length);
        foreach(var rgb in new[]{0x666666,0xFF4444,0x888888,0xBBBBBB,0x444444,0xFFEEDD,0xFF7733,0xFFFFFF})
        {
            var watch=Stopwatch.StartNew();var output=(Sprite)Call("GetVisualTintSprite",source,rgb,Color.white);watch.Stop();
            report.AppendLine("NATIVE_BAKE_MS "+rgb.ToString("X6")+" "+watch.Elapsed.TotalMilliseconds.ToString("F3"));
            Geometry(source,output);Pixels(output,"native "+rgb.ToString("X6"));
        }
        Call("ClearVisualTintCache");
    }
    static bool Enabled(ref bool __result) { __result=true; return false; }
    static bool Reader(ref NotesReader __result) { __result=reader; return false; }
    static bool SkipLifecycle() => false;
    void Frames()
    {
        reader=new NotesReader();
        var stub=new HarmonyLib.Harmony("perf.probe.visuals");
        stub.Patch(AccessTools.Method(custom,"FeaturesEnabled"),prefix:new HarmonyMethod(typeof(PerformanceProbe),nameof(Enabled)));
        stub.Patch(AccessTools.Method(typeof(NotesManager),"getReader"),prefix:new HarmonyMethod(typeof(PerformanceProbe),nameof(Reader)));
        foreach(var method in new[]{typeof(TapNote),typeof(NoteBase),typeof(SlideRoot)}.SelectMany(t=>t.GetMethods(Flags)).Where(m=>m.Name=="Awake" || m.Name=="OnEnable").Distinct())
            stub.Patch(method,prefix:new HarmonyMethod(typeof(PerformanceProbe),nameof(SkipLifecycle)));
        try
        {
            Check(NotesManager.Instance(0)!=null,"notes manager exists");
            var go=new GameObject("inactive controller");go.SetActive(false);var ctrl=go.AddComponent<GameCtrl>();
            var owners=(IDictionary)custom.GetField("VisualOwners",Flags).GetValue(null);
            var objects=new List<Component>();
            var leaseType=custom.GetNestedType("VisualLease",Flags);var payloadType=custom.GetNestedType("VisualPayload",Flags);
            for(int i=0;i<128;i++)
            {
                var obj=new GameObject("visual owner");obj.SetActive(false);
                Component owner=i%2==0?(Component)obj.AddComponent<TapNote>():obj.AddComponent<SlideRoot>();
                ((Behaviour)owner).enabled=false; obj.SetActive(true);
                Set(owner,"EndFlag",false);
                var payload=Activator.CreateInstance(payloadType,true);Set(payload,"Note",Activator.CreateInstance(asm.GetType("SinmaiAlpha.ChartVisuals.VisualNote")));Set(payload,"Stream","");
                var lease=Activator.CreateInstance(leaseType,true);Set(lease,"Payload",payload);Set(lease,"Monitor",0);owners.Add(owner,lease);objects.Add(owner);
            }
            var update=(Action<GameCtrl>)Delegate.CreateDelegate(typeof(Action<GameCtrl>),AccessTools.Method(custom,"AfterVisualFrame"));
            for(int i=0;i<10;i++)update(ctrl);
            var clock=new Stopwatch();
            GC.Collect();var gcMode=UnityEngine.Scripting.GarbageCollector.GCMode;
            UnityEngine.Scripting.GarbageCollector.GCMode=UnityEngine.Scripting.GarbageCollector.Mode.Disabled;
            try {
                var gc=GC.CollectionCount(0);long before=GC.GetTotalMemory(false);clock.Start();
                for(int i=0;i<2000;i++)update(ctrl);
                clock.Stop();long bytes=GC.GetTotalMemory(false)-before;
                report.AppendLine("FRAME_128 owners frames=2000 heapGrowthBytes="+bytes+" heapGrowthPerFrame="+(bytes/2000.0).ToString("F1")+" ms="+clock.Elapsed.TotalMilliseconds.ToString("F2"));
                Check(GC.CollectionCount(0)==gc,"no GC during allocation measurement");
            } finally { UnityEngine.Scripting.GarbageCollector.GCMode=gcMode; }
            Check(owners.Count==128,"live visual owners retained");
            Set(objects[0],"EndFlag",true);Set(objects[1],"EndFlag",true);
            objects[2].gameObject.SetActive(false);Object.DestroyImmediate(objects[3].gameObject);
            Set(owners[objects[4]],"Monitor",1);Set(objects[4],"EndFlag",true);
            update(ctrl);Check(owners.Count==124 && owners.Contains(objects[4]),"ended, inactive and destroyed owners removed; other monitor retained");
            var scratch=custom.GetField("VisualFrameOwners",Flags);
            if(scratch!=null)Check(((IList)scratch.GetValue(null)).Count==0,"scratch clears owner references after frame");
            var invalidParts=(IList)Get(owners[objects[5]],"Parts");invalidParts.Add(null);
            bool threw=false;try{update(ctrl);}catch(NullReferenceException){threw=true;}
            Check(threw,"injected visual update failure reached");
            if(scratch!=null)Check(((IList)scratch.GetValue(null)).Count==0,"scratch clears on exceptional exit");
            invalidParts.Clear();update(ctrl);Check(owners.Count==124,"next frame recovers after exceptional exit");
            owners.Clear();foreach(var owner in objects)if(owner!=null)Object.DestroyImmediate(owner.gameObject);
        }
        finally { stub.UnpatchSelf(); }
    }
}
