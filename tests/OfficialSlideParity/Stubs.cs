using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
namespace UnityEngine
{
    // The linked geometry uses Z-only rotations. This is portable geometry
    // coverage, not a test of Unity's quaternion/renderer implementation.
    public readonly struct Quaternion(float angle)
    {
        public Vector3 eulerAngles => new(0, 0, Mathf.Repeat(angle, 360));
        public static Quaternion Euler(float x, float y, float z)
            => x == 0 && y == 0 ? new(z) : throw new NotSupportedException("Only 2D geometry is covered");
        public static Quaternion Euler(Vector3 v) => Euler(v.x, v.y, v.z);
        public static Quaternion AngleAxis(float degrees, Vector3 axis)
            => axis.x == 0 && axis.y == 0 && axis.z == 1 ? new(degrees) : throw new NotSupportedException();
        public static Quaternion operator *(Quaternion a, Quaternion b) => new(a.eulerAngles.z + b.eulerAngles.z);
    }
}
namespace DB { public enum OptionMirrorID { Normal, LR, UD, UDLR } }
namespace MelonLoader { public static class MelonLogger { public static void Error(string s)=>Console.Error.WriteLine(s); } }
namespace MAI2.Util { public static class Singleton<T> where T:new() { public static T Instance {get;}=new T(); } }
namespace SinmaiAlpha.Hosting { }
namespace Manager
{
    public class SlideData { public SlideType type; public int targetNote; }
    public class NoteData { public int startButtonPos; public SlideData slideData=new(); }
    public static class InputManager
    {
        public enum TouchPanelArea { A1,A2,A3,A4,A5,A6,A7,A8,B1,B2,B3,B4,B5,B6,B7,B8,C1,C2,D1,D2,D3,D4,D5,D6,D7,D8,E1,E2,E3,E4,E5,E6,E7,E8,Blank,End }
    }
    public class SlideManager
    {
        public class HitArea { public double PushDistance,ReleaseDistance; public List<InputManager.TouchPanelArea> HitPoints=new(); }
        public static readonly JsonElement[] Cases=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Native170.json"))).RootElement.EnumerateArray().ToArray();
        public static bool RestoredReturnV = true;
        public static int NativeVCalls;
        public bool CheckSlideEnable(SlideType type,int start,int end)=>throw new Exception("Native table lookup is forbidden in unified generation");
        public float GetSlideLength(SlideType type,int start,int end)=>throw new Exception("Native length lookup is forbidden");
        public List<UnityEngine.Vector4> GetSlidePath(SlideType type,int start,int end,int star)=>throw new Exception("Native path lookup is forbidden");
        public List<HitArea> GetSlideHitArea(SlideType type,int start,int end,int star)
        {
            if (type is not (SlideType.Slide_Corner or SlideType.Slide_Skip_L or SlideType.Slide_Skip_R))
                throw new Exception("Only the explicit native v/V compatibility rule may read game tables");
            NativeVCalls++;
            return NativeVFixture(type, start, end, star);
        }
        public static List<HitArea> NativeVFixture(SlideType type,int start,int end,int star)
        {
            var span = (end - start + 8) & 7;
            if (!RestoredReturnV && type == SlideType.Slide_Corner && span == 0) return new();
            var row = Cases.FirstOrDefault(r => r.GetProperty("type").GetInt32() == (int)type && r.GetProperty("end").GetInt32() == span + 1);
            if (row.ValueKind == JsonValueKind.Undefined) return new();
            return row.GetProperty("native").EnumerateArray().Select(h => new HitArea {
                PushDistance = h.GetProperty("push").GetDouble(), ReleaseDistance = h.GetProperty("release").GetDouble(),
                HitPoints = h.GetProperty("panels").EnumerateArray().Select(p => {
                    var area = (int)Enum.Parse<InputManager.TouchPanelArea>(p.GetString());
                    if (area is 16 or 17) return (InputManager.TouchPanelArea)area;
                    var ring = area < 8 ? 0 : area < 16 ? 8 : area < 26 ? 18 : 26;
                    return (InputManager.TouchPanelArea)(ring + ((area - ring + start - star + 16) & 7));
                }).ToList()
            }).ToList();
        }
        // From the running game's SlideManager.GetSlideTypeLength.
        public int GetSlideTypeLength(SlideType type, int start, int end)
        {
            var length = end < start ? 8 + end - start : end - start;
            if (type is SlideType.Slide_Bend_R or SlideType.Slide_Circle_R or SlideType.Slide_Curve_R or
                SlideType.Slide_Skip_R or SlideType.Slide_Thunder_R) length = (8 - length) % 8;
            return length;
        }
    }
}
