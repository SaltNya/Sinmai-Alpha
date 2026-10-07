using System;
using System.Collections.Generic;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Hosting;
using HarmonyLib;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;
using UnityEngine.UI;

namespace SinmaiAlpha.Notes;

// Canvas meshes must be moved before Unity batches them. Sprite/mesh poses
// exist only during camera rendering, so native gameplay sees its own poses.
[DefaultExecutionOrder(10000)]
public sealed partial class ChartFrameMotion : MonoBehaviour
{
    private sealed class Pose
    {
        public Transform Target;
        public Vector3 Position, Scale, WorldPosition;
        public Quaternion Rotation, WorldRotation;
        public Vector3 AppliedPosition, AppliedScale;
        public Quaternion AppliedRotation;
        public bool SkipRotation;
        public bool PositionApplied, ScaleApplied, RotationApplied;

        public Pose() { }
        public void Capture(Transform target, bool skipRotation)
        {
            PositionApplied = ScaleApplied = RotationApplied = false;
            Target = target; SkipRotation = skipRotation;
            Position = target.localPosition; Scale = target.localScale; Rotation = target.localRotation;
            WorldPosition = target.position; WorldRotation = target.rotation;
        }
        public void Restore(bool leased)
        {
            if (Target == null) return;
            // UI animators may have written a newer pose since LateUpdate.
            // Restore only components still owned by the presentation lease.
            if (!leased || PositionApplied && Target.localPosition.Equals(AppliedPosition)) Target.localPosition = Position;
            if (!leased || ScaleApplied && Target.localScale.Equals(AppliedScale)) Target.localScale = Scale;
            if (!leased || RotationApplied && Target.localRotation.Equals(AppliedRotation)) Target.localRotation = Rotation;
        }
        public void Apply(Frame frame, bool transformedAncestor)
        {
            var rotation = SkipRotation ? Quaternion.identity : Quaternion.Euler(0, 0, frame.Degrees);
            var relative = WorldPosition - frame.Center;
            var planar = rotation * new Vector3(relative.x * frame.Scale, relative.y * frame.Scale, 0);
            Target.position = new Vector3(frame.Center.x + planar.x + frame.Offset.x,
                frame.Center.y + planar.y + frame.Offset.y, WorldPosition.z);
            AppliedPosition = Target.localPosition; PositionApplied = true;
            Target.rotation = rotation * WorldRotation;
            AppliedRotation = Target.localRotation; RotationApplied = true;
            // Different rotation policies can share an ancestor (the native
            // movie is a child of its black cover). Scale such a child once.
            Target.localScale = transformedAncestor ? Scale : Scale * frame.Scale;
            AppliedScale = Target.localScale; ScaleApplied = true;
        }
    }
    private sealed class Target
    {
        public Transform Transform;
        public bool SkipRotation;
        public int Depth;
    }
    private sealed class Frame
    {
        public GameMonitor Owner;
        public NotesReader Reader;
        public RectTransform Main, Bounds;
        public GameCtrl Controller;
        public PresentationCommands.Track Move, Rotate, Zoom;
        public CustomNoteTypes.NoiseRenderState Noise;
        public readonly List<Transform> Covers = new();
        public readonly List<Pose> CanvasPoses = new(), WorldPoses = new();
        public readonly List<Target> Targets = new();
        public readonly Vector3[] MainCorners = new Vector3[4], FrameCorners = new Vector3[4];
        public Vector3 Center, Offset;
        public float Degrees, Scale = 1;
        public bool Active;
        private readonly Dictionary<Transform, Target> targetIndex = new();
        private readonly Dictionary<Transform, int> descendantCounts = new();
        private readonly HashSet<Transform> appliedAncestors = new();
        private readonly Stack<Target> spareTargets = new();
        private readonly Stack<Pose> sparePoses = new();

        private void RestorePoses(List<Pose> poses, bool leased)
        {
            for (var i = poses.Count - 1; i >= 0; i--)
            { poses[i].Restore(leased); poses[i].Target = null; sparePoses.Push(poses[i]); }
            poses.Clear();
        }
        private void CountAncestors(Transform target, int delta)
        {
            for (var parent = target.parent; parent != null; parent = parent.parent)
            {
                descendantCounts.TryGetValue(parent, out var count);
                if (count + delta == 0) descendantCounts.Remove(parent);
                else descendantCounts[parent] = count + delta;
            }
        }
        public void ClearTargets()
        {
            foreach (var target in Targets) { target.Transform = null; spareTargets.Push(target); }
            Targets.Clear(); targetIndex.Clear(); descendantCounts.Clear(); appliedAncestors.Clear();
        }

