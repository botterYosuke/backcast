// ScenarioStartupTile.cs — issue #29 "Replay 実行設定パネル" (uGUI tile content)
//
// Builds + binds the Hakoniwa PanelKind::Startup tile content (slot 0; TTWR
// populate_startup_tile parity) as legacy uGUI, and wires it to ScenarioStartupController.
// A PLAIN C# builder (not a MonoBehaviour): the harness owns the GameObject lifecycle and
// the Run trigger; this class only constructs widgets and forwards edits to the controller.
//
// FIELDS (CONTEXT "run 期間 (start/end) vs lookback" + universe SoT):
//   Start / End (YYYY-MM-DD InputFields) · Granularity (Daily | Minute toggle buttons) ·
//   Initial cash (InputField) · Universe (one InputField, whitespace/comma-separated ids →
//   InstrumentRegistry.ReplaceAll — the minimal text-list editor #31's picker later replaces) ·
//   per-field error labels.
//
// #76 S6b-β-clean U5: the tile is SCENARIO-EDITING-ONLY. The Run button + run-readiness display
// moved to the Strategy Editor title bar (RunReadinessViewModel / StrategyEditorRunButton); the
// tile keeps the scenario fields and their per-field error labels. Every edit calls the controller
// setter then Refresh(), so the field errors are visible live.
//
// COLORS (issue #44): no inline color constants — every graphic reads ThemeService.Current
// (findings 0020). Retained graphic refs let ApplyTheme() repaint on a theme switch; the
// owning harness wires `ThemeService.Changed += tile.ApplyTheme`.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

public sealed class ScenarioStartupTile
{
    readonly ScenarioStartupController _ctrl;
    readonly Font _font;

    InputField _startField, _endField, _cashField, _universeField;
    Button _dailyBtn, _minuteBtn;
    Text _startErr, _endErr, _cashErr, _universeErr, _granErr;

    // Retained themed graphics (issue #44) so ApplyTheme() can repaint on a theme switch.
    Image _tileBg;
    readonly List<Image> _fieldBgs = new List<Image>();   // role: hakoniwa_tile_background (findings 0054 — Hakoniwa-isolated inset surface)
    readonly List<Text> _bodyTexts = new List<Text>();    // role: hakoniwa_text
    readonly List<Text> _errorTexts = new List<Text>();   // role: status.error

    public ScenarioStartupTile(ScenarioStartupController ctrl, Font font)
    {
        _ctrl = ctrl ?? throw new ArgumentNullException(nameof(ctrl));
        _font = font;
    }

    // Build the tile UI under `tile` (a full-stretch RectTransform owned by the harness).
    public void Build(RectTransform tile)
    {
        var bg = tile.gameObject.GetComponent<Image>();
        if (bg == null) bg = tile.gameObject.AddComponent<Image>();
        _tileBg = bg;
        bg.color = ThemeService.Current.colors.hakoniwa_panel_surface;   // findings 0054: Hakoniwa-isolated startup surface

        float y = -8f;
        const float rowH = 22f, errH = 14f, gap = 2f;

        MakeLabel(tile, "Replay Scenario", ref y, 18f, bold: true);
        y -= 4f;

        _startField = MakeField(tile, "Start (YYYY-MM-DD)", ref y, rowH, v => { _ctrl.SetStart(v); Refresh(); });
        _startErr = MakeError(tile, ref y, errH);
        _endField = MakeField(tile, "End (YYYY-MM-DD)", ref y, rowH, v => { _ctrl.SetEnd(v); Refresh(); });
        _endErr = MakeError(tile, ref y, errH);

        MakeLabel(tile, "Granularity", ref y, rowH);
        _dailyBtn = MakeButton(tile, "Daily", 0f, ref y, rowH, () => { _ctrl.SetGranularity(GranularityChoice.Daily); Refresh(); }, advanceY: false);
        _minuteBtn = MakeButton(tile, "Minute", 0.5f, ref y, rowH, () => { _ctrl.SetGranularity(GranularityChoice.Minute); Refresh(); }, advanceY: true);
        _granErr = MakeError(tile, ref y, errH);

        _cashField = MakeField(tile, "Initial cash", ref y, rowH, v => { _ctrl.SetInitialCash(v); Refresh(); });
        _cashErr = MakeError(tile, ref y, errH);

        _universeField = MakeField(tile, "Universe (ids, comma/space)", ref y, rowH, OnUniverseChanged);
        // On blur/submit, re-pull the field from the SoT (never ReplaceAll the text). A field that
        // went stale while focused (the focus-guarded skip path) otherwise survives, so the NEXT
        // keystroke's OnUniverseChanged would ReplaceAll(stale ids) and erase a sidebar add
        // (findings 0025 §12, Finding 2).
        _universeField.onEndEdit.AddListener(_ => PullUniverseField());
        _universeErr = MakeError(tile, ref y, errH);

        SyncFieldsFromController();
        Refresh();

        // Keep the held-mode universe field live when the SHARED SoT is edited elsewhere (#31
        // sidebar/picker). Dispose() unsubscribes — the owning host calls it on teardown.
        _ctrl.Universe.Changed += OnUniverseRegistryChanged;
    }

