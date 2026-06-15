// BackcastWorkspaceRoot.cs — issue #59 "本線ワークスペース合体 scene" (production composition root)
//
// The scene-authored Backcast workspace root (CONTEXT "Backcast workspace root"; ADR-0009): the
// SINGLE normal Play entry that composes every UI surface — menu bar / sidebar / center infinite-
// canvas workspace (Hakoniwa [startup, chart] + a floating Strategy Editor) / footer — into one
// screen, and is the single Python owner. Orchestration is NOT here: the durable ReplayEngineHost
// owns the engine lifecycle / launcher / poll / transport (findings 0025 §5); this root WIRES the
// authored Views to the Host and the layout store, and drives ChartView / the footer from the
// Host's published state. Demotes the throwaway ScenarioStartupHitlHarness (its AutoBootstrap is
// off; the engine path is extracted here).
//
// SINGLE PLAY-OWNER (findings 0025 §7): the root claims Python only when it is the configured owner,
// not headless, and nobody else holds the interpreter (WorkspaceOwnership.ShouldClaim). To run a
// per-part Python HITL in isolation, DISABLE this root's GameObject before Play; while the root runs,
// per-part HITLs see PythonEngine.IsInitialized and refuse (they never fight over the engine).
//
// _ownPlay gates ONLY the Python auto-start, never the UI authoring/build (owner 2026-06-15).
// isBatchMode suppresses Python init (the headless compile gate never inits Python or renders).
//
// LAYOUT (findings 0025 §8, ADR-0003): 4 dimensions (Hakoniwa panels / canvas pan+zoom / floating
// windows / Strategy Editor open file) round-trip through LayoutPathResolver.DefaultPath(). Restore
// order canvas→Hakoniwa→floating→editor; the scene-authored editor window is ADOPTED (never
// destroyed+respawned) and only ADDITIONAL saved windows are spawned. Save triggers: File→Save,
// OnApplicationQuit, OnDestroy (Editor Play-stop) — converged into one idempotent teardown.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using Python.Runtime;

public sealed class BackcastWorkspaceRoot : MonoBehaviour
{
    const string WINDOW_ID = "strategy_editor:region_001";

    // ── owner toggle: gates Python auto-start ONLY (UI build always runs) ──
    [SerializeField] bool _ownPlay = true;

    // ── scene-authored references (assigned by BackcastWorkspaceSceneBuilder) ──
    [Header("Frame containers")]
    [SerializeField] RectTransform _centerWorkspace;
    [SerializeField] RectTransform _footerContainer;
    [SerializeField] MenuBarView _menuBarView;
    [SerializeField] UniverseSidebarView _sidebarView;

    [Header("Center workspace (infinite canvas)")]
    [SerializeField] RectTransform _viewport;
    [SerializeField] RectTransform _content;
    [SerializeField] InfiniteCanvasInputSurface _inputSurface;
    [SerializeField] RectTransform _hakoniwaRoot;
    [SerializeField] RectTransform _startupTile;
    [SerializeField] RectTransform _chartTile;
    [SerializeField] RectTransform _floatingLayer;

    [Header("Strategy Editor floating window (scene-authored, adopted)")]
    [SerializeField] RectTransform _strategyEditorWindow;
    [SerializeField] RectTransform _strategyEditorBody;
    [SerializeField] FloatingWindowTitleInput _strategyEditorTitleInput;

    // ── durable orchestration (extracted) ──
    readonly ReplayEngineHost _host = new ReplayEngineHost();

    // ── reused brains / VMs (findings 0025 §5) ──
    readonly ScenarioStartupController _scenario = new ScenarioStartupController();
    readonly ReplayLifecycle _lifecycle = new ReplayLifecycle();
    readonly StrategyProviderRegistry _registry = new StrategyProviderRegistry();
    ReplayTransportViewModel _transport;

    // ── built widgets ──
    InfiniteCanvasController _canvas;
    FloatingWindowController _windows;
    HakoniwaController _hako;
    ChartView _chartView;
    ScenarioStartupTile _tile;
    ReplayFooterView _footer;
    MenuBarViewModel _menuBar;
    VenueMenuViewModel _venueMenu;
    UniverseSidebarController _sidebarCtrl;
    readonly Dictionary<string, StrategyEditorView> _editors = new Dictionary<string, StrategyEditorView>();
    Font _font;

