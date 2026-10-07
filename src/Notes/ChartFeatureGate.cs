using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Manager;
using Monitor;
using Monitor.Game;
using MAI2.Util;
using SinmaiAlpha.Assets;
using SinmaiAlpha.Hosting;

namespace SinmaiAlpha.Notes;

// Detection is separate from rendering: A000 never opens a chart for this scan.
internal static class ChartFeatureGate
{
    private static readonly Dictionary<string, Ma2fileRecordID.Def> NativeIds = new(StringComparer.Ordinal);
    internal static Ma2fileRecordID.Def FindNativeRecord(string name) => name != null && NativeIds.TryGetValue(name, out var id) ? id : Ma2fileRecordID.Def.Invalid;
    private static readonly Dictionary<string, int> NativeFields = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (long Length, DateTime Time, bool Enabled)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> Ignored = new(StringComparer.OrdinalIgnoreCase) { "SHOWJUDGEINFO", "SHOWCOMBOINFO", "OUTERBRIGHTNESS" };
    internal static void RegisterExtensions(IEnumerable<string> names) => Extensions.UnionWith(names.Where(n => !NativeFields.ContainsKey(n) && !Ignored.Contains(n)));
    private static readonly bool[] Selected = new bool[2];
    private static readonly bool?[] HoldTap = new bool?[2];
    internal static bool AllowTapInHold(int monitor, bool defaultValue = false) => monitor >= 0 && monitor < HoldTap.Length ? HoldTap[monitor] ?? defaultValue : defaultValue;
    internal static bool DetectHoldTap(string path, int difficulty) => DetectHoldTapOverride(path, difficulty) == true;
    internal static bool? DetectHoldTapOverride(string path, int difficulty)
    {
        if (string.IsNullOrWhiteSpace(path) || IsA000(path)) return null;
        try {
            path = FileSystem.ResolvePath(path);
            var marker = Path.ChangeExtension(path, ".ExtraDifficulty.flag");
            if (!File.Exists(marker) && difficulty == 3) marker = Path.Combine(Path.GetDirectoryName(path), "ExtraDifficulty.flag");
            return File.Exists(marker) ? ChartFlags.Parse(File.ReadAllText(marker)).TapInHoldOverride : null;
        } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } catch (ArgumentException) { return null; }
    }
    internal static bool AnySelected => Selected[0] || Selected[1];
    internal static void CaptureNativeRecords()
    {
        if (NativeFields.Count != 0) return;
        // Snapshot stock metadata before any extension is added.
        var table = (Array)HarmonyLib.AccessTools.Field(typeof(Ma2fileRecordID), "s_Ma2fileRecord_Data").GetValue(null);
        var itemType = table.GetType().GetElementType();
        var nameField = HarmonyLib.AccessTools.Field(itemType, "enumName");
        var countField = HarmonyLib.AccessTools.Field(itemType, "paramNum");
        for (var i = 0; i < (int)Ma2fileRecordID.Def.End; i++)
        {
            var record = table.GetValue(i);
            var name = (string)nameField.GetValue(record);
            NativeFields[name] = (int)countField.GetValue(record);
            NativeIds[name] = (Ma2fileRecordID.Def)i;
        }
    }
    internal static bool IsA000(string path) => !string.IsNullOrEmpty(path) &&
        path.Replace('\\', '/').Split('/').Any(p => p.Equals("A000", StringComparison.OrdinalIgnoreCase));
    internal static bool ContainsExtensions(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith("//") || line.StartsWith("#")) continue;
            var fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (Ignored.Contains(fields[0])) continue;
            if (!NativeFields.TryGetValue(fields[0], out var count)) { if (Extensions.Contains(fields[0])) return true; continue; }
            // Extra per-note HS, stream IDs, fake/D-zone and visual payloads.
            if (fields.Length > count && count > 0) return true;
            // Returning V and off-centre TouchHold require Alpha even with native tags.
            if (fields[0].EndsWith("SV_", StringComparison.OrdinalIgnoreCase) && fields.Length >= 7 && fields[3] == fields[6]) return true;
            if (fields[0] == "NMTHO" && fields.Length >= 6 && fields[5] != "C") return true;
        }
        return false;
    }
    internal static bool Detect(string path, string text = null)
    {
        if (IsA000(path)) return false;
        if (text != null) return ContainsExtensions(text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            path = Path.GetFullPath(path);
            var file = new FileInfo(path);
            if (!file.Exists) return false;
            if (Cache.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Time == file.LastWriteTimeUtc) return cached.Enabled;
            var enabled = ContainsExtensions(File.ReadLines(path));
            Cache[path] = (file.Length, file.LastWriteTimeUtc, enabled);
            return enabled;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }
    internal static void BeginGame()
    {
        Array.Clear(Selected, 0, Selected.Length);
        Array.Clear(HoldTap, 0, HoldTap.Length);
        for (var monitor = 0; monitor < Selected.Length; monitor++)
        {
            if (!Singleton<UserDataManager>.Instance.GetUserData(monitor).IsEntry) continue;
            var music = Singleton<DataManager>.Instance.GetMusic(GameManager.SelectMusicID[monitor]);
            var difficulty = GameManager.SelectDifficultyID[monitor];
            if (music?.notesData == null || difficulty < 0 || difficulty >= music.notesData.Count) continue;
            var path = music.notesData[difficulty]?.file?.path;
            Selected[monitor] = Detect(path);
            HoldTap[monitor] = DetectHoldTapOverride(path, difficulty);
        }
    }
    internal static bool SelectedMonitor(int monitor) => monitor >= 0 && monitor < Selected.Length && Selected[monitor];
    internal static void EndGame() { Array.Clear(Selected, 0, Selected.Length); Array.Clear(HoldTap, 0, HoldTap.Length); }
}

public partial class CustomNoteTypes
{
    internal static bool FeaturesEnabled(object owner = null)
    {
        if (owner is NotesReader reader) return RuntimeCharts.Reader(reader).Enabled;
        if (owner is GameCtrl ctrl) return FeaturesForMonitor(ctrl.MonitorIndex);
        if (owner is GameMonitor monitor) return FeaturesForMonitor(monitor.MonitorIndex);
        if (owner is NoteBase note) return FeaturesForMonitor(note.MonitorId);
        if (owner is SlideRoot slide) return FeaturesForMonitor(slide.MonitorId);
        return RuntimeCharts.HasSelection ? RuntimeCharts.Current.Enabled : ChartFeatureGate.AnySelected;
    }
    internal static bool FeaturesForMonitor(int monitor)
    { var state = RuntimeMonitor(monitor); return state.Classified ? state.Enabled : ChartFeatureGate.SelectedMonitor(monitor); }
}