        public void RestoreCanvas()
        { RestorePoses(CanvasPoses, true); }
        public void RestoreWorld()
        { RestorePoses(WorldPoses, false); }
        public bool Evaluate()
        {
            if (Owner == null || Main == null || Bounds == null || Controller == null ||
                !Main.gameObject.activeInHierarchy || GuiSizes.SinglePlayer && Owner.MonitorIndex != 0) return Active = false;
            var now = NotesManager.GetCurrentMsec();
            Degrees = Rotate?.Evaluate(now) ?? 0;
            Scale = Mathf.Clamp(1 + (Zoom?.Evaluate(now) ?? 0), .1f, 8);
            var x = 0f; var y = 0f; Move?.EvaluateVector(now, out x, out y);
            Main.GetWorldCorners(MainCorners); Bounds.GetWorldCorners(FrameCorners);
            Center = (MainCorners[0] + MainCorners[2]) * .5f;
            var offset = (FrameCorners[3] - FrameCorners[0]) * x + (FrameCorners[1] - FrameCorners[0]) * y;
            Offset = new Vector3(offset.x, offset.y, 0);
            return Active = Math.Abs(Degrees) > .0001f || Math.Abs(Scale - 1) > .0001f ||
                Math.Abs(Offset.x) > .0001f || Math.Abs(Offset.y) > .0001f;
        }
        public void Add(Transform transform, bool skipRotation)
        {
            if (transform == null) return;
            if (targetIndex.ContainsKey(transform)) return;
            var depth = 0;
            for (var parent = transform.parent; parent != null; parent = parent.parent)
            {
                depth++;
                if (targetIndex.TryGetValue(parent, out var ancestor) && ancestor.SkipRotation == skipRotation) return;
            }
            // Only an ancestor of an accepted target can replace descendants.
            // Sibling renderers in the native pools take the short path above.
            if (descendantCounts.ContainsKey(transform))
                for (var i = Targets.Count - 1; i >= 0; i--)
                    if (Targets[i].SkipRotation == skipRotation && Targets[i].Transform.IsChildOf(transform))
                    {
                        var removed = Targets[i]; CountAncestors(removed.Transform, -1);
                        targetIndex.Remove(removed.Transform); Targets.RemoveAt(i);
                        removed.Transform = null; spareTargets.Push(removed);
                    }
            var target = spareTargets.Count == 0 ? new Target() : spareTargets.Pop();
            target.Transform = transform; target.SkipRotation = skipRotation; target.Depth = depth;
            Targets.Add(target); targetIndex.Add(transform, target); CountAncestors(transform, 1);
        }
        public void Apply(List<Pose> poses)
        {
            Targets.Sort((a, b) => a.Depth.CompareTo(b.Depth));
            // Capture all native poses before moving any parent. Reuse records
            // so a long zoom/rotation does not allocate per pooled renderer.
            foreach (var target in Targets)
            {
                var pose = sparePoses.Count == 0 ? new Pose() : sparePoses.Pop();
                pose.Capture(target.Transform, target.SkipRotation); poses.Add(pose);
            }
            appliedAncestors.Clear();
            foreach (var pose in poses)
            {
                var inherited = false;
                for (var parent = pose.Target.parent; parent != null; parent = parent.parent)
                    if (appliedAncestors.Contains(parent)) { inherited = true; break; }
                pose.Apply(this, inherited); appliedAncestors.Add(pose.Target);
            }
            ClearTargets();
        }
    }

