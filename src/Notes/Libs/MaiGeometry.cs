using System;
using System.Numerics;

namespace SinmaiAlpha.Notes.Libs;

public static class MaiGeometry
{
    public struct CircleStruct(Complex center, double radius)
    {
        public Complex Center = center;
        public double Radius = radius;
    }

    public static readonly double CanvasWidth = 1080.0;
    public static readonly double MainRadius  = 480.0;
    public static readonly double CenterRadius = MainRadius * Math.Cos(Math.PI * 3 / 8);
    public static readonly double GroupBRadius = CenterRadius / Math.Cos(Math.PI / 8);

    // E 环（Majdata E 区）：半径 3.0×105 ≈ 315，位于 A(440)/B(210) 之间，角度错开 22.5°（在 A/B 位之间）
    public static readonly double ERadius = 315.0;

    // D 环（Majdata D 区 touch）：半径 4.1×105 = 430.5，与 A 环同半径、角度与 E 区一致（错开 22.5°）
    public static readonly double DRadius = 430.5;

    private static readonly double _b = Math.Cos(Math.PI / 8) / 2;
    private static readonly double _a = 1 - _b;
    private static readonly double _theta = Math.PI / 4;
    private static readonly double _s = (_a * _a + _b * _b - 2 * _a * _b * Math.Cos(_theta)) /
                                        (2 * _a - 2 * _b * Math.Cos(_theta));
    
    public static readonly double PPQQRadius = MainRadius * _b;
    public static readonly double TransferRadius = MainRadius * (_b + _s);
    public static readonly double EdgeTransferAngle = _theta;
    public static readonly double PPQQTransferAngle =
        Math.Acos((_s * _s + _b * _b - (_a - _s) * (_a - _s)) / (2 * _b * _s));
    
    public static readonly double DefaultDistance = MainRadius * Math.PI / 32;

    public static readonly int[,] MirrorInfo = new int[4, 17]
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 }, // Normal
        { 7, 6, 5, 4, 3, 2, 1, 0, 15, 14, 13, 12, 11, 10, 9, 8, 16 }, // L <-> R
        { 3, 2, 1, 0, 7, 6, 5, 4, 11, 10, 9, 8, 15, 14, 13, 12, 16 }, // U <-> D
        { 4, 5, 6, 7, 0, 1, 2, 3, 12, 13, 14, 15, 8, 9, 10, 11, 16 } // rotate 180 deg
    };

    /// <summary>
    /// Note: idx is 1-based, not 0-based
    /// </summary>
    public static Complex PointGroupA(int idx)
    {
        var angle = Math.PI * (5.0 / 8.0 - idx / 4.0);
        return Complex.FromPolarCoordinates(MainRadius, angle);
    }
    
    /// <summary>
    /// Note: idx is 1-based, not 0-based
    /// </summary>
    public static Complex PointGroupB(int idx)
    {
        var angle = Math.PI * (5.0 / 8.0 - idx / 4.0);
        return Complex.FromPolarCoordinates(GroupBRadius, angle);
    }

    /// <summary>
    /// E 环节点（Majdata E 区）：半径 ERadius，角度错开 A/B 位 22.5°。
    /// Note: idx is 1-based, not 0-based
    /// </summary>
    public static Complex PointGroupE(int idx)
    {
        var angle = Math.PI * (3.0 / 4.0 - idx / 4.0);
        return Complex.FromPolarCoordinates(ERadius, angle);
    }

    /// <summary>
    /// D 环节点（Majdata D 区 touch）：半径 DRadius（与 A 环相同 4.1），角度与 E 区一致。
    /// Note: idx is 1-based, not 0-based
    /// </summary>
    public static Complex PointGroupD(int idx)
    {
        var angle = Math.PI * (3.0 / 4.0 - idx / 4.0);
        return Complex.FromPolarCoordinates(DRadius, angle);
    }

    /// <summary>
    /// F 环节点（A 区触摸区，位于相邻环键之间——"两键中间起点"）：半径同按键环 MainRadius，
    /// 角度在 PointGroupA(idx) 与 PointGroupA(idx+1) 正中间（错开 22.5°）。
    /// Note: idx is 1-based, not 0-based
    /// </summary>
    public static Complex PointGroupF(int idx)
    {
        var angle = Math.PI * (5.0 / 8.0 - idx / 4.0) - Math.PI / 8.0;
        return Complex.FromPolarCoordinates(MainRadius, angle);
    }
    
    public static Complex Center()
    {
        return Complex.Zero;
    }

    /// <summary>
    /// idx 0 is center circle, idx 1~8 are ppqq circles, idx 9 is outer circle
    /// </summary>
    public static CircleStruct GetCircle(int idx)
    {
        if (idx == 0)
        {
            return new CircleStruct(Complex.Zero, CenterRadius);
        }

        if (idx == 9)
        {
            return new CircleStruct(Complex.Zero, MainRadius);
        }

        var angle = Math.PI * (3.0 / 4.0 - idx / 4.0);
        var center = Complex.FromPolarCoordinates(PPQQRadius, angle);
        return new CircleStruct(center, PPQQRadius);
    }

    /// <summary>
    /// Note: idx is 1-based, not 0-based
    /// </summary>
    /// <returns>CircleStruct TransferCircle, double TransferStartAngle, double TransferEndAngle</returns>
    public static Tuple<CircleStruct, double, double> TransferOutData(int idx, bool isccw)
    {
        var ppqqRad = Math.PI * (3.0 / 4.0 - idx / 4.0);
        double startAngle, endAngle;
        if (isccw)
        {
            startAngle = ppqqRad - PPQQTransferAngle;
            endAngle = ppqqRad + EdgeTransferAngle;
        }
        else
        {
            startAngle = ppqqRad + PPQQTransferAngle;
            endAngle = ppqqRad - EdgeTransferAngle;
        }
        var d = MainRadius - TransferRadius;
        var center = Complex.FromPolarCoordinates(d, endAngle);
        return new Tuple<CircleStruct, double, double>(new CircleStruct(center, TransferRadius),
            Math.IEEERemainder(startAngle, Math.PI * 2), Math.IEEERemainder(endAngle, Math.PI * 2));
    }
}
