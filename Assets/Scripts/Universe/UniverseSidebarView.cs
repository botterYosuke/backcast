// UniverseSidebarView.cs — issue #77 "menu dropdown z-order" (uGUI cutover, findings 0045)
//                         + issue #84 "footer が side bar に隠れてしまう" (sidebar overflow clip, findings 0053)
//
// The scene-authored production HOST for the instrument-picker / universe sidebar. It REUSES the durable
// UniverseSidebarController brain and renders the screen-fixed left sidebar as uGUI on its OWN nested,
// override-sorting Canvas (sortingOrder SIDEBAR_SORT), clipped to its scene-authored container
// RectTransform (draw region DERIVED, never hardcoded).
//
// WHY uGUI (was OnGUI): #77 — the sidebar and the menu were BOTH IMGUI, and a single-camera Screen-Space
// setup ignores GUI.depth, so the (later) sidebar OnGUI overpainted the menu dropdown. uGUI makes z-order
// deterministic by sortingOrder: this sidebar sits BELOW the menu (MenuBarView.MENU_SORT) and below the
// footer (WorkspaceFooterView.FOOTER_SORT, #84) so dropdowns + the status bar draw in front, and the
// EventSystem routes clicks to the top raycaster (no input bleed). See findings 0045 / 0053.
//
// WHY two ScrollRects (#84): rows + picker list are dynamic and can grow well past the sidebar's height
// (v19_morning_cell.json has a 150-instrument universe → 3300px of rows in a ~923px sidebar). Without
// clipping, the overflow drew on the sidebar Canvas (sortingOrder=500) and overpainted the footer
// (sortingOrder=0 before #84). The fix is structural: a RectMask2D + ScrollRect around BOTH rows AND
// picker list so overflow can NEVER escape the sidebar's RectTransform. Pinned elements (Title top,
// +Add between rows and picker, focus label bottom) sit on the un-masked _content so a click always
// reaches them regardless of how many rows / candidates exist. The footer is also given its own
// override-sorting Canvas above the sidebar — two independent guarantees so #84 cannot regress
// from either side of the contract.
//
// Python-FREE: the controller drives SelectedSymbol (the depth-target focus) and the universe writeback;
// the candidate source is injected by the root (a separate issue owns the real DuckDB/venue universe).
//
// RETAINED-MODE REBUILD: row + candidate children are rebuilt on the controller's observable changes
// (InstrumentRegistry.Changed / SelectedSymbol.Changed / picker toggle), NOT every frame. Scroll positions
// are CAPTURED before ClearChildren and RESTORED after — both the full Rebuild() AND the per-keystroke
// RebuildPickerList() preserve the rows scroll (typing in the picker must not jump the rows view). The
// search field is built once while the picker is open so per-keystroke list rebuilds never steal focus.

using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RectTransform))]
public sealed class UniverseSidebarView : MonoBehaviour
{
    // below MenuBarView.MENU_SORT and WorkspaceFooterView.FOOTER_SORT so dropdowns + the status bar draw
    // in front, above the field/windows (main Canvas, 0) so the sidebar chrome stays visible over the
    // workspace (findings 0045 / 0053).
    public const int SIDEBAR_SORT = 500;

    const float PAD = 6f, ROW_H = 22f, TITLE_H = 22f, GAP = 2f, REMOVE_W = 24f;

    RectTransform _container;
    UniverseSidebarController _ctrl;
    IStrategyFileProvider _strategyProvider;
    UniverseSourceMode _mode = UniverseSourceMode.Replay;
    string _replayEnd = "2024-12-31";
    Font _font;
    bool _built;
    // F2 (#84 review): Bind→Rebuild runs in BackcastWorkspaceRoot.Awake, where _content.rect.height is still
    // 0 (Canvas hasn't laid out yet). Relayout early-returns on a zero rect, so the first frame would render
    // an empty/collapsed sidebar with no instruments visible. Update re-Relayouts ONCE the rect resolves.
    bool _laidOutOnce;

