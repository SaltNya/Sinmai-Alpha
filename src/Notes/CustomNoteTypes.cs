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


public partial class CustomNoteTypes
{

    public static bool ShowMineHitFeedback = true;

    public static float MineVolume = 0.7f;

    public static bool ShowChartSideStatistics = false;

    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * 以下内容是为了添加新的 MA2 语法用于表示自定义的 note 类型
     * The following part is to add new MA2 command to Sinmai (representing custom note types)
     *
     * New note types:
     *     1. Slide Super-new Super-hot (NMSSS, BRSSS):
     *         Definition: ??SSS [bar] [grid] [start pos] [wait] [duration] [end pos] [slide code (string)]
     *         Optional extra field: [speed multiplier], e.g. x0.5 or -1
     *         Represent a slide note with highly customized path (using slide code)
     *
     * Hyper Speed (optional trailing field on any MA2 note):
     *         You can append one extra value at the end of an existing MA2 note line.
     *         If present, it is treated as that note's speed multiplier.
     *         Examples:
     *             NMTTP 1 0 5 E 0 M1 0.5
     *             NMTAP 9 96 5 x0.5
     *             NMSTR 7 0 2 -1
     *         If absent, the note uses the normal/default speed.
     *         This is an experimental per-note speed override.
     *
     * 待办（含历史 TODO）统一见同目录 TODO.md：
     *   - Mine notes ✅（已完成，见 MINE_NOTES.md）
     *   - Individual tracing duration in conn. slides
     *   - Touch-slides / slides not ending in group A
     *   - Non-C TouchHold（B 已建池，C 绝赞缺池）
     *   - Spinning tailless star (1$$) ✅ SH1 display metadata; native judgment
     *   - D 区 / Touch Slide / rp-rq（对照 MajdataViewAlpha 功能差距）
     */
    // Validation patches can still be installed when the main patch fails.
    // Keep native records valid until OnAfterPatch extends the record table.
    public static int TotalMa2RecordCount = (int)Ma2fileRecordID.Def.End;
    public static int LastMa2RecordID = (int)Ma2fileRecordID.Def.End - 1;
    public static Array Ma2FileRecordData;

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

    // Custom note kind classification.
    public enum CustomNoteKind
    {
        None,
        Mine,
        TouchBreak,
        MineTouchBreak,
        TouchStar,
        TouchBreakStar,
        MineTouchStar,
    }

    private static Dictionary<int, CustomNoteKind> NoteKinds => RuntimeCharts.Current.NoteKinds;

    // 当前谱面 NoteData 列表缓存（loadMa2Main postfix 填充；地雷 hold 触碰检测用——
    // 判定同轨道普通 tap 是否在判定窗口内，让点击优先判定给 tap）。
    private static List<Manager.NoteData> _activeNoteList { get => RuntimeCharts.Current._activeNoteList; set => RuntimeCharts.Current._activeNoteList = value; }

    // 地雷 Note 的独立对象池：每种 note 类型一份（以 object 存放 List<T>），按 GameCtrl 区分。
    // 池里的对象在创建时就换好地雷贴图，和普通 Note 池互不污染。
    private static readonly Dictionary<GameCtrl, Dictionary<string, object>> MinePools = new Dictionary<GameCtrl, Dictionary<string, object>>();

    // 当前 RegistNote 调用需要从地雷池取对象的字段名（每种 note 类型对应一或两个池字段）。
    private static readonly HashSet<string> _activeMineFields = new HashSet<string>();

    // 绝赞 touch（BRTTP，kind=TouchBreak）的独立池与当前激活字段。
    // 绝赞不是地雷，但同样需要独立池（组件是 TouchBreakNoteB，贴图/判定由类自己负责）。
    private static readonly Dictionary<GameCtrl, Dictionary<string, object>> TouchBreakPools = new Dictionary<GameCtrl, Dictionary<string, object>>();
    private static readonly HashSet<string> _activeTouchBreakFields = new HashSet<string>();

    // TouchStar（NMSTP/BRSTP，kind=TouchStar/TouchBreakStar）的独立池与当前激活字段。
    // 逻辑与 touch 完全一致，仅贴图不同（touch_star 五瓣 / touch_star_break / touch_hit_star）。
    private static readonly Dictionary<GameCtrl, Dictionary<string, object>> TouchStarPools = new Dictionary<GameCtrl, Dictionary<string, object>>();
    private static readonly HashSet<string> _activeTouchStarFields = new HashSet<string>();

    // 地雷 TouchStar（MNSTP，kind=MineTouchStar）的独立池与当前激活字段。
    // 五瓣星结构同 TouchStar，贴图 touch_star_mine；判定同地雷 touch（命中反转）。
    private static readonly Dictionary<GameCtrl, Dictionary<string, object>> MineTouchStarPools = new Dictionary<GameCtrl, Dictionary<string, object>>();
    private static readonly HashSet<string> _activeMineTouchStarFields = new HashSet<string>();

    // RegistNote 中每种 note 类型会读取的池字段。
    private static readonly string[] MinePoolFieldNames =
    [
        "_tapObjectList", "_holdObjectList", "_breakHoldObjectList",
        "_starObjectList", "_breakStarObjectList", "_breakObjectList",
        "_touchBObjectList", "_touchCTapObjectList",
        "_touchBHoldObjectList", "_touchCHoldObjectList",
        "_slideObjectList", "_fanSlideObjectList",
        // slide 轨道箭头（RegistNote slide 分支里 SetArrowObject 时读取）
        "_arrowObjectList", "_breakArrowObjectList",
    ];

