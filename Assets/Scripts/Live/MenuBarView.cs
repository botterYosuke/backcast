// MenuBarView.cs — issue #42 cutover slice 2 (production host for the global menu bar, findings 0027)
//
// The scene-authored production HOST for the menu bar. It KEEPS the existing MenuBarViewModel brain and
// renders the TTWR File/Edit/Venue/Help surface (ADR-0005 1:1 parity) via OnGUI, clipped to its scene-
// authored container RectTransform (the draw region is DERIVED, never hardcoded). The proven IMGUI was
// ported from ProductionLiveShell's DrawMenuBar/DrawVenueMenu (findings 0017 §8) — submenus draw AFTER
// the bar so other OnGUI chrome can't overpaint them (the #42 F1 lesson).
//
//   * File = Layout (New / Open / Save) — forwards to the workspace root's layout I/O.
//   * Venue = the reused VenueMenuViewModel (vm.Venue): 4 TTWR connect variants (prod grey-out) +
//     Disconnect. MOCK is NOT a parity variant — it surfaces only as a dev-only connect in the editor
//     (findings 0027 D2), used to reach the LiveAuto-on-mainline HITL.
//   * Edit / Help — present for structure parity; bodies deferred to #16 / the settings slice (stub).
//
// V-host renders OnGUI; uGUI-ification and OnGUI removal are a follow-up issue (findings 0027 §3).

using System;
using UnityEngine;

[RequireComponent(typeof(RectTransform))]
public sealed class MenuBarView : MonoBehaviour
{
    enum OpenMenu { None, File, Edit, Venue, Help }

    RectTransform _container;
    MenuBarViewModel _vm;
    Action _onNew, _onOpen, _onSave, _onDisconnect;
    Action<string, string> _onConnect;     // (venue, env)
    Func<bool> _connectReady;              // server ready && !teardown
    Func<string> _modeText;                // current execution-mode display for the bar badge
    bool _showMockConnect;                 // dev-only MOCK connect item (editor only); derived at Bind
    OpenMenu _open;
    string _message;

    // top-level button widths (fixed so the submenu drop x-offsets line up under each button).
    const float W_FILE = 56f, W_EDIT = 44f, W_VENUE = 52f, W_HELP = 44f, ITEM_H = 22f;

    // Root wires the existing brain + the layout I/O and venue callbacks. The VM owns the File→New
    // refuse-when-running gate and the venue logic (vm.Venue); the root performs the real clear/save/
    // restore and the venue login/logout RPCs (findings 0027 D3/D5).
    public void Bind(MenuBarViewModel vm,
                     Action onNew, Action onOpen, Action onSave,
                     Action<string, string> onConnect, Action onDisconnect,
                     Func<bool> connectReady, Func<string> modeText, string devVenue)
    {
        _vm = vm;
        _onNew = onNew;
        _onOpen = onOpen;
        _onSave = onSave;
        _onConnect = onConnect;
        _onDisconnect = onDisconnect;
        _connectReady = connectReady;
        _modeText = modeText;
        _showMockConnect = devVenue == "MOCK";   // MOCK is the only credential-less dev venue (findings 0027 D2)
        _container = GetComponent<RectTransform>();
    }

    public void ShowMessage(string msg) => _message = msg;

    void OnGUI()
    {
        if (_vm == null) return;
        if (_container == null) _container = GetComponent<RectTransform>();

        Rect rect = GuiRectUtil.GuiScreenRect(_container);
        if (rect.width <= 0f || rect.height <= 0f) return;

        // draw the bar (and its dropdowns) ON TOP of the other OnGUI chrome (e.g. the sidebar, which
        // would otherwise occlude a dropdown that opens over the left column).
        int prevDepth = GUI.depth;
        GUI.depth = -100;

        GUI.Box(rect, GUIContent.none);
        GUILayout.BeginArea(rect);   // clip the BAR row to the authored container
        GUILayout.BeginHorizontal();
        if (GUILayout.Button("File", GUILayout.Width(W_FILE))) Toggle(OpenMenu.File);
        if (GUILayout.Button("Edit", GUILayout.Width(W_EDIT))) Toggle(OpenMenu.Edit);
        if (GUILayout.Button("Venue", GUILayout.Width(W_VENUE))) Toggle(OpenMenu.Venue);
        if (GUILayout.Button("Help", GUILayout.Width(W_HELP))) Toggle(OpenMenu.Help);
        GUILayout.FlexibleSpace();
        GUILayout.Label($"{_vm.Venue.BadgeText}   mode: {ModeText()}");
        if (!string.IsNullOrEmpty(_message)) GUILayout.Label("   <color=orange>" + _message + "</color>");
        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        // dropdowns draw in SEPARATE areas BELOW the bar (drawing them inside the bar's menu-height
        // BeginArea clipped them away — the #42 F1 bug). x-offset lines up under the owning button.
        switch (_open)
        {
            case OpenMenu.File: DrawFileMenu(rect); break;
            case OpenMenu.Edit: DrawEditMenu(rect); break;
            case OpenMenu.Venue: DrawVenueMenu(rect); break;
            case OpenMenu.Help: DrawHelpMenu(rect); break;
        }

        GUI.depth = prevDepth;
    }

