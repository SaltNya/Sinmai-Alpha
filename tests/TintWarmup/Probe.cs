using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Monitor;
using Manager;
[assembly:MelonInfo(typeof(TintProbe),"Tint load-time regression","1.0","Codex")]
[assembly:MelonGame("sega-interactive","Sinmai")]
[assembly:HarmonyDontPatchAll]
public class TintProbe:MelonMod {
 const string Root=@"C:\项目\Alpha016-IssueFix-20261015/Tint";
 const BindingFlags Flags=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 System.Type custom;Assembly asm;StringBuilder report=new StringBuilder();int frames;bool done;
 object Call(string n,params object[] a)=>AccessTools.Method(custom,n).Invoke(null,a);
 void Set(object o,string n,object v)=>AccessTools.Field(o.GetType(),n).SetValue(o,v);
 void Check(bool v,string m){if(!v)throw new Exception(m);report.AppendLine("PASS "+m);}
 public override void OnUpdate(){if(done||++frames<30)return;done=true;try{Run();File.WriteAllText(Root+"/result.txt","PASS\n"+report);}catch(Exception e){File.WriteAllText(Root+"/result.txt","FAIL\n"+e+"\n"+report);}Application.Quit();}
 void Run(){
 Assembly.LoadFrom(@"C:\项目\Alpha060-GitPublish-20261014\Sinmai-Alpha\RuntimeDependencies\System.Numerics.dll");asm=Assembly.LoadFrom(@"C:\项目\Alpha060-GitPublish-20261014\Sinmai-Alpha\bin\Release\net472\Sinmai-Alpha.dll");custom=asm.GetType("SinmaiAlpha.Notes.CustomNoteTypes");
 var tex=new Texture2D(1024,1024,TextureFormat.RGBA32,false);var pixels=Enumerable.Repeat(new Color32(230,190,30,255),1024*1024).ToArray();tex.SetPixels32(pixels);tex.Apply(false,true);
 var sprite=Sprite.Create(tex,new Rect(0,0,1024,1024),new Vector2(.5f,.5f),100,0,SpriteMeshType.FullRect);sprite.name="representative each-guide fixture";
 var line=new GameObject("each line").AddComponent<SpriteRenderer>();line.sprite=sprite;line.color=new Color(1,1,1,.8f);line.flipX=true;
 var go=new GameObject("inactive controller");go.SetActive(false);var ctrl=go.AddComponent<Monitor.Game.GameCtrl>();
 var gg=new GameObject("inactive guide");gg.SetActive(false);var guide=gg.AddComponent<NoteGuide>();Set(guide,"_spriteEachRender",line);Set(ctrl,"_guideObjectList",new List<NoteGuide>{guide,guide});
 var reader=new NotesReader();var note=new NoteData();reader.GetNoteList().Add(note);
 var payload=Activator.CreateInstance(custom.GetNestedType("VisualPayload",Flags),true);var vn=Activator.CreateInstance(asm.GetType("SinmaiAlpha.ChartVisuals.VisualNote"));Set(vn,"Family","hold");Set(payload,"Note",vn);Set(payload,"Stream","");
 var notes=custom.GetField("VisualNotes",Flags).GetValue(null);notes.GetType().GetMethod("Add").Invoke(notes,new[]{(object)note,payload});
 var timeline=Activator.CreateInstance(custom.GetNestedType("VisualTimeline",Flags),true);var streams=(IDictionary)timeline.GetType().GetField("Streams",Flags).GetValue(timeline);var ct=asm.GetType("SinmaiAlpha.ChartVisuals.VisualChange");var vt=asm.GetType("SinmaiAlpha.ChartVisuals.VisualValue");var list=(IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(ct));
 foreach(var rgb in new[]{0xE07026,0xE07026,0x33AA66}){var c=Activator.CreateInstance(ct);Set(c,"Kind","color");Set(c,"Type","");Set(c,"Time",21650.94);var val=Activator.CreateInstance(vt);Set(val,"Color",(int?)rgb);Set(c,"Value",val);list.Add(c);}streams[""]=list;
 var timelines=custom.GetField("VisualTimelines",Flags).GetValue(null);timelines.GetType().GetMethod("Add").Invoke(timelines,new[]{(object)reader,timeline});
 var sw=Stopwatch.StartNew();var cold=(Sprite)Call("GetVisualTintSprite",sprite,0xE07026,line.color);sw.Stop();report.AppendLine("cold first-color ms="+sw.Elapsed.TotalMilliseconds.ToString("F3"));var expected=cold;
 Call("ClearVisualTintCache");sw.Restart();Call("WarmHintTints",ctrl,reader);sw.Stop();report.AppendLine("two colors warmup during loading ms="+sw.Elapsed.TotalMilliseconds.ToString("F3"));
 var cache=(IDictionary)custom.GetField("VisualTintSprites",Flags).GetValue(null);Check(cache.Count==2,"deduplicate duplicate guides and chart colors");
 sw.Restart();var cached=(Sprite)Call("GetVisualTintSprite",sprite,0xE07026,line.color);sw.Stop();report.AppendLine("first gameplay color after warmup ms="+sw.Elapsed.TotalMilliseconds.ToString("F3"));Check(cache.Count==2,"COLORV reuses prebuilt sprite without extra texture");
 var part=Activator.CreateInstance(custom.GetNestedType("VisualPart",Flags),true);Set(part,"Native",line);Set(part,"Hint",true);var value=Activator.CreateInstance(vt);Set(value,"Color",(int?)0xE07026);Set(value,"Alpha",(float?).5f);Set(value,"X",(float?)3);Set(value,"Y",(float?)3);var material=line.sharedMaterial;
 Call("ApplyVisualPart",part,value,Activator.CreateInstance(vt));var output=line.GetComponentsInChildren<SpriteRenderer>().Single(x=>x!=line);Check(output.sprite==cached,"each overlay uses warmed sprite");Check(output.sharedMaterial==material,"native arc material preserved");Check(output.flipX,"arc direction preserved");Check(output.transform.localScale==Vector3.one,"connecting geometry not resized");Check(Math.Abs(output.color.a-.4f)<.001f,"native and authored opacity preserved");
 Call("WarmHintTints",ctrl,reader);Check(cache.Count==2,"repeated warmup keeps cache bounded");Call("ClearVisualTintCache");Check(cache.Count==0,"chart cleanup clears tint cache");
 Call("WarmHintTints",ctrl,new NotesReader());Check(cache.Count==0,"ordinary chart without tint creates no textures");
 Set(ctrl,"_guideObjectList",new List<NoteGuide>());Check(!(bool)Call("WarmHintTints",ctrl,reader),"missing pool requests warmup retry");Set(ctrl,"_guideObjectList",new List<NoteGuide>{guide});Check((bool)Call("WarmHintTints",ctrl,reader),"ready pool completes retry");

 }
}
