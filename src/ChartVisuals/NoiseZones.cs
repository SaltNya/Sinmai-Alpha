// Noise geometry and timeline from the user supplied MajdataPlayAlpha 2.0.3-alpha.8
// GPL-3.0; see mc_repro/alpha-noise-20261004/reference-manifest.json.
using System;
using System.Collections.Generic;
namespace SinmaiAlpha.ChartVisuals
{
public static class NoiseZoneGeometry
{
	public sealed class Contour
	{
		public int Sensor;

		public readonly List<System.Numerics.Vector2> Points = new List<System.Numerics.Vector2>();
	}

	public static List<Contour> UnionContours(List<System.Numerics.Vector2>[] cells, IReadOnlyList<System.Numerics.Vector2> centers, IReadOnlyList<bool> selected, float radius)
	{
		List<(int, System.Numerics.Vector2, System.Numerics.Vector2)> list = new List<(int, System.Numerics.Vector2, System.Numerics.Vector2)>();
		for (int i = 0; i < cells.Length; i++)
		{
			if (!selected[i])
			{
				continue;
			}
			for (int j = 0; j < cells[i].Count; j++)
			{
				System.Numerics.Vector2 vector = cells[i][j];
				System.Numerics.Vector2 vector2 = cells[i][(j + 1) % cells[i].Count];
				if (IsUnionBoundary(i, vector, vector2, centers, selected, radius))
				{
					list.Add((i, vector, vector2));
				}
			}
		}
		List<Contour> list2 = new List<Contour>();
		while (list.Count > 0)
		{
			(int, System.Numerics.Vector2, System.Numerics.Vector2) tuple = list[0];
			list.RemoveAt(0);
			Contour contour = new Contour
			{
				Sensor = tuple.Item1
			};
			contour.Points.Add(tuple.Item2);
			System.Numerics.Vector2 next = tuple.Item3;
			while (System.Numerics.Vector2.DistanceSquared(next, tuple.Item2) > 1E-06f)
			{
				contour.Points.Add(next);
				int num = list.FindIndex(((int sensor, System.Numerics.Vector2 a, System.Numerics.Vector2 b) e) => System.Numerics.Vector2.DistanceSquared(e.a, next) < 1E-06f);
				if (num < 0)
				{
					break;
				}
				next = list[num].Item3;
				list.RemoveAt(num);
			}
			if (contour.Points.Count >= 3)
			{
				list2.Add(contour);
			}
		}
		return list2;
	}

	public static void MergeWarningLevels(List<System.Numerics.Vector2>[] cells, IReadOnlyList<System.Numerics.Vector2> centers, IReadOnlyList<bool> selected, float radius, float[] opacity, float[] shine)
	{
		bool[] array = new bool[cells.Length];
		for (int i = 0; i < cells.Length; i++)
		{
			if (!selected[i] || array[i])
			{
				continue;
			}
			List<int> list = new List<int> { i };
			array[i] = true;
			float num = opacity[i];
			float num2 = shine[i];
			for (int j = 0; j < list.Count; j++)
			{
				int num3 = list[j];
				for (int k = 0; k < cells[num3].Count; k++)
				{
					int num4 = EdgeNeighbor(num3, cells[num3][k], cells[num3][(k + 1) % cells[num3].Count], centers, radius);
					if (num4 >= 0 && selected[num4] && !array[num4])
					{
						array[num4] = true;
						list.Add(num4);
						num = Math.Max(num, opacity[num4]);
						num2 = Math.Max(num2, shine[num4]);
					}
				}
			}
			foreach (int item in list)
			{
				opacity[item] = num;
				shine[item] = num2;
			}
		}
	}

