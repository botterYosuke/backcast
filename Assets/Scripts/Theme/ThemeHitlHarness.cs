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
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

public class ThemeHitlHarness : MonoBehaviour
{
    const bool AutoBootstrapEnabled = false; // owner flips ON to claim Play (single Play-owner rule)

    Font _font;
    ScenarioStartupTile _tile;
    PythonSyntaxMeshEffect _syntax;
    readonly Dictionary<string, Graphic> _samples = new Dictionary<string, Graphic>();
    bool _alt; // false = dark, true = NonDefault

    // For ThemeProbe: the montage's own direct graphics keyed by semantic role.
    public IReadOnlyDictionary<string, Graphic> Samples => _samples;
    public PythonSyntaxMeshEffect SyntaxEffect => _syntax;

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
        _tile = new ScenarioStartupTile(new ScenarioStartupController(), null, _font);
        _tile.Build(panel);

        // -- chart strip (top-right) — colors.background bg + status.long/short candles --
        var chartBg = Panel(parent, "chart_bg", new Vector2(0.36f, 0.55f), new Vector2(1f, 1f));
        chartBg.GetComponent<Image>().color = ThemeService.Current.colors.background;
        _samples["chart_bg"] = chartBg.GetComponent<Image>();
        _samples["candle_up"] = Swatch(chartBg, new Vector2(0.1f, 0.2f), new Vector2(0.35f, 0.9f));
        _samples["candle_down"] = Swatch(chartBg, new Vector2(0.55f, 0.1f), new Vector2(0.8f, 0.7f));

        // -- depth ladder (mid-right) — status.bid / status.ask --
        var ladder = Panel(parent, "ladder", new Vector2(0.36f, 0.30f), new Vector2(1f, 0.53f));
        _samples["ladder_bg"] = ladder.GetComponent<Image>();
        _samples["ladder_ask"] = LadderRow(ladder, "ASK  101.5  x300", 0.55f);
        _samples["ladder_bid"] = LadderRow(ladder, "BID  101.4  x250", 0.15f);

        // -- editor snippet (bottom-right) — syntax palette + editor bg/text --
        var editorBg = Panel(parent, "editor", new Vector2(0.36f, 0f), new Vector2(0.78f, 0.28f));
        _samples["editor_bg"] = editorBg.GetComponent<Image>();
        var codeGo = new GameObject("code", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(PythonSyntaxMeshEffect));
        var codeRt = (RectTransform)codeGo.transform; codeRt.SetParent(editorBg, false);
        codeRt.anchorMin = Vector2.zero; codeRt.anchorMax = Vector2.one; codeRt.offsetMin = new Vector2(6, 6); codeRt.offsetMax = new Vector2(-6, -6);
        var code = codeGo.GetComponent<Text>();
        code.font = _font; code.fontSize = 13; code.supportRichText = false; code.alignment = TextAnchor.UpperLeft;
        code.horizontalOverflow = HorizontalWrapMode.Overflow; code.verticalOverflow = VerticalWrapMode.Overflow;
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
        Set("chart_bg", t.colors.background);
        Set("candle_up", t.status.@long);
        Set("candle_down", t.status.@short);
        Set("ladder_bg", t.colors.surface_background);
        Set("ladder_bid", t.status.bid);
        Set("ladder_ask", t.status.ask);
        Set("editor_bg", t.colors.background);
        Set("code_text", t.colors.text);   // set before _syntax re-render (base color for uncovered glyphs)
        Set("accents_bg", t.colors.surface_background);
        Set("accent_editor", t.players.Get(0));
        Set("accent_order", t.players.Get(2));
        _tile?.ApplyTheme();
        _syntax?.ApplyTheme();
    }

    void Set(string role, Color c)
    {
        if (_samples.TryGetValue(role, out var g) && g != null) g.color = c;
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

    Text LadderRow(RectTransform parent, string label, float yMin)
    {
        var go = new GameObject("row", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0.05f, yMin); rt.anchorMax = new Vector2(0.95f, yMin + 0.3f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        var t = go.GetComponent<Text>();
        t.font = _font; t.fontSize = 14; t.text = label; t.alignment = TextAnchor.MiddleLeft;
        return t;
    }
}
