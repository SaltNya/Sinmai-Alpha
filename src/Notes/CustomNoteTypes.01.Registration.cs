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

// 读取上下文、MA2 扩展记录表注册及地雷贴图加载。
public partial class CustomNoteTypes
{

    // Hyper Speed: per-note speed overrides keyed by the actual NoteData instance and by note index.
    private static Dictionary<NoteData, float> NoteSpeedMultipliers => RuntimeCharts.Current.NoteSpeedMultipliers;
    private static Dictionary<int, float> NoteSpeedByIndex => RuntimeCharts.Current.NoteSpeedByIndex;
    private static Queue<float> PendingNoteSpeedMultipliers => RuntimeCharts.Current.PendingNoteSpeedMultipliers;
    private static Queue<float> PendingTouchSpeedMultipliers => RuntimeCharts.Current.PendingTouchSpeedMultipliers;
    private static NoteData _currentNoteData { get => RuntimeCharts.Current._currentNoteData; set => RuntimeCharts.Current._currentNoteData = value; }
    private static int _noteCountBeforeLoad { get => RuntimeCharts.Current._noteCountBeforeLoad; set => RuntimeCharts.Current._noteCountBeforeLoad = value; }
    private static float? _optionalSpeedForCurrentLoad { get => RuntimeCharts.Current._optionalSpeedForCurrentLoad; set => RuntimeCharts.Current._optionalSpeedForCurrentLoad = value; }
    private static string _removedOptionalSpeedString { get => RuntimeCharts.Current._removedOptionalSpeedString; set => RuntimeCharts.Current._removedOptionalSpeedString = value; }
    private static bool _optionalSpeedRemoved { get => RuntimeCharts.Current._optionalSpeedRemoved; set => RuntimeCharts.Current._optionalSpeedRemoved = value; }
    // 可重叠流类型键（行尾 s{N} 字段）：当前 loadNote 的音符所属流；非流内音符为 null。
    private static string _optionalStreamIdForCurrentLoad { get => RuntimeCharts.Current._optionalStreamIdForCurrentLoad; set => RuntimeCharts.Current._optionalStreamIdForCurrentLoad = value; }
    private static string _removedOptionalStreamString { get => RuntimeCharts.Current._removedOptionalStreamString; set => RuntimeCharts.Current._removedOptionalStreamString = value; }
    private static bool _optionalStreamRemoved { get => RuntimeCharts.Current._optionalStreamRemoved; set => RuntimeCharts.Current._optionalStreamRemoved = value; }

    private static Dictionary<int, CustomNoteKind> NoteKinds => RuntimeCharts.Current.NoteKinds;

    // 当前谱面 NoteData 列表缓存（loadMa2Main postfix 填充；地雷 hold 触碰检测用——
    // 判定同轨道普通 tap 是否在判定窗口内，让点击优先判定给 tap）。
    private static List<Manager.NoteData> _activeNoteList { get => RuntimeCharts.Current._activeNoteList; set => RuntimeCharts.Current._activeNoteList = value; }

    private static bool _pendingMineForCurrentLoad { get => RuntimeCharts.Current._pendingMineForCurrentLoad; set => RuntimeCharts.Current._pendingMineForCurrentLoad = value; }
    private static bool _pendingTouchBreakForCurrentLoad { get => RuntimeCharts.Current._pendingTouchBreakForCurrentLoad; set => RuntimeCharts.Current._pendingTouchBreakForCurrentLoad = value; }
    private static bool _pendingTouchStarForCurrentLoad { get => RuntimeCharts.Current._pendingTouchStarForCurrentLoad; set => RuntimeCharts.Current._pendingTouchStarForCurrentLoad = value; }

    // findID is called in a batch before loadNote, so use queues to keep mine flags in chart order.
    private static Queue<bool> PendingMineFlags => RuntimeCharts.Current.PendingMineFlags;
    private static Queue<bool> PendingTouchBreakFlags => RuntimeCharts.Current.PendingTouchBreakFlags;
    private static Queue<bool> PendingTouchStarFlags => RuntimeCharts.Current.PendingTouchStarFlags;

