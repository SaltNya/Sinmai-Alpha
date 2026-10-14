using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Manager;
using Monitor;

namespace SinmaiAlpha.Hosting;

public static partial class GameplayDiagnostics
{
    private sealed class FanTrace
    {
        internal readonly float[] Completed = { float.NaN, float.NaN, float.NaN };
        internal bool Written;
    }
    private static readonly Dictionary<SlideFan, FanTrace> FanTraces = new();
    private static readonly FieldInfo FanIndexes = AccessTools.DeclaredField(typeof(SlideFan), "_hitIndex");
    private static readonly FieldInfo FanAreas = AccessTools.DeclaredField(typeof(SlideFan), "_hitAreaList");
    private static readonly FieldInfo SlideTail = AccessTools.Field(typeof(SlideRoot), "TailMsec");
    private static readonly FieldInfo SlideWait = AccessTools.Field(typeof(SlideRoot), "lastWaitTimeForJudge");
    private static readonly FieldInfo SlideDiff = AccessTools.Field(typeof(SlideRoot), "JudgeTimingDiffMsec");
    private static readonly FieldInfo SlideIndex = AccessTools.Field(typeof(SlideRoot), "NoteIndex");
    private static readonly FieldInfo SlideEnded = AccessTools.Field(typeof(SlideRoot), "EndFlag");

    [HarmonyPatch(typeof(SlideFan), "Initialize")]
    public static class FanTraceInitializePatch
    {
        [HarmonyPostfix]
        public static void Postfix(SlideFan __instance)
        {
            if (writer != null) FanTraces[__instance] = new FanTrace();
        }
    }

    [HarmonyPatch(typeof(SlideFan), "NoteCheck")]
    public static class FanTraceResultPatch
    {
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(SlideFan __instance)
        {
            if (!inGame || writer == null || !FanTraces.TryGetValue(__instance, out var trace) || trace.Written) return;
            try
            {
                var indexes = (int[])FanIndexes.GetValue(__instance);
                var areas = (List<SlideManager.HitArea>[])FanAreas.GetValue(__instance);
                if (indexes == null || areas == null || indexes.Length < 3 || areas.Length < 3) return;
                var now = NotesManager.GetCurrentMsec();
                for (var lane = 0; lane < 3; lane++)
                    if (areas[lane] != null && indexes[lane] >= areas[lane].Count && float.IsNaN(trace.Completed[lane]))
                        trace.Completed[lane] = now;
                if (!(bool)SlideEnded.GetValue(__instance)) return;
                trace.Written = true;
                var player = __instance.MonitorId;
                Write("WIFI player=" + player + " music=" + GameManager.SelectMusicID[player] +
                    " difficulty=" + GameManager.SelectDifficultyID[player] + " chartMs=" + F(now) +
                    " note=" + SlideIndex.GetValue(__instance) + " type=" + __instance.GetType().Name +
                    " grade=" + __instance.GetJudgeResult() + " autoPlay=" + GameManager.IsAutoPlay() +
                    " judgeDiffMs=" + F((float)SlideDiff.GetValue(__instance)) +
                    " tailMs=" + F((float)SlideTail.GetValue(__instance)) +
                    " lastWaitMs=" + F((float)SlideWait.GetValue(__instance)) +
                    " lanes=" + indexes[0] + "/" + areas[0]?.Count + "," + indexes[1] + "/" + areas[1]?.Count + "," + indexes[2] + "/" + areas[2]?.Count +
                    " completedMs=" + F(trace.Completed[0]) + "," + F(trace.Completed[1]) + "," + F(trace.Completed[2]));
            }
            catch (Exception error) { Error("WiFi trace", error.ToString()); }
        }
    }

    internal static void TintBake(string sprite, int rgb, int width, int height, long started)
    {
        if (!Enabled || writer == null) return;
        try { Write("TINT BAKE sprite=" + sprite + " color=#" + rgb.ToString("X6") +
            " size=" + width + "x" + height + " ms=" + F(Milliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - started)) + Context()); }
        catch { /* Diagnostic context is unavailable during some loading paths. */ }
    }
}
