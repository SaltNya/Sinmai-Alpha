using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using MAI2.Util;
using Mai2.Mai2Cue;
using Manager;
using Monitor;
using Process;
using Process.SubSequence;
using SinmaiAlpha.Difficulty;

internal static partial class Program
{
    private static readonly List<KeyValuePair<int, Cue>> PlayedDifficultySounds = new List<KeyValuePair<int, Cue>>();
    private static bool SkipSelectionSurface() => false;
    private static bool SelectionGuest(ref bool __result) { __result = true; return false; }
    private static bool CaptureSelectionSound(Cue __0, int __1, ref SoundManager.PlayerID __result)
    {
        PlayedDifficultySounds.Add(new KeyValuePair<int, Cue>(__1, __0));
        __result = default(SoundManager.PlayerID);
        return false;
    }

    private static void SelectionSoundChecks()
    {
        var harmony = new HarmonyLib.Harmony("SinmaiAlpha.Tests.SelectionSounds");
        var nativeField = AccessTools.Field(typeof(MusicSelectProcess), "DifficultySe");
        var native = (Cue[])nativeField.GetValue(null);
        var userManager = Singleton<UserDataManager>.Instance;
        var usersField = AccessTools.Field(typeof(UserDataManager), "_userData");
        var oldUsers = usersField.GetValue(userManager);
        int transitions = 0;
        try
        {
            Check(native.Length == 5, "Exercise the real five-slot readonly sound table");
            var skip = new HarmonyMethod(typeof(Program), nameof(SkipSelectionSurface));
            // Only the rendering/device endpoints are stubbed; sequence methods run to completion.
            foreach (var name in new[] { "AddDifficulty", "SubDifficulty", "setForceDiffControlParam", "UpdateRivalScore",
                "UpdateNationalDataObject", "ScrollDifficultySelect", "SetSideMessage", "AddChategoryDifficulty", "SubCategoryDifficulty" })
                harmony.Patch(AccessTools.Method(typeof(MusicSelectMonitor), name), prefix: skip);
            harmony.Patch(AccessTools.Method(typeof(DifficultySelectSequence), "CheckButton"), prefix: skip);
            harmony.Patch(AccessTools.Method(typeof(UserData), "IsGuest"), prefix: new HarmonyMethod(typeof(Program), nameof(SelectionGuest)));
            harmony.Patch(AccessTools.Method(typeof(SoundManager), "PlaySE", new[] { typeof(Cue), typeof(int) }),
                prefix: new HarmonyMethod(typeof(Program), nameof(CaptureSelectionSound)));
            usersField.SetValue(userManager, Enumerable.Range(0, 4).Select(_ =>
                (UserData)FormatterServices.GetUninitializedObject(typeof(UserData))).ToArray());

            var monitors = Enumerable.Range(0, 2).Select(_ =>
                (MusicSelectMonitor)FormatterServices.GetUninitializedObject(typeof(MusicSelectMonitor))).ToArray();
            for (int pass = 0; pass < 2; pass++)
            for (int maximum = 3; maximum <= 4; maximum++)
            for (int player = 0; player < 2; player++)
            for (int tab = 0; tab < 2; tab++)
            {
                var stub = new SelectionProcess(Music(16050 + maximum, maximum)) {
                    Remaster = maximum == 4, LevelTab = tab != 0, CompleteWorkflow = true, Monitors = monitors
                };
                var process = (IMusicSelectProcessProcessing)stub.GetTransparentProxy();
                foreach (var sequence in new SequenceBase[] { new MusicSelectSequence(player, true, process),
                    new DifficultySelectSequence(player, true, process), new GenreSelectSequence(player, true, process) })
                for (int direction = -1; direction <= 1; direction += 2)
                for (int start = 0; start <= maximum; start++)
                {
                    var type = sequence.GetType();
                    var method = AccessTools.Method(type, direction > 0 ? "AddDifficulty" : "SubDifficulty");
                    stub.Row.Difficulty = start;
                    stub.Difficulties[player] = (MusicDifficultyID)start;
                    stub.Difficulties[1 - player] = MusicDifficultyID.Basic;
                    stub.Selected[player] = start;
                    stub.InputLockPlayer = -1;
                    if (sequence is DifficultySelectSequence) Set(sequence, "difficultyIndex", (MusicDifficultyID)start);
                    PlayedDifficultySounds.Clear();
                    var arguments = sequence is DifficultySelectSequence
                        ? new object[] { monitors[player], InputManager.ButtonSetting.Button03 }
                        : new object[] { monitors };
                    method.Invoke(sequence, arguments);
                    int ceiling = sequence is GenreSelectSequence ? 4 : maximum;
                    bool changed = direction < 0 ? start > 0 : start < ceiling;
                    int destination = changed ? start + direction : start;
                    int expectedSounds = changed ? sequence is MusicSelectSequence && stub.LevelTab ? 2 : 1 : 0;
                    string label = type.Name + " player=" + player + " tab=" + tab + " start=" + start + " direction=" + direction;
                    Check(PlayedDifficultySounds.Count == expectedSounds, "Complete sound dispatch: " + label);
                    int soundSlot = destination;
                    foreach (var played in PlayedDifficultySounds) Check(played.Value == native[soundSlot], "Correct difficulty cue: " + label);
                    if (expectedSounds == 1) Check(PlayedDifficultySounds[0].Key == player, "Correct sound player: " + label);
                    else if (expectedSounds == 2) Check(PlayedDifficultySounds.Select(x => x.Key).SequenceEqual(new[] { 0, 1 }), "Level tab sounds both monitors");
                    bool changesCurrent = sequence is GenreSelectSequence || !stub.LevelTab;
                    Check((int)stub.Difficulties[player] == (changesCurrent ? destination : start), "Selection state after audio: " + label);
                    Check(stub.Difficulties[1 - player] == MusicDifficultyID.Basic, "Other player's selection unchanged");
                    if (changed || !(sequence is DifficultySelectSequence)) Check(stub.InputLockPlayer == player, "Reached end of selection workflow: " + label);
                    if (sequence is DifficultySelectSequence && changed) Check(stub.Selected[player] == destination, "Difficulty index updated after audio");
                    transitions++;
                }
            }
            Check(ReferenceEquals(native, nativeField.GetValue(null)), "Stock readonly sound table is never replaced");
            System.Console.WriteLine("PASS: " + transitions + " complete selection workflows including sound lookup, both directions/players/tabs; native rendering endpoints stubbed.");
            harmony.PatchAll(typeof(ExtraDifficulty.AvailableStepPatch));
            int sparseTransitions = 0;
            for (int mask = 1; mask < 32; mask++)
            for (int player = 0; player < 2; player++)
            for (int tab = 0; tab < 2; tab++)
            {
                var enabled = Enumerable.Range(0, 5).Where(d => (mask & (1 << d)) != 0).ToArray();
                var stub = new SelectionProcess(Music(16050, enabled)) {
                    Remaster = enabled.Contains(4), LevelTab = tab != 0, CompleteWorkflow = true, Monitors = monitors
                };
                var process = (IMusicSelectProcessProcessing)stub.GetTransparentProxy();
                foreach (var sequence in new SequenceBase[] { new MusicSelectSequence(player, true, process), new DifficultySelectSequence(player, true, process) })
                foreach (int start in enabled)
                foreach (int direction in new[] { -1, 1 })
                {
                    stub.Row.Difficulty = start;
                    stub.Difficulties[player] = (MusicDifficultyID)start;
                    stub.Difficulties[1 - player] = MusicDifficultyID.Basic;
                    if (sequence is DifficultySelectSequence) Set(sequence, "difficultyIndex", (MusicDifficultyID)start);
                    PlayedDifficultySounds.Clear();
                    var method = AccessTools.Method(sequence.GetType(), direction > 0 ? "AddDifficulty" : "SubDifficulty");
                    method.Invoke(sequence, sequence is DifficultySelectSequence
                        ? new object[] { monitors[player], InputManager.ButtonSetting.Button03 } : new object[] { monitors });
                    int next = DifficultyAvailability.Next(mask, start, direction);
                    int expected = next >= 0 ? next : start;
                    string label = "mask=" + mask + " start=" + start + " direction=" + direction + " tab=" + tab + " sequence=" + sequence.GetType().Name;
                    Check(PlayedDifficultySounds.Count == (next < 0 ? 0 : sequence is MusicSelectSequence && stub.LevelTab ? 2 : 1), "Sparse sound count " + label);
                    if (!stub.LevelTab) Check((int)stub.Difficulties[player] == expected, "Sparse target " + label);
                    if (sequence is DifficultySelectSequence && stub.LevelTab) Check((int)AccessTools.Field(sequence.GetType(), "difficultyIndex").GetValue(sequence) == expected, "Sparse difficulty-page target " + label);
                    foreach (var sound in PlayedDifficultySounds) Check(sound.Value == native[expected], "Sparse cue " + label);
                    Check(stub.Difficulties[1 - player] == MusicDifficultyID.Basic, "Sparse other player unchanged");
                    sparseTransitions++;
                }
            }
            System.Console.WriteLine("PASS: " + sparseTransitions + " sparse chart selection transitions on original game methods.");
        }
        finally { harmony.UnpatchSelf(); usersField.SetValue(userManager, oldUsers); }
    }
}
