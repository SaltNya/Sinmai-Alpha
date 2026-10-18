using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.Remoting.Messaging;
using System.Runtime.Remoting.Proxies;
using System.Runtime.Serialization;
using HarmonyLib;
using Manager;
using Process;
using Process.SubSequence;
using SinmaiAlpha.Difficulty;
using static Process.MusicSelectProcess;

internal static partial class Program
{
    [Serializable]
    private sealed class SelectionBoundary : Exception
    {
        public SelectionBoundary(string boundary) : base(boundary) { }
        private SelectionBoundary(SerializationInfo info, StreamingContext context) : base(info, context) { }
    }

    // Run the stock selection logic, stopping before it needs Unity rendering/audio.
    private sealed class SelectionProcess : RealProxy
    {
        public readonly MusicDifficultyID[] Difficulties = new MusicDifficultyID[2];
        public readonly int[] Selected = new int[2];
        public readonly MusicSelectData Row;
        public bool LevelTab;
        public bool Remaster;
        public bool CompleteWorkflow;
        public Monitor.MusicSelectMonitor[] Monitors;
        public int InputLockPlayer = -1;
        private readonly List<ReadOnlyCollection<CombineMusicSelectData>> catalogue;

        public SelectionProcess(Manager.MaiStudio.MusicData music) : base(typeof(IMusicSelectProcessProcessing))
        {
            Row = (MusicSelectData)FormatterServices.GetUninitializedObject(typeof(MusicSelectData));
            Row.MusicData = music;
            var combine = (CombineMusicSelectData)FormatterServices.GetUninitializedObject(typeof(CombineMusicSelectData));
            combine.musicSelectData = new List<MusicSelectData> { Row, Row };
            catalogue = new List<ReadOnlyCollection<CombineMusicSelectData>> {
                new List<CombineMusicSelectData> { combine }.AsReadOnly()
            };
        }

        public override IMessage Invoke(IMessage message)
        {
            var call = (IMethodCallMessage)message;
            object result;
            switch (call.MethodName)
            {
                case "GetMusic":
                    Check((int)call.Args[0] == 0, "Chart offset must not be replaced by player index");
                    result = Row; break;
                case "IsRemasterEnable": result = Remaster; break;
                case "IsLevelTab": result = LevelTab; break;
                case "get_CurrentDifficulty": result = Difficulties; break;
                case "get_DifficultySelectIndex": result = Selected; break;
                case "get_CombineMusicDataList": result = catalogue; break;
                case "get_CurrentCategorySelect": case "get_CurrentMusicSelect": result = 0; break;
                case "get_ScoreType": result = MAI2System.ConstParameter.ScoreKind.Standard; break;
                case "get_MonitorArray": result = Monitors; break;
                case "get_SortDecidePlayer": result = -1; break;
                case "IsMaiList": result = false; break;
                case "set_CurrentCategorySelect": case "set_CurrentMusicSelect": case "ReCalcGenreSelectData": result = null; break;
                case "GetLevelToListPositoin":
                    if (CompleteWorkflow) { result = new LevelCategoryData(); break; }
                    return new ReturnMessage(new SelectionBoundary("changed"), call);
                case "ReCalcLevelSortData":
                    if (CompleteWorkflow) { result = null; break; }
                    return new ReturnMessage(new SelectionBoundary("changed"), call);
                case "SetInputLockInfo":
                    if (CompleteWorkflow) { InputLockPlayer = (int)call.Args[0]; result = null; break; }
                    return new ReturnMessage(new SelectionBoundary("limit"), call);
                default: return new ReturnMessage(new InvalidOperationException("Unexpected selection call: " + call.MethodName), call);
            }
            return new ReturnMessage(result, null, 0, call.LogicalCallContext, call);
        }
    }

}
