// Display geometry and compositing sequence adapted from the supplied
// MajdataPlayAlpha compiled reference, GPL-3.0; see the frozen reference and
// NoiseZoneSources.md. This file has no input, judgment or score implementation.
using System;
using System.Collections.Generic;
using System.Linq;
using SinmaiAlpha.ChartVisuals;
using SinmaiAlpha.Notes.Libs;
using UnityEngine;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    internal sealed class NoiseZoneDisplay : MonoBehaviour
    {
        private readonly NoiseZoneTimeline timeline = new NoiseZoneTimeline();

        private readonly float[] warnings = new float[33];

        private readonly float[] warningShines = new float[33];

        private readonly bool[] active = new bool[33];

        private readonly bool[] visible = new bool[33];

        private readonly bool[] warningZones = new bool[33];

        private readonly List<UnityEngine.Vector3> vertices = new List<UnityEngine.Vector3>();

        private readonly List<UnityEngine.Vector2> uvs = new List<UnityEngine.Vector2>();

        private readonly List<UnityEngine.Vector2> flags = new List<UnityEngine.Vector2>();

        private readonly List<Color> colors = new List<Color>();

        private readonly List<int> triangles = new List<int>();

        private System.Numerics.Vector2[] centers;

        private List<System.Numerics.Vector2>[] cells;

        private readonly List<UnityEngine.Vector2> projected = new List<UnityEngine.Vector2>();

        internal Func<double> Clock;

        private GameObject visual;

        private Mesh mesh;

        private Material material;

        private double lastTime = 0.0 / 0.0;

        private const float Radius = 4.8f;

        internal bool HasActive => Enumerable.Any(visible, (bool value) => value);

        internal double Time => Clock?.Invoke() ?? 0.0;

        internal UnityEngine.Matrix4x4 FieldMatrix
        {
            get
            {
                if (!(visual == null))
                {
                    return visual.transform.localToWorldMatrix;
                }
                return UnityEngine.Matrix4x4.identity;
            }
        }

        internal void Configure(IEnumerable<NoiseZoneEvent> events, Func<double> clock,
            IReadOnlyList<System.Numerics.Vector2> fieldCenters, Shader warningShader, int layer, int sortingLayer)
        {
            if (fieldCenters == null || fieldCenters.Count != 33) throw new ArgumentException("Noise fields need all 33 reference sensor centers");
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (warningShader == null || !warningShader.isSupported) throw new ArgumentException("Native noise warning shader is unavailable");
            Clear();
            Clock = clock;
            timeline.Configure(events);
            centers = fieldCenters.ToArray();
            cells = NoiseZoneGeometry.BuildCells(centers, 4.8f);
            visual = new GameObject("AquaMai noise zones");
            visual.layer = layer;
            visual.transform.SetParent(base.transform, worldPositionStays: false);
            visual.transform.localPosition = new UnityEngine.Vector3(0f, 0f, -0.06f);
            mesh = new Mesh
            {
                name = "Noise zone union"
            };
            material = new Material(warningShader);
            visual.AddComponent<MeshFilter>().sharedMesh = mesh;
            MeshRenderer meshRenderer = visual.AddComponent<MeshRenderer>();
            meshRenderer.sharedMaterial = material;
            meshRenderer.sortingOrder = 32000;
            meshRenderer.sortingLayerID = sortingLayer;
            lastTime = 0.0 / 0.0;
            RenderAt(Time);
        }

        private void LateUpdate()
        {
            if (Clock != null && visual != null)
            {
                RenderAt(Time);
            }
        }

        internal void RenderAt(double time)
        {
            if (time == lastTime || visual == null)
            {
                return;
            }
            lastTime = time;
            timeline.Evaluate(time, warnings, active);
            for (int i = 0; i < 33; i++)
            {
                visible[i] = timeline.TryGetVisualSpan(i, time, out var _, out var _);
                warningZones[i] = !visible[i] && warnings[i] > 0f;
            }
            for (int j = 0; j < 33; j++)
            {
                warningShines[j] = timeline.WarningShine(j, time);
            }
            NoiseZoneGeometry.MergeWarningLevels(cells, centers, warningZones, 4.8f, warnings, warningShines);
            material.SetFloat("_ZoneTime", (float)time);
            vertices.Clear();
            uvs.Clear();
            flags.Clear();
            colors.Clear();
            triangles.Clear();
            for (int k = 0; k < 33; k++)
            {
                if (!warningZones[k] || cells[k].Count < 3)
                {
                    continue;
                }
                List<System.Numerics.Vector2> list = cells[k];
                _ = warnings[k];
                float num = warningShines[k];
                if (!(num > 0f))
                {
                    continue;
                }
                int count = vertices.Count;
                foreach (System.Numerics.Vector2 item in list)
                {
                    AddVertex(item, new Color(1f, 1f, 1f, num), 0f);
                }
                for (int l = 1; l + 1 < list.Count; l++)
                {
                    AddTriangle(count, count + l, count + l + 1);
                }
            }
            foreach (NoiseZoneGeometry.Contour item2 in NoiseZoneGeometry.UnionContours(cells, centers, warningZones, 4.8f))
            {
                float num2 = warningShines[item2.Sensor];
                float t = ((num2 > 0f) ? Mathf.Pow(Mathf.Clamp01((num2 - 0.06f) / 0.12f), 2f) : 0f);
                Color color = Color.Lerp(new Color(1f, 0.05f, 0.07f, warnings[item2.Sensor]), Color.white, t);
                List<System.Numerics.Vector2> points = item2.Points;
                int count2 = vertices.Count;
                for (int m = 0; m < points.Count; m++)
                {
                    System.Numerics.Vector2 vector = points[(m + points.Count - 1) % points.Count];
                    System.Numerics.Vector2 vector2 = points[m];
                    System.Numerics.Vector2 vector3 = points[(m + 1) % points.Count];
                    System.Numerics.Vector2 vector4 = System.Numerics.Vector2.Normalize(vector2 - vector);
                    System.Numerics.Vector2 vector5 = System.Numerics.Vector2.Normalize(vector3 - vector2);
                    System.Numerics.Vector2 vector6 = new System.Numerics.Vector2(0f - vector4.Y, vector4.X);
                    System.Numerics.Vector2 vector7 = new System.Numerics.Vector2(0f - vector5.Y, vector5.X);
                    System.Numerics.Vector2 value = vector6 + vector7;
                    System.Numerics.Vector2 vector8 = ((value.LengthSquared() > 1E-06f) ? System.Numerics.Vector2.Normalize(value) : vector7);
                    System.Numerics.Vector2 vector9 = vector8 * (0.022f / Math.Max(0.35f, System.Numerics.Vector2.Dot(vector8, vector7)));
                    AddVertex(vector2, color, 0f);
                    AddVertex(vector2 + vector9, color, 0f);
                }
                for (int n = 0; n < points.Count; n++)
                {
                    int num3 = count2 + n * 2;
                    int num4 = count2 + (n + 1) % points.Count * 2;
                    AddTriangle(num3, num4, num4 + 1);
                    AddTriangle(num3, num4 + 1, num3 + 1);
                }
            }
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetUVs(0, uvs);
            mesh.SetUVs(1, flags);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
        }

        private void AddVertex(System.Numerics.Vector2 point, Color color, float kind)
        {
            vertices.Add(new UnityEngine.Vector3(point.X, point.Y, 0f));
            uvs.Add(new UnityEngine.Vector2(point.X, point.Y));
            flags.Add(new UnityEngine.Vector2(kind, 0f));
            colors.Add(color);
        }

        private void AddTriangle(int a, int b, int c)
        {
            triangles.Add(a);
            triangles.Add(b);
            triangles.Add(c);
        }

        internal void FillActiveMask(Camera camera, UnityEngine.Matrix4x4 field, int width, int height, byte[] pixels)
            => FillActiveMask(camera, field, width, height, pixels, new PresentationRect(0, 0, 1, 1));

        internal void FillActiveMask(Camera camera, UnityEngine.Matrix4x4 field, int width, int height, byte[] pixels, PresentationRect viewport)
        {
            Array.Clear(pixels, 0, pixels.Length);
            for (int i = 0; i < active.Length; i++)
            {
                if (!visible[i] || !timeline.TryGetVisualSpan(i, Time, out var start, out var end))
                {
                    continue;
                }
                double num = ((end - start <= 0.25) ? 0.0 : Math.Min(0.24, (end - start) * 0.3));
                bool flag = Time < start + num;
                bool flag2 = Time > end - num;
                bool flag3 = false;
                if (flag | flag2)
                {
                    List<System.Numerics.Vector2> list = cells[i];
                    for (int j = 0; j < list.Count; j++)
                    {
                        System.Numerics.Vector2 a = list[j];
                        System.Numerics.Vector2 b = list[(j + 1) % list.Count];
                        int num2 = NoiseZoneGeometry.EdgeNeighbor(i, a, b, centers, 4.8f);
                        if (num2 >= 0)
                        {
                            double num3 = (flag ? start : end);
                            if (timeline.TryGetActiveSpan(num2, num3, out var start2, out var _) && !(start2 >= num3))
                            {
                                float progress = (float)((flag ? (Time - start) : (end - Time)) / num);
                                RasterCell(NoiseZoneGeometry.GrowFromEdge(list, a, b, progress), camera, field, width, height, pixels, viewport);
                                flag3 = true;
                            }
                        }
                    }
                }
                if (!flag3)
                {
                    List<System.Numerics.Vector2> cell = (flag2 ? NoiseZoneGeometry.ShrinkToCenter(cells[i], centers[i], (float)((end - Time) / num)) : cells[i]);
                    RasterCell(cell, camera, field, width, height, pixels, viewport);
                }
            }
        }

        private void RasterCell(List<System.Numerics.Vector2> cell, Camera camera, UnityEngine.Matrix4x4 field, int width, int height, byte[] pixels, PresentationRect viewport)
        {
            if (cell.Count < 3)
            {
                return;
            }
            projected.Clear();
            float num = width;
            float num2 = height;
            float num3 = 0f;
            float num4 = 0f;
            foreach (System.Numerics.Vector2 item2 in cell)
            {
                UnityEngine.Vector3 position = field.MultiplyPoint3x4(new UnityEngine.Vector3(item2.X, item2.Y, 0f));
                UnityEngine.Vector3 vector = camera.WorldToViewportPoint(position);
                vector.x = (vector.x - viewport.X) / viewport.Width;
                vector.y = (vector.y - viewport.Y) / viewport.Height;
                UnityEngine.Vector2 item = new UnityEngine.Vector2(vector.x * (float)width, vector.y * (float)height);
                projected.Add(item);
                num = Mathf.Min(num, item.x);
                num3 = Mathf.Max(num3, item.x);
                num2 = Mathf.Min(num2, item.y);
                num4 = Mathf.Max(num4, item.y);
            }
            int num5 = Mathf.Clamp(Mathf.FloorToInt(num), 0, width);
            int num6 = Mathf.Clamp(Mathf.CeilToInt(num3), 0, width);
            int num7 = Mathf.Clamp(Mathf.FloorToInt(num2), 0, height);
            int num8 = Mathf.Clamp(Mathf.CeilToInt(num4), 0, height);
            for (int i = num7; i < num8; i++)
            {
                for (int j = num5; j < num6; j++)
                {
                    if (pixels[i * width + j] != 0)
                    {
                        continue;
                    }
                    UnityEngine.Vector2 vector2 = new UnityEngine.Vector2((float)j + 0.5f, (float)i + 0.5f);
                    bool flag = false;
                    bool flag2 = false;
                    for (int k = 0; k < projected.Count; k++)
                    {
                        UnityEngine.Vector2 vector3 = projected[k];
                        UnityEngine.Vector2 vector4 = projected[(k + 1) % projected.Count];
                        float num9 = (vector4.x - vector3.x) * (vector2.y - vector3.y) - (vector4.y - vector3.y) * (vector2.x - vector3.x);
                        flag |= num9 > 1E-05f;
                        flag2 |= num9 < -1E-05f;
                        if (flag & flag2)
                        {
                            break;
                        }
                    }
                    if (!(flag & flag2))
                    {
                        pixels[i * width + j] = 255;
                    }
                }
            }
        }

        public void Clear()
        {
            timeline.Configure(null);
            Array.Clear(active, 0, active.Length);
            Array.Clear(visible, 0, visible.Length);
            Array.Clear(warnings, 0, warnings.Length);
            if (visual != null)
            {
                visual.SetActive(value: false);
                Retire(visual);
            }
            if (mesh != null)
            {
                Retire(mesh);
            }
            if (material != null)
            {
                Retire(material);
            }
            Clock = null;
            visual = null;
            mesh = null;
            material = null;
            lastTime = 0.0 / 0.0;
        }

        private void OnDestroy()
        {
            Clear();
        }

        private static void Retire(UnityEngine.Object value)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(value);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }
    }
    internal sealed class NoiseZoneCompositor : IDisposable
    {
        private NoiseZoneDisplay owner;

        private Material material;

        private Texture2D sourceMask;

        private byte[] maskBytes;

        private RenderTexture compose;

        private RenderTexture ping;

        private RenderTexture pong;

        private readonly Shader shader;
        private readonly Texture displacement;
        private readonly Texture spark;
        internal NoiseZoneCompositor(Shader shader, Texture displacement, Texture spark)
        {
            if (shader == null || !shader.isSupported || displacement == null || spark == null)
                throw new ArgumentException("Native noise resources are unavailable");
            this.shader = shader;
            this.displacement = displacement;
            this.spark = spark;
        }

        private int width;

        private int height;

        private static readonly float[] GlowWeights = MakeGlowWeights();

        internal Material Prepare(NoiseZoneDisplay value, Camera camera, int outputWidth, int outputHeight)
            => Prepare(value, camera, outputWidth, outputHeight, value != null ? value.FieldMatrix : Matrix4x4.identity, new PresentationRect(0, 0, 1, 1));

        internal Material Prepare(NoiseZoneDisplay value, Camera camera, int outputWidth, int outputHeight, Matrix4x4 field, PresentationRect viewport)
        {
            owner = value;
            if (owner == null || !owner.HasActive)
            {
                return null;
            }
            Ensure(outputWidth, outputHeight);
            owner.FillActiveMask(camera, field, width, height, maskBytes, viewport);
            sourceMask.LoadRawTextureData(maskBytes);
            sourceMask.Apply(updateMipmaps: false);
            material.SetFloat("_ZoneTime", (float)owner.Time);
            material.SetVector("_OutputSize", new UnityEngine.Vector4(outputWidth, outputHeight, 1f / (float)outputWidth, 1f / (float)outputHeight));
            ping.filterMode = FilterMode.Point;
            pong.filterMode = FilterMode.Point;
            var previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(sourceMask, compose, material, 0);
                Graphics.Blit(compose, ping, material, 1);
                for (int i = 0; i < GlowWeights.Length && !(GlowWeights[i] < 0.01f); i++)
                {
                    material.SetFloat("_FirstRing", (i == 0) ? 1 : 0);
                    material.SetFloat("_GlowWeight", GlowWeights[i]);
                    Graphics.Blit(ping, pong, material, 2);
                    RenderTexture renderTexture = pong;
                    RenderTexture renderTexture2 = ping;
                    ping = renderTexture;
                    pong = renderTexture2;
                }
                ping.filterMode = FilterMode.Bilinear;
                material.SetTexture("_Masks", ping);
                return material;
            }
            finally { RenderTexture.active = previousActive; }
        }

        private void Ensure(int outputWidth, int outputHeight)
        {
            if (material == null)
            {
                material = new Material(shader)
                {
                    hideFlags = HideFlags.HideAndDontSave
                };
                material.SetTexture("_DisplaceTex", displacement);
                material.SetTexture("_SparkTex", spark);
            }
            int num = Math.Max(1, outputWidth / 8);
            int num2 = Math.Max(1, outputHeight / 8);
            if (num != width || num2 != height || !(sourceMask != null))
            {
                ReleaseMasks();
                width = num;
                height = num2;
                maskBytes = new byte[width * height];
                sourceMask = new Texture2D(width, height, TextureFormat.R8, mipChain: false, linear: true)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Clamp
                };
                compose = Target(width, height, RenderTextureFormat.R8);
                ping = Target(width * 2, height * 2, RenderTextureFormat.ARGB32);
                pong = Target(width * 2, height * 2, RenderTextureFormat.ARGB32);
            }
        }

        private static RenderTexture Target(int w, int h, RenderTextureFormat format)
        {
            RenderTexture renderTexture = new RenderTexture(w, h, 0, format, RenderTextureReadWrite.Linear);
            renderTexture.filterMode = FilterMode.Point;
            renderTexture.wrapMode = TextureWrapMode.Clamp;
            renderTexture.hideFlags = HideFlags.HideAndDontSave;
            if (!renderTexture.Create())
            {
                Retire(renderTexture);
                throw new InvalidOperationException("Native noise mask allocation failed");
            }
            return renderTexture;
        }

        private static float[] MakeGlowWeights()
        {
            float num = 0f;
            for (int i = 1; i <= 6; i++)
            {
                num += Mathf.Pow(i, 2.65f);
            }
            float[] array = new float[6];
            for (int j = 0; j < 6; j++)
            {
                array[j] = Mathf.Pow(6 - j, 2.65f) / num;
            }
            return array;
        }

        private void ReleaseMasks()
        {
            RenderTexture[] array = new RenderTexture[3] { compose, ping, pong };
            foreach (RenderTexture renderTexture in array)
            {
                if (renderTexture != null)
                {
                    renderTexture.Release();
                    Retire(renderTexture);
                }
            }
            compose = null;
            ping = null;
            pong = null;
            if (sourceMask != null)
            {
                Retire(sourceMask);
            }
            sourceMask = null;
            maskBytes = null;
            width = height = 0;
        }

        public void Dispose()
        {
            ReleaseMasks();
            if (material != null)
            {
                Retire(material);
            }
            material = null;
            owner = null;
        }

        private static void Retire(UnityEngine.Object value)
        {
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(value);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(value);
            }
        }
    }
}