    // Pull controller state into the fields (after Populate / restore).
    public void SyncFieldsFromController()
    {
        if (_startField != null) _startField.SetTextWithoutNotify(_ctrl.Params.Start ?? "");
        if (_endField != null) _endField.SetTextWithoutNotify(_ctrl.Params.End ?? "");
        if (_cashField != null) _cashField.SetTextWithoutNotify(_ctrl.Params.InitialCash ?? "");
        PullUniverseField();
        Refresh();
    }

    // Re-pull ONLY the universe text field from the shared SoT (never the reverse — this never
    // mutates the registry). Used after build/restore, on focus-loss recovery (onEndEdit), and when
    // the SoT changes elsewhere (#31 sidebar/picker, #59 one-universe-per-workspace).
    void PullUniverseField()
    {
        if (_universeField != null)
            _universeField.SetTextWithoutNotify(string.Join(", ", _ctrl.Universe.Ids));
    }

    void OnUniverseChanged(string raw)
    {
        var ids = new List<string>();
        foreach (string tok in raw.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            ids.Add(tok.Trim());
        _ctrl.Universe.ReplaceAll(ids);
        _ctrl.Params.Dirty = true;
        Refresh();
    }

    // The universe SoT changed from ELSEWHERE (#31 sidebar/picker editing the SHARED
    // InstrumentRegistry, #59 one-universe-per-workspace). Re-pull the held-mode text field so a
    // subsequent edit here does not ReplaceAll(stale ids) and erase the sidebar's add. FOCUS-GUARD:
    // skip the text rewrite while the user is typing in this field (the field is the live editor
    // then — and our own OnUniverseChanged→ReplaceAll re-enters here; rewriting mid-type would
    // reformat under the caret). Refresh() still re-derives error labels / Run enablement.
    void OnUniverseRegistryChanged()
    {
        if (_universeField != null && !_universeField.isFocused)
            PullUniverseField();
        Refresh();
    }

    // Unsubscribe from the shared universe SoT so a destroyed tile leaves no orphan handler
    // pinning the controller. Idempotent; the owning host calls this on teardown.
    public void Dispose()
    {
        if (_ctrl?.Universe != null) _ctrl.Universe.Changed -= OnUniverseRegistryChanged;
    }

    // Recompute the per-field error labels + the granularity selection highlight from validation.
    // #76 S6b-β-clean U5: the tile is scenario-editing-only — Run + run-readiness moved to the
    // Strategy Editor title bar (RunReadinessViewModel / StrategyEditorRunButton).
    public void Refresh()
    {
        var e = _ctrl.Validate();
        SetErr(_startErr, e.Start);
        SetErr(_endErr, e.End ?? e.CrossField);
        SetErr(_granErr, e.Granularity);
        SetErr(_cashErr, e.InitialCash);
        SetErr(_universeErr, e.Universe);

        Highlight(_dailyBtn, _ctrl.Params.Granularity == GranularityChoice.Daily);
        Highlight(_minuteBtn, _ctrl.Params.Granularity == GranularityChoice.Minute);
    }

    // Repaint every retained graphic from the active theme (issue #44). Called by the owning
    // harness on ThemeService.Changed. Re-runs Refresh() so the granularity selection highlight
    // (element_selected vs hakoniwa_tile_background) re-derives from controller state under the new theme.
    public void ApplyTheme()
    {
        var t = ThemeService.Current;
        if (_tileBg != null) _tileBg.color = t.colors.hakoniwa_panel_surface;
        foreach (var img in _fieldBgs) if (img != null) img.color = t.colors.hakoniwa_tile_background;
        foreach (var txt in _bodyTexts) if (txt != null) txt.color = t.colors.hakoniwa_text;
        foreach (var txt in _errorTexts) if (txt != null) txt.color = t.status.error;
        Refresh();
    }

    // ---- widget helpers ----
    void SetErr(Text t, string msg)
    {
        if (t == null) return;
        t.text = msg ?? "";
        t.enabled = !string.IsNullOrEmpty(msg);
    }

    void Highlight(Button b, bool on)
    {
        if (b == null) return;
        var img = b.GetComponent<Image>();
        if (img == null) return;
        var c = ThemeService.Current.colors;
        img.color = on ? c.element_selected : c.hakoniwa_tile_background;   // findings 0054: off-state matches inset surface
    }

    Text MakeLabel(RectTransform parent, string text, ref float y, float h, bool bold = false)
    {
        var go = new GameObject("label", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Anchor(rt, 0.04f, 0.96f, y, h);
        var t = go.GetComponent<Text>();
        t.font = _font; t.color = ThemeService.Current.colors.hakoniwa_text; t.text = text; t.fontSize = bold ? 14 : 11;
        t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        t.alignment = TextAnchor.MiddleLeft;
        _bodyTexts.Add(t);
        y -= h + 2f;
        return t;
    }

    InputField MakeField(RectTransform parent, string label, ref float y, float h, UnityEngine.Events.UnityAction<string> onChanged)
    {
        MakeLabel(parent, label, ref y, 13f);
        var go = new GameObject("field", typeof(RectTransform), typeof(Image), typeof(InputField));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Anchor(rt, 0.04f, 0.96f, y, h);
        var fieldBg = go.GetComponent<Image>();
        fieldBg.color = ThemeService.Current.colors.hakoniwa_tile_background;
        _fieldBgs.Add(fieldBg);

        var textGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var trt = textGo.GetComponent<RectTransform>();
        trt.SetParent(rt, false);
        Stretch(trt, 4f);
        var txt = textGo.GetComponent<Text>();
        txt.font = _font; txt.color = ThemeService.Current.colors.hakoniwa_text; txt.fontSize = 11; txt.alignment = TextAnchor.MiddleLeft;
        txt.supportRichText = false;
        _bodyTexts.Add(txt);

        var field = go.GetComponent<InputField>();
        field.textComponent = txt;
        field.onValueChanged.AddListener(onChanged);
        y -= h + 2f;
        return field;
    }

    Text MakeError(RectTransform parent, ref float y, float h)
    {
        var go = new GameObject("err", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Anchor(rt, 0.04f, 0.96f, y, h);
        var t = go.GetComponent<Text>();
        t.font = _font; t.color = ThemeService.Current.status.error; t.fontSize = 10; t.alignment = TextAnchor.MiddleLeft;
        t.enabled = false;
        _errorTexts.Add(t);
        y -= h + 1f;
        return t;
    }

    Button MakeButton(RectTransform parent, string label, float xMin, ref float y, float h,
        UnityEngine.Events.UnityAction onClick, bool advanceY)
    {
        var go = new GameObject("btn:" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        float left = 0.04f + xMin * 0.92f;
        float right = 0.04f + (xMin + 0.5f) * 0.92f - 0.01f;
        Anchor(rt, left, right, y, h);
        go.GetComponent<Image>().color = ThemeService.Current.colors.hakoniwa_tile_background;

        var textGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var trt = textGo.GetComponent<RectTransform>();
        trt.SetParent(rt, false);
        Stretch(trt, 0f);
        var t = textGo.GetComponent<Text>();
        t.font = _font; t.color = ThemeService.Current.colors.hakoniwa_text; t.text = label; t.fontSize = 12;
        t.alignment = TextAnchor.MiddleCenter;
        _bodyTexts.Add(t);

        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(onClick);
        if (advanceY) y -= h + 2f;
        return btn;
    }

    // Anchor a row at vertical pixel offset `yTop` (from the tile top), height `h`, between
    // normalized x [xMin,xMax]. Uses top-stretch anchors so rows stack from the top.
    static void Anchor(RectTransform rt, float xMin, float xMax, float yTop, float h)
    {
        rt.anchorMin = new Vector2(xMin, 1f);
        rt.anchorMax = new Vector2(xMax, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(0f, 0f);
        rt.offsetMax = new Vector2(0f, 0f);
        rt.anchoredPosition = new Vector2(0f, yTop);
        rt.sizeDelta = new Vector2(0f, h);
    }

    static void Stretch(RectTransform rt, float pad)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad);
        rt.offsetMax = new Vector2(-pad, -pad);
    }
}
