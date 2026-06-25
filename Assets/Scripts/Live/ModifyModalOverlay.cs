// ModifyModalOverlay.cs — issue #34 注文訂正 UI（modify modal）の uGUI 入力面（DURABLE tier・Unity 境界）
//
// resting 行 [訂正] から開く中央オーバーレイ（SecretModalOverlay と同じ ScreenSpaceOverlay chrome 流派）。
// 数量/価格は legacy InputField（secret ではないので OrderTicketView と同じく InputField で良い・memory
// `backcast-legacy-inputfield-new-input-system`）。空欄=変更なし（findings 0101 D2）。venue が cancel+replace
// （kabu・findings 0101 D5）のとき上部に警告バナー＋「理解した上で訂正する」ack トグルを出し、root が
// ModifyModalController.CanConfirm() に従って Confirm を enable/disable する（C# は venue 名分岐しない）。
//
// 頭脳は ModifyModalController（plain C#・Python 非依存）。本 overlay は入力面のみで、検証も RPC も持たない。
// root（BackcastWorkspaceRoot.DriveModifyModal）が毎フレーム overlay の入力を controller に同期し、
// CanConfirm を Confirm ボタンへ反映、Confirm/Cancel を受けて submit/close する。

using System;
using UnityEngine;
using UnityEngine.UI;

public sealed class ModifyModalOverlay : MonoBehaviour
{
    public event Action ConfirmClicked;
    public event Action CancelClicked;

    Canvas _canvas;
    GameObject _panelRoot;
    Text _title, _status;
    RectTransform _warnRow;
    Button _ackBtn, _confirmBtn;
    InputField _qty, _price;
    bool _visible;
    bool _ackChecked;

    static readonly Color BtnColor   = new Color(0.0941f, 0.1216f, 0.2275f, 1f); // #181f3a Neutral.Step4
    static readonly Color FieldColor = new Color(0.0392f, 0.0549f, 0.1216f, 1f); // #0a0e1f Neutral.Step2
    static readonly Color TextColor  = new Color(0.8784f, 0.9059f, 0.9608f, 1f); // #e0e7f5 starlight
    static readonly Color WarnColor  = new Color(0.9569f, 0.6118f, 0.0706f, 1f); // amber 警告

    public bool IsVisible => _visible;
    public string NewQtyText => _qty != null ? _qty.text : "";
    public string NewPriceText => _price != null ? _price.text : "";
    public bool AckChecked => _ackChecked;

    public void Build(Font font)
    {
        // Unity の `??` は overloaded `==`（fake-null）を尊重しないので明示 == null で判定する
        // （SecretModalOverlay と同じ idiom）。
        _canvas = gameObject.GetComponent<Canvas>();
        if (_canvas == null) _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 1001;   // 訂正 modal は secret(1000) と同列の chrome・1 つ上
        if (gameObject.GetComponent<CanvasScaler>() == null) gameObject.AddComponent<CanvasScaler>();
        if (gameObject.GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

        var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        var brt = (RectTransform)backdrop.transform;
        brt.SetParent(transform, false);
        brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
        backdrop.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
        _panelRoot = backdrop;

        var panel = new GameObject("ModifyPanel", typeof(RectTransform), typeof(Image));
        var prt = (RectTransform)panel.transform;
        prt.SetParent(brt, false);
        prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f); prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(400f, 250f);
        panel.GetComponent<Image>().color = new Color(0.0667f, 0.0863f, 0.1686f, 1f); // #11162b Neutral.Step3

        _title = MakeLabel(prt, font, 12f, 10f, 376f, 22f, "訂正: —", true);

        // 警告バナー行（cancel+replace venue のときだけ可視）。
        _warnRow = MakeRow(prt, 12f, 36f, 376f, 56f);
        var warn = MakeLabel(_warnRow, font, 0f, 0f, 350f, 34f,
            "⚠ この venue は取消→新規発注の2段階で訂正します。途中失敗で原注文のみ取消になる恐れがあります。");
        warn.color = WarnColor; warn.horizontalOverflow = HorizontalWrapMode.Wrap; warn.verticalOverflow = VerticalWrapMode.Truncate;
        _ackBtn = MakeButton(_warnRow, font, 0f, 36f, 230f, 22f, "[ ] 理解した上で訂正する", () =>
        {
            _ackChecked = !_ackChecked;
            SetButtonText(_ackBtn, (_ackChecked ? "[x]" : "[ ]") + " 理解した上で訂正する");
        });

        MakeLabel(prt, font, 12f, 100f, 90f, 24f, "new qty");
        _qty = MakeField(prt, font, 110f, 100f, 120f, 24f, "");
        MakeLabel(prt, font, 12f, 130f, 90f, 24f, "new price");
        _price = MakeField(prt, font, 110f, 130f, 120f, 24f, "");

        _confirmBtn = MakeButton(prt, font, 12f, 168f, 130f, 30f, "確認して訂正", () => ConfirmClicked?.Invoke());
        MakeButton(prt, font, 150f, 168f, 100f, 30f, "キャンセル", () => CancelClicked?.Invoke());

        _status = MakeLabel(prt, font, 12f, 206f, 376f, 36f, "", false);
        _status.horizontalOverflow = HorizontalWrapMode.Wrap; _status.verticalOverflow = VerticalWrapMode.Truncate;

        SetVisible(false);
    }

