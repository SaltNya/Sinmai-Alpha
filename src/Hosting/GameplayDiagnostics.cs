using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;

namespace SinmaiAlpha.Hosting;

// Opt-in, bounded diagnostics. Never swallow the gameplay exception and never
// read Unity objects from the threaded log callback.
public static partial class GameplayDiagnostics
{
    internal static bool Enabled
    {
        get
        {
#if ALPHA_GAMEPLAY_DIAGNOSTICS
            return true;
#else
            return Settings.Current.GameplayDiagnostics;
#endif
        }
    }
    private static readonly object Sync = new();
    private static readonly HashSet<string> Seen = new();
    private static readonly Dictionary<GameCtrl, double> NextSample = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static DiagnosticLog writer;
    private static int errorCount;
    private static bool inGame;

    internal static void Start()
    {
        try
        {
            var directory = Path.Combine(AssetFiles.Root, "Diagnostics");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "gameplay-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".log");
            writer = new DiagnosticLog(path);
            Application.logMessageReceivedThreaded += UnityLog;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Write("UNHANDLED terminating=" + e.IsTerminating + " " + e.ExceptionObject);
            AppDomain.CurrentDomain.ProcessExit += (_, __) => Stop();
            Write("START build=" + typeof(GameplayDiagnostics).Assembly.ManifestModule.ModuleVersionId +
                " unity=" + Application.unityVersion + " gpu=" + SystemInfo.graphicsDeviceName + " api=" + SystemInfo.graphicsDeviceType +
                " cpu=" + SystemInfo.processorType + " cores=" + SystemInfo.processorCount + " ramMB=" + SystemInfo.systemMemorySize +
                " gpuMB=" + SystemInfo.graphicsMemorySize + " resolution=" + Screen.width + "x" + Screen.height +
                " targetFps=" + Application.targetFrameRate + " vsync=" + QualitySettings.vSyncCount +
                " version=" + typeof(GameplayDiagnostics).Assembly.GetName().Version +
                " chartFeatures=" + Settings.Current.ChartFeatures + " extraPool=" + Settings.Current.ExtraPoolCount +
                " singlePlayer=" + Settings.Current.SinglePlayer + " hideSubMonitor=" + Settings.Current.HideSubMonitor);
            MelonLoader.MelonLogger.Msg("[Gameplay Diagnostics] " + path);
        }
        catch (Exception e) { MelonLoader.MelonLogger.Warning("[Gameplay Diagnostics] Cannot open log: " + e.Message); }
    }
    private static void Write(string text)
    {
        try { lock (Sync) writer?.Enqueue(DateTime.Now.ToString("O") + " " + text); }
        catch { /* A disk/logging failure must not stop gameplay. */ }
    }
    private static void Error(string origin, string detail)
    {
        lock (Sync)
        {
            if (errorCount >= 64 || !Seen.Add(origin + detail)) return;
            errorCount++;
            Write("ERROR " + origin + "\n" + detail);
            if (errorCount == 64) Write("Error limit reached; duplicate/additional errors suppressed.");
        }
    }
    private static void UnityLog(string message, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            Error("Unity/" + type, message + "\n" + stack);
    }
    private static int Count(GameCtrl owner, string field)
        => AccessTools.Field(typeof(GameCtrl), field)?.GetValue(owner) is ICollection list ? list.Count : -1;
    private static void Sample(GameCtrl owner)
    {
        if (!inGame || writer == null || owner == null) return;
        var now = Clock.Elapsed.TotalSeconds;
        if (NextSample.TryGetValue(owner, out var next) && now < next) return;
        NextSample[owner] = now + 5;
        try
        {
            var monitor = owner.MonitorIndex;
            var reader = NotesManager.Instance(monitor)?.getReader();
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            Write("SAMPLE player=" + monitor + " chartMsec=" + NotesManager.GetCurrentMsec().ToString("F0") +
                " music=" + GameManager.SelectMusicID[monitor] + " difficulty=" + GameManager.SelectDifficultyID[monitor] +
                " autoPlay=" + GameManager.IsAutoPlay() + " alpha=" + Notes.CustomNoteTypes.FeaturesForMonitor(monitor) + " notes=" + reader?.GetNoteList()?.Count +
                " activeNotes=" + Count(owner, "_activeNoteList") + " activeSlides=" + Count(owner, "_activeSlideList") +
                " managedMB=" + (GC.GetTotalMemory(false) / 1048576) + " privateMB=" + (process.PrivateMemorySize64 / 1048576) +
                " workingMB=" + (process.WorkingSet64 / 1048576) +
                " unityMB=" + (UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576));
        }
        catch (Exception e) { Error("sampling", e.ToString()); }
    }
    [HarmonyPatch(typeof(Process.GameProcess), "OnStart")]
    public static class StartPatch
    {
        [HarmonyPrefix, HarmonyPriority(Priority.First + 300)]
        public static void Prefix()
        {
            inGame = true; NextSample.Clear(); ResetPerformance();
            lock (Sync) { Seen.Clear(); errorCount = 0; }
            Write("GAME START");
            try
            {
                foreach (var name in new[] { "UpdateCtrl", "UpdateNotes", "RegistNote", "CreateNotePool" })
                {
                    var method = AccessTools.Method(typeof(GameCtrl), name);
                    var info = HarmonyLib.Harmony.GetPatchInfo(method);
                    Write("PATCH " + name + " owners=" + (info == null ? "" : string.Join(",", info.Owners)));
                }
            }
            catch (Exception e) { Error("patch inventory", e.ToString()); }
        }
    }
    [HarmonyPatch(typeof(Process.GameProcess), "OnRelease")]
    public static class EndPatch
    {
        [HarmonyPrefix] public static void Prefix() => Write("GAME RELEASE begin");
        [HarmonyFinalizer] public static void Finalizer(Exception __exception)
        {
            if (__exception != null) Error("GameProcess.OnRelease", __exception.ToString());
            ReportPerformance("GAME END"); Write("GAME RELEASE end"); inGame = false; NextSample.Clear();
        }
    }
    [HarmonyPatch(typeof(GameCtrl), "UpdateCtrl")]
    public static class SamplePatch
    {
        [HarmonyPostfix] public static void Postfix(GameCtrl __instance) => Sample(__instance);
    }
    [HarmonyPatch]
    public static class ExceptionPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var name in new[] { "Initialize", "CreateNotePool", "UpdateCtrl", "UpdateNotes", "RegistNote", "ForceNoteCollect" })
                foreach (var method in typeof(GameCtrl).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Where(m => m.Name == name))
                    yield return method;
            yield return AccessTools.Method(typeof(GameMonitor), "ViewUpdate");
            yield return AccessTools.Method(typeof(GameScoreList), "SetResult");
        }
        [HarmonyFinalizer]
        public static void Finalizer(object __instance, MethodBase __originalMethod, Exception __exception)
        {
            if (__exception == null) return;
            var player = __instance is GameCtrl ctrl ? ctrl.MonitorIndex : __instance is GameMonitor monitor ? monitor.MonitorIndex : -1;
            Error(__originalMethod.DeclaringType.FullName + "." + __originalMethod.Name + " player=" + player, __exception.ToString());
            // void finalizer preserves Harmony's existing exception.
        }
    }
}

