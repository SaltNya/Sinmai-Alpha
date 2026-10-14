using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
    // Color32 does not implement IEquatable in this Unity version. Packing it
    // avoids boxing the vertex color on every cached sprite lookup.
    private readonly struct VisualTintKey : IEquatable<VisualTintKey>
    {
        public readonly Sprite Sprite;
        public readonly int Rgb, Vertex;
        public VisualTintKey(Sprite sprite, int rgb, Color32 vertex)
        {
            Sprite = sprite; Rgb = rgb;
            Vertex = vertex.r | vertex.g << 8 | vertex.b << 16 | vertex.a << 24;
        }
        public bool Equals(VisualTintKey other) => ReferenceEquals(Sprite, other.Sprite) && Rgb == other.Rgb && Vertex == other.Vertex;
        public override bool Equals(object value) => value is VisualTintKey other && Equals(other);
        public override int GetHashCode() => unchecked((RuntimeHelpers.GetHashCode(Sprite) * 31 + Rgb) * 31 + Vertex);
    }
    private static readonly Dictionary<VisualTintKey, Sprite> VisualTintSprites = new();
    private static readonly HashSet<Sprite> VisualTintFailures = new();
    private static readonly Dictionary<Sprite, Sprite> BorrowedGraySprites = new();

    private static Sprite GetBorrowedGraySprite(Sprite source)
    {
        if (BorrowedGraySprites.TryGetValue(source, out var cached)) return cached;
        try
        {
            var original = GetVisualSourceTexture(source);
            var pixels = VisualPixelBuffer(original.Pixels.Length);
            GrayVisualPixels(original.Pixels, pixels);
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
        var key = new VisualTintKey(source, rgb, keyColor);
        if (VisualTintSprites.TryGetValue(key, out var cached)) return cached;
        if (VisualTintFailures.Contains(source)) return source;
        var bakeStarted = Hosting.GameplayDiagnostics.Enabled ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        try
        {
            var original = GetVisualSourceTexture(source);
            var pixels = VisualPixelBuffer(original.Pixels.Length);
            TintVisualPixels(original.Pixels, pixels, rgb, keyColor);
            var texture = new Texture2D(original.Width, original.Height, TextureFormat.RGBA32, false)
                { name = "AquaMai Note Tint", filterMode = source.texture.filterMode, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(pixels); texture.Apply(false, false);
            var pivot = new Vector2(source.pivot.x / source.rect.width, source.pivot.y / source.rect.height);
            var sprite = Sprite.Create(texture, new Rect(0, 0, original.Width, original.Height), pivot,
                source.pixelsPerUnit, 0, SpriteMeshType.Tight, source.border);
            CopyVisualSpriteGeometry(sprite, source);
            texture.Apply(false, true);
            sprite.name = source.name + " AquaMai " + rgb.ToString("X6");
            VisualTintSprites.Add(key, sprite);
            if (bakeStarted != 0) Hosting.GameplayDiagnostics.TintBake(source.name, rgb, original.Width, original.Height, bakeStarted);
            return sprite;
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
    private static VisualTexture GetVisualSourceTexture(Sprite source)
    {
        if (!VisualSourceTextures.TryGetValue(source, out var value))
            VisualSourceTextures.Add(source, value = ReadVisualTexture(source));
        return value;
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
        // Full rectangles need no per-pixel triangle/UV reconstruction. Keep the
        // geometry path for tight/rotated/trimmed atlas sprites.
        if (vertices.Length == 4 && triangles.Length == 6 && width == cropWidth && height == cropHeight)
        {
            var corners = 0;
            for (var i = 0; i < 4; i++)
            {
                var point = vertices[i] * sprite.pixelsPerUnit + sprite.pivot;
                var rightSide = Mathf.Abs(point.x - width) < .01f;
                var topSide = Mathf.Abs(point.y - height) < .01f;
                if ((!rightSide && Mathf.Abs(point.x) >= .01f) || (!topSide && Mathf.Abs(point.y) >= .01f) ||
                    Mathf.Abs(uvs[i].x * sprite.texture.width - left - point.x) >= .01f ||
                    Mathf.Abs(uvs[i].y * sprite.texture.height - bottom - point.y) >= .01f) { corners = 0; break; }
                corners |= 1 << ((rightSide ? 1 : 0) + (topSide ? 2 : 0));
            }
            if (corners == 15)
            {
                var first = (1 << triangles[0]) | (1 << triangles[1]) | (1 << triangles[2]);
                var second = (1 << triangles[3]) | (1 << triangles[4]) | (1 << triangles[5]);
                var shared = first & second;
                var sharedPoints = new List<Vector2>(2);
                for (var i = 0; i < 4; i++) if ((shared & (1 << i)) != 0) sharedPoints.Add(vertices[i] * sprite.pixelsPerUnit);
                if ((first | second) == 15 && sharedPoints.Count == 2 &&
                    Mathf.Abs(Mathf.Abs(sharedPoints[0].x - sharedPoints[1].x) - width) < .01f &&
                    Mathf.Abs(Mathf.Abs(sharedPoints[0].y - sharedPoints[1].y) - height) < .01f)
                    return new VisualTexture { Width = width, Height = height, Pixels = crop };
            }
        }
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
        VisualPixelScratch = null;
        ChartVisuals.VisualState.ReleaseIndexes();
        ResetHintTintWarmup();
        foreach (var overlay in VisualOverlays.Values)
        { overlay.Restore(); if (overlay.Render != null) Object.Destroy(overlay.Render.gameObject); }
        VisualOverlays.Clear(); VisualOverlayRenderers.Clear();
        ReleaseNoteTintMaterials();
    }
}
