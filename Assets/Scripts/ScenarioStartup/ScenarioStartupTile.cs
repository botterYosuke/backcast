// ScenarioStartupTile.cs — issue #29 "Replay 実行設定パネル" (uGUI tile content)
//
// Builds + binds the Scenario section content (TTWR populate_startup_tile parity) as legacy uGUI, and
// wires it to ScenarioStartupController. A PLAIN C# builder (not a MonoBehaviour): the harness owns the
// GameObject lifecycle and the Run trigger; this class only constructs widgets and forwards edits to the
// controller.
//
// #137 S1/S3 REDESIGN (findings 0107 F1/D5): the tile is now hosted ONLY inside the Settings modal's
// Scenario card (ADR-0026 re-home) + ThemeHitlHarness preview — it is NOT on the Hakoniwa canvas, so its
// input面 moves OFF the Hakoniwa-isolated roles (findings 0054) ONTO the Settings input roles:
//   * each InputField gets a visible BORDER (Outline, role `border`), a PLACEHOLDER (role `text_placeholder`)
//     and a sunk fill (role `surface_background`) so it reads as an input — the owner's #1 不満点 (S1).
//   * labels are MUTED (role `text_muted`) and laid out LEFT-of the field in a 2-COLUMN row (S3) so the
//     label↔field correspondence is obvious. Field body text = role `text`.
//   * the card (SettingsModalOverlay) provides the surrounding surface, so the tile no longer paints a tile
//     background of its own (transparent) and drops its redundant inline title (the card header is "SCENARIO").
//
// FIELDS (CONTEXT "run 期間 (start/end) vs lookback" + universe SoT):
//   Start / End (YYYY-MM-DD) · Granularity (Daily | Minute) · Initial cash · Universe (comma/space ids →
//   InstrumentRegistry.ReplaceAll) · per-field error labels.
//
// COLORS (issue #44 / findings 0020): no inline color constants — every graphic reads ThemeService.Current.
// Retained graphic refs let ApplyTheme() repaint on a theme switch; the owning harness wires
// `ThemeService.Changed += tile.ApplyTheme`.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public sealed class ScenarioStartupTile
{
    readonly ScenarioStartupController _ctrl;
    readonly Font _font;

    InputField _startField, _endField, _cashField, _universeField;
    Button _dailyBtn, _minuteBtn;
    Text _startErr, _endErr, _cashErr, _universeErr, _granErr;

    // Retained themed graphics (issue #44 / S1 redesign) so ApplyTheme() can repaint on a theme switch.
    Image _tileBg;
    readonly List<Image> _fieldBgs = new List<Image>();      // role: surface_background (sunk input面 — S1/D5)
    readonly List<Outline> _fieldBorders = new List<Outline>(); // role: border (the visible input frame — S1/D5)
    readonly List<Text> _placeholders = new List<Text>();    // role: text_placeholder (S1/D5)
    readonly List<Text> _labelTexts = new List<Text>();      // role: text_muted (the 2-col left labels — S1/S3)
    readonly List<Text> _bodyTexts = new List<Text>();       // role: text (field body + button captions)
    readonly List<Image> _btnBgs = new List<Image>();        // role: element_background (granularity off-state)
    readonly List<Text> _errorTexts = new List<Text>();      // role: status.error

    // 2-column layout (S3): label left, input right; shared spacing constants (findings 0107 D5).
    const float LabelXMin = 0.04f, LabelXMax = 0.40f, FieldXMin = 0.42f, FieldXMax = 0.96f;
    const float RowH = 22f, ErrH = 13f, RowGap = 4f;

    public ScenarioStartupTile(ScenarioStartupController ctrl, Font font)
    {
        _ctrl = ctrl ?? throw new ArgumentNullException(nameof(ctrl));
        _font = font;
    }

    // Build the tile UI under `tile` (a card content RectTransform owned by the harness/overlay).
    public void Build(RectTransform tile)
    {
        var bg = tile.gameObject.GetComponent<Image>();
        if (bg == null) bg = tile.gameObject.AddComponent<Image>();
        _tileBg = bg;
        bg.color = Color.clear;          // S3: the card provides the surface; the tile面 is transparent
        bg.raycastTarget = false;

        float y = -2f;

        _startField = MakeField(tile, "Start", "YYYY-MM-DD", ref y, v => { _ctrl.SetStart(v); Refresh(); });
        _startErr = MakeError(tile, ref y);
        _endField = MakeField(tile, "End", "YYYY-MM-DD", ref y, v => { _ctrl.SetEnd(v); Refresh(); });
        _endErr = MakeError(tile, ref y);

        MakeRowLabel(tile, "Granularity", y);
        _dailyBtn = MakeButton(tile, "Daily", FieldXMin, FieldXMin + (FieldXMax - FieldXMin) / 2f - 0.01f, y,
            () => { _ctrl.SetGranularity(GranularityChoice.Daily); Refresh(); });
        _minuteBtn = MakeButton(tile, "Minute", FieldXMin + (FieldXMax - FieldXMin) / 2f + 0.01f, FieldXMax, y,
            () => { _ctrl.SetGranularity(GranularityChoice.Minute); Refresh(); });
        y -= RowH + RowGap;
        _granErr = MakeError(tile, ref y);

        _cashField = MakeField(tile, "Initial cash", "1000000", ref y, v => { _ctrl.SetInitialCash(v); Refresh(); });
        _cashErr = MakeError(tile, ref y);

        _universeField = MakeField(tile, "Universe", "9984.TSE, 7203.TSE", ref y, OnUniverseChanged);
        // On blur/submit, re-pull the field from the SoT (never ReplaceAll the text). A field that went stale
        // while focused otherwise survives, so the NEXT keystroke's OnUniverseChanged would ReplaceAll(stale
        // ids) and erase a sidebar add (findings 0025 §12, Finding 2).
        _universeField.onEndEdit.AddListener(_ => PullUniverseField());
        _universeErr = MakeError(tile, ref y);

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

    // Re-pull ONLY the universe text field from the shared SoT (never the reverse — this never mutates the
    // registry). Used after build/restore, on focus-loss recovery (onEndEdit), and when the SoT changes
    // elsewhere (#31 sidebar/picker, #59 one-universe-per-workspace).
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

    // The universe SoT changed from ELSEWHERE (#31 sidebar/picker, #59). Re-pull the held-mode text field so
    // a subsequent edit here does not ReplaceAll(stale ids) and erase the sidebar's add. FOCUS-GUARD: skip
    // the rewrite while the user is typing in this field (the field is the live editor then — and our own
    // OnUniverseChanged→ReplaceAll re-enters here). Refresh() still re-derives error labels.
    void OnUniverseRegistryChanged()
    {
        if (_universeField != null && !_universeField.isFocused)
            PullUniverseField();
        Refresh();
    }

    // Unsubscribe from the shared universe SoT so a destroyed tile leaves no orphan handler. Idempotent.
    public void Dispose()
    {
        if (_ctrl?.Universe != null) _ctrl.Universe.Changed -= OnUniverseRegistryChanged;
    }

    // Recompute the per-field error labels + the granularity selection highlight from validation.
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

    // Repaint every retained graphic from the active theme (issue #44 / S1 roles). Called by the owning
    // harness on ThemeService.Changed. Re-runs Refresh() so the granularity selection highlight re-derives.
    public void ApplyTheme()
    {
        var c = ThemeService.Current.colors;
        if (_tileBg != null) _tileBg.color = Color.clear;
        foreach (var img in _fieldBgs) if (img != null) img.color = c.surface_background;
        foreach (var ol in _fieldBorders) if (ol != null) ol.effectColor = c.border;
        foreach (var ph in _placeholders) if (ph != null) ph.color = c.text_placeholder;
        foreach (var lbl in _labelTexts) if (lbl != null) lbl.color = c.text_muted;
        foreach (var txt in _bodyTexts) if (txt != null) txt.color = c.text;
        foreach (var bb in _btnBgs) if (bb != null) bb.color = c.element_background;
        foreach (var txt in _errorTexts) if (txt != null) txt.color = ThemeService.Current.status.error;
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
        img.color = on ? c.element_selected : c.element_background;
    }

    // A 2-column input row (S3): muted label LEFT, bordered+placeholdered field RIGHT. The field widget is
    // the shared ThemedInputFieldBuilder (so the Scenario card and the Data card stay identical — D5).
    // Advances `y`.
    InputField MakeField(RectTransform parent, string label, string placeholder, ref float y,
        UnityEngine.Events.UnityAction<string> onChanged)
    {
        MakeRowLabel(parent, label, y);

        var tf = ThemedInputFieldBuilder.Build(parent, _font, placeholder);
        Anchor(tf.field.GetComponent<RectTransform>(), FieldXMin, FieldXMax, y, RowH);
        _fieldBgs.Add(tf.fill);
        _fieldBorders.Add(tf.border);
        _placeholders.Add(tf.placeholder);
        _bodyTexts.Add(tf.body);

        tf.field.onValueChanged.AddListener(onChanged);
        y -= RowH + RowGap;
        return tf.field;
    }

    // A 2-col LEFT label (muted) on the row at `yTop` — does NOT advance y (the field shares the row).
    Text MakeRowLabel(RectTransform parent, string text, float yTop)
    {
        var go = new GameObject("label", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Anchor(rt, LabelXMin, LabelXMax, yTop, RowH);
        var t = go.GetComponent<Text>();
        t.font = _font; t.color = ThemeService.Current.colors.text_muted; t.text = text; t.fontSize = 11;
        t.alignment = TextAnchor.MiddleLeft;
        _labelTexts.Add(t);
        return t;
    }

    Text MakeError(RectTransform parent, ref float y)
    {
        var go = new GameObject("err", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Anchor(rt, FieldXMin, FieldXMax, y, ErrH);
        var t = go.GetComponent<Text>();
        t.font = _font; t.color = ThemeService.Current.status.error; t.fontSize = 10; t.alignment = TextAnchor.MiddleLeft;
        t.enabled = false;
        _errorTexts.Add(t);
        y -= ErrH + 1f;
        return t;
    }

    // A granularity toggle button (named btn:Daily / btn:Minute — pinned by SCENARIO-15). Off-state =
    // element_background, on-state highlight = element_selected (Highlight()).
    Button MakeButton(RectTransform parent, string label, float xMin, float xMax, float yTop,
        UnityEngine.Events.UnityAction onClick)
    {
        var go = new GameObject("btn:" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Anchor(rt, xMin, xMax, yTop, RowH);
        var img = go.GetComponent<Image>();
        img.color = ThemeService.Current.colors.element_background;
        _btnBgs.Add(img);

        var textGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var trt = textGo.GetComponent<RectTransform>();
        trt.SetParent(rt, false);
        Stretch(trt, 0f);
        var t = textGo.GetComponent<Text>();
        t.font = _font; t.color = ThemeService.Current.colors.text; t.text = label; t.fontSize = 12;
        t.alignment = TextAnchor.MiddleCenter;
        _bodyTexts.Add(t);

        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(onClick);
        return btn;
    }

    // Anchor a row at vertical pixel offset `yTop` (from the tile top), height `h`, between normalized x
    // [xMin,xMax]. Top-stretch anchors so rows stack from the top.
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
