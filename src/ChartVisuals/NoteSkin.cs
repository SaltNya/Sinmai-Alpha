using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
#nullable disable

namespace SinmaiAlpha.ChartVisuals;

public static class NoteSkin
{
    public static bool LooksLikePath(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        var dot = body.LastIndexOf('.');
        if (dot <= 0 || dot >= body.Length - 1) return false;
        for (var i = dot + 1; i < body.Length; i++) if (!char.IsLetter(body[i])) return false;
        return true;
    }
    public static bool Normalize(string body, out string path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(body)) return false;
        var cleaned = body.Trim().Replace('\\', '/');
        if (cleaned.StartsWith("/", StringComparison.Ordinal) || cleaned.Contains(":") || cleaned.Contains("//") ||
            !new[] { ".png", ".jpg", ".jpeg" }.Any(e => cleaned.EndsWith(e, StringComparison.OrdinalIgnoreCase)) ||
            cleaned.Split('/').Any(p => p.Length == 0 || p == "." || p == "..")) return false;
        path = cleaned;
        return true;
    }
    public static string Encode(string path)
    {
        if (!Normalize(path, out var normalized)) throw new ArgumentException("Invalid note skin path", nameof(path));
        return "SK1|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized));
    }
    public static bool Decode(string marker, out string path)
    {
        path = null;
        if (marker == null || !marker.StartsWith("SK1|", StringComparison.Ordinal)) return false;
        string decoded;
        try { decoded = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(marker.Substring(4))); }
        catch (Exception e) when (e is FormatException || e is DecoderFallbackException) { return false; }
        return Normalize(decoded, out path) && decoded == path;
    }
    public static string FormatExpression(string path) => "~[" + path.Replace('/', '\\') + "]";
    public static string ResolvePath(string root, string path)
    {
        try
        {
            if (string.IsNullOrEmpty(root) || !Normalize(path, out var relative)) return null;
            var folder = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(folder, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch { return null; }
    }
    public static IEnumerable<string> ReferencedPaths(string ma2)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in ma2.Split('\n'))
        {
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length < 5 || !(fields[0].EndsWith("TAP", StringComparison.Ordinal) || fields[0].EndsWith("STR", StringComparison.Ordinal) ||
                fields[0].EndsWith("HLD", StringComparison.Ordinal) || fields[0].EndsWith("TTP", StringComparison.Ordinal) ||
                fields[0].EndsWith("STP", StringComparison.Ordinal) || fields[0].EndsWith("THO", StringComparison.Ordinal))) continue;
            foreach (var marker in fields)
                if (Decode(marker, out var path) && found.Add(path)) yield return path;
        }
    }
    public static void CopyAssets(string ma2, string sourceRoot, string targetRoot, Action<string> warning)
    {
        foreach (var path in ReferencedPaths(ma2))
        {
            var source = ResolvePath(sourceRoot, path); var target = ResolvePath(targetRoot, path);
            if (source == null || target == null || !File.Exists(source)) { warning?.Invoke("Missing note skin: " + path); continue; }
            if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            File.Copy(source, target, true);
        }
    }
}
