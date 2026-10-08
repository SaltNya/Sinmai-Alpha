using UnityEngine;

namespace SinmaiAlpha.Notes;

// Capture animated sprite coordinates before ChartFrameMotion temporarily
// moves the rendered frame. The clip then follows the same MOVE/ROTATE/ZOOM
// as the circle, independent of camera viewport, resolution or player count.
[DefaultExecutionOrder(9999)]
public sealed class FireworkPlayfieldClip : MonoBehaviour
{
    private static readonly int ClipX = Shader.PropertyToID("_PlayfieldClipX");
    private static readonly int ClipY = Shader.PropertyToID("_PlayfieldClipY");
    private RectTransform playfield;
    private SpriteRenderer[] renderers;
    private Material[] materials;

    internal void Bind(RectTransform field, SpriteRenderer[] sprites, Material[] ownedMaterials)
    {
        playfield = field;
        renderers = sprites;
        materials = ownedMaterials;
        Refresh();
    }

    private void LateUpdate() => Refresh();

    internal void Refresh()
    {
        if (materials == null) return;
        var rect = playfield != null ? playfield.rect : default;
        var radius = Mathf.Min(rect.width, rect.height) * .5f;
        var valid = playfield != null && radius > .001f;
        // A missing/destroyed playfield must never expose an unmasked effect.
        var fieldToCircle = valid
            ? Matrix4x4.Scale(new Vector3(1 / radius, 1 / radius, 1)) *
              Matrix4x4.Translate(new Vector3(-rect.center.x, -rect.center.y, 0)) * playfield.worldToLocalMatrix
            : Matrix4x4.identity;
        for (var i = 0; i < materials.Length; i++)
        {
            if (materials[i] == null || renderers[i] == null) continue;
            var spriteToCircle = fieldToCircle * renderers[i].localToWorldMatrix;
            materials[i].SetVector(ClipX, valid ? spriteToCircle.GetRow(0) : new Vector4(0, 0, 0, 2));
            materials[i].SetVector(ClipY, valid ? spriteToCircle.GetRow(1) : Vector4.zero);
        }
    }
}
