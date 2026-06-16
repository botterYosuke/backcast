// MenuBarView.cs — issue #59 "Backcast workspace root" (V-host menu bar, findings 0025 §6/§9)
//
// The scene-authored production HOST for the menu bar. It does NOT rewrite the menu bar to uGUI
// (that, plus the full File/Edit/Venue/Help dropdowns, is #42's open responsibility) — it KEEPS the
// existing MenuBarViewModel brain and renders a MINIMAL File menu via OnGUI, clipped to its
// scene-authored container RectTransform (the draw region is DERIVED, never hardcoded). File = Layout
// (ADR-0005 / findings 0017): New / Open / Save forward to the Backcast workspace root's layout I/O.
//
// V-host is the interim production host; uGUI-ification and OnGUI removal are #42's follow-up.

using System;
using UnityEngine;

[RequireComponent(typeof(RectTransform))]
public sealed class MenuBarView : MonoBehaviour
{
    RectTransform _container;
    MenuBarViewModel _vm;
    Action _onNew, _onOpen, _onSave;
    bool _fileOpen;
    string _message;

    // Root wires the existing brain + the layout I/O callbacks. The VM owns the File→New refuse-
    // when-running gate; the root performs the actual clear/save/restore (findings 0025 §9).
    public void Bind(MenuBarViewModel vm, Action onNew, Action onOpen, Action onSave)
    {
        _vm = vm;
        _onNew = onNew;
        _onOpen = onOpen;
        _onSave = onSave;
        _container = GetComponent<RectTransform>();
    }

    public void ShowMessage(string msg) => _message = msg;

    void OnGUI()
    {
        if (_vm == null) return;
        if (_container == null) _container = GetComponent<RectTransform>();

        Rect rect = GuiRectUtil.GuiScreenRect(_container);
        if (rect.width <= 0f || rect.height <= 0f) return;

        // draw the menu bar (and its File dropdown) ON TOP of the other OnGUI chrome (e.g. the
        // sidebar, which would otherwise occlude a dropdown that opens over the left column).
        int prevDepth = GUI.depth;
        GUI.depth = -100;

        GUI.Box(rect, GUIContent.none);
        GUILayout.BeginArea(rect);   // clip the BAR row to the authored container
        GUILayout.BeginHorizontal();
        bool fileClicked = GUILayout.Button(_fileOpen ? "File ▴" : "File ▾", GUILayout.Width(64));
        // Edit / Venue / Help are placeholders here — their contents are #42's responsibility.
        GUILayout.Label("Edit", GUILayout.Width(40));
        GUILayout.Label("Venue", GUILayout.Width(48));
        GUILayout.Label("Help", GUILayout.Width(40));
        GUILayout.FlexibleSpace();
        if (!string.IsNullOrEmpty(_message)) GUILayout.Label(_message);
        GUILayout.EndHorizontal();
        GUILayout.EndArea();

        if (fileClicked) _fileOpen = !_fileOpen;

        // the dropdown is drawn in a SEPARATE area BELOW the bar — drawing it inside the bar's
        // own (menu-height) BeginArea clipped it away (the bug HITL ④ hit).
        if (_fileOpen)
        {
            const float itemH = 24f;
            var dd = new Rect(rect.x, rect.yMax, 120f, itemH * 3f + 8f);
            GUI.Box(dd, GUIContent.none);
            GUILayout.BeginArea(dd);
            if (GUILayout.Button("New", GUILayout.Height(itemH - 2f))) { _fileOpen = false; _onNew?.Invoke(); }
            if (GUILayout.Button("Open", GUILayout.Height(itemH - 2f))) { _fileOpen = false; _onOpen?.Invoke(); }
            if (GUILayout.Button("Save", GUILayout.Height(itemH - 2f))) { _fileOpen = false; _onSave?.Invoke(); }
            GUILayout.EndArea();
        }

        GUI.depth = prevDepth;
    }
}
