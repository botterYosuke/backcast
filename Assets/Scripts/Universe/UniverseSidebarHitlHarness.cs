// UniverseSidebarHitlHarness.cs — issue #31 "instrument picker / universe sidebar" (HITL view)
//
// Owner-only, DEFAULT-DISABLED IMGUI render leg complementing the headless AFK gate
// (UniverseSidebarProbe). The probe value-asserts the durable brain (controllers) under
// -batchmode; this harness lets the owner SEE + DRIVE the screen-fixed left sidebar:
// click a row to focus (SelectedSymbol → depth target), × to remove, [+ Add] to open the
// picker, type to filter, click a candidate to add. It runs the SAME mock provider the probe
// uses (Python-FREE) and persists the universe to a TEMP sidecar so the owner can watch the
// writeback line update (findings 0024 D1/D2/D4).
//
// PLAY OWNERSHIP: spawned ONLY via Tools > Backcast > Universe Sidebar HITL (no auto-bootstrap),
// so it never collides with the single Play owner (mirrors DepthLadderHitlHarness). IMGUI-only
// (screen-fixed chrome): the durable view tech is HITL-only and reversible (findings 0024 §1).

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

public class UniverseSidebarHitlHarness : MonoBehaviour
{
    const float SIDEBAR_WIDTH = 200f;
    const float MENU_BAR_HEIGHT = 24f;
    const float FOOTER_HEIGHT = 28f;

    // Mock candidate universe (the real DuckDB/venue source is a separate issue).
    static readonly string[] MockAvailable =
    {
        "1301.TSE", "6758.TSE", "7203.TSE", "8918.TSE", "9432.TSE", "9984.TSE",
    };

    readonly InstrumentRegistry _registry = new InstrumentRegistry();
    readonly SelectedSymbol _selected = new SelectedSymbol();
    readonly UniverseWriteback _writeback = new UniverseWriteback();
    MockAvailableInstrumentsProvider _provider;
    UniverseSidebarController _ctrl;
    TempStrategyProvider _strategyProvider;

    UniverseSourceMode _mode = UniverseSourceMode.Replay;
    string _replayEnd = "2024-12-31";
    string _tempSidecar;
    string _lastWriteLine = "(no write yet)";

    // A trivial in-place strategy provider so the writeback has a real path to persist to.
    sealed class TempStrategyProvider : IStrategyFileProvider
    {
        public string Path;
        public bool TryGetStrategyFile(out string path) { path = Path; return !string.IsNullOrEmpty(Path); }
    }

    void Start()
    {
        string dir = Path.Combine(Application.temporaryCachePath, "universe_sidebar_hitl");
        Directory.CreateDirectory(dir);
        string strategyPy = Path.Combine(dir, "hitl_strategy.py");
        _tempSidecar = ScenarioSidecarStore.SidecarPathFor(strategyPy);
        _strategyProvider = new TempStrategyProvider { Path = strategyPy };

        _provider = new MockAvailableInstrumentsProvider(MockAvailable);
        _ctrl = new UniverseSidebarController(_registry, _selected, _writeback, _provider);

        // Seed a couple so the row list isn't empty on first frame, then prime the writeback
        // so the seed isn't counted as an unsaved edit.
        _registry.ReplaceAll(new[] { "7203.TSE", "9984.TSE" });
        _selected.Set("7203.TSE");
        _ctrl.PrimeWritebackFromCurrent();
    }

    void OnGUI()
    {
        Theme t = ThemeService.Current;
        var title = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = false, normal = { textColor = t.status.info } };
        var label = new GUIStyle(GUI.skin.label) { fontSize = 12, richText = false, normal = { textColor = t.colors.text_accent } };
        var muted = new GUIStyle(GUI.skin.label) { fontSize = 11, richText = false, normal = { textColor = t.colors.text_muted } };

        float screenH = Screen.height;
        var rect = new Rect(0, MENU_BAR_HEIGHT, SIDEBAR_WIDTH, screenH - MENU_BAR_HEIGHT - FOOTER_HEIGHT);
        GUI.Box(rect, GUIContent.none);

        GUILayout.BeginArea(new Rect(rect.x + 6, rect.y + 6, rect.width - 12, rect.height - 12));

        GUILayout.Label("Instruments", title);

        // ── universe rows (click=focus, ×=remove) ──
        foreach (SidebarRow r in _ctrl.Rows())
        {
            GUILayout.BeginHorizontal();
            string mark = r.Selected ? "▶ " : "   ";
            if (GUILayout.Button(mark + r.Id, label, GUILayout.ExpandWidth(true)))
                _ctrl.SelectRow(r.Id, _mode);
            if (GUILayout.Button("×", label, GUILayout.Width(24)))
            {
                _ctrl.Remove(r.Id, _mode, _strategyProvider);
                RecordWrite();
            }
            GUILayout.EndHorizontal();
        }
        if (_registry.Count == 0) GUILayout.Label("No instruments", muted);

        // ── [+ Add] picker toggle ──
        if (GUILayout.Button(_ctrl.Picker.Visible ? "− Close" : "+ Add", label))
            _ctrl.TogglePicker(_mode, _replayEnd);

        // ── picker dropdown ──
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
                    RecordWrite();
                }
            }
        }

        GUILayout.FlexibleSpace();

        // ── status footer (focus + writeback) ──
        GUILayout.Label("mode: " + _mode, muted);
        if (GUILayout.Button("toggle mode (Replay/Live)", muted))
            _mode = _mode == UniverseSourceMode.Replay ? UniverseSourceMode.Live : UniverseSourceMode.Replay;
        GUILayout.Label("focus → depth: " + (_selected.HasValue ? _selected.Value : "(none)"), muted);
        GUILayout.Label("writeback: " + _lastWriteLine, muted);
        if (!string.IsNullOrEmpty(_writeback.LastError)) GUILayout.Label("err: " + _writeback.LastError, muted);

        GUILayout.EndArea();
    }

    void RecordWrite()
    {
        try
        {
            if (File.Exists(_tempSidecar))
            {
                // ReadScenario takes the .py strategy path (it derives the sidecar internally).
                var snap = ScenarioSidecarStore.ReadScenario(_strategyProvider.Path);
                _lastWriteLine = snap != null
                    ? $"{snap.Instruments.Count} ids @ {Path.GetFileName(_tempSidecar)}"
                    : "(sidecar empty)";
            }
            else
            {
                _lastWriteLine = _mode == UniverseSourceMode.Replay ? "(skipped — see err)" : "(Live: no-op, gated)";
            }
        }
        catch (Exception e) { _lastWriteLine = "read-back error: " + e.Message; }
    }
}
