using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using SinmaiAlpha.Assets;
using SinmaiAlpha.Hosting;
using UnityEngine;
using UnityEngine.UI;

namespace SinmaiAlpha.Difficulty;

public static partial class ExtraDifficulty
{
    private sealed class LevelLabel { public Texture2D Texture; }
    private static readonly ConditionalWeakTable<SpriteCounter, LevelLabel> LevelLabels = new();
    private static readonly Dictionary<string, ChartFlags> LevelFlags = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> LevelTextWarnings = new();

    private static ChartFlags CustomLevelFlags(MusicIdentity identity, int difficulty)
    {
        if (identity == null || identity.Utage || identity.Id <= 0 || identity.Id >= 100000 || difficulty < 0 || difficulty > 4) return null;
        var music = Singleton<DataManager>.Instance.GetMusic(identity.Id);
        if (music?.notesData == null || difficulty >= music.notesData.Count) return null;
        var path = music.notesData[difficulty]?.file?.path;
        if (string.IsNullOrEmpty(path) || SinmaiAlpha.Notes.ChartFeatureGate.IsA000(path)) return null;
        var chart = FileSystem.ResolvePath(path);
        var marker = Path.ChangeExtension(chart, ".ExtraDifficulty.flag");
        if (!File.Exists(marker) && difficulty == Master) marker = Path.Combine(Path.GetDirectoryName(chart), MarkerFileName);
        return ReadLevelFlags(marker);
    }
    private static ChartFlags ReadLevelFlags(string marker)
    {
        if (LevelFlags.TryGetValue(marker, out var flags)) return flags;
        try { flags = File.Exists(marker) ? ChartFlags.Parse(File.ReadAllText(marker)) : null; }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
        { MelonLogger.Warning("[CustomLevelText] " + e.Message); }
        LevelFlags[marker] = flags; return flags;
    }
    private static void BindLevelText(object owner, int difficulty, bool utage, DifficultyTheme theme)
    {
        var flags = !utage ? CustomLevelFlags(Identity(owner as Component), difficulty) : null;
        Texture2D texture = null;
        if (flags?.CustomLevelText == true && !string.IsNullOrWhiteSpace(flags.LevelText))
        {
            try
            {
                var color = NativeLevelColor(difficulty);
                if (theme != null) ColorUtility.TryParseHtmlString(theme.Color, out color);
                texture = LevelTextStyle.Get(flags.LevelText, color);
                if (texture == null && LevelTextWarnings.Add(flags.LevelText))
                    MelonLogger.Warning("[CustomLevelText] Native Rodin font or rendered glyphs unavailable; keeping native level: " + flags.LevelText);
            }
            catch (Exception e)
            { if (LevelTextWarnings.Add(flags.LevelText)) MelonLogger.Warning("[CustomLevelText] Keeping native level: " + e.Message); }
        }
        foreach (var name in new[] { "_digitLevel", "_doubleDigitLevel", "_difficultySingle", "_difficultyDouble", "_singleLevel", "_doubleLevel" })
        {
            var counter = Field<SpriteCounter>(owner, name); if (counter == null) continue;
            SetLevelLabel(counter, texture);
        }
    }
    private static Color NativeLevelColor(int difficulty)
    {
        var colors = new[] { "#70D43B", "#F7B708", "#FF828C", "#9F51DC", "#DFCCF1" };
        ColorUtility.TryParseHtmlString(colors[Mathf.Clamp(difficulty, 0, 4)], out var color); return color;
    }
    internal static HashSet<Texture2D> LevelTexturesInUse()
    {
        var textures = new HashSet<Texture2D>();
        foreach (var counter in Resources.FindObjectsOfTypeAll<SpriteCounter>())
            if (LevelLabels.TryGetValue(counter, out var label) && label.Texture != null) textures.Add(label.Texture);
        return textures;
    }
    private static void SetLevelLabel(SpriteCounter counter, Texture2D texture)
    {
        if (texture == null)
        {
            if (LevelLabels.Remove(counter)) counter.SetAllDirty();
            return;
        }
        var label = LevelLabels.GetValue(counter, _ => new LevelLabel());
        if (label.Texture == texture) return;
        label.Texture = texture; counter.SetAllDirty();
    }

