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

        if (_fail.Count == 0)
            Debug.Log("[E2E SETTINGS DIALOG PASS] modal shell open/close (SETTINGS-01) / ESC guard: " +
                      "drag-revert (SETTINGS-02), secret+save-guard consume (SETTINGS-03), toggle (SETTINGS-04) / " +
                      "[x]+SetVisible (SETTINGS-05) / z-order menu<settings<secret (SETTINGS-06) / chrome+3 sections " +
                      "(SETTINGS-07) / venue section + menu Venue retired (SETTINGS-08) — ADR-0026, findings 0102");
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

            // disconnected (default poll state) → non-prod connects enabled (MOCK/demo/verify), Disconnect
            // disabled (CanDisconnect needs IsConnected). NOTE: prod variants may be greyed out by the
            // *_ALLOW_PROD env gate (prod grey-out), so we assert "≥1 connect enabled", not "all".
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
            if (connectEnabled < 1) return "disconnected: no connect button is enabled (gating wiring broken)";
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
}
