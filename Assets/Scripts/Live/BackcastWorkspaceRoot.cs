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
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Python.Runtime;

public sealed class BackcastWorkspaceRoot : MonoBehaviour
{
    const string WINDOW_ID = "strategy_editor:region_001";
    const string ORDER_WINDOW_ID = "order:region_001";   // #23 re-home: singleton Order ticket window

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

    [Header("Live data Hakoniwa tiles (scene-authored, #23 re-home)")]
    [SerializeField] RectTransform _ordersTile;
    [SerializeField] RectTransform _positionsTile;
    [SerializeField] RectTransform _runResultTile;

    [Header("Order ticket floating window (scene-authored, adopted, #23 re-home)")]
    [SerializeField] RectTransform _orderWindow;
    [SerializeField] RectTransform _orderWindowBody;
    [SerializeField] FloatingWindowTitleInput _orderWindowTitleInput;

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
    // #61 mode-conditional base tiles: id → tile RectTransform for the always-present base panels
    // (buying_power/orders/positions/run_result) + the Replay-only `startup`. _baseTiles is the handle
    // the base retile (SyncBaseTilesToMode) and restore re-assert (ReassertBaseAfterRestore) use to
    // reorder/show the base region. orders/positions/run_result are the #23 scene tiles (rendered by the
    // LivePanelTileView fields below); buying_power is spawned dynamically (SpawnBuyingPowerTile, also a
    // LivePanelTileView). _baseLive caches the current base shape (false=Replay, true=Live) so DriveFooter
    // retiles only when the shape actually flips (TTWR set-comparison).
    readonly Dictionary<string, RectTransform> _baseTiles = new Dictionary<string, RectTransform>();
    bool _baseLive;
    // #57 depth ladder: each chart tile's body is split into a mode-resized chartArea (left) + a
    // LADDER_WIDTH right strip holding a per-instrument DepthLadderView. Live shows the ladder (chart
    // shrinks left); Replay hides it (chart reclaims full width) — depth is Live-only (findings 0028
    // D1/D2). _depthRendered dedups the per-id 21-row rebuild by a depth+last signature.
    const float LADDER_WIDTH = 120f;                 // TTWR viewstate::LADDER_WIDTH
    readonly Dictionary<string, RectTransform> _chartAreas = new Dictionary<string, RectTransform>();
    readonly Dictionary<string, DepthLadderView> _depthLadders = new Dictionary<string, DepthLadderView>();
    readonly Dictionary<string, long> _depthRendered = new Dictionary<string, long>();
    bool _lastLadderLive;                             // last applied Live/Replay geometry (dedup the rect flip)
    string _lastDepthPayload;                         // last poll payload rendered into the ladders
    ScenarioStartupTile _tile;
    ReplayFooterView _footer;
    MenuBarViewModel _menuBar;
    VenueMenuViewModel _venueMenu;
    UniverseSidebarController _sidebarCtrl;
    readonly Dictionary<string, StrategyEditorView> _editors = new Dictionary<string, StrategyEditorView>();
    Font _font;

    // ── #23 re-home: live surfaces (3 data tiles / Order ticket / secret modal) ──
    // #61 adds _buyingPowerView (the 4th base panel, dynamically spawned — no scene tile) using the same
    // LivePanelTileView wiring as the 3 #23 tiles.
    LivePanelTileView _ordersView, _positionsView, _runResultView, _buyingPowerView;
    OrderTicketView _orderTicket;
    SecretModalOverlay _secretOverlay;
    bool _secretModalOpenPrev;
    // Manual ticket status crosses worker→main: LiveRpcLanes invokes the result callback on a lane
    // thread, but the OrderTicketView (uGUI) is main-only. Stash to volatiles; DriveOrderTicket applies.
    volatile string _manualStatusLine = "-";   // pre-formatted on the worker; applied to the view on main
    volatile string _manualOrderId = "";        // retained for the next "Cancel last"
    volatile bool _manualStatusDirty;
    long _lastPanelApplied = -1;                 // LivePanelViewModel.AppliedCount gate (skip idle tile re-format)
    // Venue login ack crosses worker→main (host.VenueLogin onResult runs off-main); apply to Conn on main.
    volatile bool _loginAckPending;
    volatile bool _loginAckOk;
    volatile string _loginAckEc = "";

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
    string _venue = "MOCK";                 // live venue id; resolved from LIVE_VENUE env in Awake (default MOCK)
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

        // Live venue is one-per-server and bound at server build (the backend rejects a later
        // venue_login for a different venue with VENUE_MISMATCH, live_orchestrator.py:684). So it must
        // be chosen BEFORE InitializePython — resolve it from LIVE_VENUE env now (the documented live
        // selector; default MOCK keeps the Replay mainline credential-less). A demo HITL sets
        // LIVE_VENUE=TACHIBANA|KABU in .env before Play.
        _venue = ResolveLiveVenue();

        ResolvePaths();
        BuildWorkspace();        // UI build ALWAYS runs (independent of _ownPlay / batchmode)
        _built = true;           // this root is the active layout owner regardless of Python ownership