    [HarmonyPatch(typeof(SpriteCounter), "mainTexture", MethodType.Getter)]
    public static class CustomLevelTexturePatch
    {
        public static void Postfix(SpriteCounter __instance, ref Texture __result)
        { if (LevelLabels.TryGetValue(__instance, out var label) && label.Texture != null) __result = label.Texture; }
    }
    [HarmonyPatch(typeof(SpriteCounter), "OnPopulateMesh")]
    public static class CustomLevelMeshPatch
    {
        public static void Postfix(SpriteCounter __instance, VertexHelper vh)
        {
            if (!LevelLabels.TryGetValue(__instance, out var label) || label.Texture == null || vh.currentVertCount == 0) return;
            var min = new Vector2(float.MaxValue, float.MaxValue); var max = new Vector2(float.MinValue, float.MinValue);
            var vertex = new UIVertex(); var color = Color.white;
            for (var i = 0; i < vh.currentVertCount; i++)
            {
                vh.PopulateUIVertex(ref vertex, i);
                min = Vector2.Min(min, vertex.position); max = Vector2.Max(max, vertex.position);
                if (i == 0) color = vertex.color;
            }
            var fit = Mathf.Min((max.x - min.x) / label.Texture.width, (max.y - min.y) / label.Texture.height) * LevelTextStyle.DisplayScale;
            if (fit <= 0) return;
            var center = (min + max) * .5f;
            center.x += (max.x - min.x) * LevelTextStyle.DisplayOffsetX;
            center.y += (max.y - min.y) * LevelTextStyle.DisplayOffsetY;
            var half = new Vector2(label.Texture.width, label.Texture.height) * (.5f * fit);
            vh.Clear();
            vh.AddVert(new Vector3(center.x - half.x, center.y - half.y), color, new Vector2(0, 0));
            vh.AddVert(new Vector3(center.x - half.x, center.y + half.y), color, new Vector2(0, 1));
            vh.AddVert(new Vector3(center.x + half.x, center.y + half.y), color, new Vector2(1, 1));
            vh.AddVert(new Vector3(center.x + half.x, center.y - half.y), color, new Vector2(1, 0));
            vh.AddTriangle(0, 1, 2); vh.AddTriangle(2, 3, 0);
        }
    }
    // Unity's legacy OS Font exposes no embedded font bytes to TextCore.
    // Redirect only our SimHei source to its installed file, keeping TMP's
    // normal atlas packing and every other font's loader unchanged.
    [HarmonyPatch]
    public static class CustomLevelHeiFontPatch
    {
        public static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (var method in typeof(TMPro.TMP_FontAsset).GetMethods(System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance))
                if (method.DeclaringType == typeof(TMPro.TMP_FontAsset) && method.GetMethodBody() != null &&
                    (method.Name == "CreateFontAsset" || method.Name.StartsWith("TryAddCharacter", StringComparison.Ordinal))) yield return method;
        }
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.operand is System.Reflection.MethodInfo method &&
                    method.DeclaringType == typeof(UnityEngine.TextCore.LowLevel.FontEngine) && method.Name == "LoadFontFace")
                {
                    var parameters = method.GetParameters();
                    if (parameters.Length >= 1 && parameters[0].ParameterType == typeof(Font))
                        instruction.operand = AccessTools.Method(typeof(LevelTextStyle), "LoadFontFace",
                            parameters.Length == 1 ? new[] { typeof(Font) } : new[] { typeof(Font), typeof(int) });
                }
                yield return instruction;
            }
        }
    }

}