    void Toggle(OpenMenu m) => _open = _open == m ? OpenMenu.None : m;
    string ModeText() => _modeText != null ? _modeText() : "-";
    bool Ready() => _connectReady == null || _connectReady();

    static void DisabledItem(string label) { GUI.enabled = false; GUILayout.Button(label); GUI.enabled = true; }

    void DrawFileMenu(Rect bar)
    {
        var dd = new Rect(bar.x, bar.yMax, 150f, ITEM_H * 4f + 8f);
        GUI.Box(dd, GUIContent.none);
        GUILayout.BeginArea(dd);
        if (GUILayout.Button("New", GUILayout.Height(ITEM_H - 2f))) { _open = OpenMenu.None; _onNew?.Invoke(); }
        if (GUILayout.Button("Open  (layout)", GUILayout.Height(ITEM_H - 2f))) { _open = OpenMenu.None; _onOpen?.Invoke(); }
        if (GUILayout.Button("Save  (layout)", GUILayout.Height(ITEM_H - 2f))) { _open = OpenMenu.None; _onSave?.Invoke(); }
        DisabledItem("Save As…  (deferred)");   // follow-up: native file picker (findings 0027 §3)
        GUILayout.EndArea();
    }

    void DrawEditMenu(Rect bar)
    {
        // Undo/Redo route to the active strategy editor (#16); no active-editor concept wired here yet,
        // so they are disabled stubs (findings 0027 §3 follow-up).
        var dd = new Rect(bar.x + W_FILE, bar.yMax, 200f, ITEM_H * 2f + 8f);
        GUI.Box(dd, GUIContent.none);
        GUILayout.BeginArea(dd);
        DisabledItem("Undo  (no active editor)");
        DisabledItem("Redo  (no active editor)");
        GUILayout.EndArea();
    }

    void DrawVenueMenu(Rect bar)
    {
        bool ready = Ready();
        var venue = _vm.Venue;
        var dd = new Rect(bar.x + W_FILE + W_EDIT, bar.yMax, 260f, ITEM_H * 6f + 10f);
        GUI.Box(dd, GUIContent.none);
        GUILayout.BeginArea(dd);

        // dev-only MOCK connect (editor only): MOCK is a credential-less dev venue, NOT a TTWR parity
        // variant, surfaced so the LiveAuto-on-mainline HITL is reachable (findings 0027 D2).
        if (Application.isEditor && _showMockConnect)
        {
            GUI.enabled = ready && venue.CanConnect;
            if (GUILayout.Button("Connect MOCK (dev)")) { _open = OpenMenu.None; _onConnect?.Invoke("MOCK", ""); }
        }
        foreach (var v in VenueMenuViewModel.ConnectVariants)
        {
            // prod variants grey out unless *_ALLOW_PROD is set (mirrors the login dialog; Python is the
            // safety authority). all connect items disabled while connected / mid-auth.
            GUI.enabled = ready && venue.CanConnectEnv(v.Venue, v.Env);
            if (GUILayout.Button(v.Label)) { _open = OpenMenu.None; _onConnect?.Invoke(v.Venue, v.Env); }
        }
        GUI.enabled = ready && venue.CanDisconnect;
        if (GUILayout.Button("Disconnect")) { _open = OpenMenu.None; _onDisconnect?.Invoke(); }
        GUI.enabled = true;
        GUILayout.EndArea();
    }

    void DrawHelpMenu(Rect bar)
    {
        // ADR-0005 lists Settings as its own surface — item present, body deferred to that slice.
        var dd = new Rect(bar.x + W_FILE + W_EDIT + W_VENUE, bar.yMax, 220f, ITEM_H + 8f);
        GUI.Box(dd, GUIContent.none);
        GUILayout.BeginArea(dd);
        DisabledItem("Settings  (deferred slice)");
        GUILayout.EndArea();
    }
}
