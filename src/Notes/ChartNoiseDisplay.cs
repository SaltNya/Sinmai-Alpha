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
        private readonly List<System.Numerics.Vector2>[] warningCells = new List<System.Numerics.Vector2>[33];
        private readonly NoiseZoneVisual[] styles = new NoiseZoneVisual[33];
        private NoiseZoneEvent[] visualEvents = new NoiseZoneEvent[0];
        private readonly struct NoiseZoneVisual
        {
            public readonly Color Color; public readonly Vector2 Size; public readonly bool CustomColor;
            public NoiseZoneVisual(VisualValue value)
            {
                var rgb = value?.Color ?? 0xFF0000;
                Color = new Color(((rgb >> 16) & 255)/255f, ((rgb >> 8) & 255)/255f, (rgb & 255)/255f, value?.Alpha ?? 1);
                Size = new Vector2(value?.X ?? 1, value?.Y ?? 1); CustomColor = value?.Color != null;
            }
        }
        private static float Progress(NoiseZoneEvent zone, double time) => zone.Controls != null
            ? zone.Controls.WarningProgress(time - zone.Start, zone.NativeLead)
            : time < zone.WarningStart || time >= zone.Start ? -1 : (float)((time - zone.WarningStart)/(zone.Start-zone.WarningStart));

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
            visualEvents = events.ToArray();
            timeline.Configure(visualEvents);
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
            material.SetTexture("_DisplaceTex", NoiseGraphics.Displacement);
            material.SetTexture("_SparkTex", NoiseGraphics.Spark);
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
            Array.Clear(warnings, 0, warnings.Length);
            foreach (var zone in visualEvents)
            {
                var progress = Progress(zone, time);
                if (progress >= 0) warnings[zone.Sensor] = Math.Max(warnings[zone.Sensor], 1 - .55f * progress);
            }
            for (var i = 0; i < 33; i++)
            {
                visible[i] = timeline.TryGetVisualSpan(i, time, out _, out _);
                warningZones[i] = !visible[i] && warnings[i] > 0;
                NoiseZoneEvent? selected = null;
                foreach (var zone in visualEvents)
                {
                    if (zone.Sensor != i || time >= zone.End || time < zone.Start && Progress(zone,time) < 0) continue;
                    if (!selected.HasValue || zone.Start <= time && selected.Value.Start > time ||
                        (zone.Start <= time) == (selected.Value.Start <= time) && (zone.Start <= time ? zone.Start >= selected.Value.Start : zone.Start < selected.Value.Start)) selected = zone;
                }
                styles[i] = new NoiseZoneVisual(selected?.Controls?.Style(time - selected.Value.Start));
                if (warningCells[i] == null) warningCells[i] = new List<System.Numerics.Vector2>();
                warningCells[i].Clear(); foreach(var point in cells[i]) warningCells[i].Add(ScalePoint(point,i));
                warningShines[i] = warningZones[i] ? (float)(.06 * (1 + .5 * Math.Sin(time * 37.9))) : 0;
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
			List<System.Numerics.Vector2> list = warningCells[k];
			float num4 = warnings[k];
			float num5 = warningShines[k];
			int count = vertices.Count;
			Color color = (styles[k].CustomColor ? styles[k].Color : Color.white);
			color.a = num4 * styles[k].Color.a;
			foreach (System.Numerics.Vector2 item2 in list)
			{
				AddVertex(item2, color, 1f);
			}
			for (int l = 1; l + 1 < list.Count; l++)
			{
				AddTriangle(count, count + l, count + l + 1);
			}
			if (!(num5 > 0f))
			{
				continue;
			}
			int count2 = vertices.Count;
			foreach (System.Numerics.Vector2 item3 in list)
			{
				AddVertex(item3, new Color(1f, 1f, 1f, num5 * styles[k].Color.a), 0f);
			}
			for (int m = 1; m + 1 < list.Count; m++)
			{
				AddTriangle(count2, count2 + m, count2 + m + 1);
			}
		}
		foreach (NoiseZoneGeometry.Contour item4 in NoiseZoneGeometry.UnionContours(warningCells, centers, warningZones, 4.8f))
		{
			float num6 = warningShines[item4.Sensor];
			float t = ((num6 > 0f) ? (0.35f * Mathf.Pow(Mathf.Clamp01(num6 / 0.09f), 2f)) : 0f);
			Color color2 = styles[item4.Sensor].Color;
			Color color3 = Color.Lerp(styles[item4.Sensor].CustomColor ? color2 : new Color(1f, 0.33018857f, 0.33018857f, 1f), Color.white, t);
			color3.a = warnings[item4.Sensor] * color2.a;
			List<System.Numerics.Vector2> points = item4.Points;
			int count3 = vertices.Count;
			for (int n = 0; n < points.Count; n++)
			{
				System.Numerics.Vector2 vector = points[(n + points.Count - 1) % points.Count];
				System.Numerics.Vector2 vector2 = points[n];
				System.Numerics.Vector2 vector3 = points[(n + 1) % points.Count];
				System.Numerics.Vector2 vector4 = System.Numerics.Vector2.Normalize(vector2 - vector);
				System.Numerics.Vector2 vector5 = System.Numerics.Vector2.Normalize(vector3 - vector2);
				System.Numerics.Vector2 vector6 = new System.Numerics.Vector2(0f - vector4.Y, vector4.X);
				System.Numerics.Vector2 vector7 = new System.Numerics.Vector2(0f - vector5.Y, vector5.X);
				System.Numerics.Vector2 value = vector6 + vector7;
				System.Numerics.Vector2 vector8 = ((value.LengthSquared() > 1E-06f) ? System.Numerics.Vector2.Normalize(value) : vector7);
				System.Numerics.Vector2 vector9 = vector8 * (0.022f / Math.Max(0.35f, System.Numerics.Vector2.Dot(vector8, vector7)));
				AddVertex(vector2, color3, 0f);
				AddVertex(vector2 + vector9, color3, 0f);
			}
			for (int num7 = 0; num7 < points.Count; num7++)
			{
				int num8 = count3 + num7 * 2;
				int num9 = count3 + (num7 + 1) % points.Count * 2;
				AddTriangle(num8, num9, num9 + 1);
				AddTriangle(num8, num9 + 1, num8 + 1);
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

	private System.Numerics.Vector2 ScalePoint(System.Numerics.Vector2 p, int sensor)
	{
		return centers[sensor] + (p - centers[sensor]) * new System.Numerics.Vector2(styles[sensor].Size.x, styles[sensor].Size.y);
	}

        // Keep warning geometry out of the active field's displaced scene sampling.
        internal void FillWarningMask(Camera camera, Matrix4x4 field, int width, int height, byte[] pixels, PresentationRect viewport)
        {
            Array.Clear(pixels, 0, pixels.Length);
            for (int i = 0; i < warningZones.Length; i++)
                if (warningZones[i] && styles[i].Color.a > 0)
                    RasterCell(cells[i], i, camera, field, width, height, pixels, null, viewport);
        }

	internal void FillActiveMask(Camera camera, UnityEngine.Matrix4x4 field, int width, int height, byte[] pixels, PresentationRect viewport, Color32[] stylePixels = null)
	{
		Array.Clear(pixels, 0, pixels.Length);
		if (stylePixels != null)
		{
			Array.Clear(stylePixels, 0, stylePixels.Length);
		}
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
							RasterCell(NoiseZoneGeometry.GrowFromEdge(list, a, b, progress), i, camera, field, width, height, pixels, stylePixels, viewport);
							flag3 = true;
						}
					}
				}
			}
			if (!flag3)
			{
				List<System.Numerics.Vector2> cell = (flag2 ? NoiseZoneGeometry.ShrinkToCenter(cells[i], centers[i], (float)((end - Time) / num)) : cells[i]);
				RasterCell(cell, i, camera, field, width, height, pixels, stylePixels, viewport);
			}
		}
	}

	private void RasterCell(List<System.Numerics.Vector2> cell, int sensor, Camera camera, UnityEngine.Matrix4x4 field, int width, int height, byte[] pixels, Color32[] stylePixels, PresentationRect viewport)
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
			System.Numerics.Vector2 vector = ScalePoint(item2, sensor);
			UnityEngine.Vector3 position = field.MultiplyPoint3x4(new UnityEngine.Vector3(vector.X, vector.Y, 0f));
			UnityEngine.Vector3 vector2 = camera.WorldToViewportPoint(position);
            vector2.x = (vector2.x - viewport.X) / viewport.Width;
            vector2.y = (vector2.y - viewport.Y) / viewport.Height;
			UnityEngine.Vector2 item = new UnityEngine.Vector2(vector2.x * (float)width, vector2.y * (float)height);
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
				UnityEngine.Vector2 vector3 = new UnityEngine.Vector2((float)j + 0.5f, (float)i + 0.5f);
				bool flag = false;
				bool flag2 = false;
				for (int k = 0; k < projected.Count; k++)
				{
					UnityEngine.Vector2 vector4 = projected[k];
					UnityEngine.Vector2 vector5 = projected[(k + 1) % projected.Count];
					float num9 = (vector5.x - vector4.x) * (vector3.y - vector4.y) - (vector5.y - vector4.y) * (vector3.x - vector4.x);
					flag |= num9 > 1E-05f;
					flag2 |= num9 < -1E-05f;
					if (flag & flag2)
					{
						break;
					}
				}
				if (!(flag & flag2))
				{
					pixels[i * width + j] = byte.MaxValue;
					if (stylePixels != null)
					{
						stylePixels[i * width + j] = styles[sensor].Color;
					}
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
        private Texture2D warningMask;
        private byte[] warningBytes;
        private byte[] warningScratch;
        private Texture2D sourceStyles;
        private Color32[] stylePixels;
        private int[] styleQueue, styleDistance;

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
            owner.FillActiveMask(camera, field, width, height, maskBytes, viewport, stylePixels);
            owner.FillWarningMask(camera, field, outputWidth, outputHeight, warningBytes, viewport);
            ProtectWarningBorder(outputWidth, outputHeight);
            warningMask.LoadRawTextureData(warningBytes); warningMask.Apply(false);
            material.SetTexture("_Warnings", warningMask);
            SpreadStyles();
            sourceStyles.SetPixels32(stylePixels); sourceStyles.Apply(false);
            material.SetTexture("_Styles", sourceStyles);
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
            int num = Math.Max(1, outputWidth / 4);
            int num2 = Math.Max(1, outputHeight / 4);
            if (num != width || num2 != height || sourceMask == null || warningMask == null || warningMask.width != outputWidth || warningMask.height != outputHeight)
            {
                ReleaseMasks();
                width = num;
                height = num2;
                warningBytes = new byte[outputWidth * outputHeight];
                warningScratch = new byte[warningBytes.Length];
                warningMask = new Texture2D(outputWidth, outputHeight, TextureFormat.R8, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
                maskBytes = new byte[width * height];
                stylePixels = new Color32[width * height]; styleQueue = new int[width * height]; styleDistance = new int[width * height];
                sourceStyles = new Texture2D(width, height, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
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

        private void ProtectWarningBorder(int w, int h)
        {
            // Include the existing outline and the scene texture's bilinear footprint.
            // Separate horizontal/vertical passes avoid allocating per frame.
            Array.Clear(warningScratch, 0, warningScratch.Length);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (warningBytes[y * w + x] != 0)
                        for (int nx = Math.Max(0, x - 2); nx <= Math.Min(w - 1, x + 2); nx++)
                            warningScratch[y * w + nx] = 255;
            Array.Clear(warningBytes, 0, warningBytes.Length);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (warningScratch[y * w + x] != 0)
                        for (int ny = Math.Max(0, y - 2); ny <= Math.Min(h - 1, y + 2); ny++)
                            warningBytes[ny * w + x] = 255;
        }

        private void SpreadStyles()
        {
            var head = 0; var tail = 0;
            for (var i=0;i<maskBytes.Length;i++) { styleDistance[i] = maskBytes[i] == 0 ? -1 : 0; if(maskBytes[i]!=0) styleQueue[tail++]=i; }
            while(head<tail) {
                var i=styleQueue[head++]; var x=i%width; var y=i/width;
                if(x>0) Spread(i,i-1); if(x+1<width) Spread(i,i+1); if(y>0) Spread(i,i-width); if(y+1<height) Spread(i,i+width);
            }
            void Spread(int source,int target) { if(styleDistance[target]>=0)return; stylePixels[target]=stylePixels[source]; styleDistance[target]=styleDistance[source]+1; styleQueue[tail++]=target; }
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
            if (sourceStyles != null) Retire(sourceStyles);
            sourceStyles = null; stylePixels = null; styleQueue = styleDistance = null;
            if (warningMask != null) Retire(warningMask);
            warningMask = null; warningBytes = warningScratch = null;
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
