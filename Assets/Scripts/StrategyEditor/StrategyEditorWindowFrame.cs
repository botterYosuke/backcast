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
    // Space re-skin 2026-06-20: body = Neutral.Step3 (#11162b), title = Accent.Step8 Neptune-storm
    // violet (#6a4eb3) — muted brand violet without the cyberpunk scream. TODO: route via ThemeService.
    static readonly Color BodyColor = new Color(0.0667f, 0.0863f, 0.1686f, 0.98f);
    static readonly Color TitleColor = new Color(0.4157f, 0.3059f, 0.7020f, 1f);

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
        btnGo.GetComponent<Image>().color = new Color(0.8510f, 0.3882f, 0.2627f, 1f); // #d96343 mars-rust close (space re-skin)

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

    // #95 Phase 2 土台 (ADR-0016 D2): the per-cell ▶ RUN button — runs THIS cell + reactive
    // downstream as pure computation, output in the window. IDEMPOTENT find-or-create (same shape
    // as EnsureCloseButton) so BOTH the adopted scene-authored window (region_001) and a runtime-
    // spawned window (region_002+) get a consistent ▶ WITHOUT diverging — the caller wires onClick
    // to NotebookRunController.RunCell(regionId) (via BackcastWorkspaceRoot.WireCellRunButton). Sits
    // in the title bar, just LEFT of the X.
    // Returns the Button (existing or newly created); null for a null root.
    const string RunButtonName = "CellRunButton";

    public static Button EnsureRunButton(RectTransform windowRoot, Font font)
    {
        if (windowRoot == null) return null;

        var titleBar = FindChild(windowRoot, "TitleBar") ?? windowRoot;
        var existing = FindChild(titleBar, RunButtonName);
        if (existing != null)
        {
            var b = existing.GetComponent<Button>();
            if (b != null) return b;
        }

        var btnGo = new GameObject(RunButtonName, typeof(RectTransform), typeof(Image), typeof(Button));
        var btnRt = (RectTransform)btnGo.transform;
        btnRt.SetParent(titleBar, false);
        // top-right inset square, one slot to the LEFT of the X close button.
        btnRt.anchorMin = new Vector2(1f, 1f); btnRt.anchorMax = new Vector2(1f, 1f); btnRt.pivot = new Vector2(1f, 1f);
        float side = TitleHeight - 6f;
        btnRt.sizeDelta = new Vector2(side, side);
        btnRt.anchoredPosition = new Vector2(-(side + 6f), -3f);   // X is at -3; clear it by side+gap
        btnGo.GetComponent<Image>().color = new Color(0.2275f, 0.6078f, 0.3608f, 1f); // #3a9b5c run-green

        var label = new GameObject("RunGlyph", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        var labelRt = (RectTransform)label.transform;
        labelRt.SetParent(btnRt, false);
        labelRt.anchorMin = Vector2.zero; labelRt.anchorMax = Vector2.one;
        labelRt.offsetMin = Vector2.zero; labelRt.offsetMax = Vector2.zero;
        var t = label.GetComponent<Text>();
        t.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        t.text = "▶";   // ▶
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.fontSize = 12;

        return btnGo.GetComponent<Button>();
    }

    // #95 Phase 4 (ADR-0016 D9 / findings 0073 §2 — owner HITL: ship the stop affordance): while a
    // bt.replay() backtest runs, the cell's ▶ becomes a red ■ (stop). The caller re-points onClick
    // to StopRunning while running and back to RunCell when idle.
    public static void SetRunButtonGlyph(Button runButton, bool running)
    {
        if (runButton == null) return;
        var glyph = FindChild((RectTransform)runButton.transform, "RunGlyph");
        var t = glyph != null ? glyph.GetComponent<Text>() : null;
        if (t != null) t.text = running ? "■" : "▶";
        var img = runButton.GetComponent<Image>();
        if (img != null)
            img.color = running
                ? new Color(0.78f, 0.30f, 0.30f, 1f)        // #c74d4d stop-red while running
                : new Color(0.2275f, 0.6078f, 0.3608f, 1f); // #3a9b5c run-green when idle
    }

    // #95 Phase 6 Slice 3 (findings 0075 P6-1 / ANCHOR self-decision): the THIRD run-button state —
    // STALE. A cell whose code was edited (or whose upstream went stale) but not yet re-pressed shows
    // an amber ▶ (marimo's "needs a run" cue). Driven by a SEPARATE signal from `running` (they are
    // mutually exclusive — a running ■ cell is not in the stale set), so this never touches the glyph
    // text and only re-tints: amber while stale, run-green when clean/idle. The caller must NOT call
    // this on a cell currently showing ■ (running owns the tint there).
    public static void SetRunButtonStale(Button runButton, bool stale)
    {
        if (runButton == null) return;
        var img = runButton.GetComponent<Image>();
        if (img == null) return;
        img.color = stale
            ? new Color(0.8510f, 0.6157f, 0.2078f, 1f)   // #d99d35 stale-amber: edited, needs a press
            : new Color(0.2275f, 0.6078f, 0.3608f, 1f);  // #3a9b5c run-green when clean/idle
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
