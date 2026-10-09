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

// BOUNCE/SPAWN 命令解析和按音符预计算。
public partial class CustomNoteTypes
{
    private static List<(int Bar, int Grid, string Text)> PendingBounceSegments => RuntimeCharts.Current.PendingBounceSegments;
    // 已转换的 Bounce 段（类型键 ""=全局展开后不保留；tap/star/hold/break → (msec, 时长秒)），
    // 每类型按 msec 升序。全局命令展开到 tap/star/hold；分类命令进对应类型。
    private static Dictionary<string, List<(float Msec, float DurationSec)>> BounceSegmentsByType => RuntimeCharts.Current.BounceSegmentsByType;
    // 每音符预计算的 bounce 时长（indexNote → 秒，0=无 bounce）。
    private static Dictionary<int, float> BounceDurationByNoteIndex => RuntimeCharts.Current.BounceDurationByNoteIndex;
    // 每音符的 bounce 类型键（indexNote → tap/star/hold/break）。
    private static Dictionary<int, string> BounceTypeByNoteIndex => RuntimeCharts.Current.BounceTypeByNoteIndex;
    private static bool _loggedBounce { get => RuntimeCharts.Current._loggedBounce; set => RuntimeCharts.Current._loggedBounce = value; }
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
}
