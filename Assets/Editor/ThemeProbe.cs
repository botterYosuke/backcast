// ThemeProbe.cs — issue #44 "theme（配色）システム" (THROWAWAY AFK regression gate, AC#4)
//
// Headless, Python-FREE gate for the theme color system. Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod ThemeProbe.Run -logFile <log>
//   # expect: [THEME PASS] ... / exit=0
//
// Four sections (findings 0020 Q9):
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

using System;
using System.Collections.Generic;
using UnityEditor;
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
            Section2_NonDefaultDiffers();
            Section3_ServiceSemantics();
            Section4_WiringKill();
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
        // workspace_background is an owner literal (#7fa4be), NOT a scale step — assert the value AND that
        // it is DISTINCT from background, so the viewport field can't silently collapse back onto dark.
        Eq(t.colors.workspace_background, new Color(0.4980f, 0.6431f, 0.7451f), "colors.workspace_background == #7fa4be (owner literal)");
        Ne(t.colors.workspace_background, t.colors.background, "workspace_background != background (field stays distinct from content bg)");
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

    // 2 — the verification palette genuinely differs from dark.
    static void Section2_NonDefaultDiffers()
    {
        var dark = Theme.Dark();
        var nd = Theme.NonDefault();
        // Every role the Section4 switch-kill samples must be proven dark != NonDefault here, or
        // that kill could pass vacuously (stale == target) if the two ever coincided for that role.
        Ne(nd.colors.background, dark.colors.background, "NonDefault.background != dark");
        Ne(nd.colors.workspace_background, dark.colors.workspace_background, "NonDefault.workspace_background != dark");
        Ne(nd.colors.surface_background, dark.colors.surface_background, "NonDefault.surface_background != dark");
        Ne(nd.colors.panel_background, dark.colors.panel_background, "NonDefault.panel_background != dark");
        Ne(nd.colors.text, dark.colors.text, "NonDefault.text != dark");
        Ne(nd.status.@long, dark.status.@long, "NonDefault.long != dark");
        Ne(nd.status.@short, dark.status.@short, "NonDefault.short != dark");
        Ne(nd.status.bid, dark.status.bid, "NonDefault.bid != dark");
        Ne(nd.status.ask, dark.status.ask, "NonDefault.ask != dark");
        Ne(nd.status.warning, dark.status.warning, "NonDefault.warning != dark");
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

        // -- 4a ScenarioStartupTile (panel) --
        ThemeService.ResetForTests();
        var tileGo = Spawn("probe_tile", typeof(RectTransform));
        var tile = new ScenarioStartupTile(new ScenarioStartupController(), font);
        tile.Build(tileGo.GetComponent<RectTransform>());
        var tileBg = tileGo.GetComponent<Image>();
        Eq(tileBg.color, Theme.Dark().colors.panel_background, "tile bg == dark panel_background");
        ThemeService.SetTheme(Theme.NonDefault());
        tile.ApplyTheme();
        Eq(tileBg.color, ThemeService.Current.colors.panel_background, "tile bg switched to NonDefault panel_background");

        // -- 4b PythonSyntaxMeshEffect (syntax) --
        ThemeService.ResetForTests();
        var sxGo = Spawn("probe_syntax", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(PythonSyntaxMeshEffect));
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
        var candleUp = harness.Samples["candle_up"];
        var candleDown = harness.Samples["candle_down"];
        var ladderBid = harness.Samples["ladder_bid"];   // #54: best-bid row Text (production)
        var ladderAsk = harness.Samples["ladder_ask"];   // #54: best-ask row Text (production)
        var ladderLast = harness.Samples["ladder_last"]; // #54 follow-up: TTWR LAST row Text (production)
        // Guard BEFORE the .color reads: True() only records a fail (non-aborting), so without these
        // short-circuits a null sample would NRE and crash the whole gate instead of failing cleanly.
        True(candleUp != null, "ChartView produced a bullish candle to sample");
        True(candleDown != null, "ChartView produced a bearish candle to sample");
        True(ladderBid != null, "DepthLadderView produced a best-bid row to sample");
        True(ladderAsk != null, "DepthLadderView produced a best-ask row to sample");
        True(ladderLast != null, "DepthLadderView produced a LAST row to sample");
        Eq(harness.Samples["chart_bg"].color, d.colors.background, "ChartView (production) chart_bg == dark background");
        if (candleUp != null) Eq(candleUp.color, d.status.@long, "ChartView (production) candle_up == dark long");
        if (candleDown != null) Eq(candleDown.color, d.status.@short, "ChartView (production) candle_down == dark short");

        // -- 4c-ii title bar (#53): the 2-bar mock is firstOpen=100 / lastClose=105 → +5.00% gain.
        // Value-asserts the NEW title port (price/change% formatting incl. sign) AND that the change%
        // color follows the theme (long when gain) — neither was gated before. --
        var title = harness.ChartView;
        True(title != null, "montage exposed the production ChartView");
        if (title != null)
        {
            Eq(title.PriceText.text, "105.00", "ChartView title price == last close (105)");
            Eq(title.ChangeText.text, "+5.00%", "ChartView title change% == +5.00% (mock +5% gain)");
            Eq(title.ChangeText.color, d.status.@long, "ChartView title change% colored long (gain) under dark");
        }
        if (ladderBid != null) Eq(ladderBid.color, d.status.bid, "DepthLadderView (production) ladder_bid == dark bid");
        if (ladderAsk != null) Eq(ladderAsk.color, d.status.ask, "DepthLadderView (production) ladder_ask == dark ask");
        if (ladderLast != null) Eq(ladderLast.color, d.status.warning, "DepthLadderView (production) ladder_last == dark warning");
        Eq(harness.Samples["accent_editor"].color, d.players.Get(0), "montage accent_editor == dark players[0]");
        Eq(harness.Samples["accent_order"].color, d.players.Get(2), "montage accent_order == dark players[2]");
        // ladder_bg == colors.background (TTWR overlays_ladder.rs:206 pane bg parity, #54 findings 0024).
        Eq(harness.Samples["ladder_bg"].color, d.colors.background, "DepthLadderView (production) ladder_bg == dark background");
        Eq(harness.Samples["editor_bg"].color, d.colors.background, "montage editor_bg == dark background");
        Eq(harness.Samples["accents_bg"].color, d.colors.surface_background, "montage accents_bg == dark surface");
        Eq(harness.Samples["code_text"].color, d.colors.text, "montage code_text == dark text");
        Eq(harness.SyntaxEffect.keyword, d.syntax.keyword, "montage syntax keyword == dark");

        ThemeService.SetTheme(Theme.NonDefault());
        harness.ApplyTheme();
        var nd = ThemeService.Current;
        Eq(harness.Samples["ladder_bg"].color, nd.colors.background, "DepthLadderView (production) ladder_bg switched");
        Eq(harness.Samples["editor_bg"].color, nd.colors.background, "montage editor_bg switched");
        Eq(harness.Samples["accents_bg"].color, nd.colors.surface_background, "montage accents_bg switched");
        Eq(harness.Samples["code_text"].color, nd.colors.text, "montage code_text switched");
        Eq(harness.Samples["chart_bg"].color, nd.colors.background, "ChartView (production) chart_bg switched");
        if (candleUp != null) Eq(candleUp.color, nd.status.@long, "ChartView (production) candle_up switched");
        if (candleDown != null) Eq(candleDown.color, nd.status.@short, "ChartView (production) candle_down switched");
        // title change% color must ALSO follow the switch (gain stays long, now NonDefault's long) (#53).
        if (title != null) Eq(title.ChangeText.color, nd.status.@long, "ChartView title change% recolors on switch");
        if (ladderBid != null) Eq(ladderBid.color, nd.status.bid, "DepthLadderView (production) ladder_bid switched");
        if (ladderAsk != null) Eq(ladderAsk.color, nd.status.ask, "DepthLadderView (production) ladder_ask switched");
        if (ladderLast != null) Eq(ladderLast.color, nd.status.warning, "DepthLadderView (production) ladder_last switched");
        Eq(harness.Samples["accent_editor"].color, nd.players.Get(0), "montage accent_editor switched");
        Eq(harness.Samples["accent_order"].color, nd.players.Get(2), "montage accent_order switched");
        Eq(harness.SyntaxEffect.keyword, nd.syntax.keyword, "montage syntax keyword switched");
    }

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
