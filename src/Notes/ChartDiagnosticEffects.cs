using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Manager;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // Called only for rate-limited reports, never for every note/frame.
    internal static string DiagnosticEffects(NotesReader reader, double time)
    {
        if (reader == null) return "reader-unavailable";
        var text = new StringBuilder();
        if (JudgeLineTimelines.TryGetValue(reader, out var judgeLine))
            for (int i = judgeLine.Changes.Length - 1; i >= 0; i--)
                if (judgeLine.Changes[i].Time <= time)
                { text.Append("JLINE{params=").Append(judgeLine.Changes[i].Encode()).Append("};"); break; }
        if (MediaTimelines.TryGetValue(reader, out var media))
            foreach (var change in media.Events)
                if (change.time <= time && time - change.time <= 2000)
                    text.Append("RECENT_").Append(change.kind.ToUpperInvariant()).Append("{enabled=").Append(change.enabled)
                        .Append(",track=").Append(change.track).Append(",eventMs=").Append(change.time.ToString("F0", CultureInfo.InvariantCulture)).Append("};");
        if (PresentationTimelines.TryGetValue(reader, out var presentation))
            foreach (var pair in presentation.Tracks)
            {
                var value = pair.Value.Evaluate(time, out var source);
                if (source == null) continue;
                // Retain recently disabled effects too: the expensive work may
                // have happened earlier in this long frame.
                if (Math.Abs(value) < .0001 && time - source.Time > 2000) continue;
                text.Append(pair.Key.ToUpperInvariant()).Append('=').Append(value.ToString("G5", CultureInfo.InvariantCulture))
                    .Append("{eventMs=").Append(source.Time.ToString("F0", CultureInfo.InvariantCulture))
                    .Append(",params=").Append(source.Encode()).Append("};");
            }
        if (NoiseCharts.TryGetValue(reader, out var noise))
        {
            int active = 0, warning = 0; double seconds = time * .001;
            foreach (var item in noise.Events)
            {
                if (seconds >= item.Start && seconds < item.End) active++;
                else if (seconds >= item.WarningStart && seconds < item.Start) warning++;
            }
            if (active + warning > 0) text.Append("NZONE{activeRegions=").Append(active).Append(",warningRegions=").Append(warning).Append("};");
        }
        if (VisualTimelines.TryGetValue(reader, out var visual))
        {
            int emitted = 0;
            foreach (var stream in visual.Streams)
            {
                var seen = new HashSet<string>();
                for (int i = stream.Value.Count - 1; i >= 0; i--)
                {
                    var change = stream.Value[i];
                    if (change.Time > time || !seen.Add(change.Kind + "/" + change.Type)) continue;
                    if (change.Value == null && time - change.Time > 2000) continue;
                    if (++emitted > 128) { text.Append("visual-state-truncated;"); return text.ToString(); }
                    text.Append(change.Kind.ToUpperInvariant()).Append("V[stream=").Append(stream.Key).Append(",type=").Append(change.Type)
                        .Append(",eventMs=").Append(change.Time.ToString("F0", CultureInfo.InvariantCulture)).Append("]=")
                        .Append(change.Value?.Encode() ?? "NULL").Append(';');
                }
            }
        }
        return text.Length == 0 ? "none-in-presentation/noise/visual-tracks" : text.ToString();
    }
}
