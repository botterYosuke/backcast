// StrategyEditorWindowFrame.cs — issue #59 "Backcast workspace root" (shared window-frame builder)
//
// The SINGLE source of the Strategy Editor floating-window FRAME (background + title bar + body),
// shared by the scene-authoring tool (BackcastWorkspaceSceneBuilder authors the adopted window) and
// the runtime factory (BackcastWorkspaceRoot.BuildEditorWindowFrame spawns ADDITIONAL saved windows),
// so a tweak to title-bar height / colors / body insets can't make the adopted and spawned editors
// diverge (findings 0025 §8). A runtime-assembly static (the Editor tool references runtime). It
// builds the frame ONLY — the caller parents/positions the root and wires the title input + content.

using UnityEngine;
using UnityEngine.UI;

public static class StrategyEditorWindowFrame
{
    public const float TitleHeight = 28f;
    // Cyberpunk re-skin 2026-06-20: body = Neutral.Step3 (#131840), title = Accent.Step8 purple
    // (#b14aed) so the editor frame glows with the brand purple. TODO: route via ThemeService.
    static readonly Color BodyColor = new Color(0.0745f, 0.0941f, 0.2510f, 0.98f);
    static readonly Color TitleColor = new Color(0.6941f, 0.2902f, 0.9294f, 1f);

    // Build a window-frame subtree rooted at a new GameObject (NOT parented/positioned — the caller
    // owns that). `titleInput` is the title bar's FloatingWindowTitleInput (the caller Initialize()s
    // it); `body` is the content region under the title bar (the caller injects editor content).
    public static RectTransform Build(string id, out FloatingWindowTitleInput titleInput, out RectTransform body)
    {
        var rootGo = new GameObject("Window_" + id, typeof(RectTransform), typeof(Image));
        var root = (RectTransform)rootGo.transform;
        rootGo.GetComponent<Image>().color = BodyColor;   // raycast target: editor body is interactive

        var titleGo = new GameObject("TitleBar", typeof(RectTransform), typeof(Image), typeof(FloatingWindowTitleInput));
        var titleRt = (RectTransform)titleGo.transform;
        titleRt.SetParent(root, false);
        titleRt.anchorMin = new Vector2(0f, 1f); titleRt.anchorMax = new Vector2(1f, 1f); titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.offsetMin = new Vector2(0f, -TitleHeight); titleRt.offsetMax = Vector2.zero;
        titleGo.GetComponent<Image>().color = TitleColor;
        titleInput = titleGo.GetComponent<FloatingWindowTitleInput>();

        var bodyGo = new GameObject("Body", typeof(RectTransform));
        body = (RectTransform)bodyGo.transform;
        body.SetParent(root, false);
        body.anchorMin = Vector2.zero; body.anchorMax = Vector2.one;
        body.offsetMin = new Vector2(4f, 4f); body.offsetMax = new Vector2(-4f, -(TitleHeight + 2f));

        return root;
    }

    // #81 cell-as-floating-window: the title-bar X that deletes the cell (marimo per-cell delete,
    // ADR-0013 Decision 4). IDEMPOTENT find-or-create so it works on BOTH the adopted scene-authored
    // window (which the serialized scene predates) and a runtime-spawned window WITHOUT diverging —
    // the caller wires onClick to NotebookCellCoordinator.DeleteCell(id). The button sits in the
    // title bar, top-right. Returns the Button (existing or newly created); null for a null root.
    const string CloseButtonName = "CloseButton";

    public static Button EnsureCloseButton(RectTransform windowRoot, Font font)
    {
        if (windowRoot == null) return null;

        var titleBar = FindChild(windowRoot, "TitleBar") ?? windowRoot;
        var existing = FindChild(titleBar, CloseButtonName);
        if (existing != null)
        {
            var b = existing.GetComponent<Button>();
            if (b != null) return b;
        }

        var btnGo = new GameObject(CloseButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
        var btnRt = (RectTransform)btnGo.transform;
        btnRt.SetParent(titleBar, false);
        // top-right inset square within the title bar.
        btnRt.anchorMin = new Vector2(1f, 1f); btnRt.anchorMax = new Vector2(1f, 1f); btnRt.pivot = new Vector2(1f, 1f);
        float side = TitleHeight - 6f;
        btnRt.sizeDelta = new Vector2(side, side);
        btnRt.anchoredPosition = new Vector2(-3f, -3f);
        btnGo.GetComponent<Image>().color = new Color(1.0000f, 0.1569f, 0.3333f, 1f); // #ff2855 hot pink-red close (cyberpunk)

        var label = new GameObject("X", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        var labelRt = (RectTransform)label.transform;
        labelRt.SetParent(btnRt, false);
        labelRt.anchorMin = Vector2.zero; labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero; labelRt.offsetMax = Vector2.zero;
        var t = label.GetComponent<Text>();
        t.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.text = "✕";   // ✕
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.fontSize = 14;

        return btnGo.GetComponent<Button>();
    }

    static RectTransform FindChild(RectTransform parent, string name)
    {
        if (parent == null) return null;
        for (int i = 0; i < parent.childCount; i++)
        {
            var c = parent.GetChild(i);
            if (c.name == name) return c as RectTransform;
        }
        return null;
    }
}
