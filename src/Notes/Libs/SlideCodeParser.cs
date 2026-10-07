using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using MelonLoader;

namespace SinmaiAlpha.Notes.Libs;

public static class SlideCodeParser
{
    public enum CommandType
    {
        Invalid = -1,
        NodeA = 0,
        NodeB = 1,
        NodeC = 2,
        OrbitCCW = 3,
        OrbitCW = 4,
        NodeEnd = 5,
        NodeE = 6,
        NodeD = 7,
        NodeF = 8 // A 区（相邻环键之间的触摸区，两键中间起点）
    }

    public struct Command(CommandType type, int value, char shape)
    {
        public CommandType Type = type;
        public int Value = value;
        public char Shape = shape;   // '-' 直线（默认）、'<' 逆时针弧、'>' 顺时针弧、'^' 最短弧

        public static bool IsSame(Command a, Command b)
        {
            return a.Type == b.Type && a.Value == b.Value;
        }
    }

    public static readonly char[] CommandChars =
    [
        'A', 'B', 'C', 'P', 'Q', 'K', 'E', 'D', 'F'
    ];

    // 弧线连接符（Majdata touch-slide 的 < > ^）：修饰下一个节点命令的连接方式
    public static readonly char[] ArcShapes =
    [
        '<', '>', '^'
    ];

    public static int TryParseDigit(char c)
    {
        if (c >= '0' && c <= '9') return c - '0';
        return -1;
    }

    public static List<Command> ParseCommands(string code)
    {
        if (code.Length < 3)
        {
            throw new ArgumentException($"code too short");
        }

        if (code[code.Length - 2] != 'K')
        {
            throw new ArgumentException($"should end with 'K' command");
        }

        var commands = new List<Command>();
        var currentShape = '-';
        var startPtr = 1;

        // 起点：数字 = A 环节点（按键环，兼容旧语法，如 "1K5"）；
        // B/E/C = 非 A 区启动（如 "B1K5" 起点 B1、"E3K5" 起点 E3、"CK5" 起点中心）。
        var startValue = TryParseDigit(code[0]);
        if (startValue >= 0)
        {
            commands.Add(new Command(CommandType.NodeA, startValue, currentShape));
            if (!CommandChars.Contains(code[1]) && !ArcShapes.Contains(code[1]))
            {
                throw new ArgumentException($"the 2nd char should be a command");
            }
        }
        else
        {
            switch (code[0])
            {
                case 'B':
                case 'E':
                case 'D':
                case 'F':
                    startValue = TryParseDigit(code[1]);
                    if (startValue < 0) throw new ArgumentException($"invalid char '{code[1]}'");
                    commands.Add(new Command(
                        code[0] == 'B' ? CommandType.NodeB :
                        code[0] == 'E' ? CommandType.NodeE :
                        code[0] == 'D' ? CommandType.NodeD : CommandType.NodeF,
                        startValue, currentShape));
                    startPtr = 2;
                    break;
                case 'C':
                    commands.Add(new Command(CommandType.NodeC, 0, currentShape));
                    break;
                default:
                    throw new ArgumentException($"invalid char '{code[0]}'");
            }
        }

        var currentType = commands[0].Type;
        var value = 0;
        for (var ptr = startPtr; ptr < code.Length; ptr++)
        {
            var ch = code[ptr];
            if (ArcShapes.Contains(ch))
            {
                // 弧线连接符：修饰下一个节点命令的连接方式
                currentShape = ch;
            }
            else if (CommandChars.Contains(ch))
            {
                currentType = (CommandType) Array.IndexOf(CommandChars, ch);
                if (currentType == CommandType.NodeC)
                {
                    commands.Add(new Command(CommandType.NodeC, 0, currentShape));
                    currentShape = '-';
                }
            }
            else
            {
                value = TryParseDigit(ch);
                if (value < 0) throw new ArgumentException($"invalid char '{ch}'");
                if (currentType == CommandType.NodeC)
                {
                    throw new ArgumentException($"digit should not follow 'C'");
                }
                commands.Add(new Command(currentType, value, currentShape));
                currentShape = '-';
            }
        }
        return commands;
    }

