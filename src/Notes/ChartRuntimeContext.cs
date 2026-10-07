using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SinmaiAlpha.Notes.Libs;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using Process;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private static readonly RuntimeChartStore<ChartRuntimeState> RuntimeCharts = new();
    private static ChartRuntimeState RuntimeMonitor(int monitor)
        => RuntimeCharts.Reader(NotesManager.Instance(monitor)?.getReader());

    [HarmonyPatch]
    public static class ChartReaderContextPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => typeof(NotesReader)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "loadMa2" || m.Name == "loadDLMusicScore" || m.Name == "loadStr" ||
                m.Name == "loadMa2Main" || m.Name == "loadNote" || m.Name == "calcTotal");
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        public static void Prefix(NotesReader __instance, MethodBase __originalMethod, object[] __args, out object __state)
        {
            var fresh = __originalMethod.Name == "loadMa2" || __originalMethod.Name == "loadStr" || __originalMethod.Name == "loadDLMusicScore";
            var state = RuntimeCharts.Reader(__instance, fresh);
            if (fresh)
            {
                var path = __args.Length > 0 ? __args[0] as string : null;
                var text = __originalMethod.Name == "loadDLMusicScore" && __args.Length > 1 ? __args[1] as string : null;
                state.Enabled = ChartFeatureGate.Detect(path, text);
                state.Classified = true;
            }
            __state = RuntimeCharts.Enter(state);
        }
        [HarmonyPostfix, HarmonyPriority(Priority.Last - 100)]
        public static void Postfix(NotesReader __instance, MethodBase __originalMethod)
        {
            if (__originalMethod.Name != "loadMa2Main" || !RuntimeCharts.Reader(__instance).Enabled) return;
            var notes = __instance.GetNoteList();
            if (notes == null) return;
            var state = RuntimeCharts.Reader(__instance);
            foreach (var note in notes) if (note != null) RuntimeCharts.BindNote(note, state);
        }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)]
        public static void Finalizer(object __state) => RuntimeCharts.Exit(__state);
    }

    [HarmonyPatch]
    public static class ChartControllerContextPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => new[] { "UpdateCtrl", "UpdateNotes", "RegistNote", "SkipRegistNote" }
            .Select(name => AccessTools.DeclaredMethod(typeof(GameCtrl), name));
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        public static void Prefix(GameCtrl __instance, out object __state)
            => __state = RuntimeCharts.Enter(RuntimeMonitor(__instance.MonitorIndex));
        [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)]
        public static void Finalizer(object __state) => RuntimeCharts.Exit(__state);
    }

    [HarmonyPatch]
    public static class ChartOwnerContextPatch
    {
        // Scope only native note entry points. Rendering, hold input and result
        // callbacks may also be invoked outside GameCtrl's main update.
        private static readonly HashSet<string> Names = new()
        {
            "Initialize", "Execute", "GetNoteYPosition", "MoveStarLane", "NoteCheck",
            "Judge", "JudgeToolate", "SetAutoPlayJudge", "EndNote", "SetPlayResult", "SetForcePlayResult"
        };
        public static IEnumerable<MethodBase> TargetMethods() => FakeOwnerMethods().Where(m => Names.Contains(m.Name));
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        public static void Prefix(Component __instance, MethodBase __originalMethod, object[] __args, out object __state)
        {
            ChartRuntimeState state;
            if (__originalMethod.Name == "Initialize" && __args.FirstOrDefault() is NoteData note)
                state = RuntimeCharts.BindOwner(__instance, note);
            else if (!RuntimeCharts.TryOwner(__instance, out state))
                state = RuntimeMonitor(__instance is NoteBase ring ? ring.MonitorId : ((SlideRoot)__instance).MonitorId);
            __state = RuntimeCharts.Enter(state);
        }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)]
        public static void Finalizer(object __state) => RuntimeCharts.Exit(__state);
    }

    [HarmonyPatch(typeof(GameScoreList), "SetResult")]
    public static class ChartScoreContextPatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        public static void Prefix(int ____monitorIndex, out object __state)
            => __state = RuntimeCharts.Enter(RuntimeMonitor(____monitorIndex));
        [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)]
        public static void Finalizer(object __state) => RuntimeCharts.Exit(__state);
    }

    [HarmonyPatch(typeof(GameProcess), "OnStart")]
    public static class ChartStartContextPatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First + 100)]
        public static void Prefix() { RuntimeCharts.Reset(); ChartFeatureGate.BeginGame(); }
    }
    [HarmonyPatch(typeof(GameProcess), "OnRelease")]
    public static class ChartReleaseContextPatch
    {
        [HarmonyFinalizer, HarmonyPriority(Priority.Last - 100)]
        public static void Finalizer() { RuntimeCharts.Reset(); ChartFeatureGate.EndGame(); MineAudio.ReleasePlayers(); }
    }
}