    // ── runtime state ──
    bool _isOwner;       // owns the Python interpreter this Play
    bool _built;         // BuildWorkspace ran (this root is the active layout owner) — independent of Python
    readonly OnceGate _teardownGate = new OnceGate();
    string _strategyFile;
    string _execMode = "Replay";
    string _lastPayload;
    int _renderedCount;
    bool _errLogged, _finishedHandled;
    ReplayPhase? _lastFooterPhase;

    [Serializable] struct _StateLite { public string replay_state; }

    void Awake()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        ResolvePaths();
        BuildWorkspace();        // UI build ALWAYS runs (independent of _ownPlay / batchmode)
        _built = true;           // this root is the active layout owner regardless of Python ownership

        // single Play-owner: claim Python when configured owner + not headless + (free interpreter OR
        // our own host already bootstrapped it — so a re-Play without domain reload reclaims it).
        _isOwner = WorkspaceOwnership.ShouldClaim(_ownPlay, Application.isBatchMode, PythonEngine.IsInitialized, _host.PythonInitialized);
        if (_isOwner)
        {
            try { _host.InitializePython(); }
            catch (Exception e) { Debug.LogError("[BackcastWorkspaceRoot] Python init failed: " + e); _isOwner = false; }
        }
        else
        {
            Debug.Log("[BackcastWorkspaceRoot] not Python owner (ownPlay=" + _ownPlay +
                      ", batch=" + Application.isBatchMode + ", alreadyInit=" + PythonEngine.IsInitialized + ").");
        }

