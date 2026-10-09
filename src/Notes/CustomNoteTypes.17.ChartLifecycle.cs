using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using SinmaiAlpha.Hosting;
using DB;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using Process;
using UnityEngine;
using SinmaiAlpha.Notes.Libs;

namespace SinmaiAlpha.Notes;

// 谱面开始及结束时的缓存清理。
public partial class CustomNoteTypes
{

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameProcess), "OnStart")]
    public static void OnGameStartClearHyperSpeed()
    {
        NoteSpeedMultipliers.Clear();
        NoteSpeedByIndex.Clear();
        PendingNoteSpeedMultipliers.Clear();
        PendingTouchSpeedMultipliers.Clear();
        PendingMineFlags.Clear();
        PendingTouchBreakFlags.Clear();
        PendingTouchStarFlags.Clear();
        PendingSvSegments.Clear();
        PendingHsSegments.Clear();
        SvCurves.Clear();
        HsCurves.Clear();
        SvClearTimes.Clear();
        SvTypeByNoteIndex.Clear();
        IgnoreSvByNoteIndex.Clear();
        SpeedMultByNoteIndex.Clear();
        EachChildByNoteIndex.Clear();
        _loggedSvInject = false;
        _loggedSvLeadScale = false;
        NoteKinds.Clear();
        MineNoteBehaviour.ClearInstances();
        MinePools.Clear();
        TouchBreakPools.Clear();
        TouchStarPools.Clear();
        MineTouchStarPools.Clear();
        _activeMineFields.Clear();
        _activeTouchBreakFields.Clear();
        _activeTouchStarFields.Clear();
        _activeMineTouchStarFields.Clear();
        HandledInitializations.Clear();
        RestoreAllMineGuideSprites();
        MineBreakSlides.Clear();
        MineBreakStars.Clear();
        MineHoldPenalties.Clear();
        _currentNoteData = null;
        _optionalSpeedForCurrentLoad = null;
        _removedOptionalSpeedString = null;
        _optionalSpeedRemoved = false;
        _optionalStreamIdForCurrentLoad = null;
        _removedOptionalStreamString = null;
        _optionalStreamRemoved = false;
        StreamTypeByNoteIndex.Clear();
        NoteScrollTableByNoteIndex.Clear();
        _pendingMineForCurrentLoad = false;
        _pendingTouchBreakForCurrentLoad = false;
        _pendingTouchStarForCurrentLoad = false;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameProcess), "OnRelease")]
    public static void OnGameReleaseClearHyperSpeed()
    {
        NoteSpeedMultipliers.Clear();
        NoteSpeedByIndex.Clear();
        PendingNoteSpeedMultipliers.Clear();
        PendingTouchSpeedMultipliers.Clear();
        PendingMineFlags.Clear();
        PendingTouchBreakFlags.Clear();
        PendingTouchStarFlags.Clear();
        PendingSvSegments.Clear();
        PendingHsSegments.Clear();
        SvCurves.Clear();
        HsCurves.Clear();
        SvClearTimes.Clear();
        SvTypeByNoteIndex.Clear();
        IgnoreSvByNoteIndex.Clear();
        SpeedMultByNoteIndex.Clear();
        EachChildByNoteIndex.Clear();
        _loggedSvInject = false;
        _loggedSvLeadScale = false;
        NoteKinds.Clear();
        MineNoteBehaviour.ClearInstances();
        MinePools.Clear();
        TouchBreakPools.Clear();
        TouchStarPools.Clear();
        MineTouchStarPools.Clear();
        _activeMineFields.Clear();
        _activeTouchBreakFields.Clear();
        _activeTouchStarFields.Clear();
        _activeMineTouchStarFields.Clear();
        HandledInitializations.Clear();
        RestoreAllMineGuideSprites();
        MineBreakSlides.Clear();
        MineBreakStars.Clear();
        MineHoldPenalties.Clear();
        _currentNoteData = null;
        _optionalSpeedForCurrentLoad = null;
        _removedOptionalSpeedString = null;
        _optionalSpeedRemoved = false;
        _optionalStreamIdForCurrentLoad = null;
        _removedOptionalStreamString = null;
        _optionalStreamRemoved = false;
        StreamTypeByNoteIndex.Clear();
        NoteScrollTableByNoteIndex.Clear();
        _pendingMineForCurrentLoad = false;
        _pendingTouchBreakForCurrentLoad = false;
        _pendingTouchStarForCurrentLoad = false;
    }
}