        // single Play-owner: claim Python when configured owner + not headless + (free interpreter OR
        // our own host already bootstrapped it — so a re-Play without domain reload reclaims it).
        _isOwner = WorkspaceOwnership.ShouldClaim(_ownPlay, Application.isBatchMode, PythonEngine.IsInitialized, _host.PythonInitialized);
        if (_isOwner)
        {
            try { _host.InitializePython(_venue); }   // build the server for the configured venue (not always MOCK)
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

        // #23 re-home: three live data tiles (Orders / Positions / Run Result), fed by _host.Panel
        // (LivePanelViewModel — the SoT, findings 0011 D2 / 0014 RH2). Empty until live events arrive.
        RectTransform ordersBody = BuildTileChrome(_ordersTile, "orders", out HakoniwaTileHeaderInput ordersHeader);
        _ordersView = new LivePanelTileView(FormatOrders);
        _ordersView.Build(ordersBody, _font);

        RectTransform positionsBody = BuildTileChrome(_positionsTile, "positions", out HakoniwaTileHeaderInput positionsHeader);
        _positionsView = new LivePanelTileView(FormatPositions);
        _positionsView.Build(positionsBody, _font);

        RectTransform runResultBody = BuildTileChrome(_runResultTile, "run_result", out HakoniwaTileHeaderInput runResultHeader);
        _runResultView = new LivePanelTileView(FormatRunResult);
        _runResultView.Build(runResultBody, _font);

        // Merge of #60 (dynamic chart tile family) + #23 (re-homed live tiles) + #61 (mode-conditional
        // base): the scene-authored base tiles are [startup, orders, positions, run_result] — the single
        // "chart" tile is retired (deactivated above) and replaced by the dynamic chart:<id> family driven
        // from the universe (#60). The 3 live data tiles are fed by _host.Panel (#23 RH2). #61 (below)
        // adds the BuyingPower tile and reorders to the canonical mode-conditional base set.
        _hako = new HakoniwaController(_hakoniwaRoot,
            new Dictionary<string, RectTransform>
            {
                { "startup", _startupTile },
                { "orders", _ordersTile }, { "positions", _positionsTile }, { "run_result", _runResultTile },
            },
            new[] { "startup", "orders", "positions", "run_result" });
        startupHeader.Initialize(_hako, _hakoniwaRoot, "startup");
        ordersHeader.Initialize(_hako, _hakoniwaRoot, "orders");
        positionsHeader.Initialize(_hako, _hakoniwaRoot, "positions");
        runResultHeader.Initialize(_hako, _hakoniwaRoot, "run_result");

        // #61 mode-conditional base: the base SET (the non-chart, mode-owned tiles) is [startup?,
        // buying_power, orders, positions, run_result]. Orders/Positions/RunResult are the #23-built
        // scene tiles (LivePanelTileView, already controller-registered above) — track them in _baseTiles
        // so the base retile / restore re-assert can manage them. BuyingPower has NO scene tile, so spawn
        // it dynamically with the SAME #23 LivePanelTileView wiring. `startup` is the only mode-conditional
        // base tile (ADR 0013) — SyncBaseTilesToMode toggles it. Build defaults to the Replay shape
        // (DisplayMode seeds to Replay), so startup is present and _baseLive = false.
        _baseTiles[HakoniwaBaseTiles.Startup]   = _startupTile;
        _baseTiles[HakoniwaBaseTiles.Orders]    = _ordersTile;
        _baseTiles[HakoniwaBaseTiles.Positions] = _positionsTile;
        _baseTiles[HakoniwaBaseTiles.RunResult] = _runResultTile;
        SpawnBuyingPowerTile();
        _hako.Reorder(HakoniwaBaseTiles.Kinds(false));   // canonical [startup, buying_power, orders, positions, run_result]
        _baseLive = false;

        SyncChartTilesToUniverse();                          // spawn chart:<id> for the current universe + box-grow (#60)
        _scenario.Universe.Changed += SyncChartTilesToUniverse;   // keep chart tiles == universe (Dispose unsubs)

        // adopt the scene-authored Strategy Editor window (NEVER destroyed+respawned, findings 0025 §8).
        _windows.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, WINDOW_ID, _strategyEditorWindow);
        if (_strategyEditorTitleInput != null)
            _strategyEditorTitleInput.Initialize(_windows, _canvas, _viewport, WINDOW_ID);
        var editorView = StrategyEditorContentBuilder.Build(_strategyEditorBody, WINDOW_ID, _registry, font: _font);
        if (editorView != null) _editors[WINDOW_ID] = editorView;

