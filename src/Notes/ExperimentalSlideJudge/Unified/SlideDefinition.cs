using SinmaiAlpha.Notes.Libs;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text.RegularExpressions;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge.Unified;

/// <summary>
/// Geometry and capabilities, independent of MA2 record names. Legacy spellings
/// are decoded here once; table generation and gameplay never inspect them.
/// </summary>
public sealed class SlideDefinition
{
    public IReadOnlyList<ParametricSlidePath> Paths { get; }
    public SlideJudgeProfile JudgeProfile { get; }
    public bool TouchSamples { get; }
    public bool TouchAppearance { get; }
    public bool IsFan => Paths.Count == 3;
    public int MainLane => Paths.Count / 2;
    internal int FanEndKey { get; }
    internal bool FanEndD { get; }

    private SlideDefinition(ParametricSlidePath[] paths, SlideJudgeProfile profile,
        bool touchSamples = false, bool touchAppearance = false, int fanEndKey = -1, bool fanEndD = false)
    {
        foreach (var path in paths)
            if (path == null) throw new FormatException("Slide geometry could not be parsed.");
        Paths = paths;
        JudgeProfile = profile;
        TouchSamples = touchSamples;
        TouchAppearance = touchAppearance;
        FanEndKey = fanEndKey;
        FanEndD = fanEndD;
    }

    public static SlideDefinition Parse(string code)
    {
        if (string.IsNullOrEmpty(code)) throw new FormatException("Slide code is empty.");
        if (code.StartsWith("MV1:", StringComparison.Ordinal) || code.StartsWith("MV2:", StringComparison.Ordinal) ||
            code.StartsWith("MD1:", StringComparison.Ordinal) || code.StartsWith("MD2:", StringComparison.Ordinal))
            throw new FormatException("Retired preview-slide format; reconvert with the original SC converter.");

        if (code.StartsWith("DF1:", StringComparison.Ordinal)) return ParseFan(code.Substring(4));
        if (code.StartsWith("TG1:", StringComparison.Ordinal))
            return new SlideDefinition(new[] { NativeTouchSlideGeometry.Build(code.Substring(4)) },
                SlideJudgeProfile.ExtendedTouch, touchSamples: true, touchAppearance: HasTouchNodes(code));
        if (code.StartsWith("DG1:", StringComparison.Ordinal))
            return new SlideDefinition(new[] { NativeDSlideGeometry.Build(code.Substring(4)) }, SlideJudgeProfile.ExtendedTouch);
        if (code.StartsWith("DG2:", StringComparison.Ordinal))
            return new SlideDefinition(new[] { SlideCodeParser.Parse(code.Substring(4)) },
                SlideJudgeProfile.ExtendedTouch, touchAppearance: true);

        // Passing through B/C or near D/E is not an opt-in. Preserve explicit
        // touch starts/nodes while keeping arbitrary ordinary SC in A/B/C.
        var extended = code[0] == 'B' || code[0] == 'C' ||
            code.IndexOf('D') >= 0 || code.IndexOf('E') >= 0 || code.IndexOf('F') >= 0;
        return new SlideDefinition(new[] { SlideCodeParser.Parse(code) },
            extended ? SlideJudgeProfile.ExtendedTouch : SlideJudgeProfile.StandardAbc,
            touchAppearance: HasTouchNodes(code));
    }

    private static bool HasTouchNodes(string code) => code.IndexOf('B') >= 0 || code.IndexOf('C') >= 0 ||
        code.IndexOf('D') >= 0 || code.IndexOf('E') >= 0 || code.IndexOf('F') >= 0;

    private static SlideDefinition ParseFan(string expression)
    {
        var match = Regex.Match(expression, @"^(?<start>[1-8])(?<sd>d?)w(?<end>[1-8])(?<ed>d?)$");
        if (!match.Success) throw new FormatException("Invalid wifi: " + expression);
        var start = match.Groups["start"].Value[0] - '0';
        var end = match.Groups["end"].Value[0] - '0';
        var startD = match.Groups["sd"].Value.Length != 0;
        var endD = match.Groups["ed"].Value.Length != 0;
        if (((end - start) & 7) != 4) throw new FormatException("Wifi must end opposite its start.");
        Complex Position(int key, bool d) => Complex.FromPolarCoordinates(MaiGeometry.MainRadius,
            Math.PI * (5.0 / 8.0 - (key - (d ? .5 : 0)) / 4.0));
        var paths = new ParametricSlidePath[3];
        for (var lane = 0; lane < paths.Length; lane++)
            paths[lane] = new ParametricSlidePath(new[] { new ParametricSlidePath.LineSegment(
                Position(start, startD), Position((end + lane + 6) % 8 + 1, endD)) });
        return new SlideDefinition(paths, startD || endD ? SlideJudgeProfile.ExtendedTouch : SlideJudgeProfile.StandardAbc,
            fanEndKey: end - 1, fanEndD: endD);
    }
}
