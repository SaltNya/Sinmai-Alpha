// Isolated Mine queue: mirrors Sinmai 1.66 cue selection, priorities and per-note volume settings.
// Derived from the locally supplied GameSingleCueCtrl; MineAudio owns separate players.
using DB;
using MAI2.Util;
using Mai2.Mai2Cue;
using Manager;

namespace SinmaiAlpha.Notes;

internal sealed class MineCueController
{
	private readonly OptionVolumeID[] SeVolume = new OptionVolumeID[2];

	private readonly OptionVolumeAnswerSoundID[] AnsVolume = new OptionVolumeAnswerSoundID[2];

	private readonly OptionVolumeID[] BreakVolume = new OptionVolumeID[2];

	private readonly OptionVolumeID[] SlideVolume = new OptionVolumeID[2];

	private readonly OptionVolumeID[] BreakSlideVolume = new OptionVolumeID[2];

	private readonly OptionVolumeID[] ExVolume = new OptionVolumeID[2];

	private readonly OptionVolumeID[] TouchVolume = new OptionVolumeID[2];

	private readonly OptionVolumeID[] TouchHoldVolume = new OptionVolumeID[2];

	private readonly bool[] AnsSE = new bool[2];

	private readonly bool[] SlideTouchSE = new bool[2];

	private readonly bool[] BreakSlideTouchSE = new bool[2];

	private readonly NoteJudge.JudgeBox[] BreakSlideSE = new NoteJudge.JudgeBox[2];

	private readonly NoteJudge.JudgeBox[] ExSE = new NoteJudge.JudgeBox[2];

	private readonly NoteJudge.JudgeBox[] CenterEffectSE = new NoteJudge.JudgeBox[2];

	private readonly NoteJudge.JudgeBox[] JudgeTapSE = new NoteJudge.JudgeBox[2];

	private readonly NoteJudge.JudgeBox[] JudgeTouchSE = new NoteJudge.JudgeBox[2];

	private readonly NoteJudge.JudgeBox[] JudgeBreakSE = new NoteJudge.JudgeBox[2];

	private readonly NoteJudge.JudgeBox[] JudgeTouchHoldLoopSE = new NoteJudge.JudgeBox[2];

	private readonly Cue[] SlideSe = new Cue[2];

	private readonly Cue[] BreakGoodSe = new Cue[2];

	private readonly Cue[] BreakBadSe = new Cue[2];

	private readonly Cue[] ExSe = new Cue[2];

	private readonly Cue[] TapCriticalSe = new Cue[2];

	private readonly Cue[] TapPerfectSe = new Cue[2];

	private readonly Cue[] TapGreatSe = new Cue[2];

	private readonly Cue[] TapGoodSe = new Cue[2];

	private readonly bool[] DisableTouchHoldLoop = new bool[2];

	private readonly bool[] StopTouchSe = new bool[2];

