using System;
using System.Linq;
using Mono.Cecil;
using SinmaiAlpha.Compatibility;

// No game is launched; only local fixtures execute. Supplied DLLs are inspected as metadata.
namespace MelonLoader
{
    public static class MelonLogger
    {
        public static void Msg(string text) => Console.WriteLine(text);
        public static void Warning(string text) => Console.WriteLine(text);
    }
}
namespace Monitor.Game
{
    public class GameCtrl
    {
        public static float _noteStartPos = 777, _noteEndPos = 888;
        public static readonly float[] TouchStartPosY = { 10, 11, 12, 13, 14 };
    }
}
public class Campaign
{
    public static float _noteStartPos = 123, _noteEndPos = 456;
    public static float[] TouchStartPosY = { 220, 0, 310, 400, 450 };
}
public static class Program
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    public static int Main(string[] args)
    {
        try
        {
            bool active = false;
            var host = typeof(Monitor.Game.GameCtrl);
            var originalArray = Monitor.Game.GameCtrl.TouchStartPosY;
            var bridge = new KaleidxScopeGeometry.GeometryBridge(typeof(Campaign), host, () => active);
            bridge.Synchronize();
            Check(Monitor.Game.GameCtrl._noteStartPos == 777 && Monitor.Game.GameCtrl._noteEndPos == 888 && originalArray[0] == 10,
                "Inactive campaign changed normal-mode coordinates");
            active = true;
            bridge.Synchronize();
            Check(Monitor.Game.GameCtrl._noteStartPos == 123 && Monitor.Game.GameCtrl._noteEndPos == 456, "Ring positions not synchronized");
            Check(ReferenceEquals(originalArray, Monitor.Game.GameCtrl.TouchStartPosY), "Readonly Touch array was replaced");
            Check(originalArray.SequenceEqual(Campaign.TouchStartPosY), "A retained native array reference sees stale coordinates");
            Check(!ReferenceEquals(originalArray, Campaign.TouchStartPosY), "Host and campaign arrays alias");
            Campaign.TouchStartPosY[0] = 999;
            Check(originalArray[0] == 220, "Campaign updates leaked before controller setup");
            Campaign._noteStartPos = 140; Campaign._noteEndPos = 500;
            Campaign.TouchStartPosY = new float[] { 1, 2, 3, 4, 5 };
            bridge.Synchronize();
            Check(Monitor.Game.GameCtrl._noteStartPos == 140 && Monitor.Game.GameCtrl._noteEndPos == 500 && originalArray[4] == 5,
                "Next song/controller did not refresh coordinates");
            Campaign._noteStartPos = 1; Campaign.TouchStartPosY = new float[2];
            bool rejected = false;
            try { bridge.Synchronize(); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && Monitor.Game.GameCtrl._noteStartPos == 140 && originalArray[4] == 5,
                "Malformed payload must be rejected before host mutation");
            active = false;
            bridge.Synchronize(); // Even malformed inactive payloads must leave normal play alone.
            rejected = false;
            try { new KaleidxScopeGeometry.GeometryBridge(typeof(Program), host, () => true); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Unknown field layout was accepted");
            KaleidxScopeGeometry.Initialize(null); // Optional dependency absent: no patches or errors.
            Console.WriteLine("PASS inactive mode, both ring positions, all Touch areas, retained array identity, cross-song refresh, malformed layout, absent optional mod");
            foreach (var path in args) Inspect(path);
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
    }
    private static void Inspect(string path)
    {
        using (var assembly = AssemblyDefinition.ReadAssembly(path))
        {
            if (assembly.Name.Name == "KaleidxScope.Runtime")
            {
                var getter = assembly.MainModule.GetType("KaleidxScope.Runtime.Integration").Methods.Single(m => m.Name == "get_Active");
                Check(getter.IsPublic && getter.IsStatic && getter.ReturnType.FullName == "System.Boolean", "Runtime Active contract mismatch");
            }
            else
            {
                var type = assembly.MainModule.GetType("Monitor.Game.GameCtrl");
                foreach (var name in new[] { "_noteStartPos", "_noteEndPos", "TouchStartPosY" })
                {
                    var field = type.Fields.Single(f => f.Name == name);
                    Check(field.IsStatic && field.FieldType.FullName == (name == "TouchStartPosY" ? "System.Single[]" : "System.Single"), "Coordinate shape mismatch: " + name);
                    if (name != "TouchStartPosY") Check(!field.IsInitOnly, "Scalar unexpectedly readonly");
                }
                var setup = type.Methods.Single(m => m.Name == "CreateNoteLuncher" && m.Parameters.Count == 0);
                Check(setup.IsPublic && !setup.IsStatic && setup.ReturnType.FullName == "System.Void", "Controller setup contract mismatch");
                if (assembly.Name.Name == "KaleidxScope.CiRCLE")
                {
                    var calls = type.Methods.Where(m => m.HasBody).Select(m => m.Body.Instructions
                        .Where(i => i.Operand is MethodReference).Select(i => ((MethodReference)i.Operand).Name).ToList());
                    Check(calls.Any(c => c.IndexOf("CreateNoteLuncher") >= 0 && c.IndexOf("CreateNotePool") > c.IndexOf("CreateNoteLuncher")),
                        "Coordinates no longer initialize before the note pool");
                }
            }
            Console.WriteLine("PASS metadata contract: " + assembly.Name.Name);
        }
    }
}
