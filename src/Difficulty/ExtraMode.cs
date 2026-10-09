using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MelonLoader;
using Monitor;
using Monitor.MusicSelect.ChainList;
using SinmaiAlpha.Hosting;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Difficulty;

// Cosmetic only: never changes ScoreKind, music IDs or gameplay feature gates.
public static class ExtraMode
{
    internal const string Marker = "// SINMAI_ALPHA_MODE\t";
    private sealed class Stamp { public long Length, Ticks; public int CheckedAt; public string Mode; }
    private sealed class Lease { public Sprite Original, Applied; }
    private static readonly Dictionary<string, Stamp> Charts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Sprite> Sprites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<Image, Lease> Images = new();
    private static readonly string[] Screens = { "MSS", "TST", "TTR", "UPE" };
    [ThreadStatic] private static bool applying;

    public static void OnBeforePatch(HarmonyLib.Harmony harmony)
    {
        // Mode ownership must also work with custom difficulty skins disabled.
        if (!Settings.Current.ExtraDifficulty)
            foreach (var type in new[] { typeof(ExtraDifficulty.CardDataPatch), typeof(ExtraDifficulty.CentralCardPatch),
                typeof(ExtraDifficulty.MenuPatch), typeof(ExtraDifficulty.InformationDataPatch), typeof(ExtraDifficulty.ResultTrackDataPatch) })
                harmony.PatchAll(type);
    }

    internal static string ReadMode(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            path = FileSystem.ResolvePath(path);
            var now = Environment.TickCount;
            // Animation callbacks run every frame. Do not stat the HDD on each
            // badge update; metadata is revalidated at most once per second.
            Charts.TryGetValue(path, out var previous);
            if (previous != null && unchecked(now - previous.CheckedAt) >= 0 && unchecked(now - previous.CheckedAt) < 1000) return previous.Mode;
            var info = new FileInfo(path);
            if (!info.Exists) { Charts[path] = new Stamp { Length = -1, CheckedAt = now }; return null; }
            if (previous != null && previous.Length == info.Length && previous.Ticks == info.LastWriteTimeUtc.Ticks)
            { previous.CheckedAt = now; return previous.Mode; }
            string mode = null;
            using (var reader = File.OpenText(path))
                for (var i = 0; i < 128; i++)
                {
                    var line = reader.ReadLine(); if (line == null) break;
                    if (!line.StartsWith(Marker, StringComparison.Ordinal)) continue;
                    var value = line.Substring(Marker.Length).Trim();
                    if (value.Length > 0 && value.Length <= 40 && Array.TrueForAll(value.ToCharArray(), c =>
                        c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '_' || c == '-')) mode = value;
                    break;
                }
            Charts[path] = new Stamp { Length = info.Length, Ticks = info.LastWriteTimeUtc.Ticks, Mode = mode, CheckedAt = now };
            return mode;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
        { return null; }
    }

    private static string IconKey(string name)
    {
        // 1.70 uses dedicated RSL badges for both results and KOP track history.
        if (name == "UI_RSL_MBase_Parts_01" || name == "UI_RSL_MBase_Parts_02") return "UI_TTR_Infoicon_";
        if (name == null || !name.StartsWith("UI_", StringComparison.Ordinal) || name.IndexOf("_Infoicon_", StringComparison.Ordinal) < 0) return null;
        foreach (var screen in Screens)
        {
            var prefix = "UI_" + screen + "_Infoicon_";
            if (name == prefix + "StandardMode" || name == prefix + "DeluxeMode") return prefix;
        }
        return null;
    }

