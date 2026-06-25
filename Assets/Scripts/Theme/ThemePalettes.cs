// ThemePalettes.cs — issue #44 "theme（配色）システム"
//
// PORT of TTWR ThemeColors / StatusColors / SyntaxColors / PlayerColors / Appearance
// (theme/mod.rs). FromScales(...) is the SINGLE source of truth for the scale-step →
// semantic-role mapping; swapping the ColorScales it receives (dark ↔ light) re-derives
// every role. Full role set is defined even where backcast has no consumer yet (parity
// completeness — findings 0020); the wired subset is in ScenarioStartupTile / syntax /
// editor / floating-window / chart / ladder.

using UnityEngine;

// Light / dark label. Since ADR-0028 it IS a switch input: ThemeColors.FromScales branches the
// canvas-isolated owner literals on it (CanvasLiterals.For), and the active variant is persisted
// (#43 / findings 0108 D8). Scale-derived roles still do NOT branch on it (Radix from_scales is
// appearance-agnostic — the scale swap carries the dark↔light difference for those).
public enum Appearance { Dark, Light }

// CanvasLiterals — the per-Appearance canvas-ISOLATED owner literals (workspace_background + hakoniwa_*),
// raw sRGB, NOT scale-derived (ADR-0028 / findings 0108 D3). ONE place so a re-skin is "swap values + Play"
// (no scene re-bake). The isolation seam (findings 0054) holds in BOTH variants: these are the only roles
// the chart / ladder / tiles / field read, so a scale change can't bleed into the canvas and vice-versa.
struct CanvasLiterals
{
    public Color workspace_background, hakoniwa_root_background, hakoniwa_tile_background, hakoniwa_tile_header;
    public Color hakoniwa_chart_background, hakoniwa_panel_surface, hakoniwa_tile_header_text, hakoniwa_text, hakoniwa_text_muted;
    public Color hakoniwa_up, hakoniwa_down, hakoniwa_last;

    public static CanvasLiterals For(Appearance appearance) =>
        appearance == Appearance.Light ? Light() : Dark();

    // DARK — cyan-HUD deep-space (2026-06-20/21 owner re-skin; supersedes the farm theme from findings 0054).
    // Tile = an instrument panel floating in deep space; outer field is the darkest void; tiles read as an
    // illuminated card on void; steel-blue header frames the tile; chart face a hair lighter than void for
    // ink contrast; up/down/LAST = muted aurora-teal / mars-rust / gold-star so candles glow without screaming.
    static CanvasLiterals Dark() => new CanvasLiterals
    {
        workspace_background      = new Color(0.0078f, 0.0196f, 0.0392f), // #02050a near-black cyan-navy HUD field
        hakoniwa_root_background  = new Color(0.0078f, 0.0196f, 0.0392f), // #02050a near-black HUD void (ground)
        hakoniwa_tile_background  = new Color(0.0549f, 0.0863f, 0.1490f), // #0e1626 illuminated panel card
        hakoniwa_tile_header      = new Color(0.0824f, 0.3294f, 0.4078f), // #155368 cyan-steel header / frame
        hakoniwa_chart_background = new Color(0.0235f, 0.0431f, 0.0824f), // #060b15 near-void chart face
        hakoniwa_panel_surface    = new Color(0.0549f, 0.0863f, 0.1490f), // #0e1626 same panel hue (startup tile)
        hakoniwa_tile_header_text = new Color(0.7843f, 0.9647f, 0.9922f), // #c8f6fd pale cyan header text
        hakoniwa_text             = new Color(0.8784f, 0.9059f, 0.9608f), // #e0e7f5 starlight white text
        hakoniwa_text_muted       = new Color(0.6588f, 0.7059f, 0.8314f), // #a8b4d4 cool grey-blue axes
        hakoniwa_up               = new Color(0.2314f, 0.7686f, 0.5961f), // #3bc498 aurora teal
        hakoniwa_down             = new Color(0.8510f, 0.3882f, 0.2627f), // #d96343 mars rust
        hakoniwa_last             = new Color(0.8471f, 0.6588f, 0.2314f), // #d8a83b gold star
    };

