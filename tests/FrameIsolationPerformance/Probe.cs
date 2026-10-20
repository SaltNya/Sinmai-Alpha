using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Monitor;
using UnityEngine;
using Object = UnityEngine.Object;

[assembly: MelonInfo(typeof(IsolationProbe), "Frame isolation lookup regression", "1.0", "Codex")]
[assembly: MelonGame("sega-interactive", "Sinmai")]
[assembly: HarmonyDontPatchAll]
public class IsolationProbe : MelonMod
{
    readonly StringBuilder report = new StringBuilder();
    int frames; bool done; Assembly assembly; Component motion; Type motionType, setterType;
    static bool Skip() => false;
    void Check(bool value, string message)
    { if (!value) throw new Exception(message); report.AppendLine("PASS " + message); }
    object Call(string name, params object[] args) => AccessTools.Method(motionType, name).Invoke(motion, args);
    public override void OnUpdate()
    {
        if (done || ++frames < 30) return;
        done = true;
        try { Run(); report.AppendLine("PASS ALL"); }
        catch (Exception e) { report.AppendLine("FAIL " + e); }
        File.WriteAllText(Environment.GetEnvironmentVariable("SINMAI_PROBE_OUTPUT"), report.ToString());
        Application.Quit();
    }
    void Run()
    {
        var dependencies=Path.Combine(Environment.GetEnvironmentVariable("SINMAI_PROBE_ASSETS"),"..","RuntimeDependencies");
        foreach(var path in Directory.GetFiles(dependencies,"*.dll")) Assembly.LoadFrom(path);
        assembly = Assembly.LoadFrom(Environment.GetEnvironmentVariable("SINMAI_PROBE_TARGET"));
        motionType = assembly.GetType("SinmaiAlpha.Notes.ChartFrameMotion");
        setterType = AccessTools.TypeByName("ScreenClipSetter");
        Check(setterType != null, "native ScreenClipSetter available");
        report.AppendLine("MVID " + assembly.ManifestModule.ModuleVersionId + " CPU " + SystemInfo.processorType);
        report.AppendLine("Synthetic CPU microbenchmark in Unity 2018; not GPU time or player FPS.");
        var hooks = new HarmonyLib.Harmony("isolation.probe.lifecycle");
        foreach (var type in new[] { setterType, typeof(GameMonitor), typeof(CommonMonitor), typeof(MonitorBackgroundTownController) })
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "Awake" || m.Name == "OnEnable").Distinct())
                hooks.Patch(method, prefix: new HarmonyMethod(typeof(IsolationProbe), nameof(Skip)));
        var camera = new GameObject("isolated camera").AddComponent<Camera>(); camera.enabled = false;
        motion = camera.gameObject.AddComponent(motionType); ((Behaviour)motion).enabled = false;
        var background = new GameObject("background");
        var clipped = new GameObject("clipped"); clipped.transform.SetParent(background.transform);
        clipped.AddComponent(setterType);
        var renderer = clipped.AddComponent<SpriteRenderer>();
        var properties = new MaterialPropertyBlock();
        var minId = Shader.PropertyToID("_ClipMinX"); var maxId = Shader.PropertyToID("_ClipMaxX");
        var otherId = Shader.PropertyToID("_ProbeOther");
        Action<float,float> set = (min,max) => {
            properties.Clear(); properties.SetFloat(minId,min); properties.SetFloat(maxId,max);
            properties.SetFloat(otherId,17); renderer.SetPropertyBlock(properties);
        };
        var rectType = assembly.GetType("SinmaiAlpha.Notes.Libs.PresentationRect");
        Check(rectType != null, "presentation rect type available");
        var bounds = Activator.CreateInstance(rectType, new object[] { .25f, 0f, .5f, 1f });
        Action crop = () => { var args = new object[] { background, bounds, 1080, 1920, null }; CheckCrop(args); };
        Action lease = () => Call("LeaseScreenClips", background, bounds);
        Action restore = () => Call("RestoreScreenClips");
        set(.25f,.75f); crop(); lease(); renderer.GetPropertyBlock(properties);
        Check(properties.GetFloat(minId)==0 && properties.GetFloat(maxId)==1, "clip coordinates remap to cropped viewport");
        Check(properties.GetFloat(otherId)==17, "unrelated material properties preserved");
        restore(); renderer.GetPropertyBlock(properties);
        Check(properties.GetFloat(minId)==.25f && properties.GetFloat(maxId)==.75f, "native clip values restored");
        crop(); lease(); renderer.GetPropertyBlock(properties); properties.SetFloat(minId,.1f);
        renderer.SetPropertyBlock(properties); restore(); renderer.GetPropertyBlock(properties);
        Check(properties.GetFloat(minId)==.1f && properties.GetFloat(maxId)==.75f, "newer external clip write survives restore");
        set(.25f,.75f); clipped.SetActive(false); crop(); lease(); renderer.GetPropertyBlock(properties);
        Check(properties.GetFloat(minId)==0 && properties.GetFloat(maxId)==1, "inactive clip components preserved"); restore();
        clipped.SetActive(true);
        Object.DestroyImmediate(clipped.GetComponent(setterType)); crop(); lease(); renderer.GetPropertyBlock(properties);
        Check(properties.GetFloat(minId)==.25f, "removed clip component stops remapping immediately"); restore();
        clipped.AddComponent(setterType); crop(); lease(); renderer.GetPropertyBlock(properties);
        Check(properties.GetFloat(minId)==0, "new clip component picked up immediately"); restore();
        var oldBackground=background; background=new GameObject("replacement background");
        clipped.transform.SetParent(background.transform); crop(); lease(); renderer.GetPropertyBlock(properties);
        Check(properties.GetFloat(maxId)==1, "replacement background discovered"); restore(); Object.DestroyImmediate(oldBackground);
        set(.33333f,.75f);
        var fractional = new object[] { background, bounds, 1080, 1920, null }; CheckCrop(fractional);
        var cropType=fractional[4].GetType();
        Check((int)AccessTools.Field(cropType,"Width").GetValue(fractional[4])==1080, "fractional native clip retains full projection"); restore();
        set(.25f,.75f);
        for(int i=0;i<2048;i++) { var go=new GameObject("plain sprite"); go.transform.SetParent(background.transform); go.AddComponent<SpriteRenderer>(); }
        for(int i=0;i<16;i++){crop();lease();restore();}
        for(int round=0;round<3;round++) {
            var clock=Stopwatch.StartNew(); for(int i=0;i<100;i++){crop();lease();restore();} clock.Stop();
            report.AppendLine("CLIP_BENCH renderers=2049 setters=1 round="+round+" meanMs="+(clock.Elapsed.TotalMilliseconds/100).ToString("F4"));
        }
        var ownerGo=new GameObject("owner");ownerGo.SetActive(false);var owner=ownerGo.AddComponent<GameMonitor>();
        var commonGo=new GameObject("common");commonGo.SetActive(false);var common=commonGo.AddComponent<CommonMonitor>();
        common.enabled=false;commonGo.SetActive(true);
        var bgGo=new GameObject("native background");bgGo.SetActive(false);var bg=bgGo.AddComponent<MonitorBackgroundTownController>();
        AccessTools.Field(typeof(CommonMonitor),"_backgroundController").SetValue(common,bg);
        var getBackground=(Func<GameMonitor,MonitorBackgroundTownController>)Delegate.CreateDelegate(
            typeof(Func<GameMonitor,MonitorBackgroundTownController>),AccessTools.Method(motionType,"NativeBackgroundFor"));
        Check(getBackground(owner)==bg,"native background lookup");
        var watch=Stopwatch.StartNew();for(int i=0;i<200;i++) getBackground(owner);watch.Stop();
        report.AppendLine("BACKGROUND_BENCH meanMs="+(watch.Elapsed.TotalMilliseconds/200).ToString("F4"));
        AccessTools.Field(typeof(CommonMonitor),"_backgroundController").SetValue(common,null);
        Check(getBackground(owner)==null,"cached common monitor observes changed background controller");
        Object.DestroyImmediate(commonGo);
        var replacement=new GameObject("new common");replacement.SetActive(false);var newCommon=replacement.AddComponent<CommonMonitor>();
        newCommon.enabled=false;AccessTools.Field(typeof(CommonMonitor),"_backgroundController").SetValue(newCommon,bg);replacement.SetActive(true);
        Check(getBackground(owner)==bg,"destroyed common monitor replaced");
        var custom=assembly.GetType("SinmaiAlpha.Notes.CustomNoteTypes");
        var maskProperty=AccessTools.Property(custom,"PresentationMask");
        var maskGetter=maskProperty==null ? (Func<GameObject>)(()=>GameObject.Find("Mask")) :
            (Func<GameObject>)Delegate.CreateDelegate(typeof(Func<GameObject>),maskProperty.GetGetMethod(true));
        var mask=new GameObject("Mask");
        Check(maskGetter()==mask,"mask lookup");
        var pool=new GameObject("pooled notes");
        for(int i=0;i<20000;i++){var child=new GameObject("inactive note");child.transform.SetParent(pool.transform);child.SetActive(false);}
        watch.Restart();for(int i=0;i<200;i++)maskGetter();watch.Stop();
        report.AppendLine("MASK_BENCH inactiveObjects=20000 meanMs="+(watch.Elapsed.TotalMilliseconds/200).ToString("F4"));
        mask.SetActive(false);Check(maskGetter()==null,"inactive mask not returned");
        mask.SetActive(true);Check(maskGetter()==mask,"reactivated mask found");
        mask.name="renamed mask";Check(maskGetter()==null,"renamed mask discarded");
        Object.DestroyImmediate(mask);mask=new GameObject("Mask");Check(maskGetter()==mask,"destroyed mask replaced");
        var diagnostics=assembly.GetType("SinmaiAlpha.Hosting.GameplayDiagnostics");
        var phase=diagnostics.GetNestedType("PhaseTimingPatch");
        var targets=((System.Collections.Generic.IEnumerable<MethodBase>)AccessTools.Method(phase,"TargetMethods").Invoke(null,null)).ToArray();
        var capacity=((Array)AccessTools.Field(diagnostics,"PhaseMs").GetValue(null)).Length;
        Check(targets.Length<=capacity,"diagnostic phase capacity sufficient");
        var phaseHooks=new HarmonyLib.Harmony("isolation.probe.diagnostics");
        phaseHooks.PatchAll(phase);crop();lease();restore();phaseHooks.UnpatchSelf();
        Check(true,"diagnostic hooks bind and execute");
        Call("Clear");hooks.UnpatchSelf();
    }
    void CheckCrop(object[] args)
    {
        if (!(bool)Call("TryIsolationCrop",args)) throw new Exception("Crop rejected");
    }
}