    // 池字段名 -> 对应类型的访问器（transpiler 用）。
    private static readonly Dictionary<string, MethodInfo> MinePoolGetters = new(StringComparer.Ordinal)
    {
        ["_tapObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetTapObjectList)),
        ["_holdObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetHoldObjectList)),
        ["_breakHoldObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetBreakHoldObjectList)),
        ["_starObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetStarObjectList)),
        ["_breakStarObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetBreakStarObjectList)),
        ["_breakObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetBreakObjectList)),
        ["_touchBObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetTouchBObjectList)),
        ["_touchCTapObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetTouchCTapObjectList)),
        ["_touchBHoldObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetTouchBHoldObjectList)),
        ["_touchCHoldObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetTouchCHoldObjectList)),
        ["_slideObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetSlideObjectList)),
        ["_fanSlideObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetFanSlideObjectList)),
        ["_arrowObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetArrowObjectList)),
        ["_breakArrowObjectList"] = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetBreakArrowObjectList)),
    };

    private static bool _pendingMineForCurrentLoad { get => RuntimeCharts.Current._pendingMineForCurrentLoad; set => RuntimeCharts.Current._pendingMineForCurrentLoad = value; }
    private static bool _pendingTouchBreakForCurrentLoad { get => RuntimeCharts.Current._pendingTouchBreakForCurrentLoad; set => RuntimeCharts.Current._pendingTouchBreakForCurrentLoad = value; }
    private static bool _pendingTouchStarForCurrentLoad { get => RuntimeCharts.Current._pendingTouchStarForCurrentLoad; set => RuntimeCharts.Current._pendingTouchStarForCurrentLoad = value; }

    // findID is called in a batch before loadNote, so use queues to keep mine flags in chart order.
    private static Queue<bool> PendingMineFlags => RuntimeCharts.Current.PendingMineFlags;
    private static Queue<bool> PendingTouchBreakFlags => RuntimeCharts.Current.PendingTouchBreakFlags;
    private static Queue<bool> PendingTouchStarFlags => RuntimeCharts.Current.PendingTouchStarFlags;
    private static readonly HashSet<string> AllNoteTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Mine note textures loaded from Sinmai-Alpha/CustomNoteTypes.
    private static readonly Dictionary<string, Texture2D> MineTextures = new Dictionary<string, Texture2D>();
    private static bool _mineTexturesLoaded;



    private static readonly Dictionary<string, string> MineToBaseMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MNTAP"] = "NMTAP",
        ["MNHLD"] = "NMHLD",
        ["MNSTR"] = "NMSTR",
        ["MNSSS"] = "NMSSS",
        ["MNTTP"] = "NMTTP",
        ["MNTHO"] = "NMTHO",
        // Mine variants for native slide types.
        ["MNSCR"] = "NMSCR",
        ["MNSCL"] = "NMSCL",
        ["MNSI_"] = "NMSI_",
        ["MNSLL"] = "NMSLL",
        ["MNSLR"] = "NMSLR",
        ["MNSUL"] = "NMSUL",
        ["MNSUR"] = "NMSUR",
        ["MNSXL"] = "NMSXL",
        ["MNSXR"] = "NMSXR",
        ["MNSF_"] = "NMSF_",
        ["MNSV_"] = "NMSV_",
        ["MNSSL"] = "NMSSL",
        ["MNSSR"] = "NMSSR",
        ["MNSL_"] = "NMSL_",
        ["MNSR_"] = "NMSR_",
        ["MNSU_"] = "NMSU_",
        ["MNSD_"] = "NMSD_",
        ["MNSNL"] = "NMSNL",
        ["MNSNR"] = "NMSNR",
        ["MNSFL"] = "NMSFL",
        ["MNSFR"] = "NMSFR",

        // Mine Break variants.
        ["MBTAP"] = "BRTAP",
        ["MBHLD"] = "BRHLD",
        ["MBSTR"] = "BRSTR",
        ["MBSSS"] = "BRSSS",
        ["MBSCR"] = "BRSCR",
        ["MBSCL"] = "BRSCL",
        ["MBSI_"] = "BRSI_",
        ["MBSLL"] = "BRSLL",
        ["MBSLR"] = "BRSLR",
        ["MBSUL"] = "BRSUL",
        ["MBSUR"] = "BRSUR",
        ["MBSXL"] = "BRSXL",
        ["MBSXR"] = "BRSXR",
        ["MBSF_"] = "BRSF_",
        ["MBSV_"] = "BRSV_",
        ["MBSSL"] = "BRSSL",
        ["MBSSR"] = "BRSSR",
        ["MBSL_"] = "BRSL_",
        ["MBSR_"] = "BRSR_",
        ["MBSU_"] = "BRSU_",
        ["MBSD_"] = "BRSD_",
        ["MBSNL"] = "BRSNL",
        ["MBSNR"] = "BRSNR",
        ["MBSFL"] = "BRSFL",
        ["MBSFR"] = "BRSFR",

        // Mine ExBreak variants.
        ["MZTAP"] = "BXTAP",
        ["MZHLD"] = "BXHLD",
        ["MZSTR"] = "BXSTR",
        ["MZSCR"] = "BXSCR",
        ["MZSCL"] = "BXSCL",
        ["MZSI_"] = "BXSI_",
        ["MZSLL"] = "BXSLL",
        ["MZSLR"] = "BXSLR",
        ["MZSUL"] = "BXSUL",
        ["MZSUR"] = "BXSUR",
        ["MZSXL"] = "BXSXL",
        ["MZSXR"] = "BXSXR",
        ["MZSF_"] = "BXSF_",
        ["MZSV_"] = "BXSV_",
        ["MZSSL"] = "BXSSL",
        ["MZSSR"] = "BXSSR",
        ["MZSL_"] = "BXSL_",
        ["MZSR_"] = "BXSR_",
        ["MZSU_"] = "BXSU_",
        ["MZSD_"] = "BXSD_",
        ["MZSNL"] = "BXSNL",
        ["MZSNR"] = "BXSNR",
        ["MZSFL"] = "BXSFL",
        ["MZSFR"] = "BXSFR",

        // Mine Ex variants.
        ["MXTAP"] = "EXTAP",
        ["MXHLD"] = "EXHLD",
        ["MXSTR"] = "EXSTR",
    };

    // Expected total number of string fields (including the record type) for known MA2 note records.
    // If a line has exactly one more field than expected, that trailing field is treated as speed.
    private static readonly Dictionary<string, int> KnownMa2FieldCounts = new(StringComparer.OrdinalIgnoreCase)
    {
        // Taps
        ["NMTAP"] = 4,
        ["BRTAP"] = 4,
        ["EXTAP"] = 4,
        ["BXTAP"] = 4,
        // Holds
        ["NMHLD"] = 5,
        ["BRHLD"] = 5,
        ["EXHLD"] = 5,
        ["BXHLD"] = 5,
        // Touch
        ["NMTTP"] = 7,
        ["BRTTP"] = 7,
        ["EXTTP"] = 7,
        ["BXTTP"] = 7,
        ["NMSTP"] = 7,
        ["BRSTP"] = 7,
        // Stars
        ["NMSTR"] = 4,
        ["BRSTR"] = 4,
        ["EXSTR"] = 4,
        ["BXSTR"] = 4,
        // Slides seen in standard MA2 charts
        ["NMSI_"] = 7,
        ["NMSL_"] = 7,
        ["NMSR_"] = 7,
        ["NMSU_"] = 7,
        ["NMSD_"] = 7,
        ["NMSCL"] = 7,
        ["NMSCR"] = 7,
        ["NMSXL"] = 7,
        ["NMSXR"] = 7,
        ["NMSUL"] = 7,
        ["NMSUR"] = 7,
        ["NMSLL"] = 7,
        ["NMSLR"] = 7,
        ["NMSNL"] = 7,
        ["NMSNR"] = 7,
        ["NMSFL"] = 7,
        ["NMSFR"] = 7,
        ["NMSF_"] = 7,
        // Custom slide types from this module
        ["NMSSS"] = 8,
        ["BRSSS"] = 8,
    };

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

    private static bool TryRedirectMineRecord(MA2Record rec)
    {
        var name = rec.getType().getEnumName();
        if (!MineToBaseMap.TryGetValue(name, out var baseName)) return false;

        var baseId = Ma2fileRecordID.findID(baseName);
        if (baseId == Ma2fileRecordID.Def.Invalid) return false;

        var traverse = Traverse.Create(rec);
        foreach (var fieldName in new[] { "type", "_type", "m_type", "Type" })
        {
            var field = traverse.Field(fieldName);
            if (field.FieldExists())
            {
                field.SetValue(baseId);
                return true;
            }
        }

        return false;
    }

    private static bool TryRedirectSpecialRecord(MA2Record rec)
    {
        var name = rec.getType().getEnumName();
        string baseName = null;
        var isMine = false;
        var isTouchBreak = false;
        var isTouchStar = false;

        if (MineToBaseMap.TryGetValue(name, out baseName))
        {
            isMine = true;
        }
        else if (name == "BRTTP")
        {
            // 绝赞 touch：判定同普通 touch，但命中强制 Critical Perfect（非地雷）。
            baseName = "NMTTP";
            isTouchBreak = true;
        }
        else if (name == "BRTHO")
        {
            // 绝赞 touchhold（非地雷）：命中强制 Critical Perfect。
            baseName = "NMTHO";
            isTouchBreak = true;
        }
        else if (name == "MBTTP")
        {
            // 地雷绝赞 touch。
            baseName = "NMTTP";
            isMine = true;
            isTouchBreak = true;
        }
        else if (name == "MBTHO")
        {
            // 地雷绝赞 touchhold（素材 touchhold_break_0..3 + touchhold_break）。
            baseName = "NMTHO";
            isMine = true;
            isTouchBreak = true;
        }
        else if (name == "NMSTP")
        {
            // TouchStar：逻辑同普通 touch，贴图五瓣星（touch_star / touch_hit_star）。
            baseName = "NMTTP";
            isTouchStar = true;
        }
        else if (name == "BRSTP")
        {
            // Break TouchStar：逻辑同绝赞 touch（命中强制 CP），贴图 touch_star_break。
            baseName = "NMTTP";
            isTouchBreak = true;
            isTouchStar = true;
        }
        else if (name == "MNSTP")
        {
            // 地雷 TouchStar：逻辑同地雷 touch（命中反转成 Miss），贴图 touch_star_mine 五瓣星。
            baseName = "NMTTP";
            isMine = true;
            isTouchStar = true;
        }
        else
        {
            return false;
        }

        var baseId = Ma2fileRecordID.findID(baseName);
        if (baseId == Ma2fileRecordID.Def.Invalid)
        {
            MelonLogger.Error($"[CustomNoteType] Redirect failed: cannot find base type {baseName} for {name}");
            return false;
        }

        var traverse = Traverse.Create(rec);
        foreach (var fieldName in new[] { "type", "_type", "m_type", "Type" })
        {
            var field = traverse.Field(fieldName);
            if (field.FieldExists())
            {
                field.SetValue(baseId);
                _pendingMineForCurrentLoad = isMine;
                _pendingTouchBreakForCurrentLoad = isTouchBreak;
                _pendingTouchStarForCurrentLoad = isTouchStar;
                return true;
            }
        }

        MelonLogger.Error($"[CustomNoteType] Redirect failed: cannot find type field on MA2Record for {name}");
        return false;
    }



    [HarmonyPrefix]
    [HarmonyPatch(typeof(Ma2fileRecordID), "findID")]
    public static bool FindIDPrefix(string enumName, ref Ma2fileRecordID.Def __result)
    {
        if (!FeaturesEnabled()) { __result = ChartFeatureGate.FindNativeRecord(enumName); return false; }

        // NOTE: Do NOT reset _pendingMineForCurrentLoad / _pendingTouchBreakForCurrentLoad here.
        // findID can be called multiple times before loadNote; resetting here would lose the flag.

        string baseName = null;
        var isMine = false;
        var isTouchBreak = false;
        var isTouchStar = false;

        if (MineToBaseMap.TryGetValue(enumName, out baseName))
        {
            isMine = true;
        }
        else if (enumName == "BRTTP")
        {
            // 绝赞 touch（非地雷，命中强制 CP）。
            baseName = "NMTTP";
            isTouchBreak = true;
        }
        else if (enumName == "BRTHO")
        {
            // 绝赞 touchhold（非地雷，命中强制 CP）。
            baseName = "NMTHO";
            isTouchBreak = true;
        }
        else if (enumName == "MBTTP")
        {
            // 地雷绝赞 touch。
            baseName = "NMTTP";
            isMine = true;
            isTouchBreak = true;
        }
        else if (enumName == "MBTHO")
        {
            // 地雷绝赞 touchhold。
            baseName = "NMTHO";
            isMine = true;
            isTouchBreak = true;
        }
        else if (enumName == "NMSTP")
        {
            // TouchStar：逻辑同普通 touch，贴图五瓣星（touch_star / touch_hit_star）。
            baseName = "NMTTP";
            isTouchStar = true;
        }
        else if (enumName == "BRSTP")
        {
            // Break TouchStar：逻辑同绝赞 touch（命中强制 CP），贴图 touch_star_break。
            baseName = "NMTTP";
            isTouchBreak = true;
            isTouchStar = true;
        }
        else if (enumName == "MNSTP")
        {
            // 地雷 TouchStar：逻辑同地雷 touch（命中反转成 Miss），贴图 touch_star_mine 五瓣星。
            baseName = "NMTTP";
            isMine = true;
            isTouchStar = true;
        }

        if (baseName != null)
        {
            for (var i = 0; i < TotalMa2RecordCount; i++)
            {
                var item = Ma2FileRecordData.GetValue(i);
                if (Traverse.Create(item).Field<string>("enumName").Value == baseName)
                {
                    __result = (Ma2fileRecordID.Def)i;
                    if (AllNoteTypeNames.Contains(enumName))
                    {
                        PendingMineFlags.Enqueue(isMine);
                        PendingTouchBreakFlags.Enqueue(isTouchBreak);
                        PendingTouchStarFlags.Enqueue(isTouchStar);
                    }
                    return false;
                }
            }

            MelonLogger.Error($"[CustomNoteType] findID cannot find base type {baseName} for {enumName}");
            return false;
        }

        // Normal lookup (including custom slide types like NMSSS).
        __result = Ma2fileRecordID.Def.Invalid;
        for (var i = 0; i < TotalMa2RecordCount; i++)
        {
            var item = Ma2FileRecordData.GetValue(i);
            if (Traverse.Create(item).Field<string>("enumName").Value == enumName)
            {
                __result = (Ma2fileRecordID.Def)i;
            }
        }

        if (AllNoteTypeNames.Contains(enumName))
        {
            PendingMineFlags.Enqueue(false);
            PendingTouchBreakFlags.Enqueue(false);
            PendingTouchStarFlags.Enqueue(false);
        }

        return false;
    }

    [HarmonyPatch]
    public static class Ma2RecordValidation
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                // AccessTools.Method(typeof(Ma2fileRecordID), "findID"),
                AccessTools.Method(typeof(Ma2fileRecordID), "clamp"),
                AccessTools.Method(typeof(Ma2fileRecordID), "getClampValue"),
                AccessTools.Method(typeof(Ma2fileRecordID), "isValid"),
                AccessTools.Method(typeof(Ma2fileRecordID_Extension), "isValid"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // Registration is complete before these patches are installed. Keep
            // validation a leaf operation on old cabinet Mono; a cross-assembly
            // static field load here can invalidate optimized native getter calls.
            // Mutate instructions so branch labels and exception blocks survive.
            var nativeCount = (int)Ma2fileRecordID.Def.End;
            foreach (var inst in instructions)
            {
                if (inst.LoadsConstant(nativeCount))
                { inst.opcode = OpCodes.Ldc_I4; inst.operand = TotalMa2RecordCount; }
                else if (inst.LoadsConstant(nativeCount - 1))
                { inst.opcode = OpCodes.Ldc_I4; inst.operand = LastMa2RecordID; }
                yield return inst;
            }
        }
    }

    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * 以下内容是给新的 MA2 语法写解析器
     */

    /*
     * 给新建的 noteData 初始化应有的数据, 仅仅是照搬了 NotesReader.loadNote
     */
    public static void PrepareBasicNoteData(NoteData noteData, NotesReader reader,
        MA2Record record, int index, ref int noteIndex, OptionMirrorID mirrorMode)
    {
        noteData.type = new NotesTypeID(record.getType().getNotesTypeId());
        noteData.time.init(record.getBar(), record.getGrid(), reader);
        noteData.end = noteData.time;
        // MirrorInfo[mode, pos] 索引守卫：手写/第三方谱面可能写出 -1 或 ≥17 的 start（如
        // C 区起点 NMSSS 的 start=-1，旧版转换器会产出），越界会 IndexOutOfRange 崩溃（2026-08-25）。
        // 边界用 GetLength(1) 动态读取，镜像表尺寸变化时守卫自动跟随。
        var mirror = (int)mirrorMode is >= 0 and <= 3 ? (int)mirrorMode : 0;
        var pos = record.getPos();
        if (pos < 0 || pos >= MaiGeometry.MirrorInfo.GetLength(1))
        {
            MelonLogger.Error($"[CustomNoteType] MirrorInfo start OOB: pos={pos} mode={mirrorMode} tag={record.getType().getEnumName()} bar={record.getBar()} grid={record.getGrid()} (clamped to 0)");
            pos = 0;
        }
        noteData.startButtonPos = MaiGeometry.MirrorInfo[mirror, pos];
        noteData.index = index;
        var num = record.getGrid() % 96;
        if (num == 0)
        {
            noteData.beatType = NoteData.BeatType.BeatType04;
        }
        else if (num % 48 == 0)
        {
            noteData.beatType = NoteData.BeatType.BeatType08;
        }
        else if (num % 24 == 0)
        {
            noteData.beatType = NoteData.BeatType.BeatType16;
        }
        else if (num % 16 == 0)
        {
            noteData.beatType = NoteData.BeatType.BeatType24;
        }
        else
        {
            noteData.beatType = NoteData.BeatType.BeatTypeOther;
        }
        noteData.indexNote = noteIndex;
        ++noteIndex;
    }

    /*
     * 给新建的 noteData 填入基本的 slide 相关数据, 仅仅是照搬了 NotesReader.loadNote
     */
    public static void PrepareBasicSlideData(NoteData noteData, NotesReader reader, MA2Record record, int noteIndex,
        ref int slideIndex, OptionMirrorID mirrorMode)
    {
        noteData.indexSlide = slideIndex++;
        var slideData = noteData.slideData;
        var slideWaitLen = record.getSlideWaitLen();
        var slideShootLen = record.getSlideShootLen();
        // MirrorInfo[mode, endPos] 索引守卫，同上（NMSSS end 字段越界防崩）。
        var mirror = (int)mirrorMode is >= 0 and <= 3 ? (int)mirrorMode : 0;
        var endPos = record.getSlideEndPos();
        if (endPos < 0 || endPos >= MaiGeometry.MirrorInfo.GetLength(1))
        {
            MelonLogger.Error($"[CustomNoteType] MirrorInfo end OOB: endPos={endPos} mode={mirrorMode} tag={record.getType().getEnumName()} bar={record.getBar()} grid={record.getGrid()} (clamped to 0)");
            endPos = 0;
        }
        slideData.targetNote = MaiGeometry.MirrorInfo[mirror, endPos];
        slideData.shoot.time.init(record.getBar(), record.getGrid() + slideWaitLen, reader);
        slideData.shoot.index = noteIndex;
        slideData.arrive.time.init(record.getBar(), record.getGrid() + slideWaitLen + slideShootLen, reader);
        slideData.arrive.index = noteIndex;
        noteData.end = slideData.arrive.time;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NotesReader), "loadNote")]
    public static bool LoadCustomNote(NotesReader __instance, ref bool __result, NotesData ____note, int ____playerID,
        MA2Record rec, int index, ref int noteIndex, ref int slideIndex)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        _noteCountBeforeLoad = ____note?._noteData?.Count ?? 0;
        _optionalSpeedForCurrentLoad = null;
        _removedOptionalSpeedString = null;
        _optionalSpeedRemoved = false;
        _optionalStreamIdForCurrentLoad = null;
        _removedOptionalStreamString = null;
        _optionalStreamRemoved = false;

        // Note: mine/critical flags are set in FindIDPrefix before loadNote is called.
        // Do NOT reset them here, otherwise the mine marking would be lost.

        // 流类型键 s{N}（行尾字段，AquaMai ma2 扩展语法）：先于速度字段解析。
        // 命中时对内置记录临时移除该字段，避免原生 MA2 解析器多字段错位。
        if (TryPeekStreamId(rec, out var streamId))
        {
            _optionalStreamIdForCurrentLoad = streamId;
            // Custom SSS/STP records also need the trailer removed, otherwise
            // it masks FK/DZ/VS metadata. Restore it after all native/custom reads.
            _removedOptionalStreamString = rec.getStr((uint)(rec._str.Count - 1));
            rec._str.RemoveAt(rec._str.Count - 1);
            _optionalStreamRemoved = true;
        }

        ReadVisualMarker(rec);
        ReadFakeMarker(rec);
        ReadNoteSkinMarker(rec);
        ReadBorrowedMarker(rec);
        ReadTouchRadiusMarker(rec);
        ReadStarHeadMarker(rec);
        ReadFireworkMarker(rec);
        ReadDZoneMarker(rec);
        if (TryPeekSpeedValue(rec, out var optionalSpeed) || TryGetOptionalSpeed(rec, out optionalSpeed))
        {
            _optionalSpeedForCurrentLoad = optionalSpeed;

            // For built-in records, temporarily remove the extra speed field so the original
            // MA2 parser still sees the normal number of fields.
            if (rec.getType() < Ma2fileRecordID.Def.End)
            {
                _removedOptionalSpeedString = rec.getStr((uint)(rec._str.Count - 1));
                rec._str.RemoveAt(rec._str.Count - 1);
                _optionalSpeedRemoved = true;
            }
        }

        if (rec.getType() < Ma2fileRecordID.Def.End)
        {
            // builtin record type
            return true;
        }

        var flag = true;
        switch (rec.getType().getEnumName())
        {
            case "NMSSS":
            case "BRSSS":
                var noteData = new CustomSlideNoteData();
                var mirrorMode = Singleton<GamePlayManager>.Instance.GetGameScore(____playerID).UserOption.MirrorMode;
                PrepareBasicNoteData(noteData, __instance, rec, index, ref noteIndex, mirrorMode);
                PrepareBasicSlideData(noteData, __instance, rec, noteIndex, ref slideIndex, mirrorMode);
                var success = noteData.ParseSlideCode(rec.getStr(7), mirrorMode);
                if (success)
                {
                    ____note._noteData.Add(noteData);
                    _currentNoteData = noteData;

                    // Optional per-note speed on custom slides:
                    // NMSSS/... [slide code] [speed], e.g. ... "x0.5" or "-1"
                    if (rec._str.Count > 8 && TryParseSpeedMultiplier(rec.getStr(8), out var noteSpeed))
                    {
                        NoteSpeedMultipliers[noteData] = noteSpeed;
                        NoteSpeedByIndex[noteData.indexNote] = noteSpeed;
                        TrySetNoteSpeedField(noteData, noteSpeed);
                    }
                }
                else
                {
                    flag = false;
                }
                break;
            default:
                flag = false;
                break;
        }
        __result = flag;
        return false;
    }

    private static bool TryParseSpeedMultiplier(string text, out float speed)
    {
        speed = 1f;
        if (string.IsNullOrEmpty(text)) return false;

        text = text.Trim();
        if (text.StartsWith("x") || text.StartsWith("X"))
        {
            text = text.Substring(1);
        }

        return float.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out speed);
    }

    /// <summary>解析行尾流类型键 s{N}（s1/s2/...，AquaMai ma2 扩展语法：可重叠流局部曲线）。
    /// 仅 s+纯数字 匹配；s 单独、slide 等不会误判。</summary>
    private static bool TryPeekStreamId(MA2Record rec, out string streamId)
    {
        streamId = null;
        if (rec._str.Count == 0) return false;
        var last = rec.getStr((uint)(rec._str.Count - 1))?.Trim();
        if (string.IsNullOrEmpty(last) || last.Length < 2 || last[0] != 's') return false;
        for (var i = 1; i < last.Length; i++)
        {
            if (last[i] < '0' || last[i] > '9') return false;
        }
        streamId = last;
        return true;
    }

    private static bool TryPeekSpeedValue(MA2Record rec, out float speed)
    {
        speed = 1f;
        if (rec._str.Count == 0) return false;
        var last = rec.getStr((uint)(rec._str.Count - 1))?.Trim();
        if (string.IsNullOrEmpty(last)) return false;
        if (!last.StartsWith("x") && !last.StartsWith("X") && !last.StartsWith("-") && !last.Contains(".")) return false;
        return TryParseSpeedMultiplier(last, out speed);
    }

    private static bool TryGetOptionalSpeed(MA2Record rec, out float speed)
    {
        speed = 1f;
        var typeName = rec.getType().getEnumName();

        if (typeName != null && KnownMa2FieldCounts.TryGetValue(typeName, out var expectedCount))
        {
            if (rec._str.Count == expectedCount + 1)
            {
                return TryParseSpeedMultiplier(rec.getStr((uint)(rec._str.Count - 1)), out speed);
            }
            // If the count doesn't match, fall through to the heuristic below.
            // This handles cases where the MA2 parser's field counting is different from our table.
        }

        // Conservative heuristic: if the last field looks like an explicit speed value
        // (x0.5, -1, 0.8, 1.2, ...), treat it as the optional speed field.
        // Normal MA2 note fields are integers, so this should not misread normal notes.
        return TryPeekSpeedValue(rec, out speed);
    }

    private static bool TrySetNoteSpeedField(NoteData note, float speed)
    {
        if (note == null) return false;

        var type = note.GetType();
        foreach (var name in new[] { "speed", "noteSpeed", "Speed", "NoteSpeed", "mSpeed" })
        {
            var field = AccessTools.Field(type, name);
            if (field != null && field.FieldType == typeof(float))
            {
                field.SetValue(note, speed);
                return true;
            }

            var property = AccessTools.Property(type, name);
            if (property != null && property.PropertyType == typeof(float) && property.CanWrite)
            {
                property.SetValue(note, speed);
                return true;
            }
        }

        return false;
    }

    private static void ApplyNoteSpeedMultiplier(ref float speed, Queue<float> pendingQueue)
    {
        if (_currentNoteData != null)
        {
            float multiplier;
            if (NoteSpeedMultipliers.TryGetValue(_currentNoteData, out multiplier) ||
                NoteSpeedByIndex.TryGetValue(_currentNoteData.indexNote, out multiplier))
            {
                multiplier = SanitizeSpeedMultiplier(multiplier);
                speed *= multiplier;
                // Keep the fallback queue in sync even when context works.
                if (pendingQueue.Count > 0) pendingQueue.Dequeue();
                return;
            }
        }

        // Fallback: if we can't correlate the current NoteData, assume GetNoteSpeed/GetTouchSpeed
        // is called once per note in the same order the notes were loaded.
        if (pendingQueue.Count > 0)
        {
            var queued = SanitizeSpeedMultiplier(pendingQueue.Dequeue());
            speed *= queued;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NotesReader), "loadNote")]
    public static void ApplyOptionalHyperSpeed(NotesReader __instance, NotesData ____note, MA2Record rec, bool __result, int ____playerID)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        // Put back the temporary removed speed field, if any.
        if (_optionalSpeedRemoved && _removedOptionalSpeedString != null)
        {
            rec._str.Add(_removedOptionalSpeedString);
            _optionalSpeedRemoved = false;
            _removedOptionalSpeedString = null;
        }

        RestoreDZoneMarker(rec);
        RestoreFireworkMarker(rec);
        RestoreStarHeadMarker(rec);
        RestoreTouchRadiusMarker(rec);
        RestoreBorrowedMarker(rec);
        RestoreNoteSkinMarker(rec);
        RestoreFakeMarker(rec);
        RestoreVisualMarker(rec);
        // Put back the temporary removed stream-id field, if any.
        if (_optionalStreamRemoved && _removedOptionalStreamString != null)
        {
            rec._str.Add(_removedOptionalStreamString);
            _optionalStreamRemoved = false;
            _removedOptionalStreamString = null;
        }

        if (!__result) return;

        var list = ____note?._noteData;
        if (list == null || list.Count <= _noteCountBeforeLoad) return;

        var note = list[list.Count - 1];
        RuntimeCharts.BindNote(note, RuntimeCharts.Current);
        ApplyDZonePosition(note, ____playerID);
        ApplyFakeMarker(note);
        ApplyBorrowedMarker(note, __instance, rec);
        ApplyTouchRadiusMarker(note);
        ApplyStarHeadMarker(note, __instance, rec);
        ApplyFireworkMarker(note, __instance, rec);
        ApplyNoteSkinMarker(note, __instance, rec);
        ApplyVisualMarker(note, _optionalStreamIdForCurrentLoad);
        _currentNoteData = note;

        // kind 完全由谱面原始类型标签决定（rec._str[0] 是行首类型，重定向只改 rec.type 不改 _str）。
        // 不再使用 findID 队列：findID 的调用次数与 loadNote 并不总是一一对应
        // （touch 链等会二次调用 findID 多入队），队列错位会把普通 touch 误标成地雷。
        var tag = rec._str.Count > 0 ? rec._str[0] : null;
        var kind = CustomNoteKind.None;
        if (tag != null && TryGetNoteKindByTag(tag, out var tagKind))
        {
            kind = tagKind;
        }

        if (kind != CustomNoteKind.None)
        {
            NoteKinds[note.indexNote] = kind;
        }

        // 流类型键（行尾 s{N} 字段）：该音符的曲线类型键 = s{N}（只吃本流曲线）。
        if (_optionalStreamIdForCurrentLoad != null)
        {
            StreamTypeByNoteIndex[note.indexNote] = _optionalStreamIdForCurrentLoad;
        }

        float speedToUse = 1f;
        if (_optionalSpeedForCurrentLoad.HasValue)
        {
            speedToUse = _optionalSpeedForCurrentLoad.Value;
            NoteSpeedMultipliers[note] = speedToUse;
            NoteSpeedByIndex[note.indexNote] = speedToUse;
            TrySetNoteSpeedField(note, speedToUse);
        }

        // Keep the fallback queues aligned with every loaded note.
        PendingNoteSpeedMultipliers.Enqueue(speedToUse);
        PendingTouchSpeedMultipliers.Enqueue(speedToUse);
        _optionalSpeedForCurrentLoad = null;
        _optionalStreamIdForCurrentLoad = null;
    }

    /// <summary>按谱面原始类型标签（rec._str[0]）判定 note 类别。
    /// 分支与 FindIDPrefix 一致：MineToBaseMap → Mine；BRTTP/BRTHO → TouchBreak；
    /// MBTTP/MBTHO → MineTouchBreak；NMSTP/BRSTP → TouchStar。</summary>
    public static bool TryGetNoteKindByTag(string tag, out CustomNoteKind kind)
    {
        kind = CustomNoteKind.None;
        if (string.IsNullOrEmpty(tag)) return false;
        if (MineToBaseMap.TryGetValue(tag, out _))
        {
            kind = CustomNoteKind.Mine;
            return true;
        }

        switch (tag)
        {
            case "BRTTP":
            case "BRTHO":
                kind = CustomNoteKind.TouchBreak;
                return true;
            case "MBTTP":
            case "MBTHO":
                kind = CustomNoteKind.MineTouchBreak;
                return true;
            case "NMSTP":
                kind = CustomNoteKind.TouchStar;
                return true;
            case "BRSTP":
                kind = CustomNoteKind.TouchBreakStar;
                return true;
            case "MNSTP":
                kind = CustomNoteKind.MineTouchStar;
                return true;
            default:
                return false;
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameManager), "GetNoteSpeed")]
    public static void GetNoteSpeedPostfix(ref float __result)
    {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }

        ApplyNoteSpeedMultiplier(ref __result, PendingNoteSpeedMultipliers);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameManager), "GetTouchSpeed")]
    public static void GetTouchSpeedPostfix(ref float __result)
    {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }

        ApplyNoteSpeedMultiplier(ref __result, PendingTouchSpeedMultipliers);
    }

    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * SV / HS（MajdataViewAlpha 风格：实时变速 + 时间轴命令段表，2026-08 改版）
     *
     * ma2 语法：
     *   SVSP\t<bar>\t<grid>\t<倍率|N:M|NULL>            全局 SV（从该时刻起生效，NULL 恢复 1.0）
     *   SVSP\t<bar>\t<grid>\ttap=2:1,hold=4:1,...       分类 SV（tap/star/hold/break/touch/touchhold/slide）
     *   HS\t<bar>\t<grid>\t<倍率|N:M|NULL>              全局 HS（时间轴命令，Majdata HS* 风格）
     *   HS\t<bar>\t<grid>\ttap=2:1,hold=4:1,...         分类 HS
     *   每音符行尾的 x0.5 等 = 内嵌 HS（优先于时间轴 HS，向后兼容）
     * 分类 NULL（Majdata 语义）：SV 的 `tap=NULL` = 该类型重新跟随全局 SV；
     *   HS 的 `tap=NULL` = 清除该类型的额外倍率（回到 1.0）。
     * 时长格式 N:M = 60/bpm×4/N×M 秒（与 Majdata TryParseCommandDuration 一致）。
     *
     * SV 语义（与 MajdataViewAlpha SvController 一致）：
     *   飞行进度 V_5 = 1 − (∫sv(T) − ∫sv(now)) / W
     *   ——实时变速：飞行中倍率切换即时生效；SV=0 段 ∫sv 冻结 → 音符视觉暂停（瞬移表演）；
     *   到达判定线时刻 = 判定时刻 T（∫sv(now)=∫sv(T) ⟺ now=T，判定锁音频）。
     *   W = D'×m_sv(T)（D' 为等效流速 DefaultMsec、m_sv(T) 为判定时刻 SV 倍率；
     *   无 SV 时退化为原版 V_5 = 1−(T−now)/D'）。
     *   相位 0/1（生成点排队/渐入）保持原版（等效流速 D' 缩放），仅飞行段实时变速。
     * HS 语义：时间轴命令 + 内嵌尾字段（优先），作用于 DefaultMsec 缩放（等效流速），
     *   与 SV 相乘——等价 Majdata speed = noteSpeed × HS × SV。
     * 激活提前量随总倍率（SV×HS）缩放（SvActivationLeadTranspiler，bug #35 教训）。
     * ========== ========== ========== ========== ========== ========== ========== ==========
     */
    // 解析期暂存（loadMa2Main 后置统一转 msec）。
    private static List<(int Bar, int Grid, string Text)> PendingSvSegments => RuntimeCharts.Current.PendingSvSegments;
    private static List<(int Bar, int Grid, string Text)> PendingHsSegments => RuntimeCharts.Current.PendingHsSegments;
    // 已转换的段表：类型键（""=全局，或 tap/star/hold/break/touch/touchhold/slide）→ (msec, 倍率)。
    private static Dictionary<string, List<(float Msec, float Mult)>> SvCurves => RuntimeCharts.Current.SvCurves;
    private static Dictionary<string, List<(float Msec, float Mult)>> HsCurves => RuntimeCharts.Current.HsCurves;
    // SV 分类 NULL 清除点（类型键 → msec 升序）：该时刻起该类型重新跟随全局 SV。
    private static Dictionary<string, List<float>> SvClearTimes => RuntimeCharts.Current.SvClearTimes;
    private static Dictionary<string, List<float>> HsClearTimes => RuntimeCharts.Current.HsClearTimes;
    // SV 积分累计表（预计算，二分查询）：类型键 → (段起点, 该段 SV 倍率, 该点累计 ∫sv)。
    // scroll = ∫sv（只积 SV，HS 并入 speed 窗口 W=d/HS——MajdataViewAlpha 模型：
    // distance = 4.8 − speed×(s(T)−s(now))，speed = noteSpeed×HS，HS 不进积分）。
    // SV=0 冻结、负 SV 反向由积分自然体现。
    private static Dictionary<string, List<(float Msec, float Mult, float Cum)>> SvCumCurves => RuntimeCharts.Current.SvCumCurves;
    // 每音符预计算：判定时刻累计 scroll、飞行窗口 W、类型、等效流速倍率（只含 HS，激活提前量/渐入用）、HS 倍率（W 计算用）。
    private static Dictionary<int, float> SvScrollPosByNoteIndex => RuntimeCharts.Current.SvScrollPosByNoteIndex;
    private static Dictionary<int, float> SvWindowByNoteIndex => RuntimeCharts.Current.SvWindowByNoteIndex;
    private static Dictionary<int, string> SvTypeByNoteIndex => RuntimeCharts.Current.SvTypeByNoteIndex;
    // 可重叠流类型键（indexNote → "s1"/"s2"...）：流内音符行尾 s{N} 字段（AquaMai ma2 扩展语法）。
    // 该音符的曲线类型键 = s{N}——只吃本流 `SVSP/HS ... s{N}=...` 类型化曲线，与主谱隔离。
    private static Dictionary<int, string> StreamTypeByNoteIndex => RuntimeCharts.Current.StreamTypeByNoteIndex;
    private static Dictionary<int, float> SpeedMultByNoteIndex => RuntimeCharts.Current.SpeedMultByNoteIndex;
    private static Dictionary<int, float> HsMultByNoteIndex => RuntimeCharts.Current.HsMultByNoteIndex;
    private static HashSet<int> ReferenceMotionByNoteIndex => RuntimeCharts.Current.ReferenceMotionByNoteIndex;
    // hold 身体尾的累计 scroll（∫sv(type, T+Len)）：HoldNote.Execute 身体进度由
    // SvHoldTailProgress 按 scroll 驱动（SV 交替段 hold 条抽搐/冻结段身体时停）。
    private static Dictionary<int, float> SvTailScrollPosByNoteIndex => RuntimeCharts.Current.SvTailScrollPosByNoteIndex;
    // per-note 飞行时间表（加载期一次算好，播放期只查本表）：
    // indexNote → [(msec, scroll, max前缀)]，覆盖 [T−NoteScrollCoverMsec, T+Len]：
    // 窗口起点采样 + 曲线事件点 + T + T+Len（去重、max 前缀，scroll = 全局累计 ∫sv 同基准）。
    // 实现"加载时一口气算完每个音符的变速演出，统一播放"——播放路径零全局曲线/
    // 类型查询，音符之间零相互影响。表覆盖 5s 含最慢玩家流速（DefaultMsec≈1s）下
    // mHs≈0.25 的下落窗口；极端 mHs<0.25 时窗口超覆盖 → 进场偏晚 = 安全退化方向。
    private static Dictionary<int, List<(float Msec, float Scroll, float Max)>> NoteScrollTableByNoteIndex => RuntimeCharts.Current.NoteScrollTableByNoteIndex;
    private const float NoteScrollCoverMsec = 10000f;
    // Each 双押伙伴映射（indexNote → eachChild 的 indexNote 列表）：流速不同的 each 对不画辅助条。
    private static Dictionary<int, List<int>> EachChildByNoteIndex => RuntimeCharts.Current.EachChildByNoteIndex;
    private static bool _loggedSvInject { get => RuntimeCharts.Current._loggedSvInject; set => RuntimeCharts.Current._loggedSvInject = value; }
    private static bool _loggedSvLeadScale { get => RuntimeCharts.Current._loggedSvLeadScale; set => RuntimeCharts.Current._loggedSvLeadScale = value; }

    // 每帧反射字段缓存（SvRealTimeVisualPostfix / BounceNoteVisualPostfix 每帧每 note 调用，
    // Traverse 字符串查找在变速段 note 密集时会造成卡顿）。
    private static readonly FieldInfo FNoteIndex = AccessTools.Field(typeof(NoteBase), "NoteIndex");
    private static readonly FieldInfo FAppearMsec = AccessTools.Field(typeof(NoteBase), "AppearMsec");
    private static readonly FieldInfo FDefaultMsec = AccessTools.Field(typeof(NoteBase), "DefaultMsec");
    private static readonly FieldInfo FStartPos = AccessTools.Field(typeof(NoteBase), "StartPos");
    private static readonly FieldInfo FEndPos = AccessTools.Field(typeof(NoteBase), "EndPos");
    private static readonly FieldInfo FNoteGuideTrans = AccessTools.Field(typeof(NoteBase), "NoteGuideTrans");
    private static readonly FieldInfo FTailMsec = AccessTools.Field(typeof(NoteBase), "TailMsec");
    private static readonly MethodInfo FMaiBugAdjust = AccessTools.Method(typeof(NoteBase), "GetMaiBugAdjustMSec");

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NotesRecord), "addRecord", new[] { typeof(string) })]
    public static void AddRecordSvParsePrefix(string str)
    {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }

        // 拦截每行 ma2 原文，解析 SVSP/HS 行（原方法参数名是 str，Harmony 按名匹配）。
        if (string.IsNullOrEmpty(str)) return;
        try
        {
            if (str.StartsWith("SVSP", StringComparison.OrdinalIgnoreCase))
            {
                var parts = str.Split('\t');
                if (parts.Length < 4) return;
                if (!int.TryParse(parts[1].Trim(), out var bar)) return;
                if (!int.TryParse(parts[2].Trim(), out var grid)) return;
                PendingSvSegments.Add((bar, grid, parts[3].Trim()));
                return;
            }

            if (str.StartsWith("HS", StringComparison.OrdinalIgnoreCase))
            {
                var parts = str.Split('\t');
                if (parts.Length < 4) return;
                if (!int.TryParse(parts[1].Trim(), out var bar)) return;
                if (!int.TryParse(parts[2].Trim(), out var grid)) return;
                PendingHsSegments.Add((bar, grid, parts[3].Trim()));
            }
        }
        catch
        {
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NotesReader), "loadMa2")]
    public static void LoadMa2SvResetPrefix()
    {
        ResetRingCommands();
        PendingSvSegments.Clear();
        PendingHsSegments.Clear();
        SvCurves.Clear();
        HsCurves.Clear();
        SvClearTimes.Clear();
        HsClearTimes.Clear();
        SvCumCurves.Clear();
        SvScrollPosByNoteIndex.Clear();
        SvWindowByNoteIndex.Clear();
        SvTypeByNoteIndex.Clear();
        IgnoreSvByNoteIndex.Clear();
        StreamTypeByNoteIndex.Clear();
        NoteScrollTableByNoteIndex.Clear();
        SpeedMultByNoteIndex.Clear();
        HsMultByNoteIndex.Clear();
        ReferenceMotionByNoteIndex.Clear();
        SvTailScrollPosByNoteIndex.Clear();
        _loggedSvInject = false;
        _loggedSvLeadScale = false;
    }

    /// <summary>解析倍率/时长文本：数字、N:M（按 bpm 换算）、NULL/FALSE=1。</summary>
    private static float ParseSpeedValue(string text, float bpm)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return 1f;
        if (text.Equals("NULL", StringComparison.OrdinalIgnoreCase)
            || text.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return 1f;
        if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value) && !float.IsNaN(value) && !float.IsInfinity(value))
            return value;
        var parts = text.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var division)
            && int.TryParse(parts[1], out var count)
            && division > 0 && count >= 0 && bpm > 0f)
            return 60f / bpm * 4f / division * count;
        return 1f;
    }

    private static bool IsKnownSpeedType(string key)
    {
        return key == "tap" || key == "star" || key == "hold" || key == "break"
            || key == "touch" || key == "touchhold" || key == "slide" || key == "mine" || key == "each"
            || IsStreamType(key); // 流类型键 s1/s2/...（可重叠音符流局部曲线）
    }

    /// <summary>流类型键判定：s 开头 + 全部数字（s1、s2、...）。流内音符的曲线类型键，
    /// 只吃本流曲线；无本流曲线时按原速处理（绝不跟随全局/普通类型曲线）。</summary>
    private static bool IsStreamType(string type)
    {
        if (string.IsNullOrEmpty(type) || type.Length < 2 || type[0] != 's') return false;
        for (var i = 1; i < type.Length; i++)
        {
            if (type[i] < '0' || type[i] > '9') return false;
        }
        return true;
    }

    private static void AddCurveEntry(Dictionary<string, List<(float Msec, float Mult)>> curves, string key, float msec, float mult)
    {
        if (!curves.TryGetValue(key, out var list))
        {
            list = new List<(float, float)>();
            curves[key] = list;
        }

        list.Add((msec, mult));
    }

    /// <summary>把解析期 (bar, grid, 文本) 段转成 (msec, 倍率) 分表（全局 "" 键 + 分类键），
    /// 每曲线按 msec 排序、同刻去重（保留后者）。
    /// clearTimes 非空（SV）时：分类 NULL/FALSE 记入清除点（该类型回退全局），不进曲线；
    /// clearTimes 为空（HS）时：分类 NULL 按 1.0 进曲线（Majdata：清除额外倍率）。</summary>
    private static void BuildSpeedCurves(NotesReader reader,
        List<(int Bar, int Grid, string Text)> pending,
        Dictionary<string, List<(float Msec, float Mult)>> curves,
        Dictionary<string, List<float>> clearTimes = null)
    {
        foreach (var seg in pending)
        {
            var time = new NotesTime();
            time.init(seg.Bar, seg.Grid, reader);
            var msec = time.msec;
            var bpm = reader.GetBPM_Time(msec);
            var text = seg.Text;
            if (text.Contains('='))
            {
                foreach (var pair in text.Split(','))
                {
                    var kv = pair.Split(new[] { '=' }, 2);
                    if (kv.Length != 2) continue;
                    var key = kv[0].Trim().ToLowerInvariant();
                    if (!IsKnownSpeedType(key)) continue;
                    var value = kv[1].Trim();
                    var isNull = value.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                        || value.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                    // At the same time the last authored state wins, including
                    // NULL followed by a value. Don't give NULL unconditional priority.
                    if (curves.TryGetValue(key, out var previous)) previous.RemoveAll(p => Math.Abs(p.Msec - msec) < .0001f);
                    if (clearTimes != null && clearTimes.TryGetValue(key, out var previousClears)) previousClears.RemoveAll(t => Math.Abs(t - msec) < .0001f);
                    if (isNull && clearTimes != null)
                    {
                        if (!clearTimes.TryGetValue(key, out var clearList))
                            clearTimes[key] = clearList = new List<float>();
                        clearList.Add(msec);
                    }
                    else
                    {
                        AddCurveEntry(curves, key, msec, isNull ? 1f : ParseSpeedValue(value, bpm));
                    }
                }
            }
            else
            {
                AddCurveEntry(curves, "", msec, ParseSpeedValue(text, bpm));
            }
        }

        foreach (var list in curves.Values)
        {
            var ordered = list.OrderBy(p => p.Msec).ToList();
            list.Clear(); list.AddRange(ordered);
            for (var i = list.Count - 1; i > 0; i--)
            {
                if (System.Math.Abs(list[i].Msec - list[i - 1].Msec) < 0.5f)
                {
                    list.RemoveAt(i - 1);
                }
            }
        }

        if (clearTimes != null)
        {
            foreach (var list in clearTimes.Values)
                list.Sort();
        }
    }

    /// <summary>音符类型 → Majdata 风格类型键（tap/star/hold/break/touch/touchhold/slide）。</summary>
    private static string ResolveSpeedType(NoteData note)
    {
        if (note == null) return "";
        var t = note.type;
        if (t.isTouch())
        {
            return t.isHold() ? "touchhold" : "touch";
        }

        if (t.isHold()) return "hold";
        if (t.isStar()) return "star";
        if (t.isSlide() || t.isAllSlide()) return "slide";
        return "tap";
    }

    /// <summary>全局曲线在 msec 时刻的倍率（无全局曲线 = 1）。</summary>
    private static float GetGlobalMultAt(Dictionary<string, List<(float Msec, float Mult)>> curves, float msec)
    {
        if (!curves.TryGetValue("", out var curve)) return 1f;
        var m = 1f;
        for (var i = 0; i < curve.Count; i++)
        {
            if (msec >= curve[i].Msec) m = curve[i].Mult;
            else break;
        }

        return m;
    }

    /// <summary>查询 msec 时刻生效的倍率（有效曲线：类型曲线优先、否则全局；
    /// SV 类型被 NULL 清除后回退全局曲线）。</summary>
    private static float GetCurveMultAt(Dictionary<string, List<(float Msec, float Mult)>> curves,
        Dictionary<string, List<float>> clears, string type, float msec)
    {
        if (!string.IsNullOrEmpty(type) && curves.ContainsKey(type))
        {
            var lastClear = -1f;
            if (clears != null && clears.TryGetValue(type, out var clearList))
            {
                for (var i = 0; i < clearList.Count; i++)
                {
                    if (clearList[i] <= msec) lastClear = clearList[i];
                    else break;
                }
            }

            var lastEntry = -1f;
            var curve = curves[type];
            for (var i = 0; i < curve.Count; i++)
            {
                if (curve[i].Msec <= msec) lastEntry = curve[i].Msec;
                else break;
            }

            if (lastClear >= lastEntry)
            {
                // 类型已被 NULL 清除（同刻清除优先）→ 跟随全局（流类型无全局语义 → 原速 1）
                return IsStreamType(type) ? 1f : GetGlobalMultAt(curves, msec);
            }

            var m = 1f;
            for (var i = 0; i < curve.Count; i++)
            {
                if (msec >= curve[i].Msec) m = curve[i].Mult;
                else break;
            }

            return m;
        }

        // 流类型键：本流无曲线 = 流内无变速 → 原速 1（绝不跟随全局/普通类型曲线）。
        if (IsStreamType(type)) return 1f;
        return GetGlobalMultAt(curves, msec);
    }

    /// <summary>构建某类型的有效倍率曲线：类型段覆盖全局段；NULL 清除点之后回退全局。
    /// 首段之前一律 1。仅类型有清除点（无真实段）时等效全局曲线。</summary>
    private static List<(float Msec, float Mult)> BuildEffectiveCurve(
        Dictionary<string, List<(float Msec, float Mult)>> curves,
        Dictionary<string, List<float>> clears,
        string type)
    {
        if (type == "" || !curves.ContainsKey(type))
        {
            if (IsStreamType(type)) return new List<(float Msec, float Mult)>();
            return curves.TryGetValue("", out var g)
                ? new List<(float Msec, float Mult)>(g)
                : new List<(float Msec, float Mult)>();
        }

        // 流类型键（s1/s2/...）：完全独立——不合并全局段，非段处按原速 1。
        var global = IsStreamType(type)
            ? new List<(float Msec, float Mult)>()
            : (curves.TryGetValue("", out var gv) ? gv : new List<(float Msec, float Mult)>());
        var typed = curves[type];
        var clearList = clears != null && clears.TryGetValue(type, out var cl)
            ? cl
            : new List<float>();

        var times = new List<float>();
        times.AddRange(global.Select(e => e.Msec));
        times.AddRange(typed.Select(e => e.Msec));
        times.AddRange(clearList);
        times.Sort();
        var uniq = new List<float>();
        foreach (var t in times)
        {
            if (uniq.Count == 0 || System.Math.Abs(uniq[uniq.Count - 1] - t) > 0.5f)
                uniq.Add(t);
        }

        var result = new List<(float Msec, float Mult)>();
        var active = false;
        foreach (var t in uniq)
        {
            var isClear = false;
            for (var i = 0; i < clearList.Count; i++)
            {
                if (System.Math.Abs(clearList[i] - t) < 0.5f) { isClear = true; break; }
            }

            var typeAt = false;
            var typeValue = 1f;
            for (var i = 0; i < typed.Count; i++)
            {
                if (System.Math.Abs(typed[i].Msec - t) < 0.5f) { typeAt = true; typeValue = typed[i].Mult; break; }
            }

            if (isClear)
                active = false;
            else if (typeAt)
                active = true;

            // 流类型非段处=原速 1；普通类型非段处跟随全局。
            result.Add((t, active ? typeValue : (IsStreamType(type) ? 1f : GetGlobalMultAt(curves, t))));
        }

        return result;
    }

    /// <summary>预计算每类型的 ∫sv 累计（有效曲线：类型覆盖全局、NULL 清除后回退全局；
    /// 段前倍率 1 即 ∫0→t0 = t0）。scroll 只积 SV（MajdataViewAlpha 模型：
    /// HS 并入每音符 speed 窗口 W=d/HS，不进积分）——SV=0 冻结、负 SV 反向由积分自然体现。
    /// 事件点 = SV 有效段起点（type + 全局 ""）。</summary>
    private static void BuildSvCumulatives()
    {
        SvCumCurves.Clear();
        MajdataSlideSv.Clear();
        var slideEvents = new SortedDictionary<float, float>();
        if (SvCurves.TryGetValue("slide", out var slideCurve))
            foreach (var point in slideCurve) slideEvents[point.Msec] = point.Mult;
        if (SvClearTimes.TryGetValue("slide", out var slideClears))
            foreach (var time in slideClears) slideEvents[time] = 1f;
        var previousTime = 0f;
        var previousMult = 1f;
        var slideIntegral = 0f;
        foreach (var point in slideEvents)
        {
            slideIntegral += (point.Key - previousTime) * previousMult;
            MajdataSlideSv.Add((point.Key, point.Value, slideIntegral));
            previousTime = point.Key;
            previousMult = point.Value;
        }
        var typeKeys = new List<string>();
        foreach (var kv in SvCurves)
        {
            if (kv.Key != "" && !typeKeys.Contains(kv.Key))
                typeKeys.Add(kv.Key);
        }

        foreach (var kv in SvClearTimes)
        {
            if (!typeKeys.Contains(kv.Key))
                typeKeys.Add(kv.Key);
        }

        if (SvCurves.ContainsKey(""))
            typeKeys.Add("");
        foreach (var key in typeKeys)
        {
            var effective = BuildEffectiveCurve(SvCurves, SvClearTimes, key);
            var list = effective.Select(e => e.Msec).Distinct().OrderBy(x => x).ToList();
            var cum = new List<(float Msec, float Mult, float Cum)>();
            var acc = list.Count > 0 ? list[0] : 0f;
            for (var i = 0; i < list.Count; i++)
            {
                var svMult = GetCurveMultAt(SvCurves, SvClearTimes, key, list[i]);
                cum.Add((list[i], svMult, acc));
                if (i + 1 < list.Count)
                    acc += cum[i].Item2 * (list[i + 1] - list[i]);
            }

            SvCumCurves[key] = cum;
        }
    }

    /// <summary>∫[0→msec] sv(τ)dτ（按音符类型有效曲线，二分查询预计算累计）。
    /// scroll 单调（sv 非负时）——负 SV 段递减由积分自然体现。</summary>
    private static float SvIntegral(string type, float msec)
    {
        var key = !string.IsNullOrEmpty(type) && SvCumCurves.ContainsKey(type) ? type : "";
        // 流类型键：本流无积分表 = 流内无 SV 曲线 → ∫=t 恒等原速（绝不回退全局表）。
        if (IsStreamType(type) && !SvCumCurves.ContainsKey(type)) return msec;
        if (!SvCumCurves.TryGetValue(key, out var cum) || cum.Count == 0) return msec;
        if (msec <= cum[0].Item1) return msec; // 第一段前倍率 1：∫=t
        var lo = 0;
        var hi = cum.Count - 1;
        while (lo < hi - 1)
        {
            var mid = (lo + hi) >> 1;
            if (cum[mid].Item1 <= msec) lo = mid;
            else hi = mid;
        }

        if (cum[hi].Item1 <= msec) lo = hi;
        return cum[lo].Item3 + cum[lo].Item2 * (msec - cum[lo].Item1);
    }

    /// <summary>构建音符自包含飞行时间表：[(msec, scroll, max前缀)]，覆盖
    /// [T−NoteScrollCoverMsec, T+Len]。组成：窗口起点采样（无曲线段=∫恒等 t）+ 该音符
    /// 实际曲线（类型键，无则全局 ""；流类型无表=无事件）的事件点 + T + T+Len。
    /// scroll 与 SvScrollPosByNoteIndex 同基准（全局累计 ∫sv）。max 前缀单调非降。
    /// 播放期 GetNoteScroll/GetNoteEnterTime 只查本表（二分）——零全局查询。</summary>
    private static void BuildNoteScrollTable(int index, string type, Manager.NoteData note, float scrollPos, float tailSp)
    {
        try
        {
            var tStart = Math.Min(-2000f, note.time.msec - NoteScrollCoverMsec);
            var tEnd = note.end != null ? note.end.msec : note.time.msec;
            // 实际曲线键：类型键有表用类型，否则全局 ""（与 SvIntegral 回退一致）；
            // 流类型无表 = 流内无 SV 曲线 → 无事件点（表只剩采样点，恒速检测短路原版）。
            var curveKey = IgnoreSvByNoteIndex.Contains(index) ? null : SvCumCurves.ContainsKey(type) ? type
                : (IsStreamType(type) ? null : (SvCumCurves.ContainsKey("") ? "" : null));
            var raw = new List<(float Msec, float Scroll)>(24);
            raw.Add((tStart, NoteSvIntegral(index, type, tStart))); // 窗口起点采样（无曲线 = ∫恒等 t）
            if (curveKey != null && SvCumCurves.TryGetValue(curveKey, out var cc) && cc != null)
                foreach (var p in cc)
                    if (p.Msec > tStart && p.Msec < tEnd)
                        raw.Add((p.Msec, p.Cum));
            raw.Add((note.time.msec, scrollPos));
            if (note.end != null) raw.Add((note.end.msec, tailSp));
            raw.Sort((a, b) => a.Msec.CompareTo(b.Msec));
            var table = new List<(float Msec, float Scroll, float Max)>(raw.Count);
            var mx = float.MinValue;
            foreach (var p in raw)
            {
                if (table.Count > 0 && System.Math.Abs(table[table.Count - 1].Msec - p.Msec) < 0.001f)
                {
                    var last = table[table.Count - 1];
                    table[table.Count - 1] = (p.Msec, p.Scroll, System.Math.Max(last.Max, p.Scroll));
                }
                else
                {
                    if (p.Scroll > mx) mx = p.Scroll;
                    table.Add((p.Msec, p.Scroll, mx));
                }
            }
            NoteScrollTableByNoteIndex[index] = table;
        }
        catch
        {
        }
    }

    /// <summary>查音符自己的飞行时间表：msec 处 scroll（段内线性插值——事件点之间
    /// 倍率恒定故 scroll 线性；表覆盖 [T−Cover, T+Len]，界外钳制到边界值）。
    /// 无表（谱面无变速且恒速检测已短路）→ 恒等 t。</summary>
    private static HashSet<int> IgnoreSvByNoteIndex => RuntimeCharts.Current.IgnoreSvByNoteIndex;
    private static float NoteSvIntegral(int index, string type, float msec)
        => IgnoreSvByNoteIndex.Contains(index) ? msec : SvIntegral(type, msec);

    private static float GetNoteScroll(int index, float msec)
    {
        List<(float Msec, float Scroll, float Max)> table;
        if (!NoteScrollTableByNoteIndex.TryGetValue(index, out table) || table.Count == 0) return msec;
        if (msec <= table[0].Msec) return NoteSvIntegral(index, SvTypeByNoteIndex[index], msec);
        var lastIdx = table.Count - 1;
        if (msec >= table[lastIdx].Msec) return NoteSvIntegral(index, SvTypeByNoteIndex[index], msec);
        var lo = 0;
        var hi = lastIdx;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >> 1;
            if (table[mid].Msec <= msec) lo = mid;
            else hi = mid - 1;
        }
        var p = table[lo];
        var n = table[lo + 1];
        if (n.Msec <= p.Msec) return p.Scroll;
        var f = (msec - p.Msec) / (n.Msec - p.Msec);
        return p.Scroll + (n.Scroll - p.Scroll) * f;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static void LoadMa2MainSvInjectPostfix(NotesReader __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        try
        {
            if (PendingSvSegments.Count == 0 && PendingHsSegments.Count == 0 && PendingRingSegments.Count == 0) return;

            SvCurves.Clear();
            HsCurves.Clear();
            BuildSpeedCurves(__instance, PendingSvSegments, SvCurves, SvClearTimes);
            BuildSpeedCurves(__instance, PendingHsSegments, HsCurves, HsClearTimes);
            BuildSvCumulatives();

            var svCount = SvCurves.Values.Sum(x => x.Count);
            var hsCount = HsCurves.Values.Sum(x => x.Count);
            if (!_loggedSvInject)
            {
                _loggedSvInject = true;
                MelonLogger.Msg($"[CustomNoteType] SV/HS active: {svCount} sv segments, {hsCount} hs segments");
            }

            // 预构建每音符表：类型、判定时刻总倍率 mSv×mHs（等效流速/激活提前量用）。
            try
            {
                var noteList = __instance.GetNoteList();
                if (noteList != null)
                {
                    SvScrollPosByNoteIndex.Clear();
                    SvWindowByNoteIndex.Clear();
                    SvTypeByNoteIndex.Clear();
        IgnoreSvByNoteIndex.Clear();
                    // 注意：StreamTypeByNoteIndex 不可在此 Clear——它在 loadNote 期间（ApplyOptionalHyperSpeed）
                    // 填充，而 loadMa2Main postfix 在 loadNote 循环之后运行；Clear 会丢掉刚填好的流标记，
                    // 导致流内音符回退普通类型（吃全局/类型曲线）。清空只应在 loadMa2 前缀/全量重置处做。
                    SpeedMultByNoteIndex.Clear();
                    foreach (var note in noteList)
                    {
                        if (note == null) continue;
                        if (SpeedClass(note).ReferenceMotion) ReferenceMotionByNoteIndex.Add(note.indexNote);
                        // 流内音符（行尾 s{N}）类型键 = 流键 s{N}：只吃本流曲线；
                        // 否则按音符种类（tap/star/hold/...）查类型曲线，无则全局。
                        var type = StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var streamType)
                            ? streamType
                            : ResolvePlayableSvType(note);
                        SvTypeByNoteIndex[note.indexNote] = type;
                        if (SpeedClass(note).IgnoreSV) IgnoreSvByNoteIndex.Add(note.indexNote);
                        var mHs = ResolvePlayableHs(note);
                        var scrollPos = NoteSvIntegral(note.indexNote, type, note.time.msec);
                        SvScrollPosByNoteIndex[note.indexNote] = scrollPos;
                        // 等效流速只由 HS 决定（MV：speed = noteSpeed×HS；SV 只进 scroll 积分，
                        // 不缩放每音符下落/渐入/激活）——若乘 mSv，SV 段后的音符（如重叠流
                        // 冻结段之后的 7h/2）会被末段倍率（如 0.5）整体放慢，与 MV 不符。
                        SpeedMultByNoteIndex[note.indexNote] = mHs;
                        // HS 单独存：飞行窗口 W = d/mHs（MV：speed = noteSpeed×HS 决定下落速度）。
                        HsMultByNoteIndex[note.indexNote] = mHs;
                        // hold 身体尾 scrollPos = ∫sv(type, T+Len)：身体进度随 scroll 驱动
                        // （原版帧处理后再对齐完整长条显示）。
                        var tailSp = 0f;
                        if (note.end != null)
                        {
                            SvTailScrollPosByNoteIndex[note.indexNote] = NoteSvIntegral(note.indexNote, type, note.end.msec);
                            tailSp = SvTailScrollPosByNoteIndex[note.indexNote];
                        }
                        // 每音符自包含飞行时间表（加载期一次算好）：
                        // 播放路径（位置/出现/渐入/hold 身体）只查本表，零全局查询。
                        BuildNoteScrollTable(note.indexNote, type, note, scrollPos, tailSp);
                    }

                    // Each 双押伙伴映射（原版 loadNote 后处理已填好 eachChild）：
                    // 流速不同的 each 对在 SvApplyNoteSpeedOnInit 里隐藏辅助条。
                    EachChildByNoteIndex.Clear();
                    foreach (var note in noteList)
                    {
                        if (note == null || note.eachChild == null || note.eachChild.Count == 0) continue;
                        var childIndexes = new List<int>(note.eachChild.Count);
                        foreach (var c in note.eachChild)
                            if (c != null) childIndexes.Add(c.indexNote);
                        EachChildByNoteIndex[note.indexNote] = childIndexes;
                    }

                    MelonLogger.Msg($"[CustomNoteType] SV/HS note table built: {SpeedMultByNoteIndex.Count} notes");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomNoteType] SV/HS note table failed: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CustomNoteType] SV/HS setup failed: {ex.Message}");
        }
    }

    // Keep native DefaultMsec/StartMsec; signed HS affects presentation only.
    // Record the window and hide each guides whose partners use different HS.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteBase), "Initialize")]
    public static void SvApplyNoteSpeedOnInit(NoteBase __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (SpeedMultByNoteIndex.Count == 0) return;
        try
        {
            var tr = Traverse.Create(__instance);
            var index = tr.Field("NoteIndex").GetValue<int>();
            float mult;
            if (!SpeedMultByNoteIndex.TryGetValue(index, out mult)) return;

            // 飞行窗口 W = d/mHs（见类注释；d = 原版 DefaultMsec，永不缩放）。
            var d = tr.Field("DefaultMsec").GetValue<float>();
            float mHs;
            if (!HsMultByNoteIndex.TryGetValue(index, out mHs)) mHs = 1f;
            SvWindowByNoteIndex[index] = mHs == 0 ? float.PositiveInfinity : d / mHs;

            // Each 双押辅助条：与 each 伙伴流速不同（SV/HS 倍率差）→ 隐藏连接线
            // （流速不同时连线两端移动速度不一致，辅助条会错位/拉伸）。
            if (EachChildByNoteIndex.TryGetValue(index, out var childIdxs))
            {
                foreach (var ci in childIdxs)
                {
                    float cm;
                    if (SpeedMultByNoteIndex.TryGetValue(ci, out cm) && Math.Abs(cm - mult) >= 0.001f)
                    {
                        var guide = tr.Field("GuideObj").GetValue<NoteGuide>();
                        if (guide != null) guide.HideEachGuide();
                        break;
                    }
                }
            }
        }
        catch
        {
        }
    }

    /// <summary>
    /// TouchNoteB.EndNote 里的 SetResult(NoteIndex, EScoreType.Touch, timing) 是 touch 系的唯一统计点。
    /// SetResult 有 isJudged 守卫：第一次调用后 isJudged=true，之后任何 SetResult 都会被忽略
    /// （所以原来加在 SetPlayResult override 里的 SetResult(Break) 无效，必须改这里）。
    /// 把 kind 参数（ldc.i4.4 = Touch）替换成 GetTouchScoreKind(this)：
    /// 绝赞系（BRSTP TouchBreakStar / BRTTP TouchBreak / MBTTP 地雷绝赞）→ Break(3)，
    /// 普通 touch（含 NMSTP 普通 TouchStar）→ Touch(4)——计分类别在 Initialize 已固化。
    /// TouchNoteC 不 override EndNote（继承 TouchNoteB.EndNote），B/C/E/D 全区都覆盖。
    /// 2026-08-22 修复：原实现是 CustomNoteTypes 类上的方法级 [HarmonyPatch]，方法名
    /// TouchNoteBEndNoteScoreTranspiler 不以 Prefix/Postfix/Transpiler 开头 → PatchAll 静默
    /// 跳过（日志 Applying 列表无此项）→ BRTTP 计分/统计一直按普通 Touch。必须用嵌套类 +
    /// 标准 Transpiler 方法名。
    /// </summary>
    [HarmonyPatch(typeof(TouchNoteB), "EndNote")]
    public static class TouchNoteBEndNoteScorePatch
    {
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var getKind = AccessTools.Method(typeof(CustomNoteTypes), nameof(GetTouchScoreKind));
            var codes = instructions.ToList();
            var replaced = 0;
            for (var i = 0; i < codes.Count; i++)
            {
                if (!IsSetResultCall(codes[i])) continue;
                // SetResult 调用前 6 条指令内找 ldc.i4.4（kind 参数，EScoreType.Touch）
                for (var j = i - 1; j >= 0 && j >= i - 6; j--)
                {
                    if (codes[j].opcode != OpCodes.Ldc_I4_4) continue;
                    codes[j] = new CodeInstruction(OpCodes.Ldarg_0);
                    codes.Insert(j + 1, new CodeInstruction(OpCodes.Call, getKind));
                    i++; // 跳过插入的 call
                    replaced++;
                    break;
                }
            }
            // 2026-08-25 修复：原实现用 CodeInstruction.Calls(MethodInfo)——object.Equals 按引用
            // 比较 operand 与 AccessTools.Method 结果（反汇编得到的 MethodInfo 实例与
            // GetMethod 实例不是同一对象，MethodInfo 不重写 Equals）→ 永远不命中 →
            // BRTTP/BRSTP 计分一直是普通 Touch。必须按名字+声明类型匹配。
            if (replaced == 0)
            {
                MelonLogger.Warning("[CustomNoteType] EndNote transpiler: SetResult call site NOT found; BRTTP/BRSTP scoring stays Touch");
            }
            else
            {
                MelonLogger.Msg($"[CustomNoteType] EndNote transpiler: {replaced} SetResult kind replaced -> GetTouchScoreKind");
            }
            return codes;
        }

        private static bool IsSetResultCall(CodeInstruction code)
        {
            if (code.opcode != OpCodes.Call && code.opcode != OpCodes.Callvirt) return false;
            // 反射模式 operand=MethodInfo；Cecil 模式 operand=Mono.Cecil.MethodReference。
            if (code.operand is MethodInfo mi)
            {
                return mi.Name == "SetResult" && mi.DeclaringType != null && mi.DeclaringType.Name == "GameScoreList";
            }
            if (code.operand != null)
            {
                var name = code.operand.GetType().GetProperty("Name")?.GetValue(code.operand) as string;
                if (name != "SetResult") return false;
                var decl = code.operand.GetType().GetProperty("DeclaringType")?.GetValue(code.operand);
                return decl?.GetType().GetProperty("Name")?.GetValue(decl) as string == "GameScoreList";
            }
            return false;
        }
    }

    // Keep rendering types intact. Correct both score denominators and category counts
    // using the game's score table, after each fresh calcTotal (which clears _total).
    [HarmonyPatch(typeof(NotesReader), "calcTotal")]
    public static class CalcTotalBreakFixPostfix
    {
        [HarmonyPostfix]
        public static void Postfix(NotesReader __instance)
        {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

            var total = __instance.GetTotal();
            foreach (var note in __instance.GetNoteList())
            {
                if (IsFakeNote(note)) continue;
                if (!NoteKinds.TryGetValue(note.indexNote, out var kind) ||
                    (kind != CustomNoteKind.TouchBreak && kind != CustomNoteKind.TouchBreakStar &&
                     kind != CustomNoteKind.MineTouchBreak)) continue;
                if (note.type.isBreak() || note.type.isExBreak()) continue;
                var original = note.type.isHold() ? NoteScore.EScoreType.Hold : NoteScore.EScoreType.Tap;
                total._allPerfectScore += NoteScore.GetJudgeScore(NoteJudge.ETiming.Critical, NoteScore.EScoreType.Break)
                    - NoteScore.GetJudgeScore(NoteJudge.ETiming.Critical, original);
                total._breakBonusScore += NoteScore.GetJudgeScore(NoteJudge.ETiming.Critical, NoteScore.EScoreType.BreakBonus);
                total._totalData[(int)note.type.getEnum()]--;
                total._totalData[(int)(note.type.isHold() ? NotesTypeID.Def.BreakHold : NotesTypeID.Def.Break)]++;
            }
            total._allPerfectPlusScore = total._allPerfectScore + total._breakBonusScore;
        }
    }

    /// <summary>
    /// Touch 系计分的 EScoreType。
    /// 计分类别在音符 Initialize（进入判定前）就已统一固化：
    ///   TouchStar（NMSTP）→ Touch 分——效果/判定/计分与普通 touch 完全一致；
    ///   TouchBreakStar（BRSTP）→ Break 分（绝赞额外分）——与绝赞 touch BRTTP 相同；
    ///   TouchBreak（BRTTP）→ Break 分；地雷绝赞（MBTTP）→ Break 分。
    /// 这里只读 IsBreakStar 字段/类型，不做“是 touchstar 就全给 Break”的粗分类。
    /// </summary>
    /// <summary>诊断用：已打印过的 (运行时类型 → kind) 组合，避免刷屏。</summary>
    private static readonly HashSet<string> ScoreKindLogged = new HashSet<string>();

    public static NoteScore.EScoreType GetTouchScoreKind(TouchNoteB note)
    {
        NoteScore.EScoreType kind;
        if (note is TouchStarNoteB starB) kind = starB.IsBreakStar ? NoteScore.EScoreType.Break : NoteScore.EScoreType.Touch;
        else if (note is TouchStarNoteC starC) kind = starC.IsBreakStar ? NoteScore.EScoreType.Break : NoteScore.EScoreType.Touch;
        else if (note is TouchBreakNoteB) kind = NoteScore.EScoreType.Break;
        else if (note is TouchBreakNoteC) kind = NoteScore.EScoreType.Break;
        else if (note is MineTouchNoteB mine && mine.IsMineTouchBreak) kind = NoteScore.EScoreType.Break;
        else kind = NoteScore.EScoreType.Touch;

        // 诊断（2026-09）：每种 (运行时类型 → kind) 组合只打印一次，用于确认绝赞 touch 是否真的按 Break 计分。
        var scoreKindKey = note.GetType().Name + " -> " + kind;
        if (ScoreKindLogged.Add(scoreKindKey))
        {
            MelonLogger.Msg($"[CustomNoteType] GetTouchScoreKind {scoreKindKey}");
        }

        return kind;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameScoreList), "SetResult")]
    public static void CustomBreakResultPrefix(GameScoreList __instance, int ____monitorIndex,
        int index, ref NoteScore.EScoreType kind, NoteJudge.ETiming timing)
    {
        if (!(CustomNoteTypes.FeaturesForMonitor(____monitorIndex))) {  return; }

        if (!__instance.IsEnable || !NoteKinds.TryGetValue(index, out var custom) ||
            (custom != CustomNoteKind.TouchBreak && custom != CustomNoteKind.TouchBreakStar &&
             custom != CustomNoteKind.MineTouchBreak)) return;
        var reader = NotesManager.Instance(____monitorIndex).getReader();
        var note = reader.GetNoteList()[index];
        if (note.isJudged || IsFakeNote(note)) return;
        kind = NoteScore.EScoreType.Break;
        // Stock SetResult updates touch chains only for kind=Touch. Preserve that
        // housekeeping while every call site (including forced/skip results) scores Break.
        if (note.type.getEnum() == NotesTypeID.Def.TouchTap && note.indexTouchGroup != -1)
        {
            var chain = reader.GetTouchChainList()[note.indexTouchGroup];
            chain.EndCount++;
            chain.ChainJudge = __instance.IsTrackSkip ? NoteJudge.ETiming.TooLate : timing;
        }
    }

    /// <summary>TouchNoteB 在 base.Initialize 之后自己重写 DefaultMsec/StartMsec
    /// （DefaultMsec = GetTouchSpeedForBeat(GetTouchSpeed())×4——原版有独立的 touch 流速选项；
    /// StartMsec = AppearMsec − DefaultMsec − GetTouchDispTimes()，淡入窗口 = D×0.25）。
    /// touch 系同样按类型分组总倍率 m = mSv×mHs 缩放（等效流速，无 scroll 视觉）。</summary>
    /// <summary>TouchNoteB 飞行窗口记录（等效流速缩放已删除——与 NoteBase 版一致，
    /// DefaultMsec/StartMsec 不缩放，touch 淡入/位置由原版 + scroll 层驱动；
    /// 本 postfix 只填 SvWindowByNoteIndex 供 SvRealTimeVisualPostfix/激活提前量使用）。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(TouchNoteB), "Initialize")]
    public static void SvApplyNoteSpeedOnTouchInit(TouchNoteB __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (SpeedMultByNoteIndex.Count == 0) return;
        try
        {
            var tr = Traverse.Create(__instance);
            var index = tr.Field("NoteIndex").GetValue<int>();
            float mult;
            if (!SpeedMultByNoteIndex.TryGetValue(index, out mult)) return;

            var d = tr.Field("DefaultMsec").GetValue<float>();
            float mHs;
            if (!HsMultByNoteIndex.TryGetValue(index, out mHs)) mHs = 1f;
            SvWindowByNoteIndex[index] = mHs == 0 ? float.PositiveInfinity : d / mHs;
        }
        catch
        {
        }
    }

    // Signed scroll changes presentation only; native NoteCheck still owns play.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteBase), "GetNoteYPosition")]
    public static void SvRealTimeVisualPostfix(NoteBase __instance, ref float __result)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (TryRingPresentation(__instance, false, out var presentation))
            __result = RingNativeY(__instance, presentation.Radius);
        BounceNoteVisualPostfix(__instance, ref __result);
    }

    // UpdateCtrl pools notes before Initialize, so this uses load-time data.
    // Native pools impose a 10-second lead ceiling; judgment timing is unchanged.
    private static float SvScaleActivationLead(float lead, float leadDiv, NoteData note)
    {
        lead = BounceHoldActivationLead(lead, note);
        if (SpeedMultByNoteIndex.Count == 0 || note == null) return lead;
        if (note.type.isAllSlide())
        {
            var hs = ResolvePlayableHs(note);
            if (Math.Abs(hs - 1) < .00001f) return lead;
            // Reserve enough time for the earliest slide appearance option.
            // HS uses absolute speed for fade lead; zero speed appears at the head.
            var offset = ScrollVisualTiming.SlideAppearanceOffset(RingViewSpeed(lead / leadDiv) * hs, -1);
            return Math.Min(10000f, Math.Max(lead, -offset));
        }
        if (!HsMultByNoteIndex.TryGetValue(note.indexNote, out var multiplier) ||
            !SvScrollPosByNoteIndex.TryGetValue(note.indexNote, out var target)) return lead;
        if (note.type.isTouch()) return TouchVisualActivationLead(note.indexNote, note.time.msec, lead / leadDiv, lead);
        var visible = GetRingFirstVisibleTimeCached(note, lead / leadDiv);
        return float.IsNaN(visible) ? lead : Math.Min(10000f, Math.Max(lead, note.time.msec - visible));
    }

    /// <summary>把 UpdateCtrl 里 apperMsecTap/apperMsecTouch 的读取替换为
    /// SvScaleActivationLead(apperMsec, 当前NoteData)——激活提前量随 SV 缩放。</summary>
    [HarmonyPatch]
    public static class SvActivationLeadTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return [AccessTools.Method(typeof(GameCtrl), "UpdateCtrl")];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var helper = AccessTools.Method(typeof(CustomNoteTypes), nameof(SvScaleActivationLead));
            var list = instructions.ToList();

            // 找循环里保存当前 NoteData 的局部变量（UpdateCtrl 主循环 V_11，类型 Manager.NoteData）。
            // Harmony 反射模式（MelonLoader 默认）：ldloc/stloc 的 operand 是 int 局部变量索引，
            // 类型必须从 MethodBody.LocalVariables 解析（Cecil VariableDefinition 检查在此模式永远不命中）。
            // Cecil 模式（HarmonyX TranspilerContext）：operand 是 Mono.Cecil VariableDefinition。
            Mono.Cecil.Cil.VariableDefinition noteVar = null;
            var noteVarIdx = -1;
            foreach (var inst in list)
            {
                if ((inst.opcode == OpCodes.Ldloc_S || inst.opcode == OpCodes.Ldloc
                     || inst.opcode == OpCodes.Stloc_S || inst.opcode == OpCodes.Stloc)
                    && inst.operand is Mono.Cecil.Cil.VariableDefinition vd
                    && vd.VariableType.FullName == "Manager.NoteData")
                {
                    noteVar = vd;
                    break;
                }
            }

            if (noteVar == null)
            {
                try
                {
                    var method = AccessTools.Method(typeof(GameCtrl), "UpdateCtrl");
                    foreach (var lv in method.GetMethodBody().LocalVariables)
                    {
                        if (lv.LocalType == typeof(Manager.NoteData))
                        {
                            noteVarIdx = lv.LocalIndex;
                            break;
                        }
                    }
                }
                catch (Exception ex)
                {
                    MelonLogger.Warning($"[CustomNoteType] SV lead transpiler: cannot resolve NoteData local: {ex.Message}");
                }
            }

            if (noteVar == null && noteVarIdx < 0)
            {
                MelonLogger.Warning("[CustomNoteType] SV lead transpiler: NoteData variable not found in UpdateCtrl; activation lead is NOT scaled by SV/HS (notes may pop in without fade-in)");
                foreach (var inst in list) yield return inst;
                yield break;
            }

            foreach (var inst in list)
            {
                if (inst.opcode == OpCodes.Ldfld && inst.operand is FieldInfo leadFi
                    && (leadFi.Name == "apperMsecTap" || leadFi.Name == "apperMsecTouch"))
                {
                    yield return inst;                                        // ldfld apperMsec*
                    // leadDiv：apperMsecTap = 2×DefaultMsec、apperMsecTouch = 1.25×DefaultMsec
                    // （反推 d = lead/leadDiv 给 SvScaleActivationLead 算 window = d/mHs）。
                    float leadDiv = leadFi.Name == "apperMsecTap" ? 2f : 1.25f;
                    yield return new CodeInstruction(OpCodes.Ldc_R4, leadDiv);
                    if (noteVar != null)
                    {
                        yield return new CodeInstruction(OpCodes.Ldloc_S, noteVar); // Cecil：VariableDefinition
                    }
                    else
                    {
                        yield return new CodeInstruction(OpCodes.Ldloc_S, noteVarIdx); // 反射：int 局部变量索引
                    }

                    yield return new CodeInstruction(OpCodes.Call, helper);   // call SvScaleActivationLead(float,float,NoteData)
                    continue;
                }

                yield return inst;
            }
        }
    }

    /// <summary>HoldNote.Execute 身体进度（原 num4 = 1−(TailMsec−adj−now)/DefaultMsec 纯时间驱动）
    /// 改为 scroll 驱动：p = 1−(s(T+Len)−s(now))/W。SV 交替段身体随 s 摆动（hold 条抽搐）、
    /// SV=0 冻结段身体停住（时停）、0.5 段慢速收束——与 MajdataViewAlpha HoldDrop 一致
    /// （尾 distance = 4.8 − speed×(s(T+LastFor)−s(now))）。
    /// 无 SV/HS 表（普通谱）时精确复刻原版时间公式，行为不变。</summary>
    private static float SvHoldTailProgress(NoteBase note)
    {
        if (TryRingPresentation(note, true, out var presentation))
            return (presentation.Radius - SpawnDefaultRadius) / SpawnRadiusSpan;
        var now = NotesManager.GetCurrentMsec();
        var tail = (float)FTailMsec.GetValue(note) - (float)FMaiBugAdjust.Invoke(note, null);
        return 1f - (Math.Max(tail, now) - now) / (float)FDefaultMsec.GetValue(note);
    }

    /// <summary>HoldNote.Execute：把身体进度 num4 的计算
    /// （`1f * (1f - num3 / DefaultMsec)` 时间驱动）替换为 SvHoldTailProgress(this)（scroll 驱动）。
    /// 匹配模式（实测 IL：HoldNote.Execute IL_0139-IL_014e 与 BreakHoldNote.Execute IL_0139-IL_014e 一致）：
    /// ldc.r4 1, ldc.r4 1, ldloc num3, ldarg.0（ldfld 的实例加载！）, ldfld DefaultMsec, div, sub, mul → stloc num4。
    /// 曾漏掉 ldarg.0 导致模式永不匹配——所有 hold 身体保持时间驱动（SV 交替段无抽搐）。</summary>
    [HarmonyPatch]
    public static class HoldBodyScrollTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            // BreakHoldNote 有自己的 Execute（num4 模式相同）——必须一起 patch，
            // 否则 break hold（含 MBHLD 地雷长条）身体仍走原版时间公式。
            return [
                AccessTools.Method(typeof(HoldNote), "Execute"),
                AccessTools.Method(typeof(BreakHoldNote), "Execute")
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var helper = AccessTools.Method(typeof(CustomNoteTypes), nameof(SvHoldTailProgress));
            var list = instructions.ToList();
            var replaced = false;
            for (var i = 0; i < list.Count - 8; i++)
            {
                var isOneA = list[i].opcode == OpCodes.Ldc_R4 && list[i].operand is float fa && fa == 1f;
                var isOneB = list[i + 1].opcode == OpCodes.Ldc_R4 && list[i + 1].operand is float fb && fb == 1f;
                var isLdloc = list[i + 2].opcode == OpCodes.Ldloc || list[i + 2].opcode == OpCodes.Ldloc_S;
                // ldfld 需要实例——IL 里 ldfld DefaultMsec 前必有 ldarg.0（实测 IL_0145）。
                // 覆盖全部 Ldarg 变体（Ldarg_0..3 / Ldarg_S / Ldarg）以防未来编译器差异。
                var isLdArg = list[i + 3].opcode == OpCodes.Ldarg_0
                    || list[i + 3].opcode == OpCodes.Ldarg_1
                    || list[i + 3].opcode == OpCodes.Ldarg_2
                    || list[i + 3].opcode == OpCodes.Ldarg_3
                    || list[i + 3].opcode == OpCodes.Ldarg_S
                    || list[i + 3].opcode == OpCodes.Ldarg;
                var isDfl = list[i + 4].opcode == OpCodes.Ldfld
                    && ((list[i + 4].operand is FieldInfo fi && fi.Name == "DefaultMsec")
                        || (list[i + 4].operand is Mono.Cecil.FieldDefinition fdv && fdv.Name == "DefaultMsec"));
                var isTail = list[i + 5].opcode == OpCodes.Div
                    && list[i + 6].opcode == OpCodes.Sub
                    && list[i + 7].opcode == OpCodes.Mul
                    && (list[i + 8].opcode == OpCodes.Stloc || list[i + 8].opcode == OpCodes.Stloc_S);
                if (isOneA && isOneB && isLdloc && isLdArg && isDfl && isTail)
                {
                    // 替换前 8 条指令为 ldarg.0 + call SvHoldTailProgress（stloc [i+8] 槽位保留）。
                    list[i] = new CodeInstruction(OpCodes.Ldarg_0);
                    list[i + 1] = new CodeInstruction(OpCodes.Call, helper);
                    list[i + 2] = new CodeInstruction(OpCodes.Nop);
                    list[i + 3] = new CodeInstruction(OpCodes.Nop);
                    list[i + 4] = new CodeInstruction(OpCodes.Nop);
                    list[i + 5] = new CodeInstruction(OpCodes.Nop);
                    list[i + 6] = new CodeInstruction(OpCodes.Nop);
                    list[i + 7] = new CodeInstruction(OpCodes.Nop);
                    replaced = true;
                    break;
                }
            }

            if (!replaced)
                MelonLogger.Warning("[CustomNoteType] HoldBodyScrollTranspiler: num4 pattern not found in Execute; hold body stays time-driven (SV jitter on hold bars disabled)");
            return list;
        }
    }

    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * Bounce（弹跳音符，MajdataViewAlpha BOUNCE 命令移植）
     *
     * ma2 语法（仿 SVSP 风格，MajdataViewAlpha 分组语义）：
     *   BOUNCE\t<bar>\t<grid>\t<时长>   从该时刻起，Tap/Star/Hold 音符在判定前 <时长> 内
     *                                    从判定线弹到生成半径再弹回判定线（全局展开到
     *                                    tap/star/hold 三类；each 双押在游戏里仍是 tap/star）
     *   BOUNCE\t<bar>\t<grid>\tNULL     恢复普通下落（同样展开到 tap/star/hold）
     *   BOUNCE\t<bar>\t<grid>\ttap=8:1,hold=4:1,...  分类时长（tap/star/hold/break），
     *                                    覆盖全局；分类 NULL = 该类型不弹跳
     * 时长格式（与 Majdata 一致）：秒数（0.5）或 N:M（N 分音符 M 连音，按该时刻 BPM 换算）。
     *
     * 实现（移植 MajdataViewAlpha NoteDrop.GetBounceDistance / TapBase.Update）：
     *   judgeOffset = now - AppearMsec（ms），弹跳窗口 [-B, 0)
     *   elapsed = clamp(judgeOffset + B, 0, B)；half = B/2
     *   a = 8×(4.8 - 1.225)/B²；fromApex = elapsed - half
     *   distance = 1.225 + 0.5×a×fromApex²   （4.8=判定线，1.225=生成半径，抛物线往返）
     *   映射到游戏坐标：t = (distance - 1.225)/(4.8 - 1.225)，
     *   y = StartPos + (EndPos - StartPos)×t（t=1 判定线，t=0 生成点）
     * 弹跳窗口前音符隐藏（alpha=0，对应 Majdata 的 forceRenderingOff）；
     * 判定路径完全不 patch——弹跳回到判定线的时刻 = 音频判定时刻，判定不受影响。
     * 与 SV（等效流速）、HS 正交：bounce 用原始时间驱动。
     * ========== ========== ========== ========== ========== ========== ========== ==========
     */
    private const float BounceSpawnRadius = 1.225f;
    private const float BounceJudgeLine = 4.8f;
    private static List<(int Bar, int Grid, string Text)> PendingBounceSegments => RuntimeCharts.Current.PendingBounceSegments;
    // 已转换的 Bounce 段（类型键 ""=全局展开后不保留；tap/star/hold/break → (msec, 时长秒)），
    // 每类型按 msec 升序。全局命令展开到 tap/star/hold；分类命令进对应类型。
    private static Dictionary<string, List<(float Msec, float DurationSec)>> BounceSegmentsByType => RuntimeCharts.Current.BounceSegmentsByType;
    // 每音符预计算的 bounce 时长（indexNote → 秒，0=无 bounce）。
    private static Dictionary<int, float> BounceDurationByNoteIndex => RuntimeCharts.Current.BounceDurationByNoteIndex;
    // 每音符的 bounce 类型键（indexNote → tap/star/hold/break）。
    private static Dictionary<int, string> BounceTypeByNoteIndex => RuntimeCharts.Current.BounceTypeByNoteIndex;
    private static bool _loggedBounce { get => RuntimeCharts.Current._loggedBounce; set => RuntimeCharts.Current._loggedBounce = value; }

    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * SPAWN（环形音符视觉出生半径，MajdataViewAlpha SPAWN 命令移植，2026-08）
     *
     * ma2 语法（仿 SVSP 风格，Majdata 分组语义）：
     *   SPAWN\t<bar>\t<grid>\t<半径>   从该时刻起，Tap/Hold/Star 头/Each/Break 环形音符
     *                                   视觉出生半径改为 <半径>（-4.8～4.8；1.225=原版，
     *                                   0=圆心，4.8=本侧判定线，-4.8=对面判定线）
     *   SPAWN\t<bar>\t<grid>\tNULL     恢复默认出生半径 1.225
     *   SPAWN\t<bar>\t<grid>\ttap=0,hold=4.8,...  分类半径（tap/hold/star/break/each）
     *   SPAWN\t<bar>\t<grid>\ttap=NULL           分类恢复（该类型回退全局）
     * 全局命令作用于 tap/hold/star/break（each 双押在游戏里仍是 tap/star）；touch/
     * touchhold/slide 不受影响。
     *
     * 实现（移植 MajdataViewAlpha NoteDrop.GetCurrentVisualDistance）：
     *   音符真实距离 = 1.225 + (4.8-1.225)×V_5（V_5 为滚动进度，判定线=4.8）。
     *   真实距离未达出生半径前，视觉停在出生半径处（Pending 钳制）；
     *   滚动到达后按 SV×HS 正常移动；判定时刻不变（判定锁音频）。
     *   与 BOUNCE 联动：弹跳抛物线以出生半径为起点/终点。
     * ========== ========== ========== ========== ========== ========== ========== ==========
     */
    private const float SpawnDefaultRadius = 1.225f;
    private const float SpawnJudgeLine = 4.8f;
    private const float SpawnRadiusSpan = SpawnJudgeLine - SpawnDefaultRadius; // 3.575
    private static Dictionary<int, float> SpawnRadiusByNoteIndex => RuntimeCharts.Current.SpawnRadiusByNoteIndex;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NotesRecord), "addRecord", new[] { typeof(string) })]
    public static void AddRecordBounceParsePrefix(string str)
    {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }

        if (string.IsNullOrEmpty(str) || !str.StartsWith("BOUNCE", StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            var parts = str.Split('\t');
            if (parts.Length < 4) return;
            if (!int.TryParse(parts[1].Trim(), out var bar)) return;
            if (!int.TryParse(parts[2].Trim(), out var grid)) return;
            PendingBounceSegments.Add((bar, grid, parts[3].Trim()));
        }
        catch
        {
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NotesReader), "loadMa2")]
    public static void LoadMa2BounceResetPrefix()
    {
        PendingBounceSegments.Clear();
        BounceSegmentsByType.Clear();
        BounceDurationByNoteIndex.Clear();
        BounceTypeByNoteIndex.Clear();
        _loggedBounce = false;
    }

    /// <summary>解析 bounce 时长：秒数或 N:M（N 分音符 M 连音，按 bpm 换算）。</summary>
    private static float ParseBounceDuration(string text, float bpm)
    {
        text = text.Trim();
        if (string.IsNullOrEmpty(text)) return 0f;
        if (float.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            return seconds;
        var parts = text.Split(':');
        if (parts.Length == 2
            && int.TryParse(parts[0], out var division)
            && int.TryParse(parts[1], out var count)
            && division > 0 && count >= 0 && bpm > 0f)
            return 60f / bpm * 4f / division * count;
        return 0f;
    }

    /// <summary>音符 → bounce 类型键（Majdata 语义：break 有专属曲线时用 break，
    /// 否则回落基础类型；each 双押在游戏里仍是 tap/star；touch/slide 不弹跳）。</summary>
    private static string ResolveBounceType(NoteData note)
    {
        if (note == null) return "";
        var t = note.type;
        if (t.isTouch() || t.isSlide() || t.isAllSlide()) return "";
        if (t.isBreak() && BounceSegmentsByType.ContainsKey("break")) return "break";
        if (t.isHold()) return "hold";
        if (t.isStar()) return "star";
        return "tap";
    }

    /// <summary>查询 msec 时刻某类型生效的 bounce 时长（秒，0=无 bounce）。</summary>
    private static float GetBounceDurationAt(float msec, string type)
    {
        if (string.IsNullOrEmpty(type) || !BounceSegmentsByType.TryGetValue(type, out var list))
            return 0f;
        var d = 0f;
        for (var i = 0; i < list.Count; i++)
        {
            if (msec >= list[i].Msec) d = list[i].DurationSec;
            else break;
        }

        return d;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static void LoadMa2MainBounceInjectPostfix(NotesReader __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        try
        {
            if (PendingBounceSegments.Count == 0) return;

            BounceSegmentsByType.Clear();

            void AddBounceEntry(string key, float msec, float duration)
            {
                if (!BounceSegmentsByType.TryGetValue(key, out var list))
                    BounceSegmentsByType[key] = list = new List<(float, float)>();
                list.Add((msec, duration));
            }

            foreach (var seg in PendingBounceSegments)
            {
                var time = new NotesTime();
                time.init(seg.Bar, seg.Grid, __instance);
                var msec = time.msec;
                var bpm = __instance.GetBPM_Time(msec);
                var text = seg.Text.Trim();

                if (text.Contains('='))
                {
                    // 分类命令：tap/star/hold/break；值 NULL/FALSE = 该类型不弹跳（时长 0，
                    // 必须保留以覆盖之前的全局/分类时长）。
                    foreach (var pair in text.Split(','))
                    {
                        var kv = pair.Split(new[] { '=' }, 2);
                        if (kv.Length != 2) continue;
                        var key = kv[0].Trim().ToLowerInvariant();
                        if (key != "tap" && key != "star" && key != "hold" && key != "break" && !IsStreamType(key)) continue;
                        var value = kv[1].Trim();
                        var reset = value.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                            || value.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                        AddBounceEntry(key, msec, reset ? 0f : ParseBounceDuration(value, bpm));
                    }
                }
                else
                {
                    // 全局命令：展开到默认类型（Majdata：tap/star/each/hold；each=双押，
                    // 游戏里仍是 tap/star）。NULL/FALSE = 全部关闭。
                    var reset = text.Equals("NULL", StringComparison.OrdinalIgnoreCase)
                        || text.Equals("FALSE", StringComparison.OrdinalIgnoreCase);
                    var duration = reset ? 0f : ParseBounceDuration(text, bpm);
                    AddBounceEntry("tap", msec, duration);
                    AddBounceEntry("star", msec, duration);
                    AddBounceEntry("hold", msec, duration);
                }
            }

            // 每类型按 msec 排序、同刻去重（保留后者）。
            foreach (var list in BounceSegmentsByType.Values)
            {
                list.Sort((a, b) => a.Msec.CompareTo(b.Msec));
                for (var i = list.Count - 1; i > 0; i--)
                {
                    if (System.Math.Abs(list[i].Msec - list[i - 1].Msec) < 0.5f)
                    {
                        list.RemoveAt(i - 1);
                    }
                }
            }

            // 预构建每音符 bounce 时长表。
            try
            {
                var noteList = __instance.GetNoteList();
                if (noteList != null)
                {
                    BounceDurationByNoteIndex.Clear();
                    BounceTypeByNoteIndex.Clear();
                    foreach (var note in noteList)
                    {
                        if (note == null) continue;
                        var baseType = ResolveBounceType(note);
                        var type = !string.IsNullOrEmpty(baseType) && StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var streamType)
                            ? streamType : baseType;
                        BounceTypeByNoteIndex[note.indexNote] = type;
                        BounceDurationByNoteIndex[note.indexNote] = GetBounceDurationAt(note.time.msec, type);
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[CustomNoteType] Bounce note table failed: {ex.Message}");
            }

            if (!_loggedBounce)
            {
                _loggedBounce = true;
                var total = BounceSegmentsByType.Values.Sum(x => x.Count);
                MelonLogger.Msg($"[CustomNoteType] Bounce active: {total} typed segments (keys: {string.Join(",", BounceSegmentsByType.Keys)})");
            }
        }
        catch (Exception ex)
        {
            MelonLogger.Warning($"[CustomNoteType] Bounce setup failed: {ex.Message}");
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NotesRecord), "addRecord", new[] { typeof(string) })]
    public static void AddRecordSpawnParsePrefix(string str) {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }
        ReadRingCommand(str);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(NotesReader), "loadMa2")]
    public static void LoadMa2SpawnResetPrefix() => ResetRingCommands();

    private static float GetSpawnRadiusAt(float msec, string type)
        => SinmaiAlpha.ChartVisuals.RingState.Resolve(RingChanges, msec,
            new SinmaiAlpha.ChartVisuals.VisualNote { Family = type }, IsStreamType(type) ? type : "").Spawn;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static void LoadMa2MainSpawnInjectPostfix(NotesReader __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        try { BuildRingCommands(__instance); }
        catch (Exception ex) { MelonLogger.Warning("[Ring Visual] setup failed: " + ex.Message); }
    }
    /// <summary>弹跳视觉：GetNoteYPosition 后置改写音符位置（基类，Tap/Star/Hold/Break 全覆盖）。
    /// __result 返回弹跳 y（HoldNote.Execute 用返回值算 body 长度/中点，不能改 0）。
    /// 外框（NoteGuide 弧形引导环）：按【原版运动轨迹 + 弹跳位移】处理——贴图/缩放/朝向
    /// 全部保持原版逻辑，只在其原版 local 位置上叠加 delta（= 弹跳位置 − 原版位置），
    /// 与音符的相对关系与原版任意时刻一致（原版判定线处对齐 → 弹跳中自然对齐）。
    /// 光效/绝赞挂到 NoteObj 下跟随（ReparentBounceEffects）。
    /// 弹跳窗口前：音符本体隐藏（alpha=0，对应 Majdata forceRenderingOff）；
    /// 判定后：外框位移归零（原版），恢复可见。</summary>
    public static void BounceNoteVisualPostfix(NoteBase __instance, ref float __result)
    {
        if (BounceSegmentsByType.Count == 0 || __instance == null) return;
        // TouchHoldC 用基类 GetNoteYPosition，但 bounce 只适配 Tap/Star/Hold/Break
        // （Majdata 的 BOUNCE 类型不含 touch）；TouchNoteB 有自己的位置公式，天然排除。
        if (__instance is TouchHoldC) return;
        if (__instance is HoldNote || __instance is BreakHoldNote) return;
        try
        {
            var tr = Traverse.Create(__instance);
            var appear = tr.Field("AppearMsec").GetValue<float>();
            float bounce;
            if (!BounceDurationByNoteIndex.TryGetValue(tr.Field("NoteIndex").GetValue<int>(), out bounce) || bounce <= 0f)
            {
                // fallback：直接查段表（字典可能因加载顺序未构建——GetNoteList 在 loadMa2Main 后置可能为空）。
                string fallbackType;
                if (!BounceTypeByNoteIndex.TryGetValue(tr.Field("NoteIndex").GetValue<int>(), out fallbackType))
                    return;
                bounce = GetBounceDurationAt(appear, fallbackType);
                if (bounce <= 0f) return;
            }

            var now = NotesManager.GetCurrentMsec();
            var judgeOffset = now - appear;
            var bMs = bounce * 1000f;

            if (judgeOffset < -bMs)
            {
                // 弹跳窗口前：音符本体 + 光效 + 外框全部隐藏——note 弹出前外框不可见，
                // 避免外框从中间快速放大移动到判定线的视觉（scale 预过渡也取消）。
                SetBounceAlpha(tr, 0f);
                SetBounceChildrenActive(tr, false);
                SetBounceGuideActive(tr, false);
                return;
            }

            if (judgeOffset < 0f)
            {
                // 弹跳窗口内：抛物线位置（出生半径→判定线→出生半径；SPAWN 联动）。
                // 弹跳轨迹以【原版判定线位置】为起终点（含音符速度常量偏移 V_9），
                // 否则外框在接近判定线时残留 -V_9 偏移、弧圈拼不齐。
                // 出生半径 R 由 SPAWN 曲线给出（默认 1.225）；y 仍按标准映射
                // t=(distance−1.225)/3.575，与停驻位置（v5=(R−1.225)/3.575）无缝衔接。
                var bounceIndex = tr.Field("NoteIndex").GetValue<int>();
                var spawnR = BounceSpawnRadius;
                if (!SpawnRadiusByNoteIndex.TryGetValue(bounceIndex, out spawnR))
                {
                    string fallbackType;
                    if (BounceTypeByNoteIndex.TryGetValue(bounceIndex, out fallbackType))
                        spawnR = GetSpawnRadiusAt(appear, fallbackType);
                }

                var elapsed = Mathf.Clamp(judgeOffset + bMs, 0f, bMs);                var half = bMs * 0.5f;
                var destroyR = RingValuesByNoteIndex.TryGetValue(bounceIndex, out var ringValue) ? ringValue.Destroy : BounceJudgeLine;
                var accel = 8f * (destroyR - spawnR) / (bMs * bMs);
                var fromApex = elapsed - half;
                var distance = spawnR + 0.5f * accel * fromApex * fromApex;
                var t = (distance - BounceSpawnRadius) / (BounceJudgeLine - BounceSpawnRadius);
                var startPos = tr.Field("StartPos").GetValue<float>();
                var endPos = tr.Field("EndPos").GetValue<float>();
                var span = endPos - startPos;
                var v9 = GetNoteSpeedOffsetY(__instance, span);
                var bounceY = startPos + span * t + v9;
                __result = bounceY;

                // 外框（弧形提示线）：弧心固定在音符轨道圆心（屏幕中心，localPosition 不动），
                // 弧大小直接跟随音符当前径向位置（Majdata tapLine：bounceLineScale = clamp01(distance/4.8)）。
                // 弹跳起点（distance=4.8 → scale=1）由窗口前分支的"预过渡"衔接（最后 50ms 原版渐进值
                // 平滑过渡到 1），弹跳期间直接设置——立即跟随、无跳变。
                try
                {
                    var guide = tr.Field("GuideObj").GetValue<NoteGuide>();
                    var gt = guide != null ? guide.transform : tr.Field("NoteGuideTrans").GetValue<Transform>();
                    if (gt != null)
                    {
                        var s = Mathf.Clamp01(distance / BounceJudgeLine);
                        if (s < 0.01f) s = 0.01f;
                        gt.localScale = new Vector3(s, s, 1f);
                    }
                }
                catch
                {
                }

                // 光效/绝赞：挂到 NoteObj 下跟随（音符的一部分，旋转无碍）。
                ReparentBounceEffects(tr);

                SetBounceAlpha(tr, 1f);
                SetBounceChildrenActive(tr, true);
                SetBounceGuideActive(tr, true);
                return;
            }

            // 判定后：恢复可见，外框缩放交给原版每帧继续接管（音符过判定线后外框应随
            // 原版 progress 继续缩小，不能在这里强制设回 1——否则外框停在 1 与下落中的
            // 音符错位）。
            SetBounceAlpha(tr, 1f);
            SetBounceChildrenActive(tr, true);
            SetBounceGuideActive(tr, true);
        }
        catch
        {
        }
    }

    /// <summary>原版 GetNoteYPosition 的返回 = 插值位置 + V_9（音符速度常量偏移）：
    /// V_9 = (EndPos−StartPos) × (−0.008333334) × (GetNoteSpeed()/150 − 1)。
    /// GetNoteSpeed() 读 UserOption 的原始选项值（不经 HS postfix），与原版 IL 一致。</summary>
    private static float GetNoteSpeedOffsetY(NoteBase note, float span)
    {
        try
        {
            var score = Singleton<GamePlayManager>.Instance.GetGameScore(note.MonitorId, -1);
            var speed = score.UserOption.GetNoteSpeed;
            var v8 = Convert.ToInt32(speed) / 150f;
            return span * -0.008333334f * (v8 - 1f);
        }
        catch
        {
            return 0f;
        }
    }

    /// <summary>光效/绝赞挂到 NoteObj 下（保持世界位置），随音符弹跳移动（音符的一部分，
    /// 圆形/星形光效旋转无碍）。音符回池复用 Initialize 时层级由原版重新设置。
    /// 外框（NoteGuide 弧形）不挂载——见 bounce 分支的 delta 位移方案。</summary>
    private static void ReparentBounceEffects(Traverse tr)
    {
        try
        {
            var noteObj = tr.Field("NoteObj").GetValue<GameObject>();
            if (noteObj == null) return;
            var noteTrans = noteObj.transform;
            var eff = tr.Field("EffectSprite").GetValue<SpriteRenderer>();
            if (eff != null && eff.transform.parent != noteTrans)
            {
                eff.transform.SetParent(noteTrans, true);
            }
        }
        catch
        {
        }

        try
        {
            var noteObj = tr.Field("NoteObj").GetValue<GameObject>();
            if (noteObj == null) return;
            var noteTrans = noteObj.transform;
            var exObj = tr.Field("ExObj").GetValue<GameObject>();
            if (exObj != null && exObj.transform.parent != noteTrans)
            {
                exObj.transform.SetParent(noteTrans, true);
            }
        }
        catch
        {
        }
    }

    private static void SetBounceAlpha(Traverse tr, float alpha)
    {
        var sr = tr.Field("SpriteRender").GetValue<SpriteRenderer>();
        if (sr != null)
        {
            var c = sr.color;
            c.a = alpha;
            sr.color = c;
        }

        var srEx = tr.Field("SpriteRenderEx").GetValue<SpriteRenderer>();
        if (srEx != null)
        {
            var c = srEx.color;
            c.a = alpha;
            srEx.color = c;
        }
    }

    /// <summary>外框（NoteGuide 判定提示线）的显示开关。弹跳窗口前隐藏（note 弹出前外框不可见），
    /// 弹跳窗口内显示并跟随缩放。SetActive 不会被原版每帧覆盖（原版 SetAlpha 仍执行，恢复后正常）。</summary>
    private static void SetBounceGuideActive(Traverse tr, bool active)
    {
        try
        {
            var guide = tr.Field("GuideObj").GetValue<NoteGuide>();
            if (guide != null)
            {
                guide.gameObject.SetActive(active);
            }
        }
        catch
        {
        }
    }

    /// <summary>光效（Break 脉冲）的显示开关（SetActive）。它的颜色每帧被原版
    /// GetNoteYPosition 重设，用颜色隐藏无效（且会破坏脉冲观感），只能开关 active。
    /// 外框（NoteGuide）不隐藏——原版淡入，无闪现问题。</summary>
    private static void SetBounceChildrenActive(Traverse tr, bool active)
    {
        try
        {
            var eff = tr.Field("EffectSprite").GetValue<SpriteRenderer>();
            if (eff != null)
            {
                eff.gameObject.SetActive(active);
            }
        }
        catch
        {
        }
    }

    /// <summary>弹跳音符激活瞬间（base Initialize）隐藏光效并把光效挂到 NoteObj 下
    /// ——否则激活帧光效会在生成点闪现一瞬。音符贴图的最终隐藏由各子类 Initialize 后置
    /// 完成（子类会重设颜色覆盖 alpha），外框不隐藏（原版淡入）。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteBase), "Initialize")]
    public static void BounceHideOnInit(NoteBase __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (BounceDurationByNoteIndex.Count == 0 || __instance == null) return;
        if (__instance is TouchHoldC || __instance is TouchNoteB) return;
        try
        {
            var tr = Traverse.Create(__instance);
            var index = tr.Field("NoteIndex").GetValue<int>();
            float bounce;
            if (!BounceDurationByNoteIndex.TryGetValue(index, out bounce) || bounce <= 0f) return;
            SetBounceChildrenActive(tr, false);
            SetBounceAlpha(tr, 0f);
            ReparentBounceEffects(tr);
        }
        catch
        {
        }
    }

    /// <summary>注册流程内、渲染前把音符贴图 alpha 最终归零（子类 Initialize 会把颜色重置回 1，
    /// 而 UpdateCtrl 注册在 UpdateNotes 之后——不在注册流程内隐藏就会闪一帧）。</summary>
    private static void BounceHideVisualFinal(NoteBase note)
    {
        if (BounceDurationByNoteIndex.Count == 0 || note == null) return;
        try
        {
            var tr = Traverse.Create(note);
            var index = tr.Field("NoteIndex").GetValue<int>();
            float bounce;
            if (!BounceDurationByNoteIndex.TryGetValue(index, out bounce) || bounce <= 0f) return;
            SetBounceAlpha(tr, 0f);
        }
        catch
        {
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TapNote), "Initialize")]
    public static void BounceHideTapInit(TapNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        BounceHideVisualFinal(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakNote), "Initialize")]
    public static void BounceHideBreakInit(BreakNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        BounceHideVisualFinal(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(HoldNote), "Initialize")]
    public static void BounceHideHoldInit(HoldNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        BounceHideVisualFinal(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakHoldNote), "Initialize")]
    public static void BounceHideBreakHoldInit(BreakHoldNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        BounceHideVisualFinal(__instance);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StarNote), "Initialize")]
    public static void BounceHideStarInit(StarNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        BounceHideVisualFinal(__instance);
    }

    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * 地雷判定
     *
     * 原版的判定入口（非自动播放）：
     *   - Tap / Star / BreakTap / Ex / ExBreak：NoteBase.Judge / NoteBase.JudgeToolate
     *   - Touch：TouchNoteB.Judge（超时走 NoteBase.JudgeToolate）
     *   - Slide：SlideRoot.Judge / SlideRoot.JudgeToolate
     *   - Hold / BreakHold / TouchHold：JudgeTotalResult（头判 + 体判算出的最终结果）
     * 它们都是同一个模式：算出 NoteJudge.ETiming 后 stfld 到 JudgeResult / JudgeHeadResult 字段。
     * 计分(SetPlayResult)、判定显示(JudgeGrade)、特效全部读这个字段，
     * 所以在写入字段前把地雷的判定反转即可：
     *     命中（非 Miss） -> Miss（TooLate）
     *     未命中（Miss）  -> Critical Perfect
     * 本版本 NoteJudge.ETiming 里 TooFast=0 / TooLate=14 / End=15 是三种 Miss
     * （NoteJudge.ConvertJudge 都映射到 JudgeBox.Miss），没有名为 Miss 的枚举值。
     * 注：AutoJudge / GameManager.AutoJudge 只在自动播放时调用，手动判定不走那里。
     */

    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * HoldOn 地雷贴图（transpiler）
     *
     * HoldNote.HoldOn / BreakHoldNote.HoldOn / TouchHoldC.HoldOn 都是非虚方法（无法 override），
     * 玩家按住/释放时会给 hold 条赋原版亮态贴图（NormalHoldOn / EachHoldOn / HoldOff /
     * BreakHoldOn / TouchHoldGuide / TouchHoldGuideOff），把地雷贴图覆盖掉。
     * 方案：transpiler 把方法里的 SpriteRenderer.set_sprite 调用替换成
     *   ldarg.1; call SetHoldSpriteWithMine(SpriteRenderer, Sprite, bool on)
     * （set_sprite 前栈是 [sr, sprite]，ldarg.1 压入 on 参数，栈形不变），
     * helper 先执行原版赋值，再按地雷类型重贴对应贴图。
     */

    public static void SetHoldSpriteWithMine(SpriteRenderer sr, Sprite sprite, bool on)
    {
        if (sr == null) return;
        sr.sprite = sprite;

        var note = sr.GetComponentInParent<NoteBase>();

        if (note is TouchBreakHoldC)
        {
            ApplyHoldKeyToSprite(sr, "touchhold_break");
            ApplyBreakTouchHoldProgress(note);
        }
        else if (note is MineTouchHoldC)
        {
            // touchhold 的 gauge：普通用 touchhold_off，绝赞（BRTHO，IsTouchBreak）用 touchhold_break。
            var critical = note.GetComponent<MineNoteBehaviour>()?.IsTouchBreak == true;
            ApplyHoldKeyToSprite(sr, critical ? "touchhold_break" : "touchhold_off");
        }
        else if (note is MineBreakHoldNote)
        {
            ApplyHoldKeyToSprite(sr, on ? "hold_break_mine_on" : "hold_break_mine");
        }
        else if (note is MineHoldNote)
        {
            ApplyHoldKeyToSprite(sr, on ? "hold_mine_on" : "hold_mine");
        }
    }

    private static void ApplyHoldKeyToSprite(SpriteRenderer sr, string textureKey)
    {
        if (!MineTextures.TryGetValue(textureKey, out var texture))
        {
            MelonLogger.Warning($"[CustomNoteType] Missing mine texture: {textureKey}");
            return;
        }

        sr.sprite = CreateSpriteFromTexture(textureKey, texture, sr.sprite);
    }

    [HarmonyPatch]
    public static class HoldOnMineTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(HoldNote), "HoldOn"),
                AccessTools.Method(typeof(BreakHoldNote), "HoldOn"),
                AccessTools.Method(typeof(TouchHoldC), "HoldOn"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var inst in instructions)
            {
                // 用方法名匹配 SpriteRenderer.set_sprite（不依赖 Harmony 的 Calls 匹配，兼容 Cecil operand）。
                if ((inst.opcode == OpCodes.Callvirt || inst.opcode == OpCodes.Call) &&
                    GetMethodOperandName(inst.operand) == "set_sprite")
                {
                    // set_sprite 前栈是 [sr, sprite]；ldarg.1 压入 on → call helper 消费三个参数。
                    // ⚠️ 被替换的指令可能带有分支标签 / 异常块边界（try/catch），必须转移到
                    // 第一条替换指令上，否则 DMD 编译报 "Label #N is not marked"
                    // （IL Compile Error，HoldOn 的 branch 目标就指向 set_sprite）。
                    var ldarg = new CodeInstruction(OpCodes.Ldarg_1);
                    ldarg.labels.AddRange(inst.labels);
                    ldarg.blocks.AddRange(inst.blocks);
                    yield return ldarg;
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CustomNoteTypes), nameof(SetHoldSpriteWithMine)));
                    continue;
                }

                yield return inst;
            }
        }

        private static string GetMethodOperandName(object operand)
        {
            if (operand is MethodInfo mi) return mi.Name;
            var prop = operand?.GetType().GetProperty("Name");
            return prop?.GetValue(operand) as string;
        }
    }

    private static bool _loggedMissingFanTexture;

    private static bool _loggedMineInversion;

    /// <summary>确保地雷 note 挂了 MineNoteBehaviour（slide 特判用）。
    /// SlideFan.Initialize 不调用 SlideRoot.Initialize，基类 postfix 链可能不触发，必须显式挂。
    /// ⚠️ 不能只依赖 NoteKinds[data.indexNote]：slide 初始化拿到的 NoteData 的 indexNote
    /// 可能与注册时不一致（历史 bug #22）——Mine* 类本身只可能来自地雷池，直接按类判定。</summary>
    internal static void EnsureMineBehaviour(Component note, NoteData data)
    {
        try
        {
            if (note == null || data == null) return;

            if (!NoteKinds.TryGetValue(data.indexNote, out var kind) || kind == CustomNoteKind.None)
            {
                // indexNote 查不到（slide 的 NoteData indexNote 与注册不一致）→ 按类兜底。
                if (note is not (MineSlideRoot or MineSlideFan)) return;
                kind = CustomNoteKind.Mine;
            }

            var behaviour = note.gameObject.GetComponent<MineNoteBehaviour>();
            if (behaviour == null)
            {
                behaviour = note.gameObject.AddComponent<MineNoteBehaviour>();
            }

            behaviour.Setup(data.indexNote,
                kind is CustomNoteKind.Mine or CustomNoteKind.MineTouchBreak,
                kind is CustomNoteKind.TouchBreak or CustomNoteKind.MineTouchBreak,
                applyTextures: false);
        }
        catch
        {
        }
    }

    /// <summary>判断是不是地雷 note（简单键类型；slide 不在这里处理）。
    /// ⚠️ 必须按类判断：自反转类（tap/star/break/touch）按历史设计不挂 MineNoteBehaviour，
    /// 用 behaviour 判断会导致自动 CP 对它们完全不触发（历史 bug #25）。
    /// 2026-08-26：加入 MineTouchStarNoteB/C（MNSTP 地雷 touchstar）——与 MNTTP 一样到线自动 CP。</summary>
    private static bool IsMineSimpleNote(NoteBase note)
    {
        return note is MineTapNote or MineStarNote or MineBreakNote or MineBreakStarNote
            or MineHoldNote or MineBreakHoldNote or MineTouchNoteB or MineTouchNoteC or MineTouchHoldC
            or MineTouchStarNoteB or MineTouchStarNoteC;
    }

    /// <summary>地雷键自动判定：按键到达判定线那一刻自动判 Critical Perfect，不需要玩家碰。
    /// 只取 autoplay 的"到线判 CP"时机，不改动其它任何行为（不自动推进、不忽略触摸）：
    /// 玩家在判定线之前碰到 → 正常判定路径（打偏 → Miss）；不碰 → 到线自动 CP，从这一帧起
    /// 不再有任何 late/miss 判定（note 直接以 CP 收尾）。
    /// slide 不适用：slide 有自己的规则（星星到底=CP、提前划完=Miss，见 ApplyMineJudgeTiming）。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteBase), "SetAutoPlayJudge")]
    public static void MineAutoJudgePostfix(NoteBase __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (IsFakeNoteOwner(__instance)) return;
        try
        {
            // ⚠️ hold 系排除（历史 bug：MineHoldNote 也命中 IsMineSimpleNote）：
            // 这里 Traverse 直写 JudgeResult=Critical 会绕过 MineJudgeInversion 的 stfld 反转链，
            // 且让 MineHoldTouchCheck 的守卫 `GetJudgeResult()!=End → return` 从判定线前 ~4.17ms 起
            // 永远触发 → "判定期间碰到→立即 Miss" 完全失效（用户实测"MNHLD 碰到也 CP"的根因）。
            // hold 的头自动判定由原版 SetAutoPlayJudge override（写 JudgeHeadResult，配合
            // MineHoldAutoJudge）负责，不写 JudgeResult，无冲突。
            if (__instance is MineHoldNote or MineBreakHoldNote or MineTouchHoldC) return;
            if (!IsMineSimpleNote(__instance)) return;
            // 已判过（玩家判定线前碰到 = Miss / 已自动 CP）→ 不动
            if (__instance.GetJudgeResult() != NoteJudge.ETiming.End) return;
            // 原版 autoplay 门槛：判定线前 4.17ms 内
            var now = NotesManager.GetCurrentMsec();
            var appear = Traverse.Create(__instance).Field("AppearMsec").GetValue<float>();
            if (now < appear - 4.166667f) return;
            // 到轨道 → 自动 Critical Perfect（AutoJudge 在非 autoplay 模式返回 TooFast，不能直接用它）
            Traverse.Create(__instance).Field("JudgeResult").SetValue(NoteJudge.ETiming.Critical);
            Traverse.Create(__instance).Method("PlayJudgeSe").GetValue();
        }
        catch
        {
        }
    }

    /// <summary>地雷 slide 自动判定：星星滑到 perfect 判定时刻还没提前划完 → 自动判 Critical Perfect 并立即收尾。
    /// 背景：SlideRoot : MonoBehaviour（不继承 NoteBase），MineAutoJudgePostfix（patch NoteBase.SetAutoPlayJudge）
    /// 对 slide 不生效。原版地雷 slide 的 CP 由 ApplyMineJudgeTiming 在 JudgeToolate（超时）时才给；
    /// 用户 2026-08-26 要求："在普通星星刚好判定为perfect的时候就判定为perfect 只要没提前划完就是perfect"。
    /// 判定点 = 滑到终点的 perfect 时刻（Judge() 里 diff = now-TailMsec+lastWait 为 0 的点 = TailMsec-lastWaitTimeForJudge）。
    /// 2026-08-26 二次修正：用户实测"地雷星星会稍稍快于刚好划完的时候就消失"——TailMsec-lastWait 比星星
    /// 视觉到终点（TailMsec，slide 走完整条轨道）提前了 lastWait ms → 自动收尾时刻改到 tailMsec（轨道刚走完，
    /// 星星到终点即消失）。玩家在判定点前划完 → 走原版 Judge → 反转 Miss，不受影响；判定点后、TailMsec 前
    /// 划完 → 原版 perfect/晚判 → 反转 Miss（碰到地雷 = Miss），只有完全没划完才自动 CP。
    /// 收尾必须在本帧完成（复刻 NoteCheck 收尾段），否则延迟窗口内玩家晚划会触发 Judge() 把 CP 覆盖成 Miss。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(SlideRoot), "NoteCheck")]
    public static void MineSlideAutoJudgePostfix(SlideRoot __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (IsFakeNoteOwner(__instance)) return;
        try
        {
            if (__instance is not (MineSlideRoot or MineSlideFan)) return;
            // 已判定（提前划完 = Miss / 已自动 CP）→ 不动
            if (__instance.GetJudgeResult() != NoteJudge.ETiming.End) return;
            var t = Traverse.Create(__instance);
            var tailMsec = t.Field("TailMsec").GetValue<float>();
            // 自动判定点 = 轨道走完时刻（TailMsec）：星星刚好到终点即收尾消失；
            // 未到点不自动判（玩家仍可提前划完 → Miss）。
            if (NotesManager.GetCurrentMsec() < tailMsec) return;
            // 没提前划完 → 自动 Critical Perfect
            t.Field("JudgeResult").SetValue(NoteJudge.ETiming.Critical);
            t.Field("JudgeTimingDiffMsec").SetValue(0f);
            t.Method("PlayJudgeSe").GetValue();
            // 立即收尾（复刻 SlideRoot.NoteCheck 720-749 行收尾段）
            var parent = __instance.ParentTransform;
            var arrows = t.Field("_arrowList").GetValue<List<GameObject>>();
            for (var i = arrows.Count - 1; i >= 0; i--)
            {
                arrows[i].SetActive(false);
                arrows[i].transform.SetParent(parent, false);
                arrows.RemoveAt(i);
            }

            var breaks = t.Field("_breakArrowList").GetValue<List<GameObject>>();
            for (var i = breaks.Count - 1; i >= 0; i--)
            {
                breaks[i].SetActive(false);
                breaks[i].transform.SetParent(parent, false);
                breaks.RemoveAt(i);
            }

            var judgeObj = t.Field("JudgeObj").GetValue<SlideJudge>();
            if (judgeObj != null && ShouldShowMineFeedback(__instance))
            {
                judgeObj.Initialize(NoteJudge.ETiming.Critical, 0f, t.Field("BreakFlag").GetValue<bool>());
            }

            t.Field("DispJudge").SetValue(true);
            t.Field("EndFlag").SetValue(true);
            t.Method("SetPlayResult").GetValue(); // SetResult(NoteIndex, kind, Critical) → CP 计分
            __instance.gameObject.SetActive(false);
            __instance.transform.SetParent(parent, false);
        }
        catch
        {
        }
    }

    /// <summary>地雷 hold/touchhold 视作 autoplay（自动按住到尾部，不碰=按住到底判 CP）。</summary>
    public static bool IsAutoPlayOrMineHold(object instance)
    {
        if (instance is MineHoldNote or MineBreakHoldNote or MineTouchHoldC)
        {
            return true;
        }

        return GameManager.IsAutoPlay();
    }

    /// <summary>地雷 hold 的自动判定值（AutoJudge 在非 autoplay 模式返回 TooFast，不能用）。</summary>
    public static NoteJudge.ETiming MineHoldAutoJudge(object instance)
    {
        if (instance is MineHoldNote or MineBreakHoldNote or MineTouchHoldC) return NoteJudge.ETiming.Critical;
        return GameManager.AutoJudge();
    }

    // A head hit follows MNTAP (Miss); body contact only caps the final result at Good.
    // Keep JudgeResult pending until the native tail settlement for a body contact.
    private static readonly Dictionary<NoteBase, NoteJudge.ETiming> MineHoldPenalties = new();

    /// <summary>地雷 Hold 自动避开头部、持续到尾部；头部有效按下按 MNTAP 处理，
    /// 持续阶段误碰只记录 Good，由原生尾部流程统一显示和计分。</summary>
    [HarmonyPatch]
    public static class MineHoldAutoPlayTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(HoldNote), "NoteCheck"),
                AccessTools.Method(typeof(BreakHoldNote), "NoteCheck"),
                AccessTools.Method(typeof(TouchHoldC), "NoteCheck"),
                AccessTools.Method(typeof(HoldNote), "JudgeTotalResult"),
                AccessTools.Method(typeof(BreakHoldNote), "JudgeTotalResult"),
                AccessTools.Method(typeof(TouchHoldC), "JudgeTotalResult"),
                AccessTools.Method(typeof(HoldNote), "SetAutoPlayJudge"),
                AccessTools.Method(typeof(TouchHoldC), "SetAutoPlayJudge"),
                // ⚠️ BreakHoldNote 的基类是 NoteBase（不是 HoldNote！），它自己 override 了
                // SetAutoPlayJudge——漏掉会导致绝赞地雷 hold 的头自动判定不生效（历史 bug #28）。
                AccessTools.Method(typeof(BreakHoldNote), "SetAutoPlayJudge"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var inst in instructions)
            {
                if (inst.opcode == OpCodes.Call && GetMethodOperandName(inst.operand) == "IsAutoPlay")
                {
                    // call IsAutoPlay() → ldarg.0; call IsAutoPlayOrMineHold(object)
                    // ⚠️ 这里有意匹配所有 IsAutoPlay 调用（包括静态 GameManager.IsAutoPlay）：
                    // MineHold 的判定整体按 autoplay 处理（不碰 = 头自动 CP + 身体自动按满 +
                    // 尾部自动 CP），原版 NoteCheck 的手动按钮检测分支因此关闭，玩家触碰改由
                    // MineHoldTouchCheck（NoteCheck prefix）独立侦测头部 Miss / 持续阶段 Good。
                    // 曾误以为静态调用是 bug 而跳过（修 1），结果"自动按住"效果消失且
                    // JudgeTotalResult 走回原版 JudgeHoldTotal 分支（用户实测回归，2026-08-27）。
                    // ⚠️ 被替换指令可能带分支标签/异常块边界，必须转移到第一条替换指令
                    // （历史 bug #17/#27：否则 "Label #N is not marked" → patch 全挂）。
                    var ldarg = new CodeInstruction(OpCodes.Ldarg_0);
                    ldarg.labels.AddRange(inst.labels);
                    ldarg.blocks.AddRange(inst.blocks);
                    yield return ldarg;
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CustomNoteTypes), nameof(IsAutoPlayOrMineHold)));
                    continue;
                }

                if (inst.opcode == OpCodes.Call && GetMethodOperandName(inst.operand) == "AutoJudge")
                {
                    // call AutoJudge() → ldarg.0; call MineHoldAutoJudge(object)
                    var ldarg = new CodeInstruction(OpCodes.Ldarg_0);
                    ldarg.labels.AddRange(inst.labels);
                    ldarg.blocks.AddRange(inst.blocks);
                    yield return ldarg;
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CustomNoteTypes), nameof(MineHoldAutoJudge)));
                    continue;
                }

                yield return inst;
            }
        }

        private static string GetMethodOperandName(object operand)
        {
            if (operand is MethodInfo mi) return mi.Name;
            var prop = operand?.GetType().GetProperty("Name");
            return prop?.GetValue(operand) as string;
        }
    }

    /// <summary>缓存当前谱面 NoteData 列表（loadMa2Main 后）。地雷 hold 触碰检测需要
    /// 判定同轨道普通 tap 是否在判定窗口内（点击优先给 tap，地雷 hold 不 Miss）。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NotesReader), "loadMa2Main")]
    public static void CacheNoteListPostfix(NotesReader __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        try
        {
            _activeNoteList = __instance.GetNoteList();
        }
        catch
        {
        }
    }

    // Record tail-frame contact before native settlement. Only a head hit ends
    // immediately; body contact must keep running the native Hold lifecycle.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(HoldNote), "NoteCheck")]
    public static bool MineHoldNoteCheckTouch(HoldNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        return !MineHoldTouchCheck(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(BreakHoldNote), "NoteCheck")]
    public static bool MineBreakHoldNoteCheckTouch(BreakHoldNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        return !MineHoldTouchCheck(__instance);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(TouchHoldC), "NoteCheck")]
    public static bool MineTouchHoldNoteCheckTouch(TouchHoldC __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        return !MineHoldTouchCheck(__instance);
    }

    /// <summary>头前使用 MNTAP 的按下窗口；持续阶段误碰只降为 Good，返回 true 表示已收尾。</summary>
    private static bool MineHoldTouchCheck(NoteBase note)
    {
        if (IsFakeNoteOwner(note)) return false;
        try
        {
            if (note is not (MineHoldNote or MineBreakHoldNote or MineTouchHoldC)) return false;
            var mineBehaviour = note.GetComponent<MineNoteBehaviour>();
            if (mineBehaviour != null && !mineBehaviour.IsMine) return false;
            if (MineHoldPenalties.ContainsKey(note) && note.GetJudgeResult() != NoteJudge.ETiming.End) return true;
            if (note.GetJudgeResult() != NoteJudge.ETiming.End) return false;

            var now = NotesManager.GetCurrentMsec();
            var traverse = Traverse.Create(note);
            var tail = traverse.Field("TailMsec").GetValue<float>();
            // AppearMsec is the judgment time; StartMsec is only the visual spawn time.
            var head = traverse.Field("AppearMsec").GetValue<float>();
            if (now > tail) return false;
            // Match MineAutoJudgePostfix's MNTAP cutoff, independent of note speed.
            var beforeHead = now < head - 4.166667f;
            bool down, held;
            var button = (InputManager.ButtonSetting)traverse.Property("ButtonId").GetValue<int>();
            if (note is TouchHoldC)
            {
                down = traverse.Method("IsHoldTriggerDown").GetValue<bool>();
                held = !beforeHead && traverse.Method("IsHoldTriggerPush").GetValue<bool>();
            }
            else
            {
                down = PlayableNoteInput.InGameButtonDown(note.MonitorId, button, note) ||
                       PlayableNoteInput.InGameTouchPanelAreaDown(note.MonitorId, button, note);
                held = !beforeHead && (PlayableNoteInput.GetButtonPush(note.MonitorId, button, note) ||
                       PlayableNoteInput.GetTouchPanelAreaPush(note.MonitorId, button, note));
            }
            if (!down && !held) return false;

            // Preserve same-lane ordinary Tap ±150ms priority, including D-zone bindings.
            if (note is not TouchHoldC && HasNormalTapInJudgeWindow((int)button, now, TryDZoneArea(note, out _)))
                return false;

            if (beforeHead)
            {
                // Just holding before the window is not a MNTAP hit. A fresh down
                // must also satisfy the native timing offset, note order and consumption.
                var offset = Singleton<GamePlayManager>.Instance.GetGameScore(note.MonitorId).UserOption.GetJudgeTimingFrame();
                if (now - offset * 16.666666f < head + note.GetJudgeStartMsec()) return false;
                if (!traverse.Method("IsJudgeNote").GetValue<bool>()) return false;
                var used = note is TouchHoldC
                    ? traverse.Method("IsUsedThisFrameArea").GetValue<bool>()
                    : PlayableNoteInput.IsUsedThisFrame(note.MonitorId, button, note);
                if (used) return false;
                var diff = now - head;
                var timing = NoteJudge.GetJudgeTiming(ref diff, offset,
                    traverse.Field("JudgeType").GetValue<NoteJudge.EJudgeType>());
                if (timing == NoteJudge.ETiming.End || timing == NoteJudge.ETiming.TooLate) return false;
                if (note is TouchHoldC) traverse.Method("SetUsedThisFrame").GetValue();
                else PlayableNoteInput.SetUsedThisFrame(note.MonitorId, button, note);
                MineHoldPenalties[note] = NoteJudge.ETiming.TooLate;
                traverse.Field("JudgeTimingDiffMsec").SetValue(diff);
                traverse.Field("JudgeResult").SetValue(NoteJudge.ETiming.TooLate);
                traverse.Method("EndNote").GetValue();
                return true;
            }

            // No early EndNote, no Miss and no repeated score: the native tail
            // writes its result once, then ApplyMineJudgeTiming applies this cap.
            MineHoldPenalties[note] = NoteJudge.ETiming.LateGood;
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>同轨道（startButtonPos 相同）是否有普通 tap 的判定窗口覆盖 now。
    /// 判定窗口 ±150ms（覆盖 good 判定 + 容差）。地雷/自定义音符（NoteKinds≠None）排除；
    /// 只认 tap 系（Tap/ExTap/Break/ExBreakTap）——hold/slide/star 不参与放行。</summary>
    private static bool HasNormalTapInJudgeWindow(int buttonPos, float now, bool dZone = false)
    {
        var list = _activeNoteList;
        if (list == null) return false;
        foreach (var nd in list)
        {
            // 逐元素防御：2026-08-29 用户开启 TapInHoldFix 后实测——放行检查曾抛
            // NullReferenceException（根因：NotesTypeID 自定义 op_Equality 对 null 参数不健壮，
            // `nd.type == null` 求值即 NRE；已改用 ReferenceEquals 引用比较修复）。单个坏元素
            // 跳过继续找 tap，保证放行行为不被破坏。
            try
            {
                if (nd == null || IsFakeNote(nd) || nd.startButtonPos != buttonPos) continue;
                if (DZonePositions.TryGetValue(nd, out _) != dZone) continue;
                if (NoteKinds.TryGetValue(nd.indexNote, out var kind) && kind != CustomNoteKind.None) continue;
                if (ReferenceEquals(nd.type, null)) continue;
                var t = nd.type.getEnum();
                if (t != NotesTypeID.Def.Tap && t != NotesTypeID.Def.ExTap
                    && t != NotesTypeID.Def.Break && t != NotesTypeID.Def.ExBreakTap) continue;
                var T = nd.time.msec;
                if (now >= T - 150f && now <= T + 150f) return true;
            }
            catch
            {
                // 坏元素：跳过继续找（防御）
            }
        }
        return false;
    }

    /// <summary>hold 复用时清除"被碰到"登记（池对象复用）。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(HoldNote), "Initialize")]
    public static void HoldInitClearTouched(HoldNote __instance)
    {
        MineHoldPenalties.Remove(__instance);
    }

    // BreakHoldNote derives directly from NoteBase, so HoldNote.Initialize's
    // postfix does not run for this pool. Never carry a mine hit into the next note.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakHoldNote), "Initialize")]
    public static void BreakHoldInitClearTouched(BreakHoldNote __instance)
    {
        MineHoldPenalties.Remove(__instance);
    }

    /// <summary>绝赞 touchhold（TouchBreakHoldC）：判定窗口与普通 touchhold 相同，不强制 CP；
    /// 统计由 TouchBreakHoldC.SetPlayResult override 按 BREAK 记录。</summary>

    [HarmonyPostfix]
    [HarmonyPatch(typeof(TouchHoldC), "Initialize")]
    public static void TouchHoldInitClearTouched(TouchHoldC __instance)
    {
        MineHoldPenalties.Remove(__instance);
    }

    public static NoteJudge.ETiming ApplyMineJudgeTiming(NoteJudge.ETiming timing, object note)
    {
        // 独立地雷类（ISelfJudgingMineNote）自己负责反转（override Judge / JudgeToolate），
        // 这里必须放行，否则双重反转。Hold/Slide 等非自反转类继续由 transpiler 反转。
        if (note is ISelfJudgingMineNote) return timing;

        if (note is NoteBase hold && MineHoldPenalties.TryGetValue(hold, out var penalty))
            return penalty;

        // Untouched mine Holds settle as native auto-CP. Do not invert that CP.
        if (note is MineHoldNote or MineBreakHoldNote or MineTouchHoldC) return timing;

        var behaviour = (note as Component)?.GetComponent<MineNoteBehaviour>();
        // MineSlideRoot/MineSlideFan 只可能来自地雷池——即使 behaviour 没挂上（NoteKinds 按
        // indexNote 查不到导致 EnsureMineBehaviour 兜底失败），也按地雷处理（历史 bug #22）。
        if ((behaviour == null || !behaviour.IsMine) && note is not (MineSlideRoot or MineSlideFan)) return timing;

        if (!_loggedMineInversion)
        {
            _loggedMineInversion = true;
            MelonLogger.Msg($"[CustomNoteType] Mine judge inversion active (first mine: note {(behaviour != null ? behaviour.NoteIndex : -1)})");
        }

        // slide 特判（用户 2026-08 最终规则）：SlideRoot.Judge 只在全部轨道被划掉时被调用
        // （NoteCheck 里 hitIndex >= count 才调），JudgeToolate 反之（没划完/星星滑到末尾）。
        //   划完（提前划掉全部轨道，打中）→ Miss(TooLate 14)
        //   没划完（星星跟着轨道滑到最后）→ Critical Perfect(7)
        if (note is SlideRoot slide)
        {
            var completed = IsMineSlideCompleted(slide);
            return completed ? NoteJudge.ETiming.TooLate : NoteJudge.ETiming.Critical;
        }

        // 地雷判定（标准规则，2026-08-26 用户要求四类统一）：任何有效触碰
        // （原版 Critical/Perfect/Great/Good/TooFast）-> Miss(TooLate)；没碰到（TooLate/End）-> Critical。
        // 适用于非自反转的地雷：HoldNote/BreakHoldNote/TouchHoldC 的 JudgeTotalResult（stfld JudgeResult）。
        // ⚠️ 头判定（JudgeHoldHead/JudgeToolate override，写 JudgeHeadResult）刻意不拦截：
        //   JudgeHoldTotal 的输入需要原版头判定才能算出正确总判定（没按 → 头 TooLate → 总 TooLate →
        //   这里反转成 CP；按头 → 头 CP/Good → 总 Good/Great → 反转成 Miss）。头判定显示/SE 保持原版
        //   （玩家按下时刻的反馈），最终炸/不炸由总判定决定。
        return MineJudgeHelper.InvertStandard(timing);
    }

    /// <summary>slide 是否"划完"：SlideRoot 看 _hitIndex >= _hitAreaList.Count - 1；
    /// SlideFan（3 条线）要求每条线都 >= 各自的 Count - 1（与原版 grace 判定条一致）。</summary>
    private static bool IsMineSlideCompleted(SlideRoot slide)
    {
        try
        {
            var traverse = Traverse.Create(slide);
            if (slide is SlideFan)
            {
                var hitIndexes = traverse.Field("_hitIndex").GetValue<int[]>();
                var areaLists = traverse.Field("_hitAreaList").GetValue<System.Collections.IEnumerable>();
                if (hitIndexes == null || areaLists == null) return false;
                var i = 0;
                foreach (var list in areaLists)
                {
                    var count = (list as System.Collections.ICollection)?.Count ?? 0;
                    if (i >= hitIndexes.Length || hitIndexes[i] < count - 1) return false;
                    i++;
                }

                return i >= 3;
            }
            else
            {
                var hitIndex = traverse.Field("_hitIndex").GetValue<int>();
                var areas = traverse.Field("_hitAreaList").GetValue<System.Collections.ICollection>();
                return areas != null && hitIndex >= areas.Count - 1;
            }
        }
        catch
        {
            return false;
        }
    }

    [HarmonyPatch]
    public static class MineJudgeInversion
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(NoteBase), "Judge"),
                AccessTools.Method(typeof(NoteBase), "JudgeToolate"),
                AccessTools.Method(typeof(TouchNoteB), "Judge"),
                AccessTools.Method(typeof(SlideRoot), "Judge"),
                AccessTools.Method(typeof(SlideRoot), "JudgeToolate"),
                AccessTools.Method(typeof(HoldNote), "JudgeTotalResult"),
                AccessTools.Method(typeof(BreakHoldNote), "JudgeTotalResult"),
                AccessTools.Method(typeof(TouchHoldC), "JudgeTotalResult"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var inst in instructions)
            {
                if (inst.opcode == OpCodes.Stfld && IsJudgeResultField(inst.operand))
                {
                    // ⚠️ 后置反转（历史 bug #23）：原版 SlideRoot.Judge 里有 br.s 分支直接跳到
                    // stfld JudgeResult（GetSlideJudgeTiming 路径和 autoplay 路径都是），
                    // 前置插入（ldarg.0; call）会被分支跳过 → 原值直写 → 显示 good/great/cp。
                    // 保留原 stfld（分支目标依然有效），在它后面插：
                    //   ldarg.0; ldstr "JudgeResult"; call ApplyMineJudgePostWrite(object, string)
                    // stfld 后栈为空，ldarg.0+ldstr+call 不破坏栈形，且所有路径必然经过。
                    yield return inst;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Ldstr, GetJudgeResultFieldName(inst.operand));
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(MineJudgeInversion), nameof(ApplyMineJudgePostWrite)));
                    continue;
                }

                yield return inst;
            }
        }

        /// <summary>后置反转：读取刚写入的判定值 → ApplyMineJudgeTiming 决定反转结果 → 写回。
        /// 非地雷 / ISelfJudgingMineNote 返回原值不写（自反转类自己负责）。</summary>
        public static void ApplyMineJudgePostWrite(object note, string fieldName)
        {
            try
            {
                if (note == null || fieldName == null) return;
                var traverse = Traverse.Create(note);
                var timing = traverse.Field(fieldName).GetValue<NoteJudge.ETiming>();
                var result = ApplyMineJudgeTiming(timing, note);
                if (result != timing)
                {
                    traverse.Field(fieldName).SetValue(result);
                }
            }
            catch
            {
            }
        }

        private static string GetJudgeResultFieldName(object operand)
        {
            if (operand is FieldInfo field)
            {
                return field.Name;
            }

            var nameProp = operand?.GetType().GetProperty("Name");
            return nameProp?.GetValue(operand) as string;
        }
    }

    /// <summary>判断 stfld 的 operand 是否是判定字段（Harmony 可能给 FieldInfo 或 Mono.Cecil FieldReference）。</summary>
    private static bool IsJudgeResultField(object operand)
    {
        if (operand is FieldInfo field)
        {
            return field.Name == "JudgeResult" || field.Name == "JudgeHeadResult";
        }

        // Mono.Cecil FieldReference（Harmony 的 CodeInstruction 可能持有 Cecil 引用）。
        var nameProp = operand?.GetType().GetProperty("Name");
        var name = nameProp?.GetValue(operand) as string;
        return name == "JudgeResult" || name == "JudgeHeadResult";
    }

    /// <summary>NoteCheck 里"滑完全部轨道但略迟"宽限写入的替换：
    /// 原版把 TooLate 升成 LateGood(13)——地雷划完 = Miss，宽限写入的地雷结果保持 Miss(TooLate)，
    /// 非地雷保持 13。grace 只会在"划完且原判定 TooLate"时触发，与新规则一致。</summary>
    public static void ApplyMineSlideGraceJudge(SlideRoot slide)
    {
        // Mine* 类按类兜底（历史 bug #22），不依赖 behaviour。
        var isMine = slide is MineSlideRoot or MineSlideFan ||
                     slide?.GetComponent<MineNoteBehaviour>()?.IsMine == true;
        var timing = isMine ? NoteJudge.ETiming.TooLate : (NoteJudge.ETiming)13;
        Traverse.Create(slide).Field("JudgeResult").SetValue(timing);
    }

    [HarmonyPatch]
    public static class SlideGraceJudgeTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(SlideRoot), "NoteCheck"),
                AccessTools.Method(typeof(SlideFan), "NoteCheck"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var inst = list[i];
                // 原版"滑完全部轨道但最后一下略迟 → LateGood(13)"的宽限写入，
                // 序列是 ldarg.0; ldc.i4.s 13; stfld JudgeResult。
                // 把 ldc+stfld 换成 call ApplyMineSlideGraceJudge（前面的 ldarg.0 保留作 this）。
                if (i > 0 && list[i - 1].opcode == OpCodes.Ldarg_0 &&
                    (inst.opcode == OpCodes.Ldc_I4_S || inst.opcode == OpCodes.Ldc_I4) &&
                    Convert.ToInt32(inst.operand) == 13 &&
                    i + 1 < list.Count && list[i + 1].opcode == OpCodes.Stfld &&
                    IsJudgeResultField(list[i + 1].operand))
                {
                    var call = new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CustomNoteTypes), nameof(ApplyMineSlideGraceJudge)));
                    // 被替换指令可能带分支标签/异常块，必须转移到 call 上（历史 bug #17/#27）。
                    call.labels.AddRange(inst.labels);
                    call.blocks.AddRange(inst.blocks);
                    if (list[i + 1].labels.Count > 0) call.labels.AddRange(list[i + 1].labels);
                    if (list[i + 1].blocks.Count > 0) call.blocks.AddRange(list[i + 1].blocks);
                    yield return call;
                    i++; // 跳过紧随其后的 stfld
                    continue;
                }

                yield return inst;
            }
        }
    }

    /// <summary>slide 收尾等待条件（原版：lastWaitTime <= 0 || JudgeResult == TooLate(14)）的地雷扩展：
    /// 地雷 slide 没划完时 JudgeToolate 的反转结果是 Critical(7)，原版条件不认 → 收尾会多等
    /// lastWaitTime 耗尽才显示 CP（"星星滑到底部要过一会才判 CP"）。补上 mine && Critical → 立即收尾。</summary>
    public static bool IsMineSlideEndWaitDone(SlideRoot slide)
    {
        try
        {
            if (slide == null) return true;
            var wait = Traverse.Create(slide).Field("lastWaitTime").GetValue<float>();
            if (wait <= 0f) return true;
            var judge = slide.GetJudgeResult();
            if (judge == NoteJudge.ETiming.TooLate) return true;
            if (judge == NoteJudge.ETiming.Critical && slide is (MineSlideRoot or MineSlideFan)) return true;
            return false;
        }
        catch
        {
            return true;
        }
    }

    [HarmonyPatch]
    public static class SlideEndWaitTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(SlideRoot), "NoteCheck"),
                AccessTools.Method(typeof(SlideFan), "NoteCheck"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var list = instructions.ToList();
            for (var i = 0; i < list.Count; i++)
            {
                var inst = list[i];
                // 原版收尾等待条件的后半段：call GetJudgeResult; ldc.i4.s 14; ceq
                // → ldarg.0; call IsMineSlideEndWaitDone(object)
                if ((inst.opcode == OpCodes.Call || inst.opcode == OpCodes.Callvirt) &&
                    GetMethodOperandName(inst.operand) == "GetJudgeResult" &&
                    i + 2 < list.Count &&
                    (list[i + 1].opcode == OpCodes.Ldc_I4_S || list[i + 1].opcode == OpCodes.Ldc_I4) &&
                    Convert.ToInt32(list[i + 1].operand) == 14 &&
                    (list[i + 2].opcode == OpCodes.Ceq ||
                     ((list[i + 2].opcode == OpCodes.Bne_Un || list[i + 2].opcode == OpCodes.Bne_Un_S) &&
                      i >= 4 && list[i - 4].opcode == OpCodes.Ldfld &&
                      GetMethodOperandName(list[i - 4].operand) == "lastWaitTime")))
                {
                    var call = new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CustomNoteTypes), nameof(IsMineSlideEndWaitDone)));
                    // 被替换指令可能带分支标签/异常块，必须转移（历史 bug #17/#27）。
                    call.labels.AddRange(inst.labels);
                    call.blocks.AddRange(inst.blocks);
                    if (list[i + 1].labels.Count > 0) call.labels.AddRange(list[i + 1].labels);
                    if (list[i + 1].blocks.Count > 0) call.blocks.AddRange(list[i + 1].blocks);
                    if (list[i + 2].opcode == OpCodes.Ceq)
                    {
                        if (list[i + 2].labels.Count > 0) call.labels.AddRange(list[i + 2].labels);
                        if (list[i + 2].blocks.Count > 0) call.blocks.AddRange(list[i + 2].blocks);
                    }
                    // The preceding ldarg.0 already supplies the slide. The
                    // 1.70 build branches directly rather than emitting ceq.
                    yield return call;
                    if (list[i + 2].opcode != OpCodes.Ceq)
                        yield return new CodeInstruction(list[i + 2]) { opcode = OpCodes.Brfalse };
                    i += 2; // 跳过 ldc + ceq
                    continue;
                }

                yield return inst;
            }
        }

        private static string GetMethodOperandName(object operand)
        {
            if (operand is MethodInfo mi) return mi.Name;
            var prop = operand?.GetType().GetProperty("Name");
            return prop?.GetValue(operand) as string;
        }
    }

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

    private static void ApplyMineArrowTexture(SpriteRenderer sr)
    {
        if (sr == null || !MineTextures.TryGetValue("slide_mine", out var texture)) return;
        sr.sprite = CreateSpriteFromTexture("slide_mine", texture, sr.sprite);
    }

    private static void ApplyMineBreakArrowTexture(BreakSlide bs)
    {
        if (bs == null) return;
        if (bs.SpriteRender != null && MineTextures.TryGetValue("slide_break_mine", out var texture))
        {
            bs.SpriteRender.sprite = CreateSpriteFromTexture("slide_break_mine", texture, bs.SpriteRender.sprite);
        }

        // 光效层（EffectSprite，private）：贴地雷光效 + 标记（SetSprite 会重置，postfix 重贴）。
        var effect = Traverse.Create(bs).Field("EffectSprite").GetValue<SpriteRenderer>();
        if (effect != null && MineTextures.TryGetValue("slide_break_eff_mine", out var effTexture))
        {
            MineBreakSlides.Add(bs);
            effect.sprite = CreateSpriteFromTexture("slide_break_eff_mine", effTexture, effect.sprite);
        }
    }

    /// <summary>
    /// 把 slide 的轨道箭头重新贴成地雷贴图。
    /// 原因：SlideRoot.SetEach（Initialize 内部调用）会给箭头赋原版 NormalSlide/EachSlide sprite，
    /// 覆盖地雷箭头池的贴图，所以 MineSlideRoot.SetEach override 里要重新贴。
    /// </summary>
    public static void ApplyMineArrowTextures(SlideRoot slide)
    {
        if (slide == null) return;

        var spriteRenders = Traverse.Create(slide).Field("_spriteRenders").GetValue<List<SpriteRenderer>>();
        if (spriteRenders != null)
        {
            foreach (var sr in spriteRenders)
            {
                ApplyMineArrowTexture(sr);
            }
        }

        var breakRenders = Traverse.Create(slide).Field("_breakSpriteRenders").GetValue<List<BreakSlide>>();
        if (breakRenders != null)
        {
            foreach (var bs in breakRenders)
            {
                ApplyMineBreakArrowTexture(bs);
            }
        }
    }

    /// <summary>
    /// 给 slide 的移动星（SlideRoot 内部 _starNote / _breakStarNote）贴地雷星贴图。
    /// 只精准贴这两个星对象（不遍历子物体），普通星用 star_mine、break 星用 star_break_mine。
    /// </summary>
    public static void ApplyMineSlideStarTextures(SlideRoot slide)
    {
        if (slide == null) return;

        var traverse = Traverse.Create(slide);
        var star = traverse.Field("_starNote").GetValue<GameObject>();
        if (star != null)
        {
            var sr = star.GetComponent<SpriteRenderer>();
            if (sr != null) ApplyMineStarTexture(sr, "star_mine");
        }

        var breakStar = traverse.Field("_breakStarNote").GetValue<GameObject>();
        if (breakStar != null)
        {
            var bs = breakStar.GetComponent<BreakStarNote>();
            var sr = bs != null ? bs.GetSpriteRender() : breakStar.GetComponent<SpriteRenderer>();
            if (sr != null) ApplyMineStarTexture(sr, "star_break_mine");
            // 内部 break 星的 EffectSprite（绝赞光效层）也地雷化——SetSlideStar 在 base.Initialize
            // 里已执行（此时还没登记），这里直接替换；后续 SetSlideStar 由 postfix 兜底。
            if (bs != null) MineifyBreakStarEffect(bs);
        }
    }

    private static void ApplyMineStarTexture(SpriteRenderer sr, string textureKey)
    {
        if (sr == null || !MineTextures.TryGetValue(textureKey, out var texture)) return;
        sr.sprite = CreateSpriteFromTexture(textureKey, texture, sr.sprite);
    }

    /// <summary>
    /// 给 fan slide（Wi-Fi 扇形滑）贴地雷贴图（用户提供的新贴图）：
    ///   - _spriteLines（11 组 × 2，分 LR）→ slide_fun_mine_00..10（同组 i/2 同号）
    ///   - _effectSprites（11 组 × 2，光效）→ slide_fun_eff_mine_00..10（同组 i/2 同号）
    ///   - _spriteStars / _baseSpriteStars（各 3）星 → star_mine / star_break_mine（按 BreakFlag）
    /// 注意：原版线是白色贴图 + _laneColor 染色（UpdateAlpha 每帧 set_color），
    /// 地雷线是彩色贴图，MineSlideFan 已 override UpdateAlpha 把 RGB 重置为白（保留 alpha 动画）。
    /// </summary>
    public static void ApplyMineFanSlideTextures(SlideFan fan)
    {
        if (fan == null) return;

        var traverse = Traverse.Create(fan);

        var lines = traverse.Field("_spriteLines").GetValue<SpriteRenderer[]>();
        if (lines != null)
        {
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i] == null) continue;
                // 素材文件名是两位数字：slide_fun_mine_00..10
                var key = "slide_fun_mine_" + (i / 2 % 11).ToString("D2");
                if (!MineTextures.TryGetValue(key, out var texture))
                {
                    if (!_loggedMissingFanTexture)
                    {
                        _loggedMissingFanTexture = true;
                        MelonLogger.Warning($"[CustomNoteType] Missing fan textures: '{key}'（请确认 Sinmai-Alpha/CustomNoteTypes 里有 slide_fun_mine_00..10.png）");
                    }

                    continue;
                }

                lines[i].sprite = CreateSpriteFromTexture(key, texture, lines[i].sprite);
            }
        }

        var effects = traverse.Field("_effectSprites").GetValue<SpriteRenderer[]>();
        if (effects != null)
        {
            for (var i = 0; i < effects.Length; i++)
            {
                if (effects[i] == null) continue;
                var key = "slide_fun_eff_mine_" + (i / 2 % 11).ToString("D2");
                if (MineTextures.TryGetValue(key, out var texture))
                {
                    effects[i].sprite = CreateSpriteFromTexture(key, texture, effects[i].sprite);
                }
            }
        }

        var isBreak = traverse.Field("BreakFlag").GetValue<bool>();
        var starKey = isBreak ? "star_break_mine" : "star_mine";
        var stars = traverse.Field("_spriteStars").GetValue<SpriteRenderer[]>();
        if (stars != null)
        {
            foreach (var sr in stars)
            {
                ApplyMineStarTexture(sr, starKey);
            }
        }

        var baseStars = traverse.Field("_baseSpriteStars").GetValue<SpriteRenderer[]>();
        if (baseStars != null)
        {
            foreach (var sr in baseStars)
            {
                ApplyMineStarTexture(sr, starKey);
            }
        }

        // fan 的星（SlideLaneStar = StarNote/BreakStarNote，继承 NoteBase）自带 NoteGuide 提示圈
        // （蓝色外框 + 判定前橙色闪光）——同样地雷化。
        ApplyFanStarGuideTextures(traverse);

        // fan 的 break 星（_breakStarObjs，原版 BreakStarNote 实例）的 EffectSprite（绝赞光效层）
        // 也要地雷化，否则单星判定时闪橙光。SetSlideStar 在 base.Initialize 里已执行
        // （此时还没登记），这里直接替换；后续 SetSlideStar 由 postfix 兜底。
        foreach (var field in new[] { "_starObjs", "_breakStarObjs", "_baseStarObjs" })
        {
            var objs = traverse.Field(field).GetValue<GameObject[]>();
            if (objs == null) continue;
            foreach (var go in objs)
            {
                if (go == null) continue;
                var breakStar = go.GetComponent<BreakStarNote>();
                if (breakStar != null) MineifyBreakStarEffect(breakStar);
            }
        }
    }

    // 把 fan 星对象（_starObjs / _breakStarObjs / _baseStarObjs）上的 NoteGuide 提示圈地雷化。
    private static void ApplyFanStarGuideTextures(Traverse fanTraverse)
    {
        foreach (var field in new[] { "_starObjs", "_breakStarObjs", "_baseStarObjs" })
        {
            var objs = fanTraverse.Field(field).GetValue<GameObject[]>();
            if (objs == null) continue;
            foreach (var go in objs)
            {
                if (go == null) continue;
                var noteBase = go.GetComponent<NoteBase>();
                if (noteBase != null)
                {
                    ApplyMineGuideTexture(noteBase);
                }
            }
        }
    }

    // 原版 CreateNotePool 会给池对象设置 ParentTransform（EndNote 回收时用它做父级）。
    private static void TrySetParentTransform(Component note, Transform parent)
    {
        var prop = note.GetType().GetProperty("ParentTransform");
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(note, parent);
            return;
        }

        var field = note.GetType().GetField("<ParentTransform>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        field?.SetValue(note, parent);
    }

    // 地雷 Note 取池：当前 RegistNote 需要该字段时返回地雷池/绝赞池，否则返回原版池。
    private static object GetMinePoolList(GameCtrl instance, string fieldName, System.Type elementType)
    {
        if (_activeMineFields.Contains(fieldName))
        {
            if (MinePools.TryGetValue(instance, out var pools) && pools.TryGetValue(fieldName, out var pool))
            {
                return pool;
            }

            // 防御：地雷池缺失时回退原版池（与 touchbreak/touchstar 分支一致）——
            // 返回空 List 会让 RegistNote 遍历 0 次直接返回 false → 地雷音符全部
            // 静默不注册（音符凭空消失且无日志）。
            return Traverse.Create(instance).Field(fieldName).GetValue();
        }

        if (_activeTouchBreakFields.Contains(fieldName))
        {
            // 绝赞 touch：优先用同类型专用池；创建失败时仍可回退原版池。
            if (TouchBreakPools.TryGetValue(instance, out var criticalPools) &&
                criticalPools.TryGetValue(fieldName, out var criticalPool))
            {
                return criticalPool;
            }

            return Traverse.Create(instance).Field(fieldName).GetValue();
        }

        if (_activeTouchStarFields.Contains(fieldName))
        {
            // TouchStar：有独立池用独立池；没有回退原版池。
            if (TouchStarPools.TryGetValue(instance, out var touchStarPools) &&
                touchStarPools.TryGetValue(fieldName, out var touchStarPool))
            {
                return touchStarPool;
            }

            return Traverse.Create(instance).Field(fieldName).GetValue();
        }

        if (_activeMineTouchStarFields.Contains(fieldName))
        {
            // 地雷 TouchStar（MNSTP）：有独立池用独立池；没有回退原版池。
            if (MineTouchStarPools.TryGetValue(instance, out var mineTouchStarPools) &&
                mineTouchStarPools.TryGetValue(fieldName, out var mineTouchStarPool))
            {
                return mineTouchStarPool;
            }

            return Traverse.Create(instance).Field(fieldName).GetValue();
        }

        return Traverse.Create(instance).Field(fieldName).GetValue();
    }

    // 注意：transpiler 替换 ldfld 时栈上是 [instance, "字段名"]（ldstr 在 call 之前），
    // 所以这些访问器必须接收 (GameCtrl, string) 两个参数，否则字段名字符串会被当作
    // GameCtrl 弹出导致 InvalidCastException，RegistNote 抛异常、音符全部注册失败。
    private static List<TapNote> GetTapObjectList(GameCtrl instance, string fieldName) => (List<TapNote>)GetMinePoolList(instance, fieldName, typeof(TapNote));
    private static List<HoldNote> GetHoldObjectList(GameCtrl instance, string fieldName) => (List<HoldNote>)GetMinePoolList(instance, fieldName, typeof(HoldNote));
    private static List<BreakHoldNote> GetBreakHoldObjectList(GameCtrl instance, string fieldName) => (List<BreakHoldNote>)GetMinePoolList(instance, fieldName, typeof(BreakHoldNote));
    private static List<StarNote> GetStarObjectList(GameCtrl instance, string fieldName) => (List<StarNote>)GetMinePoolList(instance, fieldName, typeof(StarNote));
    private static List<BreakStarNote> GetBreakStarObjectList(GameCtrl instance, string fieldName) => (List<BreakStarNote>)GetMinePoolList(instance, fieldName, typeof(BreakStarNote));
    private static List<BreakNote> GetBreakObjectList(GameCtrl instance, string fieldName) => (List<BreakNote>)GetMinePoolList(instance, fieldName, typeof(BreakNote));
    private static List<TouchNoteB> GetTouchBObjectList(GameCtrl instance, string fieldName) => (List<TouchNoteB>)GetMinePoolList(instance, fieldName, typeof(TouchNoteB));
    private static List<TouchNoteC> GetTouchCTapObjectList(GameCtrl instance, string fieldName) => (List<TouchNoteC>)GetMinePoolList(instance, fieldName, typeof(TouchNoteC));
    private static List<TouchHoldC> GetTouchBHoldObjectList(GameCtrl instance, string fieldName) => (List<TouchHoldC>)GetMinePoolList(instance, fieldName, typeof(TouchHoldC));
    private static List<TouchHoldC> GetTouchCHoldObjectList(GameCtrl instance, string fieldName) => (List<TouchHoldC>)GetMinePoolList(instance, fieldName, typeof(TouchHoldC));
    private static List<SlideRoot> GetSlideObjectList(GameCtrl instance, string fieldName) => (List<SlideRoot>)GetMinePoolList(instance, fieldName, typeof(SlideRoot));
    private static List<SlideFan> GetFanSlideObjectList(GameCtrl instance, string fieldName) => (List<SlideFan>)GetMinePoolList(instance, fieldName, typeof(SlideFan));
    private static List<SpriteRenderer> GetArrowObjectList(GameCtrl instance, string fieldName) => (List<SpriteRenderer>)GetMinePoolList(instance, fieldName, typeof(SpriteRenderer));
    private static List<BreakSlide> GetBreakArrowObjectList(GameCtrl instance, string fieldName) => (List<BreakSlide>)GetMinePoolList(instance, fieldName, typeof(BreakSlide));

    // note 类型（重定向后的基础类型）-> 该类型注册时读取的池字段。
    private static HashSet<string> GetMineFieldsForNoteType(NotesTypeID.Def type)
    {
        switch (type)
        {
            case NotesTypeID.Def.Tap:
            case NotesTypeID.Def.ExTap:
                return new HashSet<string> { "_tapObjectList" };
            case NotesTypeID.Def.Hold:
            case NotesTypeID.Def.ExHold:
                return new HashSet<string> { "_holdObjectList" };
            case NotesTypeID.Def.BreakHold:
            case NotesTypeID.Def.ExBreakHold:
                return new HashSet<string> { "_breakHoldObjectList" };
            case NotesTypeID.Def.Star:
            case NotesTypeID.Def.ExStar:
                return new HashSet<string> { "_starObjectList" };
            case NotesTypeID.Def.BreakStar:
            case NotesTypeID.Def.ExBreakStar:
                return new HashSet<string> { "_breakStarObjectList" };
            case NotesTypeID.Def.Break:
            case NotesTypeID.Def.ExBreakTap:
                return new HashSet<string> { "_breakObjectList" };
            case NotesTypeID.Def.Slide:
            case NotesTypeID.Def.BreakSlide:
            case NotesTypeID.Def.ExSlide:
            case NotesTypeID.Def.ExBreakSlide:
            case NotesTypeID.Def.ConnectSlide:
                return new HashSet<string> { "_slideObjectList", "_fanSlideObjectList", "_arrowObjectList", "_breakArrowObjectList" };
            case NotesTypeID.Def.TouchTap:
                return new HashSet<string> { "_touchBObjectList", "_touchCTapObjectList" };
            case NotesTypeID.Def.TouchHold:
                return new HashSet<string> { "_touchBHoldObjectList", "_touchCHoldObjectList" };
            default:
                return new HashSet<string>();
        }
    }

    [HarmonyTranspiler]
    [HarmonyPatch(typeof(GameCtrl), "RegistNote")]
    public static IEnumerable<CodeInstruction> RegistNoteTranspiler(IEnumerable<CodeInstruction> instructions)
    {
        foreach (var inst in instructions)
        {
            var fieldName = GetLoadedMinePoolFieldName(inst);
            if (fieldName != null && MinePoolGetters.TryGetValue(fieldName, out var getter))
            {
                // ldfld 前栈上是 GameCtrl 实例；改为 ldstr 字段名 + call 访问器，栈形不变。
                // 和 HoldOnMineTranspiler 同理：被替换的 ldfld 若带分支标签/异常块，必须转移到
                // 第一条替换指令，否则 DMD 编译报 "Label #N is not marked"。
                var ldstr = new CodeInstruction(OpCodes.Ldstr, fieldName);
                ldstr.labels.AddRange(inst.labels);
                ldstr.blocks.AddRange(inst.blocks);
                yield return ldstr;
                yield return new CodeInstruction(OpCodes.Call, getter);
                continue;
            }

            yield return inst;
        }
    }

    /// <summary>
    /// RegistNote 的箭头分配分支只识别 getEnum()==5（普通箭头）和 ==14（break 箭头）。
    /// 当前自定义 slide 只注册 NMSSS(Slide=5) 和 BRSSS(BreakSlide=14)，正好匹配原版分支，
    /// 无需扩展；若未来恢复 EXSSS(15)/BXSSS(16)/CNSSS(18) 注册，需重新引入族判断 transpiler。
    /// </summary>

    private static string GetLoadedMinePoolFieldName(CodeInstruction inst)
    {
        if (inst.opcode != OpCodes.Ldfld || inst.operand == null) return null;

        if (inst.operand is FieldInfo fi)
        {
            return Array.IndexOf(MinePoolFieldNames, fi.Name) >= 0 ? fi.Name : null;
        }

        var nameProp = inst.operand.GetType().GetProperty("Name");
        var name = nameProp?.GetValue(inst.operand) as string;
        return name != null && Array.IndexOf(MinePoolFieldNames, name) >= 0 ? name : null;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GameCtrl), "RegistNote")]
    public static void RegistNotePrefix(GameCtrl __instance, object[] __args)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var note = __args?.FirstOrDefault(a => a is NoteData) as NoteData;
        // 补填 bounce 类型（BounceNoteVisualPostfix 的 fallback 用；注册期一定拿到 NoteData）。
        if (note != null && BounceSegmentsByType.Count > 0)
        {
            BounceTypeByNoteIndex[note.indexNote] = ResolveBounceType(note);
        }

        var kind = note != null && NoteKinds.TryGetValue(note.indexNote, out var k) ? k : CustomNoteKind.None;
        _activeMineFields.Clear();
        _activeTouchBreakFields.Clear();
        _activeTouchStarFields.Clear();
        _activeMineTouchStarFields.Clear();
        if (note != null && kind is CustomNoteKind.Mine or CustomNoteKind.MineTouchBreak)
        {
            _activeMineFields.UnionWith(GetMineFieldsForNoteType(note.type.getEnum()));
        }
        else if (note != null && kind is CustomNoteKind.TouchBreak or CustomNoteKind.MineTouchBreak)
        {
            // 绝赞 touch：走绝赞独立池（BRTTP 重定向为 NMTTP → TouchTap 字段）。
            _activeTouchBreakFields.UnionWith(GetMineFieldsForNoteType(note.type.getEnum()));
        }
        else if (note != null && kind is CustomNoteKind.TouchStar or CustomNoteKind.TouchBreakStar)
        {
            // TouchStar：走 TouchStar 独立池（NMSTP/BRSTP 重定向为 NMTTP → TouchTap 字段）。
            _activeTouchStarFields.UnionWith(GetMineFieldsForNoteType(note.type.getEnum()));
        }
        else if (note != null && kind is CustomNoteKind.MineTouchStar)
        {
            // 地雷 TouchStar：走 MineTouchStar 独立池（MNSTP 重定向为 NMTTP → TouchTap 字段）。
            _activeMineTouchStarFields.UnionWith(GetMineFieldsForNoteType(note.type.getEnum()));
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(GameCtrl), "RegistNote")]
    public static void RegistNotePostfix(GameCtrl __instance, object[] __args)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        _activeMineFields.Clear();
        _activeTouchBreakFields.Clear();
        _activeTouchStarFields.Clear();
        _activeMineTouchStarFields.Clear();
    }

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

    [HarmonyPatch]
    public static class NoteSpeedContextPatch
    {
        // 已删除 2026-08-23：全程序集反射 patch（所有 NoteBase 子类全部方法 + 全部含 NoteData
        // 参数方法）只维护 _currentNoteData 静态字段，而该字段只在加载期被 ApplyOptionalHyperSpeed
        // 读取，加载链自身（LoadCustomNote 前缀 / ApplyOptionalHyperSpeed postfix）已自设——
        // 运行时每帧反射开销纯属浪费。SV/HS 精简（2026-08-23）移除。
    }

    private static float SanitizeSpeedMultiplier(float multiplier)
    {
        // The game does not support zero/negative scroll speed.
        // Treat them as normal speed to avoid broken notes.
        return multiplier > 0f ? multiplier : 1f;
    }

    [HarmonyPatch]
    public static class NoteSpeedApplyPatch
    {
        // 已删除 2026-08-23（SV/HS 精简）：反射式宽泛 patch（全部 Initialize/SetData/Init +
        // NoteData 方法）只为应用 x{m} 内嵌速度到 AppearMsec/StarLaunchMsec。
        // 内嵌倍率现统一由 GetNoteSpeed/GetTouchSpeed postfix 缩放 DefaultMsec 实现；
        // 地雷 MineNoteBehaviour 挂载已改为 Mine* 类 Initialize 显式 EnsureMineBehaviour。
    }



    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * 以下内容是为了实现自定义 Slide
     * 
     */

    /*
     * 把 GetSlidePath 和 GetSlideHitArea 和 GetSlideLength 重定向到我可以控制的函数上, 并且多推几个参数进来
     */

    [HarmonyPostfix]
    [HarmonyPatch(typeof(SlideRoot), "Initialize")]
    public static void SlideRootInitApplySpeed(object __instance, object[] __args)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var note = FindNoteDataFromArgs(__instance, __args);
        if (note == null) return;
        TryApplySpeedToNoteObject(__instance, note);
    }

    public static float MajdataSlideHSpeed(NoteData note)
    {
        var state = RuntimeCharts.Note(note);
        return state.NoteSpeedMultipliers.TryGetValue(note, out var speed) ? speed
            : GetCurveMultAt(state.HsCurves, null, state.StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var stream) ? stream : "slide", note.time.msec);
    }

    public static bool MajdataHasSlideSvFor(NoteData note)
    {
        if (SpeedClass(note).IgnoreSV) return false;
        var state = RuntimeCharts.Note(note);
        return state.StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var stream)
            ? state.SvCurves.ContainsKey(stream) : state.SvCurves.ContainsKey("slide");
    }

    public static float MajdataSlideProgress(NoteData note, float start, float duration, float now)
    {
        var previous = RuntimeCharts.Enter(RuntimeCharts.Note(note));
        try
        {
            if (duration <= .001f) return 1;
            if (SpeedClass(note).IgnoreSV) return Mathf.Clamp01((now - start) / duration);
            if (!StreamTypeByNoteIndex.TryGetValue(note.indexNote, out var stream)) return MajdataSlideProgress(start, duration, now);
            var origin = SvIntegral(stream, start);
            var range = SvIntegral(stream, start + duration) - origin;
            return ScrollVisualTiming.Slide(SvIntegral(stream, Mathf.Clamp(now, start, start + duration)) - origin, range, duration);
        }
        finally { RuntimeCharts.Exit(previous); }
    }

    // Typed-only SV: NULL restores 1, and global SV never affects slide progress.
    private static List<(float Msec, float Mult, float Cum)> MajdataSlideSv => RuntimeCharts.Current.MajdataSlideSv;

    public static bool MajdataHasSlideSv => SvCurves.ContainsKey("slide");

    public static float MajdataSlideProgress(float start, float duration, float now)
    {
        if (duration <= .001f) return 1;
        if (!SvCurves.ContainsKey("slide") || MajdataSlideSv.Count == 0) return Mathf.Clamp01((now - start) / duration);
        float Integral(float end)
        {
            if (end < MajdataSlideSv[0].Msec) return end;
            var lo = 0;
            var hi = MajdataSlideSv.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (MajdataSlideSv[mid].Msec <= end) lo = mid;
                else hi = mid - 1;
            }
            var point = MajdataSlideSv[lo];
            return point.Cum + (end - point.Msec) * point.Mult;
        }
        var origin = Integral(start);
        var range = Integral(start + duration) - origin;
        return ScrollVisualTiming.Slide(Integral(Mathf.Clamp(now, start, start + duration)) - origin, range, duration);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(StarNote), "Initialize")]
    public static void StarNoteInitApplySpeed(object __instance, object[] __args)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var note = FindNoteDataFromArgs(__instance, __args);
        if (note == null) return;
        TryApplySpeedToNoteObject(__instance, note);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakStarNote), "Initialize")]
    public static void BreakStarNoteInitApplySpeed(object __instance, object[] __args)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var note = FindNoteDataFromArgs(__instance, __args);
        if (note == null) return;
        TryApplySpeedToNoteObject(__instance, note);
    }

    private static NoteData FindNoteDataFromArgs(object __instance, object[] __args)
    {
        if (__args != null)
        {
            foreach (var arg in __args)
            {
                if (arg is NoteData nd) return nd;
            }
        }

        if (__instance != null)
        {
            var traverse = Traverse.Create(__instance);
            foreach (var fieldName in new[] { "noteData", "_noteData", "NoteData", "data", "_data" })
            {
                var field = traverse.Field(fieldName);
                if (field.FieldExists())
                {
                    var data = field.GetValue<NoteData>();
                    if (data != null) return data;
                }
            }
        }

        return null;
    }

    // Sprite 缓存：以前每次贴图都 Sprite.Create 新建（一个 touchhold 就有 7+ 个子物体），
    // 音符多的时候 GC 压力大导致卡顿；按（贴图key + 原sprite名）缓存复用。
    private static readonly Dictionary<string, Sprite> SpriteCache = new Dictionary<string, Sprite>();

    private static Sprite CreateSpriteFromTexture(string textureKey, Texture2D texture, Sprite original)
    {
        // ppu 必须沿用原 sprite：固定 1f 会让细长贴图（如 hold 条）尺寸/拉伸全错。
        var ppu = original != null ? original.pixelsPerUnit : 1f;
        // 缓存 key 必须包含完整几何信息：图集 sprite 的 name 可能是空的，
        // 只按 name 缓存会让不同子物体（不同 rect/border）串用同一个 Sprite。
        var cacheKey = textureKey + "|" +
            (original != null
                ? original.name + "|" + original.rect + "|" + original.pivot + "|" + original.border + "|" + ppu
                : "null");
        if (SpriteCache.TryGetValue(cacheKey, out var cached))
        {
            RememberSpriteModel(cached, original);
            return cached;
        }

        var pivot = original != null
            ? new Vector2(original.pivot.x / original.rect.width, original.pivot.y / original.rect.height)
            : new Vector2(0.5f, 0.5f);
        // 9-slice border 必须按新贴图尺寸等比换算：原版 hold 条是 Sliced 渲染（中间段拉伸），
        // border 是相对原 sprite 的像素值，直接套用会切错位置导致整条被拉伸。
        var border = Vector4.zero;
        if (original != null && original.border != Vector4.zero)
        {
            var scaleX = texture.width / original.rect.width;
            var scaleY = texture.height / original.rect.height;
            border = new Vector4(
                original.border.x * scaleX,
                original.border.y * scaleY,
                original.border.z * scaleX,
                original.border.w * scaleY);
        }

        // FullRect 不做 alpha 裁剪，避免 Tight 把细长贴图裁出奇怪的形状。
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), pivot, ppu, 0, SpriteMeshType.FullRect, border);
        SpriteCache[cacheKey] = sprite;
        RememberSpriteModel(sprite, original);
        return sprite;
    }

    // 已地雷化的 NoteGuide（提示圈，共享池对象）。
    // NoteGuide.SetColor 每次被调用都会把主体 sprite 重置回原版 Guide 贴图，
    // 所以用 postfix 在 SetColor 后把地雷化的 guide 重新贴回 mine.png。
    private static readonly HashSet<NoteGuide> MineGuides = new HashSet<NoteGuide>();

    // 把 NoteGuide（提示圈，共享池对象）替换成地雷提示圈（mine.png）。
    // GuideObj 是共享池对象；独立记录主体和 Each 子圈的原贴图，回池时归还。
    private static void ApplyMineGuideTexture(NoteBase noteBase)
    {
        try
        {
            var guide = Traverse.Create(noteBase).Field("GuideObj").GetValue<NoteGuide>();
            if (!MineTextures.TryGetValue("mine", out var texture)) return;

            if (guide == null) return;

            MineGuides.Add(guide);
            foreach (var sr in guide.GetComponentsInChildren<SpriteRenderer>(true))
            {
                if (sr.sprite == null) continue;
                ApplyMineGuideSprite(guide, sr, texture);
            }
        }
        catch
        {
        }
    }

    // 已地雷化的 BreakSlide（break 箭头）——SetSprite 会把 EffectSprite 重置为原版 BreakSlideEff，
    // 用 postfix 重贴地雷光效。
    private static readonly HashSet<BreakSlide> MineBreakSlides = new HashSet<BreakSlide>();

    /// <summary>绝赞 touchhold 的 break 风格贴图（BRTHO 绝赞 touchhold / MBTHO 地雷绝赞 touchhold 共用）：
    /// Red/Yellow/Green/Blue → touchhold_break_0..3，外框 HoldGauge → touchhold_break。</summary>
    public static void ApplyBreakTouchHoldTextures(Component root)
    {
        if (root == null) return;
        ReplaceChildSprite(root, "Red", "touchhold_break_0");
        ReplaceChildSprite(root, "Yellow", "touchhold_break_1");
        ReplaceChildSprite(root, "Green", "touchhold_break_2");
        ReplaceChildSprite(root, "Blue", "touchhold_break_3");
        ReplaceChildSprite(root, "HoldGauge", "touchhold_break");
        ReplaceChildSprite(root, "Point", root is MineTouchHoldC ? "touch_break_point_mine" : "touch_break_point");
        ApplyBreakTouchHoldProgress(root);
    }

    /// <summary>把渲染器贴成地雷光效贴图（绝赞光芒替换用）。</summary>
    public static void ApplyMineEffectTexture(SpriteRenderer sr, string textureKey)
    {
        if (sr == null || !MineTextures.TryGetValue(textureKey, out var texture)) return;
        sr.sprite = CreateSpriteFromTexture(textureKey, texture, sr.sprite);
    }

    // 已地雷化的 BreakStarNote（slide/fan 内部单星是原版 BreakStarNote 实例，不是 MineBreakStarNote，
    // 不能用 `is MineBreakStarNote` 判断——必须登记）。SetSlideStar 会把 EffectSprite（绝赞光效层）
    // 设为原版 BreakStarEff（橙色闪光），postfix 按登记重贴。
    private static readonly HashSet<BreakStarNote> MineBreakStars = new HashSet<BreakStarNote>();

    /// <summary>登记一颗地雷 break 星并立刻替换其 EffectSprite（SetMulti/SetSlideStar 可能在登记前已执行）。
    /// 单星（MultiSlide=false）用 star_break_eff_mine，双星用 star_break_double_eff_mine。</summary>
    internal static void MineifyBreakStarEffect(BreakStarNote star)
    {
        if (star == null) return;
        MineBreakStars.Add(star);
        var effect = Traverse.Create(star).Field("EffectSprite").GetValue<SpriteRenderer>();
        var multi = Traverse.Create(star).Field("MultiSlide").GetValue<bool>();
        ApplyMineEffectTexture(effect, multi ? "star_break_double_eff_mine" : "star_break_eff_mine");
    }

    /// <summary>BreakSlide.SetSprite 会把 EffectSprite 重置为原版 BreakSlideEff，地雷箭头重贴。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakSlide), "SetSprite")]
    public static void BreakSlideSetSpritePostfix(BreakSlide __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (!MineBreakSlides.Contains(__instance)) return;
        var effect = Traverse.Create(__instance).Field("EffectSprite").GetValue<SpriteRenderer>();
        ApplyMineEffectTexture(effect, "slide_break_eff_mine");
    }

    /// <summary>
    /// BreakStarNote.SetMulti 非虚：地雷 break 星的 EffectSprite（绝赞光效）替换成地雷光效。
    /// Initialize 对单星/双星都会调 SetMulti（multiFlag = child.Count >= 2），所以两种都要处理：
    /// 双星 → star_break_double_eff_mine，单星 → star_break_eff_mine。
    /// 判断用登记表（slide/fan 内部星是原版 BreakStarNote 实例，不是 MineBreakStarNote）。
    /// </summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakStarNote), "SetMulti")]
    public static void BreakStarSetMultiPostfix(BreakStarNote __instance, bool multiFlag)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (!MineBreakStars.Contains(__instance)) return;
        var effect = Traverse.Create(__instance).Field("EffectSprite").GetValue<SpriteRenderer>();
        ApplyMineEffectTexture(effect, multiFlag ? "star_break_double_eff_mine" : "star_break_eff_mine");
    }

    /// <summary>BreakStarNote.SetSlideStar 非虚：地雷单星（独立池或 slide/fan 内部星）的
    /// 绝赞光效替换成 star_break_eff_mine。内部星是原版 BreakStarNote，用登记表判断。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(BreakStarNote), "SetSlideStar")]
    public static void BreakStarSetSlideStarPostfix(BreakStarNote __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (!MineBreakStars.Contains(__instance)) return;
        var effect = Traverse.Create(__instance).Field("EffectSprite").GetValue<SpriteRenderer>();
        ApplyMineEffectTexture(effect, "star_break_eff_mine");
    }

    /// <summary>NoteGuide.SetColor 会把提示圈主体 sprite 重置回原版，地雷化过的 guide 重新贴回。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteGuide), "SetColor")]
    public static void NoteGuideSetColorPostfix(NoteGuide __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        if (!MineGuides.Contains(__instance)) return;
        if (!MineTextures.TryGetValue("mine", out var texture)) return;

        var sr = Traverse.Create(__instance).Field("_spriteRender").GetValue<SpriteRenderer>();
        if (sr != null)
        {
            ApplyMineGuideSprite(__instance, sr, texture);
        }
    }

    /// <summary>GuideObj 是共享池对象（_guideObjectList）：地雷 note 结束时 guide 归还池，
    /// 普通 note 复用时不能再贴回 mine。ReturnToBase 即归还点，从登记表移除。</summary>
    [HarmonyPostfix]
    [HarmonyPatch(typeof(NoteGuide), "ReturnToBase")]
    public static void NoteGuideReturnToBasePostfix(NoteGuide __instance)
    {
        RestoreMineGuideSprites(__instance);
    }

    private static bool ReplaceChildSprite(Component root, string childName, string textureKey)
    {
        if (root == null || !MineTextures.TryGetValue(textureKey, out var texture)) return false;

        var replaced = false;
        foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(true))
        {
            // 实际 GameObject 名字会带 (Clone)，所以用 StartsWith 匹配。
            if (sr.gameObject.name.StartsWith(childName, StringComparison.OrdinalIgnoreCase))
            {

                sr.sprite = CreateSpriteFromTexture(textureKey, texture, sr.sprite);
                replaced = true;
            }
        }

        return replaced;
    }

    private static void OffsetChildPosition(Component root, string childName, Vector3 offset)
    {
        if (root == null) return;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name.StartsWith(childName, StringComparison.OrdinalIgnoreCase))
            {
                child.localPosition += offset;
            }
        }
    }

    private static bool ReplaceFirstSprite(Component root, string textureKey)
    {
        if (root == null || !MineTextures.TryGetValue(textureKey, out var texture)) return false;

        foreach (var sr in root.GetComponentsInChildren<SpriteRenderer>(true))
        {
            if (sr.sprite != null)
            {
                sr.sprite = CreateSpriteFromTexture(textureKey, texture, sr.sprite);
                return true;
            }
        }

        return false;
    }

    public static void ApplyMineTexturesToObject(GameObject go)
    {
        if (go == null) return;
        var root = go.GetComponent<NoteBase>() ?? go.GetComponentInChildren<NoteBase>(true);
        if (root == null)
        {
            // 正常情况下 Note 组件一定在根上；走到这里说明类型或结构异常，记一条日志便于排查。
            MelonLogger.Warning($"[CustomNoteType] ApplyMineTexturesToObject: no NoteBase on '{go.name}', using Transform fallback");
            ApplyMineTextures(go.transform, null);
            return;
        }

        ApplyMineTextures(root, null);
    }

    public static void ApplyTouchBreakTexturesToObject(GameObject go)
    {
        if (go == null) return;
        var root = go.GetComponent<NoteBase>() ?? go.GetComponentInChildren<NoteBase>(true);
        ApplyTouchBreakTextures(root != null ? (object)root : go.transform);
    }

    private static void ApplyMineTextures(object instance, NoteData note)
    {
        if (instance == null) return;

        var root = instance as Component;
        if (root == null) return;

        // NoteGuide（提示圈）是独立对象（NoteBase.GuideObj，从共享池分配），
        // 不在 note 的子物体树里——必须单独替换，否则地雷键保留原版蓝色提示圈
        // 以及判定前的橙色闪光动画（"蓝色外框 + 橙色动效闪光"的来源）。
        if (root is NoteBase noteBase)
        {
            ApplyMineGuideTexture(noteBase);
        }

        var typeName = instance.GetType().Name;
        var isBreak = typeName.Contains("Break");
        var replaced = false;
        string fallbackKey = null;

        // 所有地雷 Note 的提示圈都换成 Mine.png
        replaced |= ReplaceChildSprite(root, "NoteGuide", "mine");

        if (typeName.Contains("BreakNote"))
        {
            // BreakNote = Break Tap
            fallbackKey = "tap_break_mine";
            replaced |= ReplaceChildSprite(root, "Break", fallbackKey);
        }
        else if (typeName.Contains("Tap"))
        {
            fallbackKey = isBreak ? "tap_break_mine" : "tap_mine";
            replaced |= ReplaceChildSprite(root, isBreak ? "Break" : "Tap", fallbackKey);
        }
        else if (typeName.Contains("TouchHold"))
        {
            // TouchHold 要在 Hold 之前判断，否则会被当成 Hold。
            fallbackKey = isBreak ? "touch_break_mine" : "touch_mine";
            replaced |= ReplaceChildSprite(root, "Red", "touchhold_mine_0");
            replaced |= ReplaceChildSprite(root, "Yellow", "touchhold_mine_1");
            replaced |= ReplaceChildSprite(root, "Green", "touchhold_mine_2");
            replaced |= ReplaceChildSprite(root, "Blue", "touchhold_mine_3");
            replaced |= ReplaceChildSprite(root, "HoldGauge", isBreak ? "touchhold_break_mine" : "touchhold_off");
            replaced |= ReplaceChildSprite(root, "Point", isBreak ? "touch_break_point_mine" : "touch_point_mine");
        }
        else if (typeName.Contains("Hold"))
        {
            fallbackKey = isBreak ? "hold_break_mine" : "hold_mine";
            replaced |= ReplaceChildSprite(root, "Hold", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Effect", isBreak ? "hold_break_mine_on" : "hold_mine_on");
            replaced |= ReplaceChildSprite(root, "HoldEnd", "hold_mine_end");
        }
        else if (typeName.Contains("Star"))
        {
            fallbackKey = isBreak ? "star_break_mine" : "star_mine";
            replaced |= ReplaceChildSprite(root, "Star", fallbackKey);
            replaced |= ReplaceChildSprite(root, "BreakStar", fallbackKey);
        }
        else if (typeName.Contains("Slide") || typeName.Contains("Wifi"))
        {
            fallbackKey = isBreak ? "slide_break_mine" : "slide_mine";
            replaced |= ReplaceChildSprite(root, "SlideLaneStar", isBreak ? "star_break_mine" : "star_mine");
            replaced |= ReplaceChildSprite(root, "SlideArrow", fallbackKey);
            replaced |= ReplaceChildSprite(root, "BreakSlideArrow", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Effect", fallbackKey);
        }
        else if (typeName.Contains("Touch"))
        {
            fallbackKey = isBreak ? "touch_break_mine" : "touch_mine";
            replaced |= ReplaceChildSprite(root, "Point", isBreak ? "touch_break_point_mine" : "touch_point_mine");
            replaced |= ReplaceChildSprite(root, "Up", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Right", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Down", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Left", fallbackKey);
            replaced |= ReplaceChildSprite(root, "Just", fallbackKey);

            // 临时补偿：Down 看起来偏高，往下挪一点。
            OffsetChildPosition(root, "Down", new Vector3(0f, -3f, 0f));

            // 地雷 touch 外框（Border/Reserve）：普通地雷 touch（MNTTP）用 touch_mine_border_2/3，
            // 绝赞地雷 touch（MBTTP，MineTouchNoteB.IsMineTouchBreak）用 touch_break_mine_border_2/3。
            // 此前漏掉外框替换 → 地雷键仍显示普通 touch 的 border。
            var isMineBreak = root is MineTouchNoteB mineTouch && mineTouch.IsMineTouchBreak;
            var borderKeys = isMineBreak
                ? new[] { "touch_break_mine_border_2", "touch_break_mine_border_3" }
                : new[] { "touch_mine_border_2", "touch_mine_border_3" };
            var borderRenderers = root.GetComponentsInChildren<SpriteRenderer>(true)
                .Where(sr => sr.sprite != null &&
                             (sr.gameObject.name.Contains("Border") || sr.gameObject.name.Contains("Reserve")))
                .ToArray();
            for (var i = 0; i < borderRenderers.Length; i++)
            {
                var key = borderKeys[Math.Min(i, borderKeys.Length - 1)];
                if (MineTextures.TryGetValue(key, out var texture))
                {
                    borderRenderers[i].sprite = CreateSpriteFromTexture(key, texture, borderRenderers[i].sprite);
                    replaced = true;
                }
            }
        }

        // 如果精确匹配没替换到任何东西，至少把第一个 SpriteRenderer 换掉，避免完全没效果。
        if (!replaced && fallbackKey != null)
        {
            ReplaceFirstSprite(root, fallbackKey);
        }
    }

    private static void ApplyTouchBreakTextures(object instance)
    {
        if (instance == null) return;

        var root = instance as Component;
        if (root == null) return;

        // 绝赞touch：主体用 touch_break，点用 touch_break_point。
        // 不替换 Just，避免闪橙色光芒。
        ReplaceChildSprite(root, "Point", "touch_break_point");
        ReplaceChildSprite(root, "Up", "touch_break");
        ReplaceChildSprite(root, "Right", "touch_break");
        ReplaceChildSprite(root, "Down", "touch_break");
        ReplaceChildSprite(root, "Left", "touch_break");

        // 如果有 Border/Reserve 子物体，分别尝试 border_2 / border_3。
        var borderRenderers = root.GetComponentsInChildren<SpriteRenderer>(true)
            .Where(sr => sr.sprite != null &&
                         (sr.gameObject.name.Contains("Border") || sr.gameObject.name.Contains("Reserve")))
            .ToArray();

        for (var i = 0; i < borderRenderers.Length; i++)
        {
            var key = i == 0 ? "touch_break_border_2" : "touch_break_border_3";
            if (MineTextures.TryGetValue(key, out var texture))
            {
                borderRenderers[i].sprite = CreateSpriteFromTexture(key, texture, borderRenderers[i].sprite);
            }
        }
    }

    public static CustomNoteKind GetNoteKind(NoteData note)
    {
        if (note == null) return CustomNoteKind.None;
        return RuntimeCharts.Note(note).NoteKinds.TryGetValue(note.indexNote, out var kind) ? kind : CustomNoteKind.None;
    }

    public static void ApplyTouchStarTexturesToObject(GameObject go, bool isBreak)
    {
        if (go == null) return;
        var root = go.GetComponent<NoteBase>() ?? go.GetComponentInChildren<NoteBase>(true);
        ApplyTouchStarTextures(root != null ? (object)root : go.transform, isBreak);
    }

    private static void ApplyTouchStarTextures(object instance, bool isBreak)
    {
        if (instance == null) return;

        var root = instance as Component;
        if (root == null) return;

        // TouchStar：五瓣星贴图（touch_star / touch_star_break，单瓣贴图）。
        // B 类（A/B/D/E 区）：4 个原瓣物体 + 克隆第 5 瓣 → 5 方向均匀合拢（普通 touch 是四瓣）。
        // C 类（TouchNoteC 无瓣结构）：主 sprite 换五瓣星贴图。
        // 多压（EachFlag，和 touch 一起算 each）时用 touch_star_each / touch_star_break_each；
        // each 贴图缺失时回退普通五瓣。
        var isEach = false;
        try
        {
            isEach = Traverse.Create(root).Field("EachFlag").GetValue<bool>();
        }
        catch
        {
        }

        var key = isBreak ? "touch_star_break" : "touch_star";
        if (isEach)
        {
            var eachKey = isBreak ? "touch_star_break_each" : "touch_star_each";
            if (MineTextures.ContainsKey(eachKey)) key = eachKey;
        }

        if (root is TouchNoteB touchB)
        {
            ReplaceChildSprite(root, "Up", key);
            ReplaceChildSprite(root, "Right", key);
            ReplaceChildSprite(root, "Down", key);
            ReplaceChildSprite(root, "Left", key);
            ExpandToFivePetals(touchB);
        }
        else
        {
            ReplaceFirstSprite(root, key);
        }

        // Just = 判定光环（touch_hit_star，对应原版 UI_NOTES_Touch_Just 的用途）。
        // touch_hit_star 是 320x320，原版 Just 是 128x128 → 按 128/320 = 0.4 缩放，避免合拢时重合。
        ReplaceChildSprite(root, "Just", "touch_hit_star");
        ScaleTouchStarJust(root);

        // 中心点：绝赞（BRSTP）用 touch_break_point（同绝赞 touch），普通保留原版。
        if (isBreak)
        {
            ReplaceChildSprite(root, "Point", "touch_break_point");
        }

        // 两个 border 素材暂时缺失 → 隐藏 Border/Reserve（预留圈/边框）。
        SetChildActive(root, "Border", false);
        SetChildActive(root, "Reserve", false);

        // 白色外框提示（NoticeObject，touch 出现前的提示圈）：放大 1.5 倍更醒目 + 上移一点（幂等）。
        if (ScaledTouchStarNotices.Add(root.gameObject.GetInstanceID()))
        {
            var notice = Traverse.Create(root).Field("NoticeObject").GetValue<GameObject>();
            if (notice != null)
            {
                notice.transform.localScale = notice.transform.localScale * 1.5f;
                notice.transform.localPosition = notice.transform.localPosition + new Vector3(0f, 5f, 0f);
            }
        }
    }

    public static void ApplyMineTouchStarTexturesToObject(GameObject go)
    {
        if (go == null) return;
        var root = go.GetComponent<NoteBase>() ?? go.GetComponentInChildren<NoteBase>(true);
        ApplyMineTouchStarTextures(root != null ? (object)root : go.transform);
    }

    /// <summary>
    /// 地雷 TouchStar（MNSTP）贴图：五瓣星结构同 TouchStar（touch_star_mine 单瓣贴图，
    /// 克隆第 5 瓣、Just 缩放、Notice 放大），但瓣/中心点/外框换成地雷素材：
    ///   瓣（Up/Right/Down/Left）→ touch_star_mine（each 时 touch_star_mine_each）
    ///   判定光环 Just → touch_hit_star_mine（缺失回退 touch_hit_star）
    ///   中心点 Point → touch_point_mine（同地雷 touch）
    ///   外框 Border/Reserve → touch_mine_border_2/3（同地雷 touch）
    ///   提示圈 NoteGuide → mine（ApplyMineGuideTexture）
    /// 不调 ApplyTouchStarTextures（它会把 Border/Reserve 隐藏掉），而是内联五瓣星结构。
    /// </summary>
    private static void ApplyMineTouchStarTextures(object instance)
    {
        if (instance == null) return;
        var root = instance as Component;
        if (root == null) return;

        var isEach = false;
        try
        {
            isEach = Traverse.Create(root).Field("EachFlag").GetValue<bool>();
        }
        catch
        {
        }

        var petalKey = "touch_star_mine";
        if (isEach && MineTextures.ContainsKey("touch_star_mine_each")) petalKey = "touch_star_mine_each";

        // 五瓣星：先贴 4 瓣（clone 继承第 1 瓣贴图），再扩展成 5 瓣。
        if (root is TouchNoteB touchB)
        {
            ReplaceChildSprite(root, "Up", petalKey);
            ReplaceChildSprite(root, "Right", petalKey);
            ReplaceChildSprite(root, "Down", petalKey);
            ReplaceChildSprite(root, "Left", petalKey);
            ExpandToFivePetals(touchB);
        }
        else
        {
            ReplaceFirstSprite(root, petalKey);
        }

        // 判定光环：touch_hit_star_mine（缺失回退原版五瓣光环），照 touchstar 的 0.4 缩放。
        ReplaceChildSprite(root, "Just", MineTextures.ContainsKey("touch_hit_star_mine") ? "touch_hit_star_mine" : "touch_hit_star");
        ScaleTouchStarJust(root);

        // 中心点：地雷 touch 的点。
        ReplaceChildSprite(root, "Point", "touch_point_mine");

        // 外框（Border/Reserve）：同地雷 touch（touch_mine_border_2/3 按序分配）。
        var borderKeys = new[] { "touch_mine_border_2", "touch_mine_border_3" };
        var borderRenderers = root.GetComponentsInChildren<SpriteRenderer>(true)
            .Where(sr => sr.sprite != null &&
                         (sr.gameObject.name.Contains("Border") || sr.gameObject.name.Contains("Reserve")))
            .ToArray();
        for (var i = 0; i < borderRenderers.Length; i++)
        {
            var key = borderKeys[Math.Min(i, borderKeys.Length - 1)];
            if (MineTextures.TryGetValue(key, out var texture))
            {
                borderRenderers[i].sprite = CreateSpriteFromTexture(key, texture, borderRenderers[i].sprite);
            }
        }

        // 提示圈：地雷提示圈（mine.png）。
        if (root is NoteBase noteBase)
        {
            ApplyMineGuideTexture(noteBase);
        }

        // 白色外框提示：放大 1.5 倍 + 上移（幂等，同 touchstar）。
        if (ScaledTouchStarNotices.Add(root.gameObject.GetInstanceID()))
        {
            var notice = Traverse.Create(root).Field("NoticeObject").GetValue<GameObject>();
            if (notice != null)
            {
                notice.transform.localScale = notice.transform.localScale * 1.5f;
                notice.transform.localPosition = notice.transform.localPosition + new Vector3(0f, 5f, 0f);
            }
        }
    }

    // 已放大 Notice 的实例（幂等，池复用只放大一次）。
    private static readonly HashSet<int> ScaledTouchStarNotices = new HashSet<int>();

    // 已缩放过 Just 的实例（池对象复用只缩一次，避免 scale 反复相乘越来越小）。
    private static readonly HashSet<int> ScaledTouchStarJuts = new HashSet<int>();

    private static void ScaleTouchStarJust(Component root)
    {
        if (root == null || !ScaledTouchStarJuts.Add(root.gameObject.GetInstanceID())) return;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name.StartsWith("Just", StringComparison.OrdinalIgnoreCase))
            {
                child.localScale = child.localScale * (128f / 320f);
            }
        }
    }

    /// <summary>
    /// 把 touch 的 4 瓣扩展为 5 瓣：克隆第 1 个瓣物体，5 个瓣按 72° 均匀重排。
    /// 游戏多处循环以 DefaultCorlsPos.Length 为边界遍历 ColorsObject / NotePlateSprite /
    /// NotePlateMaterial / TargetSortDiff（SetEach、GetNoteYPosition、Initialize），
    /// 所以这些数组必须同步扩到 5，否则越界崩溃。
    /// 幂等：ColorsObject 已是 5 个时直接返回（对象复用再初始化不会重复克隆）。
    /// </summary>
    private static void ExpandToFivePetals(TouchNoteB note)
    {
        if (note == null) return;
        var t = Traverse.Create(note);
        var colors = t.Field("ColorsObject").GetValue<SpriteRenderer[]>();
        var defaultPos = t.Field("DefaultCorlsPos").GetValue<Vector3[]>();
        var notePlateSprites = t.Field("NotePlateSprite").GetValue<SpriteRenderer[]>();
        var notePlateMaterials = t.Field("NotePlateMaterial").GetValue<Material[]>();
        var targetSortDiff = t.Field("TargetSortDiff").GetValue<int[]>();
        if (colors == null || colors.Length != 4 || defaultPos == null || defaultPos.Length != 4) return;

        var p0 = defaultPos[0];
        var radius = p0.magnitude;
        var baseAngle = Mathf.Atan2(p0.y, p0.x);

        // 克隆第 1 个瓣（继承已替换的 touch_star 贴图）
        var cloneGo = UnityEngine.Object.Instantiate(colors[0].gameObject, colors[0].transform.parent);
        cloneGo.SetActive(true);
        var cloneSr = cloneGo.GetComponent<SpriteRenderer>();
        if (cloneSr == null)
        {
            UnityEngine.Object.Destroy(cloneGo);
            return;
        }

        var newColors = new SpriteRenderer[5];
        var newPos = new Vector3[5];
        // 非 null 一律扩容为 5（缺项补 clone/默认）：游戏多处循环以
        // DefaultCorlsPos.Length=5 为边界遍历这些数组，Length≠4（0/2/3/5 等）
        // 若不扩容 → OOB 崩溃。Length==4 复制前 4 项，Length<4 缺失项留默认。
        var newNotePlateSprites = notePlateSprites != null ? new SpriteRenderer[5] : null;
        var newNotePlateMaterials = notePlateMaterials != null ? new Material[5] : null;
        var newTargetSortDiff = targetSortDiff != null ? new int[5] : null;

        for (var i = 0; i < 5; i++)
        {
            var angle = baseAngle + i * 72f * Mathf.Deg2Rad;
            newPos[i] = new Vector3(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius, p0.z);

            SpriteRenderer sr;
            float origAngle;
            if (i < 4)
            {
                sr = colors[i];
                origAngle = Mathf.Atan2(defaultPos[i].y, defaultPos[i].x);
                if (newNotePlateSprites != null && i < notePlateSprites.Length) newNotePlateSprites[i] = notePlateSprites[i];
                if (newNotePlateMaterials != null && i < notePlateMaterials.Length) newNotePlateMaterials[i] = notePlateMaterials[i];
                if (newTargetSortDiff != null && i < targetSortDiff.Length) newTargetSortDiff[i] = targetSortDiff[i];
            }
            else
            {
                sr = cloneSr;
                origAngle = Mathf.Atan2(defaultPos[0].y, defaultPos[0].x);
                if (newNotePlateSprites != null) newNotePlateSprites[i] = cloneSr;
                if (newNotePlateMaterials != null) newNotePlateMaterials[i] = cloneSr.material;
                if (newTargetSortDiff != null) newTargetSortDiff[i] = targetSortDiff[0];
            }

            // 瓣贴图朝向跟随新方向：原 z 旋转 + (新方向 - 原方向)
            var euler = sr.transform.localEulerAngles;
            var delta = (angle - origAngle) * Mathf.Rad2Deg;
            sr.transform.localRotation = Quaternion.Euler(euler.x, euler.y, euler.z + delta);

            // 瓣贴图（touch_star 128x192 / touch_star_each 等）比原版 touch（112x82）大 →
            // 按面积根号比缩放（sqrt((112×82)/(128×192)) ≈ 0.61，取 0.6），
            // 避免五瓣合拢时重叠成一团。each 贴图尺寸可能不同，按当前贴图实际尺寸自适应，
            // 与另外两张（touch_star / touch_star_break）保持一致的缩放规则。
            var sprite = sr.sprite;
            if (sprite != null && sprite.rect.width > 1f && sprite.rect.height > 1f)
            {
                var areaScale = Mathf.Sqrt((112f * 82f) / (sprite.rect.width * sprite.rect.height));
                sr.transform.localScale = sr.transform.localScale * areaScale;
            }
            else
            {
                sr.transform.localScale = sr.transform.localScale * 0.6f;
            }

            newColors[i] = sr;
        }

        t.Field("ColorsObject").SetValue(newColors);
        t.Field("DefaultCorlsPos").SetValue(newPos);
        if (newNotePlateSprites != null) t.Field("NotePlateSprite").SetValue(newNotePlateSprites);
        if (newNotePlateMaterials != null) t.Field("NotePlateMaterial").SetValue(newNotePlateMaterials);
        if (newTargetSortDiff != null) t.Field("TargetSortDiff").SetValue(newTargetSortDiff);
    }

    private static void SetChildActive(Component root, string childName, bool active)
    {
        if (root == null) return;
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            if (child.name.StartsWith(childName, StringComparison.OrdinalIgnoreCase))
            {
                child.gameObject.SetActive(active);
            }
        }
    }

    // 已处理过的 Initialize（对象 + noteIndex 去重）：
    // SlideRoot/StarNote/BreakStarNote 的 InitApplySpeed postfix 与 Mine* 类显式
    // EnsureMineBehaviour 可能对同一对象重复调用，用此字典保证只处理一次。
    private static readonly Dictionary<Component, int> HandledInitializations = new Dictionary<Component, int>();

    private static bool IsSlideType(NotesTypeID.Def type)
    {
        return type is NotesTypeID.Def.Slide or NotesTypeID.Def.BreakSlide or
            NotesTypeID.Def.ExSlide or NotesTypeID.Def.ExBreakSlide or NotesTypeID.Def.ConnectSlide;
    }

    private static void TryApplySpeedToNoteObject(object instance, NoteData note)
    {
        var component = instance as Component;
        if (component == null) return;

        // 去重：同一个对象同一 note 只处理一次。
        if (HandledInitializations.TryGetValue(component, out var handledIndex) && handledIndex == note.indexNote)
        {
            return;
        }

        HandledInitializations[component] = note.indexNote;

        NoteKinds.TryGetValue(note.indexNote, out var kind);

        if (kind != CustomNoteKind.None)
        {
            var isMine = kind is CustomNoteKind.Mine or CustomNoteKind.MineTouchBreak;
            var isTouchBreak = kind is CustomNoteKind.TouchBreak or CustomNoteKind.MineTouchBreak;

            var go = component.gameObject;
            // SlideRoot 内部实例化的星（StarNote/BreakStarNote 用 slide 的 NoteData 初始化）
            // 是 slide 的视觉星，不是独立地雷键：不贴地雷贴图、不挂 behaviour、判定保持原版。
            var isSlideStar = instance is StarNote or BreakStarNote && IsSlideType(note.type.getEnum());
            // ISelfJudgingMineNote 独立类贴图/判定自己负责，不需要 MineNoteBehaviour；
            // Hold/Slide 等非自反转类仍需 behaviour（transpiler 靠它识别地雷）。
            if (!isSlideStar && !(instance is ISelfJudgingMineNote))
            {
                var behaviour = go.GetComponent<MineNoteBehaviour>();
                if (behaviour == null) behaviour = go.AddComponent<MineNoteBehaviour>();
                // SlideRoot/SlideFan 的 ApplyMineVisual 会遍历子物体误伤内部星（_starNote），
                // 所以只挂判定标记不贴图；轨道贴图由独立地雷箭头池负责。
                var applyTextures = !(instance is MineSlideRoot or SlideFan);
                behaviour.Setup(note.indexNote, isMine, isTouchBreak, applyTextures);
            }
        }

        // 2026-08-23 SV/HS 精简：速度应用部分移除——内嵌 x{m} 倍率由 GetNoteSpeed/
        // GetTouchSpeed postfix 统一缩放 DefaultMsec；本函数只负责地雷 behaviour 挂载。
    }

    [HarmonyPatch]
    public static class SlideNoteDataHack

    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(SlideRoot), "Initialize"),
                AccessTools.Method(typeof(SlideFan), "Initialize"),
                // Temporarily disabled: the IL injection here can crash on built-in slide notes.
                // （待办：排查崩溃原因后恢复，见同目录 TODO.md §2.5）
                // AccessTools.Method(typeof(SlideRoot), "GetSlideArrowNum", [typeof(NoteData)]),
                AccessTools.Method(typeof(StarNote), "Initialize"),
                AccessTools.Method(typeof(BreakStarNote), "Initialize"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var methodGetSlidePathRedirect = AccessTools.Method(typeof(CustomNoteTypes), "GetSlidePathRedirect");
            var methodGetSlideHitAreaRedirect = AccessTools.Method(typeof(CustomNoteTypes), "GetSlideHitAreaRedirect");
            var methodGetSlideLengthRedirect = AccessTools.Method(typeof(CustomNoteTypes), "GetSlideLengthRedirect");

            var newInstList = new List<CodeInstruction>();
            var injected = 0;
            void Redirect(CodeInstruction original, MethodInfo method)
            {
                var note = new CodeInstruction(OpCodes.Ldarg_1);
                note.labels.AddRange(original.labels);
                var call = new CodeInstruction(OpCodes.Call, method);
                call.blocks.AddRange(original.blocks);
                newInstList.Add(note);
                newInstList.Add(call);
                injected++;
            }
            foreach (var inst in instructions)
            {
                if (IsSlideManagerCall(inst, "GetSlidePath"))
                {
                    // 原始 callvirt 调用栈：instance, slideType, start, end, starButton（5 值）。
                    // redirect 是静态方法且比原实例方法多一个 NoteData 参数（6 参数）：
                    // 必须在 call 前补 ldarg.1（三个目标 Initialize 的签名均为 (NoteData)，
                    // arg_1 就是 noteData），否则栈下溢 → Mono 验证器报
                    // InvalidProgramException "call 0x00000095"（wrapper 编译崩溃，
                    // 并连锁 SlideLayerReverse 等同一目标方法的 patch 失败）。
                    // 不能保留原 callvirt：redirect 消费全部参数栈后残留的 callvirt 同样下溢。
                    // 内置 slide 在 redirect 内部走回退分支（instance.GetXxx(...)），无行为变化。
                    Redirect(inst, methodGetSlidePathRedirect);
                }
                else if (IsSlideManagerCall(inst, "GetSlideHitArea"))
                {
                    // GetSlideHitArea(SlideType, int, int) 栈 4 值 + noteData = 5 = redirect 参数数
                    Redirect(inst, methodGetSlideHitAreaRedirect);
                }
                else if (IsSlideManagerCall(inst, "GetSlideLength"))
                {
                    // GetSlideLength(SlideType, int, int) 栈 4 值 + noteData = 5 = redirect 参数数
                    Redirect(inst, methodGetSlideLengthRedirect);
                }
                else
                {
                    newInstList.Add(inst);
                }
            }
            // 2026-08-25 修复：原实现用 inst.Calls(MethodInfo)——object.Equals 按引用比较
            // operand 与 AccessTools.Method 结果（反汇编 MethodInfo 实例与 GetMethod 实例
            // 不是同一对象）→ 永远不命中 → 自定义 slide（NMSSS/BRSSS）的路径/判定区/长度
            // redirect 从未注入，一直静默退化到原生模板近似形状。必须按名字+声明类型匹配。
            if (injected == 0)
            {
                MelonLogger.Warning("[CustomNoteType] SlideNoteDataHack: no SlideManager calls matched; custom slide redirects NOT injected");
            }
            else
            {
                MelonLogger.Msg($"[CustomNoteType] SlideNoteDataHack: {injected} SlideManager calls redirected");
            }
            return newInstList;
        }

        private static bool IsSlideManagerCall(CodeInstruction inst, string methodName)
        {
            if (inst.opcode != OpCodes.Call && inst.opcode != OpCodes.Callvirt) return false;
            // 反射模式 operand=MethodInfo；Cecil 模式 operand=Mono.Cecil.MethodReference。
            if (inst.operand is MethodInfo mi)
            {
                return mi.Name == methodName && mi.DeclaringType != null && mi.DeclaringType.Name == "SlideManager";
            }
            if (inst.operand != null)
            {
                var name = inst.operand.GetType().GetProperty("Name")?.GetValue(inst.operand) as string;
                if (name != methodName) return false;
                var decl = inst.operand.GetType().GetProperty("DeclaringType")?.GetValue(inst.operand);
                return decl?.GetType().GetProperty("Name")?.GetValue(decl) as string == "SlideManager";
            }
            return false;
        }
    }


    public static List<Vector4> GetSlidePathRedirect(SlideManager instance, SlideType slideType, int start, int end,
        int starButton, NoteData noteData)
    {
        if (noteData is CustomSlideNoteData data)
        {
            if (data.IsFan) return data.FanPathLists[starButton][data.FanLane(end)];
            return data.SlidePathList[starButton];
        }
        return instance.GetSlidePath(slideType, start, end, starButton);
    }

    /// <summary>
    /// GameCtrl.RegistNote 在 SlideRoot.Initialize 之前调用 GetSlideArrowNum(NoteData) 计算箭头排序号。
    /// 该方法内部的 GetSlidePath 调用没有被 SlideNoteDataHack 重定向（注入曾致内置 slide 崩溃被禁用），
    /// 自定义路径的 slideData.type（GetEndType 结果 1/2/3）× start/end 组合在原生查表里越界 → 进曲崩溃。
    /// 对 CustomSlideNoteData 直接按自定义路径复刻原版箭头数算法短路。
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SlideRoot), "GetSlideArrowNum", new[] { typeof(NoteData) })]
    public static bool SlideArrowNumCustomPrefix(SlideRoot __instance, NoteData note, ref int __result)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return true; }

        if (note is not CustomSlideNoteData data) return true;
        var path = data.SlidePathList[__instance.ButtonId];
        if (path == null || path.Count < 3)
        {
            __result = 0;
            return false;
        }
        // 复刻原版：倒数第二点 z + 23.556 &lt; 末点 z → Count-2，否则 Count-3
        __result = path[path.Count - 2].z + 23.556f < path[path.Count - 1].z
            ? path.Count - 2
            : path.Count - 3;
        return false;
    }

    /// <summary>
    /// UpdateAlpha 的循环边界是 _dispLaneNum（Initialize 里按路径点算），
    /// 但 _spriteRenders/_breakSpriteRenders 由 GameCtrl.RegistNote 按 GetSlideArrowNum 分配。
    /// 两者理论上一致；若不一致（分配不足/池占用），UpdateAlpha 越界崩溃。
    /// 这里做 clamp 兜底 + 打印一次差异日志定位根因。
    /// </summary>
    [HarmonyPrefix]
    [HarmonyPatch(typeof(SlideRoot), "UpdateAlpha")]
    public static void UpdateAlphaSafePrefix(SlideRoot __instance)
    {
        if (!(CustomNoteTypes.FeaturesEnabled(__instance))) {  return; }

        var t = Traverse.Create(__instance);
        var noteData = t.Field("NoteData").GetValue<NoteData>();
        if (noteData == null) return;
        var dispLane = t.Field("_dispLaneNum").GetValue<int>();
        var spriteRenders = (List<SpriteRenderer>)t.Field("_spriteRenders").GetValue();
        var breakRenders = (List<BreakSlide>)t.Field("_breakSpriteRenders").GetValue();
        var isBreak = t.Field("BreakFlag").GetValue<bool>();
        var max = isBreak ? breakRenders.Count : spriteRenders.Count;
        if (dispLane > max)
        {
            // 原版 GetSlideArrowNum（分配箭头数）与 Initialize 的 _dispLaneNum（按路径点算）
            // 理论上一致；不一致说明路径/段结构异常（如长链同屏过多耗尽箭头池），clamp 防崩。
            t.Field("_dispLaneNum").SetValue(max);
        }
    }

    public static List<SlideManager.HitArea> GetSlideHitAreaRedirect(SlideManager instance, SlideType slideType,
        int start, int end, int starButton, NoteData noteData)
    {
        if (noteData is CustomSlideNoteData data)
        {
            if (data.IsFan) return data.FanHitAreaLists[starButton][data.FanLane(end)];
            return data.SlideHitAreasList[starButton];
        }
        return instance.GetSlideHitArea(slideType, start, end, starButton);
    }

    public static float GetSlideLengthRedirect(SlideManager instance, SlideType slideType,
        int start, int end, NoteData noteData)
    {
        if (noteData is CustomSlideNoteData data)
        {
            return data.SlidePathLength;
        }
        try
        {
            // 预检：原生 _slidePathList 查表要求 slideType∈[0,13] 且 start/end∈[0,7]。
            // 越界直接走日志+兜底，避免 AOOE 在 catch 里被二次 NRE 掩蔽。
            if ((int)slideType < 0 || (int)slideType > 13 || start < 0 || start > 7 || end < 0 || end > 7)
            {
                LogGetSlideLengthOOB(slideType, start, end, noteData, "range");
                return 1f;
            }
            return instance.GetSlideLength(slideType, start, end);
        }
        catch (Exception ex)
        {
            // NotesTypeID 是 class 且重载了 ==（op_Equality 会解引用两个操作数），
            // 判空必须用 ReferenceEquals，否则 catch 里 nt == null 二次 NRE，
            // 把真实的 AOOE 信息掩蔽成表面上的 NullReferenceException。
            LogGetSlideLengthOOB(slideType, start, end, noteData, ex.GetType().Name + ":" + ex.Message);
            return 1f;
        }
    }

    private static void LogGetSlideLengthOOB(SlideType slideType, int start, int end, NoteData noteData, string why)
    {
        try
        {
            var nt = ReferenceEquals(noteData, null) ? null : noteData.type;
            var noteDesc = ReferenceEquals(nt, null)
                ? "null"
                : $"{nt.getEnum()} isTouch={nt.isTouch()} isStar={nt.isStar()} isSlide={nt.isSlide()}";
            var extra = "";
            try
            {
                if (!ReferenceEquals(noteData, null) && noteData.slideData != null)
                {
                    extra = $" slideType={noteData.slideData.type} targetNote={noteData.slideData.targetNote}";
                }
            }
            catch (Exception) { }
            MelonLogger.Error(
                $"[CustomNoteType] GetSlideLengthRedirect OOB({why}): slideType={(int)slideType}({slideType}) " +
                $"start={start} end={end} noteType={noteDesc} " +
                $"index={noteData?.index} indexNote={noteData?.indexNote} indexSlide={noteData?.indexSlide} " +
                $"startButtonPos={noteData?.startButtonPos}{extra}");
        }
        catch (Exception logEx)
        {
            MelonLogger.Error($"[CustomNoteType] GetSlideLengthRedirect LOGGING FAILED: {logEx}");
        }
    }



}