    public static void OnAfterPatch()
    {
        ChartFeatureGate.CaptureNativeRecords();
        AllNoteTypeNames.UnionWith(KnownMa2FieldCounts.Keys);
        AllNoteTypeNames.UnionWith(MineToBaseMap.Keys);
        AllNoteTypeNames.Add("BRTTP");
        AllNoteTypeNames.Add("BRTHO");
        AllNoteTypeNames.Add("MBTTP");
        AllNoteTypeNames.Add("MBTHO");
        AllNoteTypeNames.Add("NMSTP");
        AllNoteTypeNames.Add("BRSTP");

        var arrayTraverse = Traverse.Create(typeof(Ma2fileRecordID)).Field("s_Ma2fileRecord_Data");
        var targetArray = arrayTraverse.GetValue<Array>();

        var nextId = targetArray.Length;
        List<object[]> newEntries =
        [
            [nextId++, "NMSSS", "过新过热Slide", NotesTypeID.Def.Slide, SlideType.Slide_MAX, 8, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "BRSSS", "过新过热BreakSlide", NotesTypeID.Def.BreakSlide, SlideType.Slide_MAX, 8, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],

            // ========== 地雷键 ==========
            // 命名规则（2026-08 用户定版）：
            //   MN = 普通(NM) 的地雷版
            //   MB = Break(BR) 的地雷版
            //   MX = Ex(EX) 的地雷版
            //   MZ = ExBreak(BX) 的地雷版
            //   BR = 绝赞系（非地雷）：BRTTP = 绝赞 touch、BRTHO = 绝赞 touchhold
            //   MB = 地雷绝赞：MBTTP = 地雷绝赞 touch、MBTHO = 地雷绝赞 touchhold

            // MN: 普通地雷 (NMTAP->MNTAP, NMHLD->MNHLD, ...)
            [nextId++, "MNTAP", "地雷Tap", NotesTypeID.Def.Tap, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MNHLD", "地雷Hold", NotesTypeID.Def.Hold, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MNSTR", "地雷Star", NotesTypeID.Def.Star, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MNSSS", "地雷Slide", NotesTypeID.Def.Slide, SlideType.Slide_MAX, 8, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MNTTP", "地雷Touch", NotesTypeID.Def.TouchTap, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MBTTP", "地雷绝赞Touch", NotesTypeID.Def.TouchTap, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MNTHO", "地雷TouchHold", NotesTypeID.Def.TouchHold, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MBTHO", "地雷绝赞TouchHold", NotesTypeID.Def.TouchHold, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],

            // MB: Break 地雷 (BRTAP->MBTAP, BRHLD->MBHLD, ...)
            [nextId++, "MBTAP", "地雷BreakTap", NotesTypeID.Def.Break, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MBHLD", "地雷BreakHold", NotesTypeID.Def.BreakHold, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MBSTR", "地雷BreakStar", NotesTypeID.Def.BreakStar, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MBSSS", "地雷BreakSlide", NotesTypeID.Def.BreakSlide, SlideType.Slide_MAX, 8, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],

            // MZ: ExBreak 地雷 (BXTAP->MZTAP, BXHLD->MZHLD, ...)
            [nextId++, "MZTAP", "地雷ExBreakTap", NotesTypeID.Def.ExBreakTap, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MZHLD", "地雷ExBreakHold", NotesTypeID.Def.ExBreakHold, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MZSTR", "地雷ExBreakStar", NotesTypeID.Def.ExBreakStar, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],

            // MX: Ex 地雷 (EXTAP->MXTAP, EXHLD->MXHLD, ...)
            [nextId++, "MXTAP", "地雷ExTap", NotesTypeID.Def.Tap, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MXHLD", "地雷ExHold", NotesTypeID.Def.Hold, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
            [nextId++, "MXSTR", "地雷ExStar", NotesTypeID.Def.Star, SlideType.Slide_Straight, 0, Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0],
        ];

        foreach (var visualTag in new[] { "COLORV", "SIZEV", "ALPHAV", "SVSP", "HS", "SPAWN", "SPAWNMODE", "DESTROY", "BOUNCE",
                     "SHOWJUDGEINFO", "SHOWCOMBOINFO", "SHOWJUDGETEXT", "COMBODISPLAY", "INNERBRIGHTNESS", "OUTERBRIGHTNESS", "SHAKE", "FLASH", "FADE", "TINT", "GAUSSIAN", "NEON", "TRAIL", "BRIGHTNESS", "SATURATION", "CONTRAST", "RAINBOW", "VIGNETTE", "ZOOM", "GLITCH", "TVNOISE", "HUE", "MOVE", "ROTATE", "TEXT", "AUDIO", "PVOVERLAY" })
            newEntries.Add([nextId++, visualTag, "音符即时外观", NotesTypeID.Def.Invalid, SlideType.Slide_INVALID, 4,
                Ma2Category.MA2_Composition, 2, 2, 0, 0, 0, 0, 0]);

        // Register mine variants for all normal (non-break) native slide types.
        string[] mineSlideBases =
        [
            "NMSCR", "NMSCL", "NMSI_", "NMSLL", "NMSLR",
            "NMSUL", "NMSUR", "NMSXL", "NMSXR", "NMSF_",
            "NMSV_", "NMSSL", "NMSSR", "NMSL_", "NMSR_",
            "NMSU_", "NMSD_", "NMSNL", "NMSNR", "NMSFL",
            "NMSFR",
        ];

        foreach (var baseName in mineSlideBases)
        {
            var mineName = "MN" + baseName.Substring(2);
            newEntries.Add(
            [
                nextId++, mineName, "地雷" + baseName, NotesTypeID.Def.Slide, SlideType.Slide_MAX, 8,
                Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0,
            ]);
        }

        // Register mine variants for Break native slide types.
        string[] breakSlideBases =
        [
            "BRSCR", "BRSCL", "BRSI_", "BRSLL", "BRSLR",
            "BRSUL", "BRSUR", "BRSXL", "BRSXR", "BRSF_",
            "BRSV_", "BRSSL", "BRSSR", "BRSL_", "BRSR_",
            "BRSU_", "BRSD_", "BRSNL", "BRSNR", "BRSFL",
            "BRSFR",
        ];

        foreach (var baseName in breakSlideBases)
        {
            var mineName = "MB" + baseName.Substring(2);
            newEntries.Add(
            [
                nextId++, mineName, "地雷" + baseName, NotesTypeID.Def.BreakSlide, SlideType.Slide_MAX, 8,
                Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0,
            ]);
        }

        // Register mine variants for ExBreak native slide types.
        string[] exBreakSlideBases =
        [
            "BXSCR", "BXSCL", "BXSI_", "BXSLL", "BXSLR",
            "BXSUL", "BXSUR", "BXSXL", "BXSXR", "BXSF_",
            "BXSV_", "BXSSL", "BXSSR", "BXSL_", "BXSR_",
            "BXSU_", "BXSD_", "BXSNL", "BXSNR", "BXSFL",
            "BXSFR",
        ];

        foreach (var baseName in exBreakSlideBases)
        {
            var mineName = "MZ" + baseName.Substring(2);
            newEntries.Add(
            [
                nextId++, mineName, "地雷" + baseName, NotesTypeID.Def.ExBreakSlide, SlideType.Slide_MAX, 8,
                Ma2Category.MA2_Note, 2, 2, 2, 2, 2, 2, 0,
            ]);
        }


        // Append after all prior records to preserve their IDs.
        newEntries.Add([nextId++, "NZONE", "噪域事件", NotesTypeID.Def.Invalid, SlideType.Slide_INVALID, 4,
            Ma2Category.MA2_Composition, 2, 2, 0, 0, 0, 0, 0]);
        foreach (var filter in SinmaiAlpha.ChartVisuals.ExtraScreenFilters.All)
            newEntries.Add([nextId++, filter.Tag, "Alpha屏幕滤镜", NotesTypeID.Def.Invalid, SlideType.Slide_INVALID, 4,
                Ma2Category.MA2_Composition, 2, 2, 0, 0, 0, 0, 0]);

        ChartFeatureGate.RegisterExtensions(newEntries.Select(e => (string)e[1]).Concat(AllNoteTypeNames));
        // Ma2fileRecordID.Ma2fileRecord_Data is private, so we need this shit.
        var structType = targetArray.GetValue(0).GetType();
        var constructor = AccessTools.Constructor(structType,
        [
            typeof(int), typeof(string), typeof(string), typeof(NotesTypeID.Def), typeof(SlideType), typeof(int),
            typeof(Ma2Category), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int),
            typeof(int)
        ]);

        Ma2FileRecordData = Array.CreateInstance(structType, targetArray.Length + newEntries.Count);
        Array.Copy(targetArray, Ma2FileRecordData, targetArray.Length);

        for (var i = 0; i < newEntries.Count; i++)
        {
            var j = targetArray.Length + i;
            var obj = constructor.Invoke(newEntries[i]);
            Ma2FileRecordData.SetValue(obj, j);
        }

        arrayTraverse.SetValue(Ma2FileRecordData);
        TotalMa2RecordCount = Ma2FileRecordData.Length;
        LastMa2RecordID = TotalMa2RecordCount - 1;
        MelonLogger.Msg($"[CustomNoteType] MA2 record data extended, total count: {TotalMa2RecordCount}");

        // Initialize related classes ...
        SlideDataBuilder.InitializeHitAreasLookup();
        MelonLogger.Msg($"[CustomNoteType] HitAreasLookup initialized, total count: {SlideDataBuilder.HitAreasLookup.Count}");

    }

    private static void LoadMineTextures()
    {
        if (_mineTexturesLoaded) return;
        _mineTexturesLoaded = true;

        var dir = FileSystem.ResolvePath("Sinmai-Alpha/CustomNoteTypes");
        if (!Directory.Exists(dir))
        {
            MelonLogger.Warning($"[CustomNoteType] Mine texture directory not found: {dir}");
            return;
        }

        foreach (var file in Directory.EnumerateFiles(dir, "*.png", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
            var texture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            if (texture.LoadImage(File.ReadAllBytes(file)))
            {
                // touch_star 花瓣素材方向与原版 touch 相反（上宽下窄 vs 原版下宽上窄），
                // 垂直翻转后瓣尖才朝外/朝上（与原版 touch 一致）。
                // touch_star_mine（地雷 touchstar MNSTP 用）素材方向与 touch_star 相同 → 同样翻转。
                if (name is "touch_star" or "touch_star_break" or "touch_star_each" or "touch_star_break_each" or "touch_star_mine" or "touch_star_mine_each")
                {
                    // 素材方向与原版 touch 相反（上宽下窄 vs 原版下宽上窄），加载时上下翻转。
                    texture = FlipTextureVertical(texture);
                }
                MineTextures[name] = texture;
            }
        }

        MelonLogger.Msg($"[CustomNoteType] Loaded {MineTextures.Count} mine textures from {dir}");
    }

    private static Texture2D FlipTextureVertical(Texture2D tex)
    {
        var w = tex.width;
        var h = tex.height;
        var pixels = tex.GetPixels32();
        var flipped = new Color32[w * h];
        for (var y = 0; y < h; y++)
        {
            Array.Copy(pixels, y * w, flipped, (h - 1 - y) * w, w);
        }
        var result = new Texture2D(w, h, tex.format, false);
        result.SetPixels32(flipped);
        result.Apply();
        return result;
    }
}
