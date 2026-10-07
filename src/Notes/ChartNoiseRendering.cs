using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Hosting;
using SinmaiAlpha.Notes.Libs;
using HarmonyLib;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private static readonly Dictionary<GameMonitor, NoiseRenderState> NoisePresentationOwners = new();
    private static readonly PresentationTimeline EmptyNoisePresentation = new();

    private static NoiseRenderState ApplyNoisePresentation(GameMonitor owner, NotesReader reader, GameCtrl controller)
    {
        if (owner == null || controller == null || reader == null ||
            (NoisePlaybackStopped && !NotesManager.Instance(owner.MonitorIndex).IsPlaying()) ||
            !NoiseCharts.TryGetValue(reader, out var chart) || chart.Events.Count == 0)
        { ReleaseNoisePresentation(owner); return null; }
        var pool = Traverse.Create(controller).Field("_touchCLauncherLayer").GetValue<GameObject>();
        if (pool == null) { ReleaseNoisePresentation(owner); return null; }
        if (NoisePresentationOwners.TryGetValue(owner, out var state) && state.Reader == reader && state.Parent == pool.transform)
        { state.Update(); return state.Ready ? state : null; }
        ReleaseNoisePresentation(owner);
        try
        {
            state = new NoiseRenderState(owner, reader, pool.transform, chart.Events);
            NoisePresentationOwners.Add(owner, state);
            return state;
        }
        catch (Exception error)
        { MelonLogger.Warning("[Noise Zone] Display unavailable: " + error.Message); return null; }
    }
    private static void ReleaseNoisePresentation(GameMonitor owner)
    {
        if (ReferenceEquals(owner, null) || !NoisePresentationOwners.TryGetValue(owner, out var state)) return;
        state.Dispose(); NoisePresentationOwners.Remove(owner);
    }
    private static void ResetNoisePresentation(NotesReader reader = null)
    {
        foreach (var pair in NoisePresentationOwners.Where(p => reader == null || p.Value.Reader == reader).ToArray())
        { pair.Value.Dispose(); NoisePresentationOwners.Remove(pair.Key); }
    }
    private static bool NoiseInputDisplayReady(int monitor, NotesReader reader)
        => NoisePresentationOwners.Any(p => p.Key != null && p.Key.MonitorIndex == monitor &&
            p.Value.Reader == reader && p.Value.InputReady && p.Value.Parent != null && p.Value.Parent.gameObject.activeInHierarchy);

    internal sealed class NoiseRenderState : IDisposable
    {
        internal readonly GameMonitor Owner;
        internal readonly NotesReader Reader;
        internal readonly Transform Parent;
        internal NoiseZoneDisplay Display;
        private GameObject root;
        private NoiseZoneCompositor compositor;
        private Material copy;
        private RenderTexture input, output;
        private bool acquired, disposed, failed, captured;
        private int capturedFrame = -1;
        private Camera capturedCamera;
        private Matrix4x4 fieldAtRender;
        internal bool Ready => !disposed && !failed && Display != null && root != null;
        // Input runs before this frame's camera. Accept the previous rendered
        // frame, but release the mask if rendering stops instead of retaining
        // an invisible field indefinitely.
        internal bool InputReady => Ready && captured && capturedCamera != null &&
            capturedCamera.gameObject.activeInHierarchy && Time.frameCount - capturedFrame >= 0 &&
            Time.frameCount - capturedFrame <= 1 && root.activeInHierarchy && Display.isActiveAndEnabled;

        internal NoiseRenderState(GameMonitor owner, NotesReader reader, Transform parent, IEnumerable<NoiseZoneEvent> events)
        {
            Owner = owner; Reader = reader; Parent = parent;
            try
            {
                NoiseGraphics.Acquire(); acquired = true;
                root = new GameObject("AquaMai native noise fields") { hideFlags = HideFlags.HideAndDontSave, layer = parent.gameObject.layer };
                root.transform.SetParent(parent, false);
                // Native note/TouchSlide coordinates use 100 pixels per source unit.
                root.transform.localScale = Vector3.one * 100;
                Display = root.AddComponent<NoiseZoneDisplay>();
                var nativeRenderer = parent.GetComponentInChildren<Renderer>(true);
                Display.Configure(events, () => NotesManager.GetCurrentMsec() / 1000d, NoiseGraphics.Centers,
                    NoiseGraphics.Warning, parent.gameObject.layer, nativeRenderer != null ? nativeRenderer.sortingLayerID : 0);
                compositor = new NoiseZoneCompositor(NoiseGraphics.Composite, NoiseGraphics.Displacement, NoiseGraphics.Spark);
                copy = new Material(NoiseGraphics.Composite) { hideFlags = HideFlags.HideAndDontSave };
                copy.SetTexture("_Masks", NoiseGraphics.EmptyMask);
                copy.SetFloat("_AlphaClipSpace", 0);
            }
            catch { Dispose(); throw; }
        }
        internal void Update()
        { if (Ready) Display.RenderAt(NotesManager.GetCurrentMsec() / 1000d); }
        internal void CaptureField(Camera camera)
        {
            captured = Ready && camera != null;
            if (!captured) { capturedCamera = null; capturedFrame = -1; return; }
            Update(); fieldAtRender = Display.FieldMatrix; capturedCamera = camera; capturedFrame = Time.frameCount;
        }
        internal bool TryDraw(RenderTexture source, RenderTexture destination, Camera camera,
            PresentationRect bounds, PresentationRect visible, PresentationRect playfield)
        {
            if (!Ready || !captured || capturedCamera != camera || !Display.HasActive || !bounds.HasArea || !visible.HasArea) return false;
            var previous = RenderTexture.active;
            try
            {
                var width = Math.Max(1, Mathf.RoundToInt(source.width * bounds.Width));
                var height = Math.Max(1, Mathf.RoundToInt(source.height * bounds.Height));
                EnsureTargets(width, height, source.format);
                Graphics.SetRenderTarget(input); GL.Clear(false, true, Color.black);
                Draw(input, PixelRect(Relative(visible, bounds), width, height), source, visible);
                var material = compositor.Prepare(Display, camera, width, height, fieldAtRender, bounds);
                if (material == null) return false;
                var rect = Relative(playfield, bounds);
                material.SetFloat("_AlphaClipSpace", 1);
                material.SetVector("_BlitScaleBias", new Vector4(1, 1, 0, 0));
                material.SetVector("_PlayfieldRect", new Vector4(rect.X, rect.Y, rect.Right, rect.Top));
                Graphics.Blit(input, output, material, 3);
                Draw(destination, PixelRect(visible, source.width, source.height), output, Relative(visible, bounds));
                return true;
            }
            catch (Exception error)
            {
                failed = true; MelonLogger.Warning("[Noise Zone] Native player composite unavailable: " + error.Message);
                Dispose(); return false;
            }
            finally { RenderTexture.active = previous; }
        }
        private void EnsureTargets(int width, int height, RenderTextureFormat format)
        {
            if (input != null && input.width == width && input.height == height && input.format == format) return;
            ReleaseTarget(ref input); ReleaseTarget(ref output);
            input = Target(width, height, format); output = Target(width, height, format);
        }
        private static RenderTexture Target(int width, int height, RenderTextureFormat format)
        {
            var texture = new RenderTexture(width, height, 0, format, RenderTextureReadWrite.Linear)
            { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            if (texture.Create()) return texture;
            Object.Destroy(texture); throw new InvalidOperationException("Native player noise frame allocation failed");
        }
        private static void ReleaseTarget(ref RenderTexture texture)
        { if (texture != null) { texture.Release(); Object.Destroy(texture); } texture = null; }
        private static PresentationRect Relative(PresentationRect value, PresentationRect basis)
            => new((value.X - basis.X) / basis.Width, (value.Y - basis.Y) / basis.Height, value.Width / basis.Width, value.Height / basis.Height);
        private static Rect PixelRect(PresentationRect value, int width, int height)
            => new(value.X * width, (1 - value.Top) * height, value.Width * width, value.Height * height);
        private void Draw(RenderTexture target, Rect destination, Texture source, PresentationRect uv)
        {
            var previous = RenderTexture.active; var pushed = false;
            try
            {
                Graphics.SetRenderTarget(target); GL.PushMatrix(); pushed = true;
                GL.LoadPixelMatrix(0, target.width, target.height, 0);
                Graphics.DrawTexture(destination, source, new Rect(uv.X, uv.Y, uv.Width, uv.Height), 0, 0, 0, 0, Color.white, copy, 3);
            }
            finally { if (pushed) GL.PopMatrix(); RenderTexture.active = previous; }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true; captured = false; capturedCamera = null;
            if (root != null) root.SetActive(false);
            if (Display != null) Display.Clear(); Display = null;
            compositor?.Dispose(); compositor = null;
            ReleaseTarget(ref input); ReleaseTarget(ref output);
            if (copy != null) Object.Destroy(copy); copy = null;
            if (root != null) Object.Destroy(root); root = null;
            if (acquired) { acquired = false; NoiseGraphics.Release(); }
        }
    }
    private static class NoiseGraphics
    {
        private static AssetBundle bundle;
        private static int owners;
        internal static Shader Composite, Warning;
        internal static Texture2D Displacement, Spark, EmptyMask;
        internal static readonly System.Numerics.Vector2[] Centers = MakeCenters();
        private static System.Numerics.Vector2[] MakeCenters()
        {
            var values = new System.Numerics.Vector2[33];
            for (var sensor = 0; sensor < values.Length; sensor++)
            {
                if (sensor == 16) continue;
                var outer = sensor >= 17; var key = outer ? (sensor - 17) % 8 + 1 : sensor % 8 + 1;
                var radius = sensor < 8 ? 4 : sensor < 16 ? 2.2f : sensor < 25 ? 4.1f : 3.1f;
                var angle = -key * (float)Math.PI / 4 + (float)Math.PI * (outer ? 3f / 4 : 5f / 8);
                values[sensor] = new System.Numerics.Vector2(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);
            }
            return values;
        }
        internal static void Acquire()
        {
            if (bundle == null)
            {
                try
                {
                    using var source = SinmaiAlpha.Hosting.AssetFiles.Open("Alpha/AlphaNoise.ab");
                    if (source == null) throw new InvalidOperationException("Compiled noise resource missing");
                    using var memory = new MemoryStream(); source.CopyTo(memory);
                    bundle = AssetBundle.LoadFromMemory(memory.ToArray());
                    Composite = bundle?.LoadAsset<Shader>("assets/aquamai/alphanoisecomposite.shader");
                    Warning = bundle?.LoadAsset<Shader>("assets/aquamai/alphanoisezone.shader");
                    Displacement = bundle?.LoadAsset<Texture2D>("assets/aquamai/blocknoise1.texture");
                    Spark = bundle?.LoadAsset<Texture2D>("assets/aquamai/pointnoise.texture");
                    if (Composite == null || !Composite.isSupported || Warning == null || !Warning.isSupported || Displacement == null || Spark == null)
                        throw new InvalidOperationException("Compiled noise resources unsupported");
                    EmptyMask = new Texture2D(1, 1, TextureFormat.RGBA32, false, true) { hideFlags = HideFlags.HideAndDontSave, filterMode = FilterMode.Point };
                    EmptyMask.SetPixel(0, 0, Color.clear); EmptyMask.Apply(false, true);
                }
                catch { Release(); throw; }
            }
            owners++;
        }
        internal static void Release()
        {
            if (owners > 0 && --owners > 0) return;
            if (EmptyMask != null) Object.Destroy(EmptyMask);
            if (bundle != null) bundle.Unload(true);
            bundle = null; Composite = Warning = null; Displacement = Spark = EmptyMask = null; owners = 0;
        }
    }
    private static bool NoisePlaybackStopped;
    // The native process stops the chart before its fade and OnRelease. Drop
    // render resources at that boundary as well as on reader/process release.
    [HarmonyPatch(typeof(NotesManager), nameof(NotesManager.StopPlay))]
    public static class NoisePlaybackStopPatch
    {
        [HarmonyPostfix] public static void Postfix()
        {
        if (!(CustomNoteTypes.FeaturesEnabled())) {  return; }
 NoisePlaybackStopped = true; ResetNoisePresentation(); }
    }
}
