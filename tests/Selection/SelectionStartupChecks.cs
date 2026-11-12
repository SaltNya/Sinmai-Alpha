using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Manager;
using Process;
using SinmaiAlpha.Difficulty;

internal static partial class Program
{
    private static List<ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData>> StartupCatalogue()
    {
        MusicSelectProcess.CombineMusicSelectData Row(int id, int difficulty)
        {
            var row = (MusicSelectProcess.MusicSelectData)FormatterServices.GetUninitializedObject(typeof(MusicSelectProcess.MusicSelectData));
            row.MusicData = Music(id, difficulty);
            var combined = (MusicSelectProcess.CombineMusicSelectData)FormatterServices.GetUninitializedObject(typeof(MusicSelectProcess.CombineMusicSelectData));
            combined.musicSelectData = new List<MusicSelectProcess.MusicSelectData> { row, row };
            return combined;
        }
        return new List<ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData>> {
            new List<MusicSelectProcess.CombineMusicSelectData> { Row(16050, 2), Row(16051, 4) }.AsReadOnly()
        };
    }

    private static void SelectionStartupChecks()
    {
        var harmony = new HarmonyLib.Harmony("SinmaiAlpha.Tests.SelectionStartup");
        var drawing = AccessTools.Field(typeof(ExtraDifficulty), "drawingSong");
        object beforeDrawing = drawing.GetValue(null);
        try
        {
            var start = PatchProcessor.GetOriginalInstructions(AccessTools.Method(typeof(MusicSelectProcess), "OnStart"));
            int sort = start.FindIndex(i => i.operand is MethodInfo method && method.Name == "OnSortChange");
            int allocate = start.FindIndex(i => i.operand is FieldInfo field && field.Name == "_currentPlayerSubSequence" &&
                i.opcode == System.Reflection.Emit.OpCodes.Stfld);
            Check(sort >= 0 && allocate > sort, "Native OnStart sorts before allocating the sequence array");

            harmony.PatchAll(typeof(ExtraDifficulty.AvailableResolvedDifficultyPatch));
            var process = (MusicSelectProcess)FormatterServices.GetUninitializedObject(typeof(MusicSelectProcess));
            process.CurrentDifficulty = new[] { MusicDifficultyID.Master, MusicDifficultyID.Advanced };
            var getCurrent = AccessTools.Method(typeof(MusicSelectProcess), "GetCurrentDifficulty");
            var getDifficulty = AccessTools.Method(typeof(MusicSelectProcess), "GetDifficulty");
            var rankSort = AccessTools.Method(typeof(MusicSelectProcess), "SetRankSortList");
            int Query(MethodInfo method, int player, int offset = 0) => (int)method.Invoke(process,
                method == getCurrent ? new object[] { player } : new object[] { player, offset });

            void PreserveStartup(string stage)
            {
                for (int player = 0; player < 2; player++)
                {
                    int expected = (int)process.CurrentDifficulty[player];
                    Check(Query(getCurrent, player) == expected, stage + ": native current difficulty preserved");
                    Check(Query(getDifficulty, player) == expected, stage + ": nested difficulty query preserved");
                    rankSort.Invoke(process, new object[] { player, new List<MusicSelectProcess.CombineMusicSelectData>(), false });
                    Check((int)process.CurrentDifficulty[player] == expected, stage + ": startup sorting did not mutate selection");
                }
            }

            // Exact report boundary: valid CurrentDifficulty, no catalogue or
            // sequence array yet. Only original game methods execute here.
            PreserveStartup("before sequence allocation");
            Set(process, "_combineMusicDataList", StartupCatalogue());
            PreserveStartup("catalogue exists but sequences are still null");
            Set(process, "_currentPlayerSubSequence", Array.Empty<MusicSelectProcess.SubSequence>());
            PreserveStartup("empty sequence array");
            Set(process, "_currentPlayerSubSequence", new[] { MusicSelectProcess.SubSequence.Music });
            Check(Query(getCurrent, 1) == (int)MusicDifficultyID.Advanced, "Unallocated second-player sequence remains native");

            Set(process, "_currentPlayerSubSequence", new[] { MusicSelectProcess.SubSequence.Genre, MusicSelectProcess.SubSequence.Genre });
            PreserveStartup("genre before drawing a song");
            drawing.SetValue(null, 1);
            for (int player = 0; player < 2; player++)
                Check(Query(getCurrent, player) == 2, "Song drawing can resolve before genre sequence transitions");
            drawing.SetValue(null, 0);

            Set(process, "_currentPlayerSubSequence", new[] { MusicSelectProcess.SubSequence.Music, MusicSelectProcess.SubSequence.Difficulty });
            for (int player = 0; player < 2; player++)
            {
                Check(Query(getCurrent, player) == 2, "Normal selection still resolves absent current chart");
                Check(Query(getDifficulty, player, 1) == 4, "Adjacent card uses its own chart availability");
                Check(Query(getDifficulty, player, -1) == 4, "Wrapped adjacent card still resolves");
            }

            Set(process, "_combineMusicDataList", null);
            PreserveStartup("sequences exist before catalogue");
            Set(process, "_combineMusicDataList", new List<ReadOnlyCollection<MusicSelectProcess.CombineMusicSelectData>>());
            PreserveStartup("empty catalogue");
            Set(process, "_combineMusicDataList", StartupCatalogue());
            process.CurrentCategorySelect = 1;
            PreserveStartup("category outside catalogue during refresh");
            process.CurrentCategorySelect = 0;
            Check(Query(getCurrent, 0) == 2, "Resolution resumes after catalogue refresh");

            System.Console.WriteLine("PASS: original startup sorting, null/partial selection state, both players, genre transition and adjacent-card resolution.");
        }
        finally { harmony.UnpatchSelf(); drawing.SetValue(null, beforeDrawing); }
    }
}
