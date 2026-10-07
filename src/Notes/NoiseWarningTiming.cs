using System;
using System.Globalization;
using DB;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    // Match NoteBase.Initialize: GetNoteSpeedForBeat * 4 * 2 milliseconds.
    // Read the option directly so the per-note HS postfix queue is untouched.
    private static float NoiseWarningLead(float rawTapSpeed, float hs) => Math.Max(.05f,
        (float)((double)(1000f / (rawTapSpeed / 60f)) * 4d) * 2f / 1000f /
        Math.Max(.05f, Math.Abs(hs)));

    private static float GetNoisePlayerTapSpeed(OptionNotespeedID speed)
    {
        return speed.IsValid() ? speed.GetValue() : 850f;
    }
}