    // stable widgets (built once; children rebuilt on change). Inside _content, top-to-bottom:
    //   Title (pinned top, _content child) → RowsScroll [ScrollRect, viewport+RectMask2D, content] →
    //   _addBtn (pinned, _content child) → _pickerRoot (visibility-toggled) {search label + InputField
    //   + PickerListScroll [ScrollRect, viewport+RectMask2D, content]} → _focusLabel (pinned bottom).
    RectTransform _content;
    RectTransform _rowsScrollContainer, _rowsContent;
    ScrollRect _rowsScroll;
    Button _addBtn;
    Text _addLabel, _focusLabel;
    RectTransform _pickerRoot;
    // C5 (#84 review): the picker scroll scaffolding is persistent (built once in Build, hidden via
    // SetActive when the picker is closed) — mirrors how the rows scroll is treated and removes the
    // GC churn / asymmetry of tearing it down on every picker toggle.
    RectTransform _pickerListScrollContainer, _pickerListContent;
    ScrollRect _pickerListScroll;
    InputField _searchInput;

    public void Bind(UniverseSidebarController ctrl, IStrategyFileProvider strategyProvider, Font font, string replayEnd = null)
    {
        _ctrl = ctrl;
        _strategyProvider = strategyProvider;
        if (!string.IsNullOrEmpty(replayEnd)) _replayEnd = replayEnd;
        _container = GetComponent<RectTransform>();
        _font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        Build();
        _ctrl.Registry.Changed += Rebuild;
        _ctrl.Selected.Changed += OnSelectedChanged;
        Rebuild();
    }

    void Build()
    {
        if (_built) return;
        _built = true;

        // C1 (#84 review): shared chrome promotion (findings 0053; see ChromeCanvas).
        ChromeCanvas.Promote(gameObject, SIDEBAR_SORT);

        var t = ThemeService.Current;
        var bgGo = new GameObject("SidebarBg", typeof(RectTransform), typeof(Image));
        var bgRt = (RectTransform)bgGo.transform; bgRt.SetParent(_container, false); Stretch(bgRt);
        var bgImage = bgGo.GetComponent<Image>();
        var bgColor = t.colors.panel_background; bgColor.a = 0f; // sidebar 背景は透明（canvas を透かす）
        bgImage.color = bgColor;
        bgImage.raycastTarget = false; // 透明背景はクリックを遮らず後ろの canvas へ透過させる

        _content = NewRect("Content", _container);
        Stretch(_content);
        _content.offsetMin = new Vector2(PAD, PAD); _content.offsetMax = new Vector2(-PAD, -PAD);

        MakeText(_content, "Instruments", 13, t.status.info, TextAnchor.UpperLeft, 0f, TITLE_H);

        BuildVerticalScroll("RowsScroll", _content, out _rowsScrollContainer, out _rowsContent, out _rowsScroll);

        _addBtn = MakeButton(_content, "+ Add", () => { _ctrl.TogglePicker(_mode, _replayEnd); Rebuild(); }, out _addLabel);

        // PickerRoot: a top-anchored host for the picker UI. Persistent; sizeDelta=0 + SetActive(false)
        // when the picker is closed. Inside, the search label / InputField / PickerListScroll are built
        // ONCE in Build — open/close just toggles the host's visibility (C5 #84 review). RebuildPicker
        // refreshes the search-field text from the controller on open and the candidate list on demand.
        _pickerRoot = NewRect("Picker", _content); TopStrip(_pickerRoot, 0f, 0f);
        MakeText(_pickerRoot, "search:", 11, t.colors.text_muted, TextAnchor.UpperLeft, 0f, ROW_H);
        _searchInput = MakeInputField(_pickerRoot, "", -ROW_H);
        _searchInput.onValueChanged.AddListener(q =>
        {
            if (_ctrl == null) return;
            _ctrl.Picker.SetQuery(q);
            RebuildPickerList();
        });
        BuildVerticalScroll("PickerListScroll", _pickerRoot, out _pickerListScrollContainer, out _pickerListContent, out _pickerListScroll);
        _pickerRoot.gameObject.SetActive(false);

        // focus → depth label, pinned to the bottom of _content (un-masked, always visible).
        var fGo = new GameObject("focus", typeof(RectTransform), typeof(Text));
        var fRt = (RectTransform)fGo.transform; fRt.SetParent(_content, false);
        fRt.anchorMin = new Vector2(0f, 0f); fRt.anchorMax = new Vector2(1f, 0f); fRt.pivot = new Vector2(0f, 0f);
        fRt.sizeDelta = new Vector2(0f, ROW_H); fRt.anchoredPosition = Vector2.zero;
        _focusLabel = fGo.GetComponent<Text>();
        _focusLabel.font = _font; _focusLabel.fontSize = 11; _focusLabel.color = t.colors.text_muted;
        _focusLabel.alignment = TextAnchor.LowerLeft; _focusLabel.supportRichText = false; _focusLabel.raycastTarget = false;
    }

