using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using MAI2.Util;
using Mai2.Mai2Cue;
using Manager;
using Monitor.Game;

namespace SinmaiAlpha.Notes;

internal static class MineAudio
{
    internal static readonly MineCueController Cues = new MineCueController();
    private const int PlayersPerMonitor = 24; // 16 rotating voices + 8 dedicated native cue categories.
    private static int playerBase = -1;
    private static readonly int[] rotations = new int[2];
    private static readonly Dictionary<int, SoundCtrl.PlaySetting> pending = new Dictionary<int, SoundCtrl.PlaySetting>();

    internal static float Volume(float value) => float.IsNaN(value) ? .7f : Math.Max(0f, Math.Min(1f, value));

    internal static void EnsurePlayers()
    {
        if (playerBase >= 0) { EnsureVolumeSyncCapacity(); return; }
        var ctrl = Singleton<SoundCtrl>.Instance;
        var players = Traverse.Create(ctrl).Field("_players").GetValue<IDictionary>();
        if (players == null || !players.Contains(17) || !players.Contains(52)) return;
        playerBase = players.Keys.Cast<int>().DefaultIfEmpty(-1).Max() + 1;
        var type = players.GetType().GetGenericArguments()[1];
        try
        {
            EnsureVolumeSyncCapacity();
            for (var i = 0; i < PlayersPerMonitor * 2; i++)
            {
                var player = Activator.CreateInstance(type, true);
                players.Add(playerBase + i, player);
                Traverse.Create(player).Method("Create", false).GetValue();
            }
            InitializePlayers(ctrl);
        }
        catch { ReleasePlayers(); throw; }
    }
    internal static void EnsureVolumeSyncCapacity()
    {
        if (playerBase >= 0)
            Compatibility.OptionalVolumeSync.EnsureCapacity(playerBase + PlayersPerMonitor * 2);
    }

    internal static void ReleasePlayers()
    {
        pending.Clear(); Array.Clear(rotations, 0, rotations.Length);
        if (playerBase < 0) return;
        var players = Traverse.Create(Singleton<SoundCtrl>.Instance).Field("_players").GetValue<IDictionary>();
        if (players != null) for (var i = 0; i < PlayersPerMonitor * 2; i++)
        {
            var id = playerBase + i;
            if (!players.Contains(id)) continue;
            Traverse.Create(players[id]).Method("Dispose").GetValue(); players.Remove(id);
        }
        playerBase = -1;
    }

    internal static void InitializePlayers(SoundCtrl ctrl)
    {
        // Native Initialize configures only IDs 0..75. Copy routing/AISAC defaults from
        // the corresponding original player; future master/headphone changes then use
        // SoundCtrl's normal iteration over all players, as do StopAll and Terminate.
        var players = Traverse.Create(ctrl).Field("_players").GetValue<IDictionary>();
        for (var monitor = 0; monitor < 2; monitor++)
        {
            var template = Traverse.Create(players[17 + monitor * 35]);
            for (var slot = 0; slot < PlayersPerMonitor; slot++)
            {
                var target = Traverse.Create(players[playerBase + monitor * PlayersPerMonitor + slot]);
                target.Field("TargetID").SetValue(template.Field("TargetID").GetValue());
                foreach (DictionaryEntry entry in template.Field("Aisacs").GetValue<IDictionary>())
                {
                    var aisac = Traverse.Create(entry.Value);
                    if (aisac.Field("Enable").GetValue<bool>())
                        target.Method("SetAisac", (int)entry.Key, aisac.Field("Value").GetValue<float>()).GetValue();
                }
            }
        }
    }

    private static int Player(int monitor, int slot) => playerBase + monitor * PlayersPerMonitor + slot;

    public static void PlayGameSE(Cue cue, int monitor, float volume)
    {
        var slot = rotations[monitor];
        rotations[monitor] = (slot + 1) % 16;
        Queue(cue, monitor, slot, volume);
    }

    public static void PlayGameSingleSe(Cue cue, int monitor, SoundManager.PlayerID player, float volume)
        => Queue(cue, monitor, (int)player - 17, volume);

    private static void Queue(Cue cue, int monitor, int slot, float volume)
    {
        if (playerBase < 0 || monitor < 0 || monitor > 1 || slot < 0 || slot >= PlayersPerMonitor) return;
        var gain = volume * Volume(CustomNoteTypes.MineVolume);
        if (gain <= 0f) return;
        // Match the native per-frame cue deduplication, confined to Mine sounds.
        if (pending.Values.Any(v => v.SpekerTarget == monitor && v.CueIndex == (int)cue)) return;
        var id = Player(monitor, slot);
        pending[id] = new SoundCtrl.PlaySetting
        {
            PlayerID = id, AcbID = (int)SoundManager.AcbID.Default,
            CueIndex = (int)cue, SpekerTarget = monitor, Volume = gain
        };
    }

    public static void StopGameSingleSe(int monitor, SoundManager.PlayerID player)
    {
        if (playerBase < 0 || monitor < 0 || monitor > 1) return;
        var slot = (int)player - 17;
        if (slot < 0 || slot >= PlayersPerMonitor) return;
        var id = Player(monitor, slot);
        pending.Remove(id);
        Singleton<SoundCtrl>.Instance.Stop(id);
    }

    internal static void Flush()
    {
        var ctrl = Singleton<SoundCtrl>.Instance;
        foreach (var setting in pending.Values)
        {
            ctrl.Stop(setting.PlayerID);
            ctrl.Play(setting);
        }
        pending.Clear();
    }

    internal static void Clear() => pending.Clear();

    internal static void ResetMonitor(int monitor)
    {
        if (playerBase >= 0)
            for (var slot = 0; slot < PlayersPerMonitor; slot++)
            {
                var id = Player(monitor, slot);
                pending.Remove(id);
                Singleton<SoundCtrl>.Instance.Stop(id);
            }
        rotations[monitor] = 0;
        Cues.Initialize(monitor);
    }
}

public partial class CustomNoteTypes
{
    // VolumeSync rebuilds its array in Initialize's prefix. Repair it after all
    // initialization patches if Alpha players already exist (e.g. reinitialize).
    [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(SoundCtrl), "Initialize")]
    public static void MineAudioVolumeSyncInitialize() => MineAudio.EnsureVolumeSyncCapacity();

    [HarmonyPostfix, HarmonyPatch(typeof(GameSingleCueCtrl), "Initialize")]
    public static void MineAudioReset(int index) => MineAudio.ResetMonitor(index);

    [HarmonyPostfix, HarmonyPatch(typeof(GameSingleCueCtrl), "PlayJudgeSe")]
    public static void MineAudioPlay(int index) { if (FeaturesForMonitor(index)) MineAudio.Cues.PlayJudgeSe(index); }

    [HarmonyPostfix, HarmonyPatch(typeof(SoundManager), "PlayExecute")]
    public static void MineAudioFlush() { if (ChartFeatureGate.AnySelected) MineAudio.Flush(); }

    [HarmonyPrefix, HarmonyPatch(typeof(SoundCtrl), "StopAll")]
    public static void MineAudioClear() => MineAudio.Clear();

    [HarmonyPrefix, HarmonyPatch(typeof(SoundCtrl), "Terminate")]
    public static void MineAudioTerminate() => MineAudio.ReleasePlayers();
}
