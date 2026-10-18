using UnityEngine;
namespace SinmaiAlpha.Notes.Libs;
// This regression covers touch corners, lines, and arcs. Native prefab tangent
// templates require Unity and are deliberately not simulated by this harness.
internal static class MajdataRegularPath
{
    internal static Vector3[] GetTangentTemplate(int start, int end, string shape)
        => throw new NotSupportedException("This harness does not cover tangent templates");
}