    // C3 (#84 review): shared vertical-ScrollRect builder. A vertical-only Clamped ScrollRect with a
    // transparent RectMask2D viewport (catches the wheel, masks all descendants) and a top-anchored
    // Content holder. Used by both rows and picker list — and by any future scrollable sidebar pane.
    void BuildVerticalScroll(string name, RectTransform parent, out RectTransform container, out RectTransform content, out ScrollRect scroll)
    {
        container = NewRect(name, parent);
        container.anchorMin = new Vector2(0f, 1f); container.anchorMax = new Vector2(1f, 1f);
        container.pivot = new Vector2(0f, 1f);
        scroll = container.gameObject.AddComponent<ScrollRect>();
        scroll.horizontal = false; scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 24f;

        var vpGo = new GameObject("Viewport", typeof(RectTransform), typeof(RectMask2D), typeof(Image));
        var vp = (RectTransform)vpGo.transform; vp.SetParent(container, false);
        Stretch(vp);
        var vpImg = vpGo.GetComponent<Image>();
        vpImg.color = new Color(0f, 0f, 0f, 0f);   // transparent — visual stays the parent bg
        vpImg.raycastTarget = true;                 // catches the wheel for ScrollRect
        scroll.viewport = vp;

        var contentGo = new GameObject("Content", typeof(RectTransform));
        content = (RectTransform)contentGo.transform; content.SetParent(vp, false);
        content.anchorMin = new Vector2(0f, 1f); content.anchorMax = new Vector2(1f, 1f);
        content.pivot = new Vector2(0f, 1f);
        content.anchoredPosition = Vector2.zero;
        content.sizeDelta = Vector2.zero;
        scroll.content = content;
    }

    // Rebuild the dynamic content (rows + picker list) and re-flow the stable widgets in a SINGLE
    // Relayout pass (C6 #84 review). Scroll positions are captured before ClearChildren and restored
    // after Relayout, so a sidebar edit (× / +Add / focus change) doesn't snap the user back to the top.
    void Rebuild()
    {
        if (!_built) return;
        var t = ThemeService.Current;
        float prevRowsScroll = CaptureRowsScroll();

        ClearChildren(_rowsContent);
        int n = 0;
        foreach (SidebarRow r in _ctrl.Rows())
        {
            BuildRow(r, n);
            n++;
        }
        if (_ctrl.Registry.Count == 0)
            MakeText(_rowsContent, "No instruments", 11, t.colors.text_muted, TextAnchor.MiddleLeft, 0f, ROW_H);
        float rowsContentH = (n == 0 ? 1 : n) * ROW_H;   // 1 row reserved for the "No instruments" label
        _rowsContent.sizeDelta = new Vector2(0f, rowsContentH);

        bool pickerOpen = _ctrl.Picker.Visible;
        _pickerRoot.gameObject.SetActive(pickerOpen);
        if (_addLabel != null) _addLabel.text = pickerOpen ? "− Close" : "+ Add";

        if (pickerOpen)
        {
            // refresh the search field to the controller's current query. Use SetTextWithoutNotify so the
            // assignment doesn't fire onValueChanged → SetQuery → RebuildPickerList → Relayout (which
            // would double-run inside this Rebuild and defeat the C6 single-Relayout invariant). The
            // user-input path is unaffected — typing still fires onValueChanged.
            string q = _ctrl.Picker.Query ?? "";
            if (_searchInput != null && _searchInput.text != q) _searchInput.SetTextWithoutNotify(q);
            PopulatePickerListContent();
        }

        Relayout();
        RestoreRowsScroll(prevRowsScroll);

        if (_focusLabel != null)
            _focusLabel.text = "focus → depth: " + (_ctrl.Selected.HasValue ? _ctrl.Selected.Value : "(none)");
    }

