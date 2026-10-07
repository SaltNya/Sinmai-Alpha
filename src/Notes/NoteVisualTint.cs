using System;
using System.Collections.Generic;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private sealed class VisualTexture
    {
        public Color32[] Pixels;
        public int Width, Height;
    }
    private static readonly Dictionary<Sprite, VisualTexture> VisualSourceTextures = new();
    private static readonly Dictionary<(Sprite Sprite, int Rgb, Color32 Vertex), Sprite> VisualTintSprites = new();
    private static readonly HashSet<Sprite> VisualTintFailures = new();
    private static readonly Dictionary<Sprite, Sprite> BorrowedGraySprites = new();

    private static Sprite GetBorrowedGraySprite(Sprite source)
    {
        if (BorrowedGraySprites.TryGetValue(source, out var cached)) return cached;
        try
        {
            var original = ReadVisualTexture(source);
            var pixels = new Color32[original.Pixels.Length];
            for (var i = 0; i < pixels.Length; i++)
            {
                var input = (Color)original.Pixels[i];
                var luma = input.r * .299f + input.g * .587f + input.b * .114f;
                pixels[i] = new Color(luma, luma, luma, input.a);
            }
            var texture = new Texture2D(original.Width, original.Height, TextureFormat.RGBA32, false)
                { name = "AquaMai Borrowed Mine", wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels); texture.Apply(false, false);
            var pivot = new Vector2(source.pivot.x / source.rect.width, source.pivot.y / source.rect.height);
            var sprite = Sprite.Create(texture, new Rect(0, 0, original.Width, original.Height), pivot,
                source.pixelsPerUnit, 0, SpriteMeshType.Tight, source.border);
            CopyVisualSpriteGeometry(sprite, source);
            texture.Apply(false, true);
            BorrowedGraySprites[source] = sprite; return sprite;
        }
        catch (Exception e)
        { MelonLogger.Warning("[Borrowed Trajectory] Cannot gray carrier: " + e.Message); return source; }
    }

    // Sinmai does not ship NoteColorTint. Bake its RGB operation into a cached
    // sprite instead of depending on an incompatible preview AssetBundle/shader.
    // Opacity, native hold gauge materials and break flashing remain live.
    private static Sprite GetVisualTintSprite(Sprite source, int rgb, Color vertex)
    {
        Color32 keyColor = new Color(vertex.r, vertex.g, vertex.b, 1);
        var key = (source, rgb, keyColor);
        if (VisualTintSprites.TryGetValue(key, out var cached)) return cached;
        if (VisualTintFailures.Contains(source)) return source;
        try
        {
            if (!VisualSourceTextures.TryGetValue(source, out var original))
            {
                original = ReadVisualTexture(source); VisualSourceTextures.Add(source, original);
            }
            var target = new Color(((rgb >> 16) & 255) / 255f, ((rgb >> 8) & 255) / 255f, (rgb & 255) / 255f, 1);
            Color.RGBToHSV(target, out var targetHue, out var targetSat, out var targetValue);
            var pixels = new Color32[original.Pixels.Length];
            var vertexRgb = (Color)keyColor;
            for (var i = 0; i < pixels.Length; i++)
            {
                var input = (Color)original.Pixels[i];
                if (input.a < .01f) { pixels[i] = new Color(0, 0, 0, input.a); continue; }
                input.r *= vertexRgb.r; input.g *= vertexRgb.g; input.b *= vertexRgb.b;
                Color.RGBToHSV(input, out _, out var sourceSat, out var sourceValue);
                var darkGuard = VisualSmooth(.04f, .16f, sourceValue);
                var satGate = VisualSmooth(.05f, .25f, sourceSat);
                var detailGain = .35f * (1 - targetValue);
                var shapedValue = Mathf.Clamp01(sourceValue * targetValue + detailGain * (sourceValue - .5f));
                var tint = Color.HSVToRGB(targetHue, Mathf.Clamp01(sourceSat * targetSat), shapedValue);
                var chroma = Color.Lerp(input, tint, darkGuard * satGate);
                var luma = input.r * .299f + input.g * .587f + input.b * .114f;
                var gray = Mathf.Clamp01(luma * (targetValue * 2) + detailGain * (luma - .5f));
                var achromatic = Color.Lerp(input, new Color(gray, gray, gray, input.a), darkGuard);
                var output = Color.Lerp(chroma, achromatic, 1 - VisualSmooth(.05f, .20f, targetSat));
                output.a = input.a; pixels[i] = output;
            }
            var texture = new Texture2D(original.Width, original.Height, TextureFormat.RGBA32, false)
                { name = "AquaMai Note Tint", filterMode = source.texture.filterMode, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels); texture.Apply(false, false);
            var pivot = new Vector2(source.pivot.x / source.rect.width, source.pivot.y / source.rect.height);
            var sprite = Sprite.Create(texture, new Rect(0, 0, original.Width, original.Height), pivot,
                source.pixelsPerUnit, 0, SpriteMeshType.Tight, source.border);
            CopyVisualSpriteGeometry(sprite, source);
            texture.Apply(false, true);
            sprite.name = source.name + " AquaMai " + rgb.ToString("X6");
            VisualTintSprites.Add(key, sprite); return sprite;
        }
        catch (Exception e)
        {
            VisualTintFailures.Add(source);
            MelonLogger.Warning("[Note Visual] Cannot recolor " + source.name + ": " + e.Message);
            return source;
        }
    }
    private static float VisualSmooth(float low, float high, float value)
    {
        var t = Mathf.Clamp01((value - low) / (high - low)); return t * t * (3 - 2 * t);
    }
    private static VisualTexture ReadVisualTexture(Sprite sprite)
    {
        // UV/vertex mapping also handles tight-packed and rotated atlas sprites:
        // textureRect/textureRectOffset throw for tight packing in Unity.
        var vertices = sprite.vertices; var uvs = sprite.uv; var triangles = sprite.triangles;
        var minU = 1f; var minV = 1f; var maxU = 0f; var maxV = 0f;
        foreach (var uv in uvs) { minU = Mathf.Min(minU, uv.x); minV = Mathf.Min(minV, uv.y); maxU = Mathf.Max(maxU, uv.x); maxV = Mathf.Max(maxV, uv.y); }
        var left = Mathf.FloorToInt(minU * sprite.texture.width); var bottom = Mathf.FloorToInt(minV * sprite.texture.height);
        var right = Mathf.CeilToInt(maxU * sprite.texture.width); var top = Mathf.CeilToInt(maxV * sprite.texture.height);
        var rect = new Rect(left, bottom, right - left, top - bottom);
        var width = Mathf.RoundToInt(sprite.rect.width); var height = Mathf.RoundToInt(sprite.rect.height);
        var cropWidth = Mathf.RoundToInt(rect.width); var cropHeight = Mathf.RoundToInt(rect.height);
        var readable = new Texture2D(cropWidth, cropHeight, TextureFormat.RGBA32, false);
        var previous = RenderTexture.active;
        var buffer = RenderTexture.GetTemporary(sprite.texture.width, sprite.texture.height, 0, RenderTextureFormat.ARGB32);
        Color32[] crop;
        try
        {
            Graphics.Blit(sprite.texture, buffer); RenderTexture.active = buffer;
            readable.ReadPixels(rect, 0, 0, false); readable.Apply(); crop = readable.GetPixels32();
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(buffer); Object.Destroy(readable); }
        var result = new VisualTexture { Width = width, Height = height, Pixels = new Color32[width * height] };
        for (var triangle = 0; triangle < triangles.Length; triangle += 3)
        {
            var ai = triangles[triangle]; var bi = triangles[triangle + 1]; var ci = triangles[triangle + 2];
            var a = vertices[ai] * sprite.pixelsPerUnit + sprite.pivot;
            var b = vertices[bi] * sprite.pixelsPerUnit + sprite.pivot;
            var c = vertices[ci] * sprite.pixelsPerUnit + sprite.pivot;
            var denominator = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Mathf.Abs(denominator) < .000001f) continue;
            var x0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
            var x1 = Mathf.Min(width, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            var y0 = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
            var y1 = Mathf.Min(height, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
            for (var y = y0; y < y1; y++) for (var x = x0; x < x1; x++)
            {
                var wa = ((b.y - c.y) * (x + .5f - c.x) + (c.x - b.x) * (y + .5f - c.y)) / denominator;
                var wb = ((c.y - a.y) * (x + .5f - c.x) + (a.x - c.x) * (y + .5f - c.y)) / denominator;
                var wc = 1 - wa - wb;
                if (wa < -.00001f || wb < -.00001f || wc < -.00001f) continue;
                var uv = uvs[ai] * wa + uvs[bi] * wb + uvs[ci] * wc;
                var sx = Mathf.Clamp(Mathf.FloorToInt(uv.x * sprite.texture.width) - left, 0, cropWidth - 1);
                var sy = Mathf.Clamp(Mathf.FloorToInt(uv.y * sprite.texture.height) - bottom, 0, cropHeight - 1);
                result.Pixels[y * width + x] = crop[sy * cropWidth + sx];
            }
        }
        return result;
    }
    private static void ClearVisualTintCache()
    {
        foreach (var sprite in BorrowedGraySprites.Values)
            if (sprite != null) { Object.Destroy(sprite.texture); Object.Destroy(sprite); }
        BorrowedGraySprites.Clear();
        foreach (var sprite in VisualTintSprites.Values)
        {
            if (sprite == null) continue;
            Object.Destroy(sprite.texture); Object.Destroy(sprite);
        }
        VisualTintSprites.Clear(); VisualSourceTextures.Clear(); VisualTintFailures.Clear();
        foreach (var overlay in VisualOverlays.Values)
        { overlay.Restore(); if (overlay.Render != null) Object.Destroy(overlay.Render.gameObject); }
        VisualOverlays.Clear(); VisualOverlayRenderers.Clear();
        ReleaseNoteTintMaterials();
    }
}
