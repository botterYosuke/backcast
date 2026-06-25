// SettingsDataSectionView.cs — #137 S4 (ADR-0026 / findings 0107 D1-D5): the Settings「Data」section.
//
// The DuckDB root editor, re-homed from `.env` into the Settings modal「実行」tab. A bordered text field
// (S1/D5 input roles) + a [...] folder-browse button (findings 0107 D4) + a red per-field error label
// (D-E). View layer only:
//   * persistence → JquantsDuckdbRootStore (app-global PlayerPrefs — D2),
//   * os.environ injection → the host's `onCommit` callback (Py.GIL SetItem — D1, the C#↔Python seam),
//   * validation → JquantsDuckdbRootValidator (folder exists + listed_info.duckdb present — D-E).
// This keeps the view headless-testable (no Python, no native dialog) — the AFK gate injects a stub browse
// func and reads the store back (DUCKROOT-01..03); the os.environ leg is gated separately with MOCK Python
// (DUCKROOT-04).
//
// The store is the SoT: the field commits to it on blur/submit AND when a folder is browsed, then the host
// injects the value into os.environ so the next Replay reads the REAL mount (D1/D4). Empty = "no override"
// (engine/paths.py falls back to the `.env` value — D3), validated as OK (the hard-error-on-unset lives at
// Replay time, ADR-0006).

using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class SettingsDataSectionView
{
    readonly Func<string, string> _browseFolder;   // (initialDir) → chosen folder or null (host wraps IFileDialog)
    readonly Action<string> _onCommit;             // host injects os.environ["BACKCAST_JQUANTS_DUCKDB_ROOT"] (D1)
    readonly Font _font;

    InputField _field;
    ThemedInputField _tf;          // shared bordered+placeholdered field widget (ThemedInputFieldBuilder)
    Text _err, _label;
    Image _browseBg;
    Text _browseText;

    const float LabelXMin = 0.04f, LabelXMax = 0.30f;
    const float FieldXMin = 0.32f, FieldXMax = 0.80f;
    const float BtnXMin = 0.83f, BtnXMax = 0.96f;
    const float RowH = 22f, ErrH = 13f;

    public SettingsDataSectionView(Func<string, string> browseFolder, Action<string> onCommit, Font font)
    {
        _browseFolder = browseFolder;
        _onCommit = onCommit;
        _font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    public void Build(RectTransform container)
    {
        float y = -2f;

        // muted left label.
        _label = MakeLabel(container, "DuckDB root", LabelXMin, LabelXMax, y, _font, ThemeService.Current.colors.text_muted);

        // bordered + placeholdered field (S1/D5) — the shared widget the Scenario card also uses.
        _tf = ThemedInputFieldBuilder.Build(container, _font, "/path/to/jquants (contains listed_info.duckdb)", "duckdb_field");
        Anchor(_tf.field.GetComponent<RectTransform>(), FieldXMin, FieldXMax, y, RowH);
        _field = _tf.field;
        _field.SetTextWithoutNotify(JquantsDuckdbRootStore.Load());   // restore the persisted value
        _field.onEndEdit.AddListener(Commit);

        // [...] folder-browse button.
        var btnGo = new GameObject("btn_browse", typeof(RectTransform), typeof(Image), typeof(Button));
        var brt = btnGo.GetComponent<RectTransform>();
        brt.SetParent(container, false);
        Anchor(brt, BtnXMin, BtnXMax, y, RowH);
        _browseBg = btnGo.GetComponent<Image>();
        _browseBg.color = ThemeService.Current.colors.element_background;
        btnGo.GetComponent<Button>().onClick.AddListener(OnBrowse);

        var btGo = new GameObject("text", typeof(RectTransform), typeof(Text));
        var btrt = btGo.GetComponent<RectTransform>(); btrt.SetParent(brt, false); Stretch(btrt, 0f);
        _browseText = btGo.GetComponent<Text>();
        _browseText.font = _font; _browseText.color = ThemeService.Current.colors.text; _browseText.text = "...";
        _browseText.fontSize = 13; _browseText.alignment = TextAnchor.MiddleCenter;

        // red per-field error (D-E), below the row.
        y -= RowH + 1f;
        var errGo = new GameObject("duckdb_err", typeof(RectTransform), typeof(Text));
        var ert = errGo.GetComponent<RectTransform>();
        ert.SetParent(container, false);
        Anchor(ert, FieldXMin, BtnXMax, y, ErrH);
        _err = errGo.GetComponent<Text>();
        _err.font = _font; _err.color = ThemeService.Current.status.error; _err.fontSize = 10;
        _err.alignment = TextAnchor.MiddleLeft; _err.enabled = false;

        RefreshError();
    }

    // Browse → set the field, persist, inject, re-validate. fail-soft: a null result (cancel / no native
    // dialog) leaves the typed field untouched (findings 0107 D4).
    void OnBrowse()
    {
        string initialDir = string.IsNullOrEmpty(_field.text) ? "" : _field.text;
        string picked = _browseFolder != null ? _browseFolder(initialDir) : null;
        if (string.IsNullOrEmpty(picked)) return;
        _field.SetTextWithoutNotify(picked);
        Commit(picked);
    }

    // Persist to the store, inject into os.environ (host), and refresh the red error. The single write
    // path for both onEndEdit and browse so the store/os.environ/UI never diverge.
    void Commit(string root)
    {
        JquantsDuckdbRootStore.Save(root);
        _onCommit?.Invoke(root);
        RefreshError();
    }

    void RefreshError()
    {
        string msg = JquantsDuckdbRootValidator.Validate(_field != null ? _field.text : "");
        if (_err == null) return;
        _err.text = msg ?? "";
        _err.enabled = !string.IsNullOrEmpty(msg);
    }

    public void ApplyTheme()
    {
        var c = ThemeService.Current.colors;
        _tf.ApplyTheme();                              // field fill / border / body / placeholder roles
        if (_browseText != null) _browseText.color = c.text;
        if (_browseBg != null) _browseBg.color = c.element_background;
        if (_label != null) _label.color = c.text_muted;
        if (_err != null) _err.color = ThemeService.Current.status.error;
    }

    static Text MakeLabel(RectTransform parent, string text, float xMin, float xMax, float yTop, Font font, Color color)
    {
        var go = new GameObject("label", typeof(RectTransform), typeof(Text));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        Anchor(rt, xMin, xMax, yTop, RowH);
        var t = go.GetComponent<Text>();
        t.font = font; t.color = color; t.text = text; t.fontSize = 11; t.alignment = TextAnchor.MiddleLeft;
        return t;
    }

    static void Anchor(RectTransform rt, float xMin, float xMax, float yTop, float h)
    {
        rt.anchorMin = new Vector2(xMin, 1f);
        rt.anchorMax = new Vector2(xMax, 1f);
        rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
        rt.anchoredPosition = new Vector2(0f, yTop);
        rt.sizeDelta = new Vector2(0f, h);
    }

    static void Stretch(RectTransform rt, float pad)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = new Vector2(pad, pad); rt.offsetMax = new Vector2(-pad, -pad);
    }
}