    public static Complex GetNodePosition(Command cmd)
    {
        switch (cmd.Type)
        {
            case CommandType.NodeA:
            case CommandType.NodeEnd:
                return MaiGeometry.PointGroupA(cmd.Value);
            case CommandType.NodeB:
                return MaiGeometry.PointGroupB(cmd.Value);
            case CommandType.NodeE:
                return MaiGeometry.PointGroupE(cmd.Value);
            case CommandType.NodeD:
                return MaiGeometry.PointGroupD(cmd.Value);
            case CommandType.NodeF:
                return MaiGeometry.PointGroupF(cmd.Value);
            case CommandType.NodeC:
                return MaiGeometry.Center();
            default:
                throw new ArgumentException($"invalid type for node: {cmd.Type}");
        }
    }

    public static void NodeToNode(SlidePathGenerator generator, Command last, Command current)
    {
        if (Command.IsSame(last, current))
        {
            // 起点 == 终点的弧线段（如 8q8-6>6>Cb 里的 8q8、6>6）：Majdata 的 < > q p 等都是以屏幕中心
            // 为圆心的极坐标弧，起终点同角度时角度差恒为 0 → **零长度段**
            // （MajdataView Assets/Scripts/Notes/TouchSlideDrop.cs 用 Mathf.Repeat(start-end, 2π)，同点必为 0；
            //   本工程参数化路径 PolarArcSegment.ResolveAngleDelta 同理）→ 不产生任何位移，直接忽略。
            // 2026-09 一度改为 ArcToAngle(skipIfZero:false) 绕中心整圈，与制谱器表现不符
            // （用户反馈"和制谱器里呈现的效果不一样"）→ 已回退为忽略。
            return;
        }
        if (current.Shape == '-')
        {
            generator.LineToPoint(GetNodePosition(current));
        }
        else
        {
            // 弧线连接（Majdata < > ^）：极坐标弧，圆心 = 屏幕中心
            generator.PolarArcToPoint(GetNodePosition(current), current.Shape);
        }
    }

    public static void NodeToOrbit(SlidePathGenerator generator, Command last, Command current)
    {
        var isCcw = (current.Type == CommandType.OrbitCCW);
        var node = GetNodePosition(last);
        var orbit = MaiGeometry.GetCircle(current.Value);
        var diff = node - orbit.Center;
        if (Math.Abs(diff.Magnitude - orbit.Radius) < 0.1)
        {
            if (last.Type == CommandType.NodeA && current.Value == 9)
            {
                generator.TrySetLastParseMarker(ParametricSlidePath.ParseMarker.ForceAlign);
            }
            return;  // node on circle, do nothing
        }

        if (diff.Magnitude < orbit.Radius)
            throw new ArgumentException($"impossible: {last.Type}{last.Value} -> Orbit{current.Value}");
        
        generator.TangentToCircle(orbit, isCcw);
    }
    
    public static void OrbitToNode(SlidePathGenerator generator, Command last, Command current)
    {
        var isCcw = (last.Type == CommandType.OrbitCCW);
        var node = GetNodePosition(current);
        var orbit = MaiGeometry.GetCircle(last.Value);
        var diff = node - orbit.Center;
        if (Math.Abs(diff.Magnitude - orbit.Radius) < 0.1)
        {
            generator.ArcToAngle(orbit.Center, diff.Phase, isCcw, false);
            return;
        }

        if (diff.Magnitude < orbit.Radius)
            throw new ArgumentException($"impossible: Orbit{last.Value} -> {current.Type}{current.Value}");
        
        generator.ArcToTangentTowards(node, orbit.Center, isCcw);
        generator.LineToPoint(node);
    }
    
