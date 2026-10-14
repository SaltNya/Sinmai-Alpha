using System;
using System.Linq;
using UnityEngine;
using Object=UnityEngine.Object;
public partial class Alpha060GpuProbe {
 void StyleCases(){
  var custom=candidate.GetType("SinmaiAlpha.Notes.CustomNoteTypes");var graphics=custom.GetNestedType("NoiseGraphics",Any);
  graphics.GetMethod("Acquire",Any).Invoke(null,null);
  object Resource(string n)=>graphics.GetField(n,Any).GetValue(null);
  var go=new GameObject("style display");var display=go.AddComponent(custom.GetNestedType("NoiseZoneDisplay",Any));
  var cg=new GameObject("style camera");var camera=cg.AddComponent<Camera>();camera.enabled=false;camera.orthographic=true;camera.orthographicSize=5.4f;camera.aspect=1;camera.transform.position=new Vector3(0,0,-10);
  var compositor=Activator.CreateInstance(custom.GetNestedType("NoiseZoneCompositor",Any),Any,null,new[]{Resource("Composite"),Resource("Displacement"),Resource("Spark")},null);
  var controls=candidate.GetType("SinmaiAlpha.ChartVisuals.NoiseControls");var ev=candidate.GetType("SinmaiAlpha.ChartVisuals.NoiseZoneEvent");
  var src=new RenderTexture(128,128,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);src.Create();var dst=new RenderTexture(128,128,0,RenderTextureFormat.ARGB32,RenderTextureReadWrite.Linear);dst.Create();RenderTexture.active=src;GL.Clear(false,true,new Color(.12f,.2f,.3f,1));var baseline=Save(src,"style-source");
  var outputs=new System.Collections.Generic.List<Color32[]>();int count=0;
  foreach(var text in new[]{"0|color|blockarea=FF0000","0|color|blockarea=00FF00","0|color|blockarea=00FF00\n0|alpha|blockarea=0"}) {
   object[] args={Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text)),null};Check((bool)controls.GetMethod("Decode").Invoke(null,args),"Decode NZ2 color/alpha control");
   var events=Array.CreateInstance(ev,1);events.SetValue(Activator.CreateInstance(ev,new object[]{16,0d,3d,1d,args[1]}),0);
   Call(display,"Configure",events,(Func<double>)(()=>.5d),Resource("Centers"),Resource("Warning"),0,0);
   var material=(Material)Call(compositor,"Prepare",display,camera,128,128);Check(material!=null&&material.shader.isSupported,"NZ2 uses supported fine-noise shader");
   Graphics.Blit(src,dst,material,3);outputs.Add(Save(dst,"style-"+count++));
  }
  Check(!outputs[0].SequenceEqual(outputs[1]),"NZ2 color still changes noise pixels");
  Check(outputs[2].SequenceEqual(baseline),"NZ2 alpha zero restores original scene");
  Call(compositor,"Prepare",display,camera,129,130);var mask=(Texture2D)Field(compositor,"warningMask");Check(mask.width==129&&mask.height==130,"Warning mask reallocates even within same coarse mask size");
  ((IDisposable)compositor).Dispose();Check(Field(compositor,"warningMask")==null&&Field(compositor,"warningScratch")==null,"Dispose releases warning isolation resources");
  Call(display,"Clear");Object.DestroyImmediate(go);Object.DestroyImmediate(cg);graphics.GetMethod("Release",Any).Invoke(null,null);RenderTexture.active=null;src.Release();dst.Release();Object.DestroyImmediate(src);Object.DestroyImmediate(dst);
 }
}
