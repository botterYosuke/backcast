// ThemeHitlHarness.cs — issue #44 "theme（配色）システム" (HITL playmode gate, AC#4)
//
// Owner-run montage that shows ALL themed surface kinds on one screen — a scenario panel
// (ScenarioStartupTile), a candle strip (status.long/short on colors.background), a depth
// ladder (status.bid/ask), a Python editor snippet (PythonSyntaxMeshEffect), and the two
// floating-window accents (players[0]/[2]) — so a theme switch can be eyeballed for the
// per-frame re-render (mesh/candles) the AFK ThemeProbe cannot see (findings 0020 Q9).
//
// Press T to toggle dark ↔ NonDefault (the verification palette). Because shipped dark==light
// (案A; real light is #51), the toggle target is NonDefault so the switch is actually visible.
// SetTheme fires ThemeService.Changed, which this harness subscribes to → ApplyTheme().
//
// The montage's own graphics (chart/ladder/accents) are exposed via Samples so ThemeProbe can
// reuse this exact production-path wiring for its non-vacuous switch kill instead of re-painting
// its own throwaway graphics.

using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

public class ThemeHitlHarness : MonoBehaviour
{
    const bool AutoBootstrapEnabled = false; // owner flips ON to claim Play (single Play-owner rule)

    Font _font;
    ScenarioStartupTile _tile;
    PythonSyntaxMeshEffect _syntax;
    ChartView _chartView;   // #53: the REAL production candlestick part (was a fake swatch pre-#53)
    Image _scenarioPanelImg;   // #137 review HIGH 2: scenario column panel face (was unpainted Unity-white)
    readonly Dictionary<string, Graphic> _samples = new Dictionary<string, Graphic>();
    bool _alt; // false = dark, true = NonDefault

    // For ThemeProbe: the montage's own direct graphics keyed by semantic role.
    public IReadOnlyDictionary<string, Graphic> Samples => _samples;
    public PythonSyntaxMeshEffect SyntaxEffect => _syntax;
    public ChartView ChartView => _chartView;   // #53: lets ThemeProbe value-assert the title bar

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootstrap()
    {
        if (!AutoBootstrapEnabled) return;
        if (Application.isBatchMode) return;
        var go = new GameObject("ThemeHitlHarness");
        DontDestroyOnLoad(go);
        go.AddComponent<ThemeHitlHarness>();
    }

