using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.MusicSelect.UI;
using Process;
using Process.SubSequence;
using UI;
using UnityEngine;

namespace SinmaiAlpha.Difficulty;

public static partial class ExtraDifficulty
{
    private sealed class ButtonPose
    {
        public Vector3 Scale;
        public Vector2 Position;
        public float Alpha;
    }
    private static readonly ConditionalWeakTable<DifficultyContoroller, ButtonPose> ButtonPoses = new();
    [ThreadStatic] private static int drawingSong;
    private static int HeaderMask(DifficultyContoroller controller)
    {
        if (controller.kaleidoScopeMode) return -1;
        var monitor = controller.GetComponentsInParent<MusicSelectMonitor>(true).FirstOrDefault();
        var process = monitor == null ? null : Field<IMusicSelectProcess>(monitor, "_musicSelect");
        if (process == null || drawingSong == 0 && process.IsGenreSeq(Field<int>(controller, "_playerIndex"))) return -1;
        return SongMask(process.GetMusic(0));
    }

    [HarmonyPatch(typeof(MusicSelectMonitor), "OnStartMusicSelect")]
    public static class AvailableSongDrawingPatch
    {
        // Native GenreSelect draws the song before publishing its next sequence.
        [HarmonyPrefix] public static void Prefix() => drawingSong++;
        [HarmonyFinalizer] public static void Finalizer() => drawingSong--;
    }

    [HarmonyPatch(typeof(DifficultyContoroller), "Initialize")]
    public static class AvailableButtonPosePatch
    {
        [HarmonyPrefix] public static void Prefix(DifficultyContoroller __instance)
        {
            var prefab = Field<DifficultyButtonObject>(__instance, "_controlBtnPrefab");
            if (prefab == null) return;
            var rect = prefab.GetComponent<RectTransform>();
            ButtonPoses.Remove(__instance);
            ButtonPoses.Add(__instance, new ButtonPose { Scale = prefab.transform.localScale,
                Position = rect.anchoredPosition, Alpha = prefab.GetComponent<CanvasGroup>()?.alpha ?? 1f });
        }
    }

    [HarmonyPatch(typeof(DifficultyContoroller), "ChangeButton")]
    public static class AvailableButtonsPatch
    {
        [HarmonyPrefix] public static bool Prefix(DifficultyContoroller __instance, bool __0, bool __1, bool __2)
        {
            int mask = HeaderMask(__instance);
            if (mask < 0) return true;
            int current = DifficultyAvailability.Resolve(mask, Field<int>(__instance, "_difficulty"));
            if (current >= 0) Traverse.Create(__instance).Field("_difficulty").SetValue(current);
            UpdateAvailableButton(__instance, false, DifficultyAvailability.Next(mask, current, -1), __0 && !__1, __2);
            UpdateAvailableButton(__instance, true, DifficultyAvailability.Next(mask, current, 1), __0 && __1, __2);
            return false;
        }
    }

    private static void UpdateAvailableButton(DifficultyContoroller controller, bool right, int target, bool pressed, bool led)
    {
        string objectField = right ? "_rightButtonObject" : "_leftButtonObject";
        string activeField = right ? "_isRightActive" : "_isLeftActive";
        var button = Field<DifficultyButtonObject>(controller, objectField);
        if (button == null) return;
        bool active = Field<bool>(controller, activeField);
        if (target < 0)
        {
            if (active && button.gameObject.activeInHierarchy) button.SetActiveButton(false);
            else button.gameObject.SetActive(false);
            Traverse.Create(controller).Field(activeField).SetValue(false);
            return;
        }
        if (!active || !button.gameObject.activeSelf)
        {
            // An Out clip can be cached as the Animator's new default after pooling.
            // Restore the prefab pose once, before rebinding; never force UI every frame.
            button.StopAllCoroutines();
            Traverse.Create(button).Field("_disableCoroutine").SetValue(null);
            var animator = Field<Animator>(button, "ButtonAnimator");
            if (animator != null) animator.enabled = false;
            if (ButtonPoses.TryGetValue(controller, out var pose))
            {
                button.transform.localScale = pose.Scale;
                button.GetComponent<RectTransform>().anchoredPosition = pose.Position;
                var group = button.GetComponent<CanvasGroup>();
                if (group != null) group.alpha = pose.Alpha;
            }
            button.gameObject.SetActive(true);
            if (animator != null) { animator.enabled = true; animator.Rebind(); }
            button.SetActiveImmediateButton(true);
            Traverse.Create(controller).Field(activeField).SetValue(true);
        }
        var sprites = Field<Sprite[]>(controller, "_buttonTypeASprites");
        var sprite = target < sprites.Length ? sprites[target] : sprites[3];
        button.ChangeDifficulty(Field<Sprite>(controller, right ? "_buttonPlusSymbolSprite" : "_buttonMinusSymbolSprite"), sprite, false, target);
        if (pressed) button.Pressed();
        if (led) button.ChangeColor(AvailableButtonColor(target), true);
    }