        // #23 re-home: adopt the scene-authored Order ticket window (KIND_ORDER) — parity with the
        // editor adopt (never destroyed+respawned, findings 0025 §8 / 0014 RH4). Content = OrderTicketView;
        // visible ONLY while the footer mode is LiveManual (DriveOrderTicket). Place/Cancel route to the
        // host RPC lanes; the result callbacks marshal to main via the _manual* volatiles.
        if (_orderWindow != null)
        {
            _windows.Adopt(FloatingWindowCatalog.KIND_ORDER, ORDER_WINDOW_ID, _orderWindow);
            if (_orderWindowTitleInput != null)
                _orderWindowTitleInput.Initialize(_windows, _canvas, _viewport, ORDER_WINDOW_ID);
            _orderTicket = new OrderTicketView();
            _orderTicket.Build(_orderWindowBody, _font);
            _orderTicket.PlaceRequested += OnManualPlace;
            _orderTicket.CancelRequested += OnManualCancel;
            _orderWindow.gameObject.SetActive(false);   // hidden until LiveManual
        }

        // #23 re-home: secret modal overlay (screen-fixed chrome on its OWN ScreenSpaceOverlay canvas,
        // outside Content). Keystrokes drain char-by-char via the New Input System onTextInput inside the
        // overlay; the root routes them into the host's SecretModalController (no plaintext managed string).
        var secretGo = new GameObject("SecretModalOverlay");
        secretGo.transform.SetParent(transform, false);
        _secretOverlay = secretGo.AddComponent<SecretModalOverlay>();
        _secretOverlay.Build(_font);
        _secretOverlay.CharTyped += OnSecretChar;
        _secretOverlay.BackspacePressed += OnSecretBackspace;
        _secretOverlay.SubmitClicked += SubmitSecret;
        _secretOverlay.CancelClicked += CancelSecret;

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
        // #23 re-home: an Order window restored from a saved doc uses the Order frame (the singleton
        // ticket is adopted, not spawned, so this only fires for a stray/legacy id — frame only).
        if (spec.kind == FloatingWindowCatalog.KIND_ORDER)
        {
            var orderRoot = OrderTicketWindowFrame.Build(id, out var orderTitle, out _);
            orderTitle.Initialize(_windows, _canvas, _viewport, id);
            return orderRoot;
        }
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

    // Build a Hakoniwa tile shell (GameObject under the root + panel chrome + header-drag swap +
    // controller registration) shared by chart and base tiles. Returns the tile root; `body` is the
    // content inset the caller fills with its view (ChartView / LivePanelTileView). box-size-free.
    RectTransform BuildTileShell(string id, out RectTransform body)
    {
        var go = new GameObject(id, typeof(RectTransform), typeof(Image));
        var rt = (RectTransform)go.transform;
        rt.SetParent(_hakoniwaRoot, false);
        body = BuildTileChrome(rt, id, out HakoniwaTileHeaderInput header);
        _hako.AddTile(id, rt);                     // box-size-free Rebuild lays the new cell
        header.Initialize(_hako, _hakoniwaRoot, id);
        return rt;
    }

    // Build one chart tile (chart:<id>) with its own ChartView, appended as the new last slot.
    void SpawnChartTile(string instrumentId)
    {
        if (string.IsNullOrEmpty(instrumentId) || _chartViews.ContainsKey(instrumentId)) return;
        string tileId = "chart:" + instrumentId;
        var rt = BuildTileShell(tileId, out RectTransform body);

        // #57: split the body into a chartArea (left, right-inset in Live so candles aren't covered) +
        // a LADDER_WIDTH right strip hosting this instrument's DepthLadderView (TTWR overlays_ladder.rs
        // right pane, findings 0028 D1). Apply the CURRENT mode immediately so a tile spawned while Live
        // shows its ladder (TTWR Added<ChartInstrument>, D3).
        var chartAreaGo = new GameObject("ChartArea", typeof(RectTransform));
        var chartArea = (RectTransform)chartAreaGo.transform;
        chartArea.SetParent(body, false);
        chartArea.anchorMin = Vector2.zero; chartArea.anchorMax = Vector2.one;
        chartArea.offsetMin = Vector2.zero;
        chartArea.offsetMax = new Vector2(_lastLadderLive ? -LADDER_WIDTH : 0f, 0f);

        var ladderAreaGo = new GameObject("LadderArea", typeof(RectTransform));
        var ladderArea = (RectTransform)ladderAreaGo.transform;
        ladderArea.SetParent(body, false);
        ladderArea.anchorMin = new Vector2(1f, 0f); ladderArea.anchorMax = new Vector2(1f, 1f);
        ladderArea.pivot = new Vector2(1f, 0.5f);
        ladderArea.sizeDelta = new Vector2(LADDER_WIDTH, 0f);
        ladderArea.anchoredPosition = Vector2.zero;
        var ladder = ladderAreaGo.AddComponent<DepthLadderView>();
        ladder.Build(ladderArea);
        ladder.Render(DepthSnapshotView.Empty);    // show the "(no board)" placeholder until depth streams (no blank pane)
        ladderAreaGo.SetActive(_lastLadderLive);   // depth is Live-only (hidden in Replay)

        var cv = chartAreaGo.AddComponent<ChartView>();
        cv.Build(chartArea, showTitleBar: false);

        _chartTiles[instrumentId] = rt;
        _chartViews[instrumentId] = cv;
        _chartAreas[instrumentId] = chartArea;
        _depthLadders[instrumentId] = ladder;
        _lastDepthPayload = null;   // a tile added mid-Live renders its board on the NEXT poll, not the one after
    }

