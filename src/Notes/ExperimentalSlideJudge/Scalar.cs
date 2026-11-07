using System;

namespace SinmaiAlpha.Notes.ExperimentalSlideJudge
{
    // The game runs on .NET Framework/Mono. Keep the port independent of the
    // prototype's .NET 10 conveniences, without changing the original helpers.
    internal static class Scalar
    {
        internal const double Tau = 2 * Math.PI;
        internal static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);
        internal static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(max, value));
    }
}

#if NETFRAMEWORK
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit { }
}
#endif