    private static CommonButtonObject.LedColors AvailableButtonColor(int target) => target < 0
        ? CommonButtonObject.LedColors.Black
        : (CommonButtonObject.LedColors)AccessTools.Method(typeof(DifficultyContoroller), "GetLedColorEnum")
            .Invoke(null, new object[] { target });

    [HarmonyPatch(typeof(DifficultyContoroller), "SetButtonColor")]
    public static class AvailableButtonColorsPatch
    {
        [HarmonyPrefix] public static bool Prefix(DifficultyContoroller __instance, bool __0)
        {
            int mask = HeaderMask(__instance);
            if (!__0 || mask < 0) return true;
            int current = DifficultyAvailability.Resolve(mask, Field<int>(__instance, "_difficulty"));
            Field<DifficultyButtonObject>(__instance, "_leftButtonObject")?.ChangeColor(
                AvailableButtonColor(DifficultyAvailability.Next(mask, current, -1)), true);
            Field<DifficultyButtonObject>(__instance, "_rightButtonObject")?.ChangeColor(
                AvailableButtonColor(DifficultyAvailability.Next(mask, current, 1)), true);
            return false;
        }
    }

    [HarmonyPatch]
    public static class AvailableHeaderStepPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DifficultyContoroller), "AddDifficulty");
            yield return AccessTools.Method(typeof(DifficultyContoroller), "SubDifficulty");
        }
        [HarmonyPrefix] public static bool Prefix(DifficultyContoroller __instance, MethodBase __originalMethod)
        {
            int mask = HeaderMask(__instance);
            if (mask < 0) return true;
            int current = DifficultyAvailability.Resolve(mask, Field<int>(__instance, "_difficulty"));
            int target = DifficultyAvailability.Next(mask, current, __originalMethod.Name == "AddDifficulty" ? 1 : -1);
            if (target >= 0) __instance.SetDifficulty((MusicDifficultyID)target, true);
            return false;
        }
    }

    [HarmonyPatch]
    public static class AvailableHeaderVisibilityResetPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DifficultyContoroller), "SetButtonVisible");
            yield return AccessTools.Method(typeof(DifficultyContoroller), "SetDifficultyExtra");
        }
        [HarmonyPrefix] public static void Prefix(DifficultyContoroller __instance, MethodBase __originalMethod, object[] __args)
        {
            if (HeaderMask(__instance) < 0 || __originalMethod.Name == "SetButtonVisible" && (bool)__args[0]) return;
            Traverse.Create(__instance).Field("_isLeftActive").SetValue(false);
            Traverse.Create(__instance).Field("_isRightActive").SetValue(false);
        }
    }

    [HarmonyPatch(typeof(DifficultyContoroller), "ForcedSetDifficulty")]
    public static class AvailableHeaderForcedPatch
    {
        [HarmonyPrefix] public static bool Prefix(DifficultyContoroller __instance, MusicDifficultyID __0)
        {
            if (HeaderMask(__instance) < 0) return true;
            __instance.SetDifficulty(__0);
            return false;
        }
    }
}
