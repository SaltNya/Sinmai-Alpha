using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.MusicSelect.ChainList;
using Process;
using Process.SubSequence;
using UI.DaisyChainList;
using UnityEngine;

namespace SinmaiAlpha.Difficulty;

public static partial class ExtraDifficulty
{
    private sealed class DifficultyWindow { public int Center; public int[] Difficulties; }
    private static readonly ConditionalWeakTable<MusicSelectChainList, DifficultyWindow> Windows = new();
    private static readonly MethodInfo GetCard = AccessTools.Method(typeof(DaisyChainList), "GetChain").MakeGenericMethod(typeof(MusicChainCardObejct));
    private static readonly MethodInfo SetCard = AccessTools.Method(typeof(DaisyChainList), "SetSpot");
    [HarmonyPatch(typeof(DifficultySelectSequence), "OnStartSequence")]
    public static class AvailableSelectionClampPatch
    {
        [HarmonyPrefix] public static void Prefix(DifficultySelectSequence __instance)
        {
            var process = Processing(__instance); int player = Player(__instance);
            int mask = SongMask(process.GetMusic(0));
            if (mask < 0) return;
            int resolved = DifficultyAvailability.Resolve(mask, (int)process.CurrentDifficulty[player]);
            if (resolved >= 0) process.CurrentDifficulty[player] = (MusicDifficultyID)resolved;
            if (process.DifficultySelectIndex[player] >= 0)
                process.DifficultySelectIndex[player] = DifficultyAvailability.Resolve(mask, process.DifficultySelectIndex[player]);
        }
    }

    [HarmonyPatch(typeof(DifficultySelectSequence), "CheckButton")]
    public static class AvailableSelectionButtonsPatch
    {
        [HarmonyPostfix] public static void Postfix(DifficultySelectSequence __instance, MusicSelectMonitor __0)
        {
            var process = Processing(__instance);
            int mask = SongMask(process.GetMusic(0));
            if (mask < 0) return;
            int difficulty = process.IsLevelTab() ? (int)Field<MusicDifficultyID>(__instance, "difficultyIndex") : (int)process.CurrentDifficulty[Player(__instance)];
            __0.SetVisibleButton(DifficultyAvailability.Next(mask, difficulty, -1) >= 0, InputManager.ButtonSetting.Button06);
            __0.SetVisibleButton(DifficultyAvailability.Next(mask, difficulty, 1) >= 0, InputManager.ButtonSetting.Button03);
        }
    }

    [HarmonyPatch(typeof(MusicSelectChainList), "DeployDifficultyList")]
    public static class AvailableDifficultyDeployPatch
    {
        [HarmonyPrefix] public static bool Prefix(MusicSelectChainList __instance, MusicDifficultyID __0)
        {
            var process = Field<IMusicSelectProcess>(__instance, "SelectProcess");
            Windows.Remove(__instance);
            int mask = SongMask(process.GetMusic(0));
            if (mask < 0) return true;
            var available = Enumerable.Range(0, DifficultyAvailability.Count).Where(d => (mask & (1 << d)) != 0).ToArray();
            __instance.RemoveAll();
            Traverse.Create(__instance).Field("_isDifficultySelect").SetValue(true);
            var window = Windows.GetOrCreateValue(__instance);
            window.Difficulties = available;
            window.Center = Array.IndexOf(available, DifficultyAvailability.Resolve(mask, (int)__0));
            var spots = Field<ChainObject[]>(__instance, "SpotArray");
            for (int index = 0; index < available.Length; index++)
            {
                int spot = 5 - window.Center + index;
                if (spot < 0 || spot >= spots.Length) continue;
                AddDifficultyCard(__instance, available[index], spot);
            }
            __instance.Positioning(true, true);
            __instance.IsListEnable = true;
            return false;
        }
    }
    private static void AddDifficultyCard(MusicSelectChainList list, int difficulty, int spot)
    {
        var card = (MusicChainCardObejct)GetCard.Invoke(list, null);
        list.SetChainData(card, 0, false, difficulty);
        card.ChangeSize(spot == 5);
        SetCard.Invoke(list, new object[] { spot, card });
    }
    [HarmonyPatch]
    public static class AvailableDifficultyWindowResetPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MusicSelectChainList), "DeployMusicList");
            yield return AccessTools.Method(typeof(MusicSelectChainList), "DeployUtageDifficultyList");
        }
        [HarmonyPrefix] public static void Prefix(MusicSelectChainList __instance) => Windows.Remove(__instance);
    }
    [HarmonyPatch(typeof(MusicSelectChainList), "Next")]
    public static class AvailableDifficultyScrollPatch
    {
        [HarmonyPrefix] public static bool Prefix(MusicSelectChainList __instance, Direction __1)
        {
            if (!Field<bool>(__instance, "_isDifficultySelect") || !Windows.TryGetValue(__instance, out var window)) return true;
            window.Center += (int)__1;
            var spots = Field<ChainObject[]>(__instance, "SpotArray");
            int spot = __1 == Direction.Right ? 0 : spots.Length - 1;
            int index = window.Center + spot - 5;
            if (index >= 0 && index < window.Difficulties.Length)
                AddDifficultyCard(__instance, window.Difficulties[index], spot);
            return false;
        }
    }

    [HarmonyPatch]
    public static class AvailableHeaderBoundaryPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DifficultyContoroller), "ButtonActiveCheckLeft");
            yield return AccessTools.Method(typeof(DifficultyContoroller), "ButtonActiveCheckRight");
        }
        [HarmonyPrefix] public static bool Prefix(DifficultyContoroller __instance, MethodBase __originalMethod, ref bool __result)
        {
            int mask = HeaderMask(__instance);
            if (mask < 0) return true;
            int current = DifficultyAvailability.Resolve(mask, Field<int>(__instance, "_difficulty"));
            __result = DifficultyAvailability.Next(mask, current, __originalMethod.Name.EndsWith("Right", StringComparison.Ordinal) ? 1 : -1) >= 0;
            return false;
        }
    }
}