    void DespawnChartTile(string instrumentId)
    {
        _hako.RemoveTile("chart:" + instrumentId);
        if (_chartTiles.TryGetValue(instrumentId, out var rt) && rt != null) DestroyTileGo(rt.gameObject);
        _chartTiles.Remove(instrumentId);
        _chartViews.Remove(instrumentId);
        _chartRendered.Remove(instrumentId);
        // #57: the ladder + chartArea are children of the destroyed tile (no separate destroy needed).
        _chartAreas.Remove(instrumentId);
        _depthLadders.Remove(instrumentId);
        _depthRendered.Remove(instrumentId);
    }

    // ---- #61 mode-conditional base tiles: the base SET is owned by the mode (TTWR ExecutionMode) ----
    // BuyingPower base tile. Unlike Orders/Positions/RunResult (scene-authored, #23) there is no scene
    // tile for BuyingPower, so build it dynamically (BuildTileShell = chrome + controller add + header
    // swap, mirrors SpawnChartTile) and render it with the SAME #23 LivePanelTileView wiring the other
    // base panels use (FormatBuyingPower), fed from _host.Panel via RefreshLiveTiles.
    void SpawnBuyingPowerTile()
    {
        if (_buyingPowerView != null) return;
        var rt = BuildTileShell(HakoniwaBaseTiles.BuyingPower, out RectTransform body);
        _buyingPowerView = new LivePanelTileView(FormatBuyingPower);
        _buyingPowerView.Build(body, _font);
        _baseTiles[HakoniwaBaseTiles.BuyingPower] = rt;
    }

    // Base retile (TTWR reconcile_hakoniwa_tiles, base-only). The ONLY mode-conditional base tile is
    // `startup` (Replay-only, ADR 0013), so this toggles startup presence then restores the
    // [base…, chart…] order via Reorder (chart tiles keep identity — universe-owned, #169 split).
    // The scene-authored startup tile is DEACTIVATED (never destroyed) when leaving Replay.
    void SyncBaseTilesToMode(bool live)
    {
        bool wantStartup = !live;
        bool hasStartup = _hako.SlotOf(HakoniwaBaseTiles.Startup) >= 0;
        if (wantStartup && !hasStartup)
        {
            if (_startupTile != null) _startupTile.gameObject.SetActive(true);
            _hako.AddTile(HakoniwaBaseTiles.Startup, _startupTile);
        }
        else if (!wantStartup && hasStartup)
        {
            _hako.RemoveTile(HakoniwaBaseTiles.Startup);
            if (_startupTile != null) _startupTile.gameObject.SetActive(false);
        }

        _hako.Reorder(HakoniwaBaseTiles.Kinds(live));   // [base…, chart…] invariant restored
        ApplyBoxGrow();                                 // grid = n_base + n_chart (derived box-grow, #60)
        _baseLive = live;
        ForceRefreshLiveTiles();                        // shape flip: repaint the base panels now (#23 wiring)
    }

    // Re-assert the canonical [base…, chart…] order + base visibility after a layout restore. The
    // persisted doc may predate the base panels (a #60-era [startup, chart:*] sinks base tiles behind
    // charts), carry stale #12 ids, or COLLIDE with the base ids (LayoutDocument.Default() places
    // orders/positions/run_result at legacy slots) — Apply would then scramble the base region, sink
    // it behind charts, or (a stale visible=false) hide a panel. Reorder restores canonical base order
    // (charts keep their restored relative order); base tiles are not closeable so force them visible.
    // #61 is single-shared-layout: base order is canonical on restore (per-mode order persist is #62).
    void ReassertBaseAfterRestore()
    {
        _hako.Reorder(HakoniwaBaseTiles.Kinds(_baseLive));
        foreach (var id in HakoniwaBaseTiles.Kinds(_baseLive))
            if (_baseTiles.TryGetValue(id, out var rt) && rt != null) rt.gameObject.SetActive(true);
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
        _depthRendered.Clear();        // #57: a fresh run invalidates the cached per-id ladder renders
        _lastDepthPayload = null;
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

        // #23 re-home: apply a pending venue-login ack to Conn on the main thread (host.VenueLogin's
        // onResult fired on a worker; Conn is a main-only VM).
        if (_loginAckPending) { _loginAckPending = false; _host.Conn.ApplyLoginAck(_loginAckOk, _loginAckEc); }

        // #39/#23: drain live push events into the Panel (footer run lifecycle authority); a NEW
        // SecretRequired opens the secret modal (#23 re-home). Then refresh the live tiles + Order
        // ticket from the Panel, and drive the mode footer.
        bool newSecret = _host.DrainLiveEvents();
        DriveSecretModal(newSecret);
        RefreshLiveTiles();
        DriveOrderTicket();
        DriveFooter();
        DriveDepthLadders();   // #57: after DriveFooter so _footerMode.DisplayMode is the fresh mode
    }

