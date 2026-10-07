using HarmonyLib;
using Monitor;

namespace SinmaiAlpha.Notes;

public class TapInHoldFix
{
    [HarmonyPatch(typeof(NoteBase), "IsJudgeNote")]
    [HarmonyPrefix]
    public static bool IsJudgeNote(NoteBase __instance, ref bool __result)
    {
        if (!ChartFeatureGate.AllowTapInHold(__instance.MonitorId, CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        __result = !__instance.IsEnd();
        return false;
    }
}