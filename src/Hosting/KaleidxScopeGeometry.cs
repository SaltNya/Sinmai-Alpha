using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using MelonLoader;

namespace SinmaiAlpha.Compatibility;

// The campaign owns a copied GameCtrl but uses the host's NoteBase/TouchNoteB.
// Early Harmony patches can JIT those notes before the campaign redirects the
// coordinate getters. Already-inlined getters still read the host's fields.
internal static class KaleidxScopeGeometry
{
    private static GeometryBridge bridge;

    internal static void Initialize(HarmonyLib.Harmony harmony)
    {
        if (bridge != null) return;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        var campaign = assemblies.FirstOrDefault(a => a.GetName().Name == "KaleidxScope.CiRCLE");
        var runtime = assemblies.FirstOrDefault(a => a.GetName().Name == "KaleidxScope.Runtime");
        if (campaign == null || runtime == null) return;
        try
        {
            var controller = campaign.GetType("Monitor.Game.GameCtrl", true);
            var integration = runtime.GetType("KaleidxScope.Runtime.Integration", true);
            var active = integration.GetProperty("Active", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            var launcher = controller.GetMethod("CreateNoteLuncher", BindingFlags.Public | BindingFlags.Instance,
                null, Type.EmptyTypes, null);
            if (active == null || active.ReturnType != typeof(bool) || launcher == null || launcher.ReturnType != typeof(void))
                throw new InvalidOperationException("Unsupported campaign controller API");
            bridge = new GeometryBridge(controller, typeof(Monitor.Game.GameCtrl),
                (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), active));
            harmony.Patch(launcher, postfix: new HarmonyMethod(typeof(KaleidxScopeGeometry), nameof(AfterCreateNoteLauncher)));
            MelonLogger.Msg("[KALEIDXSCOPE Compatibility] Shared note coordinates enabled");
        }
        catch (Exception error)
        {
            bridge = null;
            MelonLogger.Warning("[KALEIDXSCOPE Compatibility] Coordinate bridge unavailable: " + error.Message);
        }
    }

    private static void AfterCreateNoteLauncher()
    {
        try { bridge?.Synchronize(); }
        catch (Exception error)
        {
            // A different payload layout must not turn every song into an exception loop.
            bridge = null;
            MelonLogger.Warning("[KALEIDXSCOPE Compatibility] Coordinate bridge disabled: " + error.Message);
        }
    }

    internal sealed class GeometryBridge
    {
        private const BindingFlags Fields = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        private readonly FieldInfo sourceStart, sourceEnd, sourceTouch;
        private readonly FieldInfo targetStart, targetEnd, targetTouch;
        private readonly Func<bool> isActive;

        internal GeometryBridge(Type source, Type target, Func<bool> isActive)
        {
            if (source == target) throw new ArgumentException("Campaign and host controllers must differ");
            this.isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
            sourceStart = RequireField(source, "_noteStartPos", typeof(float));
            sourceEnd = RequireField(source, "_noteEndPos", typeof(float));
            sourceTouch = RequireField(source, "TouchStartPosY", typeof(float[]));
            targetStart = RequireField(target, "_noteStartPos", typeof(float));
            targetEnd = RequireField(target, "_noteEndPos", typeof(float));
            targetTouch = RequireField(target, "TouchStartPosY", typeof(float[]));
            if (targetStart.IsInitOnly || targetEnd.IsInitOnly)
                throw new InvalidOperationException("Host note coordinates are read-only");
        }

        private static FieldInfo RequireField(Type owner, string name, Type type)
        {
            var field = owner.GetField(name, Fields);
            if (field == null || field.FieldType != type)
                throw new InvalidOperationException("Unsupported coordinate field: " + owner.FullName + "." + name);
            return field;
        }

        internal void Synchronize()
        {
            if (!isActive()) return;
            var touch = (float[])sourceTouch.GetValue(null);
            var destination = (float[])targetTouch.GetValue(null);
            if (touch == null || destination == null || touch.Length != 5 || destination.Length != touch.Length)
                throw new InvalidOperationException("Unsupported Touch coordinate array");
            var start = (float)sourceStart.GetValue(null);
            var end = (float)sourceEnd.GetValue(null);
            targetStart.SetValue(null, start);
            targetEnd.SetValue(null, end);
            // Preserve the readonly array identity: native/JIT code may retain it.
            // This runs once per controller setup, not per note or frame.
            Array.Copy(touch, destination, touch.Length);
        }
    }
}
