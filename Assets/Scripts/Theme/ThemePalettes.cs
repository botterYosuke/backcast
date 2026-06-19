// ThemePalettes.cs — issue #44 "theme（配色）システム"
//
// PORT of TTWR ThemeColors / StatusColors / SyntaxColors / PlayerColors / Appearance
// (theme/mod.rs). FromScales(...) is the SINGLE source of truth for the scale-step →
// semantic-role mapping; swapping the ColorScales it receives (dark ↔ light) re-derives
// every role. Full role set is defined even where backcast has no consumer yet (parity
// completeness — findings 0020); the wired subset is in ScenarioStartupTile / syntax /
// editor / floating-window / chart / ladder.

using UnityEngine;

// Light / dark label. NOT a behavioural switch — like TTWR, nothing branches on it; it just
// records "which variant is active" for #43 to read/persist.
public enum Appearance { Dark, Light }

// -- ThemeColors (54 semantic UI roles, field naming mirrors Zed's ThemeColors) -----------
public sealed class ThemeColors
{
    public Color background, surface_background, elevated_surface_background, panel_background;
    // workspace_background — the infinite-canvas FIELD behind every Hakoniwa tile / floating window
    // (BackcastWorkspaceRoot._viewport). NOT a Radix scale step: a deliberate owner override (2026-06-19,
    // same流儀 as the AddCellButton bottom-right divergence) so the empty space reads light-blue while
    // the content surfaces keep `background` (dark). Kept distinct from `background` precisely so changing
    // the field hue does NOT lighten chart / ladder / editor backgrounds.
    public Color workspace_background;
    // hakoniwa_* — the Hakoniwa-ISOLATED surface roles (findings 0054). The chart / ladder / panel
    // tiles and the tile chrome (card / header / label) read THESE, not the shared `background` /
    // `panel_background`, so brightening Hakoniwa does NOT lighten the strategy editor / footer /
    // sidebar (which keep the dark shared roles). All owner literals (raw sRGB, scale-non-derived,
    // same流儀 as workspace_background) so a future light scale can't silently recolor them; swap
    // these literals + Play to try a new palette (no scene re-bake).
    public Color hakoniwa_root_background, hakoniwa_tile_background, hakoniwa_tile_header;
    public Color hakoniwa_chart_background, hakoniwa_panel_surface;
    public Color hakoniwa_tile_header_text, hakoniwa_text, hakoniwa_text_muted;
    // hakoniwa trading colors (findings 0054 P1): the `status.*` green/red/warning are dark-scale steps
    // tuned for a DARK bg and wash out on the bright/cream Hakoniwa surfaces (WCAG ~1.3–2.5:1). These are
    // darker, cream-legible crop-green / barn-red / dark-amber used by ChartView candles + change% and the
    // ladder bid/ask/LAST — Hakoniwa-isolated so the rest of the app keeps semantic status.* unchanged.
    public Color hakoniwa_up, hakoniwa_down, hakoniwa_last;
    public Color panel_focused_border, status_bar_background, title_bar_background;
    public Color toolbar_background, tab_bar_background, tab_active_background, tab_inactive_background;
    public Color border, border_variant, border_focused, border_selected, border_disabled, border_transparent;
    public Color text, text_muted, text_placeholder, text_disabled, text_accent;
    public Color element_background, element_hover, element_active, element_selected, element_disabled, element_selection_background;
    public Color ghost_element_background, ghost_element_hover, ghost_element_active, ghost_element_selected, ghost_element_disabled;
    public Color accent, accent_hover;
    public Color icon, icon_muted, icon_disabled, icon_accent, icon_placeholder;
    public Color drop_target_background, drop_target_border;
    public Color search_match_background, search_active_match_background;
    public Color scrollbar_thumb_background, scrollbar_thumb_hover_background, scrollbar_thumb_active_background, scrollbar_track_background;
    public Color gutter_background, line_number, line_number_active;
    public Color modal_background, notification_background, drag_overlay_background;