    // resting 行から開くときの設定。原数量/原価格は title に出す（空欄=変更なし・findings 0101 D2）。
    public void Configure(string orderId, string sideSym, double originalQty, double? originalPrice,
                          double filledQty, bool requiresCancelReplaceAck)
    {
        string priceTxt = originalPrice.HasValue ? ("@" + originalPrice.Value) : "成行";
        string filledTxt = filledQty > 0 ? ("・約定済 " + filledQty) : "";
        if (_title != null)
            _title.text = "訂正: " + sideSym + " " + orderId + "  現 " + originalQty + " " + priceTxt + filledTxt
                        + "  （空欄=変更なし）";
        _ackChecked = false;
        if (_ackBtn != null) SetButtonText(_ackBtn, "[ ] 理解した上で訂正する");
        if (_warnRow != null) _warnRow.gameObject.SetActive(requiresCancelReplaceAck);
        if (_qty != null) _qty.text = "";
        if (_price != null) _price.text = "";
        if (_status != null) _status.text = "";
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_panelRoot != null) _panelRoot.SetActive(visible);
        if (_canvas != null) _canvas.enabled = visible;
    }

    public void SetConfirmInteractable(bool can) { if (_confirmBtn != null) _confirmBtn.interactable = can; }
    public void SetStatus(string s) { if (_status != null) _status.text = s ?? ""; }

    // ── tiny uGUI builders (top-left-anchored absolute placement) ──
    static RectTransform MakeRow(RectTransform parent, float x, float yTop, float w, float h)
    {
        var go = new GameObject("row", typeof(RectTransform));
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
        Anchor(rt, x, yTop, w, h); return rt;
    }

    static void Anchor(RectTransform rt, float x, float yTop, float w, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.anchoredPosition = new Vector2(x, -yTop); rt.sizeDelta = new Vector2(w, h);
    }

    static Text MakeLabel(RectTransform parent, Font font, float x, float yTop, float w, float h, string text, bool rich = false)
    {
        var go = new GameObject("label", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false); Anchor(rt, x, yTop, w, h);
        var t = go.GetComponent<Text>();
        t.font = font; t.fontSize = 12; t.color = TextColor;
        t.alignment = TextAnchor.MiddleLeft; t.text = text; t.raycastTarget = false; t.supportRichText = rich;
        return t;
    }

    static Button MakeButton(RectTransform parent, Font font, float x, float yTop, float w, float h, string text, Action onClick)
    {
        var go = new GameObject("btn_" + text, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false); Anchor(rt, x, yTop, w, h);
        go.GetComponent<Image>().color = BtnColor;
        go.GetComponent<Button>().onClick.AddListener(() => onClick());
        var labelGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        var lrt = (RectTransform)labelGo.transform; lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        var lt = labelGo.GetComponent<Text>();
        lt.font = font; lt.fontSize = 12; lt.color = TextColor;
        lt.alignment = TextAnchor.MiddleCenter; lt.text = text; lt.raycastTarget = false;
        return go.GetComponent<Button>();
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
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false); Anchor(rt, x, yTop, w, h);
        go.GetComponent<Image>().color = FieldColor;
        var textGo = new GameObject("Text", typeof(RectTransform), typeof(Text));
        var trt = (RectTransform)textGo.transform; trt.SetParent(rt, false);
        trt.anchorMin = Vector2.zero; trt.anchorMax = Vector2.one;
        trt.offsetMin = new Vector2(6f, 2f); trt.offsetMax = new Vector2(-6f, -2f);
        var text = textGo.GetComponent<Text>();
        text.font = font; text.fontSize = 12; text.color = TextColor;
        text.alignment = TextAnchor.MiddleLeft; text.supportRichText = false;
        var field = go.GetComponent<InputField>();
        field.textComponent = text; field.text = initial; field.lineType = InputField.LineType.SingleLine;
        return field;
    }
}