    private readonly Dictionary<GameMonitor, Frame> frames = new();
    private readonly List<GameMonitor> stale = new();
    private readonly List<Renderer> visibleRenderers = new();
    private readonly List<Graphic> visibleGraphics = new();
    // Pool objects which are inactive or renderer-disabled cannot contribute
    // pixels. They must retain their native poses, not be transformed/restored.
    internal static void CollectVisibleRenderers(GameObject root, List<Renderer> result)
    {
        result.Clear();
        if (root == null || !root.activeInHierarchy) return;
        root.GetComponentsInChildren(false, result);
        for (var i = result.Count - 1; i >= 0; i--)
            if (result[i] == null || !result[i].enabled) result.RemoveAt(i);
    }
    private void CollectVisibleGraphics(Canvas canvas)
    {
        visibleGraphics.Clear();
        if (canvas != null) canvas.GetComponentsInChildren(false, visibleGraphics);
    }
    internal void Bind(GameMonitor owner, NotesReader reader, GameCtrl controller, RectTransform main,
        RectTransform bounds, PresentationCommands.Track move, PresentationCommands.Track rotate,
        PresentationCommands.Track zoom, List<Transform> covers)
    {
        if (owner == null) return;
        if (!frames.TryGetValue(owner, out var frame)) frames[owner] = frame = new Frame { Owner = owner };
        if (frame.Reader != reader || frame.Controller != controller || frame.Main != main || frame.Bounds != bounds)
        { RestoreIsolation(true); frame.RestoreCanvas(); frame.RestoreWorld(); isolationSurfacesDirty = true; }
        frame.Reader = reader; frame.Controller = controller; frame.Main = main; frame.Bounds = bounds;
        frame.Move = move; frame.Rotate = rotate; frame.Zoom = zoom;
        frame.Covers.Clear(); if (covers != null) frame.Covers.AddRange(covers);
        // View binding runs before the first motion event. Reserve isolation
        // metadata now instead of scanning the entire scene at that event.
        if (isolationSurfacesDirty || isolationLayers.Count == 0)
        {
            DiscoverSurfaces();
            if (isolationSurfaces.Count != 0) ReserveLayers(isolationSurfaces.Count);
        }
    }
    internal void BeforeNativeUpdate(GameMonitor owner)
    {
        // Also catches interrupted rendering (disabled/destroyed cameras).
        RestoreIsolation(true);
        foreach (var frame in frames.Values) frame.RestoreWorld();
        if (frames.TryGetValue(owner, out var own)) own.RestoreCanvas();
    }
    internal void BindNoise(GameMonitor owner, CustomNoteTypes.NoiseRenderState noise)
    { if (frames.TryGetValue(owner, out var frame)) frame.Noise = noise; }
    internal void Unbind(GameMonitor owner)
    { if (!frames.TryGetValue(owner, out var frame)) return; frame.Noise?.CaptureField(null); RestoreIsolation(true); frame.RestoreWorld(); frame.RestoreCanvas(); frames.Remove(owner); isolationSurfacesDirty = true; if (frames.Count == 0) ReleaseIsolation(); }
    internal void ResetReader(NotesReader reader)
    {
        stale.Clear(); foreach (var pair in frames) if (pair.Value.Reader == reader) stale.Add(pair.Key);
        foreach (var owner in stale) Unbind(owner); stale.Clear();
    }
    internal void Clear()
    { RestoreIsolation(true); ReleaseIsolation(); foreach (var frame in frames.Values) { frame.Noise?.CaptureField(null); frame.RestoreWorld(); frame.RestoreCanvas(); } frames.Clear(); }
    private void OnDisable() => Clear();
    private void OnDestroy() => Clear();
    private void LateUpdate()
    {
        try
        {
            foreach (var frame in frames.Values)
            {
                frame.RestoreCanvas();
                if (!frame.Evaluate()) continue;
                var canvas = frame.Main.GetComponentInParent<Canvas>();
                CollectVisibleGraphics(canvas);
                if (canvas != null)
                    foreach (var graphic in visibleGraphics)
                        if (graphic != null && graphic.enabled && !graphic.transform.IsChildOf(frame.Controller.transform) &&
                            !frame.Controller.transform.IsChildOf(graphic.transform)) frame.Add(graphic.transform, true);
                foreach (var cover in frame.Covers) if (cover != null && cover.GetComponent<Graphic>() != null) frame.Add(cover, true);
                frame.Apply(frame.CanvasPoses);
            }
        }
        catch { foreach (var frame in frames.Values) { frame.RestoreCanvas(); frame.ClearTargets(); } throw; }
    }
    private void OnPreCull()
    {
        RestoreIsolation(true);
        foreach (var frame in frames.Values) frame.RestoreWorld();
        try
        {
            foreach (var frame in frames.Values)
            {
                if (!frame.Evaluate()) continue;
                var cover = Traverse.Create(frame.Controller).Field("_movieMaskSprite").GetValue<SpriteRenderer>();
                CollectVisibleRenderers(frame.Controller.gameObject, visibleRenderers);
                foreach (var renderer in visibleRenderers)
                    if (renderer != null) frame.Add(renderer.transform, cover != null && renderer.transform == cover.transform);
                foreach (var target in frame.Covers) if (target != null && target.GetComponent<Graphic>() == null) frame.Add(target, true);
                frame.Apply(frame.WorldPoses);
            }
            var renderCamera = GetComponent<Camera>();
            // Snapshot after MOVE/ROTATE/ZOOM, before OnPostRender restores
            // native poses. The later noise pass uses this exact field matrix.
            foreach (var frame in frames.Values)
                if (frame.Main != null && frame.Main.gameObject.activeInHierarchy && (!GuiSizes.SinglePlayer || frame.Owner.MonitorIndex == 0))
                    frame.Noise?.CaptureField(renderCamera);
            if (!PrepareIsolation())
                foreach (var frame in frames.Values) { frame.RestoreWorld(); frame.RestoreCanvas(); }
        }
        catch { RestoreIsolation(true); foreach (var frame in frames.Values) { frame.RestoreWorld(); frame.RestoreCanvas(); frame.ClearTargets(); } throw; }
    }
    private void OnPostRender()
    { RestoreIsolation(false); foreach (var frame in frames.Values) frame.RestoreWorld(); }
}
