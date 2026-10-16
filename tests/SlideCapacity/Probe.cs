using System;using System.IO;using System.Linq;using System.Reflection;using System.Collections.Generic;using System.Text;using HarmonyLib;using Manager;using Monitor;using MelonLoader;using UnityEngine;
[assembly:MelonInfo(typeof(AyoFollowup),"AYO manual shortage regression","1.0","Codex")]
[assembly:MelonGame("sega-interactive","Sinmai")][assembly:HarmonyDontPatchAll]
public class AyoFollowup:MelonMod {
 int frames;bool done;static bool auto;static int required;static float now=1000;static int scores;static GameScoreList score;readonly StringBuilder report=new StringBuilder();
 public override void OnUpdate(){if(done||++frames<30)return;done=true;try{Run();report.AppendLine("PASS ALL");}catch(Exception e){report.AppendLine("FAIL "+e);}File.WriteAllText(Environment.GetEnvironmentVariable("SINMAI_PROBE_OUTPUT"),report.ToString());Application.Quit();}
 static bool Enabled(ref bool __result){__result=true;return false;}
 static bool Auto(ref bool __result){__result=auto;return false;}
 static bool Clock(ref float __result){__result=now;return false;}
 static bool Skip()=>false;
 static bool Result(){scores++;return false;}
 static bool Score(ref GameScoreList __result){__result=score;return false;}
 static bool NoInput(ref bool __result){__result=false;return false;}
 static readonly BindingFlags F=BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance;
 static object Get(object o,string n)=>AccessTools.Field(o.GetType(),n).GetValue(o);
 static void Set(object o,string n,object v)=>AccessTools.Field(o.GetType(),n).SetValue(o,v);
 static int countQueries;
 static bool Required(ref int __result){countQueries++;__result=required;return false;}
 static bool RequiredNote(NoteData note,ref int __result){__result=note.indexSlide;return false;}
 static bool Distance(ref double __result){__result=1;return false;}
 static bool NoJudge(ref bool __result){__result=false;return false;}
 static bool Touch(SlideRoot __instance,ref bool __result){AccessTools.Field(typeof(SlideRoot),"_hitIndex").SetValue(__instance,1);__result=true;return false;}
 void Run(){
  Assembly.LoadFrom(Path.Combine(Environment.GetEnvironmentVariable("SINMAI_PROBE_ASSETS"),"..","RuntimeDependencies","System.Numerics.dll"));
  var asm=Assembly.LoadFrom(Environment.GetEnvironmentVariable("SINMAI_PROBE_TARGET"));var custom=asm.GetType("SinmaiAlpha.Notes.CustomNoteTypes");report.AppendLine("MVID "+asm.ManifestModule.ModuleVersionId);
  var stub=new HarmonyLib.Harmony("ayo.fixture");
  void Patch(MethodInfo method,string prefix)=>stub.Patch(method,prefix:new HarmonyMethod(typeof(AyoFollowup),prefix));
  Patch(AccessTools.Method(custom,"FeaturesEnabled"),nameof(Enabled));Patch(AccessTools.Method(typeof(GameManager),"IsAutoPlay"),nameof(Auto));Patch(AccessTools.Method(typeof(NotesManager),"GetCurrentMsec"),nameof(Clock));
  Patch(AccessTools.Method(typeof(SlideRoot),"IsNoteCheckTimeStartIgnoreJudgeWait"),nameof(Enabled));Patch(AccessTools.Method(typeof(SlideRoot),"CheckSlideTouch"),nameof(Touch));
  Patch(AccessTools.Method(typeof(SlideRoot),"GetSlideArrowNum",System.Type.EmptyTypes),nameof(Required));Patch(AccessTools.Method(typeof(SlideRoot),"GetDeleteArrowDistance"),nameof(Distance));
  Patch(AccessTools.Method(typeof(SlideRoot),"Judge"),nameof(NoJudge));Patch(AccessTools.Method(typeof(SlideRoot),"JudgeToolate"),nameof(NoJudge));
  var mod=new HarmonyLib.Harmony("ayo.current-mod");
  foreach(var n in new[]{"MineSlideAutoPlayCompatibility","SlideAutoPlayCompatibility","SlideGraceJudgeTranspiler","SlideEndWaitTranspiler"})mod.PatchAll(custom.GetNestedType(n,BindingFlags.Public|BindingFlags.NonPublic));
  var bounds=custom.GetNestedType("SlideArrowDrawingBounds",F);if(bounds!=null)mod.PatchAll(bounds);
  var postfix=AccessTools.Method(custom,"MineSlideAutoJudgePostfix");mod.Patch(AccessTools.Method(typeof(SlideRoot),"NoteCheck"),postfix:new HarmonyMethod(postfix));
  var arrow=new GameObject("arrow").AddComponent<SpriteRenderer>();
  var breakArrow=new GameObject("break arrow").AddComponent<BreakSlide>();breakArrow.SpriteRender=breakArrow.gameObject.AddComponent<SpriteRenderer>();
  foreach(var broken in new[]{false,true})foreach(var available in new[]{0,1,640,703,800})foreach(var automatic in new[]{false,true}){
   auto=automatic;required=703;var go=new GameObject("mine slide fixture");go.SetActive(false);var slide=(SlideRoot)go.AddComponent(asm.GetType("SinmaiAlpha.Notes.MineSlideRoot"));
   void Set(string n,object v)=>AccessTools.Field(typeof(SlideRoot),n).SetValue(slide,v);
   Set("_spriteRenders",Enumerable.Repeat(arrow,available).ToList());Set("_breakSpriteRenders",Enumerable.Repeat(breakArrow,available).ToList());Set("BreakFlag",broken);Set("_dispLaneNum",Math.Min(703,available));Set("_hitAreaList",new List<SlideManager.HitArea>{null});Set("JudgeResult",NoteJudge.ETiming.End);Set("TailMsec",2000f);Set("PlaySlideSe",true);
   countQueries=0;bool threw=false;try{AccessTools.Method(typeof(SlideRoot),"NoteCheck").Invoke(slide,null);}catch(TargetInvocationException e) when(e.InnerException is ArgumentOutOfRangeException){threw=true;}
   report.AppendLine("CASE break="+broken+" required=703 available="+available+" auto="+auto+" indexException="+threw);
   if(threw!=(bounds==null&&!auto&&available<required))throw new Exception("Unexpected shortage outcome");
   if(!auto&&!threw&&(int)Get(slide,"_hitIndex")!=1)throw new Exception("Input progress changed");
   if(bounds!=null&&countQueries>1)throw new Exception("Repeated geometric count inside arrow loop");
   UnityEngine.Object.DestroyImmediate(go);
  }
  report.AppendLine("PASS 20 drawing-bound cases; shortage="+(bounds==null?"reproduced":"fixed")+"; input progress unchanged");
  stub.Unpatch(AccessTools.Method(typeof(SlideRoot),"Judge"),HarmonyPatchType.Prefix,stub.Id);stub.Unpatch(AccessTools.Method(typeof(SlideRoot),"JudgeToolate"),HarmonyPatchType.Prefix,stub.Id);
  Patch(AccessTools.Method(typeof(SlideRoot),"PlayJudgeSe"),nameof(Skip));Patch(AccessTools.Method(typeof(SlideRoot),"SetPlayResult"),nameof(Result));Patch(AccessTools.Method(typeof(SlideJudge),"Initialize"),nameof(Skip));
  Patch(AccessTools.Method(typeof(SlideRoot),"IsNoteCheckTimeStart"),nameof(Enabled));Patch(AccessTools.Method(typeof(InputManager),"ConvertTouchPanelRotatePush"),nameof(NoInput));
  score=new GameScoreList(0);score.UserOption=new Manager.UserDatas.UserOption();Patch(AccessTools.Method(typeof(GamePlayManager),"GetGameScore"),nameof(Score));
  mod.PatchAll(custom.GetNestedType("MineJudgeInversion",F));
  var fanPostfix=AccessTools.Method(custom,"MineFanAutoJudgePostfix");if(fanPostfix!=null)mod.Patch(AccessTools.DeclaredMethod(typeof(SlideFan),"NoteCheck"),postfix:new HarmonyMethod(fanPostfix));
  foreach(bool automatic in new[]{false,true})foreach(int progress in new[]{0,1,2})Fan(asm,automatic,progress,fanPostfix!=null);
  if(bounds!=null)Pools(asm,custom,arrow,breakArrow);
  mod.UnpatchSelf();mod.PatchAll(custom);
  foreach(var pair in new[]{Tuple.Create(typeof(SlideRoot),"MineSlideAutoJudgePostfix"),Tuple.Create(typeof(SlideFan),"MineFanAutoJudgePostfix")})
   if(AccessTools.Method(custom,pair.Item2)!=null&&!HarmonyLib.Harmony.GetPatchInfo(AccessTools.DeclaredMethod(pair.Item1,"NoteCheck")).Postfixes.Any(p=>p.owner==mod.Id&&p.PatchMethod.Name==pair.Item2))throw new Exception("Missing runtime postfix "+pair.Item2);
  report.AppendLine("PASS runtime attribute registration for tail postfixes");
  mod.UnpatchSelf();stub.UnpatchSelf();
 }
 void Fan(Assembly asm,bool automatic,int progress,bool fixedFan){
  auto=automatic;GameManager.AutoPlay=automatic?GameManager.AutoPlayMode.Critical:GameManager.AutoPlayMode.None;
  var go=new GameObject("fan");go.SetActive(false);var fan=(SlideFan)go.AddComponent(asm.GetType("SinmaiAlpha.Notes.MineSlideFan"));
  var judgeGo=new GameObject("judge");judgeGo.SetActive(false);Set(fan,"JudgeObj",judgeGo.AddComponent<SlideJudge>());Set(fan,"JudgeResult",NoteJudge.ETiming.End);Set(fan,"TailMsec",1000f);Set(fan,"lastWaitTime",100f);Set(fan,"lastWaitTimeForJudge",100f);Set(fan,"StarLaunchMsec",0f);Set(fan,"StarArriveMsec",1000f);Set(fan,"NotesType",new NotesTypeID(NotesTypeID.Def.Slide));Set(fan,"_arrowPrefubs",new GameObject[0]);Set(fan,"PlaySlideSe",true);
  Set(fan,"_hitIndex",new[]{progress,progress,progress});Set(fan,"_hitAreaList",Enumerable.Range(0,3).Select(_=>new List<SlideManager.HitArea>{new SlideManager.HitArea{HitPoints=new List<InputManager.TouchPanelArea>{InputManager.TouchPanelArea.A1}},new SlideManager.HitArea{HitPoints=new List<InputManager.TouchPanelArea>{InputManager.TouchPanelArea.A1}}}).ToArray());
  scores=0;now=900f;AccessTools.DeclaredMethod(typeof(SlideFan),"NoteCheck").Invoke(fan,null);
  bool hit=!auto&&progress==2;
  if(hit&&fan.GetJudgeResult()!=NoteJudge.ETiming.TooLate)throw new Exception("Early mine completion did not remain Miss");
  if(!hit&&fan.GetJudgeResult()!=NoteJudge.ETiming.End)throw new Exception("Mine settled before tail");
  now=1000f;if(!(bool)Get(fan,"EndFlag"))AccessTools.DeclaredMethod(typeof(SlideFan),"NoteCheck").Invoke(fan,null);
  if(fixedFan||auto||hit){if(fan.GetJudgeResult()!=(hit?NoteJudge.ETiming.TooLate:NoteJudge.ETiming.Critical)||scores!=1)throw new Exception("Wrong fan tail score "+fan.GetJudgeResult()+" writes="+scores);}
  report.AppendLine("FAN auto="+auto+" progress="+progress+" atTail="+fan.GetJudgeResult()+" scoreWrites="+scores);
  if(fixedFan){var post=AccessTools.Method(asm.GetType("SinmaiAlpha.Notes.CustomNoteTypes"),"MineFanAutoJudgePostfix");post.Invoke(null,new object[]{fan});if(scores!=1)throw new Exception("Duplicate fan score");}
  UnityEngine.Object.DestroyImmediate(go);UnityEngine.Object.DestroyImmediate(judgeGo);
 }
 void Pools(Assembly asm,System.Type custom,SpriteRenderer arrow,BreakSlide critical){
  AccessTools.Field(typeof(GameNotePrefabContainer),"_arrow").SetValue(null,arrow);AccessTools.Field(typeof(GameNotePrefabContainer),"_breakArrow").SetValue(null,critical);
  var go=new GameObject("pool owner");go.SetActive(false);var ctrl=go.AddComponent<Monitor.Game.GameCtrl>();var parent=new GameObject("pool parent");Set(ctrl,"_slideListParent",parent);Set(ctrl,"_arrowObjectList",Enumerable.Repeat(arrow,640).ToList());Set(ctrl,"_breakArrowObjectList",Enumerable.Repeat(critical,640).ToList());
  var pools=new Dictionary<string,object>();AccessTools.Method(custom,"AddArrowPools").Invoke(null,new object[]{pools,ctrl});
  var normal=(List<SpriteRenderer>)pools["_arrowObjectList"];var breaks=(List<BreakSlide>)pools["_breakArrowObjectList"];
  if(normal.Count!=1024||breaks.Count!=1024||normal.Any(x=>x.gameObject.activeSelf||x.transform.parent!=parent.transform))throw new Exception("Default 1024 prewarm failed");
  var store=custom.GetField("RuntimeCharts",F).GetValue(null);int next=10000;
  NoteData Chain(bool broken,int arrows,bool mine=true,bool connected=true){
   var n=new NoteData{indexNote=next++,type=new NotesTypeID(broken?NotesTypeID.Def.BreakSlide:NotesTypeID.Def.Slide),indexSlide=arrows};
   if(connected)n.child=new List<NoteData>{new NoteData{type=new NotesTypeID(NotesTypeID.Def.ConnectSlide)}};
   var state=AccessTools.Method(store.GetType(),"Note").Invoke(store,new object[]{n});var kinds=(System.Collections.IDictionary)Get(state,"NoteKinds");
   kinds[n.indexNote]=Enum.Parse(kinds.GetType().GetGenericArguments()[1],mine?"Mine":"None");return n;
  }
  var plan=AccessTools.Method(custom,"LongMineChainTargets");int queries=0;
  Func<NoteData,int> count=n=>{queries++;return n.indexSlide;};
  int[] Targets(params NoteData[] notes)=>(int[])plan.Invoke(null,new object[]{notes,count});
  var empty=Targets();if(empty[0]!=0||empty[1]!=0)throw new Exception("Empty chart expanded");
  var small=Targets(Chain(false,1024),Chain(false,703),Chain(true,1200,false),Chain(false,2000,true,false));
  if(small[0]!=0||small[1]!=0||queries!=2)throw new Exception("Unrelated notes expanded/scanned");
  var longest=Targets(Chain(false,1200),Chain(false,1100),Chain(false,1200));
  if(longest[0]!=1200||longest[1]!=0)throw new Exception("Chains must use maximum, not sum, and preserve other type");
  var separate=Targets(Chain(false,1200),Chain(true,1400));if(separate[0]!=1200||separate[1]!=1400)throw new Exception("Wrong type targets");
  ((System.Collections.IDictionary)custom.GetField("MinePools",F).GetValue(null)).Add(ctrl,pools);
  var counterGo=new GameObject("inactive prewarm counter");counterGo.SetActive(false);var counter=counterGo.AddComponent<SlideRoot>();counter.ButtonId=6;counter.MonitorId=1;
  pools["_slideObjectList"]=new List<SlideRoot>{counter};
  var initPatch=new HarmonyLib.Harmony("ayo.prewarm-initialization");
  initPatch.Patch(AccessTools.Method(typeof(SlideRoot),"GetSlideArrowNum",new[]{typeof(NoteData)}),prefix:new HarmonyMethod(typeof(AyoFollowup),nameof(RequiredNote)));
  initPatch.Patch(AccessTools.Method(typeof(Monitor.Game.GameCtrl),"SetNoteManager"),postfix:new HarmonyMethod(AccessTools.Method(custom,"PrewarmLongMineChainsAfterManager")));
  if(Get(ctrl,"NoteMng")!=null)throw new Exception("Fixture already has chart manager");
  AccessTools.Method(custom,"PrewarmLongMineChains").Invoke(null,new object[]{ctrl,pools});
  if(normal.Count!=1024||breaks.Count!=1024)throw new Exception("Expanded before chart was available");
  var manager=new NotesManager();manager.getReader().GetNoteList().Add(Chain(false,1200));
  ctrl.SetNoteManager(manager);
  if(normal.Count!=1200||breaks.Count!=1024)throw new Exception("Selective prewarm failed");
  ctrl.SetNoteManager(manager);
  if(normal.Count!=1200||breaks.Count!=1024||counter.ButtonId!=6||counter.MonitorId!=1)throw new Exception("Repeated attach/state restoration failed");
  report.AppendLine("PASS native initialization order: pool=1024 with null manager; real SetNoteManager triggers prewarm to 1200 before RegistNote; repeat attach unchanged; counter restored");
  initPatch.UnpatchSelf();
  report.AppendLine("PASS selective targets: empty/short/native unchanged; 1200-arrow chain prewarms normal=1200 break=1024; maximum not sum; types separate");
  var routeType=custom.GetNestedType("PoolRoute",F);var scope=custom.GetField("PoolRoutes",F).GetValue(null);
  foreach(bool broken in new[]{false,true}){
   int active=broken?50:100,requiredCount=broken?1400:1200;for(int i=0;i<active;i++)(broken?breaks[i].gameObject:normal[i].gameObject).SetActive(true);
   var note=Chain(broken,requiredCount);
   var route=Activator.CreateInstance(routeType,true);Set(route,"Controller",ctrl);Set(route,"Note",note);Set(route,"Kind",Enum.Parse(routeType.GetField("Kind",F).FieldType,"Mine"));var lease=AccessTools.Method(scope.GetType(),"Enter").Invoke(scope,new object[]{ctrl,route});
   var method=AccessTools.Method(custom,"PrepareMineArrowCapacity");method.Invoke(null,new object[]{new NoteData(),requiredCount});if((bool)Get(route,"ArrowsPrepared"))throw new Exception("Other note used route");
   int before=broken?breaks.Count:normal.Count;var children=note.child;note.child=null;
   method.Invoke(null,new object[]{note,requiredCount});if((broken?breaks.Count:normal.Count)!=before)throw new Exception("Single slide expanded");note.child=children;
   method.Invoke(null,new object[]{note,1024});if((broken?breaks.Count:normal.Count)!=before)throw new Exception("Short chain expanded");
   method.Invoke(null,new object[]{note,requiredCount});int actual=broken?breaks.Count:normal.Count;if(actual!=requiredCount+active)throw new Exception("Free arrow expansion failed");
   method.Invoke(null,new object[]{note,requiredCount});if((broken?breaks.Count:normal.Count)!=actual)throw new Exception("Repeated expansion");
   AccessTools.Method(scope.GetType(),"Exit").Invoke(scope,new[]{lease});report.AppendLine("POOL break="+broken+" before="+before+" active="+active+" requested="+requiredCount+" expanded="+actual);
  }
 }
}
