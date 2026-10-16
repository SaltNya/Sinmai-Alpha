using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using MelonLoader;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;
using UnityEngine.UI;
using Object=UnityEngine.Object;
[assembly:MelonInfo(typeof(MotionProbe),"Frame motion isolated measurement","1.0","Codex")]
[assembly:MelonGame("sega-interactive","Sinmai")]
[assembly:HarmonyDontPatchAll]
public class MotionProbe:MelonMod {
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 readonly StringBuilder report=new StringBuilder(); int ticks; bool done; Assembly asm; System.Type type; Component motion;
 static readonly Dictionary<string,double> totals=new Dictionary<string,double>();
 static readonly Dictionary<string,int> counts=new Dictionary<string,int>();
 static bool Skip()=>false;
 static void Prefix(out long __state){__state=Stopwatch.GetTimestamp();}
 static void Postfix(MethodBase __originalMethod,long __state){var n=__originalMethod.Name;if(!totals.ContainsKey(n)){totals[n]=0;counts[n]=0;}totals[n]+=(Stopwatch.GetTimestamp()-__state)*1000.0/Stopwatch.Frequency;counts[n]++;}
 object Get(object o,string n)=>AccessTools.Field(o.GetType(),n).GetValue(o);
 void Set(object o,string n,object v)=>AccessTools.Field(o.GetType(),n).SetValue(o,v);
 object Call(object o,string n,params object[] a)=>AccessTools.Method(o.GetType(),n).Invoke(o,a);
 Action Action(object o,string n)=>(Action)Delegate.CreateDelegate(typeof(Action),o,AccessTools.Method(o.GetType(),n));
 object Track(string kind,float value){var t=asm.GetType("SinmaiAlpha.ChartVisuals.PresentationChange");var e=Activator.CreateInstance(t);Set(e,"Kind",kind);Set(e,"Target",value);var a=Array.CreateInstance(t,1);a.SetValue(e,0);return Activator.CreateInstance(asm.GetType("SinmaiAlpha.ChartVisuals.PresentationCommands+Track"),new object[]{a,kind});}
 public override void OnUpdate(){if(done||++ticks<30)return;done=true;try{Run();report.AppendLine("PASS ALL");}catch(Exception e){report.AppendLine("FAIL "+e);}File.WriteAllText(Environment.GetEnvironmentVariable("SINMAI_PROBE_OUTPUT"),report.ToString());Application.Quit();}
 void Run(){
  Application.logMessageReceived+=(s,trace,k)=>{if(k==LogType.Error||k==LogType.Exception)report.AppendLine("UNITY_ERROR "+s);};
  Assembly.LoadFrom(Path.Combine(Environment.GetEnvironmentVariable("SINMAI_PROBE_ASSETS"),"..","RuntimeDependencies","System.Numerics.dll"));
  asm=Assembly.LoadFrom(Environment.GetEnvironmentVariable("SINMAI_PROBE_TARGET"));type=asm.GetType("SinmaiAlpha.Notes.ChartFrameMotion");
  report.AppendLine("MVID "+asm.ManifestModule.ModuleVersionId+" CPU "+SystemInfo.processorType+" GPU "+SystemInfo.graphicsDeviceName);
  report.AppendLine("Synthetic single-player scene. Actual Unity 2018 camera render, no game judgment/audio/video/background shaders. Inclusive CPU wall-time, NOT GPU timing or player FPS. 1080x1920 MSAA1. 3x40 samples after warmup.");
  var lifecycle=new HarmonyLib.Harmony("motion.lifecycle");
  foreach(var t in new[]{typeof(GameCtrl),typeof(GameMonitor)}) foreach(var m in t.GetMethods(F).Where(m=>m.Name=="Awake"||m.Name=="OnEnable").Distinct())lifecycle.Patch(m,prefix:new HarmonyMethod(typeof(MotionProbe),nameof(Skip)));
  var ownerGo=new GameObject("owner");ownerGo.SetActive(false);var owner=ownerGo.AddComponent<GameMonitor>();
  var ctrlGo=new GameObject("controller");ctrlGo.SetActive(false);var ctrl=ctrlGo.AddComponent<GameCtrl>();ctrl.enabled=false;ctrlGo.SetActive(true);
  var mainGo=new GameObject("main",typeof(RectTransform),typeof(Canvas));var main=(RectTransform)mainGo.transform;main.sizeDelta=new Vector2(10.8f,19.2f);var canvas=mainGo.GetComponent<Canvas>();canvas.renderMode=RenderMode.WorldSpace;
  var camGo=new GameObject("source camera");var cam=camGo.AddComponent<Camera>();cam.enabled=false;cam.orthographic=true;cam.orthographicSize=9.6f;cam.aspect=1080f/1920;cam.transform.position=new Vector3(0,0,-10);cam.clearFlags=CameraClearFlags.SolidColor;cam.backgroundColor=Color.black;cam.allowMSAA=false;
  var src=new RenderTexture(1080,1920,24);src.Create();var dst=new RenderTexture(1080,1920,0);dst.Create();cam.targetTexture=src;
  motion=camGo.AddComponent(type);((Behaviour)motion).enabled=false;
  var ft=type.GetNestedType("Frame",F);var frame=Activator.CreateInstance(ft,true);Set(frame,"Owner",owner);Set(frame,"Controller",ctrl);Set(frame,"Main",main);Set(frame,"Bounds",main);
  ((IDictionary)Get(motion,"frames")).Add(owner,frame);
  var st=type.GetNestedType("IsolationSurface",F);var surface=Activator.CreateInstance(st,true);Set(surface,"Owner",owner);Set(surface,"Controller",ctrl);Set(surface,"Canvas",canvas);((IList)Get(motion,"isolationSurfaces")).Add(surface);Set(motion,"isolationSurfacesDirty",false);
  if(!(bool)Call(motion,"ReserveLayers",1))throw new Exception("No isolation layer");
  AccessTools.Field(typeof(NotesManager),"_curMSec").SetValue(null,500f);
  var tex=new Texture2D(16,16);tex.SetPixels32(Enumerable.Repeat(new Color32(200,100,40,255),256).ToArray());tex.Apply();var sprite=Sprite.Create(tex,new Rect(0,0,16,16),new Vector2(.5f,.5f),32);
  var sprites=new List<GameObject>();for(int i=0;i<1024;i++){var go=new GameObject("sprite");go.transform.SetParent(ctrlGo.transform,false);go.transform.localPosition=new Vector3(i%24*.4f-4.6f,(i/24%40)*.4f-8,0);go.AddComponent<SpriteRenderer>().sprite=sprite;sprites.Add(go);}
  for(int i=0;i<32;i++){var go=new GameObject("UI",typeof(RectTransform),typeof(Image));go.transform.SetParent(main,false);var r=(RectTransform)go.transform;r.sizeDelta=new Vector2(.5f,.5f);r.localPosition=new Vector3(i%8-3.5f,i/8-1.5f,0);go.GetComponent<Image>().sprite=sprite;}
  var zoom=Track("zoom",-.7f);var rotate=Track("rotate",-30f);
  var late=Action(motion,"LateUpdate");var pre=Action(motion,"OnPreCull");var post=Action(motion,"OnPostRender");var restoreCanvas=Action(frame,"RestoreCanvas");
  var restore=(Action<bool>)Delegate.CreateDelegate(typeof(Action<bool>),motion,AccessTools.Method(type,"RestoreIsolation"));
  var composite=(Action<RenderTexture,RenderTexture>)Delegate.CreateDelegate(typeof(Action<RenderTexture,RenderTexture>),motion,AccessTools.Method(type,"CompositeIsolation"));
  var hooks=new HarmonyLib.Harmony("motion.timing");foreach(var n in new[]{"LateUpdate","OnPreCull","PrepareIsolation","HasLayerCollision","OnPostRender","CompositeIsolation"})hooks.Patch(AccessTools.Method(type,n),prefix:new HarmonyMethod(typeof(MotionProbe),nameof(Prefix)),postfix:new HarmonyMethod(typeof(MotionProbe),nameof(Postfix)));
  // Recreate delegates after Harmony patches.
  late=Action(motion,"LateUpdate");pre=Action(motion,"OnPreCull");post=Action(motion,"OnPostRender");composite=(Action<RenderTexture,RenderTexture>)Delegate.CreateDelegate(typeof(Action<RenderTexture,RenderTexture>),motion,AccessTools.Method(type,"CompositeIsolation"));
  Action step=()=>{late();Canvas.ForceUpdateCanvases();pre();cam.Render();post();if(((IList)Get(motion,"isolatedFrames")).Count>0)composite(src,dst);else Graphics.Blit(src,dst);restore(true);restoreCanvas();};
  foreach(int count in new[]{64,256,1024}){
   for(int i=0;i<sprites.Count;i++)sprites[i].SetActive(i<count);
   for(int round=0;round<3;round++) foreach(bool active in (round%2==0?new[]{false,true}:new[]{true,false})){
    Set(frame,"Zoom",active?zoom:null);Set(frame,"Rotate",active?rotate:null);
    for(int i=0;i<8;i++)step();totals.Clear();counts.Clear();var watch=Stopwatch.StartNew();for(int i=0;i<40;i++)step();watch.Stop();
    if((bool)Get(frame,"Active")!=active)throw new Exception("Wrong active state");
    if(counts["OnPreCull"]!=40)throw new Exception("Unexpected automatic motion callback");
    if(active&&(bool)Get(motion,"isolationWarning"))throw new Exception("Isolation failed");
    if(sprites[0].transform.localScale!=Vector3.one||mainGo.layer!=0||sprites[0].layer!=0)throw new Exception("Leases not restored");
    report.AppendLine("BENCH sprites="+count+" ui=32 active="+active+" round="+round+" fullMeanMs="+(watch.Elapsed.TotalMilliseconds/40).ToString("F3")+" "+string.Join(" ",totals.Select(p=>p.Key+"="+(p.Value/40).ToString("F3"))));
    if(round==2)Pixels(dst,"single-"+count+"-"+active);
   }
  }
  hooks.UnpatchSelf();restore(true);restoreCanvas();
  // Measure scene-wide discovery separately, with all renderers enabled but no sprite draw.
  var collision=(Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>),motion,AccessTools.Method(type,"HasLayerCollision"));
  for(int i=0;i<sprites.Count;i++)sprites[i].SetActive(true);
  var extras=new List<GameObject>();for(int i=0;i<4096;i++){var go=new GameObject("unrelated renderer");go.AddComponent<SpriteRenderer>().enabled=false;extras.Add(go);}
  foreach(int count in new[]{0,1024,4096}){
   for(int i=0;i<extras.Count;i++)extras[i].SetActive(i<count);
   for(int i=0;i<10;i++)collision();var watch=Stopwatch.StartNew();for(int i=0;i<100;i++)if(collision())throw new Exception("Unexpected layer collision");watch.Stop();
   report.AppendLine("SCAN visibleObjects="+(1024+count)+" meanMs="+(watch.Elapsed.TotalMilliseconds/100).ToString("F3"));
  }
  var poolRoot=new GameObject("nested pool");foreach(var go in extras)go.transform.SetParent(poolRoot.transform,false);
  for(int i=0;i<20000;i++){var go=new GameObject("inactive pooled arrow");go.transform.SetParent(poolRoot.transform,false);go.AddComponent<SpriteRenderer>();go.SetActive(false);}
  foreach(int count in new[]{0,1024,4096}){
   for(int i=0;i<extras.Count;i++)extras[i].SetActive(i<count);
   for(int i=0;i<10;i++)collision();var watch=Stopwatch.StartNew();for(int i=0;i<100;i++)if(collision())throw new Exception("Unexpected nested layer collision");watch.Stop();
   report.AppendLine("NESTED_SCAN activeRenderers="+(1024+count)+" inactive=20000 meanMs="+(watch.Elapsed.TotalMilliseconds/100).ToString("F3"));
  }
  int layer=((IList)Get(motion,"isolationLayers")).Cast<int>().First();
  var intruder=new GameObject("late collision");intruder.transform.SetParent(poolRoot.transform,false);var sr=intruder.AddComponent<SpriteRenderer>();sr.enabled=false;
  intruder.layer=layer;Check(collision(),"disabled Renderer collision");intruder.SetActive(false);Check(!collision(),"inactive Renderer ignored");intruder.SetActive(true);Check(collision(),"newly activated Renderer found immediately");intruder.layer=0;Check(!collision(),"changed layer clears immediately");
  var scene=UnityEngine.SceneManagement.SceneManager.CreateScene("collision additive");intruder.transform.SetParent(null);UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(intruder,scene);intruder.layer=layer;Check(collision(),"additive-scene collision");Object.DestroyImmediate(intruder);
  var persistent=new GameObject("persistent canvas",typeof(RectTransform),typeof(Canvas));Object.DontDestroyOnLoad(persistent);persistent.layer=layer;Check(collision(),"persistent Canvas collision");Object.DestroyImmediate(persistent);Check(!collision(),"destroyed persistent Canvas no collision");
  // Two independent monitor surfaces; verify all active/passive combinations.
  poolRoot.SetActive(false);main.sizeDelta=new Vector2(5.4f,19.2f);main.position=new Vector3(-2.7f,0,0);
  var owner2Go=new GameObject("owner2");owner2Go.SetActive(false);var owner2=owner2Go.AddComponent<GameMonitor>();
  var ctrl2Go=new GameObject("controller2");ctrl2Go.SetActive(false);var ctrl2=ctrl2Go.AddComponent<GameCtrl>();ctrl2.enabled=false;ctrl2Go.SetActive(true);var s2=ctrl2Go.AddComponent<SpriteRenderer>();s2.sprite=sprite;s2.color=Color.cyan;ctrl2Go.transform.position=new Vector3(2.7f,0,0);ctrl2Go.transform.localScale=new Vector3(8,8,1);
  var main2Go=new GameObject("main2",typeof(RectTransform),typeof(Canvas));var main2=(RectTransform)main2Go.transform;main2.sizeDelta=new Vector2(5.4f,19.2f);main2.position=new Vector3(2.7f,0,0);var canvas2=main2Go.GetComponent<Canvas>();canvas2.renderMode=RenderMode.WorldSpace;
  var frame2=Activator.CreateInstance(ft,true);Set(frame2,"Owner",owner2);Set(frame2,"Controller",ctrl2);Set(frame2,"Main",main2);Set(frame2,"Bounds",main2);((IDictionary)Get(motion,"frames")).Add(owner2,frame2);
  var surface2=Activator.CreateInstance(st,true);Set(surface2,"Owner",owner2);Set(surface2,"Controller",ctrl2);Set(surface2,"Canvas",canvas2);((IList)Get(motion,"isolationSurfaces")).Add(surface2);Check((bool)Call(motion,"ReserveLayers",2),"two isolation layers");
  foreach(bool a in new[]{false,true})foreach(bool b in new[]{false,true}){Set(frame,"Zoom",a?zoom:null);Set(frame,"Rotate",a?rotate:null);Set(frame2,"Zoom",b?zoom:null);Set(frame2,"Rotate",b?rotate:null);step();Pixels(dst,"dual-"+a+"-"+b);Action(frame2,"RestoreCanvas")();Check(ctrl2Go.layer==0&&main2Go.layer==0,"second player layers restored");}
  Call(motion,"Clear");lifecycle.UnpatchSelf();
 }
 void Check(bool value,string name){if(!value)throw new Exception(name);report.AppendLine("PASS "+name);}
 void Pixels(RenderTexture source,string label){var previous=RenderTexture.active;RenderTexture.active=source;var t=new Texture2D(source.width,source.height,TextureFormat.RGBA32,false);t.ReadPixels(new Rect(0,0,source.width,source.height),0,0);var c=t.GetPixels32();var bytes=new byte[c.Length*4];for(int i=0;i<c.Length;i++){bytes[i*4]=c[i].r;bytes[i*4+1]=c[i].g;bytes[i*4+2]=c[i].b;bytes[i*4+3]=c[i].a;}using(var sha=System.Security.Cryptography.SHA256.Create())report.AppendLine("PIXELS "+label+" "+BitConverter.ToString(sha.ComputeHash(bytes)));Object.DestroyImmediate(t);RenderTexture.active=previous;}
}
