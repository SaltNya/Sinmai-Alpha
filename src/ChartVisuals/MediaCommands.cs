#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SinmaiAlpha.ChartVisuals
{
    // Names/units match the reference media controller. Time is music seconds,
    // independent of each player's note-judgment adjustment.
    public sealed class MediaChange
    {
        public double time;
        public string kind;
        public bool enabled;
        public string path = "";
        public float transition;
        public int track;
        public double sourceOffset, duration;
        public bool timelineClip;
        public string Encode()
        {
            if (timelineClip || track != 0 || sourceOffset != 0 || duration != 0)
                return MediaCommands.EncodeTimeline(this);
            return "MEDIA1|" + (enabled ? "1" : "0") + "|" +
                transition.ToString("R", CultureInfo.InvariantCulture) + "|" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
        }
    }
    public static class MediaCommands
    {
        public static bool IsKind(string kind) => kind == "audio" || kind == "pvoverlay";
        public static string NormalizePath(string kind, string text)
        {
            if (!IsKind(kind) || text == null) return null;
            var path = text.Trim().Trim('"').Replace('\\', '/');
            if (path.Length == 0 || path[0] == '/' || path.IndexOf(':') >= 0 || path.Split('/').Any(p => p == "..")) return null;
            var extensions = kind == "audio" ? new[] { ".ogg", ".wav", ".mp3" } : new[] { ".png", ".jpg", ".jpeg", ".mp4" };
            return extensions.Any(e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ? path : null;
        }
        public static bool TryParse(string kind, string text, float bpm, out MediaChange change)
        {
            change = null;
            if (!IsKind(kind) || text == null || text.Length < 3 || text[0] != '(' || text[text.Length - 1] != ')') return false;
            var parts = text.Substring(1, text.Length - 2).Split(',').Select(p => p.Trim()).ToArray();
            if (!bool.TryParse(parts[0], out var enabled)) return false;
            var audio = kind == "audio";
            var expected = enabled ? 2 : 1;
            if (parts.Length < expected || parts.Length > expected + (audio ? 0 : 1)) return false;
            var path = enabled ? NormalizePath(kind, parts[1]) : "";
            if (path == null) return false;
            float transition = 0;
            if (parts.Length > expected && (!PresentationCommands.Duration(parts[expected], bpm, out transition) || transition < 0)) return false;
            change = new MediaChange { kind = audio ? "audio" : "pvOverlay", enabled = enabled, path = path, transition = transition };
            return true;
        }
        public static bool Decode(string kind, string text, double seconds, out MediaChange change)
        {
            change = null;
            if (!IsKind(kind) || text == null || double.IsNaN(seconds) || double.IsInfinity(seconds)) return false;
            var parts = text.Split('|');
            if (parts.Length == 8 && parts[0] == "MEDIA2")
                return DecodeTimeline(kind, parts, seconds, out change);
            if (parts.Length != 4 || parts[0] != "MEDIA1" || parts[1] != "0" && parts[1] != "1" ||
                !VisualValue.Number(parts[2], out var transition) || transition < 0 || kind == "audio" && transition != 0) return false;
            string path;
            try { path = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(parts[3])); }
            catch (Exception e) when (e is FormatException || e is DecoderFallbackException) { return false; }
            var enabled = parts[1] == "1";
            if (enabled ? NormalizePath(kind, path) != path : path.Length != 0) return false;
            change = new MediaChange { kind = kind == "audio" ? "audio" : "pvOverlay", time = seconds, enabled = enabled, path = path, transition = transition };
            return true;
        }
        public static string EncodeTimeline(MediaChange change)
        {
            if (change == null || !IsKind(change.kind?.ToLowerInvariant()) ||
                change.path == null || change.track < 0 || change.track > 1 || !Nonnegative(change.sourceOffset) ||
                !Nonnegative(change.duration) || !Nonnegative(change.transition) ||
                change.kind.ToLowerInvariant() == "audio" && change.transition != 0 ||
                (change.enabled ? NormalizePath(change.kind.ToLowerInvariant(), change.path) != change.path : change.path != ""))
                throw new InvalidDataException("Invalid media timeline event");
            return "MEDIA2|" + (change.enabled ? "1" : "0") + "|" +
                change.transition.ToString("R", CultureInfo.InvariantCulture) + "|" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(change.path)) + "|" +
                change.track.ToString(CultureInfo.InvariantCulture) + "|" +
                change.sourceOffset.ToString("R", CultureInfo.InvariantCulture) + "|" +
                change.duration.ToString("R", CultureInfo.InvariantCulture) + "|" + (change.timelineClip ? "1" : "0");
        }
        private static bool DecodeTimeline(string kind, string[] parts, double seconds, out MediaChange change)
        {
            change = null;
            if (parts[1] != "0" && parts[1] != "1" || parts[7] != "0" && parts[7] != "1" ||
                !VisualValue.Number(parts[2], out var transition) || transition < 0 || kind == "audio" && transition != 0 ||
                !int.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var track) || track < 0 || track > 1 ||
                !Seconds(parts[5], out var offset) || !Seconds(parts[6], out var duration)) return false;
            string path;
            try { path = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(parts[3])); }
            catch (Exception e) when (e is FormatException || e is DecoderFallbackException) { return false; }
            var enabled = parts[1] == "1";
            if (enabled ? NormalizePath(kind, path) != path : path.Length != 0) return false;
            change = new MediaChange
            {
                kind = kind == "audio" ? "audio" : "pvOverlay", time = seconds, enabled = enabled,
                path = path, transition = transition, track = track, sourceOffset = offset,
                duration = duration, timelineClip = parts[7] == "1"
            };
            return true;
        }
        private static bool Nonnegative(double value) => !double.IsNaN(value) && !double.IsInfinity(value) && value >= 0;
        private static bool Seconds(string text, out double value)
            => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && Nonnegative(value);
        public static string ResolvePath(string root, string relative)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relative)) return null;
                var basePath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                var fullPath = Path.GetFullPath(Path.Combine(basePath, relative.Replace('/', Path.DirectorySeparatorChar)));
                return fullPath.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ? fullPath : null;
            }
            catch { return null; }
        }
        public static IEnumerable<string> ReferencedPaths(string ma2)
        {
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in ma2.Split('\n'))
            {
                var fields = line.TrimEnd('\r').Split('\t');
                if (fields.Length == 4 && Decode(fields[0].ToLowerInvariant(), fields[3], 0, out var item) && item.enabled && found.Add(item.path))
                    yield return item.path;
            }
        }
        public static void CopyAssets(string ma2, string sourceRoot, string targetRoot, Action<string> warning)
        {
            foreach (var path in ReferencedPaths(ma2))
            {
                var source = ResolvePath(sourceRoot, path); var target = ResolvePath(targetRoot, path);
                if (source == null || target == null || !File.Exists(source)) { warning?.Invoke("Missing chart media: " + path); continue; }
                if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target, true);
            }
        }
    }
}
