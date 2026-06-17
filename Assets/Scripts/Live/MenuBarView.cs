// MenuBarView.cs — issue #77 "menu dropdown z-order" (uGUI cutover of the global menu bar, findings 0045)
//
// The scene-authored production HOST for the menu bar. It KEEPS the existing MenuBarViewModel brain and
// renders the TTWR File/Edit/Venue/Help surface (ADR-0005 1:1 parity) as uGUI on its OWN nested,
// override-sorting Canvas (sortingOrder MENU_SORT), clipped to its scene-authored container
// RectTransform (the draw region is DERIVED, never hardcoded).
//
// WHY uGUI (was OnGUI): #77 — both this bar and the sidebar were IMGUI; GUI.depth is ignored in a
// single-camera Screen-Space setup, so the (later) sidebar OnGUI overpainted the dropdown. uGUI makes
// z-order DETERMINISTIC: the menu Canvas (MENU_SORT) sits above the sidebar Canvas (< MENU_SORT) so the
// dropdown always draws in front, and the EventSystem routes a click to the TOP raycaster only, so the
// dropdown can't bleed a click into the sidebar beneath it. A full-screen BACKDROP (BACKDROP_SORT,
// between sidebar and menu) is active only while a menu is open: it closes the menu on an outside click
// AND consumes that click so it never reaches the sidebar (desktop menu semantics). The secret modal's
// Canvas (1000) stays above the menu, so it remains topmost (findings 0045).
//
//   * File = Layout (New / Open / Save) — forwards to the workspace root's layout I/O.
//   * Venue = the reused VenueMenuViewModel (vm.Venue): 4 TTWR connect variants (prod grey-out) +
//     Disconnect. MOCK is NOT a parity variant — it surfaces only as a dev-only connect in the editor
//     (findings 0027 D2), used to reach the LiveAuto-on-mainline HITL.
//   * Edit / Help — present for structure parity; bodies deferred to #16 / the settings slice (stub).

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(RectTransform))]
public sealed class MenuBarView : MonoBehaviour
{
    enum OpenMenu { None, File, Edit, Venue, Help, Strategy }

    // chrome z-order contract (findings 0045): field/windows(0) < sidebar < BACKDROP < menu+dropdown
    // < secret modal(1000). Only the RELATIONS matter; these values just realise them.
    public const int MENU_SORT = 600;
    const int BACKDROP_SORT = MENU_SORT - 1;   // catches outside clicks above the sidebar, below the dropdown

    RectTransform _container;
    MenuBarViewModel _vm;
    Action _onNew, _onOpen, _onSave, _onDisconnect;
    Action<string, string> _onConnect;     // (venue, env)
    Action<string> _onOpenStrategy;        // #80: open a strategy .py by absolute path
    Func<IReadOnlyList<StrategyPickerEntry>> _enumStrategies;   // #80: re-enumerated each time the menu opens (stale-safe)
    readonly List<GameObject> _strategyItems = new List<GameObject>();   // dynamic rows, rebuilt on open
    Func<bool> _connectReady;              // server ready && !teardown
    Func<string> _modeText;                // current execution-mode display for the bar badge
    bool _showMockConnect;                 // dev-only MOCK connect item (editor only); derived at Bind
    OpenMenu _open;
    string _message;
    Font _font;
    bool _built;
    string _lastBadgeText, _lastMode, _lastMessage;   // badge source cache: rebuild the string only on change

    // top-level button widths (fixed so the submenu drop x-offsets line up under each button).
    const float W_FILE = 56f, W_EDIT = 44f, W_VENUE = 52f, W_HELP = 44f, W_STRATEGY = 72f, ITEM_H = 22f, V_MARGIN = 4f;
    const float STRATEGY_DD_W = 320f;   // wide enough for "<relpath>   — scenario: ⚠ unreadable"

