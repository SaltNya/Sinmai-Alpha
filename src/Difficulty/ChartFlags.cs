using System;
using System.Collections.Generic;
using System.Linq;

namespace SinmaiAlpha.Assets;

// Shared by the mod and MCM. A bare legacy theme and empty Strong markers remain valid.
public sealed class ChartFlags
{
    public string Theme = "None";
    public bool AllowTapInHold;
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
}
