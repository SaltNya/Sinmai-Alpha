using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Manager;
using Monitor;

namespace SinmaiAlpha.Notes;

/// <summary>
/// Input adapter for playable custom notes. Preserve the native down/held source
/// and input consumption; route only notes with an explicit custom binding.
/// Native Judge/NoteCheck still calculate grades and commit the play result.
/// </summary>
public static class PlayableNoteInput
{
    public static bool InGameButtonDown(int monitor, InputManager.ButtonSetting button, NoteBase owner)
        => !CustomNoteTypes.TryDZoneArea(owner, out _) && InputManager.InGameButtonDown(monitor, button);
    public static bool GetButtonDown(int monitor, InputManager.ButtonSetting button, NoteBase owner)
        => !CustomNoteTypes.TryDZoneArea(owner, out _) && InputManager.GetButtonDown(monitor, button);
    public static bool GetButtonPush(int monitor, InputManager.ButtonSetting button, NoteBase owner)
        => !CustomNoteTypes.TryDZoneArea(owner, out _) && InputManager.GetButtonPush(monitor, button);
    public static bool InGameButtonPush(int monitor, InputManager.ButtonSetting button, NoteBase owner)
        => !CustomNoteTypes.TryDZoneArea(owner, out _) && InputManager.InGameButtonPush(monitor, button);
    public static bool InGameTouchPanelAreaDown(int monitor, InputManager.ButtonSetting button, NoteBase owner)
        => CustomNoteTypes.TryDZoneArea(owner, out var area)
            ? InputManager.InGameTouchPanelAreaDown(monitor, area) : InputManager.InGameTouchPanelAreaDown(monitor, button);
    public static bool GetTouchPanelAreaDown(int monitor, InputManager.ButtonSetting button, NoteBase owner)
        => CustomNoteTypes.TryDZoneArea(owner, out var area)
            ? InputManager.GetTouchPanelAreaDown(monitor, area) : InputManager.GetTouchPanelAreaDown(monitor, button);
    public static bool InGameTouchPanelAreaPush(int monitor, InputManager.ButtonSetting button, NoteBase owner)
        => CustomNoteTypes.TryDZoneArea(owner, out var area)
            ? InputManager.InGameTouchPanelAreaPush(monitor, area) : InputManager.InGameTouchPanelAreaPush(monitor, button);
    public static bool GetTouchPanelAreaPush(int monitor, InputManager.ButtonSetting button, NoteBase owner)
        => CustomNoteTypes.TryDZoneArea(owner, out var area)
            ? InputManager.GetTouchPanelAreaPush(monitor, area) : InputManager.GetTouchPanelAreaPush(monitor, button);
    public static bool IsUsedThisFrame(int monitor, InputManager.ButtonSetting button, NoteBase owner)
        => CustomNoteTypes.TryDZoneArea(owner, out var area)
            ? InputManager.IsUsedThisFrame(monitor, area) : InputManager.IsUsedThisFrame(monitor, button);
    public static void SetUsedThisFrame(int monitor, InputManager.ButtonSetting button, NoteBase owner)
    {
        if (CustomNoteTypes.TryDZoneArea(owner, out var area)) InputManager.SetUsedThisFrame(monitor, area);
        else InputManager.SetUsedThisFrame(monitor, button);
    }

    internal static readonly Dictionary<MethodInfo, MethodInfo> NativeCalls = typeof(PlayableNoteInput)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Where(m => m.GetParameters().Length == 3 && m.GetParameters()[2].ParameterType == typeof(NoteBase))
        .ToDictionary(m => AccessTools.Method(typeof(InputManager), m.Name,
            new[] { typeof(int), typeof(InputManager.ButtonSetting) }), m => m);
}

public partial class CustomNoteTypes
{
    [HarmonyPatch]
    public static class PlayableNoteInputPatch
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            // Discover actual callers, including Hold and BreakHold's independent
            // head/body implementations. Shared InputManager itself is unpatched.
            return typeof(NoteBase).Assembly.GetTypes()
                .Where(t => typeof(NoteBase).IsAssignableFrom(t))
                .SelectMany(t => t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                .Where(m => !m.IsAbstract && m.GetMethodBody() != null
                    && PatchProcessor.GetOriginalInstructions(m).Any(i =>
                        i.opcode == OpCodes.Call && i.operand is MethodInfo call && PlayableNoteInput.NativeCalls.ContainsKey(call)));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.opcode != OpCodes.Call || !(instruction.operand is MethodInfo method)
                    || !PlayableNoteInput.NativeCalls.TryGetValue(method, out var adapter))
                {
                    yield return instruction;
                    continue;
                }
                var owner = new CodeInstruction(OpCodes.Ldarg_0);
                owner.labels.AddRange(instruction.labels);
                owner.blocks.AddRange(instruction.blocks);
                instruction.labels.Clear();
                instruction.blocks.Clear();
                yield return owner;
                instruction.operand = adapter;
                yield return instruction;
            }
        }
    }
}