    // retained uGUI graphics reflected by Refresh (no per-frame rebuild of the static tree).
    Canvas _canvas;
    Text _badge;
    GameObject _backdrop;
    readonly Dictionary<OpenMenu, GameObject> _dropdowns = new Dictionary<OpenMenu, GameObject>();
    // venue items whose interactable state depends on live connection — refreshed each frame.
    readonly List<(Button btn, Func<bool> enabled)> _venueItems = new List<(Button, Func<bool>)>();

    // Root wires the existing brain + the layout I/O and venue callbacks. The VM owns the File→New
    // refuse-when-running gate and the venue logic (vm.Venue); the root performs the real clear/save/
    // restore and the venue login/logout RPCs (findings 0027 D3/D5).
    public void Bind(MenuBarViewModel vm,
                     Action onNew, Action onOpen, Action onSave,
                     Action<string, string> onConnect, Action onDisconnect,
                     Func<bool> connectReady, Func<string> modeText, string devVenue, Font font,
                     Action<string> onOpenStrategy = null,
                     Func<IReadOnlyList<StrategyPickerEntry>> enumStrategies = null)
    {
        _vm = vm;
        _onNew = onNew;
        _onOpen = onOpen;
        _onSave = onSave;
        _onConnect = onConnect;
        _onOpenStrategy = onOpenStrategy;
        _enumStrategies = enumStrategies;
        _onDisconnect = onDisconnect;
        _connectReady = connectReady;
        _modeText = modeText;
        _showMockConnect = devVenue == "MOCK";   // MOCK is the only credential-less dev venue (findings 0027 D2)
        _container = GetComponent<RectTransform>();
        _font = font != null ? font : Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        Build();
        Refresh();
    }

    public void ShowMessage(string msg) { _message = msg; if (_built) Refresh(); }

    void Build()
    {
        if (_built) return;
        _built = true;

        // own override-sorting Canvas so the bar + dropdowns sit above the sidebar deterministically.
        _canvas = gameObject.GetComponent<Canvas>();
        if (_canvas == null) _canvas = gameObject.AddComponent<Canvas>();
        _canvas.overrideSorting = true;
        _canvas.sortingOrder = MENU_SORT;
        if (gameObject.GetComponent<GraphicRaycaster>() == null) gameObject.AddComponent<GraphicRaycaster>();

        var c = ThemeService.Current.colors;

        // bar background fills the authored container (raycast target so clicks on the bar gutter don't
        // fall through to the workspace beneath).
        var barBg = NewImage("BarBg", _container, c.panel_background);
        Stretch((RectTransform)barBg.transform);

        // full-screen backdrop on the ROOT canvas (own canvas between sidebar and menu): closes the menu
        // on an outside click and consumes it so it never reaches the sidebar. Hidden unless a menu opens.
        BuildBackdrop();

        // top-level buttons (left), badge (fills the rest, right-aligned content).
        float x = 0f;
        MakeBarButton("File", W_FILE, x, () => Toggle(OpenMenu.File)); x += W_FILE;
        MakeBarButton("Edit", W_EDIT, x, () => Toggle(OpenMenu.Edit)); x += W_EDIT;
        MakeBarButton("Venue", W_VENUE, x, () => Toggle(OpenMenu.Venue)); x += W_VENUE;
        MakeBarButton("Help", W_HELP, x, () => Toggle(OpenMenu.Help)); x += W_HELP;
        MakeBarButton("Strategy", W_STRATEGY, x, () => Toggle(OpenMenu.Strategy)); x += W_STRATEGY;

        _badge = MakeBadge(x + 8f);

        // dropdowns hang BELOW the bar (no mask, so they spill past the 1-row container) at the owning
        // button's x. Built once; shown/hidden by Refresh from _open.
        BuildFileMenu();
        BuildEditMenu();
        BuildVenueMenu();
        BuildHelpMenu();
        BuildStrategyMenu();
    }

