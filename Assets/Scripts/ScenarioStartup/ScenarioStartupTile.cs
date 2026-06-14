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
//   per-field error labels · a Run button gated by validation + strategy supplyability.
//
// Every edit calls the controller setter then Refresh(): error labels + Run.interactable are
// recomputed from controller.Validate(), so AC④ (invalid → no run) is visible live.

using System;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

public sealed class ScenarioStartupTile
{
    readonly ScenarioStartupController _ctrl;
    readonly Action _onRun;
    readonly Font _font;

    InputField _startField, _endField, _cashField, _universeField;
    Button _dailyBtn, _minuteBtn, _runBtn;
    Text _startErr, _endErr, _cashErr, _universeErr, _granErr, _runMsg;

    static readonly Color PANEL_BG = new Color(0.13f, 0.13f, 0.16f, 1f);
    static readonly Color FIELD_BG = new Color(0.20f, 0.20f, 0.24f, 1f);
    static readonly Color TEXT = new Color(0.90f, 0.90f, 0.92f, 1f);
    static readonly Color ERR = new Color(0.92f, 0.45f, 0.45f, 1f);
    static readonly Color SEL = new Color(0.25f, 0.55f, 0.85f, 1f);
    static readonly Color BTN = new Color(0.28f, 0.28f, 0.34f, 1f);

    public ScenarioStartupTile(ScenarioStartupController ctrl, Action onRun, Font font)
    {
        _ctrl = ctrl ?? throw new ArgumentNullException(nameof(ctrl));
        _onRun = onRun;
        _font = font;
    }

    // Build the tile UI under `tile` (a full-stretch RectTransform owned by the harness).
    public void Build(RectTransform tile)
    {
        var bg = tile.gameObject.GetComponent<Image>();
        if (bg == null) bg = tile.gameObject.AddComponent<Image>();
        bg.color = PANEL_BG;

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
        _universeErr = MakeError(tile, ref y, errH);

        y -= 6f;
        _runBtn = MakeButton(tile, "Run Replay", 0f, ref y, 26f, OnRunClicked, advanceY: true, full: true);
        _runMsg = MakeError(tile, ref y, errH);

        SyncFieldsFromController();
        Refresh();
    }

    // Pull controller state into the fields (after Populate / restore).
    public void SyncFieldsFromController()
    {
        if (_startField != null) _startField.SetTextWithoutNotify(_ctrl.Params.Start ?? "");
        if (_endField != null) _endField.SetTextWithoutNotify(_ctrl.Params.End ?? "");
        if (_cashField != null) _cashField.SetTextWithoutNotify(_ctrl.Params.InitialCash ?? "");
        if (_universeField != null) _universeField.SetTextWithoutNotify(string.Join(", ", _ctrl.Universe.Ids));
        Refresh();
    }

    void OnUniverseChanged(string raw)
    {
        var ids = new System.Collections.Generic.List<string>();
        foreach (string tok in raw.Split(new[] { ',', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            ids.Add(tok.Trim());
        _ctrl.Universe.ReplaceAll(ids);
        _ctrl.Params.Dirty = true;
        Refresh();
    }

    void OnRunClicked()
    {
        // The harness's onRun consults TryStartRun(provider); here we only forward the intent.
        _onRun?.Invoke();
    }

    // Recompute error labels + granularity selection highlight + Run enablement from validation.
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

        if (_runBtn != null) _runBtn.interactable = !e.Any;
    }

    // Surface a run-gate block reason (no supplyable strategy / invalid scenario) under Run.
    public void ShowRunMessage(string msg) => SetErr(_runMsg, msg);

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
        if (img != null) img.color = on ? SEL : BTN;
    }

    Text MakeLabel(RectTransform parent, string text, ref float y, float h, bool bold = false)
    {
        var go = new GameObject("label", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Anchor(rt, 0.04f, 0.96f, y, h);
        var t = go.GetComponent<Text>();
        t.font = _font; t.color = TEXT; t.text = text; t.fontSize = bold ? 14 : 11;
        t.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
        t.alignment = TextAnchor.MiddleLeft;
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
        go.GetComponent<Image>().color = FIELD_BG;

        var textGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var trt = textGo.GetComponent<RectTransform>();
        trt.SetParent(rt, false);
        Stretch(trt, 4f);
        var txt = textGo.GetComponent<Text>();
        txt.font = _font; txt.color = TEXT; txt.fontSize = 11; txt.alignment = TextAnchor.MiddleLeft;
        txt.supportRichText = false;

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
        t.font = _font; t.color = ERR; t.fontSize = 10; t.alignment = TextAnchor.MiddleLeft;
        t.enabled = false;
        y -= h + 1f;
        return t;
    }

    Button MakeButton(RectTransform parent, string label, float xMin, ref float y, float h,
        UnityEngine.Events.UnityAction onClick, bool advanceY, bool full = false)
    {
        var go = new GameObject("btn:" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        float left = full ? 0.04f : 0.04f + xMin * 0.92f;
        float right = full ? 0.96f : 0.04f + (xMin + 0.5f) * 0.92f - 0.01f;
        Anchor(rt, left, right, y, h);
        go.GetComponent<Image>().color = BTN;

        var textGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var trt = textGo.GetComponent<RectTransform>();
        trt.SetParent(rt, false);
        Stretch(trt, 0f);
        var t = textGo.GetComponent<Text>();
        t.font = _font; t.color = TEXT; t.text = label; t.fontSize = 12;
        t.alignment = TextAnchor.MiddleCenter;

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