    // ── #23 re-home: live data tiles (Orders / Positions / Run Result), fed by _host.Panel. Gate on
    // LivePanelViewModel.AppliedCount so the per-frame path costs one long compare (no string/StringBuilder
    // allocation) while the live session is idle — the formatters only run when an event was applied. ──
    void RefreshLiveTiles()
    {
        long applied = _host.Panel.AppliedCount;
        if (applied == _lastPanelApplied) return;
        _lastPanelApplied = applied;
        PushLiveTiles();
    }

    // Repaint all base panel tiles unconditionally (bypass the AppliedCount gate). #61 base retile uses
    // this on a Replay⇄Live shape flip so the panels redraw immediately (the gate would otherwise skip
    // an unchanged AppliedCount). _buyingPowerView is the #61 4th panel; the other three are #23 tiles.
    void ForceRefreshLiveTiles() => PushLiveTiles();

    void PushLiveTiles()
    {
        // honest empty state in Replay: _host.Panel is monotonic (never cleared), so after a Live→Replay
        // flip the live formatters would still render the LAST live account/orders/fills. In Replay the
        // base panels show "(no data — Replay)" instead; real replay numbers are follow-up #65 (findings 0028).
        if (!_baseLive)
        {
            _buyingPowerView?.ShowReplayEmpty();
            _ordersView?.ShowReplayEmpty();
            _positionsView?.ShowReplayEmpty();
            _runResultView?.ShowReplayEmpty();
            return;
        }
        LivePanelViewModel p = _host.Panel;
        _buyingPowerView?.Refresh(p);
        _ordersView?.Refresh(p);
        _positionsView?.Refresh(p);
        _runResultView?.Refresh(p);
    }

    // ── #57: per-frame depth ladder drive (TTWR overlays_ladder.rs mode_sync + render, findings 0028
    // D3/D5). depth is Live-only: flip the per-tile geometry on a Live↔Replay change, then in Live decode
    // each chart tile's OWN per_instrument[id].depth + .price and render it (skipped entirely in Replay). ──
    void DriveDepthLadders()
    {
        // Track the applied geometry even with zero ladders, so a tile spawned LATER (e.g. the universe
        // was empty when Live was entered) reads the correct _lastLadderLive at SpawnChartTile time.
        bool isLive = _footerMode != null && _footerMode.DisplayMode != FooterModeViewModel.Replay;
        if (isLive != _lastLadderLive)
        {
            ApplyDepthLadderMode(isLive);   // loops zero ladders harmlessly when the universe is empty
            _lastLadderLive = isLive;
            _depthRendered.Clear();   // re-render fresh boards when the ladders become visible again
            _lastDepthPayload = null; // ...even if the payload string is unchanged since we last rendered
        }
        if (!isLive || _depthLadders.Count == 0) return;   // Replay or no tiles: no decode

        string state = _host.LatestStateJson;
        if (state == null || state == _lastDepthPayload) return;
        _lastDepthPayload = state;
        RenderDepthLadders(state);
    }

    // Show the ladders (chart shrinks left by LADDER_WIDTH) or hide them (chart reclaims full width).
    // Touches rects only on a mode change (DriveDepthLadders dedups via _lastLadderLive).
    void ApplyDepthLadderMode(bool isLive)
    {
        foreach (var kv in _depthLadders)
        {
            if (_chartAreas.TryGetValue(kv.Key, out var area) && area != null)
                area.offsetMax = new Vector2(isLive ? -LADDER_WIDTH : 0f, area.offsetMax.y);
            if (kv.Value != null && kv.Value.gameObject.activeSelf != isLive)
                kv.Value.gameObject.SetActive(isLive);
        }
    }

    // Decode + render every chart tile's per-instrument board from the poll payload. Per-id signature
    // early-out: a tile whose depth+last is unchanged skips its 21-row rebuild even when another id moved
    // (TTWR depth_signature). Malformed snapshot keeps the last render (mirrors the OHLC loop).
    void RenderDepthLadders(string state)
    {
        foreach (var kv in _depthLadders)
        {
            if (kv.Value == null) continue;
            // Per-id try so one instrument's malformed depth keeps its OWN last render without freezing
            // the other tiles' boards for this payload (the grounded payload is normally valid JSON).
            try
            {
                DepthSnapshotView snap = DepthDecoder.Decode(state, kv.Key);
                double? last = InstrumentPriceDecoder.Decode(state, kv.Key);
                long sig = DepthSignature(snap, last);
                if (_depthRendered.TryGetValue(kv.Key, out long prev) && prev == sig) continue;
                _depthRendered[kv.Key] = sig;
                kv.Value.Render(snap, last);
            }
            catch { /* keep this id's last render; other tiles still update */ }
        }
    }

