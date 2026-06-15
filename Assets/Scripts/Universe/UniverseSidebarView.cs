// UniverseSidebarView.cs — issue #59 "Backcast workspace root" (V-host sidebar, findings 0025 §6)
//
// The scene-authored production HOST for the instrument-picker / universe sidebar. #31 is closed,
// so the sidebar's production View lands HERE (owner 2026-06-15): it REUSES the durable
// UniverseSidebarController brain and renders the screen-fixed left sidebar via OnGUI, clipped to
// its scene-authored container RectTransform (draw region DERIVED, never hardcoded — unlike the
// throwaway UniverseSidebarHitlHarness which hardcoded SIDEBAR_WIDTH/Screen.height). V-host is the
// interim production host; uGUI-ification and OnGUI removal are follow-up.
//
// Python-FREE: the controller drives SelectedSymbol (the depth-target focus) and the universe
// writeback; the candidate source is injected by the root (a separate issue owns the real
// DuckDB/venue universe — findings 0024).

using UnityEngine;

[RequireComponent(typeof(RectTransform))]
public sealed class UniverseSidebarView : MonoBehaviour
{
    RectTransform _container;
    UniverseSidebarController _ctrl;
    IStrategyFileProvider _strategyProvider;
    UniverseSourceMode _mode = UniverseSourceMode.Replay;
    string _replayEnd = "2024-12-31";

    public void Bind(UniverseSidebarController ctrl, IStrategyFileProvider strategyProvider, string replayEnd = null)
    {
        _ctrl = ctrl;
        _strategyProvider = strategyProvider;
        if (!string.IsNullOrEmpty(replayEnd)) _replayEnd = replayEnd;
        _container = GetComponent<RectTransform>();
    }

    void OnGUI()
    {
        if (_ctrl == null) return;
        if (_container == null) _container = GetComponent<RectTransform>();

        Rect rect = GuiRectUtil.GuiScreenRect(_container);
        if (rect.width <= 0f || rect.height <= 0f) return;

        Theme t = ThemeService.Current;
        var title = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = false, normal = { textColor = t.status.info } };
        var label = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false, normal = { textColor = t.colors.text_accent } };
        var muted = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = false, normal = { textColor = t.colors.text_muted } };

        GUI.Box(rect, GUIContent.none);
        GUILayout.BeginArea(new Rect(rect.x + 6, rect.y + 6, rect.width - 12, rect.height - 12));

        GUILayout.Label("Instruments", title);

        foreach (SidebarRow r in _ctrl.Rows())
        {
            GUILayout.BeginHorizontal();
            string mark = r.Selected ? "▶ " : "   ";
            if (GUILayout.Button(mark + r.Id, label, GUILayout.ExpandWidth(true)))
                _ctrl.SelectRow(r.Id, _mode);
            if (GUILayout.Button("×", label, GUILayout.Width(24)))
                _ctrl.Remove(r.Id, _mode, _strategyProvider);
            GUILayout.EndHorizontal();
        }
        if (_ctrl.Registry.Count == 0) GUILayout.Label("No instruments", muted);

        if (GUILayout.Button(_ctrl.Picker.Visible ? "− Close" : "+ Add", label))
            _ctrl.TogglePicker(_mode, _replayEnd);

        if (_ctrl.Picker.Visible)
        {
            GUILayout.Space(2);
            GUILayout.Label("search:", muted);
            string q = GUILayout.TextField(_ctrl.Picker.Query ?? "");
            _ctrl.Picker.SetQuery(q);

            foreach (PickerRow pr in _ctrl.PickerList(_mode))
            {
                if (pr.IsPlaceholder) { GUILayout.Label("  " + pr.Label, muted); continue; }
                string lbl = (pr.AlreadyAdded ? "✓ " : "+ ") + pr.Label;
                if (GUILayout.Button(lbl, label))
                {
                    long nowMs = (long)(Time.realtimeSinceStartup * 1000f);
                    _ctrl.AddFromPicker(pr.Id, _mode, _strategyProvider, nowMs);
                }
            }
        }

        GUILayout.FlexibleSpace();
        GUILayout.Label("focus → depth: " + (_ctrl.Selected.HasValue ? _ctrl.Selected.Value : "(none)"), muted);

        GUILayout.EndArea();
    }
}
