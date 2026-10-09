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

// 扩展音符对象池创建和地雷轨道池创建。
public partial class CustomNoteTypes
{

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameCtrl), "CreateNotePool")]
    public static void CreateNotePoolPostfix(GameCtrl __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (MinePools.ContainsKey(__instance)) return;
        LoadMineTextures();
        MineAudio.EnsurePlayers();

        // Every ring note also leases a shared guide. Expanding only mine taps
        // leaves later objects with a null/stale guide when the stock 128 fill.
        var guides = Traverse.Create(__instance).Field("_guideObjectList").GetValue<List<NoteGuide>>();
        var guideParent = Traverse.Create(__instance).Field("_guideListParent").GetValue<GameObject>();
        if (guides != null && guideParent != null)
            while (guides.Count < 256)
            {
                var guide = UnityEngine.Object.Instantiate(GameNotePrefabContainer.Guide, guideParent.transform);
                guide.gameObject.SetActive(false);
                guide.ParentTransform = guideParent.transform;
                guides.Add(guide);
            }

        var pools = new Dictionary<string, object>();
        // 注意：泛型参数必须显式给基类类型（TapNote 等）——游戏字段是 List<基类>，
        // 若按 lambda 返回类型推断成 Mine* 子类，List<Mine*> 无法转 List<基类>（泛型不变性）。
        // Signed HS bursts can enter the frame together. Overdead's slowest
        // option needs 159 live mine taps (+300 ms conservative release lag).
        AddMinePoolWithFactory<TapNote>(pools, __instance, "_tapObjectList", "_tapListParent", p => MineTapNote.CreateFrom(GameNotePrefabContainer.Tap, p), minimumCount: 192);
        AddMinePoolWithFactory<HoldNote>(pools, __instance, "_holdObjectList", "_holdListParent", p => MineHoldNote.CreateFrom(GameNotePrefabContainer.Hold, p));
        AddMinePoolWithFactory<BreakHoldNote>(pools, __instance, "_breakHoldObjectList", "_breakHoldListParent", p => MineBreakHoldNote.CreateFrom(GameNotePrefabContainer.BreakHold, p));
        AddMinePoolWithFactory<StarNote>(pools, __instance, "_starObjectList", "_starListParent", p => MineStarNote.CreateFrom(GameNotePrefabContainer.Star, p));
        AddMinePoolWithFactory<BreakStarNote>(pools, __instance, "_breakStarObjectList", "_breakStarListParent", p => MineBreakStarNote.CreateFrom(GameNotePrefabContainer.BreakStar, p));
        AddMinePoolWithFactory<BreakNote>(pools, __instance, "_breakObjectList", "_breakListParent", p => MineBreakNote.CreateFrom(GameNotePrefabContainer.Break, p));
        AddMinePoolWithFactory<TouchNoteB>(pools, __instance, "_touchBObjectList", "_touchListParent", p => MineTouchNoteB.CreateFrom(GameNotePrefabContainer.TouchTapB, p));
        AddMinePoolWithFactory<TouchNoteC>(pools, __instance, "_touchCTapObjectList", "_touchCTapListParent", p => MineTouchNoteC.CreateFrom(GameNotePrefabContainer.TouchTapC, p));
        AddMinePoolWithFactory<TouchHoldC>(pools, __instance, "_touchBHoldObjectList", "_touchHoldListParent", p => MineTouchHoldC.CreateFrom(GameNotePrefabContainer.TouchHoldC, p));
        AddMinePoolWithFactory<TouchHoldC>(pools, __instance, "_touchCHoldObjectList", "_touchCHoldListParent", p => MineTouchHoldC.CreateFrom(GameNotePrefabContainer.TouchHoldC, p));
        // slide 本体不在此贴图（贴图会遍历子物体误伤内部星）；轨道/箭头由独立地雷箭头池负责。
        AddMinePoolWithFactory<SlideRoot>(pools, __instance, "_slideObjectList", "_slideListParent", p => MineSlideRoot.CreateFrom(GameNotePrefabContainer.Slide, p), applyTextures: false);
        // fan slide（Wi-Fi）：MineSlideFan 独立类，Initialize 时贴 wifi_mine_0-10 轨道线。
        AddMinePoolWithFactory<SlideFan>(pools, __instance, "_fanSlideObjectList", "_fanSlideListParent", p => MineSlideFan.CreateFrom(GameNotePrefabContainer.SlideFan, p), applyTextures: false);
        AddArrowPools(pools, __instance);
        MinePools[__instance] = pools;

        // 绝赞独立池：普通感应区和 C 区均保持原版池的元素类型、数量和父物体。
        var criticalPools = new Dictionary<string, object>();
        AddMinePoolWithFactory<TouchNoteB>(criticalPools, __instance, "_touchBObjectList", "_touchListParent", p => TouchBreakNoteB.CreateFrom(GameNotePrefabContainer.TouchTapB, p));
        AddMinePoolWithFactory<TouchNoteC>(criticalPools, __instance, "_touchCTapObjectList", "_touchCTapListParent", p => TouchBreakNoteC.CreateFrom(GameNotePrefabContainer.TouchTapC, p), applyTextures: false);
        AddMinePoolWithFactory<TouchHoldC>(criticalPools, __instance, "_touchBHoldObjectList", "_touchHoldListParent", p => TouchBreakHoldC.CreateFrom(GameNotePrefabContainer.TouchHoldC, p));
        AddMinePoolWithFactory<TouchHoldC>(criticalPools, __instance, "_touchCHoldObjectList", "_touchCHoldListParent", p => TouchBreakHoldC.CreateFrom(GameNotePrefabContainer.TouchHoldC, p), applyTextures: false);
        TouchBreakPools[__instance] = criticalPools;

        // TouchStar 独立池（NMSTP 普通 / BRSTP 绝赞；逻辑同 touch，贴图五瓣星）。
        // applyTextures:false —— 贴图由 TouchStarNoteB/C.Initialize 统一处理（touch_star 系列）。
        // A/B/D/E 传感器区走 _touchBObjectList（TouchNoteB 组件），C 区走 _touchCTapObjectList（TouchNoteC 组件）。
        var touchStarPools = new Dictionary<string, object>();
        AddMinePoolWithFactory<TouchNoteB>(touchStarPools, __instance, "_touchBObjectList", "_touchListParent", p => TouchStarNoteB.CreateFrom(GameNotePrefabContainer.TouchTapB, p), applyTextures: false);
        AddMinePoolWithFactory<TouchNoteC>(touchStarPools, __instance, "_touchCTapObjectList", "_touchCTapListParent", p => TouchStarNoteC.CreateFrom(GameNotePrefabContainer.TouchTapC, p), applyTextures: false);
        TouchStarPools[__instance] = touchStarPools;

        // 地雷 TouchStar 独立池（MNSTP；五瓣星结构同 TouchStar，贴图 touch_star_mine，
        // 判定同地雷 touch）。applyTextures:false —— 贴图由 MineTouchStarNoteB/C.Initialize 统一处理。
        // B 区（_touchBObjectList）与 C 区（_touchCTapObjectList）都建，RegistNotePrefix 按
        // GetMineFieldsForNoteType(TouchTap) = {_touchBObjectList, _touchCTapObjectList} 自动分派。
        var mineTouchStarPools = new Dictionary<string, object>();
        AddMinePoolWithFactory<TouchNoteB>(mineTouchStarPools, __instance, "_touchBObjectList", "_touchListParent", p => MineTouchStarNoteB.CreateFrom(GameNotePrefabContainer.TouchTapB, p), applyTextures: false);
        AddMinePoolWithFactory<TouchNoteC>(mineTouchStarPools, __instance, "_touchCTapObjectList", "_touchCTapListParent", p => MineTouchStarNoteC.CreateFrom(GameNotePrefabContainer.TouchTapC, p), applyTextures: false);
        MineTouchStarPools[__instance] = mineTouchStarPools;

        MelonLogger.Msg($"[CustomNoteType] Mine pools ready: {string.Join(", ", pools.Select(p => $"{p.Key}={((System.Collections.ICollection)p.Value).Count}"))}");
    }

    // 按原版同类型池的数量，用 factory 为一种 note 类型创建独立地雷池。
    // TBase 必须是原版组件基类（如 TapNote），与游戏池字段 List<TBase> 一致；
    // factory 返回 Mine* 独立类对象（TBase 子类，组件替换）或原版组件克隆（如 SlideFan）。
    // applyTextures=false 时不在创建时贴图（如 slide：贴图会遍历子物体误伤内部星）。
    private static void AddMinePoolWithFactory<TBase>(Dictionary<string, object> pools, GameCtrl instance,
        string listField, string parentField, Func<Transform, TBase> factory, bool applyTextures = true, int minimumCount = 0) where TBase : Component
    {
        try
        {
            var gameList = Traverse.Create(instance).Field(listField).GetValue<List<TBase>>();
            var parent = Traverse.Create(instance).Field(parentField).GetValue<GameObject>();
            if (gameList == null || parent == null) return;

            var list = new List<TBase>();
            for (var i = 0; i < Math.Max(gameList.Count, minimumCount); i++)
            {
                var note = factory(parent.transform);
                note.gameObject.SetActive(false);
                TrySetParentTransform(note, parent.transform);
                if (applyTextures)
                {
                    ApplyMineTexturesToObject(note.gameObject);
                }

                list.Add(note);
            }

            pools[listField] = list;
        }
        catch (Exception e)
        {
            MelonLogger.Error($"[CustomNoteType] Failed to create mine pool {listField}: {e}");
        }
    }

    // slide 轨道箭头地雷池（_arrowObjectList / _breakArrowObjectList）：
    // RegistNote 的 slide 分支从这里取箭头（SetArrowObject），换好地雷贴图避免污染共享箭头池。
    private static void AddArrowPools(Dictionary<string, object> pools, GameCtrl instance)
    {
        try
        {
            var arrows = Traverse.Create(instance).Field("_arrowObjectList").GetValue<List<SpriteRenderer>>();
            if (arrows != null)
            {
                var list = new List<SpriteRenderer>();
                foreach (var unused in arrows)
                {
                    var sr = UnityEngine.Object.Instantiate(GameNotePrefabContainer.Arrow);
                    sr.gameObject.SetActive(false);
                    ApplyMineArrowTexture(sr);
                    list.Add(sr);
                }

                pools["_arrowObjectList"] = list;
            }

            var breakArrows = Traverse.Create(instance).Field("_breakArrowObjectList").GetValue<List<BreakSlide>>();
            if (breakArrows != null)
            {
                var list = new List<BreakSlide>();
                foreach (var unused in breakArrows)
                {
                    var bs = UnityEngine.Object.Instantiate(GameNotePrefabContainer.BreakArrow);
                    bs.gameObject.SetActive(false);
                    ApplyMineBreakArrowTexture(bs);
                    list.Add(bs);
                }

                pools["_breakArrowObjectList"] = list;
            }
        }
        catch (Exception e)
        {
            MelonLogger.Error($"[CustomNoteType] Failed to create mine arrow pools: {e}");
        }
    }
}
