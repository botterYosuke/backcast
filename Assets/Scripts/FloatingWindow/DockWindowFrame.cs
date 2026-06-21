// DockWindowFrame.cs — #99 Slice 3 / ADR-0017 / findings 0075 §7 (shared window-frame builder)
//
// The SINGLE source of the dock-kind floating-window FRAME (background + title bar + title text +
// body), shared by every dock window the BackcastWorkspaceRoot spawns: chart / buying_power /
// orders / positions / run_result / startup. ONE frame builder so an accent tweak or title-bar
// height change can't make the 6 dock kinds diverge — exactly the discipline
// StrategyEditorWindowFrame established for the editor (findings 0025 §8) and
// OrderTicketWindowFrame for the Order ticket. Title color is the SPEC ACCENT (PlayerColors,
// findings 0020); body color follows the Hakoniwa panel surface so the dock cluster reads as
// a coherent constellation against the deep-cosmos workspace field. closeable=false on the
// catalog → NO close button rendered here (the dock kinds are workspace-owned; mode poll +
// View menu govern visibility, not a per-window X).

using UnityEngine;
using UnityEngine.UI;

public static class DockWindowFrame
{
    public const float TitleHeight = 28f;

    // Build a window-frame subtree rooted at a new GameObject (NOT parented/positioned — the caller
    // owns that, same shape as StrategyEditorWindowFrame.Build). `titleInput` is the title bar's
    // FloatingWindowTitleInput (the caller Initialize()s it); `body` is the content region under
    // the title bar (the caller injects the dock-kind content — ChartView + DepthLadderView for
    // chart, ScenarioStartupTile for startup, LivePanelTileView for the 4 base panels).
    public static RectTransform Build(string id, string title, Color accent, Font font,
                                       out FloatingWindowTitleInput titleInput, out RectTransform body)
    {
        var rootGo = new GameObject("Window_" + id, typeof(RectTransform), typeof(Image));
        var root = (RectTransform)rootGo.transform;
        // Body color: the Hakoniwa panel surface so dock windows read as illuminated cards on the
        // deep-cosmos field (theme: hakoniwa_panel_surface — re-applied at runtime by the theme
        // subscription; this bake is the editor preview value).
        rootGo.GetComponent<Image>().color = ThemeService.Current.colors.hakoniwa_panel_surface;

        var titleGo = new GameObject("TitleBar", typeof(RectTransform), typeof(Image), typeof(FloatingWindowTitleInput));
        var titleRt = (RectTransform)titleGo.transform;
        titleRt.SetParent(root, false);
        titleRt.anchorMin = new Vector2(0f, 1f); titleRt.anchorMax = new Vector2(1f, 1f); titleRt.pivot = new Vector2(0.5f, 1f);
        titleRt.offsetMin = new Vector2(0f, -TitleHeight); titleRt.offsetMax = Vector2.zero;
        titleGo.GetComponent<Image>().color = accent;        // SPEC-DRIVEN accent (findings 0020)
        titleInput = titleGo.GetComponent<FloatingWindowTitleInput>();

        var labelGo = new GameObject("Title", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(titleRt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one;
        lrt.offsetMin = new Vector2(8f, 0f); lrt.offsetMax = new Vector2(-8f, 0f);
        var lt = labelGo.GetComponent<Text>();
        lt.font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        lt.fontSize = 12;
        lt.color = Color.white;
        lt.alignment = TextAnchor.MiddleLeft;
        lt.text = title ?? "";
        lt.raycastTarget = false;          // title text never blocks the drag input

        var bodyGo = new GameObject("Body", typeof(RectTransform));
        body = (RectTransform)bodyGo.transform;
        body.SetParent(root, false);
        body.anchorMin = Vector2.zero; body.anchorMax = Vector2.one;
        body.offsetMin = new Vector2(4f, 4f); body.offsetMax = new Vector2(-4f, -(TitleHeight + 2f));

        HudFrameChrome.Decorate(root);   // cyan HUD edge glow + corner brackets (shared chrome)
        return root;
    }
}
