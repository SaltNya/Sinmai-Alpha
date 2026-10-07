using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Notes.Libs;
using DB;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class NoiseChart
    {
        public readonly List<NoiseZoneEvent> Events = new();
        public readonly NoiseZoneTimeline Timeline = new();
    }
    private static readonly ConditionalWeakTable<NotesReader, NoiseChart> NoiseCharts = new();
    private static List<(int Bar, int Grid, string Text)> PendingNoiseZones => RuntimeCharts.Current.PendingNoiseZones;
    [HarmonyPatch(typeof(NotesRecord), "addRecord", new[] { typeof(string) })]
    public static class NoiseRecordPatch
    {
        [HarmonyPrefix]
        public static void Prefix(string str)
        {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }

            if (str == null) return;
            var p = str.Split('\t');
            if (p.Length == 4 && p[0] == "NZONE" &&
                int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var bar) &&
                int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var grid))
                PendingNoiseZones.Add((bar, grid, p[3]));
        }
    }
    [HarmonyPatch]
    public static class NoiseReaderResetPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => typeof(NotesReader).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.Name == "loadMa2" || m.Name == "loadStr" || m.Name == "loadDLMusicScore");
        [HarmonyPrefix]
        public static void Prefix(NotesReader __instance)
        { NoisePlaybackStopped = false; ResetNoisePresentation(__instance); PendingNoiseZones.Clear(); NoiseCharts.Remove(__instance); }
    }
    [HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static class NoiseReaderBuildPatch
    {
        [HarmonyPostfix]
        public static void Postfix(NotesReader __instance, int ____playerID)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            var chart = new NoiseChart();
            var option = Singleton<GamePlayManager>.Instance.GetGameScore(____playerID).UserOption;
            var speed = GetNoisePlayerTapSpeed(option.GetNoteSpeed);
            foreach (var record in PendingNoiseZones)
            {
                if (!NoiseZoneSpec.TryDecode(record.Text, out var spec))
                { MelonLogger.Warning("[Noise Zone] Invalid composition record: " + record.Text); continue; }
                var start = new NotesTime(); start.init(record.Bar, record.Grid, __instance);
                chart.Events.Add(new NoiseZoneEvent(spec.Sensor, start.msec / 1000d, spec.Duration,
                    NoiseWarningLead(speed, spec.Hs)));
            }
            chart.Timeline.Configure(chart.Events);
            NoiseCharts.Remove(__instance); NoiseCharts.Add(__instance, chart);
            if (chart.Events.Count > 0) MelonLogger.Msg("[Noise Zone] Loaded " + chart.Events.Count + " independent region events; no score notes added");
        }
    }
    internal static bool IsNoiseAreaBlocked(int monitor, int nativeArea)
    {
        var sensor = NoiseZoneSpec.NativeSensor(nativeArea);
        if (sensor < 0 || monitor < 0 || monitor > 1) return false;
        var manager = NotesManager.Instance(monitor);
        return manager != null && manager.IsPlaying() && manager.getReader() != null && NoiseInputDisplayReady(monitor, manager.getReader()) && NoiseCharts.TryGetValue(manager.getReader(), out var chart) &&
            chart.Timeline.IsBlocked(sensor, NotesManager.GetCurrentMsec() / 1000d);
    }
    [HarmonyPatch]
    public static class NoiseInputPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => typeof(InputManager).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.ReturnType == typeof(bool) &&
                (m.Name.StartsWith("InGameTouchPanelArea", StringComparison.Ordinal) || m.Name.StartsWith("GetTouchPanelArea", StringComparison.Ordinal) ||
                 m.Name == "InGameButtonDown" || m.Name == "InGameButtonPush" || m.Name == "GetButtonDown" || m.Name == "GetButtonPush" || m.Name == "GetButtonLongPush") &&
                m.GetParameters().Length >= 1 && m.GetParameters()[0].ParameterType == typeof(int) &&
                (m.GetParameters().Length >= 2 && (m.GetParameters()[1].ParameterType == typeof(InputManager.TouchPanelArea) || m.GetParameters()[1].ParameterType == typeof(InputManager.ButtonSetting) || m.GetParameters()[1].ParameterType == typeof(InputManager.TouchPanelArea[])) ||
                 m.GetParameters().Length == 1 && m.Name.Contains("_C_")));
        [HarmonyPostfix]
        public static void Postfix(MethodBase __originalMethod, object[] __args, ref bool __result)
        {
        if (!(CustomNoteTypes.FeaturesForMonitor((int)__args[0]))) {  return; }

            if (!__result) return;
            if (__args.Length == 2 && __args[1] is InputManager.TouchPanelArea[] areas)
            {
                var monitor = (int)__args[0];
                if (!areas.Any(a => IsNoiseAreaBlocked(monitor, (int)a))) return;
                // A union can still contain an unblocked hit. Each single-area
                // getter retains native history and its own noise mask.
                __result = areas.Any(a => !IsNoiseAreaBlocked(monitor, (int)a) && InputManager.GetTouchPanelAreaDown(monitor, a));
                return;
            }
            int area;
            if (__args.Length == 1) area = (int)InputManager.TouchPanelArea.C1;
            else if (__args[1] is InputManager.TouchPanelArea touch) area = (int)touch;
            else
            {
                var key = (int)(InputManager.ButtonSetting)__args[1];
                if (key < 0 || key > 7) return; // Select/system buttons remain native.
                var name = __originalMethod.Name;
                area = key + (name.Contains("_B_") ? (int)InputManager.TouchPanelArea.B1 :
                    name.Contains("_D_") ? (int)InputManager.TouchPanelArea.D1 :
                    name.Contains("_E_") ? (int)InputManager.TouchPanelArea.E1 : (int)InputManager.TouchPanelArea.A1);
            }
            if (IsNoiseAreaBlocked((int)__args[0], area)) __result = false;
        }
    }
}
