using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using System.IO;
using SinmaiAlpha.Hosting;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Difficulty;

// A compact, theme-relative rendering recipe, not a PSD loader.
internal static class LevelTextStyle
{
    [Serializable] private sealed class Recipe
    {
        public float displayScale = .85f;
        public float displayOffsetX = -.06f;
        public float displayOffsetY = -.04f;
        public float chineseBold = .65f, chineseOffsetX = -2;
        public float fontSize = 53.33333f, tracking = 10, gradientAngle = -52, gradientSplit = .49756f;
        public float fillTintStart = .10f, fillTintEnd = .04f, strokeSize = 2, shadowSize = 4;
        public float strokeBrightness = .57f, shadowBrightness = .87f, shadowSpread = .61f;
    }
    private static Recipe recipe;
    private static Recipe Settings
    {
        get
        {
            if (recipe != null) return recipe;
            recipe = new Recipe();
            var path = FileSystem.ResolvePath("Sinmai-Alpha/ExtraDifficulty/level-text-style.json");
            try { if (File.Exists(path)) JsonUtility.FromJsonOverwrite(File.ReadAllText(path), recipe); }
            catch (Exception e) { MelonLoader.MelonLogger.Warning("[CustomLevelText] Default template used: " + e.Message); recipe = new Recipe(); }
            recipe.fontSize = Mathf.Clamp(recipe.fontSize, 8, 100); recipe.strokeSize = Mathf.Clamp(recipe.strokeSize, 0, 8);
            recipe.shadowSize = Mathf.Clamp(recipe.shadowSize, recipe.strokeSize, 12); recipe.gradientSplit = Mathf.Clamp(recipe.gradientSplit, .01f, .99f);
            recipe.shadowSpread = Mathf.Clamp01(recipe.shadowSpread); return recipe;
        }
    }
    internal static float DisplayScale => Mathf.Clamp(Settings.displayScale, .25f, 1);
    // Offset is a fraction of the native level region, so every UI scale follows it.
    internal static float DisplayOffsetX => Mathf.Clamp(Settings.displayOffsetX, -.5f, .5f);
    internal static float DisplayOffsetY => Mathf.Clamp(Settings.displayOffsetY, -.5f, .5f);
    private static TMP_FontAsset font, chineseFont, mixedFont;
    private static bool chineseFontChecked;
    private static Font systemHeiSource;
    private static string systemHeiPath;
    internal static UnityEngine.TextCore.LowLevel.FontEngineError LoadFontFace(Font source, int pointSize)
        => source == systemHeiSource && source != null
            ? UnityEngine.TextCore.LowLevel.FontEngine.LoadFontFace(systemHeiPath, pointSize)
            : UnityEngine.TextCore.LowLevel.FontEngine.LoadFontFace(source, pointSize);
    internal static UnityEngine.TextCore.LowLevel.FontEngineError LoadFontFace(Font source)
        => source == systemHeiSource && source != null
            ? UnityEngine.TextCore.LowLevel.FontEngine.LoadFontFace(systemHeiPath)
            : UnityEngine.TextCore.LowLevel.FontEngine.LoadFontFace(source);
    private static bool IsHan(uint code) => (code >= 0x3400 && code <= 0x4DBF) || (code >= 0x4E00 && code <= 0x9FFF) ||
        (code >= 0xF900 && code <= 0xFAFF) || (code >= 0x20000 && code <= 0x323AF);
    private static TMP_FontAsset FontForText(TMP_FontAsset native, string text)
    {
        var hasHan = false;
        for (var i = 0; i < text.Length; i++) { var code = char.ConvertToUtf32(text, i); if (IsHan((uint)code)) hasHan = true; if (char.IsHighSurrogate(text[i])) i++; }
        if (!hasHan) return native;
        if (!chineseFontChecked)
        {
            chineseFontChecked = true;
            var installed = Font.GetOSInstalledFontNames();
            var name = installed.FirstOrDefault(n => n.Equals("SimHei", StringComparison.OrdinalIgnoreCase) || n == "黑体");
            systemHeiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "simhei.ttf");
            if (name != null && File.Exists(systemHeiPath))
            {
                systemHeiSource = Font.CreateDynamicFontFromOSFont(name, 90);
                chineseFont = TMP_FontAsset.CreateFontAsset(systemHeiSource);
                chineseFont.name = "Sinmai-Alpha SimHei";
                mixedFont = Object.Instantiate(native);
                mixedFont.name = "Sinmai-Alpha Rodin with SimHei";
                // Own the list: never remove characters or add fallbacks to the game's shared font.
                HarmonyLib.AccessTools.Field(typeof(TMP_FontAsset), "m_CharacterTable").SetValue(mixedFont, native.characterTable.Where(c => !IsHan(c.unicode)).ToList());
                mixedFont.fallbackFontAssetTable = new List<TMP_FontAsset> { chineseFont };
                mixedFont.ReadFontAssetDefinition();
            }
            else MelonLoader.MelonLogger.Warning("[CustomLevelText] Windows SimHei is unavailable; keeping native font.");
        }
        return mixedFont != null ? mixedFont : native;
    }

    private static readonly Dictionary<string, Texture2D> Cache = new();
    internal static TMP_FontAsset NativeFont()
    {
        if (font != null) return font;
        var fonts = Resources.FindObjectsOfTypeAll<TMP_FontAsset>();
        font = fonts.FirstOrDefault(f => f.name.IndexOf("Rodin", StringComparison.OrdinalIgnoreCase) >= 0 && f.name.IndexOf("EB", StringComparison.OrdinalIgnoreCase) >= 0);
        if (font == null)
            { Resources.LoadAll<TMP_FontAsset>(""); font = Resources.FindObjectsOfTypeAll<TMP_FontAsset>().FirstOrDefault(f => f.name.IndexOf("Rodin", StringComparison.OrdinalIgnoreCase) >= 0 && f.name.IndexOf("EB", StringComparison.OrdinalIgnoreCase) >= 0); }
        if (font != null) font.ReadFontAssetDefinition();
        return font;
    }

    internal static Texture2D Get(string text, Color theme)
    {
        if (text.Length > 512) throw new ArgumentException("LevelText is longer than 512 characters");
        var native = NativeFont();
        if (native == null) return null;
        native = FontForText(native, text);
        var key = native.GetInstanceID() + "|" + ColorUtility.ToHtmlStringRGB(theme) + "|" + text;
        if (Cache.TryGetValue(key, out var result)) return result;
        if (Cache.Count >= 128)
        {
            var inUse = ExtraDifficulty.LevelTexturesInUse();
            foreach (var stale in Cache.Where(p => !inUse.Contains(p.Value)).ToArray())
            { Cache.Remove(stale.Key); Object.Destroy(stale.Value); }
        }
        result = Render(text, theme, native);
        if (result != null) Cache[key] = result;
        return result;
    }

    private static Texture2D Render(string content, Color theme, TMP_FontAsset native)
    {
        var go = new GameObject("Sinmai-Alpha level text baker", typeof(RectTransform));
        go.hideFlags = HideFlags.HideAndDontSave;
        var label = go.AddComponent<TextMeshPro>();

        try
        {
            label.font = native; label.fontStyle = FontStyles.Normal;
            label.fontSize = Settings.fontSize * 20; // TMP 3D uses 0.1 units/point; bake at 2x resolution.
            label.characterSpacing = Settings.tracking / 10; label.richText = false; label.enableWordWrapping = false;
            label.color = Color.white; label.alignment = TextAlignmentOptions.BottomLeft;
            label.rectTransform.sizeDelta = new Vector2(8192, 256);
            label.text = content; label.ForceMeshUpdate();
            // Baking is immediate; the scratch mesh must never enter the scene render.
            label.GetComponent<MeshRenderer>().enabled = false;
            var info = label.textInfo;
            if (info.characterCount == 0) return null;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var i = 0; i < info.characterCount; i++)
            {
                var c = info.characterInfo[i]; if (!c.isVisible) continue;
                min = Vector2.Min(min, c.bottomLeft); max = Vector2.Max(max, c.topRight);
            }
            if (max.x <= min.x || max.y <= min.y) return null;
            var pad = Mathf.CeilToInt(Settings.shadowSize * 2) + 4;
            var scale = Mathf.Min(1, 1000f / (max.x - min.x));
            var width = Mathf.CeilToInt((max.x - min.x) * scale) + pad * 2;
            var height = Mathf.CeilToInt((max.y - min.y) * scale) + pad * 2;
            var pixels = new Color32[width * height];
            var shared = label.fontSharedMaterials;
            // Dynamic SimHei adds glyphs to its atlas between strings. Refresh just
            // those masks; native Rodin's static atlas remains cached.
            var dynamicTextures = new HashSet<Texture>();
            for (var i = 0; i < info.characterCount; i++)
                if (info.characterInfo[i].isVisible && info.characterInfo[i].fontAsset == chineseFont)
                    dynamicTextures.Add(shared[info.characterInfo[i].materialReferenceIndex].mainTexture);
            foreach (var texture in dynamicTextures) Atlases.Remove(texture);
            for (var i = 0; i < info.characterCount; i++)
            {
                var character = info.characterInfo[i]; if (!character.isVisible) continue;
                if (character.textElement == null || character.textElement.unicode != char.ConvertToUtf32(content, character.index))
                    throw new ArgumentException("Native font does not contain U+" + char.ConvertToUtf32(content, character.index).ToString("X"));
                var isHan = IsHan(character.textElement.unicode);
                var mesh = info.meshInfo[character.materialReferenceIndex]; var vi = character.vertexIndex;
                var atlas = ReadAtlas(shared[character.materialReferenceIndex].mainTexture);
                var bl = (Vector2)mesh.vertices[vi]; var tr = (Vector2)mesh.vertices[vi + 2];
                var uv0 = mesh.uvs0[vi]; var uv1 = mesh.uvs0[vi + 2];
                var left = pad + (bl.x - min.x) * scale; var bottom = pad + (bl.y - min.y) * scale;
                var right = pad + (tr.x - min.x) * scale; var top = pad + (tr.y - min.y) * scale;
                // Shift the glyph inside the existing image bounds: mixed text's
                // digits and the level counter's overall fit must not move.
                if (isHan) { var shift = Mathf.Clamp(Settings.chineseOffsetX, -3, 3) * 2 * scale; left += shift; right += shift; }
                if (right <= left || top <= bottom) continue;
                var du = (uv1.x - uv0.x) / (right - left); var dv = (uv1.y - uv0.y) / (top - bottom);
                for (var y = Mathf.Max(0, Mathf.FloorToInt(bottom)); y < Mathf.Min(height, Mathf.CeilToInt(top)); y++)
                for (var x = Mathf.Max(0, Mathf.FloorToInt(left)); x < Mathf.Min(width, Mathf.CeilToInt(right)); x++)
                {
                    var u = uv0.x + (x + .5f - left) * du; var v = uv0.y + (y + .5f - bottom) * dv;
                    var distance = atlas.Sample(u, v);
                    var dx = Mathf.Abs(atlas.Sample(u + du * .5f, v) - atlas.Sample(u - du * .5f, v));
                    var dy = Mathf.Abs(atlas.Sample(u, v + dv * .5f) - atlas.Sample(u, v - dv * .5f));
                    var aa = Mathf.Max(.002f, (dx + dy) * .5f);
                    // Thicken only Han strokes in reference pixels, leaving Latin
                    // glyphs and the shared outline/shadow recipe unchanged.
                    var threshold = .5f - (isHan ? Mathf.Clamp(Settings.chineseBold, 0, 2) * 2 * scale * Mathf.Sqrt(dx * dx + dy * dy) : 0);
                    var coverage = Mathf.SmoothStep(0, 1, Mathf.InverseLerp(threshold - aa, threshold + aa, distance));
                    var index = y * width + x;
                    pixels[index] = new Color(1, 1, 1, Mathf.Max(pixels[index].a / 255f, coverage));
                }
            }
            if (!pixels.Any(p => p.a > 32)) return null;
            var styled = Style(pixels, width, height, theme, scale);
            var output = new Texture2D(width, height, TextureFormat.RGBA32, false) { name = "Sinmai-Alpha level " + content, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            output.SetPixels32(styled); output.Apply(false, true); return output;
        }
        finally
        {
            go.SetActive(false); Object.Destroy(go);
        }
    }

    private sealed class Atlas
    {
        public int Width, Height; public byte[] Alpha;
        public float Sample(float u, float v)
        {
            var x = Mathf.Clamp(u * Width - .5f, 0, Width - 1); var y = Mathf.Clamp(v * Height - .5f, 0, Height - 1);
            var x0 = (int)x; var y0 = (int)y; var x1 = Mathf.Min(x0 + 1, Width - 1); var y1 = Mathf.Min(y0 + 1, Height - 1);
            return Mathf.Lerp(Mathf.Lerp(Alpha[y0 * Width + x0], Alpha[y0 * Width + x1], x - x0),
                Mathf.Lerp(Alpha[y1 * Width + x0], Alpha[y1 * Width + x1], x - x0), y - y0) / 255f;
        }
    }
    private static readonly Dictionary<Texture, Atlas> Atlases = new();
    private static Atlas ReadAtlas(Texture source)
    {
        if (Atlases.TryGetValue(source, out var atlas)) return atlas;
        var rt = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        var previous = RenderTexture.active; Texture2D copy = null;
        try
        {
            Graphics.Blit(source, rt); RenderTexture.active = rt;
            copy = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, true);
            copy.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0); copy.Apply();
            var colors = copy.GetPixels32(); var alpha = new byte[colors.Length];
            for (var i = 0; i < colors.Length; i++) alpha[i] = colors[i].a;
            atlas = new Atlas { Width = source.width, Height = source.height, Alpha = alpha }; Atlases[source] = atlas; return atlas;
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); if (copy != null) Object.Destroy(copy); }
    }

    internal static Color32[] Style(Color32[] source, int width, int height, Color theme, float scale)
    {
        var result = new Color32[source.Length];
        // Relative colors work for every theme; the PSD's orange is not baked in.
        var stroke = Color.Lerp(Color.black, theme, Settings.strokeBrightness);
        var shadow = Color.Lerp(Color.black, theme, Settings.shadowBrightness);
        var fillStart = Color.Lerp(Color.white, theme, Settings.fillTintStart);
        var fillEnd = Color.Lerp(Color.white, theme, Settings.fillTintEnd);
        var radius = Mathf.Max(1, Mathf.CeilToInt(Settings.shadowSize * 2 * scale));
        var outline = Settings.strokeSize * 2 * scale;
        var offsets = new List<Vector4>();
        for (var dy = -radius; dy <= radius; dy++) for (var dx = -radius; dx <= radius; dx++)
        {
            var distance = Mathf.Sqrt(dx * dx + dy * dy); if (distance > radius) continue;
            offsets.Add(new Vector4(dx, dy, Mathf.Clamp01(outline + .5f - distance),
                Mathf.Clamp01((radius + .5f - distance) / Mathf.Max(1, radius * (1 - Settings.shadowSpread)))));
        }
        var gx = Mathf.Cos(Settings.gradientAngle * Mathf.Deg2Rad); var gy = Mathf.Sin(Settings.gradientAngle * Mathf.Deg2Rad);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var a = source[y * width + x].a / 255f;
            var outer = 0f; var haze = 0f;
            if (a < 1) foreach (var offset in offsets)
            {
                var xx = x + (int)offset.x; var yy = y + (int)offset.y;
                if (xx < 0 || xx >= width || yy < 0 || yy >= height) continue;
                var v = source[yy * width + xx].a / 255f;
                outer = Mathf.Max(outer, v * offset.z); haze = Mathf.Max(haze, v * offset.w);
            }
            // -52 degree linear gradient, with the PSD's hard stop near 50%.
            var projection = x * gx + y * gy;
            var t = (projection - Mathf.Min(0, gx * (width - 1)) - Mathf.Min(0, gy * (height - 1))) /
                Mathf.Max(.001f, Mathf.Abs(gx) * (width - 1) + Mathf.Abs(gy) * (height - 1));
            var fill = t < Settings.gradientSplit ? Color.white : Color.Lerp(fillStart, fillEnd, (t - Settings.gradientSplit) / (1 - Settings.gradientSplit));
            var alpha = a + outer * (1 - a) + haze * (1 - outer) * (1 - a);
            var rgb = fill * a + stroke * (outer * (1 - a)) + shadow * (haze * (1 - outer) * (1 - a));
            if (alpha > 0) rgb /= alpha;
            rgb.a = alpha; result[y * width + x] = rgb;
        }
        return result;
    }
}