    void Start()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var canvasGo = new GameObject("ThemeCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        if (UnityEngine.EventSystems.EventSystem.current == null)
        {
            var es = new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.EventSystems.StandaloneInputModule));
            es.transform.SetParent(transform, false);
        }

        var root = new GameObject("Root", typeof(RectTransform)).GetComponent<RectTransform>();
        root.SetParent(canvasGo.transform, false);
        root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero; root.offsetMax = Vector2.zero;

        BuildMontage(root);
        ThemeService.Changed += ApplyTheme;

        Debug.Log("[THEME HITL] montage built. Press T to toggle dark ↔ NonDefault.");
    }

    void OnDestroy() => ThemeService.Changed -= ApplyTheme;

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb[Key.T].wasPressedThisFrame)
        {
            _alt = !_alt;
            ThemeService.SetTheme(_alt ? Theme.NonDefault() : Theme.Dark()); // fires Changed → ApplyTheme
            Debug.Log("[THEME HITL] switched to " + (_alt ? "NonDefault" : "dark"));
        }
    }

    // Build the montage. EditMode-safe (no Play required): ThemeProbe calls this directly after
    // AddComponent, then ApplyTheme() + reads Samples for the switch kill.
    public void BuildMontage(RectTransform parent)
    {
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        // -- scenario panel (left column) — covers panel_background / element_background / text / error --
        var panel = Panel(parent, "panel", new Vector2(0f, 0f), new Vector2(0.34f, 1f));
        _scenarioPanelImg = panel.GetComponent<Image>();   // #137 review HIGH 2: retain for ApplyTheme repaint
        _tile = new ScenarioStartupTile(new ScenarioStartupController(), _font);
        _tile.Build(panel);

        // -- chart strip (top-right) — the REAL production ChartView (#53→#155 Mesh upgrade). A 2-bar
        // mock (one bearish, one bullish) gives ThemeProbe a real candle of EACH direction to sample,
        // so AC③ verifies the PRODUCTION part's color switch (findings 0023 + 0119). No Image on the
        // host — ChartView IS a MaskableGraphic (S1 #155) and paints its own themed bg + candles in
        // ONE Mesh batch. samples["chart_bg/candle_up/candle_down"] are retired — ThemeProbe reads
        // `harness.ChartView.BackgroundColor` / `FirstCandleColor(bool)` directly (Color, not Graphic). --
        var chartGo = new GameObject("chart_strip", typeof(RectTransform));
        var chartArea = chartGo.GetComponent<RectTransform>();
        chartArea.SetParent(parent, false);
        chartArea.anchorMin = new Vector2(0.36f, 0.55f); chartArea.anchorMax = new Vector2(1f, 1f);
        chartArea.offsetMin = new Vector2(4, 4); chartArea.offsetMax = new Vector2(-4, -4);
        _chartView = chartGo.AddComponent<ChartView>();
        _chartView.Build(chartArea, showTitleBar: true);
        _chartView.Render(MockTwoBarFrame());

        // -- depth ladder (mid-right) — the REAL production DepthLadderView (#54), not fake Text rows.
        // A mock snapshot (one ask + one bid) gives ThemeProbe a real best-ask/best-bid of EACH side to
        // sample, so AC③ verifies the PRODUCTION part's color switch (findings 0024). No Image on the
        // host — DepthLadderView paints its own themed bg. (Plain RectTransform: Destroy in EditMode is
        // illegal.) --
        var ladderGo = new GameObject("ladder", typeof(RectTransform));
        var ladderArea = ladderGo.GetComponent<RectTransform>();
        ladderArea.SetParent(parent, false);
        ladderArea.anchorMin = new Vector2(0.36f, 0.30f); ladderArea.anchorMax = new Vector2(1f, 0.53f);
        ladderArea.offsetMin = new Vector2(4, 4); ladderArea.offsetMax = new Vector2(-4, -4);
        var ladderView = ladderGo.AddComponent<DepthLadderView>();
        ladderView.Build(ladderArea);
        ladderView.Render(MockDepth(), lastPrice: 101.45);   // mid → LAST row shows a value (#54 follow-up)
        _samples["ladder_bg"] = ladderView.Background;
        _samples["ladder_ask"] = ladderView.BestAsk();
        _samples["ladder_bid"] = ladderView.BestBid();
        _samples["ladder_last"] = ladderView.LastRow();      // TTWR LAST row (status.warning)

        // -- editor snippet (bottom-right) — syntax palette + editor bg/text --
        var editorBg = Panel(parent, "editor", new Vector2(0.36f, 0f), new Vector2(0.78f, 0.28f));
        _samples["editor_bg"] = editorBg.GetComponent<Image>();
        // #120: the editor snippet is now TMP_Text/SDF (the production editing surface migrated off
        // legacy uGUI Text), so a theme switch is eyeballed on the SAME pipeline the app ships.
        var codeGo = new GameObject("code", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI), typeof(PythonSyntaxMeshEffect));
        var codeRt = (RectTransform)codeGo.transform; codeRt.SetParent(editorBg, false);
        codeRt.anchorMin = Vector2.zero; codeRt.anchorMax = Vector2.one; codeRt.offsetMin = new Vector2(6, 6); codeRt.offsetMax = new Vector2(-6, -6);
        var code = codeGo.GetComponent<TextMeshProUGUI>();
        var codeFont = Resources.Load<TMP_FontAsset>(StrategyEditorContentBuilder.EditorSdfFontResourcesPath);
        code.font = codeFont != null ? codeFont : (TMP_Settings.instance != null ? TMP_Settings.defaultFontAsset : null);
        code.fontSize = 13; code.richText = false; code.alignment = TextAlignmentOptions.TopLeft;
        code.textWrappingMode = TextWrappingModes.NoWrap; code.overflowMode = TextOverflowModes.Overflow;
        // code base color (PythonSyntaxMeshEffect uses Text.color for uncovered glyphs) must repaint too.
        _samples["code_text"] = code;
        const string sample = "@decorator\ndef strategy(self):  # comment\n    n = 42\n    return \"buy\"";
        code.text = sample;
        _syntax = codeGo.GetComponent<PythonSyntaxMeshEffect>();
        _syntax.SetTokens(PythonHighlighter.Tokenize(sample));

        // -- floating-window accents (bottom-right corner) — players[0] / players[2] --
        var accents = Panel(parent, "accents", new Vector2(0.80f, 0f), new Vector2(1f, 0.28f));
        _samples["accents_bg"] = accents.GetComponent<Image>();
        _samples["accent_editor"] = Swatch(accents, new Vector2(0.1f, 0.55f), new Vector2(0.9f, 0.9f));
        _samples["accent_order"] = Swatch(accents, new Vector2(0.1f, 0.1f), new Vector2(0.9f, 0.45f));

        ApplyTheme();
    }

    // Repaint the montage's own graphics from the active theme, then delegate to the surfaces that
    // own their re-apply (tile, syntax effect). Subscribed to ThemeService.Changed.
    public void ApplyTheme()
    {
        var t = ThemeService.Current;
        // chart_bg / candle_up / candle_down are owned by ChartView (#53), and ladder_bg / ladder_bid /
        // ladder_ask are owned by DepthLadderView (#54) — both self-subscribe to ThemeService.Changed
        // and re-paint themselves, so the montage no longer sets them here.
        Set("editor_bg", t.colors.background);
        Set("code_text", t.colors.text);   // set before _syntax re-render (base color for uncovered glyphs)
        Set("accents_bg", t.colors.surface_background);
        Set("accent_editor", t.players.Get(0));
        Set("accent_order", t.players.Get(2));
        // #137 review HIGH 2: paint the scenario column face from panel_background (was Unity default white
        // because Panel() never tints its Image — the tile only paints its own children, not the host face).
        if (_scenarioPanelImg != null) _scenarioPanelImg.color = t.colors.panel_background;
        _tile?.ApplyTheme();
        _syntax?.ApplyTheme();
    }

    void Set(string role, Color c)
    {
        if (_samples.TryGetValue(role, out var g) && g != null) g.color = c;
    }

    // Deterministic 2-bar mock for the chart strip: bar0 bearish (close<open), bar1 bullish
    // (close>open) — so ChartView.FirstCandle(true) and (false) both resolve for ThemeProbe (#53).
    static ReplayBarFrame MockTwoBarFrame()
    {
        var pts = new OhlcPoint[]
        {
            new OhlcPoint { open_time_ms = 0,      open = 100, high = 101, low = 94, close = 95,  volume = 1 },
            new OhlcPoint { open_time_ms = 60_000, open = 95,  high = 106, low = 94, close = 105, volume = 1 },
        };
        return new ReplayBarFrame { Ohlc = pts, Price = 105, TimestampMs = 60_000 };
    }

    // ---- widget helpers ----
    RectTransform Panel(RectTransform parent, string name, Vector2 anchorMin, Vector2 anchorMax)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
        rt.offsetMin = new Vector2(4, 4); rt.offsetMax = new Vector2(-4, -4);
        return rt;
    }

    Image Swatch(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        var go = new GameObject("swatch", typeof(RectTransform), typeof(Image));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = anchorMin; rt.anchorMax = anchorMax;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        return go.GetComponent<Image>();
    }

    // Deterministic 1×1 mock depth for the ladder strip: one ask + one bid so DepthLadderView's
    // BestAsk()/BestBid() both resolve for ThemeProbe (#54, findings 0024).
    static DepthSnapshotView MockDepth() => new DepthSnapshotView
    {
        HasDepth = true,
        Asks = new[] { new DepthLevelView { Price = 101.5, Size = 300 } },
        Bids = new[] { new DepthLevelView { Price = 101.4, Size = 250 } },
        TimestampMs = 1,
    };
}
