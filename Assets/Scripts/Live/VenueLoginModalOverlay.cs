// VenueLoginModalOverlay.cs — #181 / ADR-0040 venue ログイン uGUI モーダルの入力面（DURABLE tier・Unity 境界）
//
// Settings ▸ Connect Tachibana(Demo)/kabu から開く中央オーバーレイ（SecretModalOverlay / ModifyModalOverlay と
// 同じ ScreenSpaceOverlay chrome 流派・z=1000）。旧 tkinter ダイアログ（別 OS ウィンドウ・macOS で crash）を置換する。
//
// 頭脳は VenueLoginModalController（plain C#・Python 非依存）。本 overlay は入力面のみで検証も RPC も持たない。
// root（BackcastWorkspaceRoot.DriveVenueLoginModal）が毎フレーム overlay ↔ controller を同期し、OK で
// WorkspaceEngineHost.SubmitVenueLogin（headless 認証）を呼ぶ。
//
// kabu API パスワードは SecretModalOverlay と同じ char[] 無バッファ方式: onTextInput で 1 文字ずつ CharTyped を
// 投げ（managed string を作らない）、masked dot だけ表示する。Tachibana の認証ID・秘密鍵パスは秘密ではないので
// legacy InputField（memory `backcast-legacy-inputfield-new-input-system`）。秘密鍵は「参照…」で .pem ピッカー。

using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.InputSystem;

public sealed class VenueLoginModalOverlay : MonoBehaviour
{
    public event Action<char> CharTyped;        // kabu パスワード（onTextInput）
    public event Action BackspacePressed;       // kabu パスワード backspace
    public event Action<string> ModeSelected;   // demo/prod/verify
    public event Action BrowseClicked;          // tachibana 秘密鍵 .pem ピッカー
    public event Action RecheckClicked;         // kabu 本体 再確認
    public event Action SubmitClicked;
    public event Action CancelClicked;

    Canvas _canvas;
    GameObject _panelRoot;
    Text _title, _status, _maskedPw, _portLabel;
    RectTransform _tachiGroup, _kabuGroup;
    InputField _authId, _keyPath;
    Button _modeA, _modeB, _okBtn, _recheckBtn;
    Keyboard _subscribedKb;     // kabu のときだけ hook する device
    bool _visible;
    bool _isKabu;

    static readonly Color BtnColor    = new Color(0.0941f, 0.1216f, 0.2275f, 1f); // #181f3a Neutral.Step4
    static readonly Color BtnSelColor = new Color(0.1804f, 0.2353f, 0.4392f, 1f); // 選択中ラジオ（明るめ）
    static readonly Color FieldColor  = new Color(0.0392f, 0.0549f, 0.1216f, 1f); // #0a0e1f Neutral.Step2
    static readonly Color TextColor   = new Color(0.8784f, 0.9059f, 0.9608f, 1f); // #e0e7f5 starlight
    static readonly Color ErrColor    = new Color(0.9216f, 0.3412f, 0.3412f, 1f); // 赤字エラー

    public bool IsVisible => _visible;
    public string AuthIdText => _authId != null ? _authId.text : "";
    public string KeyPathText => _keyPath != null ? _keyPath.text : "";
    public void SetKeyPathText(string s) { if (_keyPath != null) _keyPath.text = s ?? ""; }
    public void SetAuthIdText(string s) { if (_authId != null) _authId.text = s ?? ""; }

    public void Build(Font font)
    {
        // Unity の `??` は overloaded `==`（fake-null）を尊重しないので明示 == null（SecretModalOverlay と同 idiom）。
        _canvas = gameObject.GetComponent<Canvas>();
        if (_canvas == null) _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.sortingOrder = 1000;   // settings(900) の上・secret(1000) と同列の最前面 chrome
        if (gameObject.GetComponent<CanvasScaler>() == null) gameObject.AddComponent<CanvasScaler>();
        if (gameObject.GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

        var backdrop = new GameObject("Backdrop", typeof(RectTransform), typeof(Image));
        var brt = (RectTransform)backdrop.transform;
        brt.SetParent(transform, false);
        brt.anchorMin = Vector2.zero; brt.anchorMax = Vector2.one; brt.offsetMin = Vector2.zero; brt.offsetMax = Vector2.zero;
        backdrop.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0.55f);
        _panelRoot = backdrop;

        var panel = new GameObject("VenueLoginPanel", typeof(RectTransform), typeof(Image));
        var prt = (RectTransform)panel.transform;
        prt.SetParent(brt, false);
        prt.anchorMin = prt.anchorMax = new Vector2(0.5f, 0.5f); prt.pivot = new Vector2(0.5f, 0.5f);
        prt.sizeDelta = new Vector2(420f, 250f);
        panel.GetComponent<Image>().color = new Color(0.0667f, 0.0863f, 0.1686f, 1f); // #11162b Neutral.Step3

        _title = MakeLabel(prt, font, 12f, 10f, 396f, 22f, "venue ログイン", true);

        // Tachibana グループ: 認証ID / 秘密鍵パス + 参照… 。
        _tachiGroup = MakeRow(prt, 12f, 40f, 396f, 64f);
        MakeLabel(_tachiGroup, font, 0f, 0f, 90f, 24f, "認証 ID");
        _authId = MakeField(_tachiGroup, font, 96f, 0f, 300f, 24f);
        MakeLabel(_tachiGroup, font, 0f, 32f, 90f, 24f, "秘密鍵");
        _keyPath = MakeField(_tachiGroup, font, 96f, 32f, 232f, 24f);
        MakeButton(_tachiGroup, font, 334f, 32f, 62f, 24f, "参照…", () => BrowseClicked?.Invoke());

        // kabu グループ: API パスワード(masked) / 本体ポート / 再確認 。
        _kabuGroup = MakeRow(prt, 12f, 40f, 396f, 64f);
        MakeLabel(_kabuGroup, font, 0f, 0f, 110f, 24f, "API パスワード");
        _maskedPw = MakeLabel(_kabuGroup, font, 116f, 0f, 280f, 24f, "");
        MakeLabel(_kabuGroup, font, 0f, 32f, 110f, 24f, "本体ポート");
        _portLabel = MakeLabel(_kabuGroup, font, 116f, 32f, 120f, 24f, "");
        _recheckBtn = MakeButton(_kabuGroup, font, 250f, 32f, 100f, 24f, "再確認", () => RecheckClicked?.Invoke());

        // モード ラジオ（venue でラベルが変わる: demo/prod | verify/prod）。
        _modeA = MakeButton(prt, font, 12f, 116f, 110f, 26f, "Demo", () => ModeSelected?.Invoke(_isKabu ? "verify" : "demo"));
        _modeB = MakeButton(prt, font, 130f, 116f, 110f, 26f, "Prod", () => ModeSelected?.Invoke("prod"));

        _okBtn = MakeButton(prt, font, 12f, 156f, 130f, 30f, "OK", () => SubmitClicked?.Invoke());
        MakeButton(prt, font, 150f, 156f, 110f, 30f, "キャンセル", () => CancelClicked?.Invoke());

        _status = MakeLabel(prt, font, 12f, 196f, 396f, 44f, "", false);
        _status.color = ErrColor;
        _status.horizontalOverflow = HorizontalWrapMode.Wrap; _status.verticalOverflow = VerticalWrapMode.Truncate;

        SetVisible(false);
    }