    void BuildBackdrop()
    {
        var rootCanvas = _canvas.rootCanvas != null ? _canvas.rootCanvas : _canvas;
        var go = new GameObject("MenuBackdrop", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(rootCanvas.transform, false);
        Stretch(rt);
        var cv = go.GetComponent<Canvas>();
        cv.overrideSorting = true;
        cv.sortingOrder = BACKDROP_SORT;
        go.GetComponent<Image>().color = new Color(0f, 0f, 0f, 0f);   // invisible, but a raycast target
        go.GetComponent<Button>().onClick.AddListener(() => { _open = OpenMenu.None; Refresh(); });
        go.SetActive(false);
        _backdrop = go;
    }

    // Reflect VM/menu state into the retained widgets (cheap; no GameObject churn). Driven each frame
    // because the badge/connect-ready follow the async venue poll.
    void Refresh()
    {
        if (!_built) return;
        if (_badge != null)
        {
            // Refresh runs every frame (async venue poll), so only rebuild the interpolated/rich-text
            // string when a source value actually changed — otherwise this leaks GC garbage per frame.
            string badgeText = _vm != null ? _vm.Venue.BadgeText : "";
            string mode = ModeText();
            if (badgeText != _lastBadgeText || mode != _lastMode || _message != _lastMessage)
            {
                _lastBadgeText = badgeText; _lastMode = mode; _lastMessage = _message;
                string badge = _vm != null ? $"{badgeText}   mode: {mode}" : "";
                // the transient message keeps its orange highlight (parity with the retired OnGUI label).
                if (!string.IsNullOrEmpty(_message)) badge += "    <color=orange>" + _message + "</color>";
                _badge.text = badge;
            }
        }

        foreach (var kv in _dropdowns)
            if (kv.Value != null) kv.Value.SetActive(_open == kv.Key);
        if (_backdrop != null) _backdrop.SetActive(_open != OpenMenu.None);

        foreach (var (btn, enabled) in _venueItems)
            if (btn != null) btn.interactable = enabled();
    }

    void Update() { if (_built) Refresh(); }

    void Toggle(OpenMenu m)
    {
        _open = _open == m ? OpenMenu.None : m;
        // #80: re-enumerate the strategy list each time it OPENS so a .py added/removed/renamed
        // since last open (or a stale entry) is reflected — the picker owns no live watcher.
        if (_open == OpenMenu.Strategy) RebuildStrategyMenu();
        Refresh();
    }
    string ModeText() => _modeText != null ? _modeText() : "-";
    bool Ready() => _connectReady == null || _connectReady();

    void BuildFileMenu()
    {
        var dd = NewDropdown(OpenMenu.File, 0f, 150f, 4);
        MakeItem(dd, "New", () => { _open = OpenMenu.None; _onNew?.Invoke(); Refresh(); }, 0);
        MakeItem(dd, "Open  (layout)", () => { _open = OpenMenu.None; _onOpen?.Invoke(); Refresh(); }, 1);
        MakeItem(dd, "Save  (layout)", () => { _open = OpenMenu.None; _onSave?.Invoke(); Refresh(); }, 2);
        MakeDisabledItem(dd, "Save As…  (see #69)", 3);   // deferred: native file picker + multi-doc (#69)
    }

    void BuildEditMenu()
    {
        // Undo/Redo route to the active strategy editor (#16); no active-editor concept wired here yet,
        // so they are disabled stubs (findings 0027 §3 follow-up).
        var dd = NewDropdown(OpenMenu.Edit, W_FILE, 200f, 2);
        MakeDisabledItem(dd, "Undo  (no active editor)", 0);
        MakeDisabledItem(dd, "Redo  (no active editor)", 1);
    }

    void BuildVenueMenu()
    {
        var venue = _vm.Venue;
        var items = new List<(string label, string v, string env)>();
        // dev-only MOCK connect (editor only): MOCK is a credential-less dev venue, NOT a TTWR parity
        // variant, surfaced so the LiveAuto-on-mainline HITL is reachable (findings 0027 D2).
        if (Application.isEditor && _showMockConnect) items.Add(("Connect MOCK (dev)", "MOCK", ""));
        foreach (var v in VenueMenuViewModel.ConnectVariants) items.Add((v.Label, v.Venue, v.Env));

        var dd = NewDropdown(OpenMenu.Venue, W_FILE + W_EDIT, 260f, items.Count + 1);
        int row = 0;
        foreach (var (label, v, env) in items)
        {
            string venueId = v, envId = env;
            var btn = MakeItem(dd, label, () => { _open = OpenMenu.None; _onConnect?.Invoke(venueId, envId); Refresh(); }, row++);
            // MOCK is a plain dev connect (CanConnect); prod variants grey out unless *_ALLOW_PROD is set
            // (mirrors the login dialog; Python is the safety authority). all disabled while connected/mid-auth.
            if (venueId == "MOCK") _venueItems.Add((btn, () => Ready() && venue.CanConnect));
            else _venueItems.Add((btn, () => Ready() && venue.CanConnectEnv(venueId, envId)));
        }
        var dis = MakeItem(dd, "Disconnect", () => { _open = OpenMenu.None; _onDisconnect?.Invoke(); Refresh(); }, row);
        _venueItems.Add((dis, () => Ready() && venue.CanDisconnect));
    }

    void BuildHelpMenu()
    {
        // ADR-0005 lists Settings as its own surface — item present, body deferred to that slice.
        var dd = NewDropdown(OpenMenu.Help, W_FILE + W_EDIT + W_VENUE, 220f, 1);
        MakeDisabledItem(dd, "Settings  (deferred slice)", 0);
    }

    // #80: in-app "Open Strategy .py" picker. The dropdown PANEL is built once (empty); its rows are
    // (re)built on each open by RebuildStrategyMenu from the enumerator. Lists python/strategies/**.py
    // ONLY, annotated with scenario status; opening is unconditional (Run gate, not Open gate, blocks
    // an unrunnable .py — findings 0047 §2). The shell is HITL; StrategyPickerModel is AFK-locked.
    void BuildStrategyMenu()
    {
        NewDropdown(OpenMenu.Strategy, W_FILE + W_EDIT + W_VENUE + W_HELP, STRATEGY_DD_W, 1);
    }

    void RebuildStrategyMenu()
    {
        if (!_dropdowns.TryGetValue(OpenMenu.Strategy, out var dd) || dd == null) return;

        // Clear the previous rows: hide NOW (so they don't render this frame) then Destroy.
        foreach (var go in _strategyItems)
            if (go != null) { go.SetActive(false); if (Application.isPlaying) Destroy(go); else DestroyImmediate(go); }
        _strategyItems.Clear();

        IReadOnlyList<StrategyPickerEntry> entries = _enumStrategies != null ? _enumStrategies() : null;
        int rows = (entries == null || entries.Count == 0) ? 1 : entries.Count;
        ((RectTransform)dd.transform).sizeDelta = new Vector2(STRATEGY_DD_W, DropdownHeight(rows));

        if (entries == null || entries.Count == 0)
        {
            _strategyItems.Add(MakeDisabledItem(dd, "(no .py under python/strategies)", 0).gameObject);
            return;
        }

        int row = 0;
        foreach (var e in entries)
        {
            string path = e.Path;   // capture per-row for the closure
            string label = e.DisplayName + "   — " + StrategyPickerModel.StatusLabel(e.Status);
            _strategyItems.Add(MakeItem(dd, label,
                () => { _open = OpenMenu.None; _onOpenStrategy?.Invoke(path); Refresh(); }, row++).gameObject);
        }
    }

    // ── uGUI builders ──

    // A dropdown panel anchored to the container's bottom-left (pivot top-left) hanging down at x.
    GameObject NewDropdown(OpenMenu key, float x, float w, int rows)
    {
        var go = NewImage("dd:" + key, _container, ThemeService.Current.colors.element_background);
        var rt = (RectTransform)go.transform;
        rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);   // container bottom-left
        rt.pivot = new Vector2(0f, 1f);                       // top-left: hangs downward from the bar
        rt.anchoredPosition = new Vector2(x, 0f);
        rt.sizeDelta = new Vector2(w, DropdownHeight(rows));
        go.SetActive(false);
        _dropdowns[key] = go;
        return go;
    }