    public static void OrbitToOrbit(SlidePathGenerator generator, Command last, Command current)
    {
        if (current.Type != last.Type) throw new ArgumentException($"orbit type mismatch");

        var isCcw = (last.Type == CommandType.OrbitCCW);
        var lastOrbit = MaiGeometry.GetCircle(last.Value);
        var currentOrbit = MaiGeometry.GetCircle(current.Value);
        if (current.Value == last.Value)
        {
            generator.FullCircle(lastOrbit.Center, isCcw);
            return;
        }

        if (last.Value == 0 && current.Value == 9 || last.Value == 9 && current.Value == 0)
            throw new ArgumentException($"impossible: Orbit{last.Value} -> Orbit{current.Value}");

        if (current.Value == 9)
        {
            var data = MaiGeometry.TransferOutData(last.Value, isCcw);
            generator.ArcToAngle(lastOrbit.Center, data.Item2, isCcw, false);
            generator.ArcToAngle(data.Item1.Center, data.Item3, isCcw, false);
            generator.TrySetLastParseMarker(ParametricSlidePath.ParseMarker.SmoothAlign);
            return;
        }

        if (last.Value == 9)
        {
            var data = MaiGeometry.TransferOutData(current.Value, !isCcw);
            generator.ArcToAngle(lastOrbit.Center, data.Item3, isCcw, true);
            generator.ArcToAngle(data.Item1.Center, data.Item2, isCcw, false);
            return;
        }
        
        generator.ExternTangentTransfer(lastOrbit.Center, currentOrbit, isCcw);
    }
    
    public static ParametricSlidePath Parse(string code)
    {
        try
        {
            // Share the canonical grammar with conversion, including multi-digit
            // command groups and geometric reachability. Preserve the existing
            // path builder and touch/D/arc extensions used by older SC charts.
            if (code.All(c => "1234567890ABCPQK".Contains(c)) &&
                !SinmaiAlpha.ChartVisuals.CanonicalSlideCodeParser.TryParse(code, out _, out var codeError))
                throw new ArgumentException(codeError);
            var commands = ParseCommands(code);
            var lastCmd = commands[0];
            // 起点按首命令的节点类型取位置（A/B/E/C 均可，非 A 区启动）
            var generator = SlidePathGenerator.BeginAt(GetNodePosition(commands[0]));

            for (var i = 1; i < commands.Count; i++)
            {
                var cmd = commands[i];
                switch (cmd.Type)
                {
                    case CommandType.NodeA:
                    case CommandType.NodeB:
                    case CommandType.NodeE:
                    case CommandType.NodeD:
                    case CommandType.NodeF:
                    case CommandType.NodeC:
                    case CommandType.NodeEnd:
                        switch (lastCmd.Type)
                        {
                            case CommandType.NodeA:
                            case CommandType.NodeB:
                            case CommandType.NodeE:
                            case CommandType.NodeD:
                            case CommandType.NodeF:
                            case CommandType.NodeC:
                                NodeToNode(generator, lastCmd, cmd);
                                break;
                            case CommandType.OrbitCCW:
                            case CommandType.OrbitCW:
                                OrbitToNode(generator, lastCmd, cmd);
                                break;
                            case CommandType.NodeEnd:
                                throw new ArgumentException($"'K' should be the last command");
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                        break;
                    case CommandType.OrbitCCW:
                    case CommandType.OrbitCW:
                        switch (lastCmd.Type)
                        {
                            case CommandType.NodeA:
                            case CommandType.NodeB:
                            case CommandType.NodeE:
                            case CommandType.NodeD:
                            case CommandType.NodeF:
                            case CommandType.NodeC:
                                NodeToOrbit(generator, lastCmd, cmd);
                                break;
                            case CommandType.OrbitCCW:
                            case CommandType.OrbitCW:
                                OrbitToOrbit(generator, lastCmd, cmd);
                                break;
                            case CommandType.NodeEnd:
                                throw new ArgumentException($"'K' should be the last command");
                            default:
                                throw new ArgumentOutOfRangeException();
                        }
                        break;
                    default:
                        throw new ArgumentOutOfRangeException();
                }

                lastCmd = cmd;
            }

            return generator.GeneratePath();
        }
        catch (ArgumentException e)
        {
            var msg = $"Invalid code: {code}";
            if (e.Message != "")
            {
                msg += $", {e.Message}";
            }
            MelonLogger.Error(msg);
            return null;
        }
    }
}
