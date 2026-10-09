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

// 公共配置、音符分类与共享字段。静态字段保持原初始化顺序；功能实现见同名前缀的分文件。
// 分文件索引（CustomNoteTypes.编号.职责.cs）：
// 01–03：初始化、MA2 读取、单音符 HS。
// 04–07：SV/HS 曲线、滚动和绝赞计分。
// 08–09：弹跳命令与显示。
// 10–13：地雷 Hold 皮肤、自动播放、Hold/Slide 判定。
// 14–16：对象池创建、地雷滑条皮肤、池路由。
// 17–18：谱面生命周期和运行时速度。
// 19–21：地雷/TouchStar 皮肤、自定义滑条路径。
// 编号保留既有 Harmony 补丁发现顺序；静态字段集中保留原初始化顺序。
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
    private const float NoteScrollCoverMsec = 10000f;

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

    private static bool _loggedMissingFanTexture;

    private static bool _loggedMineInversion;

    // A head hit follows MNTAP (Miss); body contact only caps the final result at Good.
    // Keep JudgeResult pending until the native tail settlement for a body contact.
    private static readonly Dictionary<NoteBase, NoteJudge.ETiming> MineHoldPenalties = new();

    // Sprite 缓存：以前每次贴图都 Sprite.Create 新建（一个 touchhold 就有 7+ 个子物体），
    // 音符多的时候 GC 压力大导致卡顿；按（贴图key + 原sprite名）缓存复用。
    private static readonly Dictionary<string, Sprite> SpriteCache = new Dictionary<string, Sprite>();

    // 已地雷化的 NoteGuide（提示圈，共享池对象）。
    // NoteGuide.SetColor 每次被调用都会把主体 sprite 重置回原版 Guide 贴图，
    // 所以用 postfix 在 SetColor 后把地雷化的 guide 重新贴回 mine.png。
    private static readonly HashSet<NoteGuide> MineGuides = new HashSet<NoteGuide>();

    // 已地雷化的 BreakSlide（break 箭头）——SetSprite 会把 EffectSprite 重置为原版 BreakSlideEff，
    // 用 postfix 重贴地雷光效。
    private static readonly HashSet<BreakSlide> MineBreakSlides = new HashSet<BreakSlide>();

    // 已地雷化的 BreakStarNote（slide/fan 内部单星是原版 BreakStarNote 实例，不是 MineBreakStarNote，
    // 不能用 `is MineBreakStarNote` 判断——必须登记）。SetSlideStar 会把 EffectSprite（绝赞光效层）
    // 设为原版 BreakStarEff（橙色闪光），postfix 按登记重贴。
    private static readonly HashSet<BreakStarNote> MineBreakStars = new HashSet<BreakStarNote>();

    // 已放大 Notice 的实例（幂等，池复用只放大一次）。
    private static readonly HashSet<int> ScaledTouchStarNotices = new HashSet<int>();

    // 已缩放过 Just 的实例（池对象复用只缩一次，避免 scale 反复相乘越来越小）。
    private static readonly HashSet<int> ScaledTouchStarJuts = new HashSet<int>();

    // 已处理过的 Initialize（对象 + noteIndex 去重）：
    // SlideRoot/StarNote/BreakStarNote 的 InitApplySpeed postfix 与 Mine* 类显式
    // EnsureMineBehaviour 可能对同一对象重复调用，用此字典保证只处理一次。
    private static readonly Dictionary<Component, int> HandledInitializations = new Dictionary<Component, int>();
}