    // A content signature over EXACTLY what the ladder draws — depth presence, per-level price/size, and
    // LAST — so an unchanged board skips its 21-row rebuild while any drift (same count, different prices)
    // re-renders (TTWR FnvHasher). Deliberately NOT TimestampMs: a venue that bumps the board timestamp
    // every poll while bids/asks/last are byte-identical would otherwise defeat the early-out and rebuild
    // all 21 rows every poll (nothing visible changed).
    static long DepthSignature(DepthSnapshotView snap, double? last)
    {
        long h = snap.HasDepth ? 1469598103934665603L : 1L;   // FNV-ish seed, distinct for no-board
        h = MixLevels(h, snap.Asks);
        h = MixLevels(h, snap.Bids);
        h = Mix(h, last.HasValue ? BitConverter.DoubleToInt64Bits(last.Value) : long.MinValue);
        return h;
    }

    static long MixLevels(long h, IReadOnlyList<DepthLevelView> levels)
    {
        int n = levels != null ? levels.Count : 0;
        h = Mix(h, n);
        for (int i = 0; i < n; i++)
        {
            h = Mix(h, BitConverter.DoubleToInt64Bits(levels[i].Price));
            h = Mix(h, BitConverter.DoubleToInt64Bits(levels[i].Size));
        }
        return h;
    }

    static long Mix(long h, long v) => (h ^ v) * 1099511628211L;   // FNV-1a prime

    // ── #23 re-home: Order ticket window — visible only in LiveManual; show the resolved instrument and
    // gate the buttons on a live session; apply any worker-thread place/cancel status to the (main) view. ──
    void DriveOrderTicket()
    {
        if (_orderTicket == null || _orderWindow == null) return;
        bool liveManual = _footerMode != null && _footerMode.DisplayMode == FooterModeViewModel.LiveManual;
        if (_orderWindow.gameObject.activeSelf != liveManual) _orderWindow.gameObject.SetActive(liveManual);
        if (liveManual) _orderTicket.SetInstrument(ManualInstrument());   // the operator must see what they'll trade
        _orderTicket.SetInteractable(_host.ServerReady && _host.Conn.IsConnected && !_host.TeardownComplete);
        if (_manualStatusDirty)
        {
            _manualStatusDirty = false;
            _orderTicket.SetStatus(_manualStatusLine);
        }
    }

    void OnManualPlace()
    {
        if (_orderTicket == null) return;
        // Invariant-culture parse: the qty/price text must use '.' as the decimal point regardless of the
        // machine locale, matching the numeric convention the wire path expects (no locale-dependent misparse).
        if (!double.TryParse(_orderTicket.Qty, NumberStyles.Float, CultureInfo.InvariantCulture, out double qty) || qty <= 0)
        { _orderTicket.SetStatus("invalid qty"); return; }
        double? price = null;
        if (_orderTicket.Limit)
        {
            if (!double.TryParse(_orderTicket.Price, NumberStyles.Float, CultureInfo.InvariantCulture, out double p) || p <= 0)
            { _orderTicket.SetStatus("invalid limit price"); return; }
            price = p;
        }
        if (!_host.ServerReady || !_host.Conn.IsConnected || _host.Lanes == null) { _orderTicket.SetStatus("connect a venue first"); return; }
        // Live order safety: refuse rather than route to an arbitrary symbol when none is resolvable.
        string iid = ManualInstrument();
        if (string.IsNullOrEmpty(iid)) { _orderTicket.SetStatus("select an instrument (sidebar/universe) first"); return; }
        string side = _orderTicket.SideBuy ? "BUY" : "SELL";
        string type = _orderTicket.Limit ? "LIMIT" : "MARKET";
        _orderTicket.SetStatus("placing " + side + " " + qty + " " + type + "…");
        _host.Lanes.SubmitPlaceOrder(ManualVenue(), iid, side, qty, price, type, "DAY", res =>
        {
            if (res.Success && !string.IsNullOrEmpty(res.OrderId)) _manualOrderId = res.OrderId;
            string status = res.Success ? res.Status : ("ERR " + res.ErrorCode);
            _manualStatusLine = status + (string.IsNullOrEmpty(_manualOrderId) ? "" : " (" + _manualOrderId + ")");
            _manualStatusDirty = true;
        });
    }

    void OnManualCancel()
    {
        if (_orderTicket == null || _host.Lanes == null) { _orderTicket?.SetStatus("not connected"); return; }
        string oid = !string.IsNullOrEmpty(_manualOrderId) ? _manualOrderId
                   : (_host.Panel.HasOrder ? _host.Panel.LatestOrder.OrderId : "");
        if (string.IsNullOrEmpty(oid)) { _orderTicket.SetStatus("no order to cancel"); return; }
        _orderTicket.SetStatus("cancel " + oid + "…");
        // ack-then-poll venue: PENDING_CANCEL = 取消受付（poll が終端 CANCELED を後追い・findings 0014）。
        _host.Lanes.SubmitCancelOrder(ManualVenue(), oid, res =>
        {
            _manualStatusLine = res.Success ? res.Status : ("ERR " + res.ErrorCode);
            _manualStatusDirty = true;
        });
    }

