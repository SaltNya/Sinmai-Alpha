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
using MelonLoader;
using UnityEngine;
[assembly:MelonInfo(typeof(NoisePerfProbe),"Noise raster parity and performance","1.0","Codex")]
[assembly:MelonGame("sega-interactive","Sinmai")]
[assembly:HarmonyDontPatchAll]
public class NoisePerfProbe:MelonMod
{
 const BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 readonly StringBuilder report=new StringBuilder(); int frames; bool done; double time=.5;
 static readonly Dictionary<string,double> totals=new Dictionary<string,double>();
 static void Prefix(out long __state){__state=Stopwatch.GetTimestamp();}
 static void Postfix(MethodBase __originalMethod,long __state){
  var key=__originalMethod.Name; if(!totals.ContainsKey(key))totals[key]=0;
  totals[key]+=(Stopwatch.GetTimestamp()-__state)*1000.0/Stopwatch.Frequency;
 }
 object Call(object obj,string name,params object[] args)=>obj.GetType().GetMethods(F).Single(m=>m.Name==name&&m.GetParameters().Length==args.Length).Invoke(obj,args);
 object Get(object obj,string name)=>obj.GetType().GetField(name,F).GetValue(obj);
 void Set(object obj,string name,object val)=>obj.GetType().GetField(name,F).SetValue(obj,val);
 void WriteHash(string label,byte[] bytes){using(var sha=SHA256.Create())report.AppendLine("HASH "+label+" "+BitConverter.ToString(sha.ComputeHash(bytes)));}
 void HashColors(string label,Color32[] colors){var bytes=new byte[colors.Length*4];for(int i=0;i<colors.Length;i++){bytes[4*i]=colors[i].r;bytes[4*i+1]=colors[i].g;bytes[4*i+2]=colors[i].b;bytes[4*i+3]=colors[i].a;}WriteHash(label,bytes);}
 public override void OnUpdate(){
  if(done||++frames<30)return;done=true;
  try{Run();report.AppendLine("PASS ALL");}catch(Exception e){report.AppendLine("FAIL "+e);}
  File.WriteAllText(Environment.GetEnvironmentVariable("SINMAI_PROBE_OUTPUT"),report.ToString());Application.Quit();
 }
 void Run(){
  Assembly.LoadFrom(Path.Combine(Environment.GetEnvironmentVariable("SINMAI_PROBE_ASSETS"), "..", "RuntimeDependencies", "System.Numerics.dll"));
  var asm=Assembly.LoadFrom(Environment.GetEnvironmentVariable("SINMAI_PROBE_TARGET"));
  report.AppendLine("MVID "+asm.ManifestModule.ModuleVersionId);
  var custom=asm.GetType("SinmaiAlpha.Notes.CustomNoteTypes");
  var graphics=custom.GetNestedType("NoiseGraphics",F);
  var bundle=AssetBundle.LoadFromFile(Environment.GetEnvironmentVariable("SINMAI_PROBE_ASSETS")+"/Alpha/AlphaNoise.ab");
  if(bundle==null)throw new Exception("Missing noise bundle");
  var shader=bundle.LoadAsset<Shader>("assets/aquamai/alphanoisecomposite.shader");
  var warning=bundle.LoadAsset<Shader>("assets/aquamai/alphanoisezone.shader");
  var displacement=bundle.LoadAsset<Texture2D>("assets/aquamai/blocknoise1.texture");
  var spark=bundle.LoadAsset<Texture2D>("assets/aquamai/pointnoise.texture");
  graphics.GetField("Displacement",F).SetValue(null,displacement);graphics.GetField("Spark",F).SetValue(null,spark);
  var displayType=custom.GetNestedType("NoiseZoneDisplay",F);var compType=custom.GetNestedType("NoiseZoneCompositor",F);
  var evType=asm.GetType("SinmaiAlpha.ChartVisuals.NoiseZoneEvent");var rectType=asm.GetType("SinmaiAlpha.Notes.Libs.PresentationRect");
  var events=Array.CreateInstance(evType,12);
  for(int i=0;i<12;i++)events.SetValue(Activator.CreateInstance(evType,new object[]{i<6?i:17+i-6,i<6?0d:1d,3d,1d,null}),i);
  var root=new GameObject("noise");var display=root.AddComponent(displayType);
  Call(display,"Configure",events,(Func<double>)(()=>time),graphics.GetField("Centers",F).GetValue(null),warning,30,0);
  var camera=new GameObject("probe camera").AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;camera.orthographicSize=5.4f;camera.transform.position=new Vector3(0,0,-10);
  var compositor=Activator.CreateInstance(compType,F,null,new object[]{shader,displacement,spark},null);
  var hooks=new HarmonyLib.Harmony("noise.perf.measure");
  foreach(var method in new[]{displayType.GetMethod("FillWarningMask",F),displayType.GetMethod("FillActiveMask",F),compType.GetMethod("ProtectWarningBorder",F),compType.GetMethod("SpreadStyles",F)})
   hooks.Patch(method,prefix:new HarmonyMethod(typeof(NoisePerfProbe),nameof(Prefix)),postfix:new HarmonyMethod(typeof(NoisePerfProbe),nameof(Postfix)));
  var rect=Activator.CreateInstance(rectType,new object[]{0f,0f,1f,1f});
  foreach(var wh in new[]{new[]{1080,1080},new[]{1080,1920}}){
   int w=wh[0],h=wh[1];camera.aspect=(float)w/h;
   var field=(Matrix4x4)displayType.GetProperty("FieldMatrix",F).GetValue(display,null);
   Call(compositor,"Prepare",display,camera,w,h,field,rect);
   foreach(bool rebuild in new[]{false,true}){
    totals.Clear();var clock=new Stopwatch();double total=0;
    for(int i=0;i<20;i++){
     if(rebuild)Set(compositor,"warningValid",false);
     clock.Restart();Call(compositor,"Prepare",display,camera,w,h,field,rect);clock.Stop();total+=clock.Elapsed.TotalMilliseconds;
    }
    report.AppendLine("BENCH "+w+"x"+h+" warningRebuild="+rebuild+" PrepareMeanMs="+(total/20).ToString("F3")+" "+string.Join(" ",totals.Select(p=>p.Key+"="+(p.Value/20).ToString("F3"))));
   }
  }
  hooks.UnpatchSelf();
  var random=new System.Random(8123);
  for(int c=0;c<360;c++){
   int w=c<6?1080:257,h=c<6?1080:193;camera.aspect=(float)w/h;
   time=new[]{.1,.5,.95,1.05,2.9,3.5}[c%6];Call(display,"RenderAt",time);
   root.transform.localScale=c<6?Vector3.one:new Vector3((c%7==0?-1:1)*(.5f+(float)random.NextDouble()),.5f+(float)random.NextDouble(),1);
   root.transform.rotation=Quaternion.Euler(0,0,c<6?0:(float)random.NextDouble()*360);
   root.transform.position=c<6?Vector3.zero:new Vector3((float)random.NextDouble()-.5f,(float)random.NextDouble()-.5f,0);
   if(c>=180){
    root.transform.rotation=Quaternion.Euler(0,0,new[]{0f,45f,90f,135f,180f,270f,.000001f,89.99999f,90.00001f}[c%9]);
    rect=Activator.CreateInstance(rectType,new object[]{.1f,.2f,.6f,.5f});
   }
   if(c>=36&&c%19==0)root.transform.localScale=new Vector3(0,1,1);
   if(c>=36&&c%23==0)root.transform.localScale=new Vector3(.00001f,.00001f,1);
   var field=(Matrix4x4)displayType.GetProperty("FieldMatrix",F).GetValue(display,null);
   var mat=(Material)Call(compositor,"Prepare",display,camera,w,h,field,rect);
   if(mat==null){report.AppendLine("EMPTY "+c);continue;}
   WriteHash("warning-"+c,(byte[])Get(compositor,"warningBytes"));WriteHash("active-"+c,(byte[])Get(compositor,"maskBytes"));HashColors("styles-"+c,(Color32[])Get(compositor,"stylePixels"));
   if(c<6){
    var src=new Texture2D(w,h,TextureFormat.RGBA32,false,true);var pixels=new Color32[w*h];
    for(int j=0;j<pixels.Length;j++)pixels[j]=new Color32((byte)(j%251),(byte)(j/w%241),(byte)(j%97),255);
    src.SetPixels32(pixels);src.Apply();
    var dest=new RenderTexture(w,h,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);dest.Create();
    mat.SetFloat("_AlphaClipSpace",0);mat.SetVector("_BlitScaleBias",new Vector4(1,1,0,0));
    Graphics.Blit(src,dest,mat,3);RenderTexture.active=dest;
    var read=new Texture2D(w,h,TextureFormat.RGBA32,false,true);read.ReadPixels(new Rect(0,0,w,h),0,0);HashColors("render-"+c,read.GetPixels32());
    RenderTexture.active=null;dest.Release();UnityEngine.Object.Destroy(dest);UnityEngine.Object.Destroy(read);UnityEngine.Object.Destroy(src);
   }
  }

  // Compare multicolor nearest-style propagation, including alpha differences.
  for(int c=0;c<100;c++){
   int w=31,h=23;var masks=new byte[w*h];var styles=new Color32[w*h];
   for(int i=0;i<masks.Length;i++)if(random.Next(12)==0){masks[i]=255;styles[i]=c%3==0?new Color32(5,9,70,80):new Color32((byte)random.Next(256),(byte)random.Next(256),(byte)random.Next(256),(byte)random.Next(256));}
   if(c==0)Array.Clear(masks,0,masks.Length);
   Set(compositor,"width",w);Set(compositor,"height",h);Set(compositor,"maskBytes",masks);Set(compositor,"stylePixels",styles);Set(compositor,"styleQueue",new int[w*h]);Set(compositor,"styleDistance",new int[w*h]);
   Call(compositor,"SpreadStyles");HashColors("multicolor-"+c,styles);
  }
  // Exact radius-two expansion against a simple independent 5x5 reference.
  for(int c=0;c<200;c++){
   int w=1+random.Next(23),h=1+random.Next(19);var src=new byte[w*h];
   for(int i=0;i<src.Length;i++)if(random.Next(7)==0)src[i]=(byte)(1+random.Next(255));
   var expected=new byte[src.Length];
   for(int y=0;y<h;y++)for(int x=0;x<w;x++)if(src[y*w+x]!=0)
    for(int yy=Math.Max(0,y-2);yy<=Math.Min(h-1,y+2);yy++)for(int xx=Math.Max(0,x-2);xx<=Math.Min(w-1,x+2);xx++)expected[yy*w+xx]=255;
   Set(compositor,"warningBytes",src);Set(compositor,"warningScratch",new byte[src.Length]);Call(compositor,"ProtectWarningBorder",w,h);
   if(!src.SequenceEqual(expected))throw new Exception("Border expansion mismatch "+c);
  }
  report.AppendLine("PASS 200 border expansion cases");
  var phase=asm.GetType("SinmaiAlpha.Hosting.GameplayDiagnostics+PhaseTimingPatch");
  var phaseMethods=((IEnumerable)phase.GetMethod("TargetMethods",F).Invoke(null,null)).Cast<MethodBase>().ToArray();
  if(phaseMethods.Length>32)throw new Exception("Phase capacity exceeded");
  var diagnosticHooks=new HarmonyLib.Harmony("noise.perf.diagnostic-check");diagnosticHooks.PatchAll(phase);
  Set(compositor,"warningBytes",new byte[9]);Set(compositor,"warningScratch",new byte[9]);Call(compositor,"ProtectWarningBorder",3,3);
  diagnosticHooks.UnpatchSelf();report.AppendLine("PASS diagnostic targets patched: "+phaseMethods.Length);
  ((IDisposable)compositor).Dispose();Call(display,"Clear");bundle.Unload(true);
 }
}
