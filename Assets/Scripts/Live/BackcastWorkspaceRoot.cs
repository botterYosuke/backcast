// BackcastWorkspaceRoot.cs — issue #59 "本線ワークスペース合体 scene" (production composition root)
//
// The scene-authored Backcast workspace root (CONTEXT "Backcast workspace root"; ADR-0009): the
// SINGLE normal Play entry that composes every UI surface — menu bar / sidebar / center infinite-
// canvas workspace (Hakoniwa [startup, chart] + a floating Strategy Editor) / footer — into one
// screen, and is the single Python owner. Orchestration is NOT here: the durable WorkspaceEngineHost
// owns the engine lifecycle / launcher / poll / force-stop teardown (findings 0025 §5); this root
// WIRES the authored Views to the Host and the layout store, and drives ChartView / the footer from the
// Host's published state. (The throwaway ScenarioStartupHitlHarness it once demoted was retired in #76
// S6b-β-clean along with the replay transport it drove.)
//
// SINGLE PLAY-OWNER (findings 0025 §7): the root claims Python only when it is the configured owner,
// not headless, and nobody else holds the interpreter (WorkspaceOwnership.ShouldClaim). To run a
// per-part Python HITL in isolation, DISABLE this root's GameObject before Play; while the root runs,
// per-part HITLs see PythonEngine.IsInitialized and refuse (they never fight over the engine).
//
// _ownPlay gates ONLY the Python auto-start, never the UI authoring/build (owner 2026-06-15).
// isBatchMode suppresses Python init (the headless compile gate never inits Python or renders).
//
// LAYOUT (findings 0025 §8, ADR-0003; #69 multi-document, findings 0048): 4 dimensions (Hakoniwa
// panels / canvas pan+zoom / floating windows / Strategy Editor open file) round-trip through the
// "layout" key of the OPEN document's <strategy>.json (LayoutSidecarStore — coexisting with the
// engine's "scenario" key, never clobbering it). The document is the (<strategy>.py, <strategy>.json)
// pair; _currentLayoutPath is the open .py (the Save target / resume anchor). Restore order
// canvas→Hakoniwa→floating→editor; the scene-authored editor window is ADOPTED (never
// destroyed+respawned) and only ADDITIONAL saved windows are spawned. Save triggers: File→Save /
// Save As (native picker), OnApplicationQuit, OnDestroy — quit autosaves into the open document, and
// the last document is remembered across launches via a PlayerPrefs pointer (B2: no global layout file).

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
    const string WINDOW_ID = "strategy_editor:region_001";   // adopted scene-authored cell window (region_001 shell)
    const string ORDER_WINDOW_ID = "order:region_001";   // #23 re-home: singleton Order ticket window
    // #81 (ADR-0013 / findings 0050): the provider registry key is the LOGICAL notebook id — the thing
    // that supplies the `.py` to run is the NOTEBOOK aggregate, NOT a physical window. `region_001` stays
    // a physical window id (adopt / _editors / reveal); the run path resolves the notebook under THIS key.
    const string NOTEBOOK_ID = "strategy_editor:notebook";

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
    // #76 S6b-β-clean U1: the title-bar Run button's readiness brain (pure; non-mutating mirror of OnRun's gates).
    readonly RunReadinessViewModel _runReadiness = new RunReadinessViewModel();

    // #41 instruments universe prune: change-gated, on-demand prune of universe-outside instruments.
    // _pruneSource is a null source today (no live fetch / catalog producer yet — prune stays dormant
    // in production until the real sources land, same shape-now/supply-later split as #31). The
    // driver re-evaluates only when an input changed and prunes via InstrumentRegistry.PruneRetain;
    // its Changed event drives SyncChartTilesToUniverse (downstream reflect), so the driver does NOT
    // subscribe to Changed (no self-reentry). See docs/findings/0041.
    readonly IUniversePruneSource _pruneSource = new NullUniversePruneSource();
    UniversePruneDriver _pruneDriver;
    readonly StrategyProviderRegistry _registry = new StrategyProviderRegistry();

    // #81 cell-as-floating-window (ADR-0013): the notebook aggregate (the single `.py`/dirty/Save/Open
    // and the sole IStrategyFileProvider), the coordinator that turns add/delete/open/save into window
    // lifecycle, and the marimo synthesis seam (pythonnet via the host). Built in BuildWorkspace.
    IMarimoSynthesizer _synth;
    MarimoNotebookDocument _notebook;
    NotebookCellCoordinator _coordinator;
    FloatingWindowCatalog _catalog;
    Vector2 _cellWindowSize = new Vector2(520f, 380f);   // resolved from the strategy_editor spec at build

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
    // the base retile (SyncBaseTilesToMode) and restore (ApplyProfileOrder) use to
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
    // #62 per-mode layout profile (findings 0029): Replay and Live each remember their own Hakoniwa tile
    // order. Stashed on every flip (the OLD mode, before switching) and on save (the active mode) — TTWR
    // reconcile_hakoniwa_tiles / build_hakoniwa_snapshot parity; replaced on restore by
    // HakoniwaLayoutProfiles.FromDocument (per-mode, or seeded from the legacy single `panels`). _baseLive
    // is the current-shape SoT (TTWR profiles.current is owned HERE, not duplicated into the profiles).
    HakoniwaLayoutProfiles _profiles = new HakoniwaLayoutProfiles();
    ScenarioStartupTile _tile;
    WorkspaceFooterView _footer;
    StrategyEditorRunButton _editorRunButton;   // #76 S6b-β-clean U1: Run on the adopted editor title bar
    MenuBarViewModel _menuBar;
    VenueMenuViewModel _venueMenu;
    UniverseSidebarController _sidebarCtrl;
    readonly Dictionary<string, StrategyEditorView> _editors = new Dictionary<string, StrategyEditorView>();
    Font _font;

    // ── #69 multi-document layout surface (findings 0048) ──
    // The document is the (<strategy>.py, <strategy>.json) pair; the .json carries the engine's
    // "scenario" key (#29) AND the Unity "layout" key (LayoutSidecarStore), never clobbering each
    // other. _currentLayoutPath is the OPEN document's .py (TTWR buffer.original_path): the Save
    // target and resume anchor. "" = untitled (no document) -> File→Save delegates to Save As.
    string _currentLayoutPath = "";
    IFileDialog _fileDialog = PlatformFileDialog.Default();   // native picker (Win32/Mac per OS); AFK injects a StubFileDialog
    const string ResumeKey = "backcast.lastDocument";  // PlayerPrefs resume pointer (B2: no global layout file)

    // #69 AFK seam: a probe injects a StubFileDialog so Save As / Open round-trips run headless
    // (the C# equivalent of TTWR's PendingFileDialog.inject_resolved).
    public void SetFileDialog(IFileDialog dialog) { if (dialog != null) _fileDialog = dialog; }

    // #81 AFK seam: a probe injects a fake IMarimoSynthesizer (Python-free, round-trip-faithful) BEFORE
    // compose so the cell model (synthesise/decompose/save/open) runs headless without pythonnet — the
    // shared-golden discipline (the fake satisfies the same contract layer 2/3 assert, findings 0050).
    // Production leaves this null and BuildWorkspace defaults to the pythonnet synthesizer.
    public void SetSynthesizer(IMarimoSynthesizer synthesizer) { if (synthesizer != null) _synth = synthesizer; }

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
    // #65: Replay base-panel payload-change gate (mirrors DriveDepthLadders' _lastDepthPayload) so the
    // per-frame Replay drive only JsonUtility-parses when the poll/summary string actually changed.
    // Seeded to the force sentinel so the first drive always renders (honest-empty before any poll).
    const string _replayForceSentinel = "force";
    string _lastReplayPortfolioPayload = _replayForceSentinel;
    string _lastReplaySummaryPayload = _replayForceSentinel;
    // Venue login ack crosses worker→main (host.VenueLogin onResult runs off-main); apply to Conn on main.
    volatile bool _loginAckPending;
    volatile bool _loginAckOk;
    volatile string _loginAckEc = "";

    // ── runtime state ──
    bool _isOwner;       // owns the Python interpreter this Play
    bool _built;         // BuildWorkspace ran (this root is the active layout owner) — independent of Python
    readonly OnceGate _teardownGate = new OnceGate();
    string _lastPayload;
    bool _errLogged;

    // ── #39: footer mode segment (Replay/Manual/Auto), wired to the host live seam. #76 S6b-β-clean U4:
    // the LiveAuto ▶ start is retired from the footer (Live start re-wiring is a separate epic); the
    // mode VMs stay to track Live execution mode + the running-LiveAuto state the mode switch observes. ──
    FooterModeViewModel _footerMode;
    LiveAutoTransportViewModel _footerAuto;
    readonly SelectedSymbol _footerSelected = new SelectedSymbol();
    string _venue = "MOCK";                 // live venue id; resolved from LIVE_VENUE env in Awake (default MOCK)
    volatile bool _footerModeRejected;      // worker→main: a SetExecutionMode / stop-then-switch failed
    volatile int _venueLoginResult;         // worker→main: 0 none / 1 ok / 2 fail (venue_login)
    volatile string _venueLoginError;       // worker→main: error_code on a failed venue login
    volatile bool _venueLogoutFailed;       // worker→main: venue_logout returned failure
    string _lastFooterSig = "";
    string _lastRunReadySig;                // #76 U1: change-gate the title-bar Run button Refresh
    string _lastFooterPoll = "";            // dedup the footer-mode ApplyPoll (avoid per-frame JSON parse)

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

        ResumeLastDocumentOrDefault();   // #69 (B2): re-open the last document or start untitled+default
    }

    // #78: the universe is NO LONGER seeded from an env-default .py. There is ONE strategy source —
    // the editor (findings 0044) — known only after RestoreEditors, so the real seed runs in
    // SeedScenarioFromEditor at the END of ApplyLayout. ResolvePaths is retained as the "compose root
    // headlessly" seam many probes drive (ResolvePaths + BuildWorkspace), and seeds from the editor if
    // one is already bound — a no-op when unbound (fresh install / pre-restore), which is the #78
    // "未ロード→走らない" guarantee. No env-default path remains.
    void ResolvePaths()
    {
        SeedScenarioFromEditor();
    }

    // #78: the run layer's single strategy source — the editor, resolved LIVE through the registry by
    // window id each call (findings 0044 §2-1). One cached immutable adapter so Run / LiveAuto / sidebar
    // writeback / seed can never target different windows; a future multi-editor active-pick repoints
    // this ONE field (the registry is readonly-initialised and WINDOW_ID is const, so lazy-init is safe
    // from any seam, including the probes' reflective ResolvePaths-before-BuildWorkspace).
    RegistryStrategyFileProvider _editorFileProvider;
    RegistryStrategyFileProvider EditorFileProvider =>
        _editorFileProvider ??= new RegistryStrategyFileProvider(_registry, NOTEBOOK_ID);   // #81: the notebook supplies the .py

    // Seed the scenario panel from the editor's CURRENT strategy .py (sidecar ?? inline fallback —
    // the #66 mechanism, re-homed from env-default to the LOADED editor, findings 0044 §2-3). When the
    // editor is unbound (no restore / fresh install) the registry provider returns false and we seed
    // NOTHING — universe stays empty and Run is blocked, which is the #78 "未ロード→走らない" guarantee.
    void SeedScenarioFromEditor()
    {
        if (!EditorFileProvider.TryGetStrategyFile(out string strategyPath)) return;

        ScenarioSnapshot inlineFallback = ScenarioInlineReader.Read(strategyPath, out var inlineStatus);
        // findings 0051 D3: a CORRUPT scenario sidecar must NOT crash File→Open — read it TOLERANTLY and
        // degrade to the inline fallback (or empty) instead of throwing out of the open. The sidecar still
        // wins over inline when readable. The Run gate then blocks on the empty universe; the user opens
        // the .py to FIX the sidecar.
        bool sidecarOk = ScenarioSidecarStore.TryReadScenario(strategyPath, out var sidecar);
        _scenario.PopulateFrom(sidecar ?? inlineFallback, DateTime.Now);
        // Surface the ACTUAL state: a corrupt sidecar is its own message (the inline fallback may still
        // have populated the universe, so don't claim "to set the universe"); an unreadable inline with
        // no sidecar keeps the original guidance.
        if (!sidecarOk)
            _menuBarView?.ShowMessage("scenario sidecar unreadable — fix " + Path.GetFileName(ScenarioSidecarStore.SidecarPathFor(strategyPath)));
        else if (inlineStatus == ScenarioReadStatus.Unparseable)
            _menuBarView?.ShowMessage("strategy SCENARIO unreadable — save a scenario sidecar to set the universe");
    }

    // #78: the canonical "(re)bind editor .py → re-seed scenario/universe" tail, shared by ApplyLayout's
    // post-RestoreEditors seed, File→Open (OnFileOpen), File→Save and boot's canonical/resume open. ORDER
    // matters (findings 0025 §12): scenario first; then the Startup tile fields (written ONLY here — they
    // have NO Changed event, so a stale tile would render blank while Run uses the seeded Params, a WYSIWYR
    // break); then the sidebar writeback prime (so _lastFlushed matches the just-seeded universe — else
    // a later sidebar edit diffs against a stale set → phantom-id hazard).
    void ReseedFromEditor()
    {
        SeedScenarioFromEditor();
        _tile?.SyncFieldsFromController();
        _sidebarCtrl?.PrimeWritebackFromCurrent();
    }

    // ---- compose the authored Views into live widgets (existing builders fill inner elements) ----
    void BuildWorkspace()
    {
        // center workspace: infinite canvas (Content) hosts HakoniwaRoot + FloatingWindowLayer (P-all).
        _canvas = new InfiniteCanvasController(_content);
        if (_inputSurface != null) _inputSurface.Initialize(_canvas, _viewport);

        _catalog = FloatingWindowCatalog.Default();
        _windows = new FloatingWindowController(_floatingLayer, _catalog, BuildEditorWindowFrame);
        if (_catalog.TryGet(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, out var cellSpec)) _cellWindowSize = cellSpec.defaultSize;

        // Hakoniwa [startup slot0, chart slot1] on Content (CONTEXT: Startup = PanelKind::Startup slot 0).
        // Each tile gets a panel bg + a header bar so it reads as a DISTINCT box against the infinite-
        // space field (else tile bg ≈ field and the grid is invisible), and HakoniwaTileHeaderInput
        // makes header-drag SWAP while body-drag falls through to pan. Mirrors HakoniwaHitlHarness's
        // durable construction (HakoniwaController only lays cells; the caller owns tile chrome).
        EnsureRootImage(_hakoniwaRoot, HAKO_ROOT_COLOR);

        RectTransform startupBody = BuildTileChrome(_startupTile, "startup", out HakoniwaTileHeaderInput startupHeader);
        _tile = new ScenarioStartupTile(_scenario, _font);   // #76 S6b-β-clean U5: scenario-editing-only (Run moved to the editor title bar)
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
        _pruneDriver = new UniversePruneDriver(_scenario.Universe);   // #41: prune driver over the same SoT

        // #81 cell-as-floating-window (ADR-0013): the notebook aggregate is the single `.py` owner and
        // the sole IStrategyFileProvider (registered under the LOGICAL notebook id, NOT a window id —
        // the run path resolves the notebook, not region_001). The production synthesiser calls marimo
        // through the host (single Python owner, ADR-0009); it touches Python only at Save/Open, after
        // InitializePython has run in Awake. The coordinator turns add/delete/open/save into window
        // lifecycle and is driven by the root's delegates (viewFor / viewport anchor / X callback).
        _synth ??= new PythonnetMarimoSynthesizer(_host);   // null unless a probe injected a fake (SetSynthesizer)
        _notebook = new MarimoNotebookDocument(_synth);
        _registry.Register(NOTEBOOK_ID, _notebook);

        // adopt the scene-authored Strategy Editor window = the never-Destroy region_001 cell shell
        // (findings 0025 §8). Its editor view is a Cell fragment view (unbound until the coordinator
        // binds cell 0 in ResumeLastDocumentOrDefault). The X button deletes the cell (coordinator).
        _windows.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, WINDOW_ID, _strategyEditorWindow);
        if (_strategyEditorTitleInput != null)
        {
            _strategyEditorTitleInput.Initialize(_windows, _canvas, _viewport, WINDOW_ID);
            // #76 S6b-β-clean U1: the SINGLE Run entry — a small ▶ Run on the adopted editor's title bar.
            // Run targets WHAT THE EDITOR SHOWS via EditorFileProvider — now the NOTEBOOK aggregate (#81),
            // resolved through the registry under NOTEBOOK_ID; the ▶ wiring is unchanged. Click → OnRun().
            _editorRunButton = new StrategyEditorRunButton(OnRun);
            _editorRunButton.Build((RectTransform)_strategyEditorTitleInput.transform, _font);
        }
        // #81: the adopted window's content is a Cell fragment view (no Document / no registry — the
        // notebook aggregate, registered above under NOTEBOOK_ID, is the sole provider).
        var editorView = StrategyEditorContentBuilder.Build(_strategyEditorBody, font: _font);
        if (editorView != null) _editors[WINDOW_ID] = editorView;

        _coordinator = new NotebookCellCoordinator(_notebook, _windows, ViewFor, SpawnAnchorTopLeft, _cellWindowSize);
        WireCellCloseButton(_strategyEditorWindow, WINDOW_ID);
        BuildAddCellButton();

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

        // #76 S6b-β-clean U4: workspace footer = mode segments (Replay/Manual/Auto) + status ONLY.
        // The replay transport controls (▶/⏸/step/stop/speed) are retired (ADR-0012 reactive model —
        // run→即完了); Run lives on the editor title bar (U1). The mode segments stay (Live execution
        // mode, NOT transport); _footerAuto drives the live-mode status line + the run/auto state the
        // mode switch and auto-replay observe (findings 0026 §4).
        _footerMode = new FooterModeViewModel();
        _footerAuto = new LiveAutoTransportViewModel(
            _host.Panel,                                              // run lifecycle authority
            EditorFileProvider,                                       // #78: run WHAT THE EDITOR SHOWS
            _footerSelected,
            () => new List<string>(_scenario.Universe.Ids),          // scenario run universe
            () => !string.IsNullOrEmpty(_host.Conn.VenueId) ? _host.Conn.VenueId : _venue);
        _footer = new WorkspaceFooterView(_footerMode, _footerAuto, OnFooterMode, _font);
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
            _menuBarView.Bind(_menuBar, OnFileNew, OnFileOpen, OnFileSave, OnFileSaveAs,
                OnVenueConnect, OnVenueDisconnect,
                () => _host.ServerReady && !_host.TeardownComplete,   // connect-ready gate
                () => _footerMode.DisplayMode,                        // bar mode badge
                _venue,                                               // "MOCK" → dev connect item (editor only)
                _font);                                               // uGUI font (#77)

        // sidebar (V-host): reuse the durable controller brain. The sidebar edits the SAME universe
        // SoT the startup tile edits and OnRun reads (_scenario.Universe) — "one universe per workspace"
        // (#31 designed controller.Registry to be host-wired; the cutover shell wires it here, #59).
        // SelectedSymbol is the SHARED _footerSelected so a sidebar instrument selection reaches the
        // footer's LiveAuto start (#39; else _footerSelected stays empty and LiveAuto always uses
        // universe[0] regardless of what the user picked).
        // The candidate source is still a mock (real supply = #46 kabu list / #41 prune / DuckDB).
        var provider = new MockAvailableInstrumentsProvider(new[] { "1301.TSE", "6758.TSE", "7203.TSE", "8918.TSE", "9432.TSE", "9984.TSE" });
        _sidebarCtrl = new UniverseSidebarController(_scenario.Universe, _footerSelected, new UniverseWriteback(), provider);
        // #78: at BuildWorkspace the editor is still UNBOUND (the .py binds later in RestoreEditors), so
        // the universe is EMPTY here — this prime is against the empty set. The REAL writeback prime that
        // matches the seeded universe happens at the END of ApplyLayout, right after SeedScenarioFromEditor
        // (do NOT drop that re-prime: without it a later sidebar edit diffs against this stale empty set →
        // phantom-id hazard, findings 0025 §12). Kept here so a no-restore compose path still has a primed
        // writeback. The restored ids are not an unsaved edit (#31 D4).
        _sidebarCtrl.PrimeWritebackFromCurrent();
        if (_sidebarView != null) _sidebarView.Bind(_sidebarCtrl, EditorFileProvider, _font);   // #78: editor's .py sidecar; #77: uGUI font
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
            // #81: a spawned cell window — a Cell fragment view (the coordinator binds its cell right
            // after the spawn) + a title-bar X wired to delete that cell.
            var view = StrategyEditorContentBuilder.Build(body, font: _font);
            if (view != null) _editors[id] = view;
            WireCellCloseButton(root, id);
        }
        return root;
    }

    // ---- #81 cell-as-floating-window: coordinator wiring (delegates the root injects) ----

    // regionId -> its editor view (null-tolerant: a window the factory hasn't built yet, or a torn-down
    // window's stale/destroyed entry, returns null and the coordinator skips the bind).
    StrategyEditorView ViewFor(string regionId)
        => _editors.TryGetValue(regionId, out var v) && v != null ? v : null;

    // The viewport-centre canvas-LOGICAL point = the next spawn anchor (used verbatim as a top-left;
    // SpawnPlacement cascades off it). CanvasView.panX/panY is exactly that point (findings 0006 §2).
    Vector2 SpawnAnchorTopLeft()
    {
        var v = _canvas != null ? _canvas.CaptureView() : null;
        return v != null ? new Vector2(v.panX, v.panY) : Vector2.zero;
    }

    // Find-or-create the title-bar X on a cell window and wire it to delete that cell. Idempotent, so
    // both the adopted scene window and a spawned window get a consistent X (StrategyEditorWindowFrame).
    void WireCellCloseButton(RectTransform windowRoot, string regionId)
    {
        var btn = StrategyEditorWindowFrame.EnsureCloseButton(windowRoot, _font);
        if (btn == null) return;
        btn.onClick.RemoveAllListeners();
        btn.onClick.AddListener(() => OnDeleteCell(regionId));
    }

    void OnDeleteCell(string regionId)
    {
        if (_coordinator == null) return;
        if (!_coordinator.DeleteCell(regionId))
            _menuBarView?.ShowMessage("Delete cell: a notebook keeps at least one cell.");
    }

    // The screen-fixed "+ Python cell" overlay (ONE button, appends an empty cell). Owner override
    // (2026-06-19): anchored BOTTOM-RIGHT, not marimo's top-centre (edit-app.tsx:454) — a deliberate
    // divergence from TTWR/marimo parity at the owner's request. Its own ScreenSpaceOverlay canvas keeps
    // it screen-fixed (it does NOT pan with the canvas) and above Content but below the secret modal
    // (z帯, findings 0050).
    void BuildAddCellButton()
    {
        var overlayGo = new GameObject("AddCellOverlay", typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
        overlayGo.transform.SetParent(transform, false);
        var canvas = overlayGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 200;   // above the scene canvas (menu/footer @0), below the secret modal

        var btnGo = new GameObject("AddCellButton", typeof(RectTransform), typeof(Image), typeof(Button));
        var rt = (RectTransform)btnGo.transform;
        rt.SetParent(overlayGo.transform, false);
        rt.anchorMin = new Vector2(1f, 0f); rt.anchorMax = new Vector2(1f, 0f); rt.pivot = new Vector2(1f, 0f);
        rt.sizeDelta = new Vector2(170f, 30f);
        rt.anchoredPosition = new Vector2(-20f, 56f);   // bottom-right; y = footer bar (40px, scene-authored) + 16px gap
        btnGo.GetComponent<Image>().color = new Color(0.20f, 0.50f, 0.35f, 0.95f);

        var lblGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        var lrt = (RectTransform)lblGo.transform;
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        var t = lblGo.GetComponent<Text>();
        t.font = _font;
        t.text = "+ Python cell";
        t.alignment = TextAnchor.MiddleCenter;
        t.color = Color.white;
        t.fontSize = 14;

        btnGo.GetComponent<Button>().onClick.AddListener(OnAddCell);
    }

    void OnAddCell() => _coordinator?.AddCell();

    // cellPositions <-> Vector2 list converters (the layout sidecar POCO <-> the coordinator's runtime form).
    static List<Vector2> ToVectors(List<CellPosition> positions)
    {
        if (positions == null) return null;
        var list = new List<Vector2>(positions.Count);
        foreach (var p in positions) list.Add(p != null ? new Vector2(p.x, p.y) : Vector2.zero);
        return list;
    }

    static List<CellPosition> ToCellPositions(List<Vector2> positions)
    {
        var list = new List<CellPosition>(positions != null ? positions.Count : 0);
        if (positions != null)
            foreach (var p in positions) list.Add(new CellPosition(p.x, p.y));
        return list;
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
        // #62 reconcile parity (TTWR reconcile_hakoniwa_tiles): stash the CURRENT layout into the OLD
        // mode's profile BEFORE changing anything, switch the base membership, then load the NEW mode's
        // profile (validated honor / canonical) via ApplyProfileOrder.
        StashActiveProfile();

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

        ApplyProfileOrder(live);                        // load new mode: chart order + [base…, chart…] honored/canonical
        ApplyBoxGrow();                                 // grid = n_base + n_chart (derived box-grow, #60)
        _baseLive = live;
        ForceRefreshLiveTiles();                        // shape flip: repaint the base panels now (#23 wiring)
    }

    // Apply a mode's per-mode profile to the live controller (#62, findings 0029 §2/§3). The unified
    // path for BOTH a mode flip (SyncBaseTilesToMode) and a disk restore (ApplyLayout) — TTWR
    // reconcile/apply_hakoniwa_restore_resources parity:
    //   1. if the mode has a stored profile, Apply it (restores the per-mode CHART order; Apply's
    //      tolerance reconciles universe membership — stale ids skipped, new charts appended);
    //   2. Reorder by BaseOrderForMode(live): a VALID profile's base order is honored (user header-drag
    //      swaps remembered per mode), an invalid/legacy/seeded one falls to canonical Kinds(live) —
    //      the [base…, chart…] invariant and the #61 collision-safe behavior (LayoutDocument.Default() /
    //      #60-era sidecars have a mismatched base set → canonical), generalized to validated honor;
    //   3. base tiles are not closeable → force them visible (a stale visible=false must not hide one).
    // This REPLACES #61's ReassertBaseAfterRestore (always-canonical) — empty profiles → canonical, so
    // the #61 collision regression (HakoniwaBaseModeProbe Section4) still holds.
    void ApplyProfileOrder(bool live)
    {
        var stored = _profiles.Get(live);
        if (stored != null)
            _hako.Apply(new LayoutDocument { version = LayoutDocument.CURRENT_VERSION, panels = stored });
        _hako.Reorder(_profiles.BaseOrderForMode(live));
        foreach (var id in HakoniwaBaseTiles.Kinds(live))
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

        // #76 S6b-β-clean U1/U5: the title-bar Run button shows the steady block reason (RunReadiness),
        // and is only clickable when ready — so these click-time gates are defensive. Surface a click-
        // time failure (a commit I/O exception, or a race that flipped readiness) on the menu notice line
        // (the tile is scenario-editing-only now; it no longer carries a Run message).
        RunGateResult gate;
        try { gate = _scenario.TryStartRun(EditorFileProvider); }
        catch (Exception e) { _menuBarView?.ShowMessage("Could not save scenario: " + e.Message); Debug.LogError("[BackcastWorkspaceRoot] commit failed: " + e); return; }
        if (!gate.IsReady) { _menuBarView?.ShowMessage(gate.Message); Debug.LogWarning("[BackcastWorkspaceRoot] run blocked: " + gate.Message); return; }

        // Commit just wrote the sidecar to the CURRENT universe; re-prime the sidebar writeback's
        // _lastFlushed to match, so a later sidebar ×/add (the only path that flushes) diffs against
        // what is actually on disk. Without this, a tile-added id committed at Run leaves _lastFlushed
        // stale -> the next sidebar edit sees no diff -> Flush SKIPS -> phantom id persists on disk
        // (findings 0025 §12, Finding 1).
        _sidebarCtrl.PrimeWritebackFromCurrent();

        if (!_isOwner) { _menuBarView?.ShowMessage(RunReadinessViewModel.NotOwner); return; }

        _chartRendered.Clear();
        _depthRendered.Clear();        // #57: a fresh run invalidates the cached per-id ladder renders
        _lastDepthPayload = null;
        _lastPayload = null;
        _errLogged = false;

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
        // #76 S6b-β-clean U1: drive the title-bar Run button readiness EVERY frame, BEFORE the owner
        // guard — a non-owner session must still see Run greyed with "Not the Python owner". strategyReady
        // = the provider's 5-condition supplyable (non-mutating); scenarioValid = no validation errors.
        // Only Refresh the uGUI button when the readiness actually changed (change-gated like DriveFooter's
        // _lastFooterSig), so a steady workspace costs the read but not the per-frame color/text churn.
        _runReadiness.Evaluate(_isOwner, _host.IsRunning,
            EditorFileProvider.TryGetStrategyFile(out _), !_scenario.Validate().Any);
        string readySig = _runReadiness.CanRun + "|" + _runReadiness.BlockReason;
        if (readySig != _lastRunReadySig) { _lastRunReadySig = readySig; _editorRunButton?.Refresh(_runReadiness); }

        if (!_isOwner) return;

        // Surface a run failure on-screen (the footer no longer carries a replay phase, and the tile is
        // scenario-only): the menu notice line shows the error so a failed run is not Console-only.
        string err = _host.StartError;
        if (err != null && !_errLogged) { Debug.LogError("[BackcastWorkspaceRoot] FAIL: " + err); _menuBarView?.ShowMessage("Run failed: " + err); _errLogged = true; }

        string state = _host.LatestStateJson;
        if (state != null && state != _lastPayload)
        {
            _lastPayload = state;

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
        DrivePrune();          // #41: after DriveFooter (fresh DisplayMode); before depth so a pruned chart tile propagates first
        DriveDepthLadders();   // #57: after DriveFooter so _footerMode.DisplayMode is the fresh mode
    }

    // #41 instruments universe prune: assemble the gate inputs fresh from the current mode + prune
    // source + venue state + scenario.end (on-demand re-resolve, no cache), then let the change-gated
    // driver decide. Both Live segments (LiveManual/LiveAuto) fold to the Live gate via IsLiveShape.
    // VenueState is the raw poll state — the gate applies its OWN stricter {CONNECTED,SUBSCRIBED}
    // predicate (NOT Conn.IsConnected, which includes RECONNECTING for the badge band). findings 0041.
    void DrivePrune()
    {
        if (_pruneDriver == null) return;

        _pruneSource.LiveSnapshot(out var source, out var status, out var liveIds);
        string end = _scenario.Params != null ? _scenario.Params.End : null;
        AvailableInstrumentsResult catalog = _pruneSource.ReplayCatalog(end);

        var inputs = new UniversePruneInputs
        {
            Mode = HakoniwaBaseTiles.IsLiveShape(_footerMode.DisplayMode)
                ? UniverseSourceMode.Live : UniverseSourceMode.Replay,
            LiveSource = source,
            LiveStatus = status,
            VenueState = _host.Conn.VenueState,
            LiveIds = liveIds,
            ScenarioEnd = end,
            ReplayStatus = catalog.Kind,
            ReplayIds = catalog.Ids,
        };
        _pruneDriver.Tick(inputs);
    }

    // ── #23 re-home: live data tiles (Orders / Positions / Run Result), fed by _host.Panel. Gate on
    // LivePanelViewModel.AppliedCount so the per-frame path costs one long compare (no string/StringBuilder
    // allocation) while the live session is idle — the formatters only run when an event was applied. ──
    void RefreshLiveTiles()
    {
        // #65: Replay drives the base panels from the get_portfolio_json poll, which the live
        // AppliedCount gate never observes — no live events drain in Replay, so AppliedCount is
        // frozen and the gate would render PushReplayTiles only once (at the shape flip), leaving the
        // panels stuck while the run streams. Bypass the gate in Replay and drive every frame;
        // PushReplayTiles dedups on the poll payload (one string compare) and ShowText dedups the
        // text write, so a steady snapshot is cheap. Live keeps the AppliedCount gate.
        if (!_baseLive) { PushReplayTiles(); return; }
        long applied = _host.Panel.AppliedCount;
        if (applied == _lastPanelApplied) return;
        _lastPanelApplied = applied;
        PushLiveTiles();
    }

    // Repaint all base panel tiles unconditionally (bypass the AppliedCount gate). #61 base retile uses
    // this on a Replay⇄Live shape flip so the panels redraw immediately (the gate would otherwise skip
    // an unchanged AppliedCount). _buyingPowerView is the #61 4th panel; the other three are #23 tiles.
    void ForceRefreshLiveTiles()
    {
        // #65: a flip must repaint NOW even if the Replay poll payload is unchanged (Live→Replay must
        // replace live figures; Replay→Live must drop replay ones). Reset the payload gate to the force
        // sentinel so the next PushReplayTiles always re-decodes/renders.
        _lastReplayPortfolioPayload = _replayForceSentinel;
        _lastReplaySummaryPayload = _replayForceSentinel;
        PushLiveTiles();
    }

    void PushLiveTiles()
    {
        // #65: in Replay the base panels render the real run's portfolio (get_portfolio_json poll),
        // not the monotonic live VM. Before the first poll / outside a run last_portfolio is null →
        // DecodePortfolio yields an empty snapshot, so we keep the #61 honest-empty "(no data)".
        if (!_baseLive)
        {
            PushReplayTiles();
            return;
        }
        LivePanelViewModel p = _host.Panel;
        _buyingPowerView?.Refresh(p);
        _ordersView?.Refresh(p);
        _positionsView?.Refresh(p);
        _runResultView?.Refresh(p);
    }

    // #65: Replay base-panel drive. Decodes the get_portfolio_json poll snapshot into the 4 tiles.
    // Portfolio absent (run not started / cleared) → honest-empty, preserving the #61 anti-stale-live
    // contract (a Live→Replay flip never leaves stale live figures on screen). RunResult is two-stage
    // (TTWR run_result_panel.rs): running view (counts + realized/unrealized) while the run streams,
    // full stats (fills/sharpe/dd + pnl/sortino) once the launcher captures summary_json.
    void PushReplayTiles()
    {
        string portfolioJson = _host.LatestPortfolioJson;
        string summaryJson = _host.RunSummaryJson;
        // payload-change gate: only decode+render when the poll snapshot OR the completion summary
        // actually changed, so the per-frame Replay drive doesn't JsonUtility-parse every frame. Both
        // are tracked because the running→full-stats switch (summary flips non-null) can land on a
        // frame where the portfolio string is unchanged.
        if (portfolioJson == _lastReplayPortfolioPayload && summaryJson == _lastReplaySummaryPayload)
            return;
        _lastReplayPortfolioPayload = portfolioJson;
        _lastReplaySummaryPayload = summaryJson;

        if (string.IsNullOrWhiteSpace(portfolioJson))
        {
            _buyingPowerView?.ShowReplayEmpty();
            _ordersView?.ShowReplayEmpty();
            _positionsView?.ShowReplayEmpty();
            _runResultView?.ShowReplayEmpty();
            return;
        }

        PortfolioSnapshot snap = ReplayPanelDecoder.DecodePortfolio(portfolioJson);
        _buyingPowerView?.ShowText(FormatReplayBuyingPower(snap));
        _ordersView?.ShowText(FormatReplayOrders(snap));
        _positionsView?.ShowText(FormatReplayPositions(snap));

        _runResultView?.ShowText(
            string.IsNullOrWhiteSpace(summaryJson)
                ? FormatReplayRunResultRunning(snap)
                : FormatReplayRunResultComplete(ReplayPanelDecoder.DecodeRunResult(summaryJson)));
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

    // ── #65 Replay base-panel formatters (decoded get_portfolio_json / summary_json) ──
    // Style mirrors the Live formatters above so the same tile reads consistently across modes.
    static string FormatReplayBuyingPower(PortfolioSnapshot s)
    {
        var sb = new StringBuilder();
        sb.Append("bp=").Append(s.BuyingPower).Append("  equity=").Append(s.Equity);
        return sb.ToString();
    }

    static string FormatReplayOrders(PortfolioSnapshot s)
    {
        var sb = new StringBuilder();
        int n = s.Orders?.Count ?? 0;
        if (n > 0)
        {
            // Show the most recent fill (Replay is MARKET-immediate → every order is a FILLED row).
            PortfolioOrderRow o = s.Orders[n - 1];
            sb.Append(o.symbol).Append("  ").Append(o.side).Append("  ").Append(o.status)
              .Append("  qty=").Append(o.qty).Append('@').Append(o.price).Append('\n');
        }
        else sb.Append("(none)\n");
        sb.Append("filled-order count: ").Append(n);
        return sb.ToString();
    }

    static string FormatReplayPositions(PortfolioSnapshot s)
    {
        if (s.Positions != null && s.Positions.Count > 0)
        {
            var sb = new StringBuilder();
            foreach (PositionRow p in s.Positions)
                sb.Append(p.symbol).Append("  qty=").Append(p.qty).Append("  avg=").Append(p.avg_price)
                  .Append("  uPnL=").Append(p.unrealized_pnl).Append('\n');
            sb.Append("cash=").Append(s.BuyingPower).Append("  equity=").Append(s.Equity);
            return sb.ToString();
        }
        return "(flat)";
    }

    // RunResult running view (TTWR run_result_panel.rs running branch): o:orders f:fills + realized/unrlz.
    static string FormatReplayRunResultRunning(PortfolioSnapshot s)
    {
        int orders = s.Orders?.Count ?? 0;
        var sb = new StringBuilder();
        sb.Append("running  o:").Append(orders).Append("  f:").Append(orders).Append('\n');
        sb.Append("pnl:").Append(s.RealizedPnl).Append("  unrlz:").Append(s.UnrealizedPnl);
        return sb.ToString();
    }

    // RunResult full-stats view (TTWR full-stats branch): fills/sharpe/dd + pnl/sortino from summary_json.
    static string FormatReplayRunResultComplete(RunResult r)
    {
        var sb = new StringBuilder();
        sb.Append("fills:").Append(r.FillsCount).Append("  sh:").Append(r.Sharpe.ToString("F2"))
          .Append("  dd:").Append(r.MaxDrawdown.ToString("F0")).Append('\n');
        sb.Append("pnl:").Append(r.TotalPnl.ToString("F0")).Append("  so:").Append(r.Sortino.ToString("F2"));
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

        // Refresh only when something the footer renders changed (mode segments + the live-mode status):
        // DisplayMode / lock / venue-live (segment visibility) + the LiveAuto run id (status line).
        string sig = _footerMode.DisplayMode + "|" + _footerMode.Locked + "|" + _footerMode.VenueLive
                   + "|" + _footerAuto.HasActiveRun + "|" + _footerAuto.ActiveRunId;
        if (sig != _lastFooterSig) { _lastFooterSig = sig; _footer.Refresh(); }
    }

    // ---- footer mode segment → Host. #76 S6b-β-clean U4: the replay transport (▶/⏸/step/stop/speed)
    // and the LiveAuto ▶ start are retired from the footer (ADR-0012 reactive model — Run is on the
    // editor title bar, U1). Only the Replay/Manual/Auto mode segments remain (Live execution mode, not
    // transport); LiveAuto start re-wiring is a separate Live epic. ----
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

    // ---- File = Layout (findings 0025 §9) ----
    void OnFileNew()
    {
        var decision = _menuBar.FileNew(out string modeReq, out string refuse);
        if (decision == FileNewDecision.RefusedRunning) { _menuBarView?.ShowMessage(refuse); return; }

        // #81: full in-memory reset (findings 0017 §4) honouring the adopt invariant (findings 0025 §8):
        // the coordinator resets the notebook to one empty cell and rebuilds the cell windows — region_001
        // is reset IN PLACE (never destroyed), region_002+ cell windows are despawned. canvas pan/zoom +
        // Hakoniwa are NOT reset (§4 targets strategy/editor state).
        //
        // #76↔#81 reconciliation: #76's U2 seeded MarimoStrategyTemplate.NewStrategy into the single editor
        // buffer. The #81 cell model (findings 0050) DELIBERATELY rejects body-seeding (marimo-faithful: a
        // fresh cell is empty, with the host-API names shown as a placeholder, not as "example to delete").
        // A one-empty-cell notebook still synthesises to a valid (runnable-once-saved) marimo app. #76
        // (2026-06-19): a no-resume boot lands in this SAME File→New blank state (OpenFileNewDefault →
        // _coordinator.New()); strategies are opened via File→Open, not auto-opened at boot.
        _coordinator.New();

        // scenario buffer + universe (the in-memory clear the host owns).
        _scenario.Clear();
        _tile?.SyncFieldsFromController();

        // mode side-effect (findings 0027 D3): SetExecutionMode(LiveManual) ONLY when connected — the VM
        // returns null otherwise (gap② guard, TTWR observable no-op). HITL-verified; the AFK probe runs
        // disconnected so modeReq is null here and the host is never touched.
        SendModeSideEffect(modeReq);

        _currentLayoutPath = "";   // #69: New drops to untitled (TTWR handle_file_new_system: buffer=default)
        _menuBarView?.ShowMessage("New: workspace cleared.");
    }

    void OnFileOpen()
    {
        // #69: native picker selects the document's .py (canonical identity, #78); the layout/scenario
        // come from its <strategy>.json sidecar. Cancel = no-op.
        string py = _fileDialog.OpenStrategy(InitialDir());
        if (string.IsNullOrEmpty(py)) { _menuBarView?.ShowMessage("Open: cancelled."); return; }

        // Layout is OPTIONAL (#80 intent / findings 0051). A valid "layout" key RESTORES the saved
        // geometry; a missing / scenario-only / corrupt sidecar still OPENS the .py BARE — keep the
        // current geometry and reseed so Run unblocks (the door for a fresh v19 whose <strategy>.json
        // carries only a "scenario" key). findings 0048 D4's no-wipe guarantee still holds: geometry is
        // touched ONLY by ApplyLayout below, which a bare open skips (so a bad sidecar can't wipe the work).
        bool layoutOk = LayoutSidecarStore.TryReadLayout(py, out var doc);

        // TTWR: opening a layout WHILE Live transitions to LiveAuto, BEFORE the load (findings 0017 §1).
        // FileOpenModeSideEffect returns null in Replay / when disconnected (gap② guard) → no host touch.
        // `?.` — the mode side-effect is OPTIONAL (the headless AFK gate drives Open with no menu VM wired).
        SendModeSideEffect(_menuBar?.FileOpenModeSideEffect());

        // #81: the picked .py IS the notebook document — decompose it into N cell windows (saved
        // cellPositions when we have a layout, else auto-cascade). The NOTEBOOK itself failing to open
        // (non-marimo / unreadable .py) is the ONLY abort — keep the workspace, change nothing.
        if (!_coordinator.Open(py, layoutOk ? ToVectors(doc.cellPositions) : null))
        {
            _menuBarView?.ShowMessage("Open: '" + Path.GetFileName(py) + "' " + (_notebook.LastError ?? "is not a notebook"));
            return;
        }
        if (layoutOk) ApplyLayout(doc);   // restore geometry ONLY when a valid layout is present
        _currentLayoutPath = py;
        PersistResumePointer(py);
        ReseedFromEditor();
        _menuBarView?.ShowMessage("Opened " + Path.GetFileName(py) + (layoutOk ? "" : " (no saved layout)"));
    }

    // SetExecutionMode for a File-op side-effect, with the standard worker→main reject marshalling. No-op
    // for a null mode (the VM returns null when disconnected / not in a Live mode — TTWR observable no-op).
    void SendModeSideEffect(string mode)
    {
        if (mode != null) _host.SetExecutionMode(mode, ok => { if (!ok) _footerModeRejected = true; });
    }

    void OnFileSave()
    {
        // #69: untitled (no document open) → delegate to Save As (TTWR dialogs.rs:272-275). Otherwise
        // merge the layout key into the open document's <strategy>.json (the scenario key is preserved).
        if (string.IsNullOrEmpty(_currentLayoutPath)) { OnFileSaveAs(); return; }
        // #81: write the notebook `.py` (synthesise the ordered cells) AND merge the layout key
        // (incl. cellPositions). The .py write clears the notebook's dirty flag, so the editor becomes
        // supplyable again (WYSIWYR) — reseed so Run unblocks immediately.
        bool pyOk = _coordinator.Save();
        bool layoutOk = TryWriteLayout(_currentLayoutPath);
        if (!pyOk || !layoutOk) { _menuBarView?.ShowMessage("Save failed (see log)."); return; }
        ReseedFromEditor();
        _menuBarView?.ShowMessage("Save: notebook + layout written.");
    }

    // #69: Save As forks the whole document to a new pair (findings 0048 D6): write <newname>.py
    // (strategy), then the scenario + layout keys into <newname>.json, then rebind/track the new doc.
    void OnFileSaveAs()
    {
        string newPy = _fileDialog.SaveStrategyAs(InitialDir(), InitialFileName());
        if (string.IsNullOrEmpty(newPy)) { _menuBarView?.ShowMessage("Save As: cancelled."); return; }
        if (!string.Equals(Path.GetExtension(newPy), ".py", StringComparison.OrdinalIgnoreCase))
            newPy = Path.ChangeExtension(newPy, ".py");   // defensive; the dialog's defext usually adds it

        // 1. #81: fork the WHOLE notebook to the new `.py` (N windows -> 1 `.py`) and rebind the
        //    aggregate. #78/#79: run cwd becomes <newname> dir.
        if (_coordinator == null) { _menuBarView?.ShowMessage("Save As: no notebook."); return; }
        if (!_coordinator.SaveAs(newPy))
        { _menuBarView?.ShowMessage("Save As: could not write " + Path.GetFileName(newPy)); return; }

        // 2. scenario key into <newname>.json (best-effort: Commit writes nothing on an invalid buffer,
        //    leaving a layout-only sidecar — which load_scenario tolerates, CONTEXT.md L380).
        try { _scenario.Commit(newPy); }
        catch (Exception e) { Debug.LogWarning("[BackcastWorkspaceRoot] Save As scenario writeback skipped: " + e.Message); }

        // 3. layout key into <newname>.json (Newtonsoft merge preserves the scenario key just written).
        if (!TryWriteLayout(newPy)) { _menuBarView?.ShowMessage("Save As failed (see log)."); return; }

        // 4. the new pair is the open document now; re-seed (editor rebound) + remember it for resume.
        _currentLayoutPath = newPy;
        PersistResumePointer(newPy);
        ReseedFromEditor();
        _menuBarView?.ShowMessage("Saved as " + Path.GetFileName(newPy));
    }

    // #69: the picker's initial dir/filename follow the open document, else persistentDataPath.
    string InitialDir() =>
        string.IsNullOrEmpty(_currentLayoutPath) ? Application.persistentDataPath : Path.GetDirectoryName(_currentLayoutPath);
    string InitialFileName() =>
        string.IsNullOrEmpty(_currentLayoutPath) ? "strategy.py" : Path.GetFileName(_currentLayoutPath);

    // #69 (B2): the resume pointer lives in PlayerPrefs (app state, not a user file). No global
    // layout.json — boot re-opens the last document; Save As / Open keep the pointer current.
    void PersistResumePointer(string py)
    {
        PlayerPrefs.SetString(ResumeKey, py ?? "");
        PlayerPrefs.Save();
    }

    // #69 (B2): boot resume — re-open the last document's layout from its <strategy>.json, or start
    // with the default workspace when there is no resumable document (fresh install / moved file).
    // #76 (2026-06-19): a no-resume boot lands in the File→New blank state (an untitled empty notebook),
    // NOT a strategy. Strategies are opened explicitly via File→Open (owner: "this app opens files via
    // File→Open"). The earlier U3 "boot opens the canonical v19 marimo" is withdrawn.
    void ResumeLastDocumentOrDefault()
    {
        string py = PlayerPrefs.GetString(ResumeKey, "");
        if (!string.IsNullOrEmpty(py) && File.Exists(py) && LayoutSidecarStore.TryReadLayout(py, out var doc))
        {
            // #81: restore geometry, then decompose the document `.py` into N cell windows at the saved
            // cellPositions. If the open fails (e.g. Python not yet ready / non-marimo), fall through to
            // the File→New blank state rather than leaving a half-restored state.
            ApplyLayout(doc);
            if (_coordinator.Open(py, ToVectors(doc.cellPositions)))
            {
                _currentLayoutPath = py;
                ReseedFromEditor();
                return;
            }
        }
        ApplyLayout(LayoutDocument.Default());   // fresh / unresumable → default workspace
        _currentLayoutPath = "";                 // untitled document
        OpenFileNewDefault();                    // #76: boot to the File→New blank state (no canonical auto-open)
    }

    // #76 (2026-06-19): the no-resume / unresumable boot state = the File→New blank document (one empty
    // cell in region_001, unbound). Identical to OnFileNew's notebook reset (`_coordinator.New()` =
    // ResetUnboundEmpty + SyncWindowsToNotebook) so boot and File→New land in the SAME state; Run stays
    // blocked until the user saves (WYSIWYR). The layout document stays UNTITLED (_currentLayoutPath = "").
    void OpenFileNewDefault()
    {
        _coordinator.New();
        ReseedFromEditor();
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
    // Stash the ACTIVE mode's current controller layout into its profile (TTWR build_hakoniwa_snapshot /
    // reconcile_hakoniwa_tiles parity). Shared by the mode flip (stash OLD before switching) and save
    // (stash active). Set stores the list BY REFERENCE — callers that persist Clone() first.
    void StashActiveProfile() => _profiles.Set(_baseLive, _hako.Capture().panels);

    LayoutDocument CaptureLayout()
    {
        // #62 (findings 0029 §4): stash the ACTIVE mode (the other mode keeps its stored profile), then
        // take ONE deep clone for the doc. hakoniwaProfiles is the SoT; `panels` mirrors the active mode
        // FROM THE CLONE (back-compat for a pre-#62 reader) — never aliasing live _profiles state.
        StashActiveProfile();
        var profiles = _profiles.Clone();
        // #81: cell windows are EXCLUDED from floatingWindows (single source of truth — their position
        // is the cell-order-parallel cellPositions, regenerated FROM LIVE by the coordinator, findings
        // 0050 trap 1). floatingWindows now carries only NON-cell windows (the Order ticket). The old
        // per-window strategyEditors content list is retired (the notebook has ONE path = the document).
        var nonCell = new List<FloatingWindowLayout>();
        foreach (var w in _windows.Capture().floatingWindows)
            if (w != null && w.kind != FloatingWindowCatalog.KIND_STRATEGY_EDITOR) nonCell.Add(w);

        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = profiles.Get(_baseLive),
            hakoniwaProfiles = profiles,
            canvasView = _canvas.CaptureView(),
            floatingWindows = nonCell,
            strategyEditors = new List<StrategyEditorState>(),
            cellPositions = _coordinator != null ? ToCellPositions(_coordinator.CapturePositions()) : new List<CellPosition>(),
        };
        return doc;
    }

    // #69 (B2): on quit, persist the layout into the OPEN document's <strategy>.json (TTWR autosave-to-
    // original_path). Untitled (no document) persists nothing — under the 2-file model layout lives with
    // a document, so there is no global scratch file to write (findings 0048 D7).
    void AutosaveCurrentDocument()
    {
        if (!string.IsNullOrEmpty(_currentLayoutPath)) TryWriteLayout(_currentLayoutPath);
    }

    // #69: merge the current layout into `path`'s <strategy>.json, logging (not throwing) on failure.
    // Shared by Save / Save As / quit autosave so the capture + try/catch + log live in one place.
    bool TryWriteLayout(string path)
    {
        try { LayoutSidecarStore.WriteLayout(path, CaptureLayout()); return true; }
        catch (Exception e) { Debug.LogWarning("[BackcastWorkspaceRoot] layout write failed: " + e.Message); return false; }
    }

    // restore order canvas → Hakoniwa → floating → Strategy Editor (findings 0025 §8).
    void ApplyLayout(LayoutDocument doc)
    {
        if (doc == null) return;
        if (doc.canvasView != null) _canvas.ApplyView(doc.canvasView);
        // #62 (findings 0029 §4): adopt the per-mode profiles from disk, or SEED both from the legacy
        // single `panels` when the doc predates #62 (forward-compat). Then apply the CURRENT mode's
        // profile (build seeds _baseLive=Replay). ApplyProfileOrder subsumes #61's ReassertBaseAfterRestore
        // (validated honor / canonical) — a legacy/colliding seed has a mismatched base set → canonical.
        _profiles = HakoniwaLayoutProfiles.FromDocument(doc);
        ApplyProfileOrder(_baseLive);
        RestoreFloating(doc);
        // #81: cell windows are restored by the coordinator (Open/New/Sync) from the notebook + the
        // cellPositions list — NOT here. Each caller (OnFileOpen / OpenFileNewDefault / Resume) runs the
        // coordinator open + the ReseedFromEditor tail around this geometry restore (restore order
        // canvas -> Hakoniwa -> floating(non-cell) -> cells -> reseed, findings 0025 §8).
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
            // #81: SKIP cell windows — they are owned by the coordinator (cellPositions), never spawned
            // here. A legacy sidecar that still lists strategy_editor in floatingWindows must not spawn
            // an untracked duplicate cell window (it would escape the coordinator's region map).
            if (w.kind == FloatingWindowCatalog.KIND_STRATEGY_EDITOR) continue;
            if (_windows.Has(w.id)) _windows.ApplyGeometry(w);
            else _windows.Spawn(w.kind, w.id, w.x, w.y, w.w, w.h, w.visible);
            _windows.BringToFront(w.id);
        }
    }

    // ---- idempotent teardown (findings 0025 §10). OnApplicationQuit + OnDestroy converge here. ----
    void StopAndDispose()
    {
        if (!_teardownGate.TryEnter()) return;     // 1. double-run guard (OnApplicationQuit + OnDestroy converge)
        // 2. save layout once while THIS root is the active layout owner. Gated on _built, NOT Python
        // ownership: a yielded root (GameObject disabled before Play) never ran Awake/BuildWorkspace so
        // it never reaches here, but a built-yet-non-Python-owner root must still persist its layout.
        if (_built) AutosaveCurrentDocument();
        _tile?.Dispose();                         // unsubscribe the tile from _scenario.Universe.Changed (no orphan handler)
        _scenario.Universe.Changed -= SyncChartTilesToUniverse;   // #60 chart-tile sync unsubscribe (no orphan handler)
        _host.Stop();                             // 3-7. force_stop → poll stop → bounded join → no Shutdown
        Debug.Log("[BackcastWorkspaceRoot] teardown complete.");
    }

    void OnApplicationQuit() => StopAndDispose();
    void OnDestroy() => StopAndDispose();
}
