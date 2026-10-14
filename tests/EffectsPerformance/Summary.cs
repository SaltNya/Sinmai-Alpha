using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Collections.Generic;
using HarmonyLib;
using Manager;
using Monitor;
using UnityEngine;
using Object=UnityEngine.Object;
public partial class Alpha060GpuProbe
{
 void SummaryCases()
 {
  var extra=candidate.GetType("SinmaiAlpha.Difficulty.ExtraDifficulty");
  var patch=extra.GetNestedType("SummaryDataPatch");
  var prefix=patch.GetMethod("Prefix");
  var harmony=new HarmonyLib.Harmony("local.alpha060.summary.regression");
  harmony.PatchAll(patch);
  Check(HarmonyLib.Harmony.GetPatchInfo(AccessTools.Method(typeof(Process.PhotoEditProcess),"SetData")).Prefixes.Any(p=>p.owner==harmony.Id),"Summary prefix binds to native PhotoEditProcess.SetData");
  var roots=new[]{new GameObject("Summary P1"),new GameObject("Summary P2")};
  foreach(var root in roots)root.SetActive(false);
  var monitors=roots.Select(r=>r.AddComponent<PhotoEditMonitor>()).ToArray();
  var cards=roots.Select(r=>{var child=new GameObject("SingleResultCard");child.transform.SetParent(r.transform);return child.AddComponent<SingleResultCardController>();}).ToArray();
  var scores=new GameScoreList[2,3];
  int[,] ids={{16045,16021,16099},{16099,16045,16021}};
  for(int player=0;player<2;player++)for(int track=0;track<3;track++) {
   scores[player,track]=(GameScoreList)FormatterServices.GetUninitializedObject(typeof(GameScoreList));
   scores[player,track].SessionInfo=new Game.SessionInfo{musicId=ids[player,track],difficulty=track==1?3:4};
  }
  GameManager.SelectMusicID=new[]{16099,16021};GameManager.SelectDifficultyID=new[]{4,3};
  object Identity(Component c)=>extra.GetMethod("Identity",Any).Invoke(null,new object[]{c});
  int Read(object identity,string field)=>(int)identity.GetType().GetField(field,Any).GetValue(identity);
  try {
   Check(Read(Identity(cards[0]),"Id")==-1,"Unbound summary cannot borrow last song");
   foreach(int track in new[]{0,1,2,1,0,2,0})for(int player=0;player<2;player++) {
    prefix.Invoke(null,new object[]{player,player,track,true,monitors,scores});
    var identity=Identity(cards[player]);
    Check(Read(identity,"Id")==ids[player,track]&&Read(identity,"Difficulty")==scores[player,track].SessionInfo.difficulty,"Own song and difficulty on reused summary P"+(player+1)+" track "+(track+1));
    prefix.Invoke(null,new object[]{player,1-player,track,false,monitors,scores});
    Check(Read(Identity(cards[player]),"Id")==ids[player,track],"Rival data cannot overwrite own summary");
    Check((string)extra.GetMethod("ThemeName",Any).Invoke(null,new[]{(object)"UI_RSL_MBase_MST",MakeTheme(track==1?"STR":"INS")})=="UI_RSL_MBase_"+(track==1?"STR":"INS"),"Result plate resolves theme artwork");
   }
   prefix.Invoke(null,new object[]{0,0,-1,true,monitors,scores});
   Check(Read(Identity(cards[0]),"Id")==-1,"Invalid track clears pooled summary ownership");
   scores[0,0]=null;prefix.Invoke(null,new object[]{0,0,0,true,monitors,scores});
   Check(Read(Identity(cards[0]),"Id")==-1,"Missing score cannot retain previous theme or level identity");
   scores[0,0]=scores[1,0];scores[0,0].SessionInfo=new Game.SessionInfo{musicId=100001,difficulty=3};
   prefix.Invoke(null,new object[]{0,0,0,true,monitors,scores});
   Check((bool)Identity(cards[0]).GetType().GetField("Utage").GetValue(Identity(cards[0])),"Utage exclusion follows historical chart");
  } finally {harmony.UnpatchSelf();foreach(var root in roots)Object.DestroyImmediate(root);}
 }
 object MakeTheme(string code) {
  var type=candidate.GetType("SinmaiAlpha.Assets.DifficultyTheme");var theme=Activator.CreateInstance(type);
  type.GetField("Id").SetValue(theme,code);type.GetField("textureCode").SetValue(theme,code);return theme;
 }
}
