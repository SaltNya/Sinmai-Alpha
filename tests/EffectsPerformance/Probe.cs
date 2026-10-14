using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Collections.Generic;
using System.Text;
using MelonLoader;
using UnityEngine;
using Object=UnityEngine.Object;
[assembly:MelonInfo(typeof(Alpha060GpuProbe),"Alpha060 mixed noise check","2.0.0","Codex")]
[assembly:MelonGame("sega-interactive","Sinmai")]
[assembly:HarmonyDontPatchAll]
public partial class Alpha060GpuProbe:MelonMod {
 const string Stage=@"C:\项目\Alpha060-Update-20261013";
 const string Output=@"C:\项目\Alpha060-EffectsPerf-20261014";
 const BindingFlags Any=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.Static;
 Assembly candidate;int frames;bool done;StringBuilder report=new StringBuilder();
 void Check(bool value,string message){if(!value)throw new Exception(message);report.AppendLine("PASS "+message);}
 public override void OnUpdate(){if(done||++frames<30)return;done=true;try{Run();File.WriteAllText(Output+"/result.txt","PASS\n"+report);}catch(Exception e){File.WriteAllText(Output+"/result.txt","FAIL\n"+e+"\n"+report);}Application.Quit();}
 object Field(object obj,string n)=>obj.GetType().GetField(n,Any).GetValue(obj);
 void Set(object obj,string n,object v)=>obj.GetType().GetField(n,Any).SetValue(obj,v);
 object Call(object obj,string n,params object[] args)=>obj.GetType().GetMethods(Any).Single(m=>m.Name==n&&m.GetParameters().Length==args.Length).Invoke(obj,args);
 Color32[] Save(RenderTexture rt,string name){RenderTexture.active=rt;var t=new Texture2D(rt.width,rt.height,TextureFormat.RGBA32,false,true);t.ReadPixels(new Rect(0,0,rt.width,rt.height),0,0);t.Apply();File.WriteAllBytes(Output+"/"+name+".png",t.EncodeToPNG());var p=t.GetPixels32();Object.DestroyImmediate(t);return p;}
 void Run(){
  foreach(var f in Directory.GetFiles(Stage+"/Sinmai-Alpha/RuntimeDependencies","*.dll"))Assembly.LoadFrom(f);
  candidate=Assembly.LoadFrom(@"C:\项目\Alpha060-GitPublish-20261014\Sinmai-Alpha\bin\Release\net472\Sinmai-Alpha.dll");
  var oldBundle=AssetBundle.LoadFromFile(Stage+"/baseline/Sinmai-Alpha/assets/Alpha/AlphaNoise.ab");var oldShader=oldBundle.LoadAsset<Shader>("assets/aquamai/alphanoisecomposite.shader");oldBundle.Unload(false);
  var custom=candidate.GetType("SinmaiAlpha.Notes.CustomNoteTypes");var stateType=custom.GetNestedType("NoiseRenderState",Any);var compositorType=custom.GetNestedType("NoiseZoneCompositor",Any);var graphics=custom.GetNestedType("NoiseGraphics",Any);
  var eventType=candidate.GetType("SinmaiAlpha.ChartVisuals.NoiseZoneEvent");var rectType=candidate.GetType("SinmaiAlpha.Notes.Libs.PresentationRect");
  object Rect(float x,float y,float w,float h)=>Activator.CreateInstance(rectType,new object[]{x,y,w,h});
  var indices=Enumerable.Range(0,8).Concat(Enumerable.Range(17,16)).Concat(new[]{8}).ToArray();var events=Array.CreateInstance(eventType,indices.Length);
  for(int i=0;i<indices.Length;i++)events.SetValue(Activator.CreateInstance(eventType,new object[]{indices[i],indices[i]==8?1.3d:0d,3d,1d,null}),i);
  var cg=new GameObject("mixed camera");var camera=cg.AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;camera.orthographicSize=5.4f;camera.aspect=2;camera.transform.position=new Vector3(0,0,-10);camera.nearClipPlane=.1f;camera.farClipPlane=20;camera.cullingMask=1<<30;camera.clearFlags=CameraClearFlags.SolidColor;camera.backgroundColor=new Color(.04f,.04f,.04f,1);
  var anchor=new GameObject("native anchor");anchor.layer=30;anchor.transform.position=new Vector3(-5.4f,0,0);anchor.transform.localScale=Vector3.one*.01f;
  var ownerGo=new GameObject("native owner");var owner=ownerGo.AddComponent<Monitor.GameMonitor>();typeof(MonitorBase).GetField("monitorIndex",Any).SetValue(owner,0);
  var reader=FormatterServices.GetUninitializedObject(typeof(Manager.NotesReader));typeof(Manager.NotesManager).GetField("_curMSec",Any).SetValue(null,500f);
  var state=Activator.CreateInstance(stateType,Any,null,new object[]{owner,reader,anchor.transform,events},null);
  var source=new RenderTexture(1440,720,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);source.Create();var dest=new RenderTexture(1440,720,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);dest.Create();
  camera.targetTexture=source;Call(state,"CaptureField",camera);camera.Render();var sourcePixels=Save(source,"warning-source");
  var timing=new System.Diagnostics.Stopwatch();
  for(int pass=0;pass<2;pass++) {
   double total=0,max=0;for(int i=0;i<60;i++) {
    typeof(Manager.NotesManager).GetField("_curMSec",Any).SetValue(null,pass==0?500f+i:1500f+i);
    Call(state,"CaptureField",camera);timing.Restart();
    Call(state,"TryDraw",source,dest,camera,Rect(0,0,.5f,1),Rect(0,0,.5f,1),Rect(0,0,.5f,1));timing.Stop();total+=timing.Elapsed.TotalMilliseconds;max=Math.Max(max,timing.Elapsed.TotalMilliseconds);
   }report.AppendLine("BENCH noise "+(pass==0?"mixed":"active-only")+" CPU submit mean="+(total/60).ToString("F3")+" max="+max.ToString("F3"));
  }
  typeof(Manager.NotesManager).GetField("_curMSec",Any).SetValue(null,500f);Call(state,"CaptureField",camera);
  var newShader=(Shader)graphics.GetField("Composite",Any).GetValue(null);var colors=new List<Color32[]>();
  foreach(var test in new[]{("fix2",newShader),("legacy",oldShader)}) {
   ((IDisposable)Field(state,"compositor")).Dispose();var compositor=Activator.CreateInstance(compositorType,Any,null,new object[]{test.Item2,graphics.GetField("Displacement",Any).GetValue(null),graphics.GetField("Spark",Any).GetValue(null)},null);Set(state,"compositor",compositor);
   Object.DestroyImmediate((Material)Field(state,"copy"));var copy=new Material(test.Item2);copy.SetTexture("_Masks",(Texture)graphics.GetField("EmptyMask",Any).GetValue(null));copy.SetFloat("_AlphaClipSpace",0);Set(state,"copy",copy);
   for(int frame=0;frame<2;frame++) {Graphics.Blit(source,dest);Call(state,"TryDraw",source,dest,camera,Rect(0,0,.5f,1),Rect(0,0,.5f,1),Rect(0,0,.5f,1));colors.Add(Save(dest,test.Item1+"-"+frame));Save((RenderTexture)Field(state,"input"),test.Item1+"-input-"+frame);}
   if(test.Item1=="fix2") {
    var mask=(byte[])Field(compositor,"warningBytes");var w=(Texture2D)Field(compositor,"sourceMask");
    Check(w.width==180&&w.height==180,"Active mask resolution doubled per axis at 720p");
    Check(mask.Length==720*720&&mask.Any(v=>v>0),"Full resolution warning isolation mask populated");
    int covered=0,mismatch=0,outside=0;
    for(int y=0;y<720;y++)for(int x=0;x<1440;x++) {
     int index=y*1440+x;
     if(x<720&&mask[y*720+x]>0){covered++;if(!colors[0][index].Equals(sourcePixels[index]))mismatch++;}
     if(x>=720&&!colors[0][index].Equals(sourcePixels[index]))outside++;
    }
    report.AppendLine("protected warning pixels="+covered+" changed="+mismatch);
    Check(mismatch==0,"Warning destination retains exact original pixels");Check(outside==0,"Other player's pixels unchanged");
    camera.cullingMask=0;camera.Render();Graphics.Blit(source,dest);Call(state,"TryDraw",source,dest,camera,Rect(0,0,.5f,1),Rect(0,0,.5f,1),Rect(0,0,.5f,1));var clean=Save(dest,"fix2-clean-source");
    int leaked=0;
    for(int y=3;y<717;y++)for(int x=3;x<717;x++) {
     bool near=false;for(int dy=-3;dy<=3;dy++)for(int dx=-3;dx<=3;dx++)near|=mask[(y+dy)*720+x+dx]>0;
     if(!near&&!clean[y*1440+x].Equals(colors[0][y*1440+x]))leaked++;
    }
    report.AppendLine("warning source leaked pixels outside 3px boundary="+leaked);
    Check(leaked==0,"Displaced active scene does not drag warning material out of its boundary");
    camera.cullingMask=1<<30;camera.Render();
    foreach(float time in new[]{1500f,500f,4500f,500f}) {
     typeof(Manager.NotesManager).GetField("_curMSec",Any).SetValue(null,time);Call(state,"CaptureField",camera);camera.Render();Graphics.Blit(source,dest);
     bool drawn=(bool)Call(state,"TryDraw",source,dest,camera,Rect(0,0,.5f,1),Rect(0,0,.5f,1),Rect(0,0,.5f,1));
     Check(drawn==(time<4500),"Noise lifecycle renders correctly at "+time+"ms");
     if(drawn)Check(((byte[])Field(compositor,"warningBytes")).Any(v=>v>0)==(time==500),"Warning mask clears or returns on time seek "+time+"ms");
    }
    for(int change=0;change<3;change++) {
     if(change==0)camera.transform.position+=new Vector3(.2f,0,0);
     if(change==1)camera.orthographicSize=5f;
     if(change==2)anchor.transform.localScale=Vector3.one*.012f;
     Call(state,"CaptureField",camera);camera.Render();Graphics.Blit(source,dest);
     Call(state,"TryDraw",source,dest,camera,Rect(0,0,.5f,1),Rect(0,0,.5f,1),Rect(0,0,.5f,1));
     var updated=Save(dest,"cache-movement-"+change);var maskUpdated=((byte[])Field(compositor,"warningBytes")).ToArray();
     Set(compositor,"warningValid",false);Graphics.Blit(source,dest);
     Call(state,"TryDraw",source,dest,camera,Rect(0,0,.5f,1),Rect(0,0,.5f,1),Rect(0,0,.5f,1));
     Check(maskUpdated.SequenceEqual((byte[])Field(compositor,"warningBytes")),"Warning cache invalidates geometry change "+change);
     Check(updated.SequenceEqual(Save(dest,"forced-movement-"+change)),"Cached and forced render identical for geometry change "+change);
    }
    camera.transform.position=new Vector3(0,0,-10);camera.orthographicSize=5.4f;anchor.transform.localScale=Vector3.one*.01f;
    Call(state,"CaptureField",camera);camera.Render();
   }
  }
  Check(!colors[0].SequenceEqual(colors[2]),"Fine noise visibly differs from coarse legacy noise");
  Check(colors[0].SequenceEqual(colors[1]),"Same-time repeated render has no stale material state");
  ((IDisposable)state).Dispose();camera.targetTexture=null;RenderTexture.active=null;source.Release();dest.Release();Object.DestroyImmediate(source);Object.DestroyImmediate(dest);Object.DestroyImmediate(ownerGo);Object.DestroyImmediate(anchor);Object.DestroyImmediate(cg);
  StyleCases();SummaryCases();ScreenBench();
 }
}
