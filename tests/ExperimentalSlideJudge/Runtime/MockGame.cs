// Integration fixture only. Executes the real extension/config/cache/Harmony
// patches on a minimal game API. Numerical compiler parity has its own suite.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Manager;

namespace SinmaiAlpha.Hosting { public static class AssetFiles { public static string Root; } }
namespace MelonLoader
{
    public static class MelonLogger
    {
        public static readonly List<string> Messages = new();
        public static void Msg(string s) { Messages.Add(s); Console.WriteLine(s); }
        public static void Warning(string s) { Messages.Add(s); }
        public static void Error(string s) { Messages.Add(s); }
    }
}
namespace DB { public enum OptionMirrorID { Normal, LR, UD, UDLR } }
namespace UnityEngine
{
    public struct Vector4 { public float x,y,z,w; public Vector4(float z){x=y=w=0;this.z=z;} }
}
namespace Manager
{
    public enum SlideType { Straight=1, Fan=13 }
    public class SlideData { public SlideType type=SlideType.Straight; public int targetNote=4; }
    public sealed class NotesTypeID
    {
        public enum Def { Slide, ConnectSlide }
        public Def Value;
        public Def getEnum() => Value;
    }
    public class NoteData { public List<NoteData> child; public NotesTypeID type = new(); public int startButtonPos; public SlideData slideData=new(); }
    public class SlideManager
    {
        public static readonly SlideManager Instance=new();
        public class HitArea { public double PushDistance,ReleaseDistance; public List<InputManager.TouchPanelArea> HitPoints=new(); }
        public static List<HitArea> Legacy() => new() {
            new HitArea{PushDistance=1,ReleaseDistance=1,HitPoints=new(){InputManager.TouchPanelArea.A1}},
            new HitArea{PushDistance=1,ReleaseDistance=0,HitPoints=new(){InputManager.TouchPanelArea.A5}}
        };
        [MethodImpl(MethodImplOptions.NoInlining)]
        public List<HitArea> GetSlideHitArea(SlideType type,int start,int end,int star)=>Legacy();
        [MethodImpl(MethodImplOptions.NoInlining)]
        public List<UnityEngine.Vector4> GetSlidePath(SlideType type,int start,int end,int star)=>new(){new(0),new(30),new(60),new(100)};
        [MethodImpl(MethodImplOptions.NoInlining)]
        public float GetSlideLength(SlideType type,int start,int end)=>100;
    }
    public static class InputManager
    {
        public enum TouchPanelArea { A1,A2,A3,A4,A5,A6,A7,A8,B1,B2,B3,B4,B5,B6,B7,B8,C1,C2,D1,D2,D3,D4,D5,D6,D7,D8,E1,E2,E3,E4,E5,E6,E7,E8 }
        public static readonly ulong[] Pressed = new ulong[2];
        public static int Reads;
        public static bool ConvertTouchPanelRotatePush(int monitor,TouchPanelArea area,int star)
        {
            Reads++;
            int panel=(int)area;
            if(panel==16||panel==17)panel=16;
            else {int ring=panel<8?0:panel<16?8:panel<26?18:26;panel=ring+((panel-ring+star)&7);}
            return (Pressed[monitor] & (1UL<<panel))!=0;
        }
    }
}
namespace SinmaiAlpha.Notes.Libs
{
    public class CustomSlideNoteData : NoteData
    {
        public bool IsFan;
        public string SlideCode;
        public DB.OptionMirrorID TouchMirror;
        public float SlidePathLength;
        public List<List<UnityEngine.Vector4>> SlidePathList=new();
        public List<List<SlideManager.HitArea>> SlideHitAreasList=new();
        public List<List<List<UnityEngine.Vector4>>> FanPathLists=new();
        public List<List<List<SlideManager.HitArea>>> FanHitAreaLists=new();
        public int FanLane(int end)=>1;
        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool ParseSlideCode(string slideCode,DB.OptionMirrorID mirrorMode)
        {
            SlideCode=slideCode;TouchMirror=mirrorMode;SlidePathList.Clear();SlideHitAreasList.Clear();
            if(slideCode=="invalid")return false;
            IsFan=slideCode=="fan";SlidePathLength=240;
            for(int star=0;star<8;star++){
                SlidePathList.Add(new(){new(0),new(60),new(120),new(180),new(240)});
                SlideHitAreasList.Add(SlideManager.Legacy());
                FanPathLists.Add(new(){SlidePathList[star],SlidePathList[star],SlidePathList[star]});
                FanHitAreaLists.Add(new(){SlideHitAreasList[star],SlideHitAreasList[star],SlideHitAreasList[star]});
            }
            return true;
        }
    }
}
namespace Monitor
{
    public class SlideRoot
    {
        private List<SlideManager.HitArea> _hitAreaList;
        private int _hitIndex, _hitSubIndex;
        private bool _hitIn;
        public int ButtonId {get;set;}
        public int MonitorId {get;set;}
        public bool AutoPlay;
        public int NativeTouchCalls,NativeNextCalls;
        public float LastWait;
        public int Index=>_hitIndex;
        public bool Entered=>_hitIn;
        public List<SlideManager.HitArea> Areas=>_hitAreaList;

        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual void Initialize(NoteData note)
        {
            ButtonId=note.startButtonPos;_hitIndex=_hitSubIndex=0;_hitIn=false;
            _hitAreaList=SlideManager.Instance.GetSlideHitArea(note.slideData.type,note.startButtonPos,note.slideData.targetNote,ButtonId).ToList();
            foreach(var child in note.child??new List<NoteData>())
                if(child.type.Value==NotesTypeID.Def.ConnectSlide)
                    _hitAreaList.AddRange(SlideManager.Instance.GetSlideHitArea(child.slideData.type,child.startButtonPos,child.slideData.targetNote,ButtonId).Skip(1));
            LastWait=(float)(1000*_hitAreaList.Last().PushDistance/_hitAreaList.Sum(a=>a.PushDistance+a.ReleaseDistance));
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public virtual int GetSlideArrowNum(NoteData note)
        {
            int count=SlideManager.Instance.GetSlidePath(note.slideData.type,note.startButtonPos,note.slideData.targetNote,ButtonId).Count-2;
            foreach(var child in note.child??new List<NoteData>())
                count+=SlideManager.Instance.GetSlidePath(child.slideData.type,child.startButtonPos,child.slideData.targetNote,ButtonId).Count-2;
            return count;
        }
        [MethodImpl(MethodImplOptions.NoInlining)]
        public void NoteCheck()
        {
            if(AutoPlay){_hitIndex=_hitAreaList.Count;return;}
            if(_hitIndex>=_hitAreaList.Count)return;
            bool progress;
            do{
                progress=CheckSlideTouch(_hitIndex,_hitIn);
                if(!progress&&NextCheck()&&_hitIndex+1<_hitAreaList.Count)progress=CheckSlideTouch(_hitIndex+1,false);
            }while(progress&&_hitIndex<_hitAreaList.Count);
        }
        private bool NextCheck(){NativeNextCalls++;return _hitAreaList.Count>3||_hitAreaList.Count-1!=_hitIndex+1||_hitIn;}
        protected bool CheckSlideTouch(int index,bool In)
        {
            NativeTouchCalls++;
            if(!In){for(int i=0;i<_hitAreaList[index].HitPoints.Count;i++)
                if(InputManager.ConvertTouchPanelRotatePush(MonitorId,_hitAreaList[index].HitPoints[i],ButtonId))
                {_hitIndex=index==_hitAreaList.Count-1?index+1:index;_hitSubIndex=i;_hitIn=true;return true;}}
            else if(!InputManager.ConvertTouchPanelRotatePush(MonitorId,_hitAreaList[index].HitPoints[_hitSubIndex],ButtonId))
            {_hitIndex=index+1;_hitSubIndex=0;_hitIn=false;return true;}
            return false;
        }
    }
    public class SlideFan : SlideRoot
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public override void Initialize(NoteData note)=>base.Initialize(note);
        [MethodImpl(MethodImplOptions.NoInlining)] public override int GetSlideArrowNum(NoteData note)=>base.GetSlideArrowNum(note);
    }
    public class StarNote
    {
        public float Length;
        [MethodImpl(MethodImplOptions.NoInlining)] public virtual void Initialize(NoteData note)=>Length=SlideManager.Instance.GetSlideLength(note.slideData.type,note.startButtonPos,note.slideData.targetNote);
    }
    public class BreakStarNote:StarNote
    {
        [MethodImpl(MethodImplOptions.NoInlining)] public override void Initialize(NoteData note)=>Length=SlideManager.Instance.GetSlideLength(note.slideData.type,note.startButtonPos,note.slideData.targetNote);
    }
}
namespace SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified
{
    public sealed class SlideTable
    {
        public bool UsesNativeVTable;
        public sealed class Lane { public List<SlideManager.HitArea> HitAreas; }
        public sealed class Frame { public Lane[] Lanes; }
        public Frame[] Frames=new Frame[8];
    }
    public static class UnifiedSlideNoteData
    {
        public static int Calls;
        public static bool TryParseInto(SinmaiAlpha.Notes.Libs.CustomSlideNoteData note,string code,DB.OptionMirrorID mirror,out SlideTable table)
        {
            Calls++;table=null;note.SlideCode=code;note.TouchMirror=mirror;note.SlidePathList.Clear();note.SlideHitAreasList.Clear();
            if(code=="invalid")return false;
            table=new SlideTable{UsesNativeVTable=code=="v"};note.IsFan=code=="fan";note.SlidePathLength=240;
            for(int star=0;star<8;star++){
                var hits=SlideManager.Legacy();
                note.SlidePathList.Add(new(){new(0),new(60),new(120),new(180),new(240)});
                note.SlideHitAreasList.Add(hits);
                note.FanPathLists.Add(new(){note.SlidePathList[star],note.SlidePathList[star],note.SlidePathList[star]});
                note.FanHitAreaLists.Add(new(){hits,hits,hits});
                table.Frames[star]=new SlideTable.Frame{Lanes=new[]{new SlideTable.Lane{HitAreas=hits}}};
            }
            return true;
        }
    }
}
namespace Process
{
    public class GameProcess { [MethodImpl(MethodImplOptions.NoInlining)] public void OnRelease() {} }
}
namespace SinmaiAlpha.Notes.ExperimentalSlideJudge
{
    internal sealed class JudgeStep
    {
        internal int[] Panels; internal double Push=1,Release=0;
        internal bool RequireEntry; internal ulong RequireRearmMask=0;
    }
    internal static class NativeTableCompiler
    {
        internal static int Calls;
        internal static CompiledJudgeNote Compile(string code,DB.OptionMirrorID mirror)
        {
            Calls++;
            if(code=="reject")throw new ArgumentException("uncovered endpoint");
            var frames=new CompiledJudgeFrame[8];
            for(int star=0;star<8;star++)frames[star]=new CompiledJudgeFrame(new[]{
                new JudgeStep{Panels=new[]{(8-star)&7}},
                new JudgeStep{Panels=new[]{8+((8-star)&7)},RequireEntry=true},
                new JudgeStep{Panels=new[]{(12-star)&7},Push=2}
            });
            return new CompiledJudgeNote(frames);
        }
    }
}