	public static List<System.Numerics.Vector2>[] BuildCells(IReadOnlyList<System.Numerics.Vector2> centers, float radius)
	{
		List<System.Numerics.Vector2>[] array = new List<System.Numerics.Vector2>[centers.Count];
		for (int i = 0; i < centers.Count; i++)
		{
			List<System.Numerics.Vector2> list = new List<System.Numerics.Vector2>();
			for (int j = 0; j < 128; j++)
			{
				double num = Math.PI * 2.0 * (double)j / 128.0;
				list.Add(new System.Numerics.Vector2((float)Math.Cos(num) * radius, (float)Math.Sin(num) * radius));
			}
			for (int k = 0; k < centers.Count; k++)
			{
				if (list.Count == 0)
				{
					break;
				}
				if (i != k)
				{
					System.Numerics.Vector2 normal = centers[k] - centers[i];
					if (!(normal.LengthSquared() < 1E-06f))
					{
						float distance = (centers[k].LengthSquared() - centers[i].LengthSquared()) * 0.5f;
						list = Clip(list, normal, distance);
					}
				}
			}
			array[i] = list;
		}
		return array;
	}

	private static List<System.Numerics.Vector2> Clip(List<System.Numerics.Vector2> source, System.Numerics.Vector2 normal, float distance)
	{
		List<System.Numerics.Vector2> list = new List<System.Numerics.Vector2>();
		System.Numerics.Vector2 value = source[source.Count - 1];
		float num = System.Numerics.Vector2.Dot(value, normal) - distance;
		foreach (System.Numerics.Vector2 item in source)
		{
			float num2 = System.Numerics.Vector2.Dot(item, normal) - distance;
			if (num <= 0f != num2 <= 0f)
			{
				list.Add(System.Numerics.Vector2.Lerp(value, item, num / (num - num2)));
			}
			if (num2 <= 0f)
			{
				list.Add(item);
			}
			value = item;
			num = num2;
		}
		int num3 = list.Count - 1;
		while (num3 >= 0 && list.Count > 1)
		{
			if (System.Numerics.Vector2.DistanceSquared(list[num3], list[(num3 + 1) % list.Count]) < 1E-08f)
			{
				list.RemoveAt(num3);
			}
			num3--;
		}
		return list;
	}

	public static int EdgeNeighbor(int sensor, System.Numerics.Vector2 a, System.Numerics.Vector2 b, IReadOnlyList<System.Numerics.Vector2> centers, float radius)
	{
		System.Numerics.Vector2 vector = b - a;
		if (vector.LengthSquared() < 1E-07f)
		{
			return -1;
		}
		System.Numerics.Vector2 value = (a + b) * 0.5f + System.Numerics.Vector2.Normalize(new System.Numerics.Vector2(vector.Y, 0f - vector.X)) * 0.003f;
		if (value.LengthSquared() >= radius * radius)
		{
			return -1;
		}
		int num = -1;
		float num2 = 3.4028235E+38f;
		for (int i = 0; i < centers.Count; i++)
		{
			float num3 = System.Numerics.Vector2.DistanceSquared(value, centers[i]);
			if (num3 < num2)
			{
				num2 = num3;
				num = i;
			}
		}
		if (num != sensor)
		{
			return num;
		}
		return -1;
	}

	public static List<System.Numerics.Vector2> ShrinkToCenter(List<System.Numerics.Vector2> cell, System.Numerics.Vector2 center, float progress)
	{
		if (progress >= 1f)
		{
			return cell;
		}
		if (progress <= 0f)
		{
			return new List<System.Numerics.Vector2>();
		}
		progress = progress * progress * (3f - 2f * progress);
		List<System.Numerics.Vector2> list = new List<System.Numerics.Vector2>(cell.Count);
		foreach (System.Numerics.Vector2 item in cell)
		{
			list.Add(center + (item - center) * progress);
		}
		return list;
	}