        RestoreLayout();         // restore the persisted 4 dimensions (Default on missing/corrupt)
    }

    void ResolvePaths()
    {
        _strategyFile = EnvConfig.Get("BACKCAST_HITL_STRATEGY",
            Path.Combine(PythonRuntimeLocator.ProjectRoot, "spike", "fixtures", "strategies", "kernel_spike_buy_sell.py"));
        _scenario.Populate(_strategyFile, DateTime.Now);
    }

    // ---- compose the authored Views into live widgets (existing builders fill inner elements) ----
    void BuildWorkspace()
    {
        // center workspace: infinite canvas (Content) hosts HakoniwaRoot + FloatingWindowLayer (P-all).
        _canvas = new InfiniteCanvasController(_content);
        if (_inputSurface != null) _inputSurface.Initialize(_canvas, _viewport);

        _windows = new FloatingWindowController(_floatingLayer, FloatingWindowCatalog.Default(), BuildEditorWindowFrame);

        // Hakoniwa [startup slot0, chart slot1] on Content (CONTEXT: Startup = PanelKind::Startup slot 0).
        // Each tile gets a panel bg + a header bar so it reads as a DISTINCT box against the infinite-
        // space field (else tile bg ≈ field and the grid is invisible), and HakoniwaTileHeaderInput
        // makes header-drag SWAP while body-drag falls through to pan. Mirrors HakoniwaHitlHarness's
        // durable construction (HakoniwaController only lays cells; the caller owns tile chrome).
        EnsureRootImage(_hakoniwaRoot, HAKO_ROOT_COLOR);

        RectTransform startupBody = BuildTileChrome(_startupTile, "startup", out HakoniwaTileHeaderInput startupHeader);
        _tile = new ScenarioStartupTile(_scenario, OnRun, _font);
        _tile.Build(startupBody);
        _tile.SyncFieldsFromController();

        RectTransform chartBody = BuildTileChrome(_chartTile, "chart", out HakoniwaTileHeaderInput chartHeader);
        _chartView = chartBody.gameObject.AddComponent<ChartView>();
        _chartView.Build(chartBody, showTitleBar: false);

        _hako = new HakoniwaController(_hakoniwaRoot,
            new Dictionary<string, RectTransform> { { "startup", _startupTile }, { "chart", _chartTile } },
            new[] { "startup", "chart" });
        startupHeader.Initialize(_hako, _hakoniwaRoot, "startup");
        chartHeader.Initialize(_hako, _hakoniwaRoot, "chart");

        // adopt the scene-authored Strategy Editor window (NEVER destroyed+respawned, findings 0025 §8).
        _windows.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, WINDOW_ID, _strategyEditorWindow);
        if (_strategyEditorTitleInput != null)
            _strategyEditorTitleInput.Initialize(_windows, _canvas, _viewport, WINDOW_ID);
        var editorView = StrategyEditorContentBuilder.Build(_strategyEditorBody, WINDOW_ID, _registry, font: _font);
        if (editorView != null) _editors[WINDOW_ID] = editorView;

        // footer transport (the durable parity surface).
        _transport = new ReplayTransportViewModel(_lifecycle);
        _footer = new ReplayFooterView(_transport, OnFooterPlayPause, OnFooterStep, OnFooterStop, OnFooterSpeed, _font);
        _footer.Build(_footerContainer);

        // menu bar (V-host): File = Layout New/Open/Save.
        _venueMenu = new VenueMenuViewModel(new VenueConnectionViewModel(), new LiveLogoutCoordinator());
        _menuBar = new MenuBarViewModel(_venueMenu, new VenueConnectionViewModel(),
            currentMode: () => _execMode,
            isLiveAutoRunning: () => false,
            isReplayRunning: () => _host.IsRunning);
        if (_menuBarView != null) _menuBarView.Bind(_menuBar, OnFileNew, OnFileOpen, OnFileSave);

        // sidebar (V-host): reuse the durable controller brain; mock candidate source for now.
        var provider = new MockAvailableInstrumentsProvider(new[] { "1301.TSE", "6758.TSE", "7203.TSE", "8918.TSE", "9432.TSE", "9984.TSE" });
        _sidebarCtrl = new UniverseSidebarController(new InstrumentRegistry(), new SelectedSymbol(), new UniverseWriteback(), provider);
        if (_sidebarView != null) _sidebarView.Bind(_sidebarCtrl, new BoundStrategyFileProvider(_strategyFile));
    }

    // window factory for ADDITIONAL saved editor windows (the scene-authored one is adopted). Uses
    // the SAME frame builder as the scene-authoring tool so adopted/spawned editors can't diverge.
    RectTransform BuildEditorWindowFrame(FloatingWindowSpec spec, string id)
    {
        var root = StrategyEditorWindowFrame.Build(id, out var titleInput, out var body);
        titleInput.Initialize(_windows, _canvas, _viewport, id);
        if (spec.kind == FloatingWindowCatalog.KIND_STRATEGY_EDITOR)
        {
            var view = StrategyEditorContentBuilder.Build(body, id, _registry, font: _font);
            if (view != null) _editors[id] = view;
        }
        return root;
    }

    // ---- Hakoniwa tile chrome (panel bg + header swap handle), so tiles read against the field ----
    static readonly Color HAKO_ROOT_COLOR = new Color(0.12f, 0.12f, 0.15f, 1f);
    static readonly Color HAKO_TILE_COLOR = new Color(0.16f, 0.18f, 0.22f, 1f);
    static readonly Color HAKO_HEADER_COLOR = new Color(0.27f, 0.30f, 0.38f, 1f);
    static readonly Color HAKO_LABEL_COLOR = new Color(0.92f, 0.92f, 0.94f, 1f);
    const float HAKO_HEADER_H = 26f;

    static void EnsureRootImage(RectTransform root, Color color)
    {
        var img = root.GetComponent<Image>();
        if (img == null) img = root.gameObject.AddComponent<Image>();
        img.color = color;
        img.raycastTarget = false;   // gaps between tiles fall through to canvas pan
    }

    // tile = body Image (NOT a raycast target, so body-drag pans) + a header bar (raycast target +
    // HakoniwaTileHeaderInput, so header-drag SWAPS) + a content Body inset below the header.
    RectTransform BuildTileChrome(RectTransform tile, string id, out HakoniwaTileHeaderInput header)
    {
        var tileImg = tile.GetComponent<Image>();
        if (tileImg == null) tileImg = tile.gameObject.AddComponent<Image>();
        tileImg.color = HAKO_TILE_COLOR;
        tileImg.raycastTarget = false;

        var headerGo = new GameObject("Header", typeof(RectTransform), typeof(Image), typeof(HakoniwaTileHeaderInput));
        var hRt = (RectTransform)headerGo.transform;
        hRt.SetParent(tile, false);
        hRt.anchorMin = new Vector2(0f, 1f); hRt.anchorMax = new Vector2(1f, 1f); hRt.pivot = new Vector2(0.5f, 1f);
        hRt.offsetMin = new Vector2(2f, -HAKO_HEADER_H); hRt.offsetMax = new Vector2(-2f, -2f);
        headerGo.GetComponent<Image>().color = HAKO_HEADER_COLOR;   // opaque -> raycast target for the drag
        header = headerGo.GetComponent<HakoniwaTileHeaderInput>();

        var labelGo = new GameObject("Label", typeof(RectTransform), typeof(Text));
        var lRt = (RectTransform)labelGo.transform;
        lRt.SetParent(hRt, false);
        lRt.anchorMin = Vector2.zero; lRt.anchorMax = Vector2.one;
        lRt.offsetMin = new Vector2(8f, 0f); lRt.offsetMax = new Vector2(-8f, 0f);
        var t = labelGo.GetComponent<Text>();
        t.font = _font; t.fontSize = 12; t.color = HAKO_LABEL_COLOR; t.alignment = TextAnchor.MiddleLeft;
        t.text = id; t.raycastTarget = false;

        var bodyGo = new GameObject("Body", typeof(RectTransform));
        var body = (RectTransform)bodyGo.transform;
        body.SetParent(tile, false);
        body.anchorMin = Vector2.zero; body.anchorMax = Vector2.one;
        body.offsetMin = new Vector2(2f, 2f); body.offsetMax = new Vector2(-2f, -HAKO_HEADER_H - 2f);
        return body;
    }

    // ---- run path (footer ▶ / Startup tile Run): controller validates + writes the sidecar, then
    // the Host drives load_replay_data → start_engine (findings 0025 §4/§5). ----
    void OnRun()
    {
        if (_host.IsRunning) return;

        RunGateResult gate;
        try { gate = _scenario.TryStartRun(new BoundStrategyFileProvider(_strategyFile)); }
        catch (Exception e) { _tile.ShowRunMessage("Could not save scenario: " + e.Message); Debug.LogError("[BackcastWorkspaceRoot] commit failed: " + e); return; }
        if (!gate.IsReady) { _tile.ShowRunMessage(gate.Message); Debug.LogWarning("[BackcastWorkspaceRoot] run blocked: " + gate.Message); return; }
        _tile.ShowRunMessage(null);

        if (!_isOwner) { _tile.ShowRunMessage("Not the Python owner — cannot run."); return; }

        _renderedCount = 0;
        _lastPayload = null;
        _errLogged = false;
        _finishedHandled = false;
        _transport?.OnRunStarted();

        var req = new ReplayEngineHost.RunRequest
        {
            Instruments = new List<string>(_scenario.Universe.Ids).ToArray(),
            Start = _scenario.Params.Start,
            End = _scenario.Params.End,
            Granularity = ScenarioStartupParams.GranularityToString(_scenario.Params.Granularity),
            StrategyPath = gate.StrategyPath,
        };
        _host.TryStartRun(req);
    }

    void Update()
    {
        if (!_isOwner) return;

        string err = _host.StartError;
        if (err != null && !_errLogged) { Debug.LogError("[BackcastWorkspaceRoot] FAIL: " + err); _errLogged = true; }

        if (_host.RunFinished && !_finishedHandled)
        {
            _finishedHandled = true;
            // Re-read AFTER observing RunFinished: the launcher writes _startError BEFORE _runFinished,
            // so a value sampled before RunFinished can be a stale null and mis-latch a FAILED run as Done.
            string termErr = _host.StartError;
            if (termErr != null) _lifecycle.MarkFailed(termErr); else _lifecycle.MarkDone();
        }

        string state = _host.LatestStateJson;
        if (state != null && state != _lastPayload)
        {
            _lastPayload = state;
            try { _lifecycle.ApplyPoll(JsonUtility.FromJson<_StateLite>(state).replay_state); }
            catch { /* malformed poll snapshot: keep the last phase */ }

            ReplayBarFrame frame = ReplayBarDecoder.Decode(state);
            if (frame.Ohlc != null && frame.Ohlc.Count != _renderedCount)
            {
                _chartView.Render(frame);
                _renderedCount = frame.Ohlc.Count;
            }
        }

        if (_footer != null)
        {
            ReplayPhase phase = _lifecycle.Phase;
            if (_lastFooterPhase != phase) { _lastFooterPhase = phase; _footer.Refresh(); }
        }
    }

    // ---- footer click handlers → Host transport ----
    void OnFooterPlayPause()
    {
        switch (_transport.PlayPauseIntent())
        {
            case ReplayTransportIntent.Run: OnRun(); break;
            case ReplayTransportIntent.Pause: _host.Pause(); break;
            case ReplayTransportIntent.Resume: _host.Resume(); break;
        }
    }

    void OnFooterStep() => _host.Step();
    void OnFooterStop() => _host.ForceStop();
    void OnFooterSpeed(int mult) { if (_transport.SelectSpeed(mult)) _host.SetSpeed(mult); }

    // ---- File = Layout (findings 0025 §9) ----
    void OnFileNew()
    {
        var decision = _menuBar.FileNew(out _, out string refuse);
        if (decision == FileNewDecision.RefusedRunning) { _menuBarView?.ShowMessage(refuse); return; }
        // workspace clear: scenario buffer + universe (the in-memory clear the host owns).
        _scenario.Clear();
        _tile?.SyncFieldsFromController();
        _menuBarView?.ShowMessage("New: workspace cleared.");
    }

    void OnFileOpen()
    {
        _ = _menuBar.FileOpenModeSideEffect();   // Live-mode side-effect (no-op in Replay mainline; #39/#42)
        RestoreLayout();
        _menuBarView?.ShowMessage("Open: layout restored.");
    }

    void OnFileSave()
    {
        SaveLayout();
        _menuBarView?.ShowMessage("Save: layout written.");
    }

    // ---- layout persistence (4 dimensions) ----
    LayoutDocument CaptureLayout()
    {
        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = _hako.Capture().panels,
            canvasView = _canvas.CaptureView(),
            floatingWindows = _windows.Capture().floatingWindows,
            strategyEditors = new List<StrategyEditorState>(),
        };
        foreach (var kv in _editors)
        {
            if (kv.Value == null) continue;
            var s = kv.Value.CaptureState();
            if (s != null) doc.strategyEditors.Add(s);
        }
        return doc;
    }

    void SaveLayout()
    {
        try { LayoutStore.Save(CaptureLayout(), LayoutPathResolver.DefaultPath()); }
        catch (Exception e) { Debug.LogWarning("[BackcastWorkspaceRoot] layout save failed: " + e.Message); }
    }

    void RestoreLayout()
    {
        LayoutDocument doc;
        try { doc = LayoutStore.Load(LayoutPathResolver.DefaultPath()); }
        catch (Exception e) { Debug.LogWarning("[BackcastWorkspaceRoot] layout load failed; using default: " + e.Message); doc = LayoutDocument.Default(); }
        ApplyLayout(doc);
    }

    // restore order canvas → Hakoniwa → floating → Strategy Editor (findings 0025 §8).
    void ApplyLayout(LayoutDocument doc)
    {
        if (doc == null) return;
        if (doc.canvasView != null) _canvas.ApplyView(doc.canvasView);
        _hako.Apply(doc);
        RestoreFloating(doc);
        RestoreEditors(doc);
    }

    // floating: adopted/existing windows repositioned IN PLACE (never destroyed); only additional
    // saved windows are spawned; ascending zOrder → BringToFront yields contiguous front order.
    void RestoreFloating(LayoutDocument doc)
    {
        var wins = doc.floatingWindows;
        if (wins == null) return;
        var sorted = new List<FloatingWindowLayout>(wins);
        sorted.Sort((a, b) => (a?.zOrder ?? 0).CompareTo(b?.zOrder ?? 0));
        foreach (var w in sorted)
        {
            if (w == null || string.IsNullOrEmpty(w.id)) continue;
            if (_windows.Has(w.id)) _windows.ApplyGeometry(w);
            else _windows.Spawn(w.kind, w.id, w.x, w.y, w.w, w.h, w.visible);
            _windows.BringToFront(w.id);
        }
    }

    void RestoreEditors(LayoutDocument doc)
    {
        var states = doc.strategyEditors;
        if (states == null) return;
        foreach (var s in states)
        {
            if (s == null || string.IsNullOrEmpty(s.id)) continue;
            if (_editors.TryGetValue(s.id, out var view) && view != null) view.RestoreFrom(s);
        }
    }

    // ---- idempotent teardown (findings 0025 §10). OnApplicationQuit + OnDestroy converge here. ----
    void StopAndDispose()
    {
        if (!_teardownGate.TryEnter()) return;     // 1. double-run guard (OnApplicationQuit + OnDestroy converge)
        // 2. save layout once while THIS root is the active layout owner. Gated on _built, NOT Python
        // ownership: a yielded root (GameObject disabled before Play) never ran Awake/BuildWorkspace so
        // it never reaches here, but a built-yet-non-Python-owner root must still persist its layout.
        if (_built) SaveLayout();
        _host.Stop();                             // 3-7. force_stop → poll stop → bounded join → no Shutdown
        Debug.Log("[BackcastWorkspaceRoot] teardown complete.");
    }

    void OnApplicationQuit() => StopAndDispose();
    void OnDestroy() => StopAndDispose();
}
