// AccountSummaryBarView.cs — issues #174-178 "account summary bar" (ADR-0038 / findings 0126)
//
// A game-style resource strip anchored DIRECTLY BELOW the menu bar on its OWN ScreenSpaceOverlay
// Canvas (NOT a child of Content). Being screen-anchored is the whole point: a pan/zoom on the
// infinite canvas never moves it, it does not ride the 1.0×/1.2× parallax planes, and it reserves
// NO gutter — it overlaps the canvas (ADR-0038 §Decision 2, sister run-result popup #172 "excluded
// from the 3D space"). It persists NOTHING (a fixed anchor has no coordinates; visibility is derived)
// and is ALWAYS visible in every mode (Replay / LiveManual / LiveAuto — §Decision 5).
//
// 4 metric slots, each = an icon frame (RawImage; #177/S5 fills it from a RenderTexture, the swap
// seam for future real art) + a single primary value Text (#175/S2; slot ① recolours green/red by
// the unrealized-pnl sign) + a hover detail card (#176/S3; pointer-enter shows "今のパネルと同じ詳細
// 情報", pointer-exit hides). The OWNER (BackcastWorkspaceRoot) drives the primary text/colour and the
// hover detail string each frame from the live VM / Replay snapshot; this view is pure chrome and
// holds no domain logic. Until data arrives the owner sets the "—" placeholder (§Decision 5).
//
// Probe observability (E2E-CONVENTIONS §"probe observability"): the primary text+colour, the hover
// card text+visibility, and the icon texture are all readable WITHOUT real pointer events or a GPU,
// so AccountSummaryBarE2ERunner can drive SetHovered / read PrimaryText headlessly.