	public static List<System.Numerics.Vector2> GrowFromEdge(List<System.Numerics.Vector2> cell, System.Numerics.Vector2 a, System.Numerics.Vector2 b, float progress)
	{
		if (progress >= 1f)
		{
			return cell;
		}
		if (progress <= 0f || cell.Count < 3)
		{
			return new List<System.Numerics.Vector2>();
		}
		System.Numerics.Vector2 vector = b - a;
		System.Numerics.Vector2 vector2 = System.Numerics.Vector2.Normalize(new System.Numerics.Vector2(0f - vector.Y, vector.X));
		float num = 0f;
		foreach (System.Numerics.Vector2 item in cell)
		{
			num = Math.Max(num, System.Numerics.Vector2.Dot(item - a, vector2));
		}
		float num2 = progress * progress * (3f - 2f * progress);
		return Clip(cell, vector2, System.Numerics.Vector2.Dot(a, vector2) + num * num2);
	}

	public static bool IsUnionBoundary(int sensor, System.Numerics.Vector2 a, System.Numerics.Vector2 b, IReadOnlyList<System.Numerics.Vector2> centers, IReadOnlyList<bool> selected, float radius)
	{
		System.Numerics.Vector2 vector = (a + b) * 0.5f;
		System.Numerics.Vector2 vector2 = b - a;
		if (vector2.LengthSquared() < 1E-07f)
		{
			return false;
		}
		System.Numerics.Vector2 value = vector + System.Numerics.Vector2.Normalize(new System.Numerics.Vector2(vector2.Y, 0f - vector2.X)) * 0.003f;
		if (value.LengthSquared() >= radius * radius)
		{
			return true;
		}
		int num = sensor;
		float num2 = 3.4028235E+38f;
		for (int i = 0; i < centers.Count; i++)
		{
			float num3 = System.Numerics.Vector2.DistanceSquared(value, centers[i]);
			if (num3 < num2)
			{
				num2 = num3;
				num = i;
			}
		}
		if (num != sensor)
		{
			return !selected[num];
		}
		return true;
	}
}
public readonly struct NoiseZoneEvent
{
	public readonly int Sensor;

	public readonly double Start;

	public readonly double End;

	public readonly double WarningStart;

    public NoiseZoneEvent(int sensor, double start, double duration, double lead)
    { Sensor = sensor; Start = start; End = start + Math.Max(1.0 / 60.0, duration); WarningStart = start - Math.Max(0.001, lead); }
}
public sealed class NoiseZoneTimeline
{
	private readonly List<NoiseZoneEvent> events = new List<NoiseZoneEvent>();

	private readonly List<NoiseZoneEvent>[] spans = new List<NoiseZoneEvent>[33];

	public void Configure(IEnumerable<NoiseZoneEvent> source)
	{
		events.Clear();
		if (source != null)
		{
			events.AddRange(source);
		}
		events.Sort(delegate(NoiseZoneEvent a, NoiseZoneEvent b)
		{
			double warningStart = a.WarningStart;
			return warningStart.CompareTo(b.WarningStart);
		});
		for (int num = 0; num < spans.Length; num++)
		{
			List<NoiseZoneEvent>[] array = spans;
			int num2 = num;
			if (array[num2] == null)
			{
				array[num2] = new List<NoiseZoneEvent>();
			}
			spans[num].Clear();
			List<NoiseZoneEvent> list = new List<NoiseZoneEvent>();
			foreach (NoiseZoneEvent @event in events)
			{
				if (@event.Sensor == num)
				{
					list.Add(@event);
				}
			}
			list.Sort(delegate(NoiseZoneEvent a, NoiseZoneEvent b)
			{
				double start = a.Start;
				return start.CompareTo(b.Start);
			});
			foreach (NoiseZoneEvent item in list)
			{
				if (spans[num].Count == 0 || spans[num][spans[num].Count - 1].End < item.Start)
				{
					spans[num].Add(item);
					continue;
				}
				NoiseZoneEvent noiseZoneEvent = spans[num][spans[num].Count - 1];
				spans[num][spans[num].Count - 1] = new NoiseZoneEvent(num, noiseZoneEvent.Start, Math.Max(noiseZoneEvent.End, item.End) - noiseZoneEvent.Start, noiseZoneEvent.Start - noiseZoneEvent.WarningStart);
			}
		}
	}

