using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Manager;
using MelonLoader;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    [HarmonyPatch]
    public static class ChartLoadDiagnosticsPatch
    {
        public static IEnumerable<MethodBase> TargetMethods() => typeof(NotesReader)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.Name == "loadMa2" || m.Name == "loadDLMusicScore");

        [HarmonyPostfix]
        public static void Postfix(NotesReader __instance, string __0, object[] __args, bool __result)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            if (!__args.OfType<LoadType>().Any(t => t == LoadType.LOAD_FULL)) return;
            var notes = __instance.GetNoteList();
            var count = notes?.Count ?? 0;
            var tail = notes == null || count == 0 ? 0 : notes.Max(n => n.end.msec);
            var message = $"[Chart Load] {__0}: success={__result}, notes={count}, scoreNotes={__instance.GetTotal().GetAllNoteNum()}, tailMsec={tail:F3}, recordLimit={TotalMa2RecordCount}";
            if (!__result || count == 0) MelonLogger.Error(message);
            else MelonLogger.Msg(message);
        }

        [HarmonyFinalizer]
        public static void Finalizer(string __0, Exception __exception)
        {
            if (__exception != null) MelonLogger.Error($"[Chart Load] {__0}: {__exception}");
        }
    }
}
