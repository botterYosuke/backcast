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
            workspace_background = new Color(0.0118f, 0.0235f, 0.0627f), // #030610 — deep cosmos void around floating panels (owner re-skin 2026-06-20)
            // hakoniwa SPACE palette (2026-06-20 owner re-skin, supersedes farm theme from findings 0054):
            // tile is an instrument panel floating in deep space. Outer field is the darkest void (#030610);
            // tiles sit on Neutral.Step3 so they read as illuminated card on void; steel-blue header frames
            // the tile (Neutral.Step7, no extra primary); chart face is a hair lighter than void for ink
            // contrast. Up/down/LAST use the muted aurora-teal / mars-rust / gold-star scale anchors so
            // candles glow without screaming. Owner literals (raw sRGB) — a future light scale can't
            // silently recolor them; swap + Play to tune.
            hakoniwa_root_background  = new Color(0.0118f, 0.0235f, 0.0627f), // #030610 — deepest cosmos (ground)
            hakoniwa_tile_background  = new Color(0.0667f, 0.0863f, 0.1686f), // #11162b — illuminated panel card (Neutral.Step3)
            hakoniwa_tile_header      = new Color(0.2078f, 0.2588f, 0.4471f), // #354272 — steel-blue header / frame (Neutral.Step7)
            hakoniwa_chart_background = new Color(0.0314f, 0.0471f, 0.1098f), // #080c1c — near-void chart face for ink contrast
            hakoniwa_panel_surface    = new Color(0.0667f, 0.0863f, 0.1686f), // #11162b — same panel hue (startup tile)
            hakoniwa_tile_header_text = new Color(0.7843f, 0.8824f, 0.9608f), // #c8e1f5 — pale stellar text on steel header
            hakoniwa_text             = new Color(0.8784f, 0.9059f, 0.9608f), // #e0e7f5 — starlight white text
            hakoniwa_text_muted       = new Color(0.6588f, 0.7059f, 0.8314f), // #a8b4d4 — cool grey-blue axes / change% / ladder header
            hakoniwa_up               = new Color(0.2314f, 0.7686f, 0.5961f), // #3bc498 — aurora teal: candle up / change% gain / ladder bid
            hakoniwa_down             = new Color(0.8510f, 0.3882f, 0.2627f), // #d96343 — mars rust: candle down / change% loss / ladder ask
            hakoniwa_last             = new Color(0.8471f, 0.6588f, 0.2314f), // #d8a83b — gold star: ladder LAST marker
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
