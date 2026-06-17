// UniverseSidebarView.cs — issue #77 "menu dropdown z-order" (uGUI cutover of the universe sidebar, findings 0045)
//
// The scene-authored production HOST for the instrument-picker / universe sidebar. It REUSES the durable
// UniverseSidebarController brain and renders the screen-fixed left sidebar as uGUI on its OWN nested,
// override-sorting Canvas (sortingOrder SIDEBAR_SORT), clipped to its scene-authored container
// RectTransform (draw region DERIVED, never hardcoded).
//
// WHY uGUI (was OnGUI): #77 — the sidebar and the menu were BOTH IMGUI, and a single-camera Screen-Space
// setup ignores GUI.depth, so the (later) sidebar OnGUI overpainted the menu dropdown. uGUI makes z-order
// deterministic by sortingOrder: this sidebar sits BELOW the menu (MenuBarView.MENU_SORT) so the dropdown
// draws in front, and the EventSystem routes clicks to the top raycaster (no input bleed). See findings 0045.
//
// Python-FREE: the controller drives SelectedSymbol (the depth-target focus) and the universe writeback;
// the candidate source is injected by the root (a separate issue owns the real DuckDB/venue universe).
//
// RETAINED-MODE REBUILD: the instrument rows + picker list are rebuilt on the controller's observable
// changes (InstrumentRegistry.Changed / SelectedSymbol.Changed / picker toggle), NOT every frame. The
// search field is built once while the picker is open so per-keystroke list rebuilds never steal focus.

using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RectTransform))]
public sealed class UniverseSidebarView : MonoBehaviour
{
    // below MenuBarView.MENU_SORT so the dropdown overlaps the sidebar, above the field/windows (0) so the
    // sidebar chrome stays visible over the workspace (findings 0045).
    public const int SIDEBAR_SORT = 500;

    const float PAD = 6f, ROW_H = 22f, TITLE_H = 22f, GAP = 2f, REMOVE_W = 24f;

    RectTransform _container;
    UniverseSidebarController _ctrl;
    IStrategyFileProvider _strategyProvider;
    UniverseSourceMode _mode = UniverseSourceMode.Replay;
    string _replayEnd = "2024-12-31";
    Font _font;
    bool _built;

    // stable widgets (positions recomputed by Relayout; children rebuilt on change).
    RectTransform _content, _rowsRoot, _pickerRoot, _pickerListRoot;
    Button _addBtn;
    Text _addLabel, _focusLabel;
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

        var canvas = gameObject.GetComponent<Canvas>();
        if (canvas == null) canvas = gameObject.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = SIDEBAR_SORT;
        if (gameObject.GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

        var t = ThemeService.Current;
        var bgGo = new GameObject("SidebarBg", typeof(RectTransform), typeof(Image));
        var bgRt = (RectTransform)bgGo.transform; bgRt.SetParent(_container, false); Stretch(bgRt);
        bgGo.GetComponent<Image>().color = t.colors.panel_background;

        _content = NewRect("Content", _container);
        Stretch(_content);
        _content.offsetMin = new Vector2(PAD, PAD); _content.offsetMax = new Vector2(-PAD, -PAD);

        MakeText(_content, "Instruments", 13, t.status.info, TextAnchor.UpperLeft, 0f, TITLE_H);

        _rowsRoot = NewRect("Rows", _content); TopStrip(_rowsRoot, -(TITLE_H + GAP), 0f);

        _addBtn = MakeButton(_content, "+ Add", () => { _ctrl.TogglePicker(_mode, _replayEnd); Rebuild(); }, out _addLabel);

        _pickerRoot = NewRect("Picker", _content); TopStrip(_pickerRoot, 0f, 0f);

        // focus → depth label, pinned to the bottom.
        var fGo = new GameObject("focus", typeof(RectTransform), typeof(Text));
        var fRt = (RectTransform)fGo.transform; fRt.SetParent(_content, false);
        fRt.anchorMin = new Vector2(0f, 0f); fRt.anchorMax = new Vector2(1f, 0f); fRt.pivot = new Vector2(0f, 0f);
        fRt.sizeDelta = new Vector2(0f, ROW_H); fRt.anchoredPosition = Vector2.zero;
        _focusLabel = fGo.GetComponent<Text>();
        _focusLabel.font = _font; _focusLabel.fontSize = 11; _focusLabel.color = t.colors.text_muted;
        _focusLabel.alignment = TextAnchor.LowerLeft; _focusLabel.supportRichText = false; _focusLabel.raycastTarget = false;
    }

