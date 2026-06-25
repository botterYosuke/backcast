// SettingsDialogE2ERunner.cs — Surface E2E for the Settings modal (ADR-0026 / findings 0102).
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod SettingsDialogE2ERunner.Run -logFile <abs log>
//   # expect: [E2E SETTINGS DIALOG PASS] ... + per-id [E2E SETTINGS-0N PASS] / exit=0
//   # (confirm with Bash `grep -a "E2E SETTINGS"` — ripgrep/Select-String drop the → in PASS lines.)
//
// Pure / headless: the controller (SettingsModalController) is MonoBehaviour-free; the overlay
// (SettingsModalOverlay) and the venue section view build under bare GameObjects and are reflected
// (activeSelf / interactable / private lists) — no GPU, no Python. The Mode section view is gated by
// FooterModeE2ERunner (FOOTER-06/07 retargeted to SettingsModeSegmentView); the Scenario section's
// dock-count 5→4 + forward-compat skip by ScenarioStartupE2ERunner (SCENARIO-16/17); the venue VM gating
// by VenueMenuM3Probe — this台本 owns the modal SHELL + section hosting + z-order + ESC guard.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;

public static class SettingsDialogE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    static readonly List<string> _fail = new List<string>();

    static string Poll(string venueState) =>
        "{\"execution_mode\":\"Replay\",\"venue_state\":\"" + venueState + "\"}";

    public static void Run()
    {
        Section("SETTINGS-01", Section01_ControllerToggle);
        Section("SETTINGS-02", Section02_EscDefersToDrag);
        Section("SETTINGS-03", Section03_EscConsumedByBlockingModal);
        Section("SETTINGS-04", Section04_EscToggles);
        Section("SETTINGS-05", Section05_OverlayVisibilityAndClose);
        Section("SETTINGS-06", Section06_ZOrderContract);
        Section("SETTINGS-07", Section07_OverlayChromeNonVacuity);
        Section("SETTINGS-08", Section08_VenueSectionAndMenuRetired);
        Section("SETTINGS-09", Section09_InputFieldDistinction);
        Section("SETTINGS-10", Section10_TwoTabSwitch);
        Section("SETTINGS-11", Section11_CardSurfacesAndRoles);
        Section("SETTINGS-13", Section13_LiveReThemeWhileOpen);

        if (_fail.Count == 0)
            Debug.Log("[E2E SETTINGS DIALOG PASS] modal shell open/close (SETTINGS-01) / ESC guard: " +
                      "drag-revert (SETTINGS-02), secret+save-guard consume (SETTINGS-03), toggle (SETTINGS-04) / " +
                      "[x]+SetVisible (SETTINGS-05) / z-order menu<settings<secret (SETTINGS-06) / chrome+sections " +
                      "(SETTINGS-07) / venue section + menu Venue retired (SETTINGS-08) / input-field border+placeholder+" +
                      "muted label (SETTINGS-09) / 2-tab 実行↔外観 switch (SETTINGS-10) / card面+role解決 (SETTINGS-11) / " +
                      "LIVE Dark↔Light re-theme while open: close btn + venue + mode (SETTINGS-13) " +
                      "— ADR-0026, findings 0102/0107");
        else
            Debug.LogError("[E2E SETTINGS DIALOG FAIL]\n  - " + string.Join("\n  - ", _fail));

        EditorApplication.Exit(_fail.Count == 0 ? 0 : 1);
    }

    // Run one section; emit its per-Action-ID PASS tag (single token → rollup-visible) when it returns null.
    static void Section(string id, Func<string> body)
    {
        string err;
        try { err = body(); }
        catch (Exception e) { err = "exception: " + e; }
        if (err == null) Debug.Log("[E2E " + id + " PASS]");
        else _fail.Add(id + ": " + err);
    }

    // SETTINGS-01: the controller opens, closes, and reports IsOpen.
    static string Section01_ControllerToggle()
    {
        var c = new SettingsModalController();
        if (c.IsOpen) return "controller starts open (must start closed)";
        c.Open();
        if (!c.IsOpen) return "Open() did not set IsOpen";
        c.Open();
        if (!c.IsOpen) return "second Open() flipped state (must be idempotent)";
        c.Close();
        if (c.IsOpen) return "Close() did not clear IsOpen";
        return null;
    }

    // SETTINGS-02: a window drag in progress → ESC defers to drag-revert (ADR-0024 §8); Settings unchanged.
    static string Section02_EscDefersToDrag()
    {
        var c = new SettingsModalController();
        // closed + dragging → no open
        if (c.OnEscape(dragInProgress: true, blockingModalOpen: false) != SettingsEscDecision.DeferToDrag)
            return "ESC during drag did not return DeferToDrag";
        if (c.IsOpen) return "ESC during drag OPENED Settings (must defer to drag-revert)";
        // open + dragging → stays open (does not close behind the drag)
        c.Open();
        if (c.OnEscape(true, false) != SettingsEscDecision.DeferToDrag) return "open+drag ESC not DeferToDrag";
        if (!c.IsOpen) return "ESC during drag CLOSED an open Settings (must defer)";
        // PRIORITY: drag-revert OUTRANKS a blocking modal (findings 0102 D1: drag > secret/save-guard > toggle).
        // Pins the ordering that drag=true & blocking=true is still DeferToDrag — swapping the OnEscape branch
        // order would flip this to ConsumedByBlockingModal and otherwise go undetected.
        if (c.OnEscape(true, true) != SettingsEscDecision.DeferToDrag) return "drag+blocking ESC must rank drag first (DeferToDrag)";
        if (!c.IsOpen) return "drag+blocking ESC closed Settings (drag-revert must win and defer)";
        return null;
    }

    // SETTINGS-03: secret / save-guard open → ESC consumed by that modal; Settings does NOT toggle.
    static string Section03_EscConsumedByBlockingModal()
    {
        var c = new SettingsModalController();
        // closed + blocking → must NOT open behind the blocking modal
        if (c.OnEscape(false, blockingModalOpen: true) != SettingsEscDecision.ConsumedByBlockingModal)
            return "ESC with blocking modal did not return ConsumedByBlockingModal";
        if (c.IsOpen) return "ESC opened Settings behind a blocking modal (secret/save-guard must consume ESC)";
        // open + blocking (e.g. secret raised FROM the Venue section) → Settings stays open under the secret
        c.Open();
        if (c.OnEscape(false, true) != SettingsEscDecision.ConsumedByBlockingModal) return "open+blocking ESC not consumed";
        if (!c.IsOpen) return "ESC closed Settings while a blocking modal was up (must stay open under secret)";
        return null;
    }

    // SETTINGS-04: otherwise ESC toggles open↔close.
    static string Section04_EscToggles()
    {
        var c = new SettingsModalController();
        if (c.OnEscape(false, false) != SettingsEscDecision.Toggled) return "ESC did not return Toggled";
        if (!c.IsOpen) return "ESC did not open a closed Settings";
        if (c.OnEscape(false, false) != SettingsEscDecision.Toggled) return "2nd ESC did not return Toggled";
        if (c.IsOpen) return "2nd ESC did not close an open Settings";
        return null;
    }

    // SETTINGS-05: the overlay reflects visibility and the [x] button raises CloseClicked.
    static string Section05_OverlayVisibilityAndClose()
    {
        var go = new GameObject("settings_overlay_e2e");
        try
        {
            var overlay = go.AddComponent<SettingsModalOverlay>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            overlay.Build(font);

            if (overlay.IsVisible) return "overlay starts visible (Build must SetVisible(false))";
            overlay.SetVisible(true);
            if (!overlay.IsVisible) return "SetVisible(true) did not set IsVisible";
            overlay.SetVisible(false);
            if (overlay.IsVisible) return "SetVisible(false) did not clear IsVisible";

            // [x] raises CloseClicked.
            bool closed = false;
            overlay.CloseClicked += () => closed = true;
            Button xBtn = null;
            foreach (var b in go.GetComponentsInChildren<Button>(true))
                if (b.gameObject.name == "btn_x") { xBtn = b; break; }
            if (xBtn == null) return "no [x] close button (btn_x) built";
            xBtn.onClick.Invoke();
            if (!closed) return "[x] click did not raise CloseClicked";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // SETTINGS-06: z-order contract — menu(600) < footer-promoted? no: footer(550) < settings(900) < secret(1000).
    // This is also the #128 secret-over-settings guarantee (a Venue-section second-password prompt at 1000
    // draws OVER Settings). RED litmus: bump SETTINGS_SORT to ≥1000 → secret no longer on top → fails here.
    static string Section06_ZOrderContract()
    {
        int settings = SettingsModalOverlay.SETTINGS_SORT;
        if (!(settings > MenuBarView.MENU_SORT)) return $"settings({settings}) not above menu({MenuBarView.MENU_SORT})";
        if (!(settings > WorkspaceFooterView.FOOTER_SORT)) return $"settings({settings}) not above footer({WorkspaceFooterView.FOOTER_SORT})";
        if (!(settings < 1000)) return $"settings({settings}) not below the secret/save-guard overlays (1000) — secret must draw on top";
        return null;
    }

    // SETTINGS-07: the overlay builds its chrome (backdrop + panel + [x]) and the THREE section containers
    // (Venue / Mode / Scenario) the host builds the reused section views into. Non-vacuity for SETTINGS-08
    // and for the Footer/Scenario runners that depend on these containers existing.
    static string Section07_OverlayChromeNonVacuity()
    {
        var go = new GameObject("settings_overlay_chrome_e2e");
        try
        {
            var overlay = go.AddComponent<SettingsModalOverlay>();
            overlay.Build(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

            if (overlay.VenueSection == null) return "VenueSection container missing";
            if (overlay.ModeSection == null) return "ModeSection container missing";
            if (overlay.ScenarioSection == null) return "ScenarioSection container missing";
            // the three sections must be distinct containers (a copy/paste bug would alias them).
            if (overlay.VenueSection == overlay.ModeSection || overlay.ModeSection == overlay.ScenarioSection
                || overlay.VenueSection == overlay.ScenarioSection)
                return "section containers alias each other (must be 3 distinct RectTransforms)";

            var names = new HashSet<string>();
            foreach (var rt in go.GetComponentsInChildren<RectTransform>(true)) names.Add(rt.gameObject.name);
            if (!names.Contains("Backdrop")) return "no full-screen Backdrop built";
            if (!names.Contains("SettingsPanel")) return "no centered SettingsPanel built";
            if (!names.Contains("btn_x")) return "no [x] close button built";

            // own ScreenSpaceOverlay canvas at SETTINGS_SORT.
            var canvas = go.GetComponent<Canvas>();
            if (canvas == null) return "overlay has no Canvas";
            if (canvas.renderMode != RenderMode.ScreenSpaceOverlay) return "overlay canvas is not ScreenSpaceOverlay";
            if (canvas.sortingOrder != SettingsModalOverlay.SETTINGS_SORT) return "overlay canvas sortingOrder ≠ SETTINGS_SORT";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // SETTINGS-08: the Venue section (#128) renders one button per VisibleConnectItems entry + Disconnect,
    // with interactable following the VM gating (disconnected → connects enabled, Disconnect disabled). AND
    // the menu-bar Venue dropdown is retired (OpenMenu enum no longer has a Venue entry).
    static string Section08_VenueSectionAndMenuRetired()
    {
        // (a) menu Venue dropdown retired — the OpenMenu nested enum has no "Venue".
        var openMenu = typeof(MenuBarView).GetNestedType("OpenMenu", BindingFlags.NonPublic);
        if (openMenu == null) return "MenuBarView.OpenMenu enum not found (renamed?)";
        foreach (var n in Enum.GetNames(openMenu))
            if (n == "Venue") return "MenuBarView.OpenMenu still has a Venue entry (dropdown must be retired)";

        // (b) the Settings Venue section builds connect buttons + Disconnect, gated by the VM.
        var go = new GameObject("settings_venue_e2e", typeof(RectTransform));
        try
        {
            var conn = new VenueConnectionViewModel();
            var coord = new LiveLogoutCoordinator();
            var vm = new VenueMenuViewModel(conn, coord);
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            // Capture the brain callbacks so (c) below can prove the buttons are actually WIRED, not just gated.
            string firedVenue = null, firedEnv = "<unset>"; bool disconnectFired = false;
            var view = new SettingsVenueSectionView(vm, null,
                (v, e) => { firedVenue = v; firedEnv = e; }, () => { disconnectFired = true; },
                () => true, font);
            view.Build((RectTransform)go.transform);

            var items = (IList)typeof(SettingsVenueSectionView).GetField("_items", BF).GetValue(view);
            int expectConnect = VenueMenuViewModel.VisibleConnectItems(null, Application.isEditor).Count;
            if (items.Count != expectConnect + 1)
                return $"venue section built {items.Count} items, expected {expectConnect} connects + 1 Disconnect";

            // disconnected (default poll state) → ALL connects enabled, Disconnect disabled
            // (CanDisconnect needs IsConnected). ADR-0027: the *_ALLOW_PROD env gate is abolished, so
            // prod variants are no longer greyed out — every connect button (incl. prod) is enabled
            // while disconnected (re-grey-out a prod variant → connectEnabled < connectCount → RED).
            view.Refresh();
            var btns = ((RectTransform)go.transform).GetComponentsInChildren<Button>(true);
            Button disconnect = null, firstConnect = null; int connectEnabled = 0, connectCount = 0;
            foreach (var b in btns)
            {
                if (b.gameObject.name == "venue:Disconnect") disconnect = b;
                else if (b.gameObject.name.StartsWith("venue:"))
                {
                    connectCount++; if (b.interactable) connectEnabled++;
                    if (firstConnect == null) firstConnect = b;   // reuse this pass for the onClick-wiring check below
                }
            }
            if (disconnect == null) return "no Disconnect button in the venue section";
            if (connectCount == 0) return "no connect buttons in the venue section (vacuous)";
            if (connectEnabled != connectCount)
                return $"disconnected: {connectCount - connectEnabled} connect button(s) greyed out — ADR-0027 requires all (incl. prod) enabled";
            if (disconnect.interactable) return "disconnected: Disconnect is enabled (must require IsConnected)";

            // (c) onClick WIRING (not just gating): exercise the actual button → brain callback. The asserts
            //     above only read interactable; an unwired button (onClick → nothing) would still pass them.
            //     onClick.Invoke fires the UnityEvent directly regardless of interactable — same technique as
            //     SETTINGS-05's [x] button. Closes the H1 "wiring not AFK-covered" gap (findings 0102).
            if (firstConnect == null) return "no connect button to exercise onClick (vacuous)";
            firstConnect.onClick.Invoke();
            if (firedVenue == null) return "connect button onClick did not fire _onConnect(venue,env) — wiring broken";
            if (firedEnv == "<unset>") return "connect onClick fired but env arg not threaded through — wiring broken";
            disconnect.onClick.Invoke();
            if (!disconnectFired) return "Disconnect button onClick did not fire _onDisconnect — wiring broken";

            // ADR-0027 / #130 — the enabled prod button actually DISPATCHES onConnect(venue,"prod").
            // This is the EXACT bug surface: the old grey-out left prod interactable=false so onClick
            // never fired ("press → nothing happens"). (c) proves wiring generically; this proves the
            // PROD click specifically reaches the handler with env="prod" (the variant that was greyed out).
            Button prodBtn = null;
            foreach (var b in btns)
                if (b.gameObject.name.IndexOf("Prod", StringComparison.Ordinal) >= 0) { prodBtn = b; break; }
            if (prodBtn == null) return "no prod connect button found to exercise dispatch";
            if (!prodBtn.interactable) return "prod connect button not interactable while disconnected";
            prodBtn.onClick.Invoke();
            if (firedEnv != "prod")
                return $"prod button dispatched env={firedEnv ?? "null"} (expected \"prod\")";
            if (firedVenue != "TACHIBANA" && firedVenue != "KABU")
                return $"prod button dispatched venue={firedVenue ?? "null"} (expected a live venue)";

            // connected → connects disable (CanConnect false). Proves Refresh re-reads the live VM gating.
            conn.ApplyStatePoll(Poll("CONNECTED"));
            view.Refresh();
            connectEnabled = 0;
            foreach (var b in btns)
                if (b.gameObject.name.StartsWith("venue:") && b.gameObject.name != "venue:Disconnect" && b.interactable) connectEnabled++;
            if (connectEnabled != 0) return "connected: a connect button is still enabled (CanConnect gating not applied)";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    static bool SameColor(Color a, Color b) =>
        Mathf.Abs(a.r - b.r) < 0.003f && Mathf.Abs(a.g - b.g) < 0.003f &&
        Mathf.Abs(a.b - b.b) < 0.003f && Mathf.Abs(a.a - b.a) < 0.003f;

    // SETTINGS-09 (#137 S1 / findings 0107 D5): every Settings InputField reads as an input — a visible
    // BORDER (Outline, role `border`), a PLACEHOLDER (role `text_placeholder`, non-empty), a sunk fill (role
    // `surface_background`) — and is paired with a MUTED label (role `text_muted`), so the owner's「どれが入力欄か
    // 分からない」不満 is fixed. Exercised on the Scenario tile (the representative Settings input form). The
    // Data field's own border/placeholder is pinned by DUCKROOT-03. RED litmus: drop the Outline, or paint a
    // field/label with an inline color instead of the role, → the role-equality asserts fail.
    static string Section09_InputFieldDistinction()
    {
        var go = new GameObject("settings_inputfield_e2e", typeof(RectTransform));
        try
        {
            var c = ThemeService.Current.colors;
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            var ctrl = new ScenarioStartupController();
            var tile = new ScenarioStartupTile(ctrl, font);
            tile.Build((RectTransform)go.transform);

            var fields = go.GetComponentsInChildren<InputField>(true);
            if (fields.Length < 4) return $"tile built {fields.Length} InputFields, expected ≥4 (Start/End/cash/universe)";

            foreach (var f in fields)
            {
                var border = f.GetComponent<Outline>();
                if (border == null) return "an InputField has no Outline border (S1 requires a visible border)";
                if (!SameColor(border.effectColor, c.border))
                    return "InputField border color is not the `border` role (inline color or wrong role)";

                var fill = f.GetComponent<Image>();
                if (fill == null || !SameColor(fill.color, c.surface_background))
                    return "InputField fill is not the `surface_background` role (sunk input面 — D5)";

                var ph = f.placeholder as Text;
                if (ph == null) return "an InputField has no Text placeholder (S1 requires a placeholder)";
                if (string.IsNullOrEmpty(ph.text)) return "InputField placeholder text is empty (must hint e.g. YYYY-MM-DD)";
                if (!SameColor(ph.color, c.text_placeholder))
                    return "InputField placeholder color is not the `text_placeholder` role";

                var body = f.textComponent;
                if (body == null || !SameColor(body.color, c.text))
                    return "InputField body text is not the `text` role";
            }

            // a YYYY-MM-DD placeholder must actually be present (non-vacuity for the date fields).
            bool sawDateHint = false;
            foreach (var f in fields)
                if ((f.placeholder as Text)?.text?.Contains("YYYY-MM-DD") == true) sawDateHint = true;
            if (!sawDateHint) return "no field carries the YYYY-MM-DD placeholder hint";

            // a muted label exists and is visually DISTINCT from the body text (label≠input — the core不満).
            bool sawMutedLabel = false;
            foreach (var t in go.GetComponentsInChildren<Text>(true))
                if (t.gameObject.name == "label" && SameColor(t.color, c.text_muted)) sawMutedLabel = true;
            if (!sawMutedLabel) return "no `text_muted` field label found (labels must be muted, distinct from inputs)";
            if (SameColor(c.text_muted, c.text)) return "text_muted == text role (labels would not be distinguishable)";

            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // SETTINGS-10 (#137 S2 / findings 0107 D6): the two tabs「実行 / 外観」. 実行 hosts Venue/Mode/Scenario/Data,
    // 外観 hosts Appearance; SelectTab toggles the group containers' activeSelf and the tab buttons recolor.
    // RED litmus: make SelectTab a no-op (or alias the two groups) → the activeSelf / section-parent asserts fail.
    static string Section10_TwoTabSwitch()
    {
        var go = new GameObject("settings_tabs_e2e");
        try
        {
            var overlay = go.AddComponent<SettingsModalOverlay>();
            overlay.Build(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            var c = ThemeService.Current.colors;

            var run = overlay.RunTabContent; var app = overlay.AppearanceTabContent;
            if (run == null || app == null) return "tab content groups missing";
            if (run == app) return "the two tab groups alias each other";

            // the 実行 sections live under the run group; Appearance lives under the appearance group.
            if (overlay.DataSection == null) return "DataSection container missing (#137 S4)";
            if (!IsDescendant(overlay.VenueSection, run)) return "Venue section not under the 実行 tab group";
            if (!IsDescendant(overlay.ModeSection, run)) return "Mode section not under the 実行 tab group";
            if (!IsDescendant(overlay.ScenarioSection, run)) return "Scenario section not under the 実行 tab group";
            if (!IsDescendant(overlay.DataSection, run)) return "Data section not under the 実行 tab group";
            if (!IsDescendant(overlay.AppearanceSection, app)) return "Appearance section not under the 外観 tab group";

            // tab buttons exist.
            Button runTab = null, appTab = null;
            foreach (var b in go.GetComponentsInChildren<Button>(true))
            {
                if (b.gameObject.name == "tab_run") runTab = b;
                else if (b.gameObject.name == "tab_appearance") appTab = b;
            }
            if (runTab == null || appTab == null) return "tab buttons (tab_run / tab_appearance) not built";

            // default = 実行 active.
            if (overlay.ActiveTab != SettingsTab.Run) return "default ActiveTab is not Run";
            if (!run.gameObject.activeSelf) return "default: 実行 group not active";
            if (app.gameObject.activeSelf) return "default: 外観 group active behind 実行";
            if (!SameColor(runTab.GetComponent<Image>().color, c.tab_active_background))
                return "active 実行 tab not painted tab_active_background";
            if (!SameColor(appTab.GetComponent<Image>().color, c.tab_inactive_background))
                return "inactive 外観 tab not painted tab_inactive_background";

            // click 外観 → groups swap + tab colors swap.
            appTab.onClick.Invoke();
            if (overlay.ActiveTab != SettingsTab.Appearance) return "clicking 外観 did not switch ActiveTab";
            if (run.gameObject.activeSelf) return "外観 selected but 実行 group still active";
            if (!app.gameObject.activeSelf) return "外観 selected but 外観 group not active";
            if (!SameColor(appTab.GetComponent<Image>().color, c.tab_active_background))
                return "外観 tab not painted active after click";

            // click 実行 → back.
            runTab.onClick.Invoke();
            if (overlay.ActiveTab != SettingsTab.Run) return "clicking 実行 did not switch back";
            if (!run.gameObject.activeSelf || app.gameObject.activeSelf) return "実行 click did not restore group visibility";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // SETTINGS-11 (#137 S3 / findings 0107 D5): each section sits in a themed CARD面
    // (elevated_surface_background, raised from the panel_background face) with a muted/uppercase header; all
    // chrome resolves through ThemeService roles (no inline color). RED litmus: paint a card with an inline
    // color, or remove the elevated card surface, → the role-equality asserts fail.
    static string Section11_CardSurfacesAndRoles()
    {
        var go = new GameObject("settings_cards_e2e");
        try
        {
            var overlay = go.AddComponent<SettingsModalOverlay>();
            overlay.Build(Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            var c = ThemeService.Current.colors;

            // panel face = panel_background role.
            var panel = FindByName(go, "SettingsPanel");
            if (panel == null) return "SettingsPanel missing";
            var panelImg = panel.GetComponent<Image>();
            if (panelImg == null || !SameColor(panelImg.color, c.panel_background))
                return "panel face is not the `panel_background` role";

            // every named section card is an elevated surface raised above the panel face.
            string[] cards = { "card:VENUE", "card:MODE", "card:SCENARIO", "card:DATA", "card:APPEARANCE" };
            int seen = 0;
            foreach (var name in cards)
            {
                var card = FindByName(go, name);
                if (card == null) return $"section card '{name}' missing";
                var img = card.GetComponent<Image>();
                if (img == null || !SameColor(img.color, c.elevated_surface_background))
                    return $"card '{name}' is not the `elevated_surface_background` role";
                seen++;
            }
            if (seen != cards.Length) return "not all section cards built";
            if (SameColor(c.elevated_surface_background, c.panel_background))
                return "card surface == panel face (cards would not be visually grouped)";

            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(go); }
    }

    // SETTINGS-13 (#137 review HIGH 1+2 / findings 0107 追補): the Settings chrome must re-theme in place on
    // a LIVE Dark↔Light switch — close button face/label, Venue row buttons (face + label), and the Mode
    // segment labels are all baked at build time and won't self-update; BackcastWorkspaceRoot.ApplyViewportTheme
    // is responsible for calling SettingsModalOverlay.ApplyTheme + SettingsVenueSectionView.ApplyTheme +
    // SettingsModeSegmentView.Refresh so they rebake. RED litmus: remove the venue/mode calls from
    // ApplyViewportTheme — or revert MakeButton so [x] Image/Text are not retained — and the rebased role
    // values won't match the new palette here.
    static string Section13_LiveReThemeWhileOpen()
    {
        // baseline: ensure Dark before build so the bake corresponds to a known palette.
        ThemeService.SetTheme(Theme.Dark());
        var go = new GameObject("settings_retheme_e2e");
        try
        {
            var overlay = go.AddComponent<SettingsModalOverlay>();
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            overlay.Build(font);

            // build the venue + mode section views into the overlay's containers (same wiring as the host).
            var conn = new VenueConnectionViewModel();
            var coord = new LiveLogoutCoordinator();
            var venueVm = new VenueMenuViewModel(conn, coord);
            var venueView = new SettingsVenueSectionView(venueVm, null, (v, e) => { }, () => { }, () => true, font);
            venueView.Build(overlay.VenueSection);

            var modeVm = new FooterModeViewModel();
            // #137 review round 3 (MED): seed VenueLive=true so Manual/Auto segments are visible — without this
            // ShowManualAutoSegments=false (default) → Manual/Auto seg は SetActive(false) → assert ループの
            // `if (!seg.gameObject.activeSelf) continue` で skip → modeChecked=1 (Replay のみ) で vacuity guard
            // を擦り抜け、Manual/Auto label rebake 回帰を検出できない（HIGH 1 RED litmus が Replay 1 個でしか
            // 効いていなかった）。venue_state=CONNECTED の poll を seed して 3 seg すべて assert する。
            modeVm.ApplyPoll("{\"execution_mode\":\"Replay\",\"venue_state\":\"CONNECTED\"}");
            var modeView = new SettingsModeSegmentView(modeVm, _ => { }, font);
            modeView.Build(overlay.ModeSection);

            // capture Dark roles for the comparison after the flip.
            var dark = Theme.Dark().colors;
            var light = Theme.Light().colors;

            // flip to Light and replay the host's ApplyViewportTheme contract (the exact order matters: the
            // chrome rebakes, then the section views rebake from the same role table — production parity).
            ThemeService.SetTheme(Theme.Light());
            overlay.ApplyTheme();
            venueView.ApplyTheme();
            modeView.Refresh();

            // [x] close button face + label (HIGH 1 — was previously baked once and never repainted).
            Button xBtn = null;
            foreach (var b in go.GetComponentsInChildren<Button>(true))
                if (b.gameObject.name == "btn_x") { xBtn = b; break; }
            if (xBtn == null) return "[x] close button missing";
            var xImg = xBtn.GetComponent<Image>();
            if (xImg == null || !SameColor(xImg.color, light.element_background))
                return "[x] face did not rebase to Light element_background on LIVE switch (HIGH 1 RED)";
            var xLbl = xBtn.GetComponentInChildren<Text>(true);
            if (xLbl == null || !SameColor(xLbl.color, light.text))
                return "[x] label did not rebase to Light text on LIVE switch (HIGH 1 RED)";

            // venue rows: face + label rebake (HIGH 1 — sections didn't have an ApplyTheme before).
            int venueChecked = 0;
            foreach (var b in overlay.VenueSection.GetComponentsInChildren<Button>(true))
            {
                if (!b.gameObject.name.StartsWith("venue:")) continue;
                var face = b.GetComponent<Image>();
                if (face == null || !SameColor(face.color, light.element_background))
                    return "venue row '" + b.gameObject.name + "' face did not rebase to Light element_background (HIGH 1 RED)";
                var lbl = b.GetComponentInChildren<Text>(true);
                if (lbl == null || !SameColor(lbl.color, light.text))
                    return "venue row '" + b.gameObject.name + "' label did not rebase to Light text (HIGH 1 RED)";
                venueChecked++;
            }
            if (venueChecked == 0) return "no venue rows to verify rebake (vacuous)";

            // mode segment labels: Refresh must rebake the label RGB from the current theme's `text` role,
            // not just adjust alpha (HIGH 1 — old Refresh kept the baked dark text on a Light flip).
            int modeChecked = 0;
            foreach (var seg in overlay.ModeSection.GetComponentsInChildren<Button>(true))
            {
                if (!seg.gameObject.name.StartsWith("seg:")) continue;
                if (!seg.gameObject.activeSelf) continue;
                var lbl = seg.GetComponentInChildren<Text>(true);
                if (lbl == null) continue;
                // alpha is locked-state dependent; only compare RGB (the role-rebake contract).
                var got = lbl.color; var want = light.text;
                if (Mathf.Abs(got.r - want.r) > 0.003f || Mathf.Abs(got.g - want.g) > 0.003f || Mathf.Abs(got.b - want.b) > 0.003f)
                    return "mode segment '" + seg.gameObject.name + "' label RGB did not rebase to Light text (HIGH 1 RED)";
                modeChecked++;
            }
            // #137 review round 3 (MED): VenueLive=true seed で 3 seg (Replay/Manual/Auto) すべて active になる
            // はず — 3 未満なら Manual/Auto seg が rebake 対象から漏れている（HIGH 1 の RED litmus が部分的に
            // しか効いていなかった旧状態への退行）。
            if (modeChecked < 3) return "expected 3 active mode segments (Replay/Manual/Auto with VenueLive=true), saw " + modeChecked;

            // non-vacuity: the Light/Dark palettes must actually differ on the roles we asserted, otherwise
            // the rebake would trivially pass even with the production calls removed.
            if (SameColor(dark.element_background, light.element_background) && SameColor(dark.text, light.text))
                return "Dark/Light palettes identical on element_background+text (test would be vacuous)";

            // also sanity-check the panel/card chrome rebased (covers SettingsModalOverlay.ApplyTheme too).
            var panel = FindByName(go, "SettingsPanel");
            var panelImg = panel != null ? panel.GetComponent<Image>() : null;
            if (panelImg == null || !SameColor(panelImg.color, light.panel_background))
                return "panel face did not rebase to Light panel_background";

            return null;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(go);
            // restore Dark so downstream sections (and other runners) start from the canonical baseline.
            ThemeService.SetTheme(Theme.Dark());
        }
    }

    static bool IsDescendant(Transform child, Transform ancestor)
    {
        for (var t = child; t != null; t = t.parent) if (t == ancestor) return true;
        return false;
    }

    static GameObject FindByName(GameObject root, string name)
    {
        foreach (var rt in root.GetComponentsInChildren<RectTransform>(true))
            if (rt.gameObject.name == name) return rt.gameObject;
        return null;
    }
}