    private static Sprite GetSprite(string mode, string key, Sprite native)
    {
        var cacheKey = mode + "/" + key + "/" + native.GetInstanceID();
        if (Sprites.TryGetValue(cacheKey, out var cached)) return cached;
        var path = Path.Combine(AssetFiles.Root, "ExtraMode", mode, key + mode + "Mode.png");
        if (native.name.StartsWith("UI_RSL_MBase_Parts_", StringComparison.Ordinal))
        {
            var resultPath = Path.Combine(AssetFiles.Root, "ExtraMode", mode, "UI_RSL_MBase_Parts_" + mode + ".png");
            if (File.Exists(resultPath)) path = resultPath;
        }
        Sprite sprite = null;
        Texture2D texture = null;
        try
        {
            if (File.Exists(path))
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (texture.LoadImage(File.ReadAllBytes(path)))
                {
                    texture = RestoreCanvas(texture, native);
                    sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height),
                        new Vector2(native.pivot.x / native.rect.width, native.pivot.y / native.rect.height),
                        native.pixelsPerUnit, 0, SpriteMeshType.FullRect, native.border);
                    sprite.name = key + mode + "Mode";
                }
                else Object.Destroy(texture);
            }
        }
        catch (Exception error)
        { if (texture != null) Object.Destroy(texture); MelonLogger.Warning("[ExtraMode] " + path + ": " + error.Message); }
        return Sprites[cacheKey] = sprite;
    }

    // Exported PNGs contain the trimmed pixels, whereas native UI sprites
    // retain the original canvas (e.g. MSS is 116x44 with a 116x32 crop).
    // Recreate that transparent canvas instead of stretching the crop to fill it.
    private static Texture2D RestoreCanvas(Texture2D source, Sprite native)
    {
        var width = Mathf.RoundToInt(native.rect.width);
        var height = Mathf.RoundToInt(native.rect.height);
        if (source.width == width && source.height == height) return source;
        var offset = native.textureRectOffset;
        var crop = native.textureRect;
        var x = Mathf.RoundToInt(offset.x); var y = Mathf.RoundToInt(offset.y);
        var cropWidth = Mathf.Min(width - x, Mathf.RoundToInt(crop.width));
        var cropHeight = Mathf.Min(height - y, Mathf.RoundToInt(crop.height));
        if (native.name.StartsWith("UI_RSL_MBase_Parts_", StringComparison.Ordinal))
        {
            // A dedicated RSL asset is optional. Fit the supplied TTR badge in
            // the smaller result box without squeezing its lettering.
            var scale = Mathf.Min((float)cropWidth / source.width, (float)cropHeight / source.height);
            var fitWidth = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
            var fitHeight = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
            x += (cropWidth - fitWidth) / 2; y += (cropHeight - fitHeight) / 2;
            cropWidth = fitWidth; cropHeight = fitHeight;
        }
        var output = new Texture2D(width, height, TextureFormat.RGBA32, false);
        output.SetPixels32(new Color32[width * height]);
        if (source.width == cropWidth && source.height == cropHeight)
            output.SetPixels(x, y, cropWidth, cropHeight, source.GetPixels());
        else
        {
            // Higher resolution replacements keep the same native placement.
            var pixels = new Color[cropWidth * cropHeight];
            for (var row = 0; row < cropHeight; row++)
                for (var col = 0; col < cropWidth; col++)
                    pixels[row * cropWidth + col] = source.GetPixelBilinear((col + .5f) / cropWidth, (row + .5f) / cropHeight);
            output.SetPixels(x, y, cropWidth, cropHeight, pixels);
        }
        output.Apply(false, true);
        Object.Destroy(source);
        return output;
    }

    internal static void Apply(Image image)
    {
        if (applying || image == null) return;
        Images.TryGetValue(image, out var lease);
        var native = lease != null && image.sprite == lease.Applied ? lease.Original : image.sprite;
        var key = IconKey(native?.name);
        if (key == null) { if (lease != null) { lease.Applied = null; lease.Original = null; } return; }
        var mode = ReadMode(ExtraDifficulty.ModeChart(image, native.name));
        var replacement = mode == null ? null : GetSprite(mode, key, native);
        if (lease == null) lease = Images.GetValue(image, _ => new Lease());
        lease.Original = native; lease.Applied = replacement;
        var desired = replacement ?? native;
        if (image.sprite == desired) return;
        applying = true;
        try { image.sprite = desired; }
        finally { applying = false; }
    }

    private static void Refresh(Component owner)
    {
        if (owner == null) return;
        foreach (var image in owner.GetComponentsInChildren<Image>(true)) Apply(image);
    }

    [HarmonyPatch(typeof(Image), "sprite", MethodType.Setter)]
    public static class SpritePatch
    {
        [HarmonyPostfix] public static void Postfix(Image __instance) => Apply(__instance);
    }

    [HarmonyPatch(typeof(Graphic), "OnDidApplyAnimationProperties")]
    public static class AnimationPatch
    {
        [HarmonyPostfix] public static void Postfix(Graphic __instance) { if (__instance is Image image) Apply(image); }
    }

    [HarmonyPatch]
    public static class RefreshPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(MusicChainCardObejct), "SetMusicData");
            yield return AccessTools.Method(typeof(MusicChainCardObejct), "SetScoreKind");
            yield return AccessTools.Method(typeof(MusicChainCardObejct), "SetDifficulty");
            yield return AccessTools.Method(typeof(MusicInfomationController), "SetMusicData");
            yield return AccessTools.Method(typeof(MusicInfomationController), "SetScoreKind");
            yield return AccessTools.Method(typeof(KOP_ResultTrackData), "SetMusicData");
            yield return AccessTools.Method(typeof(TrackStartMonitor), "SetTrackStart");
            yield return AccessTools.Method(typeof(ResultMonitor), "SetGameScoreType");
        }
        [HarmonyPostfix, HarmonyPriority(Priority.Last)] public static void Postfix(Component __instance) => Refresh(__instance);
    }
}
