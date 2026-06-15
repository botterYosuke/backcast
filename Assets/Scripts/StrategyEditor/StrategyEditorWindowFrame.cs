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
    static readonly Color BodyColor = new Color(0.13f, 0.14f, 0.17f, 0.98f);
    static readonly Color TitleColor = new Color(0.24f, 0.27f, 0.34f, 1f);

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
}