    // Rebuild the dynamic content (rows + picker) and re-flow the stable widgets.
    void Rebuild()
    {
        if (!_built) return;
        var t = ThemeService.Current;

        ClearChildren(_rowsRoot);
        int n = 0;
        foreach (SidebarRow r in _ctrl.Rows())
        {
            BuildRow(r, n);
            n++;
        }
        if (_ctrl.Registry.Count == 0)
            MakeText(_rowsRoot, "No instruments", 11, t.colors.text_muted, TextAnchor.MiddleLeft, 0f, ROW_H);
        float rowsH = (n == 0 ? 1 : n) * ROW_H;   // 1 row of height reserved for the "No instruments" label
        _rowsRoot.sizeDelta = new Vector2(0f, rowsH);

        if (_addLabel != null) _addLabel.text = _ctrl.Picker.Visible ? "− Close" : "+ Add";

        BuildPicker();
        Relayout(rowsH);

        if (_focusLabel != null)
            _focusLabel.text = "focus → depth: " + (_ctrl.Selected.HasValue ? _ctrl.Selected.Value : "(none)");
    }

    void BuildRow(SidebarRow r, int index)
    {
        var t = ThemeService.Current;
        string id = r.Id;
        var rowGo = new GameObject("row:" + id, typeof(RectTransform));
        var rowRt = (RectTransform)rowGo.transform; rowRt.SetParent(_rowsRoot, false);
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

    // Build the search field + candidate list when the picker is open; tear it down when closed. The
    // field is created here ONCE per open, so the per-keystroke RebuildPickerList never destroys it.
    void BuildPicker()
    {
        ClearChildren(_pickerRoot);
        _searchInput = null; _pickerListRoot = null;
        if (!_ctrl.Picker.Visible) { _pickerRoot.sizeDelta = new Vector2(0f, 0f); return; }

        var t = ThemeService.Current;
        MakeText(_pickerRoot, "search:", 11, t.colors.text_muted, TextAnchor.UpperLeft, 0f, ROW_H);

        _searchInput = MakeInputField(_pickerRoot, _ctrl.Picker.Query ?? "", -ROW_H);
        _searchInput.onValueChanged.AddListener(q => { _ctrl.Picker.SetQuery(q); RebuildPickerList(); });

        _pickerListRoot = NewRect("PickerList", _pickerRoot); TopStrip(_pickerListRoot, -2f * ROW_H, 0f);
        RebuildPickerList();   // sets _pickerListRoot.sizeDelta.y to the list height — reuse it (no re-enumeration).
        _pickerRoot.sizeDelta = new Vector2(0f, 2f * ROW_H + _pickerListRoot.sizeDelta.y);
    }

    // List-only rebuild (query change): leaves the persistent search field untouched.
    void RebuildPickerList()
    {
        if (_pickerListRoot == null) return;
        ClearChildren(_pickerListRoot);
        var t = ThemeService.Current;
        int i = 0;
        foreach (PickerRow pr in _ctrl.PickerList(_mode))
        {
            if (pr.IsPlaceholder)
            {
                MakeText(_pickerListRoot, "  " + pr.Label, 11, t.colors.text_muted, TextAnchor.MiddleLeft, -i * ROW_H, ROW_H);
                i++;
                continue;
            }
            string id = pr.Id;
            string lbl = (pr.AlreadyAdded ? "✓ " : "+ ") + pr.Label;
            var go = new GameObject("cand:" + id, typeof(RectTransform), typeof(Image), typeof(Button));
            var rt = (RectTransform)go.transform; rt.SetParent(_pickerListRoot, false);
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
        if (_pickerListRoot != null) _pickerListRoot.sizeDelta = new Vector2(0f, i * ROW_H);
    }

    // Re-flow the add button + picker under the (variable-height) rows block.
    void Relayout(float rowsH)
    {
        float y = -(TITLE_H + GAP);
        _rowsRoot.anchoredPosition = new Vector2(0f, y);
        y -= rowsH + GAP;
        var addRt = (RectTransform)_addBtn.transform;
        addRt.anchoredPosition = new Vector2(0f, y);
        y -= ROW_H + GAP;
        _pickerRoot.anchoredPosition = new Vector2(0f, y);
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
