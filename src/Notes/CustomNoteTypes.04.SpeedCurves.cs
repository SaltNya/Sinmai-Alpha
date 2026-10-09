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

// SV/HS 命令解析、曲线构建、积分与滚动查表。
public partial class CustomNoteTypes
{

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
    // Each 双押伙伴映射（indexNote → eachChild 的 indexNote 列表）：流速不同的 each 对不画辅助条。
    private static Dictionary<int, List<int>> EachChildByNoteIndex => RuntimeCharts.Current.EachChildByNoteIndex;
    private static bool _loggedSvInject { get => RuntimeCharts.Current._loggedSvInject; set => RuntimeCharts.Current._loggedSvInject = value; }
    private static bool _loggedSvLeadScale { get => RuntimeCharts.Current._loggedSvLeadScale; set => RuntimeCharts.Current._loggedSvLeadScale = value; }

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
}
