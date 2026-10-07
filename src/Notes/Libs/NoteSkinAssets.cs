using System;
using System.Collections.Generic;
using System.IO;
using SinmaiAlpha.ChartVisuals;
using MelonLoader;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes.Libs;

// Image semantics match the reference NoteSkinLibrary: centered pivot, same
// world width as the normal model, original aspect ratio, no EX/guide swap.
public sealed class NoteSkinAssets
{
    public string Root;
    private readonly Dictionary<string, Texture2D> textures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Path, int Model), Sprite> sprites = new();
    private readonly Queue<string> preload = new();
    private bool queued;
    public void Prepare(IEnumerable<string> paths)
    {
        if (queued) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths) if (seen.Add(path)) preload.Enqueue(path);
        queued = true;
    }
    public bool PreloadTick()
    {
        // Decode a bounded number on the Unity thread during native readiness.
        for (var i = 0; i < 4 && preload.Count > 0; i++) Load(preload.Dequeue());
        return preload.Count == 0;
    }
    private Texture2D Load(string path)
    {
        if (textures.TryGetValue(path, out var cached)) return cached;
        Texture2D texture = null;
        try
        {
            var full = NoteSkin.ResolvePath(Root, path);
            if (full == null || !File.Exists(full)) throw new IOException("Image not found next to MA2");
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!texture.LoadImage(File.ReadAllBytes(full))) throw new IOException("Unreadable png/jpg");
            texture.wrapMode = TextureWrapMode.Clamp;
        }
        catch (Exception e)
        {
            if (texture != null) Object.Destroy(texture);
            texture = null;
            MelonLogger.Warning("[Note Skin] " + path + ": " + e.Message + "; keeping native picture");
        }
        textures[path] = texture;
        return texture;
    }
    public Sprite Create(string path, Sprite model)
    {
        if (!NoteSkin.Normalize(path, out var safe)) return null;
        var key = (safe, model == null ? 0 : model.GetInstanceID());
        if (sprites.TryGetValue(key, out var cached)) return cached;
        var texture = Load(safe);
        if (texture == null) { sprites[key] = null; return null; }
        var width = model != null && model.rect.width > 0 ? model.rect.width / model.pixelsPerUnit : 0;
        var ppu = width > .0001f ? texture.width / width : 100f;
        var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), ppu);
        sprite.name = safe;
        sprites[key] = sprite;
        return sprite;
    }
    public void Clear()
    {
        foreach (var sprite in sprites.Values) if (sprite != null) Object.Destroy(sprite);
        sprites.Clear();
        foreach (var texture in textures.Values) if (texture != null) Object.Destroy(texture);
        textures.Clear(); preload.Clear(); queued = false;
    }
}
