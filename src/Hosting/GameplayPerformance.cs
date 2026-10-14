using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Manager;
using Monitor.Game;

namespace SinmaiAlpha.Hosting;

public static partial class GameplayDiagnostics
{
    private static readonly FrameStatistics Intervals = new(), Updates = new();
    private static readonly Dictionary<MethodBase, int> PhaseIds = new();
    private static readonly List<string> PhaseNames = new();
    private static readonly double[] PhaseMs = new double[32], PhaseMax = new double[32];
    private static readonly long[] PhaseCalls = new long[32];
    private static long lastFrame, nextReport, nextSlow;
    private static int gc0, gc1, gc2;
    private static double IntervalMs;
    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private static string F(double value) => value.ToString("F2", CultureInfo.InvariantCulture);

    internal static void Stop()
    {
        Write("SESSION END");
        DiagnosticLog log;
        lock (Sync) { log = writer; writer = null; }
        log?.Dispose();
    }
    private static void ResetPerformance()
    {
        Intervals.Clear(); Updates.Clear(); lastFrame = nextSlow = 0;
        nextReport = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
        Array.Clear(PhaseMs, 0, PhaseMs.Length); Array.Clear(PhaseMax, 0, PhaseMax.Length); Array.Clear(PhaseCalls, 0, PhaseCalls.Length);
        gc0 = GC.CollectionCount(0); gc1 = GC.CollectionCount(1); gc2 = GC.CollectionCount(2);
        Write("PERFORMANCE: intervals include frame pacing/render/input/wait; phase timings are inclusive CPU wall time, not GPU time. p95/p99 are ceil-ms histogram estimates (1001 means >1000ms). Slow records limited to one/second; counters include all frames. Loading excluded before first OnUpdate.");
    }
    private static string Context()
    {
        return " chartMs=" + F(NotesManager.GetCurrentMsec()) + " autoPlay=" + GameManager.IsAutoPlay() +
            " music=" + string.Join(",", GameManager.SelectMusicID) + " difficulty=" + string.Join(",", GameManager.SelectDifficultyID);
    }
    private static void Effects()
    {
        for (int player = 0; player < GameManager.SelectMusicID.Length; player++)
        {
            var reader = NotesManager.Instance(player)?.getReader();
            if (reader != null) Write("EFFECTS player=" + player + " chartMs=" + F(NotesManager.GetCurrentMsec()) + " " + Notes.CustomNoteTypes.DiagnosticEffects(reader, NotesManager.GetCurrentMsec()));
        }
    }
    private static void ReportPerformance(string reason)
    {
        try
        {
            Write(reason + Context() + " interval{" + Intervals.Describe() + "} update{" + Updates.Describe() + "}" +
                " gcDelta=" + (GC.CollectionCount(0) - gc0) + "," + (GC.CollectionCount(1) - gc1) + "," + (GC.CollectionCount(2) - gc2));
            Effects();
            for (int i = 0; i < PhaseNames.Count; i++)
                if (PhaseCalls[i] > 0) Write("PHASE " + PhaseNames[i] + " calls=" + PhaseCalls[i] + " totalMs=" + F(PhaseMs[i]) + " maxMs=" + F(PhaseMax[i]));
        }
        catch (Exception e) { Error("performance report", e.ToString()); }
    }
    [HarmonyPatch(typeof(Process.GameProcess), "OnUpdate")]
    public static class FrameTimingPatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(out long __state)
        {
            __state = 0;
            if (!inGame || writer == null) return;
            __state = Stopwatch.GetTimestamp();
            IntervalMs = lastFrame == 0 ? 0 : Milliseconds(__state - lastFrame);
            if (lastFrame != 0) Intervals.Add(IntervalMs);
            lastFrame = __state;
        }
        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        public static void Finalizer(long __state, Exception __exception)
        {
            if (__state == 0) return;
            try
            {
                long now = Stopwatch.GetTimestamp();
                double elapsed = Milliseconds(now - __state); Updates.Add(elapsed);
                if (__exception != null) Error("GameProcess.OnUpdate", __exception.ToString());
                if ((IntervalMs > 25 || elapsed > 25) && now >= nextSlow)
                {
                    nextSlow = now + Stopwatch.Frequency;
                    Write("SLOW" + Context() + " intervalMs=" + F(IntervalMs) + " updateMs=" + F(elapsed) +
                        " gcCounts=" + GC.CollectionCount(0) + "," + GC.CollectionCount(1) + "," + GC.CollectionCount(2) +
                        " managedMB=" + GC.GetTotalMemory(false) / 1048576);
                    Effects();
                }
                if (now >= nextReport) { nextReport = now + 5 * Stopwatch.Frequency; ReportPerformance("SUMMARY cumulative"); }
            }
            catch { /* Diagnostics must never mask the original game exception. */ }
        }
    }
    [HarmonyPatch]
    public static class PhaseTimingPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            var targets = new[] {
                new KeyValuePair<System.Type,string>(typeof(GameCtrl), "UpdateCtrl"),
                new KeyValuePair<System.Type,string>(typeof(GameCtrl), "UpdateNotes"),
                new KeyValuePair<System.Type,string>(typeof(GameCtrl), "RegistNote"),
                new KeyValuePair<System.Type,string>(typeof(Notes.ChartScreenEffects), "TryDraw"),
                new KeyValuePair<System.Type,string>(typeof(Notes.CustomNoteTypes.NoiseZoneDisplay), "LateUpdate"),
                new KeyValuePair<System.Type,string>(typeof(Notes.CustomNoteTypes.NoiseZoneCompositor), "Prepare") };
            foreach (var target in targets)
                foreach (var method in target.Key.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    if (method.Name == target.Value)
                    {
                        if (!PhaseIds.ContainsKey(method)) { PhaseIds.Add(method, PhaseNames.Count); PhaseNames.Add(target.Key.Name + "." + method.Name + "/" + method.GetParameters().Length); }
                        yield return method;
                    }
        }
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix(out long __state) => __state = inGame && writer != null ? Stopwatch.GetTimestamp() : 0;
        [HarmonyFinalizer, HarmonyPriority(Priority.Last)]
        public static void Finalizer(MethodBase __originalMethod, long __state, Exception __exception)
        {
            if (__state == 0) return;
            try
            {
                double ms = Milliseconds(Stopwatch.GetTimestamp() - __state);
                int i = PhaseIds[__originalMethod]; PhaseMs[i] += ms; PhaseMax[i] = Math.Max(PhaseMax[i], ms); PhaseCalls[i]++;
                if (__exception != null) Error(PhaseNames[i], __exception.ToString());
                // Record expensive phases even when they run after GameProcess.OnUpdate.
                if (ms > 25 && Stopwatch.GetTimestamp() >= phaseSlowAfter)
                { phaseSlowAfter = Stopwatch.GetTimestamp() + Stopwatch.Frequency; Write("SLOW PHASE " + PhaseNames[i] + " ms=" + F(ms) + Context()); Effects(); }
            }
            catch { }
        }
        private static long phaseSlowAfter;
    }
}