    void BuildRow(SidebarRow r, int index)
    {
        var t = ThemeService.Current;
        string id = r.Id;
        var rowGo = new GameObject("row:" + id, typeof(RectTransform));
        var rowRt = (RectTransform)rowGo.transform; rowRt.SetParent(_rowsContent, false);
        rowRt.anchorMin = new Vector2(0f, 1f); rowRt.anchorMax = new Vector2(1f, 1f); rowRt.pivot = new Vector2(0f, 1f);
        rowRt.sizeDelta = new Vector2(0f, ROW_H - 1f);
        rowRt.anchoredPosition = new Vector2(0f, -index * ROW_H);

        // select button fills the row except the remove column.
        var selGo = new GameObject("sel", typeof(RectTransform), typeof(Image), typeof(Button));
        var selRt = (RectTransform)selGo.transform; selRt.SetParent(rowRt, false);
        selRt.anchorMin = new Vector2(0f, 0f); selRt.anchorMax = new Vector2(1f, 1f);
        selRt.offsetMin = Vector2.zero; selRt.offsetMax = new Vector2(-REMOVE_W, 0f);
        selGo.GetComponent<Image>().color = r.Selected ? t.colors.element_selected : t.colors.element_background;
        selGo.GetComponent<Button>().onClick.AddListener(() => { _ctrl.SelectRow(id, _mode); });
        AddLabel(selRt, (r.Selected ? "▶ " : "   ") + id, TextAnchor.MiddleLeft, t.colors.text_accent);

        var rmGo = new GameObject("rm", typeof(RectTransform), typeof(Image), typeof(Button));
        var rmRt = (RectTransform)rmGo.transform; rmRt.SetParent(rowRt, false);
        rmRt.anchorMin = new Vector2(1f, 0f); rmRt.anchorMax = new Vector2(1f, 1f); rmRt.pivot = new Vector2(1f, 0.5f);
        rmRt.sizeDelta = new Vector2(REMOVE_W, 0f); rmRt.anchoredPosition = Vector2.zero;
        rmGo.GetComponent<Image>().color = t.colors.element_background;
        rmGo.GetComponent<Button>().onClick.AddListener(() => { _ctrl.Remove(id, _mode, _strategyProvider); });
        AddLabel(rmRt, "×", TextAnchor.MiddleCenter, t.colors.text_accent);
    }

    // F3 (#84 review): per-keystroke handler from the picker search field. Repopulates the picker list
    // content, then re-flows (the natural list size changed → Relayout re-caps the picker viewport).
    // Captures+restores the ROWS scroll across Relayout so typing in the search box never jumps the
    // rows view (the only contract that matters for typing UX).
    void RebuildPickerList()
    {
        if (!_built || _pickerListContent == null) return;
        float prevRowsScroll = CaptureRowsScroll();
        PopulatePickerListContent();
        Relayout();
        RestoreRowsScroll(prevRowsScroll);
    }

    void PopulatePickerListContent()
    {
        ClearChildren(_pickerListContent);
        var t = ThemeService.Current;
        int i = 0;
        foreach (PickerRow pr in _ctrl.PickerList(_mode))
        {
            if (pr.IsPlaceholder)
            {
                MakeText(_pickerListContent, "  " + pr.Label, 11, t.colors.text_muted, TextAnchor.MiddleLeft, -i * ROW_H, ROW_H);
                i++;
                continue;
            }
            string id = pr.Id;
            string lbl = (pr.AlreadyAdded ? "✓ " : "+ ") + pr.Label;
            var go = new GameObject("cand:" + id, typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform; rt.SetParent(_pickerListContent, false);
            rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0f, 1f);
            rt.sizeDelta = new Vector2(0f, ROW_H - 1f); rt.anchoredPosition = new Vector2(0f, -i * ROW_H);
            go.GetComponent<Image>().color = t.colors.element_background;
            go.GetComponent<Button>().onClick.AddListener(() =>
            {
                long nowMs = (long)(Time.realtimeSinceStartup * 1000f);
                _ctrl.AddFromPicker(id, _mode, _strategyProvider, nowMs);
            });
            AddLabel(rt, lbl, TextAnchor.MiddleLeft, t.colors.text_accent);
            i++;
        }
        _pickerListContent.sizeDelta = new Vector2(0f, i * ROW_H);
    }

