using System;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;
using SinmaiAlpha.Compatibility;
using Manager;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes.Libs;

public sealed class NativeMediaClock
{
    public int Monitor;
    public float AudioTime => NotesManager.GetCurrentMsec() / 1000f;
    public float CurrentSpeed => PracticeMode.speed;
    public bool PlaybackStarted => NotesManager.Instance(Monitor).IsPlaying();
    public bool IsPreview => false;
}

// The overlays are siblings of the native movie sprite, behind its brightness
// mask. This lease never changes the mask or the native movie player's state.
public sealed class NativeMediaBackground
{
    private readonly SpriteRenderer movie;
    private Color nativeColor;
    private bool nativeEnabled;
    private bool leased;
    private int fitMode;
    private Material nativeMaterial, mediaMaterial;
    private SortingGroup frameGroup;
    private Transform nativeParent;
    private int nativeSibling, nativeSortingOrder, nativeSortingLayer;
    public NativeMediaBackground(SpriteRenderer renderer) { movie = renderer; }
    public void ConfigureRenderer(SpriteRenderer renderer, int order)
    {
        if (renderer == null || movie == null) return;
        EnsureFrameGroup();
        renderer.transform.SetParent(frameGroup.transform, false);
        renderer.transform.localPosition = movie.transform.localPosition;
        renderer.transform.localRotation = movie.transform.localRotation;
        renderer.gameObject.layer = movie.gameObject.layer;
        renderer.sortingLayerID = movie.sortingLayerID;
        renderer.sortingOrder = movie.sortingOrder + order;
        renderer.maskInteraction = movie.maskInteraction;
    }
    private void EnsureFrameGroup()
    {
        if (frameGroup != null || movie == null) return;
        nativeParent = movie.transform.parent; nativeSibling = movie.transform.GetSiblingIndex();
        nativeSortingOrder = movie.sortingOrder; nativeSortingLayer = movie.sortingLayerID;
        var frame = new GameObject("ChartMediaFrame", typeof(SortingGroup));
        frame.layer = movie.gameObject.layer; frame.transform.SetParent(nativeParent, false);
        frameGroup = frame.GetComponent<SortingGroup>();
        frameGroup.sortingLayerID = nativeSortingLayer; frameGroup.sortingOrder = nativeSortingOrder;
        // Actual 1.70 prefab: movie=-32767, brightness mask=-32766.
        // Group internal ordering gives room for outgoing/incoming overlays
        // without moving any of them above that native brightness mask.
        movie.transform.SetParent(frame.transform, false); movie.sortingOrder = 0;
    }
    public void SetBackgroundFitMode(int mode) => fitMode = Mathf.Clamp(mode, 0, 1);
    public void ApplyMediaScale(SpriteRenderer renderer, float width, float height, bool square)
    {
        if (movie == null || renderer == null || renderer.sprite == null || width <= 0 || height <= 0) return;
        var bounds = renderer.sprite.bounds.size;
        if (bounds.x <= 0 || bounds.y <= 0) return;
        // Sinmai's circular playfield is 1080 native local units. The reference
        // is 10.8 world units; both place the judgment radius at 4.8/480.
        const float frame = 1080;
        var aspect = width / height;
        var scale = fitMode == 1 ? frame / bounds.x : frame / bounds.y;
        var local = square ? (fitMode == 1 ? new Vector3(scale, scale / aspect, 1) : new Vector3(scale * aspect, scale, 1)) : new Vector3(scale, scale, 1);
        renderer.transform.localScale = Vector3.Scale(local, movie.transform.localScale);
    }
    public void SetMediaOverlayBlend(float blend)
    {
        if (movie == null) return;
        if (!leased) { nativeColor = movie.color; nativeEnabled = movie.enabled; leased = true; }
        var color = nativeColor; color.a *= 1f - Mathf.Clamp01(blend); movie.color = color;
        ApplyNativeMaterial(blend);
        movie.enabled = nativeEnabled && blend < 1;
    }
    private void ApplyNativeMaterial(float blend)
    {
        // Installed CriMana's opaque YUV/RGB passes need the same keyword and
        // blend factors as RendererResource.SetApplyTargetAlpha(true). Keep a
        // per-renderer copy so the other player's movie and CRI decoder retain
        // their original material. Copy its current YUV textures each frame.
        if (movie.sharedMaterial != mediaMaterial)
        {
            if (mediaMaterial != null) Object.Destroy(mediaMaterial);
            mediaMaterial = null;
            nativeMaterial = movie.sharedMaterial;
        }
        if (nativeMaterial == null || nativeMaterial.shader == null ||
            !nativeMaterial.shader.name.StartsWith("CriMana/", StringComparison.Ordinal)) return;
        if (mediaMaterial == null && blend <= 0) return;
        if (mediaMaterial == null) mediaMaterial = new Material(nativeMaterial);
        mediaMaterial.CopyPropertiesFromMaterial(nativeMaterial);
        mediaMaterial.EnableKeyword("CRI_APPLY_TARGET_ALPHA");
        mediaMaterial.SetInt("_SrcBlendMode", 5);
        mediaMaterial.SetInt("_DstBlendMode", 10);
        movie.sharedMaterial = mediaMaterial;
    }
    public void Restore()
    {
        if (movie != null && leased) { movie.color = nativeColor; movie.enabled = nativeEnabled; }
        if (movie != null && mediaMaterial != null && movie.sharedMaterial == mediaMaterial) movie.sharedMaterial = nativeMaterial;
        if (mediaMaterial != null) Object.Destroy(mediaMaterial);
        mediaMaterial = nativeMaterial = null;
        if (frameGroup != null)
        {
            if (movie != null)
            {
                movie.transform.SetParent(nativeParent, false); movie.transform.SetSiblingIndex(nativeSibling);
                movie.sortingLayerID = nativeSortingLayer; movie.sortingOrder = nativeSortingOrder;
            }
            Object.Destroy(frameGroup.gameObject); frameGroup = null;
        }
        leased = false;
    }
    public static Sprite LoadSprite(string path)
    {
        Texture2D texture = null;
        try
        {
            if (!File.Exists(path)) return null;
            texture = new Texture2D(2, 2);
            if (!texture.LoadImage(File.ReadAllBytes(path)) || texture.width <= 0 || texture.height <= 0)
            { Object.Destroy(texture); return null; }
            return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f));
        }
        catch { if (texture != null) Object.Destroy(texture); return null; }
    }
}
