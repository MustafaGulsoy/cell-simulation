using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

// Tiny helpers for building uGUI in code. The game's own HUD is authored in the scenes; the extras added
// later (ping, minimap, mute, tutorial, leaderboard, colour picker) are created here so they need no scene
// or prefab edits and work in any scene.
public static class RuntimeUi
{
    public static readonly Color PanelColor = new Color(0.05f, 0.07f, 0.12f, 0.72f);
    public static readonly Color TextColor = new Color(1f, 1f, 1f, 0.95f);

    // The game is landscape-only (ProjectSettings), and the scenes' own canvases use 1280x720, so the
    // extras use the same reference; the old portrait 1080x1920 made them overlap the scene UI.
    public static readonly Vector2 Reference = new Vector2(1280f, 720f);

    /// <summary>A full-screen overlay canvas that scales with the screen (landscape 1280x720 reference).</summary>
    public static Canvas CreateCanvas(string name, int sortingOrder)
    {
        var go = new GameObject(name);
        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = sortingOrder;

        var scaler = go.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = Reference;
        scaler.matchWidthOrHeight = 0.5f;

        go.AddComponent<GraphicRaycaster>();
        return canvas;
    }

    public static RectTransform Rect(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchorMin = anchor;
        rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = anchoredPosition;
        rt.sizeDelta = size;
        return rt;
    }

    public static Image Panel(string name, Transform parent, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size, Color color)
    {
        var rt = Rect(name, parent, anchor, pivot, anchoredPosition, size);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;
        return img;
    }

    public static TextMeshProUGUI Label(string name, Transform parent, string text, float fontSize, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size, TextAlignmentOptions alignment)
    {
        var rt = Rect(name, parent, anchor, pivot, anchoredPosition, size);
        var tmp = rt.gameObject.AddComponent<TextMeshProUGUI>();
        tmp.text = text;
        tmp.fontSize = fontSize;
        tmp.color = TextColor;
        tmp.alignment = alignment;
        tmp.enableWordWrapping = true;
        tmp.raycastTarget = false;
        return tmp;
    }

    /// <summary>A rounded-corner-free button with a centred label. Needs an EventSystem in the scene (both game scenes have one).</summary>
    public static Button ButtonWithLabel(string name, Transform parent, string label, float fontSize, Vector2 anchor, Vector2 pivot, Vector2 anchoredPosition, Vector2 size, Color background)
    {
        var img = Panel(name, parent, anchor, pivot, anchoredPosition, size, background);
        img.raycastTarget = true;
        var button = img.gameObject.AddComponent<Button>();
        button.targetGraphic = img;

        var text = Label("Label", img.transform, label, fontSize, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, size, TextAlignmentOptions.Center);
        text.enableWordWrapping = false;
        return button;
    }

    public static bool HasEventSystem
    {
        get { return EventSystem.current != null; }
    }
}
