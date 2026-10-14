using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEngine;
public partial class Alpha060GpuProbe {
 object BenchTrack(string kind,string body){var t=candidate.GetType("SinmaiAlpha.ChartVisuals.PresentationCommands");object[] a={kind,body,120f,null};if(!(bool)t.GetMethod("TryParse").Invoke(null,a))throw new Exception("Parse "+kind);var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(a[3].GetType()));list.Add(a[3]);return Activator.CreateInstance(t.GetNestedType("Track"),new object[]{list,kind});}
 void ScreenBench(){
 report.AppendLine("DEVICE "+SystemInfo.graphicsDeviceName+"; "+SystemInfo.processorType+"; 720x720 per player; readback synchronized");
 var st=candidate.GetType("SinmaiAlpha.Notes.ChartScreenEffects");var tt=candidate.GetType("SinmaiAlpha.ChartVisuals.PresentationCommands+Track");var rt=candidate.GetType("SinmaiAlpha.Notes.Libs.PresentationRect");var rect=Activator.CreateInstance(rt,new object[]{0f,0f,1f,1f});
 var src=new RenderTexture(720,720,0);src.Create();var dst=new RenderTexture(720,720,0);dst.Create();Graphics.Blit(Texture2D.whiteTexture,src);var read=new Texture2D(1,1,TextureFormat.RGBA32,false);var sw=new Stopwatch();
 foreach(var kind in new[]{"gaussian","neon","trail","glitch","tvnoise","rainbow","vignette","kaleidoscope","bloom","shatter","ripple","pixelate","split","invert","hue","flash","brightness","contrast","saturation","shake","mixed"}){
 var tracks=(IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(typeof(string),tt));if(kind=="mixed"){foreach(var k in new[]{"gaussian","neon","trail","bloom","shatter"})tracks.Add(k,BenchTrack(k,"(True,0.65,0)"));}else tracks.Add(kind,BenchTrack(kind,kind=="kaleidoscope"?"(True,45,0)":kind=="shake"?"(True,0.5,6)":"(True,0.65,0)"));
 sw.Restart();var screen=Activator.CreateInstance(st,Any,null,new object[]{tracks},null);sw.Stop();double load=sw.Elapsed.TotalMilliseconds,total=0,max=0,first=0;
 Graphics.Blit(src,dst);RenderTexture.active=dst;read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();
 for(int i=0;i<21;i++){sw.Restart();bool drawn=(bool)st.GetMethod("TryDraw",Any).Invoke(screen,new[]{src,dst,rect,rect,rect,(object)(300d+i*16.666)});RenderTexture.active=dst;read.ReadPixels(new Rect(0,0,1,1),0,0);read.Apply();sw.Stop();if(!drawn)throw new Exception("Not drawn "+kind);if(i==0)first=sw.Elapsed.TotalMilliseconds;else{total+=sw.Elapsed.TotalMilliseconds;max=Math.Max(max,sw.Elapsed.TotalMilliseconds);}}
 report.AppendLine("BENCH screen "+kind+" load="+load.ToString("F3")+" first="+first.ToString("F3")+" synchronized mean="+(total/20).ToString("F3")+" max="+max.ToString("F3"));((IDisposable)screen).Dispose();
 }
 RenderTexture.active=null;src.Release();dst.Release();UnityEngine.Object.DestroyImmediate(src);UnityEngine.Object.DestroyImmediate(dst);UnityEngine.Object.DestroyImmediate(read);
 }
}
