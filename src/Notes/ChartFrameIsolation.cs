using System;
using System.Collections.Generic;
using SinmaiAlpha.Hosting;
using SinmaiAlpha.Notes.Libs;
using HarmonyLib;
using MelonLoader;
using Monitor;
using Monitor.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

// A shared arcade camera must not let one player's moved frame cover the other.
// Render the authored frame with the native projection and materials, then copy
// only its original viewport. Layers and camera masks are render-time leases;
// native input objects, queues, clocks and judge results are never replaced.
public sealed partial class ChartFrameMotion
{
    private sealed class IsolationSurface
    {
        public GameMonitor Owner;
        public GameCtrl Controller;
        public Canvas Canvas;
        public MonitorBackgroundTownController BackgroundController;
        public GameObject Background;
        public int Layer;
    }
    private sealed class LayerLease
    {
        public GameObject Owner;
        public int Original, Applied;
    }
    private sealed class IsolatedFrame
    {
        public RenderTexture Texture;
        public PresentationRect Bounds;
        public PresentationRect TextureUv;
    }
    private sealed class ScreenClipLease
    {
        public Renderer Owner;
        public float Min, Max, AppliedMin, AppliedMax;
    }
    private static readonly int ClipMinX = Shader.PropertyToID("_ClipMinX");
    private static readonly int ClipMaxX = Shader.PropertyToID("_ClipMaxX");
    private static readonly HashSet<int> ReservedIsolationLayers = new();
    private readonly List<int> isolationLayers = new();
    private readonly List<IsolationSurface> isolationSurfaces = new();
    private readonly Dictionary<GameObject, LayerLease> isolationLeases = new();
    private readonly List<IsolatedFrame> isolatedFrames = new();
    private readonly List<ScreenClipLease> screenClipLeases = new();
    private readonly List<Renderer> isolationRenderers = new();
    private readonly List<Canvas> isolationCanvases = new();
    private readonly Stack<LayerLease> spareLayerLeases = new();
    private readonly MaterialPropertyBlock screenClipProperties = new();
    private readonly Vector3[] isolationCorners = new Vector3[4];
    private Camera isolationCamera, leasedSourceCamera;
    private int originalCameraMask, appliedCameraMask;
    private bool isolationSurfacesDirty = true, renderingIsolation, isolationWarning;

