using System;
using System.Collections.Generic;
using System.IO;

namespace SinmaiAlpha.Artwork
{
    // One index per mount. Resolve the entire request (including thumbnail
    // fallback) in a higher-priority mount before trying a lower-priority one.
    internal sealed class LooseAssetIndex
    {
        internal static readonly string[] Extensions =
            { ".gif", ".mp4", ".webm", ".mov", ".m4v", ".png", ".jpg", ".jpeg" };
        private readonly Dictionary<string, List<string>> files =
            new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        internal Func<string, bool> Filter;

        internal void AddDirectory(string directory)
        {
            if (!Directory.Exists(directory)) return;
            var paths = Directory.GetFiles(directory, "*", SearchOption.AllDirectories);
            Array.Sort(paths, StringComparer.Ordinal);
            foreach (string path in paths)
            {
                if (ExtensionPriority(path) < 0) continue;
                string relative = path.Substring(directory.TrimEnd('/', '\\').Length).TrimStart('/', '\\');
                Add(WithoutExtension(Normalize(relative)), path);
                string stem = WithoutExtension(FileName(relative));
                if (Normalize(relative).IndexOf('/') < 0 || !long.TryParse(stem, out _)) Add(stem, path);
            }
        }

        private void Add(string key, string path)
        {
            if (!files.TryGetValue(key, out var candidates)) files[key] = candidates = new List<string>();
            if (!candidates.Contains(path)) candidates.Add(path);
            candidates.Sort((a, b) => {
                int priority = ExtensionPriority(a).CompareTo(ExtensionPriority(b));
                return priority != 0 ? priority : StringComparer.Ordinal.Compare(a, b);
            });
        }

        internal bool TryResolve(string request, bool animated, out string path)
        {
            // A full-size animation also replaces an old static thumbnail in the
            // same pack; an explicit animated _S file still wins.
            if (animated && TryResolveCore(request, 2, out path)) return true;
            return TryResolveCore(request, 0, out path);
        }

        private bool TryResolveCore(string request, int mediaKind, out string path)
        {
            path = null;
            if (string.IsNullOrEmpty(request)) return false;
            string key = WithoutExtension(Normalize(request));
            string stem = FileName(key);
            if (Find(key, mediaKind, out path) || Find(stem, mediaKind, out path)) return true;
            bool jacket = stem.StartsWith("ui_jacket_", StringComparison.OrdinalIgnoreCase);
            bool frame = stem.StartsWith("ui_frame_", StringComparison.OrdinalIgnoreCase);
            bool icon = stem.StartsWith("ui_icon_", StringComparison.OrdinalIgnoreCase);
            bool plate = stem.StartsWith("ui_plate_", StringComparison.OrdinalIgnoreCase);
            if (!jacket && !frame && !icon && !plate) return false;
            string fullStem = stem.EndsWith("_s", StringComparison.OrdinalIgnoreCase)
                ? stem.Substring(0, stem.Length - 2) : stem;
            string category = jacket ? "jacket" : frame ? "frame" : icon ? "icon" : "nameplate";
            if (Find(category + "/" + fullStem, mediaKind, out path) || Find(fullStem, mediaKind, out path)) return true;
            string id = fullStem.Substring(jacket ? 10 : icon ? 8 : 9);
            // Bare numeric files retain AquaMai's jacket meaning. Other collection IDs must
            // be scoped so the same number cannot accidentally replace a jacket.
            return Find(category + "/" + id, mediaKind, out path) ||
                (jacket && Find(id, mediaKind, out path));
        }

        private bool Find(string key, int mediaKind, out string path)
        {
            path = null;
            if (!files.TryGetValue(key, out var candidates)) return false;
            foreach (string candidate in candidates)
                if ((Filter == null || Filter(candidate)) && (mediaKind == 2 ? IsAnimated(candidate) : IsStill(candidate)) && File.Exists(candidate)) { path = candidate; return true; }
            return false;
        }

        internal static bool IsStill(string path) { return ExtensionPriority(path) >= 5; }
        internal static bool IsAnimated(string path) { int p = ExtensionPriority(path); return p >= 0 && p < 5; }
        internal static int ExtensionPriority(string path)
        {
            string extension = Path.GetExtension(path);
            for (int i = 0; i < Extensions.Length; i++)
                if (string.Equals(extension, Extensions[i], StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
        private static string Normalize(string path) { return path.Replace('\\', '/').TrimStart('/'); }
        private static string FileName(string path) { path = Normalize(path); return path.Substring(path.LastIndexOf('/') + 1); }
        private static string WithoutExtension(string path) { int dot = path.LastIndexOf('.'); return dot > path.LastIndexOf('/') ? path.Substring(0, dot) : path; }
    }
}
