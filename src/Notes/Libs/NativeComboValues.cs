using System;
using Manager;

namespace SinmaiAlpha.Notes.Libs;

// Read-only display data. The caller supplies the native maximum base score
// for the current session (including both sides in a co-op session). No
// preview note counter, score setter, result callback or judge method is used.
public sealed class NativeComboValues
{
    public uint Combo, DeluxeScore;
    public long Score, NormalizedScore, NormalizedDownScore;
    public decimal AchievementClassic, AchievementDownClassic, AchievementDeluxe, AchievementDownDeluxe;

    public static NativeComboValues Capture(GameScoreList score, uint maximumBaseScore)
        => CaptureWithPartner(score, maximumBaseScore, null);

    public static NativeComboValues CaptureSession(GameScoreList score, GameScoreList partner)
    {
        if (score == null || !score.IsEnable) return new NativeComboValues();
        var maximum = score.ScoreTotal._allPerfectScore;
        if (!score.SessionInfo.isUtageCoop || partner == null || !partner.IsEnable) partner = null;
        else maximum += partner.ScoreTotal._allPerfectScore;
        return CaptureWithPartner(score, maximum, partner, score.SessionInfo.isUtageCoop ? 200 : 100);
    }
    private static NativeComboValues CaptureWithPartner(GameScoreList score, uint maximumBaseScore, GameScoreList partner, float fullRate = 100)
    {
        var value = new NativeComboValues();
        if (score == null || !score.IsEnable) return value;
        value.Combo = score.Combo;
        value.DeluxeScore = score.GetDeluxeScoreAll();
        value.Score = score.TotalScore;
        value.AchievementDeluxe = score.GetAchivement();
        value.AchievementDownDeluxe = score.GetDecAchivement();
        decimal loss = 0, bonus = 0;
        ReadLoss(score, ref loss, ref bonus);
        if (partner != null) ReadLoss(partner, ref loss, ref bonus);
        if (maximumBaseScore != 0)
        {
            // Keep actual native Break awards. Classic display percentages
            // use the base-score denominator and do not rewrite game results.
            value.AchievementClassic = value.Score * 100m / maximumBaseScore;
            value.AchievementDownClassic = (maximumBaseScore - loss + bonus) * 100m / maximumBaseScore;
            value.NormalizedScore = Normalize(maximumBaseScore, value.AchievementDeluxe, fullRate);
            value.NormalizedDownScore = Normalize(maximumBaseScore, value.AchievementDownDeluxe, fullRate);
        }
        return value;
    }
    private static void ReadLoss(GameScoreList score, ref decimal loss, ref decimal bonus)
    {
        for (var index = 0; index < score.GetScoreLength(); index++)
        {
            var result = score.GetScoreAt(index);
            if (!result.Judged || result.Type == NoteScore.EScoreType.End) continue;
            loss += (decimal)result.TheoryScore - result.Score;
            bonus += result.ScoreBonus;
        }
    }
    private static long Normalize(uint maximum, decimal achievement, float fullRate) =>
        (long)Math.Round((float)maximum * ((float)achievement / fullRate) / 5f) * 5;

    public string Format(int mode)
    {
        switch (mode)
        {
            case 2: return string.Format("{0:#,##0}", Score);
            case 3: return string.Format("{0,6:0.00}%", AchievementClassic);
            case 4: return string.Format("{0,6:0.00}%", AchievementDownClassic);
            case 11: return string.Format("{0,8:0.0000}%", AchievementDeluxe);
            case 12: return string.Format("{0,8:0.0000}%", AchievementDownDeluxe);
            case 13: return DeluxeScore.ToString();
            case 101: return string.Format("{0:#,##0}", NormalizedScore);
            case 102: return string.Format("{0:#,##0}", NormalizedDownScore);
            default: return Combo > 0 ? Combo.ToString() : "";
        }
    }
    public decimal Rate(int mode)
    {
        switch (mode)
        {
            case 3: return AchievementClassic;
            case 4: return AchievementDownClassic;
            case 11: return AchievementDeluxe;
            case 12: return AchievementDownDeluxe;
            default: return 0;
        }
    }
}