    // Dropdown panel height for `rows` item rows (ITEM_H each) + a small bottom pad. Shared by
    // NewDropdown's initial sizing and RebuildStrategyMenu's per-open re-sizing (#80) so the formula
    // lives in one place.
    static float DropdownHeight(int rows) => rows * ITEM_H + 4f;

    Button MakeItem(GameObject dd, string label, Action onClick, int row)
    {
        var go = new GameObject("item:" + label, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(dd.transform, false);
        rt.anchorMin = new Vector2(0f, 1f); rt.anchorMax = new Vector2(1f, 1f); rt.pivot = new Vector2(0.5f, 1f);
        rt.offsetMin = new Vector2(2f, 0f); rt.offsetMax = new Vector2(-2f, 0f);
        rt.sizeDelta = new Vector2(rt.sizeDelta.x, ITEM_H - 2f);
        rt.anchoredPosition = new Vector2(0f, -row * ITEM_H - 2f);
        go.GetComponent<Image>().color = ThemeService.Current.colors.element_background;
        var btn = go.GetComponent<Button>();
        btn.onClick.AddListener(() => onClick());
        AddLabel(rt, label, TextAnchor.MiddleLeft, ThemeService.Current.colors.text);
        return btn;
    }

    Button MakeDisabledItem(GameObject dd, string label, int row)
    {
        var btn = MakeItem(dd, label, () => { }, row);
        btn.interactable = false;
        return btn;
    }

    void MakeBarButton(string text, float w, float x, Action onClick)
    {
        var go = new GameObject("bar:" + text, typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)go.transform;
        rt.SetParent(_container, false);
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(0f, 1f); rt.pivot = new Vector2(0f, 0.5f);
        rt.sizeDelta = new Vector2(w, -V_MARGIN);
        rt.anchoredPosition = new Vector2(x, 0f);
        go.GetComponent<Image>().color = ThemeService.Current.colors.element_background;
        go.GetComponent<Button>().onClick.AddListener(() => onClick());
        AddLabel(rt, text, TextAnchor.MiddleCenter, ThemeService.Current.colors.text);
    }

    Text MakeBadge(float xLeft)
    {
        var go = new GameObject("badge", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform;
        rt.SetParent(_container, false);
        rt.anchorMin = new Vector2(0f, 0f); rt.anchorMax = new Vector2(1f, 1f);
        rt.offsetMin = new Vector2(xLeft, 0f); rt.offsetMax = new Vector2(-6f, 0f);
        var t = go.GetComponent<Text>();
        t.font = _font; t.fontSize = 12; t.color = ThemeService.Current.colors.text_muted;
        t.alignment = TextAnchor.MiddleRight; t.supportRichText = true;   // <color=orange> message highlight
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
        return t;
    }

    GameObject NewImage(string name, RectTransform parent, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        go.GetComponent<Image>().color = color;
        return go;
    }

    void AddLabel(RectTransform parent, string text, TextAnchor anchor, Color color)
    {
        var go = new GameObject("t", typeof(RectTransform), typeof(Text));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        Stretch(rt);
        rt.offsetMin = new Vector2(6f, 0f); rt.offsetMax = new Vector2(-4f, 0f);
        var t = go.GetComponent<Text>();
        t.font = _font; t.fontSize = 12; t.color = color; t.text = text;
        t.alignment = anchor; t.supportRichText = false; t.raycastTarget = false;
        t.horizontalOverflow = HorizontalWrapMode.Overflow; t.verticalOverflow = VerticalWrapMode.Overflow;
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    // The backdrop is a SEPARATE GO under the root canvas, so it isn't hidden when this view is disabled.
    // Force-close the menu and hide it here — else a disable while a menu is open would leave a full-screen
    // invisible click-blocker over the workspace. Update()/Refresh() re-syncs cleanly on re-enable (_open=None).
    void OnDisable()
    {
        _open = OpenMenu.None;
        if (_backdrop != null) _backdrop.SetActive(false);
    }

    void OnDestroy()
    {
        if (_backdrop != null)
        {
            if (Application.isPlaying) Destroy(_backdrop); else DestroyImmediate(_backdrop);
        }
    }
}
