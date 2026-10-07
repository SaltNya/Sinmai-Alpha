using System.Collections.Generic;
using HarmonyLib;
using Monitor;
using SinmaiAlpha.Compatibility;
using SinmaiAlpha.Hosting;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace SinmaiAlpha.Notes;

public partial class CustomNoteTypes
{
    private static readonly Dictionary<GameMonitor, ChartWatermark> Watermarks = new();

    private static void ApplyWatermark(GameMonitor monitor)
    {
        if (GuiSizes.SinglePlayer && monitor.MonitorIndex != 0 || Watermarks.ContainsKey(monitor)) return;
        var main = Traverse.Create(monitor).Field("Main").GetValue<CanvasGroup>();
        if (main == null) return;
        var display = monitor.gameObject.AddComponent<ChartWatermark>();
        display.Bind(monitor, main);
        Watermarks.Add(monitor, display);
    }

    private static void ReleaseWatermarks()
    {
        foreach (var display in Watermarks.Values)
            if (display != null) { display.Stop(); Object.Destroy(display); }
        Watermarks.Clear();
    }
}

// 独立屏幕 UI，不参与谱面运动、滤镜、亮度和音符判定。
public sealed class ChartWatermark : MonoBehaviour
{
    internal const string Credit = "Sinmai-Alpha Mod by SaltNya";
    private GameMonitor owner;
    private CanvasGroup main;
    private Canvas sourceCanvas;
    private Camera sourceCamera;
    private RawImage adjustedMain, adjustedSub;
    private GameObject overlay;
    private TextMeshProUGUI label;
    private readonly Vector3[] corners = new Vector3[4];

    internal void Bind(GameMonitor monitor, CanvasGroup group)
    {
        owner = monitor; main = group;
        sourceCanvas = group.GetComponentInParent<Canvas>();
        if (sourceCanvas != null) sourceCanvas = sourceCanvas.rootCanvas;
        sourceCamera = sourceCanvas != null && sourceCanvas.renderMode == RenderMode.ScreenSpaceOverlay
            ? null : sourceCanvas != null && sourceCanvas.worldCamera != null ? sourceCanvas.worldCamera : Camera.main;
        adjustedMain = ScreenPositionAdjust.DisplayImage(monitor.MonitorIndex, false);
        adjustedSub = ScreenPositionAdjust.DisplayImage(monitor.MonitorIndex, true);
        var font = ReferenceSubtitleDisplay.NativeFont(group);
        if (font == null) return;
        overlay = new GameObject("Sinmai-Alpha Credit", typeof(RectTransform), typeof(Canvas));
        var canvas = overlay.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 32765;
        canvas.targetDisplay = sourceCanvas != null ? sourceCanvas.targetDisplay : 0;
        var textObject = new GameObject(Credit, typeof(RectTransform));
        textObject.transform.SetParent(overlay.transform, false);
        label = textObject.AddComponent<TextMeshProUGUI>();
        label.font = font; label.text = Credit; label.fontSize = 18;
        label.color = new Color(.85f, .85f, .85f, .75f);
        label.alignment = TextAlignmentOptions.TopLeft;
        label.enableWordWrapping = false; label.richText = false; label.raycastTarget = false;
        label.rectTransform.anchorMin = label.rectTransform.anchorMax = Vector2.zero;
        label.rectTransform.pivot = new Vector2(0, 1);
        label.enabled = false;
    }

    // 游戏原生纵向画布为 1080x1920，顶端信息区高 450；与用户红框对应。
    internal static Rect Place(Rect frame, bool mainOnly)
    {
        var scale = frame.width / 1080f;
        return new Rect(frame.xMin + 8 * scale,
            frame.yMax - (mainOnly ? 6 * scale : frame.height * 450f / 1920f) - 28 * scale,
            360 * scale, 28 * scale);
    }

    private Rect ScreenBounds(RectTransform rect, Camera camera)
    {
        rect.GetWorldCorners(corners);
        var min = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (var corner in corners)
        {
            var point = RectTransformUtility.WorldToScreenPoint(camera, corner);
            min = Vector2.Min(min, point); max = Vector2.Max(max, point);
        }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private Rect ImageBounds(RawImage image)
    {
        var canvas = image.canvas;
        return ScreenBounds(image.rectTransform, canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null);
    }

    private void LateUpdate()
    {
        if (label == null) return;
        label.enabled = false;
        if (owner == null || main == null || !owner.gameObject.activeInHierarchy ||
            !main.gameObject.activeInHierarchy || main.alpha <= 0 || !CustomNoteTypes.FeaturesForMonitor(owner.MonitorIndex) ||
            GuiSizes.SinglePlayer && owner.MonitorIndex != 0) return;
        var mainOnly = GuiSizes.SinglePlayer && SinglePlayer.HideSubMonitor;
        Rect placement;
        if (!mainOnly && adjustedSub != null && adjustedSub.isActiveAndEnabled)
        {
            var banner = ImageBounds(adjustedSub);
            var scale = banner.width / 1080f;
            placement = new Rect(banner.xMin + 8 * scale, banner.yMin - 28 * scale, 360 * scale, 28 * scale);
        }
        else if (adjustedMain != null && adjustedMain.isActiveAndEnabled)
            placement = Place(ImageBounds(adjustedMain), true);
        else
        {
            var frame = !mainOnly && sourceCanvas != null ? sourceCanvas.transform as RectTransform : main.transform as RectTransform;
            if (frame == null) return;
            placement = Place(ScreenBounds(frame, sourceCamera), mainOnly);
        }
        if (placement.width <= 0 || placement.height <= 0) return;
        var rect = label.rectTransform;
        rect.anchoredPosition = new Vector2(placement.xMin, placement.yMax);
        rect.sizeDelta = placement.size;
        label.fontSize = 18 * placement.width / 360f;
        label.enabled = true;
    }

    private void OnDisable() { if (label != null) label.enabled = false; }
    internal void Stop()
    {
        enabled = false;
        if (overlay != null) { overlay.SetActive(false); Object.Destroy(overlay); overlay = null; }
    }
    private void OnDestroy() => Stop();
}
