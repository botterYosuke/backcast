// OrderTicketWindowFrame.cs — issue #23 re-home slice (shared window-frame builder)
//
// The SINGLE source of the Order ticket floating-window FRAME (background + title bar + body),
// shared by the scene-authoring tool (BackcastWorkspaceSceneBuilder authors the adopted Order
// window) and any runtime factory spawn, so the adopted and spawned Order windows can't diverge —
// exactly the discipline StrategyEditorWindowFrame established for the editor (findings 0025 §8).
// A runtime-assembly static; it builds the frame ONLY (the caller parents/positions the root and
// injects OrderTicketView content + the title input).

using UnityEngine;
using UnityEngine.UI;

public static class OrderTicketWindowFrame
{
    public const float TitleHeight = 28f;
    static readonly Color BodyColor = new Color(0.13f, 0.14f, 0.17f, 0.98f);
    static readonly Color TitleColor = new Color(0.30f, 0.24f, 0.14f, 1f);   // amber-ish (order accent, findings 0020)

    public static RectTransform Build(string id, out FloatingWindowTitleInput titleInput, out RectTransform body)
    {
        var rootGo = new GameObject("Window_" + id, typeof(RectTransform), typeof(Image));
        var root = (RectTransform)rootGo.transform;
        rootGo.GetComponent<Image>().color = BodyColor;   // raycast target: ticket body is interactive

        var titleGo = new GameObject("TitleBar", typeof(RectTransform), typeof(Image), typeof(FloatingWindowTitleInput));
        var titleRt = (RectTransform)titleGo.transform;
        titleRt.SetParent(root, false);
        titleRt.anchorMin = new Vector2(0f, 1f); titleRt.anchorMax = new Vector2(1f, 1f); titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.offsetMin = new Vector2(0f, -TitleHeight); titleRt.offsetMax = Vector2.zero;
        titleGo.GetComponent<Image>().color = TitleColor;
        titleInput = titleGo.GetComponent<FloatingWindowTitleInput>();

        var labelGo = new GameObject("Title", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(titleRt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = new Vector2(8f, 0f); lrt.offsetMax = new Vector2(-8f, 0f);
        var lt = labelGo.GetComponent<Text>();
        lt.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        lt.fontSize = 12; lt.color = new Color(0.92f, 0.92f, 0.94f, 1f);
        lt.alignment = TextAnchor.MiddleLeft; lt.text = "ORDER (manual)"; lt.raycastTarget = false;

        var bodyGo = new GameObject("Body", typeof(RectTransform));
        body = (RectTransform)bodyGo.transform;
        body.SetParent(root, false);
        body.anchorMin = Vector2.zero; body.anchorMax = Vector2.one;
        body.offsetMin = new Vector2(4f, 4f); body.offsetMax = new Vector2(-4f, -(TitleHeight + 2f));

        return root;
    }
}