    public static ThemeColors FromScales(ColorScales s)
    {
        var n = s.neutral; var a = s.accent; var y = s.yellow;
        return new ThemeColors
        {
            background = n.Step1,
            workspace_background = new Color(0.4980f, 0.6431f, 0.7451f), // #7fa4be — owner-chosen field hue (raw sRGB, findings 0020)
            // hakoniwa FARM palette (findings 0054) — inspired by "The Farmer Was Replaced": the box-garden
            // reads like a farm plot. grass-green field behind tiles; soil-brown tile headers/frames; light
            // tilled-earth + cream surfaces so charts/panels stay legible (the dark-scale status green/red read
            // like crops on cream); dark-soil text. NOT near-white (washes out ladder bid/ask). Swap + Play to tune.
            hakoniwa_root_background  = new Color(0.4157f, 0.6078f, 0.2549f), // #6a9b41 — grass-green field (ground tiles sit on)
            hakoniwa_tile_background  = new Color(0.8902f, 0.8353f, 0.6902f), // #e3d5b0 — tilled-earth tile card (panel bg)
            hakoniwa_tile_header      = new Color(0.5412f, 0.3843f, 0.2235f), // #8a6239 — soil-brown header bar / frame
            hakoniwa_chart_background = new Color(0.9373f, 0.9059f, 0.8235f), // #efe7d2 — cream crop face (chart + ladder)
            hakoniwa_panel_surface    = new Color(0.9216f, 0.8824f, 0.7843f), // #ebe1c8 — warm cream (startup tile)
            hakoniwa_tile_header_text = new Color(0.9529f, 0.9255f, 0.8471f), // #f3ecd8 — cream label on soil-brown header
            hakoniwa_text             = new Color(0.1843f, 0.1490f, 0.0863f), // #2f2616 — dark-soil text on cream
            hakoniwa_text_muted       = new Color(0.4353f, 0.3686f, 0.2471f), // #6f5e3f — mid-brown axes / change% / ladder header
            hakoniwa_up               = new Color(0.1804f, 0.4314f, 0.1922f), // #2e6e31 — crop-green: candle up / change% gain / ladder bid (cream-legible)
            hakoniwa_down             = new Color(0.6275f, 0.1765f, 0.1216f), // #a02d1f — barn-red: candle down / change% loss / ladder ask
            hakoniwa_last             = new Color(0.4784f, 0.3529f, 0.0706f), // #7a5a12 — dark-amber: ladder LAST marker
            surface_background = n.Step2,
            elevated_surface_background = n.Step3,
            panel_background = n.Step2,
            panel_focused_border = a.Step8,
            status_bar_background = n.Step2,
            title_bar_background = n.Step2,
            toolbar_background = n.Step2,
            tab_bar_background = n.Step2,
            tab_active_background = n.Step4,
            tab_inactive_background = n.Step2,

            border = n.Step6,
            border_variant = n.Step7,
            border_focused = a.Step8,
            border_selected = a.Step7,
            border_disabled = n.Step5,
            border_transparent = Color.clear,

            text = n.Step12,
            text_muted = n.Step11,
            text_placeholder = n.Step9,
            text_disabled = n.Step8,
            text_accent = a.Step11,

            element_background = n.Step3,
            element_hover = n.Step4,
            element_active = n.Step5,
            element_selected = a.Step5,
            element_disabled = n.Step3,
            element_selection_background = a.Step5,

            ghost_element_background = n.Step2,
            ghost_element_hover = n.Step3,
            ghost_element_active = n.Step4,
            ghost_element_selected = a.Step3,
            ghost_element_disabled = n.Step2,

            accent = a.Step9,
            accent_hover = a.Step10,

            icon = n.Step11,
            icon_muted = n.Step8,
            icon_disabled = n.Step7,
            icon_accent = a.Step11,
            icon_placeholder = n.Step9,

            drop_target_background = a.Step3,
            drop_target_border = a.Step8,

            search_match_background = y.Step3,
            search_active_match_background = y.Step5,

            scrollbar_thumb_background = n.Step6,
            scrollbar_thumb_hover_background = n.Step7,
            scrollbar_thumb_active_background = n.Step8,
            scrollbar_track_background = n.Step2,

            gutter_background = n.Step1,
            line_number = n.Step8,
            line_number_active = n.Step12,

            modal_background = n.Step4,
            notification_background = n.Step5,
            drag_overlay_background = n.Step6,
        };
    }
}

// -- StatusColors (info/warn/error/success + trading long/short/bid/ask) -------------------
public sealed class StatusColors
{
    public Color info, info_background, info_border;
    public Color warning, warning_background, warning_border;
    public Color error, error_background, error_border;
    public Color success, success_background, success_border;
    public Color @long, long_background, long_border;
    public Color @short, short_background, short_border;
    public Color bid, bid_background, bid_border;
    public Color ask, ask_background, ask_border;

    public static StatusColors FromScales(ColorScales s)
    {
        var blue = s.blue; var y = s.yellow; var red = s.red; var green = s.green;
        return new StatusColors
        {
            info = blue.Step9, info_background = blue.Step3, info_border = blue.Step7,
            warning = y.Step9, warning_background = y.Step3, warning_border = y.Step7,
            error = red.Step9, error_background = red.Step3, error_border = red.Step7,
            success = green.Step9, success_background = green.Step3, success_border = green.Step7,
            @long = green.Step9, long_background = green.Step3, long_border = green.Step7,
            @short = red.Step9, short_background = red.Step3, short_border = red.Step7,
            bid = green.Step11, bid_background = green.Step2, bid_border = green.Step6,
            ask = red.Step11, ask_background = red.Step2, ask_border = red.Step6,
        };
    }
}

// -- SyntaxColors (8 lexical roles) -------------------------------------------------------
// type_ retains the trailing underscore from TTWR. backcast's Decorator class maps here
// (findings 0020) — TTWR has no decorator role.
public sealed class SyntaxColors
{
    public Color comment, keyword, str, number, type_, function, variable, op;

    public static SyntaxColors FromScales(ColorScales s)
    {
        var n = s.neutral; var a = s.accent; var green = s.green; var y = s.yellow; var blue = s.blue;
        return new SyntaxColors
        {
            comment = n.Step8,
            keyword = a.Step11,
            str = green.Step11,
            number = y.Step11,
            type_ = a.Step12,
            function = blue.Step11,
            variable = n.Step12,
            op = n.Step11,
        };
    }
}

// -- PlayerColors (8 distinct chart-series hues; also reused for floating-window accents) --
public sealed class PlayerColors
{
    readonly Color[] _colors; // 8

    public PlayerColors(Color[] colors) { _colors = colors; }

    public Color Get(int index) => _colors[((index % 8) + 8) % 8];

    public static PlayerColors FromScales(ColorScales s)
    {
        var a = s.accent; var green = s.green; var y = s.yellow; var red = s.red;
        return new PlayerColors(new[]
        {
            a.Step9, green.Step9, y.Step9, red.Step9,
            a.Step11, green.Step11, y.Step11, red.Step11,
        });
    }
}
