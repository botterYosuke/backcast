// BackcastWorkspaceRoot.cs — issue #59 "本線ワークスペース合体 scene" (production composition root)
//
// The scene-authored Backcast workspace root (CONTEXT "Backcast workspace root"; ADR-0009): the
// SINGLE normal Play entry that composes every UI surface — menu bar / sidebar / center infinite-
// canvas workspace (Hakoniwa [startup, chart] + a floating Strategy Editor) / footer — into one
// screen, and is the single Python owner. Orchestration is NOT here: the durable WorkspaceEngineHost
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
    readonly WorkspaceEngineHost _host = new WorkspaceEngineHost();   // #39→#59 Step 2: generalized (Replay + Live seam)

    // ── reused brains / VMs (findings 0025 §5) ──
    readonly ScenarioStartupController _scenario = new ScenarioStartupController();
    readonly ReplayLifecycle _lifecycle = new ReplayLifecycle();
    readonly StrategyProviderRegistry _registry = new StrategyProviderRegistry();
    ReplayTransportViewModel _transport;

    // ── built widgets ──
    InfiniteCanvasController _canvas;
    FloatingWindowController _windows;
    HakoniwaController _hako;
    // #60 chart tile family: one chart tile + ChartView per universe instrument (id "chart:<id>"),
    // membership-synced from _scenario.Universe via InstrumentRegistry.Changed. _chartRendered dedups
    // the per-id render by series length.
    readonly Dictionary<string, RectTransform> _chartTiles = new Dictionary<string, RectTransform>();
    readonly Dictionary<string, ChartView> _chartViews = new Dictionary<string, ChartView>();
    readonly Dictionary<string, int> _chartRendered = new Dictionary<string, int>();
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
    string _lastPayload;
    bool _errLogged, _finishedHandled;

    // ── #39: footer mode segment + LiveAuto ▶, wired to the host live seam (Step 3/4) ──
    FooterModeViewModel _footerMode;
    LiveAutoTransportViewModel _footerAuto;
    readonly SelectedSymbol _footerSelected = new SelectedSymbol();
    string _venue = "MOCK";                 // live venue id (MOCK for AFK/HITL bring-up)
    volatile bool _footerModeRejected;      // worker→main: a SetExecutionMode / stop-then-switch failed
    volatile int _footerStartResult;        // worker→main: 0 none / 1 ok / 2 fail (register→start)
    volatile string _footerStartedRunId;    // worker→main: run_id from a successful start (guard release)
    volatile int _venueLoginResult;         // worker→main: 0 none / 1 ok / 2 fail (venue_login)
    volatile string _venueLoginError;       // worker→main: error_code on a failed venue login
    volatile bool _venueLogoutFailed;       // worker→main: venue_logout returned failure
    string _autoStatus = "-";
    string _lastFooterSig = "";
    string _lastFooterPoll = "";            // dedup the footer-mode ApplyPoll (avoid per-frame JSON parse)

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

        // #60 chart tile family: base = [startup] only; chart tiles are dynamic (chart:<id> per
        // universe instrument). The scene-authored single "chart" tile is retired (deactivated) — the
        // dynamic family replaces it. Membership is owned by _scenario.Universe (the shared SoT, #59
        // §12); InstrumentRegistry.Changed drives spawn/despawn, and THIS root (the membership
        // orchestrator) owns box-grow (findings 0027 §6 — Rebuild stays box-size-free).
        if (_chartTile != null) _chartTile.gameObject.SetActive(false);

        _hako = new HakoniwaController(_hakoniwaRoot,
            new Dictionary<string, RectTransform> { { "startup", _startupTile } },
            new[] { "startup" });
        startupHeader.Initialize(_hako, _hakoniwaRoot, "startup");

        SyncChartTilesToUniverse();                          // spawn chart:<id> for the current universe + box-grow
        _scenario.Universe.Changed += SyncChartTilesToUniverse;   // keep chart tiles == universe (Dispose unsubs)

        // adopt the scene-authored Strategy Editor window (NEVER destroyed+respawned, findings 0025 §8).
        _windows.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, WINDOW_ID, _strategyEditorWindow);
        if (_strategyEditorTitleInput != null)
            _strategyEditorTitleInput.Initialize(_windows, _canvas, _viewport, WINDOW_ID);
        var editorView = StrategyEditorContentBuilder.Build(_strategyEditorBody, WINDOW_ID, _registry, font: _font);
        if (editorView != null) _editors[WINDOW_ID] = editorView;

        // footer transport + #39 mode segment / LiveAuto ▶ (wired to the host live seam). The
        // mode-aware footer reuses the same view; modeVm/autoVm enable the Replay/Manual/Auto segments
        // and the LiveAuto ▶ on top of the Replay transport (findings 0026 §4).
        _transport = new ReplayTransportViewModel(_lifecycle);
        _footerMode = new FooterModeViewModel();
        _footerAuto = new LiveAutoTransportViewModel(
            _host.Panel,                                              // run lifecycle authority
            new BoundStrategyFileProvider(_strategyFile),
            _footerSelected,
            () => new List<string>(_scenario.Universe.Ids),          // scenario run universe
            () => !string.IsNullOrEmpty(_host.Conn.VenueId) ? _host.Conn.VenueId : _venue);
        _footer = new ReplayFooterView(
            _transport, OnFooterPlayPause, OnFooterStep, OnFooterStop, OnFooterSpeed, _font,
            _footerMode, _footerAuto, OnFooterMode);
        _footer.Build(_footerContainer);

        // menu bar (V-host): File = Layout; the Venue submenu reuses the host's durable Conn/Coord so a
        // connect routes to host.VenueLogin (the prod Venue submenu UI is #42; secret modal is #23).
        // mode/run now come from the live seam (footer DisplayMode + Panel lifecycle + the host run).
        _venueMenu = new VenueMenuViewModel(_host.Conn, _host.Coord);
        _menuBar = new MenuBarViewModel(_venueMenu, _host.Conn,
            currentMode: () => _footerMode.DisplayMode,
            isLiveAutoRunning: () => _footerAuto != null && _footerAuto.HasActiveRun,
            isReplayRunning: () => _host.IsRunning);
        if (_menuBarView != null)
            _menuBarView.Bind(_menuBar, OnFileNew, OnFileOpen, OnFileSave,
                OnVenueConnect, OnVenueDisconnect,
                () => _host.ServerReady && !_host.TeardownComplete,   // connect-ready gate
                () => _footerMode.DisplayMode,                        // bar mode badge
                _venue);                                              // "MOCK" → dev connect item (editor only)

        // sidebar (V-host): reuse the durable controller brain. The sidebar edits the SAME universe
        // SoT the startup tile edits and OnRun reads (_scenario.Universe) — "one universe per workspace"
        // (#31 designed controller.Registry to be host-wired; the cutover shell wires it here, #59).
        // SelectedSymbol is the SHARED _footerSelected so a sidebar instrument selection reaches the
        // footer's LiveAuto start (#39; else _footerSelected stays empty and LiveAuto always uses
        // universe[0] regardless of what the user picked).
        // The candidate source is still a mock (real supply = #46 kabu list / #41 prune / DuckDB).
        var provider = new MockAvailableInstrumentsProvider(new[] { "1301.TSE", "6758.TSE", "7203.TSE", "8918.TSE", "9432.TSE", "9984.TSE" });
        _sidebarCtrl = new UniverseSidebarController(_scenario.Universe, _footerSelected, new UniverseWriteback(), provider);
        // Populate (ResolvePaths) already restored the universe into the shared registry, so prime the
        // sidebar's fresh writeback to that set — the restored ids are not an unsaved edit (#31 D4).
        _sidebarCtrl.PrimeWritebackFromCurrent();
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

    // ---- #60 chart tile family: keep Hakoniwa chart tiles == the universe SoT ----
    // box-grow constants (TTWR HAKONIWA_BOX_GROW_MIN_TILE_SIZE / HAKONIWA_DEFAULT_SIZE). #60 has no
    // box drag handle, so dragHeight = 0 (#63 supplies the real height with the box drag strip).
    static readonly Vector2 HAKO_MIN_TILE = new Vector2(280f, 180f);
    static readonly Vector2 HAKO_DEFAULT_BOX = new Vector2(700f, 450f);

    // Reconcile the live chart tiles to _scenario.Universe (the membership SoT): despawn tiles whose
    // instrument left the universe, spawn one for each newly-present instrument, then box-grow. Bound
    // to InstrumentRegistry.Changed so a sidebar/tile/picker edit reflects immediately (no Run wait).
    void SyncChartTilesToUniverse()
    {
        var ids = _scenario.Universe.Ids;
        var desired = new HashSet<string>(ids);

        var stale = new List<string>();
        foreach (var id in _chartTiles.Keys) if (!desired.Contains(id)) stale.Add(id);
        foreach (var id in stale) DespawnChartTile(id);

        foreach (var id in ids) if (!_chartViews.ContainsKey(id)) SpawnChartTile(id);

        ApplyBoxGrow();
    }

    // Build one chart tile (chart:<id>) with the same chrome as the base tiles + its own ChartView,
    // register it with the controller (appended as the new last slot), and wire header-drag swap.
    void SpawnChartTile(string instrumentId)
    {
        if (string.IsNullOrEmpty(instrumentId) || _chartViews.ContainsKey(instrumentId)) return;
        string tileId = "chart:" + instrumentId;

        var go = new GameObject(tileId, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(_hakoniwaRoot, false);

        RectTransform body = BuildTileChrome(rt, tileId, out HakoniwaTileHeaderInput header);
        var cv = body.gameObject.AddComponent<ChartView>();
        cv.Build(body, showTitleBar: false);

        _hako.AddTile(tileId, rt);                 // box-size-free Rebuild lays the new cell
        header.Initialize(_hako, _hakoniwaRoot, tileId);

        _chartTiles[instrumentId] = rt;
        _chartViews[instrumentId] = cv;
    }

    void DespawnChartTile(string instrumentId)
    {
        _hako.RemoveTile("chart:" + instrumentId);
        if (_chartTiles.TryGetValue(instrumentId, out var rt) && rt != null) DestroyTileGo(rt.gameObject);
        _chartTiles.Remove(instrumentId);
        _chartViews.Remove(instrumentId);
        _chartRendered.Remove(instrumentId);
    }

    // Destroy at runtime, DestroyImmediate in edit mode (the AFK probe drives spawn/despawn headlessly
    // — Object.Destroy is a no-op-with-error outside play).
    static void DestroyTileGo(GameObject go)
    {
        if (go == null) return;
        if (Application.isPlaying) Destroy(go); else DestroyImmediate(go);
    }

    // box-grow: the box SIZE is derived from the tile count every membership change (NOT persisted);
    // the box POSITION is fixed. Owned HERE (the membership orchestrator), not in the controller's
    // box-size-free Rebuild (findings 0027 §0/§6 — TTWR sync-system-owns-box vs relayout-box-free).
    void ApplyBoxGrow()
    {
        if (_hakoniwaRoot != null)
            _hakoniwaRoot.sizeDelta = HakoniwaGridMath.ComputeBoxSize(_hako.Count, HAKO_MIN_TILE, 0f, HAKO_DEFAULT_BOX);
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

        // Commit just wrote the sidecar to the CURRENT universe; re-prime the sidebar writeback's
        // _lastFlushed to match, so a later sidebar ×/add (the only path that flushes) diffs against
        // what is actually on disk. Without this, a tile-added id committed at Run leaves _lastFlushed
        // stale -> the next sidebar edit sees no diff -> Flush SKIPS -> phantom id persists on disk
        // (findings 0025 §12, Finding 1).
        _sidebarCtrl.PrimeWritebackFromCurrent();

        if (!_isOwner) { _tile.ShowRunMessage("Not the Python owner — cannot run."); return; }

        _chartRendered.Clear();
        _lastPayload = null;
        _errLogged = false;
        _finishedHandled = false;
        _transport?.OnRunStarted();

        var req = new WorkspaceEngineHost.RunRequest
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

            // per-id chart render (#60): each chart:<id> tile draws its OWN per_instrument[id].ohlc_points
            // (NOT the aggregate/primary top-level ohlc_points). _chartRendered dedups by series length.
            // Guarded like ApplyPoll above: InstrumentOhlcDecoder surfaces a malformed/partial snapshot as
            // FormatException, so keep the last per-id render rather than throwing out of Update each frame.
            try
            {
                foreach (var kv in _chartViews)
                {
                    InstrumentOhlcFrame f = InstrumentOhlcDecoder.Decode(state, kv.Key);
                    if (!f.HasSeries || kv.Value == null) continue;
                    int cnt = f.Ohlc != null ? f.Ohlc.Count : 0;
                    if (!_chartRendered.TryGetValue(kv.Key, out int prev) || prev != cnt)
                    {
                        kv.Value.Render(new ReplayBarFrame { Ohlc = f.Ohlc });
                        _chartRendered[kv.Key] = cnt;
                    }
                }
            }
            catch { /* malformed poll snapshot: keep the last per-id render */ }
        }

        // #39: drain live push events into the Panel (footer run lifecycle authority), then drive the
        // mode footer. (A SecretRequired event — live order 2nd password — would surface a secret modal;
        // that modal is #23's surface, so here we only feed the Panel.)
        _host.DrainLiveEvents();
        DriveFooter();
    }

    // ---- #39: main-thread footer drive (ported from the retired ProductionLiveShell, with its review
    // fixes): consume worker→VM signals, overwrite the mode display from the poll (D1), release the
    // start guard as the lifecycle catches up, honour venue-drop auto-replay (G1: stop the run, not just
    // fall back to Replay), and Refresh only on a real state change. ----
    void DriveFooter()
    {
        if (_footer == null || _footerMode == null) return;

        if (_footerModeRejected) { _footerModeRejected = false; _footerMode.NotifyModeResult(false); }
        int sr = _footerStartResult;
        if (sr != 0)
        {
            _footerStartResult = 0;
            _footerAuto.NotifyStartResult(sr == 1, _footerStartedRunId);
            // No live panels on the mainline yet (slice 3), so surface a start failure as a menu notice.
            if (sr == 2) _menuBarView?.ShowMessage("LiveAuto start failed (see Console)");
        }

        // venue login/logout acks (worker→main): apply the login ack to the poll-canonical Conn VM and
        // surface a failure notice; the badge otherwise tracks the poll (findings 0027 D5).
        int lr = _venueLoginResult;
        if (lr != 0)
        {
            _venueLoginResult = 0;
            _host.Conn.ApplyLoginAck(lr == 1, _venueLoginError);
            // clear the transient "connecting…" on success (the badge alone shows Connected); surface
            // the reason on failure.
            _menuBarView?.ShowMessage(lr == 2 ? "login failed: " + _venueLoginError : null);
        }
        if (_venueLogoutFailed) { _venueLogoutFailed = false; _menuBarView?.ShowMessage("logout failed (write in flight?)"); }

        string st = _host.LatestStateJson;
        if (!string.IsNullOrEmpty(st))
        {
            // FooterModeViewModel.ApplyPoll has no internal dedup (it parses JSON every call), so gate it
            // on a changed payload to avoid a per-frame parse. Conn.ApplyStatePoll dedups internally.
            if (st != _lastFooterPoll) { _lastFooterPoll = st; _footerMode.ApplyPoll(st); }
            _host.Conn.ApplyStatePoll(st);
        }

        _footerAuto.ObserveLifecycle();

        // G1: a venue drop does NOT stop a running LiveAuto run (engine emits VenueLogoutDetected only),
        // so an active run must be stopped first. Act (and consume the one-shot) only when no live RPC is
        // in flight and we are not tearing down — level-triggered, so deferring a frame is safe.
        if (!_host.TeardownComplete && !_host.LiveRpcInFlight && _footerMode.ShouldAutoReplay)
        {
            _footerMode.ConsumeAutoReplay();
            if (_footerAuto.HasActiveRun)
                _host.StopLiveThenSetMode(_footerAuto.ActiveRunId, FooterModeViewModel.Replay, ok => { if (!ok) _footerModeRejected = true; });
            else
                _host.SetExecutionMode(FooterModeViewModel.Replay, ok => { if (!ok) _footerModeRejected = true; });
        }

        // Refresh only when something the footer renders changed (DisplayMode/lock/venue/run/glyph/auto
        // status, plus the Replay transport phase for the Replay-mode footer).
        string sig = _footerMode.DisplayMode + "|" + _footerMode.Locked + "|" + _footerMode.VenueLive
                   + "|" + _footerAuto.HasActiveRun + "|" + _footerAuto.PlayGlyph + "|" + _autoStatus
                   + "|" + _lifecycle.Phase;
        if (sig != _lastFooterSig) { _lastFooterSig = sig; _footer.Refresh(); }
    }

    // ---- footer click handlers → Host (mode-routed) ----
    // ▶/⏸ is mode-routed (TTWR footer_pause_resume_system): LiveAuto → start/pause/resume on the live
    // seam; Replay → the replay transport. step/stop/speed are Replay-only (the footer hides them in Live).
    void OnFooterPlayPause()
    {
        // The ▶ is hidden in LiveManual (the view); guard the handler too so a stray click can't fall
        // through to the Replay branch and start a REPLAY run while the engine mode is LiveManual.
        if (_footerMode.DisplayMode == FooterModeViewModel.LiveManual) return;
        if (_footerMode.DisplayMode == FooterModeViewModel.LiveAuto)
        {
            var d = _footerAuto.PlayPauseDecision();
            switch (d.Action)
            {
                case LiveAutoAction.Start: FooterAutoStart(d.Start); break;
                case LiveAutoAction.Pause: _host.PauseLiveStrategy(d.RunId, _ => { }); _autoStatus = "pausing…"; break;
                case LiveAutoAction.Resume: _host.ResumeLiveStrategy(d.RunId, _ => { }); _autoStatus = "resuming…"; break;
                case LiveAutoAction.None: _autoStatus = d.Message; break;
            }
            return;
        }
        switch (_transport.PlayPauseIntent())
        {
            case ReplayTransportIntent.Run: OnRun(); break;
            case ReplayTransportIntent.Pause: _host.Pause(); break;
            case ReplayTransportIntent.Resume: _host.Resume(); break;
        }
    }

    // footer mode segment → SetExecutionMode (D1) / stop-then-switch on leaving LiveAuto (D2).
    void OnFooterMode(string target)
    {
        // Block a mode switch while a live RPC (or a start awaiting its first lifecycle) is in flight, so
        // set_execution_mode can't race start_live_strategy under the GIL and a StopRunThenSwitch can't
        // lock the segment VM then no-op (the #39 review's High finding).
        if (_host.LiveRpcInFlight || _footerAuto.IsStartInFlight)
        {
            _menuBarView?.ShowMessage("Live action in flight — wait before switching mode.");
            return;
        }
        var req = _footerMode.RequestMode(target, _footerAuto.HasActiveRun);
        switch (req.Kind)
        {
            case FooterModeRequestKind.SwitchImmediate:
            case FooterModeRequestKind.SwitchLockedLive:
                _host.SetExecutionMode(req.Target, ok => { if (!ok) _footerModeRejected = true; });
                break;
            case FooterModeRequestKind.StopRunThenSwitch:
                _host.StopLiveThenSetMode(_footerAuto.ActiveRunId, req.Target, ok => { if (!ok) _footerModeRejected = true; });
                break;
            case FooterModeRequestKind.BlockedVenueNotLive:
                _menuBarView?.ShowMessage(req.Message);
                break;
            case FooterModeRequestKind.Ignore:
                break;
        }
    }

    // LiveAuto ▶ at rest → register→start on the live seam. Pre-flight is already gated by the VM; the
    // connection gate (the VM only checks a non-empty venue string) is re-asserted here.
    void FooterAutoStart(LiveAutoStartRequest req)
    {
        // Surface the pre-flight block reason: _autoStatus is not rendered on the mainline (no live panel
        // yet — slice 3), so a ▶ that silently no-ops (no instrument / no saved strategy / venue) was
        // undiagnosable. Route it to the menu notice (findings 0027 §2.1 HITL observability).
        if (req.Gate != LiveAutoStartGate.Ready) { _autoStatus = req.Message; _menuBarView?.ShowMessage("LiveAuto ▶: " + req.Message); return; }
        if (!_host.ServerReady || !_host.Conn.IsConnected) { _autoStatus = "connect a venue before starting LiveAuto"; _menuBarView?.ShowMessage("connect a venue before starting LiveAuto"); return; }
        _footerAuto.NotifyStartIssued();
        _autoStatus = "register+start…";
        _host.RegisterAndStartLiveAuto(req.StrategyFile, req.OriginalPath, req.InstrumentId, req.Venue,
            (ok, runId) => { _footerStartedRunId = runId; _footerStartResult = ok ? 1 : 2; });
    }

    void OnFooterStep() => _host.Step();
    void OnFooterStop() => _host.ForceStop();
    void OnFooterSpeed(int mult) { if (_transport.SelectSpeed(mult)) _host.SetSpeed(mult); }

    // ---- File = Layout (findings 0025 §9) ----
    void OnFileNew()
    {
        var decision = _menuBar.FileNew(out string modeReq, out string refuse);
        if (decision == FileNewDecision.RefusedRunning) { _menuBarView?.ShowMessage(refuse); return; }

        // full TTWR in-memory reset (findings 0017 §4) that HONOURS the adopt invariant (findings 0025 §8):
        // ADDITIONAL editor windows are destroyed + unregistered; the scene-authored adopted editor is
        // reset IN PLACE (never destroyed — Apply(Default()) would destroy it, so close each additional
        // window individually). canvas pan/zoom + Hakoniwa are NOT reset (§4 targets strategy/editor state).
        var additional = new List<string>();
        foreach (var id in _editors.Keys) if (id != WINDOW_ID) additional.Add(id);
        foreach (var id in additional)
        {
            _windows.Close(id);
            _registry.Unregister(id);
            _editors.Remove(id);
        }
        if (_editors.TryGetValue(WINDOW_ID, out var adoptedEditor) && adoptedEditor != null)
            adoptedEditor.ResetUnboundEmpty();

        // scenario buffer + universe (the in-memory clear the host owns).
        _scenario.Clear();
        _tile?.SyncFieldsFromController();

        // mode side-effect (findings 0027 D3): SetExecutionMode(LiveManual) ONLY when connected — the VM
        // returns null otherwise (gap② guard, TTWR observable no-op). HITL-verified; the AFK probe runs
        // disconnected so modeReq is null here and the host is never touched.
        SendModeSideEffect(modeReq);

        _menuBarView?.ShowMessage("New: workspace cleared.");
    }

    void OnFileOpen()
    {
        // TTWR: opening a layout WHILE Live transitions to LiveAuto, BEFORE the load (findings 0017 §1).
        // FileOpenModeSideEffect returns null in Replay / when disconnected (gap② guard) → no host touch.
        SendModeSideEffect(_menuBar.FileOpenModeSideEffect());
        RestoreLayout();
        _menuBarView?.ShowMessage("Open: layout restored.");
    }

    // SetExecutionMode for a File-op side-effect, with the standard worker→main reject marshalling. No-op
    // for a null mode (the VM returns null when disconnected / not in a Live mode — TTWR observable no-op).
    void SendModeSideEffect(string mode)
    {
        if (mode != null) _host.SetExecutionMode(mode, ok => { if (!ok) _footerModeRejected = true; });
    }

    void OnFileSave()
    {
        SaveLayout();
        _menuBarView?.ShowMessage("Save: layout written.");
    }

    // ---- Venue submenu → host venue RPCs (findings 0027 D5). Connect builds the request via the reused
    // VenueMenuViewModel (MOCK = credential-less dev venue → "env" source so no tkinter prompt spawns),
    // routes to the host lane, and the login ack is marshalled to main in DriveFooter (worker→main). The
    // host owns the GIL discipline; this root never touches _conn off the main thread. ----
    void OnVenueConnect(string venue, string env)
    {
        _venue = venue;
        VenueConnectRequest req = _venueMenu.BuildConnectRequest(venue, env);
        if (venue == "MOCK") req.CredentialsSource = "env";   // credential-less dev venue (findings 0027 D2)
        _host.VenueLogin(req.Venue, req.CredentialsSource, req.EnvironmentHint,
            (ok, ec) => { _venueLoginError = ec; _venueLoginResult = ok ? 1 : 2; });
        _menuBarView?.ShowMessage(venue == "MOCK" ? "connecting MOCK…" : "login (enter credentials)…");
    }

    void OnVenueDisconnect()
    {
        // Serialize against in-flight live RPCs (same guard as OnFooterMode) so logout can't interleave
        // with a register→start / mode switch under the GIL (review High).
        if (_host.LiveRpcInFlight || _footerAuto.IsStartInFlight)
        {
            _menuBarView?.ShowMessage("Live action in flight — wait before disconnecting.");
            return;
        }
        // With an active LiveAuto run, stop it + leave Live (Replay) BEFORE venue_logout so the engine's
        // _teardown_live_components can't race the footer's auto-replay recovery (review High, D2 ordering).
        if (_footerAuto.HasActiveRun)
            _host.StopLiveThenLogout(_footerAuto.ActiveRunId, ok => { if (!ok) _venueLogoutFailed = true; });
        else
            _host.VenueLogout(ok => { if (!ok) _venueLogoutFailed = true; });
        _menuBarView?.ShowMessage("disconnecting…");
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
        _tile?.Dispose();                         // unsubscribe the tile from _scenario.Universe.Changed (no orphan handler)
        _scenario.Universe.Changed -= SyncChartTilesToUniverse;   // #60 chart-tile sync unsubscribe (no orphan handler)
        _host.Stop();                             // 3-7. force_stop → poll stop → bounded join → no Shutdown
        Debug.Log("[BackcastWorkspaceRoot] teardown complete.");
    }

    void OnApplicationQuit() => StopAndDispose();
    void OnDestroy() => StopAndDispose();
}