    private bool ReserveLayers(int count)
    {
        if (isolationLayers.Count >= count) return true;
        var used = new HashSet<int>(ReservedIsolationLayers);
        foreach (var obj in Resources.FindObjectsOfTypeAll<GameObject>()) if (obj != null) used.Add(obj.layer);
        var available = new List<int>();
        for (var layer = 31; layer >= 8 && available.Count + isolationLayers.Count < count; layer--)
            if (!used.Contains(layer) && string.IsNullOrEmpty(LayerMask.LayerToName(layer))) available.Add(layer);
        if (available.Count + isolationLayers.Count < count) return false;
        foreach (var layer in available) { isolationLayers.Add(layer); ReservedIsolationLayers.Add(layer); }
        return true;
    }
    private bool HasLayerCollision()
    {
        if (isolationLayers.Count == 0) return false;
        // All our previous leases have already been restored. A new visible
        // renderer/Canvas on a reserved bit therefore belongs to another mod.
        foreach (var renderer in Object.FindObjectsOfType<Renderer>())
            if (renderer != null && isolationLayers.Contains(renderer.gameObject.layer)) return true;
        foreach (var canvas in Object.FindObjectsOfType<Canvas>())
            if (canvas != null && isolationLayers.Contains(canvas.gameObject.layer)) return true;
        return false;
    }
    internal static MonitorBackgroundTownController NativeBackgroundFor(GameMonitor owner)
    {
        if (owner == null) return null;
        foreach (var common in Object.FindObjectsOfType<CommonMonitor>())
            if (common != null && common.MonitorIndex == owner.MonitorIndex)
                return Traverse.Create(common).Field("_backgroundController").GetValue<MonitorBackgroundTownController>();
        return null;
    }
    private void DiscoverSurfaces()
    {
        if (!isolationSurfacesDirty) return;
        isolationSurfaces.Clear();
        foreach (var owner in Object.FindObjectsOfType<GameMonitor>())
        {
            if (owner == null || GuiSizes.SinglePlayer && owner.MonitorIndex != 0) continue;
            var main = Traverse.Create(owner).Field("Main").GetValue<CanvasGroup>();
            var controller = Traverse.Create(owner).Field("GameController").GetValue<GameCtrl>();
            var canvas = main != null ? main.GetComponentInParent<Canvas>() : null;
            if (main == null || controller == null || canvas == null || !main.gameObject.activeInHierarchy) continue;
            // CommonProcess is a separate native monitor hierarchy. Its index
            // owns the background even when two canvases share world position.
            var background = NativeBackgroundFor(owner);
            isolationSurfaces.Add(new IsolationSurface { Owner = owner, Controller = controller, Canvas = canvas, BackgroundController = background });
        }
        isolationSurfacesDirty = false;
    }
    private void LeaseScreenClips(GameObject background, PresentationRect viewport)
    {
        if (background == null) return;
        foreach (var renderer in background.GetComponentsInChildren<Renderer>(true))
        {
            // The bundled cross-version reference assembly predates this 1.70
            // component. Resolve its real native component by name at runtime.
            if (renderer == null || renderer.GetComponent("ScreenClipSetter") == null) continue;
            var alreadyLeased = false;
            foreach (var previous in screenClipLeases) if (previous.Owner == renderer) { alreadyLeased = true; break; }
            if (alreadyLeased) continue;
            renderer.GetPropertyBlock(screenClipProperties);
            var min = screenClipProperties.GetFloat(ClipMinX); var max = screenClipProperties.GetFloat(ClipMaxX);
            // Native SetScreenClipsX writes both floats on initialized sprites.
            // Empty/uninitialized or intentionally hidden ranges stay native.
            if (float.IsNaN(min) || float.IsNaN(max) || float.IsInfinity(min) || float.IsInfinity(max) || max <= min) continue;
            var lease = new ScreenClipLease { Owner = renderer, Min = min, Max = max,
                AppliedMin = (min - viewport.X) / viewport.Width, AppliedMax = (max - viewport.X) / viewport.Width };
            screenClipLeases.Add(lease);
            screenClipProperties.SetFloat(ClipMinX, lease.AppliedMin); screenClipProperties.SetFloat(ClipMaxX, lease.AppliedMax);
            renderer.SetPropertyBlock(screenClipProperties);
        }
    }
    private bool TryIsolationCrop(GameObject background, PresentationRect bounds, int width, int height, out FrameViewportCrop crop)
    {
        if (background != null)
            foreach (var renderer in background.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer.GetComponent("ScreenClipSetter") == null) continue;
                renderer.GetPropertyBlock(screenClipProperties);
                var min = screenClipProperties.GetFloat(ClipMinX); var max = screenClipProperties.GetFloat(ClipMaxX);
                if (float.IsNaN(min) || float.IsNaN(max) || float.IsInfinity(min) || float.IsInfinity(max) || max <= min) continue;
                // Native projected-coordinate clip comparisons can round in
                // opposite directions at a source pixel center after cropping.
                // Keep the original projection for fractional native clip
                // edges (e.g. half of an odd-width target), retaining the exact
                // source shader coordinates and the same final visible bounds.
                if (!ClipPixelAligned(min, width) || !ClipPixelAligned(max, width))
                    return FrameViewportCrop.TryCreateFull(bounds, width, height, out crop);
            }
        return FrameViewportCrop.TryCreate(bounds, width, height, out crop);
    }
    private static bool ClipPixelAligned(float edge, int width)
    {
        var pixel = (double)edge * width;
        return Math.Abs(pixel - Math.Round(pixel)) <= .0001;
    }
    private void RestoreScreenClips()
    {
        foreach (var lease in screenClipLeases)
        {
            if (lease.Owner == null) continue;
            lease.Owner.GetPropertyBlock(screenClipProperties);
            var restoreMin = screenClipProperties.GetFloat(ClipMinX) == lease.AppliedMin;
            var restoreMax = screenClipProperties.GetFloat(ClipMaxX) == lease.AppliedMax;
            if (restoreMin) screenClipProperties.SetFloat(ClipMinX, lease.Min);
            if (restoreMax) screenClipProperties.SetFloat(ClipMaxX, lease.Max);
            if (restoreMin || restoreMax) lease.Owner.SetPropertyBlock(screenClipProperties);
        }
        screenClipLeases.Clear();
    }
    private void LeaseLayer(GameObject owner, int layer, int cameraMask)
    {
        if (owner == null) return;
        if (isolationLeases.TryGetValue(owner, out var existing))
        {
            if (existing.Applied != layer) throw new InvalidOperationException("Monitor render objects share an isolation root");
            return;
        }
        // Do not make an originally camera-hidden object visible.
        if ((cameraMask & (1 << owner.layer)) == 0) return;
        var lease = spareLayerLeases.Count == 0 ? new LayerLease() : spareLayerLeases.Pop();
        lease.Owner = owner; lease.Original = owner.layer; lease.Applied = layer;
        isolationLeases.Add(owner, lease); owner.layer = layer;
    }
    private void LeaseRenderers(GameObject owner, int layer, int mask)
    { CollectVisibleRenderers(owner, isolationRenderers); foreach (var renderer in isolationRenderers) LeaseLayer(renderer.gameObject, layer, mask); }
    private PresentationRect IsolationBounds(RectTransform bounds, Camera camera)
    {
        bounds.GetWorldCorners(isolationCorners);
        var x = float.PositiveInfinity; var y = x; var right = float.NegativeInfinity; var top = right;
        foreach (var corner in isolationCorners)
        {
            var point = camera.WorldToViewportPoint(corner);
            x = Mathf.Min(x, point.x); y = Mathf.Min(y, point.y); right = Mathf.Max(right, point.x); top = Mathf.Max(top, point.y);
        }
        return new PresentationRect(x, y, right - x, top - y).Intersect(new PresentationRect(0, 0, 1, 1));
    }
    private bool PrepareIsolation()
    {
        var active = false;
        foreach (var frame in frames.Values) if (frame.Active) { active = true; break; }
        if (!active) return true;
        var source = GetComponent<Camera>();
        if (source == null || renderingIsolation) return FailIsolation("Native render camera unavailable");
        DiscoverSurfaces();
        if (HasLayerCollision())
        {
            foreach (var layer in isolationLayers) ReservedIsolationLayers.Remove(layer);
            isolationLayers.Clear();
        }
        if (isolationSurfaces.Count == 0 || !ReserveLayers(isolationSurfaces.Count)) return FailIsolation("No unused render layers available");
        var all = 0; var passive = 0;
        originalCameraMask = source.cullingMask;
        for (var i = 0; i < isolationSurfaces.Count; i++)
        {
            var surface = isolationSurfaces[i];
            if (surface.Canvas == null || surface.Canvas.renderMode == RenderMode.ScreenSpaceOverlay)
                return FailIsolation("Monitor canvas cannot be isolated by a world camera");
            surface.Layer = isolationLayers[i]; var bit = 1 << surface.Layer; all |= bit;
            // SetFestaBGActive destroys/replaces _bgObject during native setup.
            // Read the live root instead of holding the first prefab clone.
            surface.Background = surface.BackgroundController != null ? Traverse.Create(surface.BackgroundController).Field("_bgObject").GetValue<GameObject>() : null;
            if (!frames.TryGetValue(surface.Owner, out var frame) || !frame.Active) passive |= bit;
            // Unity culls a world Canvas by its root layer, rather than by an
            // individual label. Nested canvases keep their native sorting.
            isolationCanvases.Clear(); surface.Canvas.GetComponentsInChildren(false, isolationCanvases);
            foreach (var canvas in isolationCanvases) if (canvas.enabled) LeaseLayer(canvas.gameObject, surface.Layer, originalCameraMask);
            LeaseRenderers(surface.Controller.gameObject, surface.Layer, originalCameraMask);
            LeaseRenderers(surface.Background, surface.Layer, originalCameraMask);
            if (frame != null) foreach (var cover in frame.Covers) if (cover != null) LeaseRenderers(cover.gameObject, surface.Layer, originalCameraMask);
        }
        // Each active owner must have its own discovered native canvas.
        foreach (var pair in frames) if (pair.Value.Active)
        {
            var found = false; foreach (var surface in isolationSurfaces) if (surface.Owner == pair.Key) { found = true; break; }
            if (!found) return FailIsolation("Native monitor ownership changed");
        }
        if (isolationCamera == null)
        {
            var obj = new GameObject("ChartFrameIsolationCamera") { hideFlags = HideFlags.HideAndDontSave };
            isolationCamera = obj.AddComponent<Camera>(); isolationCamera.enabled = false;
        }
        var previous = RenderTexture.active;
        renderingIsolation = true;
        try
        {
            foreach (var surface in isolationSurfaces)
            {
                if (!frames.TryGetValue(surface.Owner, out var frame) || !frame.Active) continue;
                var bounds = IsolationBounds(frame.Bounds, source); if (!bounds.HasArea) continue;
                var width = source.targetTexture != null ? source.targetTexture.width : source.pixelWidth;
                var height = source.targetTexture != null ? source.targetTexture.height : source.pixelHeight;
                if (width <= 0 || height <= 0) return FailIsolation("Native camera has no viewport");
                if (!TryIsolationCrop(surface.Background, bounds, width, height, out var crop)) continue;
                // The installed screen-position adapter uses this RGB format.
                // No alpha channel: translucent UI must not cause the completed
                // frame to be alpha-blended a second time during the copy.
                var samples = source.targetTexture != null ? source.targetTexture.antiAliasing : source.allowMSAA ? Math.Max(1, QualitySettings.antiAliasing) : 1;
                var texture = RenderTexture.GetTemporary(crop.Width, crop.Height, 24, RenderTextureFormat.RGB111110Float, RenderTextureReadWrite.Default, samples);
                isolatedFrames.Add(new IsolatedFrame { Bounds = bounds, Texture = texture, TextureUv = crop.TextureUv });
                isolationCamera.CopyFrom(source); isolationCamera.enabled = false;
                isolationCamera.transform.position = source.transform.position; isolationCamera.transform.rotation = source.transform.rotation;
                isolationCamera.targetTexture = texture;
                isolationCamera.rect = new Rect(0, 0, 1, 1);
                isolationCamera.projectionMatrix = crop.Projection(source.projectionMatrix);
                isolationCamera.ResetCullingMatrix();
                isolationCamera.cullingMask = (originalCameraMask & ~all) | (1 << surface.Layer);
                // An opaque frame avoids alpha being multiplied a second time
                // when the native GUI texture copy composites it. Common native
                // backgrounds still render; other monitor roots are excluded.
                isolationCamera.clearFlags = CameraClearFlags.SolidColor; isolationCamera.backgroundColor = Color.black;
                try { LeaseScreenClips(surface.Background, crop.ProjectionViewport); isolationCamera.Render(); }
                finally { RestoreScreenClips(); }
            }
            leasedSourceCamera = source;
            appliedCameraMask = (originalCameraMask & ~all) | passive;
            source.cullingMask = appliedCameraMask;
            return true;
        }
        finally { isolationCamera.targetTexture = null; renderingIsolation = false; RenderTexture.active = previous; }
    }
    private bool FailIsolation(string reason)
    {
        RestoreIsolation(true);
        if (!isolationWarning) { isolationWarning = true; MelonLogger.Warning("[Chart Frame] " + reason + "; native frame retained"); }
        return false;
    }
    private void RestoreIsolation(bool releaseTextures)
    {
        RestoreScreenClips();
        if (leasedSourceCamera != null && leasedSourceCamera.cullingMask == appliedCameraMask) leasedSourceCamera.cullingMask = originalCameraMask;
        leasedSourceCamera = null;
        foreach (var lease in isolationLeases.Values)
        {
            if (lease.Owner != null && lease.Owner.layer == lease.Applied) lease.Owner.layer = lease.Original;
            lease.Owner = null; spareLayerLeases.Push(lease);
        }
        isolationLeases.Clear();
        if (!releaseTextures) return;
        foreach (var frame in isolatedFrames) if (frame.Texture != null) RenderTexture.ReleaseTemporary(frame.Texture);
        isolatedFrames.Clear();
    }
    private void ReleaseIsolation()
    {
        foreach (var layer in isolationLayers) ReservedIsolationLayers.Remove(layer);
        isolationLayers.Clear(); isolationSurfaces.Clear(); isolationSurfacesDirty = true;
        if (isolationCamera != null) Object.Destroy(isolationCamera.gameObject);
        isolationCamera = null; isolationWarning = false;
    }
    internal bool HasIsolatedFrames => isolatedFrames.Count != 0;
    internal void CompositeIsolation(RenderTexture source, RenderTexture destination)
    {
        var previous = RenderTexture.active; var pushed = false;
        try
        {
            Graphics.Blit(source, destination);
            Graphics.SetRenderTarget(destination); GL.PushMatrix(); pushed = true;
            GL.LoadPixelMatrix(0, source.width, source.height, 0);
            foreach (var frame in isolatedFrames)
            {
                var rect = frame.Bounds;
                Graphics.DrawTexture(new Rect(rect.X * source.width, (1 - rect.Top) * source.height, rect.Width * source.width, rect.Height * source.height),
                    frame.Texture, new Rect(frame.TextureUv.X, frame.TextureUv.Y, frame.TextureUv.Width, frame.TextureUv.Height), 0, 0, 0, 0);
            }
        }
        finally { if (pushed) GL.PopMatrix(); RenderTexture.active = previous; }
    }
}
