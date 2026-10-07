using System.Collections.Generic;
using MAI2.Util;
using Manager;
using Monitor;
using Monitor.Game;
using UnityEngine;

namespace SinmaiAlpha.Notes;

// Native ring note classes continue to own timing, hold release accumulation,
// EX/break scoring, audio, autoplay and recycling. This launcher supplies only
// the alternate position, independent queue and corresponding feedback objects.
public sealed class DZoneLane : MonoBehaviour
{
    private static readonly List<DZoneLane> Instances = new List<DZoneLane>();
    public int Key { get; private set; }
    public int MonitorId { get; private set; }
    public JudgeGrade Grade { get; private set; }
    public TouchEffect Effect { get; private set; }

    internal static IEnumerable<JudgeGrade> Grades(int monitorId)
    {
        foreach (var lane in Instances)
            if (lane != null && lane.MonitorId == monitorId && lane.Grade != null)
                yield return lane.Grade;
    }

    internal static DZoneLane Create(Transform nativeLauncher, int key, int monitorId)
    {
        var root = new GameObject("AquaMai D" + (key + 1));
        root.transform.SetParent(nativeLauncher.parent, false);
        root.transform.localPosition = nativeLauncher.localPosition;
        root.transform.localScale = nativeLauncher.localScale;
        root.transform.localRotation = nativeLauncher.localRotation * Quaternion.Euler(0f, 0f, 22.5f);
        var lane = root.AddComponent<DZoneLane>();
        lane.Key = key;
        lane.MonitorId = monitorId;

        // IsJudgeNote expects two fixed launcher children, followed by the note
        // queue in reverse registration order (childCount - 3 == siblingIndex).
        var start = new GameObject("NoteStart");
        start.transform.SetParent(root.transform, false);
        var end = new GameObject("NoteEnd");
        end.transform.SetParent(root.transform, false);
        var nativeEnd = nativeLauncher.Find("NoteEnd");
        if (nativeEnd != null)
        {
            end.transform.localPosition = nativeEnd.localPosition;
            end.transform.localRotation = nativeEnd.localRotation;
            end.transform.localScale = nativeEnd.localScale;
        }
        else
        {
            end.transform.localPosition = new Vector3(0f, GameCtrl.NoteEndPos(), 0f);
        }

        lane.Grade = Object.Instantiate(GameNotePrefabContainer.JudgeGrade, end.transform);
        var option = Singleton<GamePlayManager>.Instance.GetGameScore(monitorId).UserOption;
        lane.Grade.SetOption(option.DispJudge, (int)option.DispJudgePos);
        lane.Grade.SetLedSetting(-1, monitorId);
        lane.Grade.gameObject.SetActive(false);
        lane.Effect = Object.Instantiate(GameNotePrefabContainer.TouchEffect, end.transform);
        lane.Effect.SetUpParticle(monitorId);
        lane.Effect.gameObject.SetActive(false);
        Instances.Add(lane);
        return lane;
    }

    internal static void Tick(int monitorId)
    {
        foreach (var lane in Instances)
        {
            if (lane == null || lane.MonitorId != monitorId) continue;
            if (lane.Grade.gameObject.activeSelf) lane.Grade.Execute();
            if (lane.Effect.gameObject.activeSelf) lane.Effect.Execute();
        }
    }

    internal static void ResetEffects(int monitorId)
    {
        foreach (var lane in Instances)
        {
            if (lane == null || lane.MonitorId != monitorId) continue;
            lane.Effect.StopHoldPlay();
            lane.Effect.gameObject.SetActive(false);
            lane.Grade.gameObject.SetActive(false);
        }
    }

    private void OnDestroy()
    {
        Instances.Remove(this);
    }
}
