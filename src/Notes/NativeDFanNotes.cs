using SinmaiAlpha.Notes.Libs;
using HarmonyLib;
using Manager;
using Monitor;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    [HarmonyPatch(typeof(SlideFan), "Initialize")]
    public static class NativeDFanInitializePatch
    {
        public static void Prefix(SlideFan __instance, NoteData note, out int __state)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) { __state = default; return; }

            // Fan.Initialize does not call SlideRoot.Initialize. Reset context
            // even on ordinary notes, including pooled mine fans.
            Traverse.Create(__instance).Field("NoteData").SetValue(note);
            __state = __instance.EndButtonId;
            if (note is CustomSlideNoteData data && data.IsFan)
                __instance.EndButtonId = note.slideData.targetNote;
        }

        public static void Postfix(SlideFan __instance, NoteData note, int __state)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            __instance.EndButtonId = __state;
            if (!(note is CustomSlideNoteData data) || !data.IsFan) return;
            var offset = data.FanVisualOffsets[__instance.ButtonId];
            var rotation = Quaternion.Euler(0f, 0f, offset);
            __instance.transform.localRotation *= rotation;
            // Native fan art follows the start ring; star paths were authored
            // in that frame. Grade objects use a separate shared launcher.
            var judge = Traverse.Create(__instance).Field("JudgeObj").GetValue<SlideJudge>();
            if (judge != null)
            {
                judge.transform.localPosition = rotation * judge.transform.localPosition;
                judge.transform.localRotation = rotation * judge.transform.localRotation;
            }
            TryApplySpeedToNoteObject(__instance, note);
        }
    }

    [HarmonyPatch(typeof(SlideFan), "GetSlideArrowNum", new[] { typeof(NoteData) })]
    public static class NativeDFanArrowCountPatch
    {
        public static bool Prefix(NoteData note, ref int __result)
        {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return true; }

            if (!(note is CustomSlideNoteData data) || !data.IsFan) return true;
            // The fan prefab owns 22 side sprites, rather than a pooled list
            // inferred by looking up an A-only stock route.
            __result = 22;
            return false;
        }
    }
}
