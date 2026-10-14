using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Manager;
using Monitor;
using SinmaiAlpha.Hosting;
using SinmaiAlpha.Notes.Libs;
using UnityEngine;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    [HarmonyPatch(typeof(SlideRoot), "Initialize")]
    public static class SssJudgePathDebugPatch
    {
        [HarmonyPrefix]
        public static void Prefix(SlideRoot __instance)
        {
            if (!Settings.Current.ShowSssJudgePath) return;
            __instance.GetComponent<SssJudgePathDebug>()?.Clear();
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        public static void Postfix(SlideRoot __instance, NoteData note)
        {
            if (!Settings.Current.ShowSssJudgePath || !FeaturesEnabled(__instance) ||
                note is not CustomSlideNoteData custom || custom.IsFan) return;
            try
            {
                var display = __instance.GetComponent<SssJudgePathDebug>();
                if (display == null) display = __instance.gameObject.AddComponent<SssJudgePathDebug>();
                display.Configure(__instance);
            }
            catch (Exception error)
            {
                __instance.GetComponent<SssJudgePathDebug>()?.Clear();
                MelonLoader.MelonLogger.Warning("[SSS Judge Path] Display unavailable: " + error.Message);
            }
        }
    }
}

// Reads the native hit list AFTER Initialize, including mirror/rotation and
// connected-slide expansion. No input, note state, scoring or hit areas are changed.
public sealed class SssJudgePathDebug : MonoBehaviour
{
    private static readonly FieldInfo HitAreas = AccessTools.Field(typeof(SlideRoot), "_hitAreaList");
    private static readonly FieldInfo HitIndex = AccessTools.Field(typeof(SlideRoot), "_hitIndex");
    private static readonly FieldInfo Start = AccessTools.Field(typeof(SlideRoot), "StartMsec");
    private static readonly FieldInfo BaseSprite = AccessTools.Field(typeof(SlideRoot), "BaseSpriteRender");
    private static readonly Color RouteColor = new Color(.1f, .9f, 1f, .8f);
    private static readonly Color BlockedColor = new Color(1f, .2f, .2f, 1f);
    private static readonly Color DoneColor = new Color(.3f, 1f, .4f, .7f);
    private static readonly Color CurrentColor = new Color(1f, .9f, .15f, 1f);
    private struct Marker { internal int Vertex, Step, Area; }
    private readonly List<Marker> markers = new();
    private readonly bool[] blocked = new bool[34];
    private SlideRoot owner;
    private GameObject visual;
    private Mesh mesh;
    private Material material;
    private Color[] colors;
    private float startMsec;

    internal void Configure(SlideRoot slide)
    {
        Clear();
        var areas = HitAreas?.GetValue(slide) as List<SlideManager.HitArea>;
        if (areas == null || areas.Count == 0 || HitIndex == null || Start == null) return;
        var shader = Shader.Find("Sprites/Default");
        if (shader == null || !shader.isSupported) shader = Shader.Find("UI/Default");
        if (shader == null || !shader.isSupported) throw new InvalidOperationException("No supported debug line shader");
        owner = slide;
        startMsec = (float)Start.GetValue(slide);
        visual = new GameObject("SSS judge area centers") { layer = slide.gameObject.layer };
        // HitPoints are already relative to ButtonId. SlideRoot rotates by
        // -45 * ButtonId, so applying that rotation again would show a wrong path.
        visual.transform.SetParent(slide.transform, false);
        visual.transform.localPosition = new Vector3(0, 0, -4.5f);
        var vertices = new List<Vector3>();
        var triangles = new List<int>();
        var initialColors = new List<Color>();
        for (var step = 0; step < areas.Count; step++)
        {
            foreach (var point in areas[step].HitPoints)
            {
                if ((int)point < 0 || (int)point >= 34) continue;
                var center = Center((int)point);
                if (step > 0)
                    foreach (var previous in areas[step - 1].HitPoints)
                    {
                        if ((int)previous < 0 || (int)previous >= 34) continue;
                        AddLine(Center((int)previous), center, vertices, triangles, initialColors);
                    }
                markers.Add(new Marker { Vertex = vertices.Count, Step = step, Area = (int)point });
                AddQuad(center + Vector3.left * 4, center + Vector3.up * 4,
                    center + Vector3.right * 4, center + Vector3.down * 4, vertices, triangles, initialColors);
            }
        }
        mesh = new Mesh { name = "SSS actual judge path" };
        mesh.SetVertices(vertices);
        mesh.SetTriangles(triangles, 0);
        colors = initialColors.ToArray();
        mesh.colors = colors;
        mesh.RecalculateBounds();
        visual.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = visual.AddComponent<MeshRenderer>();
        material = new Material(shader);
        material.mainTexture = Texture2D.whiteTexture;
        renderer.sharedMaterial = material;
        var sprite = BaseSprite?.GetValue(slide) as SpriteRenderer;
        if (sprite != null)
        {
            renderer.sortingLayerID = sprite.sortingLayerID;
            visual.layer = sprite.gameObject.layer;
        }
        renderer.sortingOrder = 32700;
        visual.SetActive(false);
    }

    private static Vector3 Center(int area)
    {
        var point = SssJudgePathGeometry.Center(area);
        return new Vector3(point.X, point.Y, 0);
    }
    private static void AddLine(Vector3 a, Vector3 b, List<Vector3> vertices, List<int> triangles, List<Color> colors)
    {
        var delta = b - a;
        if (delta.sqrMagnitude < .001f) return;
        var side = new Vector3(-delta.y, delta.x, 0).normalized * .8f;
        AddQuad(a - side, a + side, b + side, b - side, vertices, triangles, colors);
    }
    private static void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d,
        List<Vector3> vertices, List<int> triangles, List<Color> colors)
    {
        var index = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        for (var i = 0; i < 4; i++) colors.Add(RouteColor);
        triangles.Add(index); triangles.Add(index + 1); triangles.Add(index + 2);
        triangles.Add(index); triangles.Add(index + 2); triangles.Add(index + 3);
    }
    private void LateUpdate()
    {
        if (owner == null || visual == null) return;
        var visible = Settings.Current.ShowSssJudgePath && !owner.IsEnd() &&
            owner.isActiveAndEnabled && NotesManager.GetCurrentMsec() >= startMsec;
        if (visual.activeSelf != visible) visual.SetActive(visible);
        if (!visible) return;
        var index = (int)HitIndex.GetValue(owner);
        for (var area = 0; area < blocked.Length; area++)
            blocked[area] = CustomNoteTypes.IsNoiseAreaBlocked(owner.MonitorId,
                SssJudgePathGeometry.PhysicalArea(area, owner.ButtonId));
        var changed = false;
        foreach (var marker in markers)
        {
            var color = blocked[marker.Area] ? BlockedColor : marker.Step < index ? DoneColor :
                marker.Step == index ? CurrentColor : RouteColor;
            if (colors[marker.Vertex] == color) continue;
            for (var v = 0; v < 4; v++) colors[marker.Vertex + v] = color;
            changed = true;
        }
        if (changed) mesh.colors = colors;
    }
    private void OnDisable() { if (visual != null) visual.SetActive(false); }
    private void OnDestroy() => Clear();
    internal void Clear()
    {
        if (visual != null) { visual.SetActive(false); Object.Destroy(visual); }
        if (mesh != null) Object.Destroy(mesh);
        if (material != null) Object.Destroy(material);
        visual = null; mesh = null; material = null; owner = null; colors = null;
        markers.Clear();
    }
}
