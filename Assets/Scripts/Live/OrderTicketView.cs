// OrderTicketView.cs — issue #23 re-home slice (DURABLE tier, Unity boundary)
//
// The uGUI manual Order ticket (findings 0014 RH4): BUY/SELL, qty, MARKET/LIMIT, limit price, and
// Place / Cancel-last buttons. It lives in the KIND_ORDER floating window (adopted by the workspace
// root, parity with the Strategy Editor window) and is shown ONLY while the footer mode is LiveManual.
//
// SEPARATION: this view owns the FORM widgets and raises PlaceRequested / CancelRequested with the
// current field values exposed as read-only props. It performs NO validation and issues NO RPC —
// BackcastWorkspaceRoot validates (qty/price) and marshals to _host.Lanes.SubmitPlaceOrder /
// SubmitCancelOrder, then SetStatus()es the outcome back here. This mirrors how the footer/menu Views
// stay logic-free and the root owns the host seam.

using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class OrderTicketView
{
    public event Action PlaceRequested;
    public event Action CancelRequested;

    Button _sideBtn, _typeBtn, _placeBtn, _cancelBtn;
    InputField _qty, _price;
    RectTransform _priceRow;
    Text _status, _instrument;
    bool _sideBuy = true;
    bool _limit;

    public bool SideBuy => _sideBuy;
    public bool Limit => _limit;
    public string Qty => _qty != null ? _qty.text : "";
    public string Price => _price != null ? _price.text : "";

    // Cyberpunk re-skin 2026-06-20 (raw literals, TODO: route via ThemeService).
    static readonly Color BtnColor = new Color(0.1020f, 0.1255f, 0.3137f, 1f);   // #1a2050 Neutral.Step4
    static readonly Color FieldColor = new Color(0.0510f, 0.0667f, 0.1882f, 1f); // #0d1130 Neutral.Step2 (input bg)
    static readonly Color TextColor = new Color(0.8784f, 0.9176f, 1.0000f, 1f);  // #e0eaff Neutral.Step12

    public void Build(RectTransform body, Font font)
    {
        if (body == null) return;

        _instrument = MakeLabel(body, font, 8f, 6f, 330f, 20f, "instrument: —");

        _sideBtn = MakeButton(body, font, 8f, 30f, 90f, 26f, "BUY", () =>
        {
            _sideBuy = !_sideBuy;
            SetButtonText(_sideBtn, _sideBuy ? "BUY" : "SELL");
        });
        _typeBtn = MakeButton(body, font, 104f, 30f, 100f, 26f, "MARKET", () =>
        {
            _limit = !_limit;
            SetButtonText(_typeBtn, _limit ? "LIMIT" : "MARKET");
            if (_priceRow != null) _priceRow.gameObject.SetActive(_limit);
        });

        MakeLabel(body, font, 8f, 62f, 60f, 22f, "qty");
        _qty = MakeField(body, font, 70f, 62f, 120f, 24f, "100");

        _priceRow = MakeRow(body, 8f, 90f, 200f, 24f);
        MakeLabel(_priceRow, font, 0f, 0f, 24f, 24f, "@");
        _price = MakeField(_priceRow, font, 28f, 0f, 120f, 24f, "");
        _priceRow.gameObject.SetActive(false);   // hidden until MARKET→LIMIT

        _placeBtn = MakeButton(body, font, 8f, 118f, 96f, 28f, "Place", () => PlaceRequested?.Invoke());
        _cancelBtn = MakeButton(body, font, 110f, 118f, 110f, 28f, "Cancel last", () => CancelRequested?.Invoke());

        _status = MakeLabel(body, font, 8f, 152f, 330f, 80f, "last order: -");
        _status.horizontalOverflow = HorizontalWrapMode.Wrap;
        _status.verticalOverflow = VerticalWrapMode.Truncate;
    }

    string _iidShown = "\0";
    public void SetInstrument(string iid)
    {
        iid ??= "";
        if (iid == _iidShown) return;   // skip the per-frame concat while the instrument is unchanged
        _iidShown = iid;
        if (_instrument != null) _instrument.text = "instrument: " + (iid.Length == 0 ? "— (select one)" : iid);
    }

    // Gate the action buttons (root passes canTrade = serverReady && connected && !teardown).
    public void SetInteractable(bool canTrade)
    {
        if (_placeBtn != null) _placeBtn.interactable = canTrade;
        if (_cancelBtn != null) _cancelBtn.interactable = canTrade;
    }

    public void SetStatus(string s)
    {
        if (_status != null) _status.text = "last order: " + (s ?? "-");
    }

    // ── tiny uGUI builders (top-left-anchored absolute placement; deterministic for a fixed window) ──
    static RectTransform MakeRow(RectTransform parent, float x, float yTop, float w, float h)
    {
        var go = new GameObject("row", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        Anchor(rt, x, yTop, w, h);
        return rt;
    }

    static void Anchor(RectTransform rt, float x, float yTop, float w, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f);
        rt.anchorMax = new Vector2(0f, 1f);
        rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop);
        rt.sizeDelta = new Vector2(w, h);
    }

    static Text MakeLabel(RectTransform parent, Font font, float x, float yTop, float w, float h, string text)
    {
        var go = new GameObject("label", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        Anchor(rt, x, yTop, w, h);
        var t = go.GetComponent<Text>();
        t.font = font; t.fontSize = 12; t.color = TextColor;
        t.alignment = TextAnchor.MiddleLeft; t.text = text; t.raycastTarget = false;
        return t;
    }

    static Button MakeButton(RectTransform parent, Font font, float x, float yTop, float w, float h, string text, Action onClick)
    {
        var go = new GameObject("btn_" + text, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        Anchor(rt, x, yTop, w, h);
        go.GetComponent<Image>().color = BtnColor;
        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(() => onClick());

        var labelGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform;
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        var lt = labelGo.GetComponent<Text>();
        lt.font = font; lt.fontSize = 12; lt.color = TextColor;
        lt.alignment = TextAnchor.MiddleCenter; lt.text = text; lt.raycastTarget = false;
        return btn;
    }

    static void SetButtonText(Button btn, string text)
    {
        if (btn == null) return;
        var t = btn.GetComponentInChildren<Text>();
        if (t != null) t.text = text;
    }

    static InputField MakeField(RectTransform parent, Font font, float x, float yTop, float w, float h, string initial)
    {
        var go = new GameObject("field", typeof(RectTransform), typeof(Image), typeof(InputField));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        Anchor(rt, x, yTop, w, h);
        go.GetComponent<Image>().color = FieldColor;

        var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        var trt = (RectTransform)textGo.transform;
        trt.SetParent(rt, false);
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(6f, 2f); trt.offsetMax = new Vector2(-6f, -2f);
        var text = textGo.GetComponent<Text>();
        text.font = font; text.fontSize = 12; text.color = TextColor;
        text.alignment = TextAnchor.MiddleLeft; text.supportRichText = false;

        var field = go.GetComponent<InputField>();
        field.textComponent = text;
        field.text = initial;
        field.lineType = InputField.LineType.SingleLine;
        return field;
    }
}