    // venue: the connected session id (poll-canonical) if any, else the configured fallback.
    string ManualVenue() => !string.IsNullOrEmpty(_host.Conn.VenueId) ? _host.Conn.VenueId : _venue;

    // instrument: the sidebar-focused symbol if any (shared SelectedSymbol), else universe[0]. Returns ""
    // when nothing is resolvable — the live-order path REFUSES rather than default to an arbitrary symbol.
    string ManualInstrument()
    {
        if (_footerSelected != null && _footerSelected.HasValue) return _footerSelected.Value;
        var ids = _scenario.Universe.Ids;
        if (ids != null && ids.Count > 0) return ids[0];
        return "";
    }

    // ── #23 re-home: secret modal (second password). The overlay drains keystrokes char-by-char; the
    // root routes them into the host SecretModalController and reads MaskedDisplay back. No plaintext
    // managed string is ever formed (the buffer lives only in the controller's zeroable char[]). ──
    void DriveSecretModal(bool newSecret)
    {
        SecretModalController modal = _host.Modal;
        if (newSecret && !modal.IsOpen)
        {
            modal.Open(_host.Panel.LatestSecretRequired, Time.realtimeSinceStartup);
            // Secret discipline: drop any focused uGUI InputField (e.g. the order qty field or the strategy
            // editor) so the device-level onTextInput keystrokes don't ALSO land in its plaintext .text.
            if (EventSystem.current != null) EventSystem.current.SetSelectedGameObject(null);
        }
        if (modal.IsOpen && modal.TickExpire(Time.realtimeSinceStartup))   // 25s absolute timeout (zeroizes)
            _menuBarView?.ShowMessage("secret modal timed out (25s) — reconnect to retry.");
        if (modal.IsOpen != _secretModalOpenPrev)
        {
            _host.Coord.SetSecretModalOpen(modal.IsOpen);   // hold off logout while awaiting the secret
            _secretModalOpenPrev = modal.IsOpen;
        }
        if (_secretOverlay != null)
        {
            _secretOverlay.SetVisible(modal.IsOpen);
            if (modal.IsOpen) _secretOverlay.SetMasked(modal.MaskedDisplay);
        }
    }

    void OnSecretChar(char c) => _host.Modal.AppendChar(c);
    void OnSecretBackspace() => _host.Modal.Backspace();

    void SubmitSecret()
    {
        SecretModalController modal = _host.Modal;
        // Use the request the modal OPENED with (not the newest LatestSecretRequired), so a second
        // SecretRequired arriving mid-entry can't make us submit these keystrokes against the wrong id.
        string reqId = modal.RequestId;
        char[] payload = modal.Submit();        // one-shot; controller zeroizes its own copy
        if (payload == null) return;
        _host.Coord.SetSecretModalOpen(false);
        _secretModalOpenPrev = false;
        // Zeroize the plaintext if the lane is unavailable (else the secret char[] would linger un-cleared —
        // the lane is the only consumer that zeroizes it, mirroring the retired path's contract).
        if (_host.Lanes != null) _host.Lanes.SubmitSecret(reqId, payload, _ => { });
        else Array.Clear(payload, 0, payload.Length);
    }

    void CancelSecret()
    {
        _host.Modal.Cancel();
        _host.Coord.SetSecretModalOpen(false);
        _secretModalOpenPrev = false;
    }

    // ── #23 re-home: venue connect SEAM (findings 0014 RH5). The mainline Venue submenu UI that drives
    // this is #42; the #23 root-based HITL harness invokes it for the demo roundtrip. It REUSES the
    // durable VenueMenuViewModel (request build) + host.VenueLogin (login → LiveManual), never
    // reimplementing the retired ProductionLiveShell.ConnectEnv logic. The login ack is marshalled to
    // Conn on the main thread (Update). ──
    // Connect the CONFIGURED venue (the one the server was built with — connecting any other would hit
    // VENUE_MISMATCH). The env hint comes from the durable VenueMenuViewModel; MOCK uses the credential-
    // less "env" source. This is the only connect entry the HITL harness needs — there are no per-variant
    // buttons, so the harness can't drive a venue the server isn't configured for.
    public void ConnectConfigured()
    {
        if (!CanConnectConfigured()) return;
        string env = _venueMenu.EnvironmentHintFor(_venue);
        VenueConnectRequest req = _venueMenu.BuildConnectRequest(_venue, env);
        if (_venue == "MOCK") req.CredentialsSource = "env";   // credential-less dev venue (no prompt subprocess)
        _host.VenueLogin(req.Venue, req.CredentialsSource, req.EnvironmentHint, (ok, ec) =>
        {
            _loginAckOk = ok; _loginAckEc = ec ?? ""; _loginAckPending = true;   // marshalled to Conn in Update
        });
    }