    // LIGHT — Miro-風 whiteboard (ADR-0028). Off-white field (the dotted grid rides on this), white cards
    // that pop against the field, near-black ink text, and candles tuned to read on a WHITE chart face
    // (deeper / more saturated than the dark aurora set so they don't wash out). Owner literals — swap + Play.
    static CanvasLiterals Light() => new CanvasLiterals
    {
        workspace_background      = new Color(0.9333f, 0.9412f, 0.9529f), // #eef0f3 soft cool off-white field
        hakoniwa_root_background  = new Color(0.9333f, 0.9412f, 0.9529f), // #eef0f3 same ground
        hakoniwa_tile_background  = new Color(1.0000f, 1.0000f, 1.0000f), // #ffffff white card
        hakoniwa_tile_header      = new Color(0.9255f, 0.9333f, 0.9451f), // #eceef1 light grey header band
        hakoniwa_chart_background = new Color(1.0000f, 1.0000f, 1.0000f), // #ffffff white chart face (ink contrast)
        hakoniwa_panel_surface    = new Color(1.0000f, 1.0000f, 1.0000f), // #ffffff white panel (startup tile)
        hakoniwa_tile_header_text = new Color(0.0667f, 0.0941f, 0.1098f), // #11181c near-black ink on light header
        hakoniwa_text             = new Color(0.0667f, 0.0941f, 0.1098f), // #11181c near-black ink text
        hakoniwa_text_muted       = new Color(0.4078f, 0.4392f, 0.4627f), // #687076 grey axes / change% / ladder header
        hakoniwa_up               = new Color(0.0863f, 0.5255f, 0.2471f), // #16863f deep green: candle up / gain / bid
        hakoniwa_down             = new Color(0.8235f, 0.2471f, 0.2471f), // #d23f3f legible red: candle down / loss / ask
        hakoniwa_last             = new Color(0.6902f, 0.4667f, 0.0000f), // #b07700 dark gold: ladder LAST marker
    };
}

// -- ThemeColors (54 semantic UI roles, field naming mirrors Zed's ThemeColors) -----------
public sealed class ThemeColors
{
    public Color background, surface_background, elevated_surface_background, panel_background;
    // workspace_background — the infinite-canvas FIELD behind every Hakoniwa tile / floating window
    // (BackcastWorkspaceRoot._viewport). NOT a Radix scale step: a deliberate owner override so the field
    // hue can be tuned independently of `background`. Kept distinct from `background` precisely so changing
    // the field hue does NOT recolor chart / ladder / editor backgrounds. Current (space re-skin
    // 2026-06-20) value is a deep-cosmos void slightly darker than `background` so floating panels read as
    // illuminated cards against outer space; was light-blue under the earlier farm palette.
    public Color workspace_background;
    // hakoniwa_* — the Hakoniwa-ISOLATED surface roles (findings 0054). The chart / ladder / panel
    // tiles and the tile chrome (card / header / label) read THESE, not the shared `background` /
    // `panel_background`, so the Hakoniwa farm CAN evolve independently of the strategy editor /
    // footer / sidebar (which keep the dark shared roles). All owner literals (raw sRGB,
    // scale-non-derived, same流儀 as workspace_background) so a future light scale can't silently
    // recolor them; swap these literals + Play to try a new palette (no scene re-bake). After the
    // space re-skin (2026-06-20) the values cohere with the dark shared chrome rather than diverging
    // brightly, but the isolation seam stays in place so a future Hakoniwa-only palette can re-diverge
    // without disturbing the rest.
    public Color hakoniwa_root_background, hakoniwa_tile_background, hakoniwa_tile_header;
    public Color hakoniwa_chart_background, hakoniwa_panel_surface;
    public Color hakoniwa_tile_header_text, hakoniwa_text, hakoniwa_text_muted;
    // hakoniwa trading colors (findings 0054 P1, re-tuned for the space re-skin 2026-06-20): kept as a
    // SEPARATE override from `status.*` so the candle / ladder colors can be tuned for the Hakoniwa
    // chart background independently of the shared error/success/warning hues. Under the original farm
    // palette these were darker crop-green / barn-red / dark-amber for cream-surface legibility; under
    // the space palette they are aurora-teal / mars-rust / gold-star tuned for the near-void chart
    // face. ChartView candles + change% and the ladder bid/ask/LAST read these — Hakoniwa-isolated so
    // the rest of the app keeps semantic status.* unchanged.
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

    public static ThemeColors FromScales(ColorScales s, Appearance appearance)
    {
        var n = s.neutral; var a = s.accent; var y = s.yellow;
        var canvas = CanvasLiterals.For(appearance);
        return new ThemeColors
        {
            background = n.Step1,
            // workspace_background + hakoniwa_* are the canvas-ISOLATED owner literals (NOT scale steps) —
            // switched per Appearance (ADR-0028 / findings 0108 D3). The isolation seam (findings 0054) is
            // preserved in BOTH variants so a future scale can't silently recolor the canvas; the dark =
            // cyan-HUD deep-space field, the light = Miro-風 whiteboard. See CanvasLiterals below for the
            // raw sRGB values + rationale per variant.
            workspace_background      = canvas.workspace_background,
            hakoniwa_root_background  = canvas.hakoniwa_root_background,
            hakoniwa_tile_background  = canvas.hakoniwa_tile_background,
            hakoniwa_tile_header      = canvas.hakoniwa_tile_header,
            hakoniwa_chart_background = canvas.hakoniwa_chart_background,
            hakoniwa_panel_surface    = canvas.hakoniwa_panel_surface,
            hakoniwa_tile_header_text = canvas.hakoniwa_tile_header_text,
            hakoniwa_text             = canvas.hakoniwa_text,
            hakoniwa_text_muted       = canvas.hakoniwa_text_muted,
            hakoniwa_up               = canvas.hakoniwa_up,
            hakoniwa_down             = canvas.hakoniwa_down,
            hakoniwa_last             = canvas.hakoniwa_last,
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
