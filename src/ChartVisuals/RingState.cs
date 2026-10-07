using System;
using System.Collections.Generic;
using System.Globalization;

namespace SinmaiAlpha.ChartVisuals
{
    public sealed class RingChange
    {
        public double Time;
        public string Kind = "", Type = "";
        public float? Value; // NULL clears this category, allowing the next priority.
    }
    public readonly struct RingValue
    {
        public readonly float Spawn, Destroy;
        public readonly bool Once;
        public RingValue(float spawn, float destroy, bool once)
        { Spawn = spawn; Destroy = destroy; Once = once; }
    }
    // Shared command grammar/state only; no preview lifecycle or gameplay code.
    public static class RingState
    {
        public static bool IsKind(string kind) => kind == "spawn" || kind == "spawnmode" || kind == "destroy";
        public static bool IsType(string type) => type == "tap" || type == "star" || type == "hold" ||
            type == "mine" || type == "break" || type == "each";
        public static bool IsStream(string type)
        {
            if (type.Length < 2 || type[0] != 's') return false;
            for (var i = 1; i < type.Length; i++) if (type[i] < '0' || type[i] > '9') return false;
            return true;
        }
        public static bool TryParse(string kind, string text, double time, out List<RingChange> changes, bool allowStreams = false)
        {
            changes = new List<RingChange>();
            if (!IsKind(kind) || string.IsNullOrWhiteSpace(text)) return false;
            if (!text.Contains("=") && text.Contains(",")) return false;
            var parsed = new List<RingChange>();
            foreach (var part in text.Split(','))
            {
                var equal = part.IndexOf('=');
                var type = equal < 0 ? "" : part.Substring(0, equal).Trim().ToLowerInvariant();
                var value = (equal < 0 ? part : part.Substring(equal + 1)).Trim();
                if (equal >= 0 && (!IsType(type) && !(allowStreams && IsStream(type)))) return false;
                if (equal < 0 && text.Contains("=")) return false;
                if (kind == "spawnmode" && value.Length >= 2 && value[0] == '(' && value[value.Length - 1] == ')')
                    value = value.Substring(1, value.Length - 2).Trim();
                float? number;
                if (value.Equals("NULL", StringComparison.OrdinalIgnoreCase)) number = null;
                else if (kind == "spawnmode")
                {
                    if (value.Equals("Once", StringComparison.OrdinalIgnoreCase)) number = 1;
                    else if (value.Equals("Rewind", StringComparison.OrdinalIgnoreCase)) number = 0;
                    else return false;
                }
                else
                {
                    if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var radius) ||
                        float.IsNaN(radius) || float.IsInfinity(radius) ||
                        Math.Abs(radius) > (kind == "spawn" ? 4.8f : 20f)) return false;
                    number = radius;
                }
                parsed.Add(new RingChange { Time = time, Kind = kind, Type = type, Value = number });
            }
            changes = parsed; // A malformed pair discards the entire command.
            return true;
        }
        public static RingValue Resolve(IReadOnlyList<RingChange> changes, double time, VisualNote note, string stream = "")
        {
            if (!IsType(note.Family)) return new RingValue(1.225f, 4.8f, false);
            float? Lookup(string kind, string type)
            {
                float? value = null;
                foreach (var change in changes)
                {
                    if (change.Time > time) break; // stable time/authored order
                    if (change.Kind == kind && change.Type == type) value = change.Value;
                }
                return value;
            }
            float Get(string kind, float fallback)
            {
                if (stream.Length != 0) return Lookup(kind, stream) ?? fallback;
                if (note.Mine && Lookup(kind, "mine") is float mine) return mine;
                if (note.Break && Lookup(kind, "break") is float br) return br;
                if (note.Each && Lookup(kind, "each") is float each) return each;
                return Lookup(kind, note.Family) ?? Lookup(kind, "") ?? fallback;
            }
            return new RingValue(Get("spawn", 1.225f), Get("destroy", 4.8f), Get("spawnmode", 0) == 1);
        }
    }
}