    // F4 (#84 review): NaN guard. ScrollRect.verticalNormalizedPosition returns NaN when content fits
    // entirely in the viewport (denominator is 0); Mathf.Clamp01(NaN) returns 0 (snap-to-bottom), which
    // would defeat the "don't snap" contract for short universes when the user later adds enough rows
    // to overflow. Treat NaN as "no captured state" (return NaN → RestoreRowsScroll skips).
    float CaptureRowsScroll()
    {
        if (_rowsScroll == null || _rowsScroll.content == null || _rowsScroll.viewport == null) return float.NaN;
        float v = _rowsScroll.verticalNormalizedPosition;
        return float.IsNaN(v) ? float.NaN : v;
    }

    void RestoreRowsScroll(float prev)
    {
        if (_rowsScroll == null || float.IsNaN(prev)) return;
        _rowsScroll.verticalNormalizedPosition = Mathf.Clamp01(prev);
    }

    // F1 fix (#84 review): re-flow the rows scroll + add button + picker under the container's available
    // height. Picker block (when open) = label (ROW_H) + InputField (ROW_H) + picker list (pickerListH).
    // The previous formula collapsed label+input into a single ROW_H slot, leaving the InputField
    // occluded by the picker list ScrollRect viewport — typing into the search box became impossible.
    //
    // Layout (top→bottom): Title (TITLE_H) | gap | RowsScroll [variable] | gap | +Add (ROW_H) | gap |
    // [picker { search label ROW_H + InputField ROW_H + PickerListScroll [variable] }] | FocusLabel (pinned).
    //
    // 余高 = container.h − (TITLE_H + ROW_H_add + ROW_H_focus + 4*GAP); when picker is closed, RowsScroll
    // takes all of 余高. When open, the picker header reserves 2*ROW_H (label + input + GAP between picker
    // header and list) and the candidate list takes min(natural, remaining/2) — so the picker can't
    // starve the rows even with hundreds of candidates.
    const float PICKER_HEADER_H = 2f * ROW_H;   // search label + InputField
    void Relayout()
    {
        if (_content == null || _rowsScrollContainer == null || _addBtn == null) return;
        float containerH = _content.rect.height;
        if (containerH <= 0f) return;   // F2: pre-layout frame; Update re-Relayouts once rect resolves.

        float pinned = TITLE_H + ROW_H + ROW_H + 4f * GAP;   // title + add + focus + 4 separator gaps
        float available = Mathf.Max(0f, containerH - pinned);

        float pickerListH = 0f;
        bool pickerOpen = _ctrl != null && _ctrl.Picker.Visible && _pickerListScrollContainer != null;
        if (pickerOpen)
        {
            float roomAfterHeader = Mathf.Max(0f, available - PICKER_HEADER_H - GAP);
            float naturalListH = _pickerListContent != null ? _pickerListContent.sizeDelta.y : 0f;
            pickerListH = Mathf.Min(naturalListH, roomAfterHeader * 0.5f);
            available -= PICKER_HEADER_H + GAP + pickerListH + GAP;
        }
        // Cap the rows viewport to the natural list height (bounded by 余高). The viewport Image is a
        // raycastTarget (catches the wheel for scroll), so a full-余高 viewport would blanket the visually
        // EMPTY band below the last instrument and steal canvas pan-drags there. Sizing it to the content
        // leaves that empty band raycast-free → pan falls through to the InfiniteCanvasInputSurface, while
        // the list band still captures wheel/scroll/selection ("list priority"). Overflow (content > 余高)
        // still gets the full 余高 → scroll works unchanged.
        float naturalRowsH = _rowsContent != null ? _rowsContent.sizeDelta.y : available;
        float rowsViewportH = Mathf.Clamp(naturalRowsH, 0f, available);

        // Title sits at y=0 with height TITLE_H (MakeText). Start below it.
        float y = -(TITLE_H + GAP);
        _rowsScrollContainer.anchoredPosition = new Vector2(0f, y);
        _rowsScrollContainer.sizeDelta = new Vector2(0f, rowsViewportH);
        y -= rowsViewportH + GAP;

        var addRt = (RectTransform)_addBtn.transform;
        addRt.anchoredPosition = new Vector2(0f, y);
        y -= ROW_H + GAP;

        _pickerRoot.anchoredPosition = new Vector2(0f, y);
        if (pickerOpen)
        {
            _pickerRoot.sizeDelta = new Vector2(0f, PICKER_HEADER_H + pickerListH);
            // PickerListScroll sits below the picker header (label + InputField = 2*ROW_H).
            _pickerListScrollContainer.anchoredPosition = new Vector2(0f, -PICKER_HEADER_H);
            _pickerListScrollContainer.sizeDelta = new Vector2(0f, pickerListH);
        }
        else
        {
            _pickerRoot.sizeDelta = new Vector2(0f, 0f);
        }
    }

