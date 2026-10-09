using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using SinmaiAlpha.Hosting;
using DB;
using HarmonyLib;
using MAI2.Util;
using Manager;
using MelonLoader;
using Monitor;
using Monitor.Game;
using Process;
using UnityEngine;
using SinmaiAlpha.Notes.Libs;

namespace SinmaiAlpha.Notes;

// 地雷 Hold 按下时的贴图替换。
public partial class CustomNoteTypes
{

    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * 地雷判定
     *
     * 原版的判定入口（非自动播放）：
     *   - Tap / Star / BreakTap / Ex / ExBreak：NoteBase.Judge / NoteBase.JudgeToolate
     *   - Touch：TouchNoteB.Judge（超时走 NoteBase.JudgeToolate）
     *   - Slide：SlideRoot.Judge / SlideRoot.JudgeToolate
     *   - Hold / BreakHold / TouchHold：JudgeTotalResult（头判 + 体判算出的最终结果）
     * 它们都是同一个模式：算出 NoteJudge.ETiming 后 stfld 到 JudgeResult / JudgeHeadResult 字段。
     * 计分(SetPlayResult)、判定显示(JudgeGrade)、特效全部读这个字段，
     * 所以在写入字段前把地雷的判定反转即可：
     *     命中（非 Miss） -> Miss（TooLate）
     *     未命中（Miss）  -> Critical Perfect
     * 本版本 NoteJudge.ETiming 里 TooFast=0 / TooLate=14 / End=15 是三种 Miss
     * （NoteJudge.ConvertJudge 都映射到 JudgeBox.Miss），没有名为 Miss 的枚举值。
     * 注：AutoJudge / GameManager.AutoJudge 只在自动播放时调用，手动判定不走那里。
     */

    /*
     * ========== ========== ========== ========== ========== ========== ========== ==========
     * HoldOn 地雷贴图（transpiler）
     *
     * HoldNote.HoldOn / BreakHoldNote.HoldOn / TouchHoldC.HoldOn 都是非虚方法（无法 override），
     * 玩家按住/释放时会给 hold 条赋原版亮态贴图（NormalHoldOn / EachHoldOn / HoldOff /
     * BreakHoldOn / TouchHoldGuide / TouchHoldGuideOff），把地雷贴图覆盖掉。
     * 方案：transpiler 把方法里的 SpriteRenderer.set_sprite 调用替换成
     *   ldarg.1; call SetHoldSpriteWithMine(SpriteRenderer, Sprite, bool on)
     * （set_sprite 前栈是 [sr, sprite]，ldarg.1 压入 on 参数，栈形不变），
     * helper 先执行原版赋值，再按地雷类型重贴对应贴图。
     */

    public static void SetHoldSpriteWithMine(SpriteRenderer sr, Sprite sprite, bool on)
    {
        if (sr == null) return;
        sr.sprite = sprite;

        var note = sr.GetComponentInParent<NoteBase>();

        if (note is TouchBreakHoldC)
        {
            ApplyHoldKeyToSprite(sr, "touchhold_break");
            ApplyBreakTouchHoldProgress(note);
        }
        else if (note is MineTouchHoldC)
        {
            // touchhold 的 gauge：普通用 touchhold_off，绝赞（BRTHO，IsTouchBreak）用 touchhold_break。
            var critical = note.GetComponent<MineNoteBehaviour>()?.IsTouchBreak == true;
            ApplyHoldKeyToSprite(sr, critical ? "touchhold_break" : "touchhold_off");
        }
        else if (note is MineBreakHoldNote)
        {
            ApplyHoldKeyToSprite(sr, on ? "hold_break_mine_on" : "hold_break_mine");
        }
        else if (note is MineHoldNote)
        {
            ApplyHoldKeyToSprite(sr, on ? "hold_mine_on" : "hold_mine");
        }
    }

    private static void ApplyHoldKeyToSprite(SpriteRenderer sr, string textureKey)
    {
        if (!MineTextures.TryGetValue(textureKey, out var texture))
        {
            MelonLogger.Warning($"[CustomNoteType] Missing mine texture: {textureKey}");
            return;
        }

        sr.sprite = CreateSpriteFromTexture(textureKey, texture, sr.sprite);
    }

    [HarmonyPatch]
    public static class HoldOnMineTranspiler
    {
        public static IEnumerable<MethodBase> TargetMethods()
        {
            return
            [
                AccessTools.Method(typeof(HoldNote), "HoldOn"),
                AccessTools.Method(typeof(BreakHoldNote), "HoldOn"),
                AccessTools.Method(typeof(TouchHoldC), "HoldOn"),
            ];
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            foreach (var inst in instructions)
            {
                // 用方法名匹配 SpriteRenderer.set_sprite（不依赖 Harmony 的 Calls 匹配，兼容 Cecil operand）。
                if ((inst.opcode == OpCodes.Callvirt || inst.opcode == OpCodes.Call) &&
                    GetMethodOperandName(inst.operand) == "set_sprite")
                {
                    // set_sprite 前栈是 [sr, sprite]；ldarg.1 压入 on → call helper 消费三个参数。
                    // ⚠️ 被替换的指令可能带有分支标签 / 异常块边界（try/catch），必须转移到
                    // 第一条替换指令上，否则 DMD 编译报 "Label #N is not marked"
                    // （IL Compile Error，HoldOn 的 branch 目标就指向 set_sprite）。
                    var ldarg = new CodeInstruction(OpCodes.Ldarg_1);
                    ldarg.labels.AddRange(inst.labels);
                    ldarg.blocks.AddRange(inst.blocks);
                    yield return ldarg;
                    yield return new CodeInstruction(OpCodes.Call,
                        AccessTools.Method(typeof(CustomNoteTypes), nameof(SetHoldSpriteWithMine)));
                    continue;
                }

                yield return inst;
            }
        }

        private static string GetMethodOperandName(object operand)
        {
            if (operand is MethodInfo mi) return mi.Name;
            var prop = operand?.GetType().GetProperty("Name");
            return prop?.GetValue(operand) as string;
        }
    }
}
