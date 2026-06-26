// ThemeProbe.cs — issue #44 "theme（配色）システム" (THROWAWAY AFK regression gate, AC#4)
//
// Headless, Python-FREE gate for the theme color system. Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod ThemeProbe.Run -logFile <log>
//   # expect: [THEME PASS] ... / exit=0
//
// Five sections (findings 0020 Q9; §5 added by findings 0054):
//   1. DERIVATION PARITY — representative semantic roles equal the right Radix scale step (the
//      from_scales single-source-of-truth, TTWR theme_scale_accent_step_9 parity).
//   2. NonDefault != Dark — the verification palette actually differs from shipped dark, so the
//      switch kill below is non-vacuous EVEN THOUGH shipped dark==light (案A; real light is #51).
//   3. ThemeService SEMANTICS — Current defaults dark, SetTheme fires Changed once and swaps Current.
//   4. WIRING KILL (the point) — build each themed surface, sample a graphic under dark, switch to
//      NonDefault + ApplyTheme, assert the SAME graphic moved to the NonDefault token. A surface
//      that kept an inline color would NOT move → FAIL. Covers ScenarioStartupTile (panel),
//      PythonSyntaxMeshEffect (syntax), and the ThemeHitlHarness montage (chart/ladder/accents) —
//      the same production-path wiring the HITL gate uses.
//   5. CHROME SUBSCRIPTION KILL — build the REAL BackcastWorkspaceRoot, sample tile chrome under dark,
//      then SetTheme(NonDefault) and ONLY that; assert chrome followed via its Changed subscription.

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public static class ThemeProbe
{
    static readonly List<string> _fails = new List<string>();
    static readonly List<GameObject> _spawned = new List<GameObject>();

    public static void Run()
    {
        _fails.Clear();
        try
        {
            ThemeService.ResetForTests();
            Section1_DerivationParity();
            Section1b_LightVariant();
            Section2_NonDefaultDiffers();
            Section3_ServiceSemantics();
            Section4_WiringKill();
            // Section5_ChromeSubscriptionKill retired with the Hakoniwa surface (#99 / ADR-0017): the
            // Hakoniwa root/tile chrome and its ApplyHakoniwaChromeTheme subscription no longer exist.
            // ChartView / DepthLadderView still self-subscribe to ThemeService.Changed for their own
            // candle/board colors; their tests (ChartViewProbe / DepthLadderProbe) cover that.
        }
        catch (Exception e)
        {
            _fails.Add("EXCEPTION: " + e);
        }
        finally
        {
            foreach (var go in _spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            _spawned.Clear();
            ThemeService.ResetForTests();
        }

        if (_fails.Count == 0)
        {
            Debug.Log("[THEME PASS] derivation + NonDefault≠dark + service semantics + wiring kill all green");
        }
        else
        {
            foreach (var f in _fails) Debug.LogError("[THEME FAIL] " + f);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // 1 — from_scales maps roles to the right Radix step (parity SoT).
    static void Section1_DerivationParity()
    {
        var t = Theme.Dark();
        var n = ColorScale.NeutralDark();
        var a = ColorScale.AccentDark();
        var green = ColorScale.GreenDark();
        var red = ColorScale.RedDark();
        var yellow = ColorScale.YellowDark();
        var blue = ColorScale.BlueDark();

        Eq(t.colors.background, n.Step1, "colors.background == neutral.1");
        // workspace_background is the DARK canvas owner literal (CanvasLiterals.Dark, ADR-0028), NOT a scale
        // step — assert the SHIPPED space value AND that it is DISTINCT from background, so the viewport field
        // can't silently collapse back onto the content bg. (Synced 2026-06-25 from the stale farm #7fa4be.)
        Eq(t.colors.workspace_background, new Color(0.0078f, 0.0196f, 0.0392f), "colors.workspace_background == #02050a (dark owner literal)");
        Ne(t.colors.workspace_background, t.colors.background, "workspace_background != background (field stays distinct from content bg)");
        // hakoniwa_* are the DARK canvas owner literals (CanvasLiterals.Dark — cyan-HUD space re-skin), NOT
        // scale steps. Assert the shipped values AND the structural isolation Ne (chart/panel bg differ from
        // the shared roles, else the editor/footer/sidebar leak into the canvas and the isolation is vacuous).
        Eq(t.colors.hakoniwa_root_background,  new Color(0.0078f, 0.0196f, 0.0392f), "hakoniwa_root_background == #02050a (HUD void)");
        Eq(t.colors.hakoniwa_tile_background,  new Color(0.0549f, 0.0863f, 0.1490f), "hakoniwa_tile_background == #0e1626 (panel card)");
        Eq(t.colors.hakoniwa_tile_header,      new Color(0.0824f, 0.3294f, 0.4078f), "hakoniwa_tile_header == #155368 (cyan-steel header)");
        Eq(t.colors.hakoniwa_chart_background, new Color(0.0235f, 0.0431f, 0.0824f), "hakoniwa_chart_background == #060b15 (near-void chart face)");
        Eq(t.colors.hakoniwa_panel_surface,    new Color(0.0549f, 0.0863f, 0.1490f), "hakoniwa_panel_surface == #0e1626 (panel hue)");
        Eq(t.colors.hakoniwa_tile_header_text, new Color(0.7843f, 0.9647f, 0.9922f), "hakoniwa_tile_header_text == #c8f6fd (pale cyan)");
        Eq(t.colors.hakoniwa_text,             new Color(0.8784f, 0.9059f, 0.9608f), "hakoniwa_text == #e0e7f5 (starlight white)");
        Eq(t.colors.hakoniwa_text_muted,       new Color(0.6588f, 0.7059f, 0.8314f), "hakoniwa_text_muted == #a8b4d4 (cool grey-blue)");
        // hakoniwa trading colors (CanvasLiterals.Dark): aurora-teal / mars-rust / gold-star. Under the space
        // re-skin these intentionally SHARE the green/red/yellow scale anchors (hakoniwa_up == green.9 etc.),
        // so the old "!= status.long" distinctness assert is GONE — the isolation is the separate ROLE (a future
        // palette CAN diverge them), proven by the chart/panel Ne below + the Section-light switch kill.
        Eq(t.colors.hakoniwa_up,   new Color(0.2314f, 0.7686f, 0.5961f), "hakoniwa_up == #3bc498 (aurora teal)");
        Eq(t.colors.hakoniwa_down, new Color(0.8510f, 0.3882f, 0.2627f), "hakoniwa_down == #d96343 (mars rust)");
        Eq(t.colors.hakoniwa_last, new Color(0.8471f, 0.6588f, 0.2314f), "hakoniwa_last == #d8a83b (gold star)");
        Ne(t.colors.hakoniwa_chart_background, t.colors.background, "hakoniwa_chart_background != background (chart/ladder isolated from editor)");
        Ne(t.colors.hakoniwa_panel_surface, t.colors.panel_background, "hakoniwa_panel_surface != panel_background (startup isolated from footer/sidebar)");
        Eq(t.colors.surface_background, n.Step2, "colors.surface_background == neutral.2");
        Eq(t.colors.text, n.Step12, "colors.text == neutral.12");
        Eq(t.colors.text_muted, n.Step11, "colors.text_muted == neutral.11");
        Eq(t.colors.panel_background, n.Step2, "colors.panel_background == neutral.2");
        Eq(t.colors.element_background, n.Step3, "colors.element_background == neutral.3");
        Eq(t.colors.element_selected, a.Step5, "colors.element_selected == accent.5");
        Eq(t.colors.accent, a.Step9, "colors.accent == accent.9");

        Eq(t.status.@long, green.Step9, "status.long == green.9");
        Eq(t.status.@short, red.Step9, "status.short == red.9");
        Eq(t.status.bid, green.Step11, "status.bid == green.11");
        Eq(t.status.ask, red.Step11, "status.ask == red.11");
        Eq(t.status.error, red.Step9, "status.error == red.9");
        Eq(t.status.warning, yellow.Step9, "status.warning == yellow.9");
        Eq(t.status.info, blue.Step9, "status.info == blue.9");

        Eq(t.syntax.keyword, a.Step11, "syntax.keyword == accent.11");
        Eq(t.syntax.type_, a.Step12, "syntax.type_ == accent.12 (Decorator target)");
        Eq(t.syntax.function, blue.Step11, "syntax.function == blue.11 (Definition target)");
        Eq(t.syntax.comment, n.Step8, "syntax.comment == neutral.8");

        Eq(t.players.Get(0), a.Step9, "players[0] == accent.9 (editor accent)");
        Eq(t.players.Get(2), yellow.Step9, "players[2] == yellow.9 (order accent)");
    }

    // 1b — Light (Miro-風 whiteboard) variant: derivation parity on the REAL Radix light scales + the
    // canvas owner literals, AND non-vacuity (light != dark) so the appearance switch genuinely moves both
    // the scale-derived chrome AND the canvas (ADR-0028 / findings 0108 S1+S2). Without this, a regression
    // that left Light()==Dark() (the old 案A stub) would pass every other section silently.
    static void Section1b_LightVariant()
    {
        var l = Theme.Light();
        var dark = Theme.Dark();
        var nL = ColorScale.NeutralLight();
        var aL = ColorScale.AccentLight();
        var greenL = ColorScale.GreenLight();

        True(l.appearance == Appearance.Light, "Theme.Light().appearance == Light");

        // scale-derived chrome follows the LIGHT scales (from_scales is appearance-agnostic — proves the
        // scale swap, not a branch, carries the chrome difference).
        Eq(l.colors.background, nL.Step1, "light colors.background == NeutralLight.1 (#fbfcfd near-white)");
        Eq(l.colors.text, nL.Step12, "light colors.text == NeutralLight.12 (#11181c ink)");
        Eq(l.colors.accent, aL.Step9, "light colors.accent == AccentLight.9 (#3e63dd Miro-blue)");
        Eq(l.status.@long, greenL.Step9, "light status.long == GreenLight.9");

        // canvas owner literals follow the LIGHT branch (CanvasLiterals.Light — Miro whiteboard).
        Eq(l.colors.workspace_background, new Color(0.9333f, 0.9412f, 0.9529f), "light workspace_background == #eef0f3 (off-white field)");
        Eq(l.colors.hakoniwa_tile_background, new Color(1f, 1f, 1f), "light hakoniwa_tile_background == #ffffff (white card)");
        Eq(l.colors.hakoniwa_chart_background, new Color(1f, 1f, 1f), "light hakoniwa_chart_background == #ffffff (white chart face)");
        Eq(l.colors.hakoniwa_text, new Color(0.0667f, 0.0941f, 0.1098f), "light hakoniwa_text == #11181c (ink)");
        Eq(l.colors.hakoniwa_up, new Color(0.0863f, 0.5255f, 0.2471f), "light hakoniwa_up == #16863f (deep green)");

        // NON-VACUITY: light must actually differ from dark, scale-derived AND canvas, else the switch is a no-op.
        Ne(l.colors.background, dark.colors.background, "light background != dark (whiteboard vs void)");
        Ne(l.colors.text, dark.colors.text, "light text != dark");
        Ne(l.colors.accent, dark.colors.accent, "light accent != dark (Miro-blue vs cyan)");
        Ne(l.colors.workspace_background, dark.colors.workspace_background, "light workspace_background != dark");
        Ne(l.colors.hakoniwa_tile_background, dark.colors.hakoniwa_tile_background, "light hakoniwa_tile_background != dark");
        Ne(l.colors.hakoniwa_up, dark.colors.hakoniwa_up, "light hakoniwa_up != dark");
    }

    // 2 — the verification palette genuinely differs from dark.
    static void Section2_NonDefaultDiffers()
    {
        var dark = Theme.Dark();
        var nd = Theme.NonDefault();
        // Every role the Section4 switch-kill samples must be proven dark != NonDefault here, or
        // that kill could pass vacuously (stale == target) if the two ever coincided for that role.
        Ne(nd.colors.background, dark.colors.background, "NonDefault.background != dark");
        Ne(nd.colors.workspace_background, dark.colors.workspace_background, "NonDefault.workspace_background != dark");
        // hakoniwa_* roles the Section4 switch-kill samples (chrome + chart/ladder + startup + text).
        Ne(nd.colors.hakoniwa_root_background, dark.colors.hakoniwa_root_background, "NonDefault.hakoniwa_root_background != dark");
        Ne(nd.colors.hakoniwa_tile_background, dark.colors.hakoniwa_tile_background, "NonDefault.hakoniwa_tile_background != dark");
        Ne(nd.colors.hakoniwa_tile_header, dark.colors.hakoniwa_tile_header, "NonDefault.hakoniwa_tile_header != dark");
        Ne(nd.colors.hakoniwa_chart_background, dark.colors.hakoniwa_chart_background, "NonDefault.hakoniwa_chart_background != dark");
        Ne(nd.colors.hakoniwa_panel_surface, dark.colors.hakoniwa_panel_surface, "NonDefault.hakoniwa_panel_surface != dark");
        Ne(nd.colors.hakoniwa_tile_header_text, dark.colors.hakoniwa_tile_header_text, "NonDefault.hakoniwa_tile_header_text != dark");
        Ne(nd.colors.hakoniwa_text, dark.colors.hakoniwa_text, "NonDefault.hakoniwa_text != dark");
        Ne(nd.colors.hakoniwa_text_muted, dark.colors.hakoniwa_text_muted, "NonDefault.hakoniwa_text_muted != dark");
        Ne(nd.colors.surface_background, dark.colors.surface_background, "NonDefault.surface_background != dark");
        Ne(nd.colors.panel_background, dark.colors.panel_background, "NonDefault.panel_background != dark");
        Ne(nd.colors.text, dark.colors.text, "NonDefault.text != dark");
        Ne(nd.status.@long, dark.status.@long, "NonDefault.long != dark");
        Ne(nd.status.@short, dark.status.@short, "NonDefault.short != dark");
        Ne(nd.status.bid, dark.status.bid, "NonDefault.bid != dark");
        Ne(nd.status.ask, dark.status.ask, "NonDefault.ask != dark");
        Ne(nd.status.warning, dark.status.warning, "NonDefault.warning != dark");
        Ne(nd.colors.hakoniwa_up, dark.colors.hakoniwa_up, "NonDefault.hakoniwa_up != dark");
        Ne(nd.colors.hakoniwa_down, dark.colors.hakoniwa_down, "NonDefault.hakoniwa_down != dark");
        Ne(nd.colors.hakoniwa_last, dark.colors.hakoniwa_last, "NonDefault.hakoniwa_last != dark");
        Ne(nd.syntax.keyword, dark.syntax.keyword, "NonDefault.keyword != dark");
        Ne(nd.players.Get(0), dark.players.Get(0), "NonDefault.players[0] != dark");
        Ne(nd.players.Get(2), dark.players.Get(2), "NonDefault.players[2] != dark");
    }

    // 3 — single global owner: lazy dark, Changed fires once, Current swaps.
    static void Section3_ServiceSemantics()
    {
        ThemeService.ResetForTests();
        True(ThemeService.Current.appearance == Appearance.Dark, "Current defaults to dark");

        int fired = 0;
        Action h = () => fired++;
        ThemeService.Changed += h;
        var nd = Theme.NonDefault();
        ThemeService.SetTheme(nd);
        True(fired == 1, "SetTheme fired Changed exactly once (got " + fired + ")");
        True(ReferenceEquals(ThemeService.Current, nd), "Current reflects the set theme");
        ThemeService.Changed -= h;
        ThemeService.ResetForTests();
    }

    // 4 — non-vacuous switch kill across surfaces.
    static void Section4_WiringKill()
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // -- 4a ScenarioStartupTile (input field面) --
        // #137 (findings 0107 F1): the tile no longer paints a Hakoniwa panel surface — it is hosted in the
        // Settings card, which provides the surface, so the tile面 is now Color.clear. The wiring-kill samples
        // the field FILL role (surface_background) instead, proving the redesigned input面 follows a switch.
        ThemeService.ResetForTests();
        var tileGo = Spawn("probe_tile", typeof(RectTransform));
        var tile = new ScenarioStartupTile(new ScenarioStartupController(), font);
        tile.Build(tileGo.GetComponent<RectTransform>());
        Image tileFieldBg = null;
        foreach (var img in tileGo.GetComponentsInChildren<Image>(true))
            if (img.gameObject.name == "field") { tileFieldBg = img; break; }
        if (tileFieldBg == null) _fails.Add("4a: tile built no input field面 (surface_background sample missing)");
        else
        {
            Ne(Theme.Dark().colors.surface_background, Theme.NonDefault().colors.surface_background,
               "surface_background dark != NonDefault (4a non-vacuity)");
            Eq(tileFieldBg.color, Theme.Dark().colors.surface_background, "tile field fill == dark surface_background (#137 S1)");
            ThemeService.SetTheme(Theme.NonDefault());
            tile.ApplyTheme();
            Eq(tileFieldBg.color, ThemeService.Current.colors.surface_background, "tile field fill switched to NonDefault surface_background");
        }

        // -- 4b PythonSyntaxMeshEffect (syntax) --
        ThemeService.ResetForTests();
        // #120: the syntax recolour driver now requires a TMP_Text (was UnityEngine.UI.Text). This
        // probe only checks the palette fields follow a theme switch, so no font is set (Recolour
        // no-ops on a fontless TMP_Text).
        var sxGo = Spawn("probe_syntax", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI), typeof(PythonSyntaxMeshEffect));
        var eff = sxGo.GetComponent<PythonSyntaxMeshEffect>();
        eff.ApplyTheme(); // OnEnable is play-mode-only; apply explicitly for the headless probe
        var dk = Theme.Dark();
        Eq(eff.keyword, dk.syntax.keyword, "syntax effect keyword == dark");
        Eq(eff.decorator, dk.syntax.type_, "syntax effect decorator == type_ (findings 0020)");
        Eq(eff.definition, dk.syntax.function, "syntax effect definition == function");
        ThemeService.SetTheme(Theme.NonDefault());
        eff.ApplyTheme();
        Eq(eff.keyword, ThemeService.Current.syntax.keyword, "syntax effect keyword switched");

        // -- 4c ThemeHitlHarness montage (chart / ladder / accents) --
        // chart_bg / candle_up / candle_down are now the REAL production ChartView graphics (#53), and
        // ladder_bg / ladder_bid / ladder_ask are the REAL production DepthLadderView graphics (#54) —
        // so this kill proves the PRODUCTION parts follow a theme switch (AC③).
        ThemeService.ResetForTests();
        var mGo = Spawn("probe_montage", typeof(RectTransform));
        var harness = mGo.AddComponent<ThemeHitlHarness>();
        harness.BuildMontage(mGo.GetComponent<RectTransform>());
        var d = Theme.Dark();
        // S1 #155 + S8 #161 (findings 0119 D-8 + 0120 D-13/D-14): ChartView + DepthLadderView color
        // seams are Color properties on the MaskableGraphic widgets, queried via harness.ChartView /
        // harness.LadderView directly (no Graphic samples). ChartPalette guarantees Bid/Ask single-source
        // so Bid == ChartView.BULLISH == LADDER-PALETTE-01 invariant.
        True(harness.ChartView != null, "harness exposed the production ChartView (Mesh widget, S1 #155)");
        True(harness.LadderView != null, "harness exposed the production DepthLadderView (Mesh widget, S8 #161)");
        Eq(harness.ChartView.BackgroundColor, d.colors.hakoniwa_chart_background, "ChartView (production) BackgroundColor == dark hakoniwa_chart_background (findings 0054 + 0119 D-8)");
        Eq(harness.ChartView.FirstCandleColor(true), d.colors.hakoniwa_up, "ChartView (production) FirstCandleColor(true) == hakoniwa_up (findings 0054 P1 + 0119 D-8)");
        Eq(harness.ChartView.FirstCandleColor(false), d.colors.hakoniwa_down, "ChartView (production) FirstCandleColor(false) == hakoniwa_down");

        // -- 4c-ii title bar (#53): the 2-bar mock is firstOpen=100 / lastClose=105 → +5.00% gain.
        // Value-asserts the NEW title port (price/change% formatting incl. sign) AND that the change%
        // color follows the theme (long when gain) — neither was gated before. --
        var title = harness.ChartView;
        True(title != null, "montage exposed the production ChartView");
        if (title != null)
        {
            Eq(title.PriceText.text, "105.00", "ChartView title price == last close (105)");
            Eq(title.ChangeText.text, "+5.00%", "ChartView title change% == +5.00% (mock +5% gain)");
            Eq(title.ChangeText.color, d.colors.hakoniwa_up, "ChartView title change% colored hakoniwa_up (gain) under dark");
        }
        Eq(harness.LadderView.BestBidColor, d.colors.hakoniwa_up, "DepthLadderView (production) BestBidColor == hakoniwa_up (findings 0054 P1 + 0120 D-13)");
        Eq(harness.LadderView.BestAskColor, d.colors.hakoniwa_down, "DepthLadderView (production) BestAskColor == hakoniwa_down");
        Eq(harness.LadderView.LastRowColor, d.colors.hakoniwa_last, "DepthLadderView (production) LastRowColor == hakoniwa_last");
        Eq(harness.LadderView.BackgroundColor, d.colors.hakoniwa_chart_background, "DepthLadderView (production) BackgroundColor == hakoniwa_chart_background (single-source with ChartView via ChartPalette)");
        // LADDER-PALETTE-01: ChartView and DepthLadderView share Bid/Ask via ChartPalette.
        Eq(harness.LadderView.BestBidColor, harness.ChartView.FirstCandleColor(true), "LADDER-PALETTE-01 dark: ladder Bid == chart Bullish (ChartPalette single-source)");
        Eq(harness.LadderView.BestAskColor, harness.ChartView.FirstCandleColor(false), "LADDER-PALETTE-01 dark: ladder Ask == chart Bearish");
        Eq(harness.Samples["accent_editor"].color, d.players.Get(0), "montage accent_editor == dark players[0]");
        Eq(harness.Samples["accent_order"].color, d.players.Get(2), "montage accent_order == dark players[2]");
        // ladder_bg == hakoniwa_chart_background (findings 0054: chart + ladder share one Hakoniwa-isolated bg role).
        // (ladder_bg / chart_bg assertions moved to LadderView.BackgroundColor / ChartView.BackgroundColor above — S8 #161)
        Eq(harness.Samples["editor_bg"].color, d.colors.background, "montage editor_bg == dark background");
        Eq(harness.Samples["accents_bg"].color, d.colors.surface_background, "montage accents_bg == dark surface");
        Eq(harness.Samples["code_text"].color, d.colors.text, "montage code_text == dark text");
        Eq(harness.SyntaxEffect.keyword, d.syntax.keyword, "montage syntax keyword == dark");

        ThemeService.SetTheme(Theme.NonDefault());
        harness.ApplyTheme();
        var nd = ThemeService.Current;
        Eq(harness.LadderView.BackgroundColor, nd.colors.hakoniwa_chart_background, "DepthLadderView (production) BackgroundColor switched (S8 #161)");
        Eq(harness.LadderView.BestBidColor, nd.colors.hakoniwa_up, "DepthLadderView (production) BestBidColor switched");
        Eq(harness.LadderView.BestAskColor, nd.colors.hakoniwa_down, "DepthLadderView (production) BestAskColor switched");
        Eq(harness.LadderView.LastRowColor, nd.colors.hakoniwa_last, "DepthLadderView (production) LastRowColor switched");
        Eq(harness.LadderView.BestBidColor, harness.ChartView.FirstCandleColor(true), "LADDER-PALETTE-01 nondefault: ladder Bid still == chart Bullish after switch");
        Eq(harness.Samples["editor_bg"].color, nd.colors.background, "montage editor_bg switched");
        Eq(harness.Samples["accents_bg"].color, nd.colors.surface_background, "montage accents_bg switched");
        Eq(harness.Samples["code_text"].color, nd.colors.text, "montage code_text switched");
        Eq(harness.ChartView.BackgroundColor, nd.colors.hakoniwa_chart_background, "ChartView (production) BackgroundColor switched (S1 #155)");
        Eq(harness.ChartView.FirstCandleColor(true), nd.colors.hakoniwa_up, "ChartView (production) FirstCandleColor(true) switched");
        Eq(harness.ChartView.FirstCandleColor(false), nd.colors.hakoniwa_down, "ChartView (production) FirstCandleColor(false) switched");
        // title change% color must ALSO follow the switch (gain stays long, now NonDefault's long) (#53).
        if (title != null) Eq(title.ChangeText.color, nd.colors.hakoniwa_up, "ChartView title change% recolors on switch");
        // (ladder_bid/ask/last switched assertions moved to the LadderView.Color reads above)
        Eq(harness.Samples["accent_editor"].color, nd.players.Get(0), "montage accent_editor switched");
        Eq(harness.Samples["accent_order"].color, nd.players.Get(2), "montage accent_order switched");
        Eq(harness.SyntaxEffect.keyword, nd.syntax.keyword, "montage syntax keyword switched");
    }

    // 5 — Hakoniwa tile chrome: REAL subscription kill (findings 0054, P2 review). The chrome used to be
    // painted from hard literals that ignored theme. Build the ACTUAL BackcastWorkspaceRoot (the same
    // headless compose BackcastWorkspaceProbe uses: scene open + reflect _font + ResolvePaths +
    // ---- helpers ----
    static GameObject Spawn(string name, params Type[] components)
    {
        var go = new GameObject(name, components);
        _spawned.Add(go);
        return go;
    }

    static bool Approx(Color a, Color b) =>
        Mathf.Approximately(a.r, b.r) && Mathf.Approximately(a.g, b.g) &&
        Mathf.Approximately(a.b, b.b) && Mathf.Approximately(a.a, b.a);

    static void Eq(Color got, Color want, string msg)
    {
        if (!Approx(got, want)) _fails.Add(msg + $" (got {got}, want {want})");
    }

    static void Eq(string got, string want, string msg)
    {
        if (got != want) _fails.Add(msg + $" (got \"{got}\", want \"{want}\")");
    }

    static void Ne(Color got, Color other, string msg)
    {
        if (Approx(got, other)) _fails.Add(msg + $" (both {got})");
    }

    static void True(bool cond, string msg)
    {
        if (!cond) _fails.Add(msg);
    }
}
