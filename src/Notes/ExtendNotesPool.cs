using System;
using System.Collections;
using System.Collections.Generic;
using HarmonyLib;
using MAI2.Util;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;


public class ExtendNotesPool
{

    public static int count = 128;

    [HarmonyPrefix, HarmonyPriority(Priority.First + 200)]
    [HarmonyPatch(typeof(GameCtrl), "CreateNotePool")]
    public static void CaptureStock(GameCtrl __instance, out Dictionary<string, int> __state)
    {
        __state = null;
        if (!CustomNoteTypes.FeaturesEnabled(__instance)) return;
        // The native routine clears/recreates these pools. Its post-count is observed
        // by a first-priority postfix, before AquaMai and our extension run.
        __state = new Dictionary<string, int>();
    }
    [HarmonyPostfix, HarmonyPriority(Priority.First + 200)]
    [HarmonyPatch(typeof(GameCtrl), "CreateNotePool")]
    public static void CaptureNativeSizes(GameCtrl __instance, Dictionary<string, int> __state)
    {
        if (__state == null) return;
        foreach (var field in typeof(GameCtrl).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic))
            if (field.GetValue(__instance) is IList list) __state[field.Name] = list.Count;
    }
    [HarmonyPostfix, HarmonyPriority(Priority.Last - 200)]
    [HarmonyPatch(typeof(GameCtrl), "CreateNotePool")]
    public static void CreateNotePool(ref GameCtrl __instance, Dictionary<string, int> __state,
        GameObject ____tapListParent, List<TapNote> ____tapObjectList,
        GameObject ____holdListParent, List<HoldNote> ____holdObjectList,
        GameObject ____breakHoldListParent, List<BreakHoldNote> ____breakHoldObjectList,
        GameObject ____starListParent, List<StarNote> ____starObjectList,
        GameObject ____breakStarListParent, List<BreakStarNote> ____breakStarObjectList,
        GameObject ____breakListParent, List<BreakNote> ____breakObjectList,
        GameObject ____touchListParent, List<TouchNoteB> ____touchBObjectList,
        GameObject ____touchCTapListParent, List<TouchNoteC> ____touchCTapObjectList,
        GameObject ____touchCHoldListParent, List<TouchHoldC> ____touchCHoldObjectList,
        GameObject ____slideListParent, List<SlideRoot> ____slideObjectList,
        GameObject ____fanSlideListParent, List<SlideFan> ____fanSlideObjectList,
        GameObject ____slideJudgeListParent, List<SlideJudge> ____judgeSlideObjectList,
        GameObject ____guideListParent, List<NoteGuide> ____guideObjectList,
        GameObject ____barGuideListParent, List<BarGuide> ____barGuideObjectList,
        List<SpriteRenderer> ____arrowObjectList, List<BreakSlide> ____breakArrowObjectList
    )
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (__state == null) return;
        int Target(string name, int multiplier = 1) => (__state.TryGetValue(name, out var stock) ? stock : 0) + count * multiplier;
        while (____tapObjectList.Count < Target("_tapObjectList"))
        {
            var tapNote = Object.Instantiate(GameNotePrefabContainer.Tap, ____tapListParent.transform);
            tapNote.gameObject.SetActive(false);
            tapNote.ParentTransform = ____tapListParent.transform;
            ____tapObjectList.Add(tapNote);
        }
        while (____holdObjectList.Count < Target("_holdObjectList"))
        {
            var holdNote = Object.Instantiate(GameNotePrefabContainer.Hold, ____holdListParent.transform);
            holdNote.gameObject.SetActive(false);
            holdNote.ParentTransform = ____holdListParent.transform;
            ____holdObjectList.Add(holdNote);
        }
        while (____breakHoldObjectList.Count < Target("_breakHoldObjectList"))
        {
            var breakHoldNote = Object.Instantiate(GameNotePrefabContainer.BreakHold, ____breakHoldListParent.transform);
            breakHoldNote.gameObject.SetActive(false);
            breakHoldNote.ParentTransform = ____breakHoldListParent.transform;
            ____breakHoldObjectList.Add(breakHoldNote);
        }
        while (____starObjectList.Count < Target("_starObjectList"))
        {
            var starNote = Object.Instantiate(GameNotePrefabContainer.Star, ____starListParent.transform);
            starNote.gameObject.SetActive(false);
            starNote.ParentTransform = ____starListParent.transform;
            ____starObjectList.Add(starNote);
        }
        while (____breakStarObjectList.Count < Target("_breakStarObjectList"))
        {
            var breakStarNote = Object.Instantiate(GameNotePrefabContainer.BreakStar, ____breakStarListParent.transform);
            breakStarNote.gameObject.SetActive(false);
            breakStarNote.ParentTransform = ____breakStarListParent.transform;
            ____breakStarObjectList.Add(breakStarNote);
        }
        while (____breakObjectList.Count < Target("_breakObjectList"))
        {
            var breakNote = Object.Instantiate(GameNotePrefabContainer.Break, ____breakListParent.transform);
            breakNote.gameObject.SetActive(false);
            breakNote.ParentTransform = ____breakListParent.transform;
            ____breakObjectList.Add(breakNote);
        }
        while (____touchBObjectList.Count < Target("_touchBObjectList"))
        {
            var touchNoteB = Object.Instantiate(GameNotePrefabContainer.TouchTapB, ____touchListParent.transform);
            touchNoteB.gameObject.SetActive(false);
            touchNoteB.ParentTransform = ____touchListParent.transform;
            ____touchBObjectList.Add(touchNoteB);
        }
        while (____touchCTapObjectList.Count < Target("_touchCTapObjectList"))
        {
            var touchNoteC = Object.Instantiate(GameNotePrefabContainer.TouchTapC, ____touchCTapListParent.transform);
            touchNoteC.gameObject.SetActive(false);
            touchNoteC.ParentTransform = ____touchCTapListParent.transform;
            ____touchCTapObjectList.Add(touchNoteC);
        }
        while (____touchCHoldObjectList.Count < Target("_touchCHoldObjectList"))
        {
            var touchHoldC = Object.Instantiate(GameNotePrefabContainer.TouchHoldC, ____touchCHoldListParent.transform);
            touchHoldC.gameObject.SetActive(false);
            touchHoldC.ParentTransform = ____touchCHoldListParent.transform;
            ____touchCHoldObjectList.Add(touchHoldC);
        }
        while (____slideObjectList.Count < Target("_slideObjectList"))
        {
            var slideRoot = Object.Instantiate(GameNotePrefabContainer.Slide, ____slideListParent.transform);
            slideRoot.gameObject.SetActive(false);
            slideRoot.ParentTransform = ____slideListParent.transform;
            ____slideObjectList.Add(slideRoot);
        }
        while (____fanSlideObjectList.Count < Target("_fanSlideObjectList"))
        {
            var slideFan = Object.Instantiate(GameNotePrefabContainer.SlideFan, ____fanSlideListParent.transform);
            slideFan.gameObject.SetActive(false);
            slideFan.ParentTransform = ____fanSlideListParent.transform;
            ____fanSlideObjectList.Add(slideFan);
        }
        while (____judgeSlideObjectList.Count < Target("_judgeSlideObjectList"))
        {
            var slideJudge = Object.Instantiate(GameNotePrefabContainer.SlideJudge, ____slideJudgeListParent.transform);
            slideJudge.gameObject.SetActive(false);
            slideJudge.ParentTransform = ____slideJudgeListParent.transform;
            slideJudge.SetOption(Singleton<GamePlayManager>.Instance.GetGameScore(__instance.MonitorIndex).UserOption.DispJudge);
            ____judgeSlideObjectList.Add(slideJudge);
        }
        while (____guideObjectList.Count < Target("_guideObjectList"))
        {
            var noteGuide = Object.Instantiate(GameNotePrefabContainer.Guide, ____guideListParent.transform);
            noteGuide.gameObject.SetActive(false);
            noteGuide.ParentTransform = ____guideListParent.transform;
            ____guideObjectList.Add(noteGuide);
        }
        while (____barGuideObjectList.Count < Target("_barGuideObjectList"))
        {
            var barGuide = Object.Instantiate(GameNotePrefabContainer.BarGuide, ____barGuideListParent.transform);
            barGuide.gameObject.SetActive(false);
            barGuide.ParentTransform = ____barGuideListParent.transform;
            ____barGuideObjectList.Add(barGuide);
        }
        while (____arrowObjectList.Count < Target("_arrowObjectList", 50))
        {
            var spriteRenderer = Object.Instantiate(GameNotePrefabContainer.Arrow, ____slideListParent.transform);
            spriteRenderer.gameObject.SetActive(false);
            ____arrowObjectList.Add(spriteRenderer);
        }
        while (____breakArrowObjectList.Count < Target("_breakArrowObjectList", 50))
        {
            var breakSlide = Object.Instantiate(GameNotePrefabContainer.BreakArrow, ____slideListParent.transform);
            breakSlide.gameObject.SetActive(false);
            ____breakArrowObjectList.Add(breakSlide);
        }

    }
}

