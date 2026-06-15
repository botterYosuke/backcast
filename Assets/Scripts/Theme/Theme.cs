// Theme.cs — issue #44 "theme（配色）システム"
//
// PORT of TTWR Theme (theme/mod.rs): the design-system root. #44 carries the COLOR layer
// only (findings 0020) — spacing/typography/elevation/radius/layout are #52. Build via
// FromScales(scales); Dark()/Light() differ only by the ColorScales passed (Light() stubs
// to dark until #51). NonDefault() is the verification palette (every role a distinct value)
// used by ThemeProbe / ThemeHitlHarness to prove switching is non-vacuous even while
// shipped dark==light.

using UnityEngine;

public sealed class Theme
{
    public readonly ThemeColors colors;
    public readonly StatusColors status;
    public readonly SyntaxColors syntax;
    public readonly PlayerColors players;
    public readonly ColorScales scale;
    public readonly Appearance appearance;

    Theme(ThemeColors colors, StatusColors status, SyntaxColors syntax, PlayerColors players,
          ColorScales scale, Appearance appearance)
    {
        this.colors = colors;
        this.status = status;
        this.syntax = syntax;
        this.players = players;
        this.scale = scale;
        this.appearance = appearance;
    }

    public static Theme FromScales(ColorScales scales, Appearance appearance)
    {
        return new Theme(
            ThemeColors.FromScales(scales),
            StatusColors.FromScales(scales),
            SyntaxColors.FromScales(scales),
            PlayerColors.FromScales(scales),
            scales,
            appearance);
    }

    public static Theme Dark() => FromScales(ColorScales.Dark(), Appearance.Dark);

    // Light palette is the dark stub until #51 ships real Radix light scales (TTWR parity).
    public static Theme Light() => FromScales(ColorScales.Light(), Appearance.Light);

    // -- NonDefault (verification palette) ------------------------------------------------
    // PORT of TTWR non_default_theme(): every serializable color is a distinct non-default
    // value. ThemeProbe / ThemeHitlHarness switch dark↔NonDefault so the non-vacuous wiring
    // kill works regardless of shipped dark==light (findings 0020 Q9).
    static Color Distinct(int i)
    {
        float v = i / 255f;
        float w = ((i * 2) % 256) / 255f;
        return new Color(v, 1f - v, w);
    }

    static ColorScale DistinctScale(int baseI)
    {
        var steps = new Color[12];
        for (int k = 0; k < 12; k++) steps[k] = Distinct((baseI + k) & 0xff);
        return new ColorScale(steps);
    }

    public static Theme NonDefault()
    {
        var scales = new ColorScales(
            DistinctScale(110), DistinctScale(122), DistinctScale(134),
            DistinctScale(146), DistinctScale(158), DistinctScale(170));

        var colors = new ThemeColors
        {
            background = Distinct(0), surface_background = Distinct(1), elevated_surface_background = Distinct(2),
            panel_background = Distinct(3), panel_focused_border = Distinct(4), status_bar_background = Distinct(5),
            title_bar_background = Distinct(6), toolbar_background = Distinct(7), tab_bar_background = Distinct(8),
            tab_active_background = Distinct(9), tab_inactive_background = Distinct(10), border = Distinct(11),
            border_variant = Distinct(12), border_focused = Distinct(13), border_selected = Distinct(14),
            border_disabled = Distinct(15), border_transparent = Distinct(16), text = Distinct(17),
            text_muted = Distinct(18), text_placeholder = Distinct(19), text_disabled = Distinct(20),
            text_accent = Distinct(21), element_background = Distinct(22), element_hover = Distinct(23),
            element_active = Distinct(24), element_selected = Distinct(25), element_disabled = Distinct(26),
            element_selection_background = Distinct(27), ghost_element_background = Distinct(28),
            ghost_element_hover = Distinct(29), ghost_element_active = Distinct(30), ghost_element_selected = Distinct(31),
            ghost_element_disabled = Distinct(32), accent = Distinct(33), accent_hover = Distinct(34),
            icon = Distinct(35), icon_muted = Distinct(36), icon_disabled = Distinct(37), icon_accent = Distinct(38),
            icon_placeholder = Distinct(39), drop_target_background = Distinct(40), drop_target_border = Distinct(41),
            search_match_background = Distinct(42), search_active_match_background = Distinct(43),
            scrollbar_thumb_background = Distinct(44), scrollbar_thumb_hover_background = Distinct(45),
            scrollbar_thumb_active_background = Distinct(46), scrollbar_track_background = Distinct(47),
            gutter_background = Distinct(48), line_number = Distinct(49), line_number_active = Distinct(50),
            modal_background = Distinct(51), notification_background = Distinct(52), drag_overlay_background = Distinct(53),
        };

        var status = new StatusColors
        {
            info = Distinct(60), info_background = Distinct(61), info_border = Distinct(62),
            warning = Distinct(63), warning_background = Distinct(64), warning_border = Distinct(65),
            error = Distinct(66), error_background = Distinct(67), error_border = Distinct(68),
            success = Distinct(69), success_background = Distinct(70), success_border = Distinct(71),
            @long = Distinct(72), long_background = Distinct(73), long_border = Distinct(74),
            @short = Distinct(75), short_background = Distinct(76), short_border = Distinct(77),
            bid = Distinct(78), bid_background = Distinct(79), bid_border = Distinct(80),
            ask = Distinct(81), ask_background = Distinct(82), ask_border = Distinct(83),
        };

        var syntax = new SyntaxColors
        {
            comment = Distinct(90), keyword = Distinct(91), str = Distinct(92), number = Distinct(93),
            type_ = Distinct(94), function = Distinct(95), variable = Distinct(96), op = Distinct(97),
        };

        var players = new PlayerColors(new[]
        {
            Distinct(100), Distinct(101), Distinct(102), Distinct(103),
            Distinct(104), Distinct(105), Distinct(106), Distinct(107),
        });

        return new Theme(colors, status, syntax, players, scales, Appearance.Light);
    }
}
