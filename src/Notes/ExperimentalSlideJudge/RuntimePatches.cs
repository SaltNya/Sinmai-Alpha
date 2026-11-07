using System;
using System.Collections.Generic;
using HarmonyLib;
using Manager;
using Monitor;
using SinmaiAlpha.Notes.Libs;
using DB;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge
{
    internal static class RuntimePatches
    {
        internal static void Install(HarmonyLib.Harmony harmony)
        {
            if (!ExperimentalJudgeConfig.Enabled) return;
            // Install callees before callers, and before the existing root
            // transpilers JIT their redirects. Otherwise Mono can inline the
            // old getter/NextCheck into an already generated Harmony wrapper.
            foreach (var type in PatchTypes) harmony.PatchAll(type);
        }

        internal static readonly System.Type[] PatchTypes = {
            typeof(ParsePatch), typeof(AreasPatch), typeof(TouchPatch), typeof(NextPatch),
            typeof(UnifiedSlideHooks.LegacyTranspilerBypass), typeof(UnifiedSlideHooks.ArrowPrefixBridge),
            typeof(UnifiedSlideHooks.CorrectSlideRedirects),
            typeof(InputFramePatch), typeof(InitializePatch), typeof(ReleasePatch)
        };

        [HarmonyPatch(typeof(CustomSlideNoteData), nameof(CustomSlideNoteData.ParseSlideCode))]
        internal static class ParsePatch
        {
            [HarmonyPrefix]
            internal static bool Prefix(CustomSlideNoteData __instance, string slideCode, OptionMirrorID mirrorMode, ref bool __result)
            {
                __result = RuntimeCache.Parse(__instance, slideCode, mirrorMode);
                return false;
            }
        }

        [HarmonyPatch(typeof(CustomNoteTypes), nameof(CustomNoteTypes.GetSlideHitAreaRedirect))]
        internal static class AreasPatch
        {
            [HarmonyPrefix]
            internal static bool Prefix(NoteData noteData, int starButton, ref List<SlideManager.HitArea> __result)
            {
                if (noteData is not CustomSlideNoteData data || !RuntimeCache.TryFrame(data, starButton, out var frame)) return true;
                __result = frame.Areas;
                return false;
            }
        }

        [HarmonyPatch(typeof(SlideRoot), nameof(SlideRoot.Initialize))]
        internal static class InitializePatch
        {
            [HarmonyPrefix, HarmonyPriority(Priority.First)]
            internal static void Prefix(SlideRoot __instance) => RuntimeCache.Detach(__instance);

            [HarmonyPostfix, HarmonyPriority(Priority.Last)]
            internal static void Postfix(SlideRoot __instance, NoteData note, List<SlideManager.HitArea> ____hitAreaList)
                => RuntimeCache.Attach(__instance, note, ____hitAreaList);
        }

        [HarmonyPatch(typeof(SlideRoot), "NoteCheck")]
        internal static class InputFramePatch
        {
            [HarmonyPrefix]
            internal static void Prefix(SlideRoot __instance)
            {
                if (!RuntimeCache.TryPlayer(__instance, out var state)) return;
                ulong pressed = 0;
                foreach (int panel in state.Frame.Sensors)
                    if (InputManager.ConvertTouchPanelRotatePush(__instance.MonitorId,
                        (InputManager.TouchPanelArea)panel, __instance.ButtonId)) pressed |= 1UL << panel;
                state.BeginFrame(pressed);
            }
        }

        [HarmonyPatch(typeof(SlideRoot), "NextCheck")]
        internal static class NextPatch
        {
            [HarmonyPrefix]
            internal static bool Prefix(SlideRoot __instance, int ____hitIndex, bool ____hitIn, ref bool __result)
            {
                if (!RuntimeCache.TryPlayer(__instance, out var state)) return true;
                __result = state.NextCheck(____hitIndex, ____hitIn);
                return false;
            }
        }

        [HarmonyPatch(typeof(SlideRoot), "CheckSlideTouch")]
        internal static class TouchPatch
        {
            [HarmonyPrefix]
            internal static bool Prefix(SlideRoot __instance, int index, bool In,
                ref int ____hitIndex, ref int ____hitSubIndex, ref bool ____hitIn, ref bool __result)
            {
                if (!RuntimeCache.TryPlayer(__instance, out var state)) return true;
                __result = state.CheckTouch(index, In, ref ____hitIndex, ref ____hitSubIndex, ref ____hitIn);
                return false;
            }
        }

        [HarmonyPatch(typeof(Process.GameProcess), "OnRelease")]
        internal static class ReleasePatch
        {
            [HarmonyPostfix] internal static void Postfix() => RuntimeCache.Clear();
        }
    }
}

namespace SinmaiAlpha.Notes
{
    public partial class CustomNoteTypes
    {
        // The existing Plugin.Apply lifecycle discovers this method; no edit
        // to Runtime.cs, Settings.cs, registrations or the old judge is needed.
        public static void OnBeforePatch(HarmonyLib.Harmony harmony)
            => ExperimentalSlideJudge.RuntimePatches.Install(harmony);
    }
}