    // F2 fix (#84 review): the first Bind→Rebuild happens in BackcastWorkspaceRoot.Awake where the
    // Canvas hasn't laid out yet, so _content.rect.height is 0 and Relayout early-returns. As soon as
    // the rect resolves, Relayout once so the sidebar isn't empty for the first user-action delay.
    void Update()
    {
        if (_laidOutOnce || !_built || _content == null) return;
        if (_content.rect.height > 0f)
        {
            _laidOutOnce = true;
            Relayout();
        }
    }

    // ── uGUI builders ──

    static RectTransform NewRect(string name, RectTransform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
        return rt;
    }

    // a top-anchored, full-width strip whose top edge sits at anchoredPosition.y inside the parent.
    static void TopStrip(RectTransform rt, float topY, float h)
    {
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(0f, h); rt.anchoredPosition = new Vector2(0f, topY);
    }

    Text MakeText(RectTransform parent, string text, int size, Color color, TextAnchor anchor, float topY, float h)
    {
        var go = new GameObject("text", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0f, 1f);
        rt.sizeDelta = new Vector2(0f, h); rt.anchoredPosition = new Vector2(0f, topY);
        var t = go.GetComponent<Text>();
        t.font = _font; t.fontSize = size; t.color = color; t.text = text; t.alignment = anchor;
        t.supportRichText = false; t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    Button MakeButton(RectTransform parent, string text, UnityEngine.Events.UnityAction onClick, out Text label)
    {
        var go = new GameObject("btn:" + text, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
        TopStrip(rt, 0f, ROW_H - 1f);
        go.GetComponent<Image>().color = ThemeService.Current.colors.element_background;
        go.GetComponent<Button>().onClick.AddListener(onClick);
        label = AddLabel(rt, text, TextAnchor.MiddleLeft, ThemeService.Current.colors.text_accent);
        return go.GetComponent<Button>();
    }

    InputField MakeInputField(RectTransform parent, string value, float topY)
    {
        var go = new GameObject("search", typeof(RectTransform), typeof(Image), typeof(InputField));
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
        TopStrip(rt, topY, ROW_H - 1f);
        go.GetComponent<Image>().color = ThemeService.Current.colors.element_background;
        var text = AddLabel(rt, "", TextAnchor.MiddleLeft, ThemeService.Current.colors.text);
        text.supportRichText = false;
        var input = go.GetComponent<InputField>();
        input.textComponent = text;
        input.text = value;
        return input;
    }

    Text AddLabel(RectTransform parent, string text, TextAnchor anchor, Color color)
    {
        var go = new GameObject("t", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform; rt.SetParent(parent, false);
        Stretch(rt); rt.offsetMin = new Vector2(4f, 0f); rt.offsetMax = new Vector2(-4f, 0f);
        var t = go.GetComponent<Text>();
        t.font = _font; t.fontSize = 12; t.color = color; t.text = text; t.alignment = anchor;
        t.supportRichText = false; t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    void ClearChildren(RectTransform root)
    {
        if (root == null) return;
        for (int i = root.childCount - 1; i >= 0; i--)
        {
            var go = root.GetChild(i).gameObject;
            if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
        }
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    void OnSelectedChanged(string _) => Rebuild();

    void OnDestroy()
    {
        if (_ctrl != null)
        {
            _ctrl.Registry.Changed -= Rebuild;
            if (_ctrl.Selected != null) _ctrl.Selected.Changed -= OnSelectedChanged;
        }
    }
}