    // Gate for the harness connect affordance. MOCK is the credential-less dev venue (always connectable
    // when ready); real venues defer to the durable CanConnectEnv, which greys out *prod* unless the
    // matching *_ALLOW_PROD flag is set (the #42 prod-safety parity, not regressed by the harness).
    public bool CanConnectConfigured()
    {
        if (!_isOwner || !_host.ServerReady || _host.Conn.IsConnected || _host.LoginRunning) return false;
        if (_venue == "MOCK") return true;
        return _venueMenu.CanConnectEnv(_venue, _venueMenu.EnvironmentHintFor(_venue));
    }

    // Focus the HITL target instrument so the manual Order ticket (ManualInstrument → SelectedSymbol) and
    // the chart/depth point at it — the harness calls this so its configured instrument is actually used,
    // not merely displayed.
    public void FocusInstrument(string iid)
    {
        if (!string.IsNullOrEmpty(iid)) _footerSelected.Set(iid);
    }

    // LIVE_VENUE selects the live venue the server is built for; default MOCK. Whitelisted so a typo can't
    // build a server for an unknown venue (it falls back to MOCK rather than failing opaquely).
    static string ResolveLiveVenue()
    {
        string v = (EnvConfig.Get("LIVE_VENUE", "MOCK") ?? "MOCK").Trim().ToUpperInvariant();
        return (v == "TACHIBANA" || v == "KABU") ? v : "MOCK";
    }

    // Read-only seams the root-based HITL harness observes (connect affordance gating + badge readout).
    public bool IsPythonOwner => _isOwner;
    public bool ServerReady => _host.ServerReady;
    public bool VenueConnected => _host.Conn.IsConnected;
    public string VenueId => _host.Conn.VenueId;
    public string ConfiguredVenue => _venue;

    // ── #23 re-home: tile formatters (mirror the retired ProductionLiveShell.DrawPanels content) ──
    // #61 BuyingPower base panel (no #23 formatter — BuyingPower was not one of the 3 re-homed tiles).
    static string FormatBuyingPower(LivePanelViewModel vm)
    {
        if (vm.HasAccount)
        {
            var sb = new StringBuilder();
            sb.Append("bp=").Append(vm.LatestAccount.BuyingPower).Append("  cash=").Append(vm.LatestAccount.Cash);
            return sb.ToString();
        }
        return "(no account snapshot)";
    }

    static string FormatOrders(LivePanelViewModel vm)
    {
        var sb = new StringBuilder();
        if (vm.HasOrder)
        {
            LiveOrderEvent o = vm.LatestOrder;
            sb.Append(o.ClientOrderId).Append("  ").Append(o.Status)
              .Append("  filled=").Append(o.FilledQty).Append('@').Append(o.AvgPrice).Append('\n');
        }
        else sb.Append("(none)\n");
        sb.Append("filled-order count: ").Append(vm.FilledOrderCount);
        return sb.ToString();
    }

    static string FormatPositions(LivePanelViewModel vm)
    {
        if (vm.HasAccount && vm.LatestAccount.Positions != null && vm.LatestAccount.Positions.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (LivePosition p in vm.LatestAccount.Positions)
                sb.Append(p.symbol).Append("  qty=").Append(p.qty).Append("  avg=").Append(p.avg_price)
                  .Append("  uPnL=").Append(p.unrealized_pnl).Append('\n');
            sb.Append("cash=").Append(vm.LatestAccount.Cash).Append("  bp=").Append(vm.LatestAccount.BuyingPower);
            return sb.ToString();
        }
        return "(flat / no account snapshot)";
    }

    static string FormatRunResult(LivePanelViewModel vm)
    {
        var sb = new StringBuilder();
        if (vm.HasLifecycle) sb.Append("run=").Append(vm.LatestLifecycle.RunId).Append("  ").Append(vm.LatestLifecycle.Status).Append('\n');
        if (vm.HasTelemetry)
        {
            LiveTelemetryEvent t = vm.LatestTelemetry;
            sb.Append("realized=").Append(t.RealizedPnl).Append("  unrealized=").Append(t.UnrealizedPnl)
              .Append("  orders=").Append(t.OrderCount).Append("  fills=").Append(t.FillCount);
        }
        if (!vm.HasLifecycle && !vm.HasTelemetry) sb.Append("(no run)");
        return sb.ToString();
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

        // #61 base retile: the poll is the mode SoT (DisplayMode). When the base SHAPE flips
        // (Replay⇄Live — LiveManual⇄LiveAuto is the same Live shape, so no-op), retile base only;
        // chart tiles keep identity. Cheap bool compare each frame; SyncBaseTilesToMode only on flip.
        bool live = HakoniwaBaseTiles.IsLiveShape(_footerMode.DisplayMode);
        if (live != _baseLive) SyncBaseTilesToMode(live);   // base retile; SyncBaseTilesToMode repaints on the flip

        // base panel content is refreshed by RefreshLiveTiles (Update, before DriveFooter) — gated on the
        // VM AppliedCount so idle frames cost one long compare. A shape flip force-repaints inside
        // SyncBaseTilesToMode (above).

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
        ReassertBaseAfterRestore();   // #61: keep base canonical + visible + before charts (collision/legacy-safe)
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
