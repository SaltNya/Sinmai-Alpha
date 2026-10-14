using System;using System.IO;using System.Reflection;using MelonLoader;using UnityEngine;
[assembly:MelonInfo(typeof(SpawnProbe),"Outside ring probe","1.0","Codex")][assembly:MelonGame("sega-interactive","Sinmai")][assembly:HarmonyDontPatchAll]
public class SpawnProbe:MelonMod {
 int frame;bool done;public override void OnUpdate(){if(done||++frame<30)return;done=true;try{
 var a=Assembly.LoadFrom(@"C:\项目\Alpha060-GitPublish-20261014\Sinmai-Alpha\bin\Release\net472\Sinmai-Alpha.dll");var flags=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 var t=a.GetType("SinmaiAlpha.ChartVisuals.RingState");object[] args={"spawn","s44=5.2",0d,null,true};if(!(bool)t.GetMethod("TryParse").Invoke(null,args))throw new Exception("stream spawn rejected");
 var note=Activator.CreateInstance(a.GetType("SinmaiAlpha.ChartVisuals.VisualNote"));note.GetType().GetField("Family").SetValue(note,"hold");var value=t.GetMethod("Resolve").Invoke(null,new[]{args[3],(object)1d,note,"s44"});if(Math.Abs((float)value.GetType().GetField("Spawn").GetValue(value)-5.2f)>.00001)throw new Exception("spawn changed");
 var timing=a.GetType("SinmaiAlpha.Notes.ScrollVisualTiming");var v=timing.GetMethod("Ring",flags).Invoke(null,new object[]{1000d,600d,.001f,5.2f,4.8f,false});if(Math.Abs((float)v.GetType().GetField("Radius",flags).GetValue(v)-5.2f)>.00001)throw new Exception("radius clamped");
 var end=timing.GetMethod("Ring",flags).Invoke(null,new object[]{1000d,1000d,.001f,5.2f,4.8f,false});if(Math.Abs((float)end.GetType().GetField("Radius",flags).GetValue(end)-4.8f)>.00001)throw new Exception("judge radius changed");
 File.WriteAllText(@"C:\项目\Alpha060-SpawnCompat-20261014\runtime-result.txt","PASS stream s44 keeps 5.2; display starts at 5.2 and reaches unchanged 4.8 ring at target time.\n");
 }catch(Exception e){File.WriteAllText(@"C:\项目\Alpha060-SpawnCompat-20261014\runtime-result.txt","FAIL "+e);}Application.Quit();}}