	public void Initialize(int index)
	{
		SeVolume[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.TapHoldVolume;
		BreakVolume[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.BreakVolume;
		SlideVolume[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.SlideVolume;
		BreakSlideVolume[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.BreakSlideVolume;
		ExVolume[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.ExVolume;
		AnsVolume[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.AnsVolume;
		TouchVolume[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.TouchVolume;
		TouchHoldVolume[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.TouchHoldVolume;
		SlideSe[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.SlideSe.GetSlideCue();
		BreakGoodSe[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.BreakSe.GetBreakGoodCue();
		BreakBadSe[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.BreakSe.GetBreakBadCue();
		ExSe[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.ExSe.GetExCue();
		TapCriticalSe[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.TapSe.GetTapCriticalCue();
		TapPerfectSe[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.TapSe.GetTapPerfectCue();
		TapGreatSe[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.TapSe.GetTapGreatCue();
		TapGoodSe[index] = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.TapSe.GetTapGoodCue();
		DisableTouchHoldLoop[index] = true;
		StopTouchSe[index] = false;
		ClearCue(index);
	}

	public void ReserveAnswerSe(int index)
	{
		AnsSE[index] = true;
	}

	public void ReserveTouchJudgeSe(int index, NoteJudge.JudgeBox judge)
	{
		if (JudgeTouchSE[index] == NoteJudge.JudgeBox.End || JudgeTouchSE[index] < judge)
		{
			JudgeTouchSE[index] = judge;
		}
	}

	public void ReserveTapJudgeSe(int index, NoteJudge.JudgeBox judge)
	{
		if (JudgeTapSE[index] == NoteJudge.JudgeBox.End || JudgeTapSE[index] < judge)
		{
			JudgeTapSE[index] = judge;
		}
	}

	public void ReserveBreakJudgeSe(int index, NoteJudge.JudgeBox judge)
	{
		if (JudgeBreakSE[index] == NoteJudge.JudgeBox.End || JudgeBreakSE[index] < judge)
		{
			JudgeBreakSE[index] = judge;
		}
	}

	public void ReserveSlideTouchSe(int index)
	{
		SlideTouchSE[index] = true;
	}

	public void ReserveBreakSlideTouchSe(int index)
	{
		BreakSlideTouchSE[index] = true;
	}

	public void ReserveBreakSlideSe(int index, NoteJudge.JudgeBox judge)
	{
		if (BreakSlideSE[index] == NoteJudge.JudgeBox.End || BreakSlideSE[index] < judge)
		{
			BreakSlideSE[index] = judge;
		}
	}

	public void ReserveExSe(int index, NoteJudge.JudgeBox judge)
	{
		if (ExSE[index] == NoteJudge.JudgeBox.End || ExSE[index] < judge)
		{
			ExSE[index] = judge;
		}
	}

	public void ReserveCenterEffectSe(int index, NoteJudge.JudgeBox judge)
	{
		if (CenterEffectSE[index] == NoteJudge.JudgeBox.End || CenterEffectSE[index] < judge)
		{
			CenterEffectSE[index] = judge;
		}
	}

	public void ReserveTouchHoldLoopSe(int index, NoteJudge.JudgeBox judge, bool loopDisable)
	{
		if (JudgeTouchHoldLoopSE[index] == NoteJudge.JudgeBox.End || JudgeTouchHoldLoopSE[index] < judge)
		{
			JudgeTouchHoldLoopSE[index] = judge;
			DisableTouchHoldLoop[index] = loopDisable;
		}
	}

	public void PlayJudgeSe(int index)
	{
		bool flag = false;
		if (AnsSE[index] && AnsVolume[index] != OptionVolumeAnswerSoundID.Mute)
		{
			MineAudio.PlayGameSE(Cue.SE_GAME_ANSWER, index, AnsVolume[index].GetValue());
		}
		if (JudgeTapSE[index] != NoteJudge.JudgeBox.End && SeVolume[index] != OptionVolumeID.Mute)
		{
			switch (Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.CriticalSe)
			{
			case OptionCriticalID.Default:
				switch (JudgeTapSE[index])
				{
				case NoteJudge.JudgeBox.Good:
					MineAudio.PlayGameSE(TapGoodSe[index], index, SeVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Great:
					MineAudio.PlayGameSE(TapGreatSe[index], index, SeVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Perfect:
					MineAudio.PlayGameSE(TapPerfectSe[index], index, SeVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Critical:
					MineAudio.PlayGameSE(TapPerfectSe[index], index, SeVolume[index].GetValue());
					break;
				}
				break;
			case OptionCriticalID.CriticalOn:
				switch (JudgeTapSE[index])
				{
				case NoteJudge.JudgeBox.Good:
					MineAudio.PlayGameSE(TapGoodSe[index], index, SeVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Great:
					MineAudio.PlayGameSE(TapGreatSe[index], index, SeVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Perfect:
					MineAudio.PlayGameSE(TapPerfectSe[index], index, SeVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Critical:
					MineAudio.PlayGameSE(TapCriticalSe[index], index, SeVolume[index].GetValue());
					break;
				}
				break;
			case OptionCriticalID.CriticalOnly:
			{
				NoteJudge.JudgeBox judgeBox2 = JudgeTapSE[index];
				if (judgeBox2 == NoteJudge.JudgeBox.Critical)
				{
					MineAudio.PlayGameSE(TapCriticalSe[index], index, SeVolume[index].GetValue());
				}
				break;
			}
			case OptionCriticalID.NotPerfect:
			{
				NoteJudge.JudgeBox judgeBox = JudgeTapSE[index];
				if ((uint)judgeBox <= 3u)
				{
					MineAudio.PlayGameSE(Cue.SE_GAME_NORMAL, index, SeVolume[index].GetValue());
					flag = true;
				}
				break;
			}
			}
		}
		if (JudgeTouchSE[index] != NoteJudge.JudgeBox.End && TouchVolume[index] != OptionVolumeID.Mute)
		{
			switch (Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.CriticalSe)
			{
			case OptionCriticalID.Default:
				switch (JudgeTouchSE[index])
				{
				case NoteJudge.JudgeBox.Good:
					MineAudio.PlayGameSE(Cue.SE_GAME_SCR_TOUCH_GOOD, index, TouchVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Great:
					MineAudio.PlayGameSE(Cue.SE_GAME_SCR_TOUCH_GREAT, index, TouchVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Perfect:
				case NoteJudge.JudgeBox.Critical:
					MineAudio.PlayGameSE(Cue.SE_GAME_SCR_TOUCH_PERFECT, index, TouchVolume[index].GetValue());
					break;
				}
				break;
			case OptionCriticalID.CriticalOn:
				switch (JudgeTouchSE[index])
				{
				case NoteJudge.JudgeBox.Good:
					MineAudio.PlayGameSE(Cue.SE_GAME_SCR_TOUCH_GOOD, index, TouchVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Great:
					MineAudio.PlayGameSE(Cue.SE_GAME_SCR_TOUCH_GREAT, index, TouchVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Perfect:
				case NoteJudge.JudgeBox.Critical:
					MineAudio.PlayGameSE(Cue.SE_GAME_SCR_TOUCH_PERFECT, index, TouchVolume[index].GetValue());
					break;
				}
				break;
			case OptionCriticalID.CriticalOnly:
			{
				NoteJudge.JudgeBox judgeBox4 = JudgeTouchSE[index];
				if (judgeBox4 == NoteJudge.JudgeBox.Critical)
				{
					MineAudio.PlayGameSE(Cue.SE_GAME_SCR_TOUCH_PERFECT, index, TouchVolume[index].GetValue());
				}
				break;
			}
			case OptionCriticalID.NotPerfect:
			{
				NoteJudge.JudgeBox judgeBox3 = JudgeTouchSE[index];
				if ((uint)judgeBox3 <= 3u && !flag)
				{
					MineAudio.PlayGameSE(Cue.SE_GAME_NORMAL, index, TouchVolume[index].GetValue());
				}
				break;
			}
			}
		}
		if (JudgeBreakSE[index] != NoteJudge.JudgeBox.End && BreakVolume[index] != OptionVolumeID.Mute)
		{
			switch (Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.CriticalSe)
			{
			case OptionCriticalID.CriticalOnly:
			{
				NoteJudge.JudgeBox judgeBox6 = JudgeBreakSE[index];
				if (judgeBox6 == NoteJudge.JudgeBox.Critical)
				{
					MineAudio.PlayGameSingleSe(BreakGoodSe[index], index, SoundManager.PlayerID.BreakSe, BreakVolume[index].GetValue());
					MineAudio.PlayGameSingleSe(Cue.SE_GAME_CHEER, index, SoundManager.PlayerID.Chear, BreakVolume[index].GetValue());
				}
				break;
			}
			case OptionCriticalID.NotPerfect:
			{
				NoteJudge.JudgeBox judgeBox5 = JudgeBreakSE[index];
				if ((uint)judgeBox5 <= 3u && !flag)
				{
					MineAudio.PlayGameSE(Cue.SE_GAME_NORMAL, index, SeVolume[index].GetValue());
				}
				break;
			}
			default:
				switch (JudgeBreakSE[index])
				{
				case NoteJudge.JudgeBox.Good:
					MineAudio.PlayGameSingleSe(BreakBadSe[index], index, SoundManager.PlayerID.BreakSe, BreakVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Great:
					MineAudio.PlayGameSingleSe(BreakBadSe[index], index, SoundManager.PlayerID.BreakSe, BreakVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Perfect:
					MineAudio.PlayGameSingleSe(BreakGoodSe[index], index, SoundManager.PlayerID.BreakSe, BreakVolume[index].GetValue());
					break;
				case NoteJudge.JudgeBox.Critical:
					MineAudio.PlayGameSingleSe(BreakGoodSe[index], index, SoundManager.PlayerID.BreakSe, BreakVolume[index].GetValue());
					MineAudio.PlayGameSingleSe(Cue.SE_GAME_CHEER, index, SoundManager.PlayerID.Chear, BreakVolume[index].GetValue());
					break;
				}
				break;
			}
		}
		if (SlideTouchSE[index] && SlideVolume[index] != OptionVolumeID.Mute)
		{
			MineAudio.PlayGameSingleSe(SlideSe[index], index, SoundManager.PlayerID.SlideSe, SlideVolume[index].GetValue());
		}
		if (BreakSlideTouchSE[index] && BreakSlideVolume[index] != OptionVolumeID.Mute)
		{
			MineAudio.PlayGameSingleSe(Cue.SE_GAME_BREAKSLIDE, index, SoundManager.PlayerID.BreakSlideSe, BreakSlideVolume[index].GetValue());
		}
		if (BreakSlideTouchSE[index] && SlideVolume[index] != OptionVolumeID.Mute)
		{
			MineAudio.PlayGameSingleSe(SlideSe[index], index, SoundManager.PlayerID.SlideSe, SlideVolume[index].GetValue());
		}
		if (BreakSlideSE[index] != NoteJudge.JudgeBox.End && BreakSlideVolume[index] != OptionVolumeID.Mute && BreakSlideSE[index] == NoteJudge.JudgeBox.Critical && Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.CriticalSe != OptionCriticalID.NotPerfect)
		{
			MineAudio.PlayGameSingleSe(Cue.SE_GAME_CHEER_BREAKSLIDE, index, SoundManager.PlayerID.BreakSlideChear, BreakSlideVolume[index].GetValue());
		}
		if (ExSE[index] != NoteJudge.JudgeBox.End && ExVolume[index] != OptionVolumeID.Mute)
		{
			OptionCriticalID criticalSe = Singleton<GamePlayManager>.Instance.GetGameScore(index).UserOption.CriticalSe;
			if (criticalSe == OptionCriticalID.NotPerfect)
			{
				NoteJudge.JudgeBox judgeBox7 = ExSE[index];
				if ((uint)judgeBox7 <= 3u && !flag)
				{
					MineAudio.PlayGameSE(Cue.SE_GAME_NORMAL, index, SeVolume[index].GetValue());
				}
			}
			else
			{
				NoteJudge.JudgeBox judgeBox8 = ExSE[index];
				if ((uint)(judgeBox8 - 1) <= 3u)
				{
					MineAudio.PlayGameSingleSe(ExSe[index], index, SoundManager.PlayerID.ExSe, ExVolume[index].GetValue());
				}
			}
		}
		if (CenterEffectSE[index] != NoteJudge.JudgeBox.End && TouchHoldVolume[index] != OptionVolumeID.Mute)
		{
			NoteJudge.JudgeBox judgeBox9 = CenterEffectSE[index];
			if ((uint)(judgeBox9 - 1) <= 3u)
			{
				MineAudio.PlayGameSingleSe(Cue.SE_GAME_SCR_TOUCH_CENTER, index, SoundManager.PlayerID.CenterSe, TouchHoldVolume[index].GetValue());
			}
		}
		if (StopTouchSe[index])
		{
			MineAudio.StopGameSingleSe(index, SoundManager.PlayerID.TouchHoldLoop);
			StopTouchSe[index] = false;
		}
		if (JudgeTouchHoldLoopSE[index] != NoteJudge.JudgeBox.End && TouchVolume[index] != OptionVolumeID.Mute)
		{
			switch (JudgeTouchHoldLoopSE[index])
			{
			case NoteJudge.JudgeBox.Miss:
				MineAudio.PlayGameSingleSe(Cue.SE_GAME_TOUCH_HOLD_MISS, index, SoundManager.PlayerID.TouchHoldLoop, TouchVolume[index].GetValue());
				break;
			case NoteJudge.JudgeBox.Good:
				MineAudio.PlayGameSingleSe(Cue.SE_GAME_TOUCH_HOLD_GOOD, index, SoundManager.PlayerID.TouchHoldLoop, TouchVolume[index].GetValue());
				break;
			case NoteJudge.JudgeBox.Great:
				MineAudio.PlayGameSingleSe(Cue.SE_GAME_TOUCH_HOLD_GREAT, index, SoundManager.PlayerID.TouchHoldLoop, TouchVolume[index].GetValue());
				break;
			case NoteJudge.JudgeBox.Perfect:
			case NoteJudge.JudgeBox.Critical:
				MineAudio.PlayGameSingleSe(Cue.SE_GAME_TOUCH_HOLD_PERFECT, index, SoundManager.PlayerID.TouchHoldLoop, TouchVolume[index].GetValue());
				break;
			}
			if (DisableTouchHoldLoop[index])
			{
				MineAudio.StopGameSingleSe(index, SoundManager.PlayerID.TouchHoldLoop);
				StopTouchSe[index] = true;
			}
		}
		ClearCue(index);
	}

	private void ClearCue(int index)
	{
		AnsSE[index] = false;
		SlideTouchSE[index] = false;
		BreakSlideTouchSE[index] = false;
		BreakSlideSE[index] = NoteJudge.JudgeBox.End;
		ExSE[index] = NoteJudge.JudgeBox.End;
		CenterEffectSE[index] = NoteJudge.JudgeBox.End;
		JudgeTapSE[index] = NoteJudge.JudgeBox.End;
		JudgeTouchSE[index] = NoteJudge.JudgeBox.End;
		JudgeBreakSE[index] = NoteJudge.JudgeBox.End;
		JudgeTouchHoldLoopSE[index] = NoteJudge.JudgeBox.End;
	}
}
