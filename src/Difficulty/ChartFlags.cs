using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;

namespace SinmaiAlpha.Assets;

// Shared by the mod and MCM. A bare legacy theme and empty Strong markers remain valid.
public sealed class ChartFlags
{
    public string Theme = "None";
    public bool AllowTapInHold;
    public bool CustomLevelText;
    public string LevelText;
    public bool HasTapInHoldOverride;
    public bool? TapInHoldOverride => HasTapInHoldOverride ? AllowTapInHold : (bool?)null;
    private readonly List<string> options = new List<string>();
    public static ChartFlags Parse(string text)
    {
        var flags = new ChartFlags();
        if (string.IsNullOrWhiteSpace((text ?? "").Trim('\uFEFF'))) { flags.Theme = "Strong"; return flags; }
        foreach (var raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim().TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("//")) { flags.options.Add(raw); continue; }
            var pair = line.Split(new[] { '=' }, 2);
            if (pair[0].Trim().Equals("AllowTapInHold", StringComparison.OrdinalIgnoreCase))
            {
                flags.HasTapInHoldOverride = true;
                flags.AllowTapInHold = pair.Length == 1 || pair[1].Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
                flags.options.Add(line);
            }
            else if (pair[0].Trim().Equals("CustomLevelText", StringComparison.OrdinalIgnoreCase))
            { flags.CustomLevelText = pair.Length == 2 && bool.TryParse(pair[1].Trim(), out var enabled) && enabled; flags.options.Add(raw); }
            else if (pair[0].Trim().Equals("LevelText", StringComparison.OrdinalIgnoreCase))
            { flags.LevelText = pair.Length == 2 ? pair[1].Trim() : null; flags.options.Add(raw); }
            else if (pair[0].Trim().Equals("Difficulty", StringComparison.OrdinalIgnoreCase) && pair.Length == 2) flags.Theme = pair[1].Trim();
            else if (pair.Length == 1) flags.Theme = line;
            else flags.options.Add(raw);
        }
        return flags;
    }
    public string WithTapInHold(bool? allowed)
    {
        var lines = options.Where(line => !line.Trim().Split(new[] { '=' }, 2)[0].Trim().Equals("AllowTapInHold", StringComparison.OrdinalIgnoreCase)).ToList();
        if (allowed.HasValue) lines.Add("AllowTapInHold=" + (allowed.Value ? "true" : "false"));
        return Theme + Environment.NewLine + (lines.Count == 0 ? "" : string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }
    public string WithTheme(string theme) => theme + Environment.NewLine + (options.Count == 0 ? "" : string.Join(Environment.NewLine, options) + Environment.NewLine);

    public static ChartFlags FromXml(XmlNode node)
    {
        var flags = new ChartFlags();
        flags.Theme = node.SelectSingleNode("Difficulty")?.InnerText.Trim() ?? "None";
        if (flags.Theme.Length == 0) flags.Theme = "None";
        if (bool.TryParse(node.SelectSingleNode("AllowTapInHold")?.InnerText.Trim(), out var allowed))
        { flags.HasTapInHoldOverride = true; flags.AllowTapInHold = allowed; }
        flags.CustomLevelText = bool.TryParse(node.SelectSingleNode("CustomLevelText")?.InnerText.Trim(), out var custom) && custom;
        flags.LevelText = node.SelectSingleNode("LevelText")?.InnerText;
        var legacy = node.SelectSingleNode("LegacyOptions")?.InnerText;
        if (!string.IsNullOrEmpty(legacy)) flags.options.AddRange(legacy.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries));
        return flags;
    }

    public void WriteXml(XmlElement node)
    {
        SetXml(node, "Difficulty", string.IsNullOrWhiteSpace(Theme) ? "None" : Theme);
        SetXml(node, "AllowTapInHold", HasTapInHoldOverride ? (AllowTapInHold ? "true" : "false") : null);
        SetXml(node, "CustomLevelText", CustomLevelText ? "true" : "false");
        SetXml(node, "LevelText", LevelText);
        var unknown = options.Where(line => !new[] { "Difficulty", "AllowTapInHold", "CustomLevelText", "LevelText" }
            .Contains(line.Trim().Split(new[] { '=' }, 2)[0].Trim(), StringComparer.OrdinalIgnoreCase)).ToArray();
        SetXml(node, "LegacyOptions", unknown.Length == 0 ? null : string.Join(Environment.NewLine, unknown));
    }

    private static void SetXml(XmlElement parent, string name, string value)
    {
        var node = parent.SelectSingleNode(name);
        if (value == null) { if (node != null) parent.RemoveChild(node); return; }
        if (node == null) node = parent.AppendChild(parent.OwnerDocument.CreateElement(name));
        node.InnerText = value;
    }
}