    public void SetVisible(bool visible)
    {
        _visible = visible;
        if (_panelRoot != null) _panelRoot.SetActive(visible);
        if (_canvas != null) _canvas.enabled = visible;
        // kabu のときだけ onTextInput を drain（tachibana は InputField が自前で受ける）。
        if (visible && _isKabu) Subscribe(); else Unsubscribe();
    }

    // controller の状態を view に反映（毎フレーム・DriveVenueLoginModal から）。
    public void Reflect(VenueLoginModalController c)
    {
        if (c == null) return;
        bool kabu = c.IsKabu;
        if (kabu != _isKabu)
        {
            _isKabu = kabu;
            // venue が切り替わったら onTextInput の購読を kabu のときだけに張り替える。
            if (_visible && kabu) Subscribe(); else Unsubscribe();
        }
        if (_tachiGroup != null) _tachiGroup.gameObject.SetActive(!kabu);
        if (_kabuGroup != null) _kabuGroup.gameObject.SetActive(kabu);

        if (_title != null)
            _title.text = (kabu ? "kabuStation ログイン" : "Tachibana ログイン (公開鍵認証)") + "  [" + c.Mode + "]";

        if (kabu)
        {
            if (_maskedPw != null) _maskedPw.text = c.MaskedPassword;
            if (_portLabel != null) _portLabel.text = c.StationRunning ? (c.StationPort + " (起動中)") : (c.StationPort + " (未起動)");
        }

        // モード ラジオ: ラベル + 選択ハイライト。
        SetButtonText(_modeA, kabu ? "Verify" : "Demo");
        string modeAValue = kabu ? "verify" : "demo";
        SetButtonColor(_modeA, c.Mode == modeAValue ? BtnSelColor : BtnColor);
        SetButtonColor(_modeB, c.Mode == "prod" ? BtnSelColor : BtnColor);

        if (_recheckBtn != null) _recheckBtn.gameObject.SetActive(kabu);
        if (_status != null) _status.text = c.StatusText ?? "";
        if (_okBtn != null) _okBtn.interactable = c.CanSubmit();
    }

    void Update()
    {
        if (!_visible || !_isKabu) return;
        var kb = Keyboard.current;
        if (kb != null && kb.backspaceKey.wasPressedThisFrame) BackspacePressed?.Invoke();
    }

    void Subscribe()
    {
        if (_subscribedKb != null) return;
        var kb = Keyboard.current;
        if (kb == null) return;
        kb.onTextInput += OnTextInput;
        _subscribedKb = kb;
    }

    void Unsubscribe()
    {
        if (_subscribedKb == null) return;
        _subscribedKb.onTextInput -= OnTextInput;
        _subscribedKb = null;
    }

    void OnTextInput(char c)
    {
        if (c < ' ' || c == (char)0x7F) return;   // 制御文字は無視（backspace は key poll）
        CharTyped?.Invoke(c);
    }

    void OnDisable() => Unsubscribe();
    void OnDestroy() => Unsubscribe();

    // ── tiny uGUI builders（top-left-anchored・ModifyModalOverlay と同型）──
    static RectTransform MakeRow(RectTransform parent, float x, float yTop, float w, float h)
    {
        var go = new GameObject("row", typeof(RectTransform));
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false); Anchor(rt, x, yTop, w, h); return rt;
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

    static void SetButtonColor(Button btn, Color c)
    {
        if (btn == null) return;
        var img = btn.GetComponent<Image>();
        if (img != null) img.color = c;
    }

    static InputField MakeField(RectTransform parent, Font font, float x, float yTop, float w, float h)
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
        field.textComponent = text; field.text = ""; field.lineType = InputField.LineType.SingleLine;
        return field;
    }
}
