using System;
using System.Diagnostics;
using HarmonyLib;
using MelonLoader;
using Process;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // One stopwatch per process, no per-frame allocation. A long frame and
    // release are recorded separately so a wait cannot masquerade as GC/FX.
    [HarmonyPatch(typeof(GameProcess), "OnUpdate")]
    public static class ChartLongFramePatch
    {
        private static readonly Stopwatch Watch = new();
        private static long lastReport;
        [HarmonyPrefix] public static void Prefix() {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }
        Watch.Restart();
    }
        [HarmonyPostfix] public static void Postfix()
        {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }

            Watch.Stop();
            if (Watch.ElapsedMilliseconds < 250 || Environment.TickCount - lastReport < 2000) return;
            lastReport = Environment.TickCount;
            MelonLogger.Warning("[Chart Ending] Slow game update: " + Watch.ElapsedMilliseconds +
                " ms; chartTime=" + Manager.NotesManager.GetCurrentMsec() + " ms; heap=" + GC.GetTotalMemory(false) / 1048576 + " MiB");
        }
    }
    [HarmonyPatch(typeof(GameProcess), "OnRelease")]
    public static class ChartReleaseDurationPatch
    {
        private static readonly Stopwatch Watch = new();
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        public static void Prefix()
        {
            Watch.Restart();
            MelonLogger.Msg("[Chart Ending] Release begins; chartTime=" + Manager.NotesManager.GetCurrentMsec() +
                " ms; heap=" + GC.GetTotalMemory(false) / 1048576 + " MiB");
        }
        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix()
        { Watch.Stop(); MelonLogger.Msg("[Chart Ending] Release completed in " + Watch.ElapsedMilliseconds + " ms"); }
    }
}