	public bool IsBlocked(int sensor, double time)
	{
		foreach (NoiseZoneEvent @event in events)
		{
			if (@event.WarningStart > time)
			{
				break;
			}
			if (@event.Sensor == sensor && time >= @event.Start && time < @event.End)
			{
				return true;
			}
		}
		return false;
	}

	public void Evaluate(double time, float[] warnings, bool[] active)
	{
		Array.Clear(warnings, 0, warnings.Length);
		Array.Clear(active, 0, active.Length);
		foreach (NoiseZoneEvent @event in events)
		{
			if (@event.WarningStart > time)
			{
				break;
			}
			if (@event.Sensor >= 0 && @event.Sensor < active.Length && !(time >= @event.End))
			{
				if (time >= @event.Start)
				{
					active[@event.Sensor] = true;
					continue;
				}
				double num = (time - @event.WarningStart) / (@event.Start - @event.WarningStart);
				warnings[@event.Sensor] = Math.Max(warnings[@event.Sensor], (float)(1.0 - 0.7 * num));
			}
		}
	}

	public bool TryGetActiveSpan(int sensor, double time, out double start, out double end)
	{
		start = (end = 0.0);
		if (sensor < 0 || sensor >= spans.Length || spans[sensor] == null)
		{
			return false;
		}
		List<NoiseZoneEvent> list = spans[sensor];
		int num = 0;
		int num2 = list.Count - 1;
		int num3 = -1;
		while (num <= num2)
		{
			int num4 = (num + num2) / 2;
			if (list[num4].Start <= time)
			{
				num3 = num4;
				num = num4 + 1;
			}
			else
			{
				num2 = num4 - 1;
			}
		}
		if (num3 >= 0 && time < list[num3].End)
		{
			start = list[num3].Start;
			end = list[num3].End;
			return true;
		}
		return false;
	}

	public bool TryGetVisualSpan(int sensor, double time, out double start, out double end)
	{
		start = (end = 0.0);
		if (sensor < 0 || sensor >= spans.Length || spans[sensor] == null)
		{
			return false;
		}
		List<NoiseZoneEvent> list = spans[sensor];
		int num = 0;
		int num2 = list.Count - 1;
		int num3 = -1;
		while (num <= num2)
		{
			int num4 = (num + num2) / 2;
			if (list[num4].Start <= time)
			{
				num3 = num4;
				num = num4 + 1;
			}
			else
			{
				num2 = num4 - 1;
			}
		}
		if (num3 < 0)
		{
			return false;
		}
		NoiseZoneEvent noiseZoneEvent = list[num3];
		double num5 = Math.Max(noiseZoneEvent.End, noiseZoneEvent.Start + 0.1);
		if (time >= num5)
		{
			return false;
		}
		start = noiseZoneEvent.Start;
		end = num5;
		return true;
	}

	public float WarningShine(int sensor, double time)
	{
		if (IsBlocked(sensor, time))
		{
			return 0f;
		}
		foreach (NoiseZoneEvent @event in events)
		{
			if (@event.WarningStart > time)
			{
				break;
			}
			if (@event.Sensor == sensor && time >= Math.Max(@event.WarningStart, @event.Start - 0.5) && time < @event.Start)
			{
				return (float)(0.12 * (1.0 + 0.5 * Math.Sin(time * 37.9)));
			}
		}
		return 0f;
	}

	public static int SensorIndex(char area, int position)
	{
		switch (char.ToUpperInvariant(area))
		{
		case 'A':
			if (position >= 1 && position <= 8)
			{
				return position - 1;
			}
			break;
		case 'B':
			if (position >= 1 && position <= 8)
			{
				return position + 7;
			}
			break;
		case 'C':
			return 16;
		case 'D':
			if (position >= 1 && position <= 8)
			{
				return position + 16;
			}
			break;
		case 'E':
			if (position >= 1 && position <= 8)
			{
				return position + 24;
			}
			break;
		}
		return -1;
	}
}
}