using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public sealed class AccountSummaryBarView : MonoBehaviour
{
    public const int SLOT_COUNT = 4;

    // The semantic intent of a primary value's colour — the OWNER passes intent, the BAR resolves it to a
    // theme colour. Keeping the mapping here means a Dark/Light flip (ApplyTheme) re-resolves every slot's
    // colour from its stored intent WITHOUT the owner re-pushing (the drive is gated on data change, so a
    // theme flip while idle must not leave the numbers in the old theme's colour — the ADR-0028 trap).
    public enum PrimaryTint { Neutral, Gain, Loss }

    // chrome z-order (findings 0045/0126): workspace content / sidebar / footer all live on the base
    // scene Canvas (sortingOrder 0); the menu bar + dropdowns are MENU_SORT(600) with a BACKDROP at 599.
    // The bar sits ABOVE the workspace + sidebar (so it reads on top of the canvas it overlaps) but
    // BELOW the menu dropdowns (which must still spill over it). 550 satisfies both relations.
    public const int BAR_SORT = MenuBarView.MENU_SORT - 50;

    const float BAR_H = 44f;       // strip height (below the 24px menu strip)
    const float ICON = 30f;        // icon frame square
    const float CARD_W = 300f;     // hover detail card width
    const float CARD_H = 96f;      // hover detail card height

    Font _font;
    Canvas _canvas;
    RectTransform _strip;
    bool _built;
    readonly Slot[] _slots = new Slot[SLOT_COUNT];

    sealed class Slot
    {
        public RawImage icon;
        public Image cardBg;
        public Text primary;
        public PrimaryTint tint;       // remembered so ApplyTheme can re-resolve the colour on a theme flip
        public RectTransform card;
        public Text cardText;
    }

    static Color ResolveTint(PrimaryTint tint)
    {
        var c = ThemeService.Current.colors;
        switch (tint)
        {
            case PrimaryTint.Gain: return c.hakoniwa_up;
            case PrimaryTint.Loss: return c.hakoniwa_down;
            default: return c.text;
        }
    }

    Image _stripBg;

    // ── probe observability ──
    public int SlotCount => SLOT_COUNT;
    public Canvas Canvas => _canvas;
    public RectTransform Strip => _strip;
    public string PrimaryText(int i) => Valid(i) ? _slots[i].primary.text : null;
    public Color PrimaryColor(int i) => Valid(i) ? _slots[i].primary.color : default;
    public bool CardVisible(int i) => Valid(i) && _slots[i].card != null && _slots[i].card.gameObject.activeSelf;
    public string CardText(int i) => Valid(i) && _slots[i].cardText != null ? _slots[i].cardText.text : null;
    public Texture IconTexture(int i) => Valid(i) && _slots[i].icon != null ? _slots[i].icon.texture : null;
    public RawImage IconImage(int i) => Valid(i) ? _slots[i].icon : null;

    bool Valid(int i) => _built && i >= 0 && i < SLOT_COUNT && _slots[i] != null;

    // Build the strip on its OWN ScreenSpaceOverlay canvas. `topOffset` = the menu bar height, so the
    // strip hangs flush UNDER the menu (derived, never hardcoded against the menu's authored height).
    public void Build(Font font, float topOffset)
    {
        if (_built) return;
        _built = true;
        _font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        var c = ThemeService.Current.colors;

        // own override-sorting ScreenSpaceOverlay canvas (screen-anchored — independent of Content).
        _canvas = gameObject.GetComponent<Canvas>();
        if (_canvas == null) _canvas = gameObject.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = BAR_SORT;
        if (gameObject.GetComponent<CanvasScaler>() == null) gameObject.AddComponent<CanvasScaler>();
        if (gameObject.GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

        // full-width top strip, BAR_H tall, pushed down by the menu height. anchorMin/Max top-stretch,
        // pivot top so anchoredPosition.y = -topOffset places the top edge at the menu's bottom edge.
        var stripGo = new GameObject("Strip", typeof(RectTransform), typeof(Image));
        _strip = (RectTransform)stripGo.transform;
        _strip.SetParent(transform, false);
        _strip.anchorMin = new Vector2(0f, 1f);
        _strip.anchorMax = new Vector2(1f, 1f);
        _strip.pivot = new Vector2(0.5f, 1f);
        _strip.sizeDelta = new Vector2(0f, BAR_H);
        _strip.anchoredPosition = new Vector2(0f, -topOffset);
        _stripBg = stripGo.GetComponent<Image>();
        _stripBg.color = c.panel_background;   // raycast target: the bar gutter eats clicks

        for (int i = 0; i < SLOT_COUNT; i++) _slots[i] = BuildSlot(i, c);
    }

    Slot BuildSlot(int index, ThemeColors c)
    {
        var slot = new Slot();

        // slot root spans 1/SLOT_COUNT of the width; raycast target so PointerEnter/Exit fire on it.
        var rootGo = new GameObject("slot" + index, typeof(RectTransform), typeof(Image), typeof(EventTrigger));
        var root = (RectTransform)rootGo.transform;
        root.SetParent(_strip, false);
        float w = 1f / SLOT_COUNT;
        root.anchorMin = new Vector2(index * w, 0f);
        root.anchorMax = new Vector2((index + 1) * w, 1f);
        root.offsetMin = new Vector2(4f, 0f); root.offsetMax = new Vector2(-4f, 0f);
        rootGo.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);   // invisible but raycastable

        // icon frame (RawImage; #177 fills .texture). Left-aligned square.
        var iconGo = new GameObject("icon", typeof(RectTransform), typeof(RawImage));
        var iconRt = (RectTransform)iconGo.transform;
        iconRt.SetParent(root, false);
        iconRt.anchorMin = new Vector2(0f, 0.5f); iconRt.anchorMax = new Vector2(0f, 0.5f);
        iconRt.pivot = new Vector2(0f, 0.5f);
        iconRt.sizeDelta = new Vector2(ICON, ICON);
        iconRt.anchoredPosition = new Vector2(6f, 0f);
        slot.icon = iconGo.GetComponent<RawImage>();
        slot.icon.color = c.element_background;   // visible placeholder frame until a texture is assigned
        slot.icon.raycastTarget = false;

        // primary value text, right of the icon, vertically centred. "—" until the owner drives a value.
        var pGo = new GameObject("primary", typeof(RectTransform), typeof(Text));
        var pRt = (RectTransform)pGo.transform;
        pRt.SetParent(root, false);
        pRt.anchorMin = new Vector2(0f, 0f); pRt.anchorMax = new Vector2(1f, 1f);
        pRt.offsetMin = new Vector2(ICON + 12f, 0f); pRt.offsetMax = new Vector2(-4f, 0f);
        slot.primary = pGo.GetComponent<Text>();
        slot.primary.font = _font; slot.primary.fontSize = 15; slot.primary.color = c.text;
        slot.primary.alignment = TextAnchor.MiddleLeft;
        slot.primary.horizontalOverflow = HorizontalWrapMode.Overflow;
        slot.primary.verticalOverflow = VerticalWrapMode.Overflow;
        slot.primary.raycastTarget = false;
        slot.primary.text = AccountSummaryFormat.PLACEHOLDER;

        // hover detail card: hangs DOWN from the slot's bottom-left, hidden until hovered.
        var cardGo = new GameObject("card", typeof(RectTransform), typeof(Image));
        slot.card = (RectTransform)cardGo.transform;
        slot.card.SetParent(root, false);
        slot.card.anchorMin = new Vector2(0f, 0f); slot.card.anchorMax = new Vector2(0f, 0f);
        slot.card.pivot = new Vector2(0f, 1f);
        slot.card.sizeDelta = new Vector2(CARD_W, CARD_H);
        slot.card.anchoredPosition = new Vector2(0f, -2f);
        slot.cardBg = cardGo.GetComponent<Image>();
        slot.cardBg.color = c.element_background;
        slot.cardBg.raycastTarget = false;

        var ctGo = new GameObject("cardText", typeof(RectTransform), typeof(Text));
        var ctRt = (RectTransform)ctGo.transform;
        ctRt.SetParent(slot.card, false);
        ctRt.anchorMin = Vector2.zero; ctRt.anchorMax = Vector2.one;
        ctRt.offsetMin = new Vector2(8f, 6f); ctRt.offsetMax = new Vector2(-8f, -6f);
        slot.cardText = ctGo.GetComponent<Text>();
        slot.cardText.font = _font; slot.cardText.fontSize = 12;
        slot.cardText.color = c.hakoniwa_text;
        slot.cardText.alignment = TextAnchor.UpperLeft;
        slot.cardText.horizontalOverflow = HorizontalWrapMode.Wrap;
        slot.cardText.verticalOverflow = VerticalWrapMode.Truncate;
        slot.cardText.raycastTarget = false;
        cardGo.SetActive(false);

        // pointer enter/exit → show/hide the card (the same path SetHovered drives for the probe).
        var trig = rootGo.GetComponent<EventTrigger>();
        AddTrigger(trig, EventTriggerType.PointerEnter, index, true);
        AddTrigger(trig, EventTriggerType.PointerExit, index, false);

        return slot;
    }

    void AddTrigger(EventTrigger trig, EventTriggerType type, int index, bool hovered)
    {
        var e = new EventTrigger.Entry { eventID = type };
        e.callback.AddListener(_ => SetHovered(index, hovered));
        trig.triggers.Add(e);
    }

    // ── owner drive API ──

    // Set the bar's single primary value + its colour INTENT (slot ① uses Gain/Loss by pnl sign; others
    // Neutral). The bar resolves intent→theme colour now AND again on a theme flip (ApplyTheme).
    public void SetPrimary(int i, string text, PrimaryTint tint)
    {
        if (!Valid(i)) return;
        if (_slots[i].primary.text != text) _slots[i].primary.text = text;
        _slots[i].tint = tint;
        Color color = ResolveTint(tint);
        if (_slots[i].primary.color != color) _slots[i].primary.color = color;
    }

    // Set the hover detail card content ("今のパネルと同じ詳細情報"). Does not change visibility.
    public void SetDetail(int i, string detail)
    {
        if (!Valid(i)) return;
        if (_slots[i].cardText.text != (detail ?? "")) _slots[i].cardText.text = detail ?? "";
    }

    // #177/S5 swap seam: point the slot's icon frame at a texture (RenderTexture now, real art later).
    public void SetIconTexture(int i, Texture tex)
    {
        if (!Valid(i)) return;
        _slots[i].icon.texture = tex;
        // once a texture is assigned, drop the placeholder tint so the icon reads as the texture.
        if (tex != null) _slots[i].icon.color = Color.white;
    }

    // Show/hide the hover card. Driven by the EventTrigger (real pointer) AND callable by the probe.
    public void SetHovered(int i, bool hovered)
    {
        if (!Valid(i)) return;
        if (_slots[i].card.gameObject.activeSelf != hovered)
            _slots[i].card.gameObject.SetActive(hovered);
    }

    // ADR-0028 (#174-178): re-theme the baked static chrome on a Dark/Light flip. The primary text +
    // colour are re-pushed every frame by the owner, but the strip / icon frame / card backgrounds + card
    // text are baked at Build, so the owner calls this on ThemeService.Changed (alongside the run_result tile).
    public void ApplyTheme()
    {
        if (!_built) return;
        var c = ThemeService.Current.colors;
        if (_stripBg != null) _stripBg.color = c.panel_background;
        foreach (var s in _slots)
        {
            if (s == null) continue;
            // re-resolve the primary value colour from its remembered intent (else a flip-while-idle leaves
            // the numbers in the old theme's colour — the drive is gated, so it won't re-push them).
            if (s.primary != null) s.primary.color = ResolveTint(s.tint);
            // an icon with a texture keeps Color.white (so the texture reads); only the placeholder tint re-themes.
            if (s.icon != null && s.icon.texture == null) s.icon.color = c.element_background;
            if (s.cardBg != null) s.cardBg.color = c.element_background;
            if (s.cardText != null) s.cardText.color = c.hakoniwa_text;
        }
    }
}
