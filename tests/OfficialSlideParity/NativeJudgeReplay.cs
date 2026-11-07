using System.Collections.Generic;
using Manager;

// Headless replay of 1.70 SlideRoot.NextCheck/CheckSlideTouch/manual progression.
// Source Assembly-CSharp.dll SHA256:
// DC0E46A4133D4EE4781BA6DEB2FE30063DD3F82EE91C31A5A8A7E61E644D9446
// Rendering, sound, wall-clock timing and Unity input are deliberately excluded.
sealed class NativeJudgeReplay(SlideType type, int start, int end, List<SlideManager.HitArea> areas)
{
    int index, subIndex;
    bool entered;
    public (int Index, int SubIndex, bool Entered) State => (index, subIndex, entered);

    internal void Reset(int hitIndex, bool hitIn, int hitSubIndex = 0)
    { index = hitIndex; entered = hitIn; subIndex = hitSubIndex; }

    internal bool NextCheck()
    {
        if (type is SlideType.Slide_Skip_L or SlideType.Slide_Skip_R)
        {
            var length = new SlideManager().GetSlideTypeLength(type, start, end);
            return length switch {
                1 or 2 or 3 => ExtraHitAreaCheck(1),
                4 => ExtraHitAreaCheck(1) && ExtraHitAreaCheck(3),
                _ => true
            };
        }
        return areas.Count > 3 || areas.Count - 1 != index + 1 || entered;
    }

    bool ExtraHitAreaCheck(int checkpoint) => index != checkpoint || entered;

    internal void Step(ulong pressed)
    {
        if (index >= areas.Count) return;
        bool progress;
        do
        {
            progress = CheckSlideTouch(index, entered, pressed);
            if (!progress && NextCheck() && index + 1 < areas.Count)
                progress = CheckSlideTouch(index + 1, false, pressed);
            if (index >= areas.Count) progress = false;
        } while (progress);
    }

    bool CheckSlideTouch(int hitIndex, bool hitIn, ulong pressed)
    {
        if (!hitIn)
        {
            var points = areas[hitIndex].HitPoints;
            for (var i = 0; i < points.Count; i++)
                if (IsPressed(points[i], pressed))
                {
                    index = hitIndex; subIndex = i; entered = true;
                    if (hitIndex == areas.Count - 1) index++;
                    return true;
                }
        }
        else if (!IsPressed(areas[hitIndex].HitPoints[subIndex], pressed))
        {
            index = hitIndex + 1; subIndex = 0; entered = false;
            return true;
        }
        return false;
    }

    bool IsPressed(InputManager.TouchPanelArea area, ulong pressed)
    {
        var sensor = (int)area;
        if (sensor is 16 or 17) sensor = 16; // native C1/C2 both read the center
        else
        {
            var ring = sensor < 8 ? 0 : sensor < 16 ? 8 : sensor < 26 ? 18 : 26;
            sensor = ring + ((sensor - ring + start) & 7);
        }
        return (pressed & (1UL << sensor)) != 0;
    }
}
