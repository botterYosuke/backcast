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
using Newtonsoft.Json.Linq;

public sealed class BackcastWorkspaceRoot : MonoBehaviour
{
    const string WINDOW_ID = "strategy_editor:region_001";   // adopted scene-authored cell window (region_001 shell)
    const string ORDER_WINDOW_ID = "order:region_001";   // #23 re-home: singleton Order ticket window
    // #81 (ADR-0013 / findings 0050): the provider registry key is the LOGICAL notebook id — the thing
    // that supplies the `.py` to run is the NOTEBOOK aggregate, NOT a physical window. `region_001` stays
    // a physical window id (adopt / _editors / reveal); the run path resolves the notebook under THIS key.
    const string NOTEBOOK_ID = "strategy_editor:notebook";

    // #99 (ADR-0017 / findings 0075 §0/§3): the dock cluster's base window ids — the 5 former
    // Hakoniwa base tiles, now independent floating windows that snap on release. `startup` is
    // shown in Replay and hidden in Live (visibility toggle, NEVER destroyed — findings §3).
    // Singletons (one window each); ids = the catalog kind verbatim (no instance suffix).
    const string WINDOW_ID_STARTUP = "startup";
    const string WINDOW_ID_BUYING_POWER = "buying_power";
    const string WINDOW_ID_ORDERS = "orders";
    const string WINDOW_ID_POSITIONS = "positions";
    const string WINDOW_ID_RUN_RESULT = "run_result";

    // #105: the base dock cluster's id list — the single source for BOTH the first-launch spawn
    // (SpawnBaseDockWindows) and the factory grouping (FormFactoryBaseGroup). Order = findings 0075 §4.
    static readonly string[] BaseDockWindowIds = {
        WINDOW_ID_STARTUP, WINDOW_ID_BUYING_POWER, WINDOW_ID_ORDERS,
        WINDOW_ID_POSITIONS, WINDOW_ID_RUN_RESULT,
    };

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
    [SerializeField] RectTransform _floatingLayer;
    // #103 (ADR-0018 / findings 0075 §10): the BACK depth plane — a Content child that rides Content
    // at 1.0× (NO parallax offset) and is the EARLIER sibling of _floatingLayer, so it always draws
    // BEHIND it. The 6 former Hakoniwa kinds (chart / orders / positions / run_result / buying_power /
    // startup) live here; strategy_editor + order stay on _floatingLayer (1.2×, front). The pan
    // SPEED difference between the two planes (1.0 vs 1.2) is the restored depth cue (#99 regression).
    [SerializeField] RectTransform _dockLayer;
    // Parallax depth cue: _floatingLayer rides Content PLUS this extra factor, so it travels 1.2× per
    // unit pan and feels IN FRONT of the 1.0× _dockLayer. 1 = coplanar; >1 = foreground (findings 0006 §2).
    [SerializeField] float _floatingParallaxFactor = 1.2f;

    [Header("Strategy Editor floating window (scene-authored, adopted)")]
    [SerializeField] RectTransform _strategyEditorWindow;
    [SerializeField] RectTransform _strategyEditorBody;
    [SerializeField] FloatingWindowTitleInput _strategyEditorTitleInput;

    [Header("Order ticket floating window (scene-authored, adopted, #23 re-home)")]
    [SerializeField] RectTransform _orderWindow;
    [SerializeField] RectTransform _orderWindowBody;
    [SerializeField] FloatingWindowTitleInput _orderWindowTitleInput;

    // ── durable orchestration (extracted) ──
    readonly WorkspaceEngineHost _host = new WorkspaceEngineHost();   // #39→#59 Step 2: generalized (Replay + Live seam)

    // ── reused brains / VMs (findings 0025 §5) ──
    readonly ScenarioStartupController _scenario = new ScenarioStartupController();

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
    // #103 (ADR-0018 / findings 0075 §10): the BACK-plane controller. Owns ONLY the 6 dock kinds on
    // _dockLayer (1.0×). Separate from _windows so snap/focus母集合 is per-plane — a dock window can
    // never snap to the editor/order on the front plane (cross-plane snap ban is structural, not coded).
    FloatingWindowController _dockWindows;
    // #99 chart family (ADR-0017 §5 / findings 0075 §3): one floating window + ChartView per universe
    // instrument (id "chart:<id>"), membership-synced from _scenario.Universe via InstrumentRegistry.Changed.
    // Replaces the #60 Hakoniwa chart tile family — `_dockWindows.SpawnDockedToFocus(KIND_CHART, …)` (#103:
    // the back plane) is the ONLY chart spawn path. _chartRendered dedups the per-id render by series length.
    readonly Dictionary<string, ChartView> _chartViews = new Dictionary<string, ChartView>();
    readonly Dictionary<string, int> _chartRendered = new Dictionary<string, int>();
    // #57 → #99: each chart WINDOW's body is split into a mode-resized chartArea (left) + a
    // LADDER_WIDTH right strip holding a per-instrument DepthLadderView. Live shows the ladder (chart
    // shrinks left); Replay hides it (chart reclaims full width). _depthRendered dedups the per-id
    // 21-row rebuild by a depth+last signature.
    const float LADDER_WIDTH = 120f;                 // TTWR viewstate::LADDER_WIDTH
    readonly Dictionary<string, RectTransform> _chartAreas = new Dictionary<string, RectTransform>();
    readonly Dictionary<string, DepthLadderView> _depthLadders = new Dictionary<string, DepthLadderView>();
    readonly Dictionary<string, long> _depthRendered = new Dictionary<string, long>();
    bool _lastLadderLive;                             // last applied Live/Replay geometry (dedup the rect flip)
    string _lastDepthPayload;                         // last poll payload rendered into the ladders
    // #99 dock cluster current Live-shape cache (replaces #61 _baseLive). True when the footer reports a
    // Live mode (LiveManual/LiveAuto share one shape — DockShape.IsLiveShape parity); transitions
    // toggle the `startup` window's visibility (show in Replay, hide in Live) WITHOUT destroying it.
    bool _lastLiveShape;
    ScenarioStartupTile _tile;
    WorkspaceFooterView _footer;
    NotebookRunLane _notebookRunLane;           // #95 Phase 2 土台: dedicated worker thread for per-cell RUN
    NotebookRunController _notebookRun;          // #95 Phase 2 土台: per-cell RUN orchestration brain
    readonly Dictionary<string, Button> _cellRunButtons = new Dictionary<string, Button>();  // #95 P4: region → ▶/■ button
    MenuBarViewModel _menuBar;
    VenueMenuViewModel _venueMenu;
    UniverseSidebarController _sidebarCtrl;
    LiveSubscriptionCoordinator _subCoord;   // #107: live market-data 購読の本番トリガ (ADR-0022)
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
    // #89: quit-confirm (findings 0068). The pure controller decides; this root wires the real
    // Save/SaveAs/Quit + a _quitConfirmed latch so the 2nd wantsToQuit (fired by our own Quit() after
    // a confirmed save/discard) is allowed through.
    SaveGuardOverlay _saveGuardOverlay;
    SaveGuardController _saveGuardController;
    bool _quitConfirmed;
    // #87 slice 3: the deferred action the SaveGuard authorizes (Quit / File→New / File→Open). Set when a
    // dirty document defers an action behind the modal; run on Save-then-proceed or Discard; cleared on Cancel.
    Action _guardProceed;
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
    string _lastFooterPoll = "";            // dedup the footer-mode ApplyPoll (avoid per-frame JSON parse)

    void Awake()
    {
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_font == null) _font = Resources.GetBuiltinResource<Font>("Arial.ttf");

        // ADR-0021: the server is built for an INITIAL venue (resolved from LIVE_VENUE env; default MOCK
        // keeps the Replay mainline credential-less), but the venue is NO LONGER locked — a later
        // venue_login for a different venue rebinds the adapter factory at runtime (the Venue menu's
        // venue switch). LIVE_VENUE, when set, also filters the menu to that one venue
        // (ResolveExplicitLiveVenue). A demo HITL still sets LIVE_VENUE=TACHIBANA|KABU before Play for
        // the scripted ConnectConfigured harness, but mainline users just pick from the menu.
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
        // center workspace: infinite canvas (Content) hosts the BACK DockLayer (1.0×) + the FRONT
        // FloatingWindowLayer (1.2× parallax). Only the front layer gets the parallax depth cue; the
        // dock layer is a plain Content child (factor 1.0 ⇒ zero offset), so it rides Content at 1.0×
        // and the pan SPEED difference between the planes is the restored depth (#103 / ADR-0018).
        _canvas = new InfiniteCanvasController(_content, _floatingLayer, _floatingParallaxFactor);
        if (_inputSurface != null) _inputSurface.Initialize(_canvas, _viewport);

        // theme the infinite-canvas FIELD (the viewport bg) from workspace_background and follow theme
        // switches (parity with ChartView/DepthLadderView self-subscription, #44 AC②). Applied at runtime
        // so the scene-baked literal is just an editor preview — no scene re-bake needed to change the hue.
        // cyan-HUD re-skin: ApplyViewportTheme paints the viewport field fill AND ensures the faint
        // cyan grid as a back child of CONTENT, so the grid pans + zooms with the canvas exactly like
        // the dock windows (1.0× plane). Subscribed to theme swaps so both re-tint together.
        ThemeService.Changed += ApplyViewportTheme;
        ApplyViewportTheme();

        _catalog = FloatingWindowCatalog.Default();
        // #103 (ADR-0018): two controllers share the catalog but own SEPARATE layers/planes. _windows
        // = the FRONT plane (strategy_editor + order on _floatingLayer, 1.2×); _dockWindows = the BACK
        // plane (the 6 dock kinds on _dockLayer, 1.0×). Each factory wires its windows' title input to
        // ITS OWN controller, so a drag-release snap / dock-focus only sees same-plane neighbours.
        _windows = new FloatingWindowController(_floatingLayer, _catalog, BuildFloatingWindowFrame);
        // #103 (ADR-0018): the back plane MUST be its own layer. Do NOT silently fall back to _floatingLayer
        // when _dockLayer is unwired — that would collapse both planes onto one layer (the #99 regression this
        // change fixes) and mis-anchor dock spawns. A null _dockLayer means the scene was not rebuilt for #103,
        // so fail LOUD: the controller ctor throws ArgumentNullException, forcing a Build Workspace Scene run.
        _dockWindows = new FloatingWindowController(_dockLayer, _catalog, BuildDockWindowFrame);
        // #104 Slice G (ADR-0019 / findings 0082 §8): one drag-ghost layer per plane. Each lives as a
        // child of its plane's layer (so ghosts ride the same Content pan/zoom + parallax as the real
        // windows on that plane), and is bound to that plane's controller via AttachGhostLayer. The
        // controllers paint ghosts during DragApplyDelta and Clear at ReleaseDrag (commit-on-release).
        _windows.AttachGhostLayer(NewGhostLayer(_floatingLayer, "FloatGhostLayer"));
        _dockWindows.AttachGhostLayer(NewGhostLayer(_dockLayer,    "DockGhostLayer"));
        if (_catalog.TryGet(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, out var cellSpec)) _cellWindowSize = cellSpec.defaultSize;

        // #99 (ADR-0017 / findings 0075 §3/§4): the dock cluster's 5 base windows. All are independent
        // floating windows on the same layer as the strategy editor + order ticket; the magnet-snap
        // seam (Slice 1) is the only "Hakoniwa-ness" they have. Default placement is a grid-style
        // initial cascade (DockDefaultPlacement); a saved layout will reposition them in RestoreFloating.
        SpawnBaseDockWindows();

        // #99 chart family (ADR-0017 §5 / findings 0075 §3): one floating chart window per universe
        // instrument (id "chart:<iid>"). Membership is owned by _scenario.Universe (the shared SoT);
        // InstrumentRegistry.Changed drives spawn/close. Replaces the #60 Hakoniwa chart tile family.
        SyncChartWindowsToUniverse();
        _scenario.Universe.Changed += SyncChartWindowsToUniverse;   // keep chart windows == universe
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
            // #95 Phase 6 (ADR-0016 D2/D4 / findings 0075 P6-4): the title-bar ▶ Run is SUNSET. The
            // sole strategy execution entry is per-cell RUN (each cell window's ▶, wired below). The
            // old StrategyEditorRunButton + OnRun (batch-run-of-saved-strategy) are retired.
        }
        // #81: the adopted window's content is a Cell fragment view (no Document / no registry — the
        // notebook aggregate, registered above under NOTEBOOK_ID, is the sole provider).
        var editorView = StrategyEditorContentBuilder.Build(_strategyEditorBody, font: _font);
        if (editorView != null)
        {
            _editors[WINDOW_ID] = editorView;
            // #95 Phase 6 Slice 4 (findings 0075 P6-1): an edit/blur re-projects the per-cell stale
            // badges. Owner-gated like the ▶ press (a restage only lands on the owner's in-proc kernel);
            // _notebookRun is wired just below and read lazily at blur time.
            editorView.EditCommitted = () => { if (_isOwner && _host.ServerReady) _notebookRun?.Restage(); };
        }

        _coordinator = new NotebookCellCoordinator(_notebook, _windows, ViewFor, SpawnAnchorTopLeft, _cellWindowSize);
        // #95 Phase 2 土台 (ADR-0016 D2): per-cell RUN — every cell window gets a ▶ that runs THAT cell
        // + reactive downstream as pure computation on a dedicated worker thread (one marimo kernel,
        // RUNs queue — findings 0070 F5), output routed back to each window. The engine-run path (OnRun)
        // is untouched; the title-bar single Run sunsets in Phase 6, not here.
        _notebookRunLane = new NotebookRunLane(new HostNotebookCellExecutor(_host));
        // #95 Phase 4: a bt.replay()/bt.step() cell drives a real backtest — the committed scenario
        // rides along, ■ force-stops the in-flight run, and the running cell's ▶ toggles to ■.
        _notebookRun = new NotebookRunController(_coordinator, ViewFor, _notebookRunLane,
            msg => _menuBarView?.ShowMessage("Run cell: " + msg),
            BuildNotebookScenarioJson,
            () => _host.ForceStop(),
            SetCellRunButtonState,
            SetCellStaleRegions);
        // #102 findings 0079 §6 D7: every AddCell/DeleteCell/SyncWindowsToNotebook bumps the run
        // controller's generation so an in-flight per-cell RUN whose pressed-index frame predates the
        // mutation is dropped at drain time (the dormant region_001 reuse race — pressing A, deleting A,
        // adding B that reuses R1, would otherwise paint A's stdout onto B's window).
        _coordinator.ListMutated += () => _notebookRun.Invalidate();
        WireCellCloseButton(_strategyEditorWindow, WINDOW_ID);
        WireCellRunButton(_strategyEditorWindow, WINDOW_ID);
        BuildAddCellButton();
        HudFrameChrome.Decorate(_strategyEditorWindow);   // cyan HUD chrome on the adopted editor (no scene rebuild needed)

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
            HudFrameChrome.Decorate(_orderWindow);   // cyan HUD chrome on the adopted order ticket
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

        // #89: quit-confirm overlay + pure controller (findings 0068). On OS close with a dirty
        // document, wantsToQuit blocks and this modal asks Save / Don't Save / Cancel. NOT wired in
        // batchmode so AFK -quit is never held by a modal (QUIT-08). Build() already SetVisible(false).
        var quitGo = new GameObject("SaveGuardOverlay");
        quitGo.transform.SetParent(transform, false);
        _saveGuardOverlay = quitGo.AddComponent<SaveGuardOverlay>();
        _saveGuardOverlay.Build(_font);
        _saveGuardOverlay.SaveClicked += OnGuardSave;
        _saveGuardOverlay.DiscardClicked += OnGuardDiscard;
        _saveGuardOverlay.CancelClicked += OnGuardCancel;
        _saveGuardController = new SaveGuardController();
        if (!Application.isBatchMode) Application.wantsToQuit += OnWantsToQuit;

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
                DocumentBadgeText,                                    // #95 P6 S6 (#90): document-identity badge
                ResolveExplicitLiveVenue(),                           // ADR-0021: pinned LIVE_VENUE (null=show all)
                _font);                                               // uGUI font (#77)

        // sidebar (V-host): reuse the durable controller brain. The sidebar edits the SAME universe
        // SoT the startup tile edits and OnRun reads (_scenario.Universe) — "one universe per workspace"
        // (#31 designed controller.Registry to be host-wired; the cutover shell wires it here, #59).
        // SelectedSymbol is the SHARED _footerSelected so a sidebar instrument selection reaches the
        // footer's LiveAuto start (#39; else _footerSelected stays empty and LiveAuto always uses
        // universe[0] regardless of what the user picked).
        // The candidate source is the real two-source supply: Replay → listed_info.duckdb
        // (point-in-time MAX(Date) <= scenario.end, owner decision 2026-06-21), Live →
        // instruments_store / venue master. The provider hits the in-proc backend on a
        // background thread so the picker hot path never blocks UI under the GIL
        // (BackendAvailableInstrumentsProvider). The host is captured by reference, so building
        // the provider here (BuildWorkspace runs before InitializePython) is safe — Queries that
        // arrive before _serverReady get a SERVER_NOT_READY response which the provider treats
        // as TRANSIENT (not cached, re-fired next tick), so the picker self-heals once Initialize
        // completes (Slice review F1 — earlier versions cached the warmup status and stuck on
        // Loading forever until the user changed scenario.end).
        var provider = new BackendAvailableInstrumentsProvider(_host);
        _sidebarCtrl = new UniverseSidebarController(_scenario.Universe, _footerSelected, new UniverseWriteback(), provider);
        // #78: at BuildWorkspace the editor is still UNBOUND (the .py binds later in RestoreEditors), so
        // the universe is EMPTY here — this prime is against the empty set. The REAL writeback prime that
        // matches the seeded universe happens at the END of ApplyLayout, right after SeedScenarioFromEditor
        // (do NOT drop that re-prime: without it a later sidebar edit diffs against this stale empty set →
        // phantom-id hazard, findings 0025 §12). Kept here so a no-restore compose path still has a primed
        // writeback. The restored ids are not an unsaved edit (#31 D4).
        _sidebarCtrl.PrimeWritebackFromCurrent();
        if (_sidebarView != null) _sidebarView.Bind(_sidebarCtrl, EditorFileProvider, _font);   // #78: editor's .py sidecar; #77: uGUI font

        // #107 (ADR-0022): the production trigger for live market-data subscription. Two triggers:
        // (1) bulk-subscribe the whole universe on a Live-mode rising edge (LiveManual 突入), fed by
        // OnModePoll in DriveFooter; (2) the #31 DEFERRED LiveSubscribeHook, fired by the sidebar on a
        // Live row-select AND [+ Add] (per-instrument). There is deliberately NO universe-Changed
        // auto-subscribe — that would make the hook redundant and break the AC#6 delete-to-RED litmus.
        // Reads the SAME _scenario.Universe SoT and NEVER writes it (membership 不可侵 / D3). The egress is
        // the real LiveRpcLanes write lane via LaneSubscribeSink (host.Lanes resolved lazily — built by
        // InitializePython, always ready before the first Live edge).
        _subCoord = new LiveSubscriptionCoordinator(new LaneSubscribeSink(_host), _scenario.Universe);
        _sidebarCtrl.LiveSubscribeHook = _subCoord.OnLiveRowSelected;
    }

    // #104 Slice G (findings 0082 §8): create a per-plane ghost overlay container as a child of the
    // plane's layer (so ghosts inherit the plane's parallax) and wrap it in a DragGhostLayer bound to
    // the catalog. Production ghost factory mints uGUI Image + CanvasGroup (alpha + raycast off);
    // AFK injects bare RectTransforms via the alternate ctor (covered by Section31).
    DragGhostLayer NewGhostLayer(RectTransform planeLayer, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(planeLayer, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;   // identity (children position absolutely in plane-local coords)
        return new DragGhostLayer(rt, _catalog);
    }

    // Re-paint the infinite-canvas field (the viewport bg) from the active theme's workspace_background.
    // Subscribed to ThemeService.Changed in BuildWorkspace; null-safe so a probe that never authored the
    // viewport Image is a no-op.
    void ApplyViewportTheme()
    {
        if (_viewport != null)
        {
            var img = _viewport.GetComponent<Image>();
            if (img != null) img.color = ThemeService.Current.colors.workspace_background;
        }
        // grid lives on CONTENT (pans/zooms with the canvas), not the viewport — ensure/re-tint here
        // so it tracks theme swaps; null-safe so a probe without a content transform is a no-op.
        HudGridBackground.Ensure(_content);
    }

    // The FRONT-plane (_windows) factory: order ticket + strategy_editor cell windows. Dispatches on
    // spec.kind so adopted / scene-authored windows and runtime-spawned windows of the same kind
    // can't diverge (the shared frame builders enforce identity of chrome). Title inputs bind to
    // _windows, so a snap-on-release / dock-focus only sees FRONT-plane neighbours (#103 / ADR-0018:
    // cross-plane snap ban is structural). Dock kinds never reach here — they route to _dockWindows
    // and BuildDockWindowFrame on restore (RestoreFloating) and at spawn. Unknown kinds fall through
    // to the cell-window frame (a forward-evolution courtesy; Spawn's TryGet pre-filter rejects
    // unknown kinds before this factory runs).
    RectTransform BuildFloatingWindowFrame(FloatingWindowSpec spec, string id)
    {
        // #103 (ADR-0018): a dock kind must NEVER reach the front controller (RestoreFloating / the spawn
        // paths route by DockShape.IsDockKind). If one does, the old single-factory dock branch is gone, so it
        // would silently fall through to a BLANK cell frame (no dock content) — fail loud instead of shipping
        // an empty window, so a future mis-route is caught at this single chokepoint.
        if (DockShape.IsDockKind(spec.kind))
        {
            Debug.LogError($"[BackcastWorkspaceRoot] dock kind '{spec.kind}' (id {id}) routed to the FRONT controller — must use _dockWindows");
            return null;
        }

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
            if (view != null)
            {
                _editors[id] = view;
                view.EditCommitted = () => { if (_isOwner && _host.ServerReady) _notebookRun?.Restage(); };   // #95 P6 S4: blur -> restage
            }
            WireCellCloseButton(root, id);
            WireCellRunButton(root, id);   // #95 Phase 2 土台: spawned cell windows get a ▶ RUN too
        }
        return root;
    }

    // The BACK-plane (_dockWindows) factory: the 6 #99 dock kinds (ADR-0017 / findings 0075 §7) —
    // chart / startup / buying_power / orders / positions / run_result. Frame chrome comes from
    // DockWindowFrame (spec accent → title bar); content (ChartView+DepthLadderView for chart,
    // ScenarioStartupTile for startup, LivePanelTileView for the 4 base panels) is injected here so
    // the spawn flow is ONE call. #103 (ADR-0018): the title input binds to _dockWindows so dock
    // snap/focus stays WITHIN the back plane (a dock window never snaps to the front-plane editor).
    RectTransform BuildDockWindowFrame(FloatingWindowSpec spec, string id)
    {
        // #103 (ADR-0018): symmetric guard — a front kind (order / strategy_editor) must never reach the back
        // controller. Fail loud rather than build a dock frame around editor/order content.
        if (!DockShape.IsDockKind(spec.kind))
        {
            Debug.LogError($"[BackcastWorkspaceRoot] front kind '{spec.kind}' (id {id}) routed to the BACK controller — must use _windows");
            return null;
        }

        var dockRoot = DockWindowFrame.Build(id, spec.title, spec.accent, _font, out var dockTitle, out var dockBody);
        dockTitle.Initialize(_dockWindows, _canvas, _viewport, id);
        BuildDockContent(spec.kind, id, dockBody);
        return dockRoot;
    }

    // Build the per-kind content INSIDE a dock window's body. The dock kinds are SINGLETONS
    // except `chart`, which is multi-instance with id = "chart:<instrument>" — the instrument
    // id is recovered from the id via DockShape so the ChartView / DepthLadderView lookup
    // dictionaries can key on it. Idempotent (re-entry on the same id leaves the existing view
    // in place; the spawn path's first-wins guard means the factory normally fires once per id).
    void BuildDockContent(string kind, string id, RectTransform body)
    {
        if (body == null) return;

        if (kind == FloatingWindowCatalog.KIND_STARTUP)
        {
            if (_tile == null) _tile = new ScenarioStartupTile(_scenario, _font);
            _tile.Build(body);
            _tile.SyncFieldsFromController();
            return;
        }

        if (kind == FloatingWindowCatalog.KIND_BUYING_POWER)
        { _buyingPowerView = new LivePanelTileView(FormatBuyingPower); _buyingPowerView.Build(body, _font); return; }
        if (kind == FloatingWindowCatalog.KIND_ORDERS)
        { _ordersView = new LivePanelTileView(FormatOrders); _ordersView.Build(body, _font); return; }
        if (kind == FloatingWindowCatalog.KIND_POSITIONS)
        { _positionsView = new LivePanelTileView(FormatPositions); _positionsView.Build(body, _font); return; }
        if (kind == FloatingWindowCatalog.KIND_RUN_RESULT)
        { _runResultView = new LivePanelTileView(FormatRunResult); _runResultView.Build(body, _font); return; }

        if (kind == FloatingWindowCatalog.KIND_CHART)
        {
            string iid = DockShape.InstrumentOfChartId(id);
            if (string.IsNullOrEmpty(iid)) return;        // defensive: a malformed id never lands a chart view
            BuildChartContent(iid, body);
            return;
        }
    }

    // Construct a chart window body: a left chartArea (ChartView) + a LADDER_WIDTH right strip
    // hosting the per-instrument DepthLadderView. The Live/Replay show/hide mirrors the old
    // Hakoniwa chart tile (TTWR overlays_ladder.rs right pane, findings 0028 D1) — depth is
    // Live-only, so a tile spawned mid-Replay starts with the ladder hidden and the chart
    // claiming the full body width.
    void BuildChartContent(string instrumentId, RectTransform body)
    {
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
        ladder.Render(DepthSnapshotView.Empty);
        ladderAreaGo.SetActive(_lastLadderLive);

        var cv = chartAreaGo.AddComponent<ChartView>();
        cv.Build(chartArea, showTitleBar: false);

        _chartViews[instrumentId] = cv;
        _chartAreas[instrumentId] = chartArea;
        _depthLadders[instrumentId] = ladder;
        _lastDepthPayload = null;   // a tile added mid-Live renders its board on the NEXT poll
    }

    // Spawn the 5 base dock windows (singletons) at the DockDefaultPlacement positions. Order
    // matches findings 0075 §4: startup / buying_power / orders / positions / run_result.
    // First-launch positions can be overridden by a saved layout via RestoreFloating's
    // ApplyGeometry on the matching id (the window is already registered by the time the
    // restore runs, so it gets repositioned in place — never destroyed+respawned).
    void SpawnBaseDockWindows()
    {
        // #105: FLUSH placement (gap = 0) so the factory Hakoniwa group looks docked (touching), not
        // scattered. Shared with the AFK gate via DockDefaultPlacement.ComputeFlushRects (one source).
        var rects = DockDefaultPlacement.ComputeFlushRects(BaseDockWindowIds.Length);
        for (int i = 0; i < BaseDockWindowIds.Length; i++)
        {
            var r = rects[i];
            string id = BaseDockWindowIds[i];
            // #103 (ADR-0018): base dock windows spawn on the BACK plane (_dockWindows / _dockLayer, 1.0×).
            _dockWindows.Spawn(id, id, r.topLeft.x, r.topLeft.y, r.size.x, r.size.y, true);
        }
    }

    // #105 (ADR-0019 D8 amendment / findings 0082 §12, findings 0083): factory-default grouping.
    // On a no-resume / unresumable boot (saved layout 無し＝first launch), bundle the 5 base dock
    // windows into ONE Hakoniwa group. The cluster includes startup + run_result cores
    // (DockShape.IsCoreKind) so the group is automatically a HAKONIWA group: tile-fixed swap, core-
    // locked, no whole-cluster free translate (findings 0082 §2). This is the ONLY first-launch
    // grouping path — a resumed/opened SAVED layout NEVER calls this; RestoreFloating honors the
    // doc's persisted groupId instead (工場出荷値のみ＝owner decision). The base windows are already
    // spawned ungrouped (BuildWorkspace → SpawnBaseDockWindows), so this only stamps the shared groupId.
    void FormFactoryBaseGroup() => _dockWindows?.FormGroup(BaseDockWindowIds);

    // ---- #81 cell-as-floating-window: coordinator wiring (delegates the root injects) ----

    // regionId -> its editor view (null-tolerant: a window the factory hasn't built yet, or a torn-down
    // window's stale/destroyed entry, returns null and the coordinator skips the bind).
    StrategyEditorView ViewFor(string regionId)
        => _editors.TryGetValue(regionId, out var v) && v != null ? v : null;

    // The viewport-centre canvas-LOGICAL point = the next spawn anchor for the FRONT plane (used by the
    // cell coordinator). CanvasView.panX/panY is exactly that point (findings 0006 §2).
    Vector2 SpawnAnchorTopLeft() => SpawnAnchorTopLeftIn(_floatingLayer);

    // The viewport-centre anchor expressed in a SPECIFIC layer's local coords. A parallax-shifted layer
    // (_floatingLayer, 1.2×) has a non-zero anchoredPosition when panned, so its layer-local top-left
    // differs from the Content-logical viewport-centre by that offset — subtract it so a new window still
    // lands at the viewport centre regardless of the depth cue. The 1.0× _dockLayer rides Content with a
    // zero offset, so this reduces to (panX, panY) there (#103 / ADR-0018). One helper, both planes.
    Vector2 SpawnAnchorTopLeftIn(RectTransform layer)
    {
        var v = _canvas != null ? _canvas.CaptureView() : null;
        Vector2 anchor = v != null ? new Vector2(v.panX, v.panY) : Vector2.zero;
        if (layer != null) anchor -= layer.anchoredPosition;
        return anchor;
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

    // #95 Phase 2 土台: find-or-create the title-bar ▶ RUN on a cell window and wire it to run that
    // cell. Idempotent (StrategyEditorWindowFrame.EnsureRunButton), so the adopted scene window and a
    // spawned window get a consistent ▶ without diverging (X-button と同型・WireCellCloseButton と並ぶ).
    void WireCellRunButton(RectTransform windowRoot, string regionId)
    {
        var btn = StrategyEditorWindowFrame.EnsureRunButton(windowRoot, _font);
        if (btn == null) return;
        _cellRunButtons[regionId] = btn;   // #95 P4: remember it so the run state can toggle ▶/■
        btn.onClick.RemoveAllListeners();
        // Only run when this session owns the Python engine and the server is up — otherwise the press
        // would do a wasteful main-thread synthesise under the GIL and then report a generic backend
        // failure (the run can only land on the owner's in-proc kernel). The AFK gate wires its own
        // onClick straight to RunCell, so this owner gate is production-only.
        btn.onClick.AddListener(() =>
        {
            if (_isOwner && _host.ServerReady) _notebookRun?.RunCell(regionId);
        });
    }

    // #95 Phase 4: toggle a running cell's button between ▶ (idle → RunCell) and ■ (running →
    // StopRunning). Called by the controller as a backtest starts/stops. Tolerates a vanished window
    // (the cell was deleted / notebook replaced mid-run) — the lookup simply misses.
    void SetCellRunButtonState(string regionId, bool running)
    {
        if (regionId == null) return;
        if (!_cellRunButtons.TryGetValue(regionId, out var btn) || btn == null) return;
        StrategyEditorWindowFrame.SetRunButtonGlyph(btn, running);
        btn.onClick.RemoveAllListeners();
        if (running)
            btn.onClick.AddListener(() => { if (_isOwner) _notebookRun?.StopRunning(); });
        else
            btn.onClick.AddListener(() => { if (_isOwner && _host.ServerReady) _notebookRun?.RunCell(regionId); });
    }

    // #95 Phase 6 Slice 3 (findings 0075 P6-1): paint the per-cell STALE badge. The controller hands the
    // regions whose cells are still stale after a run (edited but not re-pressed) — each gets an amber ▶;
    // every OTHER known cell button is restored to green ▶. running (■) and stale (amber) are mutually
    // exclusive: when this fires the controller has already cleared any running ■ back to ▶ and no other
    // cell can be running (the running guard rejects a concurrent press), so painting the non-stale set
    // green never clobbers a live ■. Re-pressing a stale cell runs it → next result drops it → green.
    void SetCellStaleRegions(IReadOnlyList<string> staleRegions)
    {
        if (_cellRunButtons == null) return;
        var stale = staleRegions != null ? new HashSet<string>(staleRegions) : new HashSet<string>();
        foreach (var kv in _cellRunButtons)
        {
            if (kv.Value == null) continue;
            StrategyEditorWindowFrame.SetRunButtonStale(kv.Value, stale.Contains(kv.Key));
        }
    }

    // #95 Phase 4: serialise the committed startup-panel scenario into the dict the backend's
    // _build_notebook_bt expects (instruments / start / end / granularity / initial_cash). Returns
    // null when no universe is committed (the backend then keeps the pure-compute path / errors
    // visibly), so a bt cell run only starts against a real scenario (ADR-0016 D5).
    string BuildNotebookScenarioJson()
    {
        var ids = _scenario?.Universe?.Ids;
        if (ids == null || ids.Count == 0) return null;
        var p = _scenario.Params;
        var o = new JObject
        {
            ["instruments"] = new JArray(ids),
            ["start"] = p.Start ?? "",
            ["end"] = p.End ?? "",
            ["granularity"] = ScenarioStartupParams.GranularityToString(p.Granularity),
        };
        if (long.TryParse(p.InitialCash, NumberStyles.Integer, CultureInfo.InvariantCulture, out long cash))
            o["initial_cash"] = cash;
        return o.ToString(Newtonsoft.Json.Formatting.None);
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
        rt.sizeDelta = new Vector2(30f, 30f);
        rt.anchoredPosition = new Vector2(-20f, 56f);   // bottom-right; y = footer bar (40px, scene-authored) + 16px gap
        btnGo.GetComponent<Image>().color = new Color(0.2314f, 0.7686f, 0.5961f, 0.95f); // #3bc498 aurora-teal "Go" (space re-skin 2026-06-20)

        var lblGo = new GameObject("Label", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        var lrt = (RectTransform)lblGo.transform;
        lrt.SetParent(rt, false);
        lrt.anchorMin = Vector2.zero; lrt.anchorMax = Vector2.one; lrt.offsetMin = Vector2.zero; lrt.offsetMax = Vector2.zero;
        var t = lblGo.GetComponent<Text>();
        t.font = _font;
        t.text = "+";
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

    // ---- #99 chart family: keep chart windows == the universe SoT ----

    // Reconcile the live chart windows to _scenario.Universe (the membership SoT): close windows
    // whose instrument left the universe, spawn one for each newly-present instrument. Bound to
    // InstrumentRegistry.Changed so a sidebar/picker/tile edit reflects immediately (no Run wait).
    // Replaces #60 SyncChartTilesToUniverse — `_dockWindows.Spawn/Close(KIND_CHART, "chart:<iid>", …)`
    // (#103: the back plane) is the only chart spawn/close path. No box-grow (the dock cluster is not bounded).
    void SyncChartWindowsToUniverse()
    {
        var ids = _scenario.Universe.Ids;
        var desired = new HashSet<string>(ids);

        // Close any chart window whose instrument left the universe (the snapshot below is taken
        // BEFORE iteration to avoid mutating the controller's dictionary during enumeration).
        var stale = new List<string>();
        foreach (var iid in _chartViews.Keys) if (!desired.Contains(iid)) stale.Add(iid);
        foreach (var iid in stale) DespawnChartWindow(iid);

        // Spawn the missing ones. #101 (fix #99 regression; findings 0078): NO DockDefaultPlacement.
        // ComputeRects(N) here — a count-scaled grid cell made each new chart a different SIZE as the
        // universe grew. Each chart now spawns at the spec-fixed KIND_CHART size and snaps flush to the
        // focused (or nearest) window via SpawnDockedToFocus.
        for (int i = 0; i < ids.Count; i++)
        {
            string iid = ids[i];
            if (_chartViews.ContainsKey(iid)) continue;
            SpawnChartWindow(iid);
        }
    }

    // Spawn one chart window (id = "chart:<iid>") at the spec-fixed KIND_CHART size, snapped flush to the
    // user's focused window (or, with no focus history, the window nearest the viewport centre) — #101 /
    // findings 0078. Size is INDEPENDENT of the chart count (the #99 bug). Content (ChartView +
    // DepthLadderView) is injected by the factory's BuildDockContent path during the spawn — by the time
    // it returns, `_chartViews[iid]` is populated.
    void SpawnChartWindow(string instrumentId)
    {
        if (string.IsNullOrEmpty(instrumentId) || _chartViews.ContainsKey(instrumentId)) return;
        string windowId = DockShape.ChartId(instrumentId);
        // #103 (ADR-0018): chart windows live on the BACK plane (_dockWindows / _dockLayer, 1.0×). The
        // anchor is taken in DOCK-layer coords (the 1.0× layer has zero parallax offset, so it is just
        // the viewport centre). The #101 spec-fixed size + focus-adjacent snap is unchanged — it now
        // resolves the focus target WITHIN the back plane, so a chart snaps to a dock window, never the
        // front-plane editor (cross-plane snap ban).
        _dockWindows.SpawnDockedToFocus(FloatingWindowCatalog.KIND_CHART, windowId, SpawnAnchorTopLeftIn(_dockLayer), true);
    }

    // Despawn one chart window and clear its render bookkeeping. The GameObject is destroyed by
    // FloatingWindowController.Close (the ChartArea / LadderArea / ChartView / DepthLadderView are
    // children and go with it — no separate destroy needed).
    void DespawnChartWindow(string instrumentId)
    {
        if (string.IsNullOrEmpty(instrumentId)) return;
        _dockWindows.Close(DockShape.ChartId(instrumentId));   // #103: chart lives on the back plane
        _chartViews.Remove(instrumentId);
        _chartRendered.Remove(instrumentId);
        _chartAreas.Remove(instrumentId);
        _depthLadders.Remove(instrumentId);
        _depthRendered.Remove(instrumentId);
    }

    // #99 (ADR-0017 / findings 0075 §3): the only mode-conditional dock surface left is the
    // `startup` window — shown in Replay, hidden in Live (NEVER destroyed, visibility toggle —
    // dormant temp via _windows.Hide(id) / Show(id), so a Replay→Live→Replay round-trip keeps the
    // user's startup position). Replaces the #61 SyncBaseTilesToMode (which retiled the whole base
    // set on every flip); under the dock model nothing else is mode-conditional.
    void SyncStartupVisibilityToMode(bool live)
    {
        if (live) _dockWindows.Hide(WINDOW_ID_STARTUP);   // #103: startup lives on the back plane
        else _dockWindows.Show(WINDOW_ID_STARTUP);
        _lastLiveShape = live;
        ForceRefreshLiveTiles();   // shape flip: repaint the base panels now (#23 wiring)
    }

    // #95 Phase 6 (ADR-0016 D2/D4 / findings 0075 P6-4): OnRun — the batch-run-of-saved-strategy
    // entry the title-bar ▶ Run drove — is SUNSET. Strategy execution is per-cell RUN only (the
    // engine-run primitive WorkspaceEngineHost.TryStartRun survives for the Replay→Hakoniwa path;
    // the scenario sidecar is committed by the startup panel's Commit, not a Run gate).

    void Update()
    {
        // #95 Phase 2 土台: route any completed per-cell RUN outputs into their windows. Drained every
        // frame (cheap when empty), BEFORE the owner guard so a queued press is never stranded.
        _notebookRun?.DrainAndRoute();

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
        DriveSidebarContext(); // findings 0084: after DriveFooter (fresh DisplayMode) so [+ Add] picks the live mode + scenario.end
        DrivePrune();          // #41: after DriveFooter (fresh DisplayMode); before depth so a pruned chart tile propagates first
        DriveDepthLadders();   // #57: after DriveFooter so _footerMode.DisplayMode is the fresh mode
    }

    // findings 0084: push the sidebar [+ Add] picker's universe scope (mode + scenario.end) so it
    // queries the LIVE universe instead of the Bind-time defaults (Replay + "2024-12-31"). Re-resolved
    // on-demand each tick (mirrors DrivePrune below, same mode/end derivation) — the picker captures
    // end at open time, so this just keeps the scope it WILL capture current.
    void DriveSidebarContext()
    {
        if (_sidebarView == null) return;
        var mode = DockShape.IsLiveShape(_footerMode.DisplayMode)
            ? UniverseSourceMode.Live : UniverseSourceMode.Replay;
        string end = _scenario.Params != null ? _scenario.Params.End : null;
        _sidebarView.SetContext(mode, end);
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
            Mode = DockShape.IsLiveShape(_footerMode.DisplayMode)
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
        if (!_lastLiveShape) { PushReplayTiles(); return; }
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
        if (!_lastLiveShape)
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

    // LIVE_VENUE selects the live venue the server is INITIALLY built for; default MOCK when unset or
    // unknown (a typo can't build a server for an unknown venue — it falls back to MOCK rather than
    // failing opaquely). ADR-0021: this is no longer a LOCK — a menu venue_login rebinds the server's
    // venue at runtime; the env only chooses the startup factory and (via ResolveExplicitLiveVenue)
    // filters the Venue menu. Derived from the explicit value so the whitelist lives in ONE place.
    static string ResolveLiveVenue() => ResolveExplicitLiveVenue() ?? "MOCK";

    // ADR-0021: the EXPLICITLY-set LIVE_VENUE (whitelisted, upper) or null when unset/unknown. Distinct
    // from ResolveLiveVenue() which defaults to MOCK — the Venue menu needs to tell "user pinned a venue"
    // (offer only that one) from "unset" (offer all, rebind on login).
    static string ResolveExplicitLiveVenue()
    {
        string v = (EnvConfig.Get("LIVE_VENUE", "") ?? "").Trim().ToUpperInvariant();
        return (v == "MOCK" || v == "TACHIBANA" || v == "KABU") ? v : null;
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

        // #107 (ADR-0022): feed the poll's execution_mode to the subscription coordinator. A Replay→Live
        // rising edge bulk-subscribes the universe so every chart/ladder tile updates on LiveManual entry.
        // Cheap edge-detector (idempotent when the mode is unchanged).
        _subCoord?.OnModePoll(_footerMode.DisplayMode);

        // #99 dock shape flip: the poll is the mode SoT (DisplayMode). LiveManual⇄LiveAuto share one
        // Live shape (no-op); a Replay⇄Live transition flips the `startup` window visibility (Replay
        // shows it, Live hides it — findings 0075 §3, ADR-0017 §4) and force-repaints the base panels.
        bool live = DockShape.IsLiveShape(_footerMode.DisplayMode);
        if (live != _lastLiveShape) SyncStartupVisibilityToMode(live);

        // base panel content is refreshed by RefreshLiveTiles (Update, before DriveFooter) — gated on the
        // VM AppliedCount so idle frames cost one long compare. A shape flip force-repaints inside
        // SyncStartupVisibilityToMode (above).

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

    // #95 Phase 6 Slice 6 (#90 / findings 0075 P6-5): the menu-bar document-identity badge string, read
    // live each frame by MenuBarView.Refresh. "Untitled" while unbound (File→New / a fresh notebook),
    // the .py basename once bound (File→Open / Save As), with a "* " prefix while the notebook is dirty
    // (an unsaved edit). A SEPARATE lane from the venue/mode/message badge — document identity vs
    // execution state (P6-5 responsibility split). Cached on its source (path/dirty/bound) so the
    // per-frame read stays GC-clean (it rebuilds the string only when the identity actually changes).
    string _docBadgeCache = string.Empty;
    string _docBadgePath;
    bool _docBadgeDirty, _docBadgeBound, _docBadgeInit;
    string DocumentBadgeText()
    {
        if (_notebook == null) return string.Empty;
        if (!_docBadgeInit || _notebook.IsDirty != _docBadgeDirty
            || _notebook.IsBound != _docBadgeBound || _notebook.CurrentPath != _docBadgePath)
        {
            _docBadgeInit = true;
            _docBadgeDirty = _notebook.IsDirty;
            _docBadgeBound = _notebook.IsBound;
            _docBadgePath = _notebook.CurrentPath;
            string name = _docBadgeBound ? Path.GetFileName(_docBadgePath) : "Untitled";
            _docBadgeCache = (_docBadgeDirty ? "* " : "") + name;
        }
        return _docBadgeCache;
    }

    // ---- File = Layout (findings 0025 §9) ----
    void OnFileNew()
    {
        var decision = _menuBar.FileNew(out string modeReq, out string refuse);
        if (decision == FileNewDecision.RefusedRunning) { _menuBarView?.ShowMessage(refuse); return; }
        // #87: a dirty document asks Save/Discard/Cancel before New clears it. The running-run refuse above
        // is evaluated FIRST — a running run blocks New regardless of dirty state (AC unchanged).
        GuardThenProceed(() => DoFileNew(modeReq));
    }

    // The actual File→New clear — run immediately on a clean document, or after the SaveGuard authorizes it.
    void DoFileNew(string modeReq)
    {
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
        _notebookRun?.Invalidate();   // #95 Phase 2: drop any in-flight per-cell run against the replaced notebook
        _coordinator.New();

        // scenario buffer + universe (the in-memory clear the host owns).
        _scenario.Clear();
        _tile?.SyncFieldsFromController();

        // mode side-effect (findings 0027 D3): SetExecutionMode(LiveManual) ONLY when connected — the VM
        // returns null otherwise (gap② guard, TTWR observable no-op). HITL-verified; the AFK probe runs
        // disconnected so modeReq is null here and the host is never touched.
        SendModeSideEffect(modeReq);

        // #100 Slice ① (findings 0077): document boundary — drop the prior strategy's Replay tile
        // state (portfolio AND run-summary) so the 4 base panels render honest-empty after New
        // instead of carrying the prior doc's last finalized run.  Pairs with File→Open.
        _host?.ClearReplayRunView();

        _currentLayoutPath = "";   // #69: New drops to untitled (TTWR handle_file_new_system: buffer=default)
        _menuBarView?.ShowMessage("New: workspace cleared.");
    }

    void OnFileOpen()
    {
        // #69: native picker selects the document's .py (canonical identity, #78); the layout/scenario
        // come from its <strategy>.json sidecar. Cancel = no-op.
        string py = _fileDialog.OpenStrategy(InitialDir());
        if (string.IsNullOrEmpty(py)) { _menuBarView?.ShowMessage("Open: cancelled."); return; }
        // #87: a dirty document asks Save/Discard/Cancel before the picked .py replaces the buffer. The
        // picker runs FIRST (a cancelled picker is a no-op with nothing to guard); the guard defers the
        // load behind the modal. On Discard the load passes discardDirty:true (DoFileOpen) so even a
        // non-marimo wrap may replace dirty cells — the guard IS the discard authorization (#86 F1).
        GuardThenProceed(() => DoFileOpen(py));
    }

    // The actual File→Open load — run immediately on a clean document, or after the SaveGuard authorizes it
    // (clean / saved / explicit Discard). discardDirty:true is always correct here: control only reaches
    // this point once the guard has authorized losing any unsaved work.
    void DoFileOpen(string py)
    {
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
        // cellPositions when we have a layout, else auto-cascade). #86 widened Open so a non-marimo
        // `.py` BOOTSTRAPS as one anonymous cell (raw file text as body) — the only Open failures
        // now are path/IO errors (missing file / unreadable / wrong extension). On any failure the
        // workspace is untouched and we surface LastError.
        if (!_coordinator.Open(py, layoutOk ? ToVectors(doc.cellPositions) : null, discardDirty: true))
        {
            _menuBarView?.ShowMessage("Open: '" + Path.GetFileName(py) + "' " + (_notebook.LastError ?? "could not be opened"));
            return;   // fail-soft: the notebook is UNCHANGED, so an in-flight run is still valid — do NOT invalidate
        }
        _notebookRun?.Invalidate();   // #95 Phase 2: notebook replaced — drop any in-flight per-cell run against the old one
        // #100 Slice ① (findings 0077): document boundary — drop the prior doc's Replay tile state
        // (portfolio AND run-summary) so the new doc's tiles start honest-empty.  Called AFTER the
        // successful Open commits (a fail-soft Open path above returned early), so the gesture is
        // tied to "the document actually changed" — pairs with File→New's identical call.
        _host?.ClearReplayRunView();
        if (layoutOk) ApplyLayout(doc);   // restore geometry ONLY when a valid layout is present
        _currentLayoutPath = py;
        PersistResumePointer(py);
        ReseedFromEditor();
        // F3 (#86, findings 0054 §D2a): a non-marimo `.py` wrap-Open looks identical to a clean
        // marimo Open in the menu toast; users then Ctrl+S into the destructive marimo conversion
        // (§D2) blind. Surface the wrap state on the same toast line so the conversion is informed.
        string wrapHint = _notebook.WrapMode ? " (wrap mode — Save will convert to marimo)" : "";
        string layoutHint = layoutOk ? "" : " (no saved layout)";
        _menuBarView?.ShowMessage("Opened " + Path.GetFileName(py) + wrapHint + layoutHint);
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
        _menuBarView?.ShowMessage("Saved " + Path.GetFileName(_currentLayoutPath));
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

    // ── #89/#87 SaveGuard wiring (findings 0068/0069). The pure SaveGuardController decides; here we run
    // the real Save/SaveAs and the DEFERRED action (_guardProceed) the guard authorizes — Application.Quit
    // for OS-close (the _quitConfirmed latch lets our own Quit() through), or File→New / File→Open for the
    // nav-guard reuse (#87). Data-protection guard: a Save that leaves the notebook still dirty (write
    // failed / picker cancelled) ABORTS the action — edits are never lost silently.
    bool OnWantsToQuit()
    {
        if (_quitConfirmed) return true;                       // our own confirmed Quit() — let it through
        if (_saveGuardController == null) return true;              // not wired (defensive) — don't block shutdown
        if (_saveGuardController.RequestProceed(_notebook != null && _notebook.IsDirty) == SaveGuardDecision.Proceed)
            return true;                                       // clean (or no notebook) → quit immediately
        OpenSaveGuard(ConfirmAndQuit);                         // dirty → modal; on proceed, quit
        return false;
    }

    // #87: run `proceed` NOW if the document is clean (or the guard is unwired), else defer it behind the
    // SaveGuard modal (a Save/Discard verdict resolves it). Used by File→New / File→Open. OnWantsToQuit
    // can't use this directly — its clean branch returns a bool to the OS instead of acting — so it shares
    // only the dirty tail via OpenSaveGuard.
    void GuardThenProceed(Action proceed)
    {
        if (proceed == null) return;
        if (_saveGuardController == null ||
            _saveGuardController.RequestProceed(_notebook != null && _notebook.IsDirty) == SaveGuardDecision.Proceed)
        { proceed(); return; }
        OpenSaveGuard(proceed);
    }

    // Open the SaveGuard modal, deferring `proceed` until a Save/Discard verdict resolves it.
    void OpenSaveGuard(Action proceed)
    {
        _guardProceed = proceed;
        _saveGuardOverlay?.SetVisible(true);
    }

    // Run + clear the deferred guarded action (null-safe, one-shot).
    void RunGuardProceed()
    {
        var proceed = _guardProceed;
        _guardProceed = null;
        proceed?.Invoke();
    }

    void OnGuardSave()
    {
        bool isBound = !string.IsNullOrEmpty(_currentLayoutPath);
        var outcome = _saveGuardController.ChooseSave(isBound);     // closes the modal (IsOpen=false)
        _saveGuardOverlay?.SetVisible(false);
        if (outcome == SaveGuardOutcome.SaveThenProceed)
        {
            OnFileSave();
            bool saved = _notebook != null && !_notebook.IsDirty;
            if (_saveGuardController.ResolveSave(saved) == SaveGuardOutcome.SaveThenProceed) RunGuardProceed();   // .py persisted → proceed
            else _guardProceed = null;                        // still dirty → Save failed: keep the document, abandon the deferred action (data-protection guard)
        }
        else if (outcome == SaveGuardOutcome.SaveAsThenProceed) // (untitled): native picker, then resolve via the controller (案A)
        {
            OnFileSaveAs();
            bool committed = _notebook != null && !_notebook.IsDirty;
            if (_saveGuardController.ResolveSaveAs(committed) == SaveGuardOutcome.SaveAsThenProceed) RunGuardProceed();
            else _guardProceed = null;                        // picker cancelled / write failed → Abort: keep the document, abandon the deferred action
        }
        else _guardProceed = null;                            // closed-dialog Abort no-op: abandon any deferred action
    }

    void OnGuardDiscard()
    {
        _saveGuardController.ChooseDiscard();                       // ProceedWithoutSave
        _saveGuardOverlay?.SetVisible(false);
        RunGuardProceed();                                     // proceed without saving (dirty edits discarded)
    }

    void OnGuardCancel()
    {
        _saveGuardController.ChooseCancel();                        // Abort
        _saveGuardOverlay?.SetVisible(false);                       // stay in the app
        _guardProceed = null;                                 // abandon the deferred action
    }

    void ConfirmAndQuit()
    {
        _quitConfirmed = true;
        Application.Quit();
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
        bool restoredSavedLayout = false;   // #105: a saved layout's persisted groupId was applied — DON'T factory-group
        if (!string.IsNullOrEmpty(py) && File.Exists(py) && LayoutSidecarStore.TryReadLayout(py, out var doc))
        {
            // #81: restore geometry, then decompose the document `.py` into N cell windows at the saved
            // cellPositions. If the open fails (e.g. Python not yet ready / non-marimo), fall through to
            // the File→New blank state rather than leaving a half-restored state.
            ApplyLayout(doc);
            restoredSavedLayout = true;   // #105: ApplyLayout(doc) honored the doc's groupId (RestoreFloating) — first-launch grouping must NOT clobber it
            _notebookRun?.Invalidate();   // #95 Phase 2: drop any in-flight per-cell run against the replaced notebook
            if (_coordinator.Open(py, ToVectors(doc.cellPositions)))
            {
                _currentLayoutPath = py;
                ReseedFromEditor();
                return;
            }
        }
        ApplyLayout(LayoutDocument.Default());   // fresh / unresumable → default workspace
        // #105: factory-group ONLY when no saved layout was applied. A resume whose layout READ but whose
        // _coordinator.Open FAILED already restored the doc's persisted groupId via ApplyLayout(doc) above;
        // re-minting here would clobber it (工場出荷値のみ＝saved layout を尊重・ADR-0020 D2).
        if (!restoredSavedLayout) FormFactoryBaseGroup();
        _currentLayoutPath = "";                 // untitled document
        OpenFileNewDefault();                    // #76: boot to the File→New blank state (no canonical auto-open)
    }

    // #76 (2026-06-19): the no-resume / unresumable boot state = the File→New blank document (one empty
    // cell in region_001, unbound). Identical to OnFileNew's notebook reset (`_coordinator.New()` =
    // ResetUnboundEmpty + SyncWindowsToNotebook) so boot and File→New land in the SAME state; Run stays
    // blocked until the user saves (WYSIWYR). The layout document stays UNTITLED (_currentLayoutPath = "").
    void OpenFileNewDefault()
    {
        _notebookRun?.Invalidate();   // #95 Phase 2: drop any in-flight per-cell run against the replaced notebook
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

    // ---- layout persistence (3 active dimensions: canvasView / floatingWindows / cellPositions) ----
    // #99 (ADR-0017 §6 / findings 0075 §6): the dock cluster lives entirely in `floatingWindows`
    // (base + chart + Order ticket + adopted editor shells), so the Hakoniwa-era per-mode profiles
    // and the single `panels` list are RETIRED — `hakoniwaProfiles` is left null on capture and
    // `panels` is the empty list. The schema keeps the fields (forward-evolution tolerance,
    // findings 0008 §3) so an older build that knows them does not crash on a doc written by a
    // newer build that omits them; they just carry no Hakoniwa intent any more.
    LayoutDocument CaptureLayout()
    {
        // #81: cell windows are EXCLUDED from floatingWindows (single source of truth — their
        // position is the cell-order-parallel cellPositions, regenerated FROM LIVE by the
        // coordinator, findings 0050 trap 1). #99: ALL OTHER live windows (base + chart + Order
        // + adopted editor shell) DO ride floatingWindows verbatim — that is the dock cluster's
        // single source of truth (ADR-0017 §6). #103 (ADR-0018 / findings 0075 §10): the windows
        // now live on TWO planes (front _windows = order/editor; back _dockWindows = the 6 dock
        // kinds), so capture is the UNION of both controllers into the one floatingWindows list.
        // kind disambiguates the plane on restore (RestoreFloating routes by DockShape.IsDockKind),
        // so no schema field is added (ADR-0017 §6 unchanged).
        // NOTE: zOrder in the unioned list is PER-PLANE-RELATIVE (each Capture() re-ranks its own windows
        // 0..n-1), so front and back z ranges overlap. That is harmless: the planes are separate Content
        // siblings (DockLayer always draws behind regardless of z), and RestoreFloating routes by kind and
        // BringToFronts within each controller, so same-plane relative order is preserved. Do NOT treat this
        // list as a single global z-stack.
        var nonCell = new List<FloatingWindowLayout>();
        foreach (var w in _windows.Capture().floatingWindows)
            if (w != null && w.kind != FloatingWindowCatalog.KIND_STRATEGY_EDITOR) nonCell.Add(w);
        foreach (var w in _dockWindows.Capture().floatingWindows)
            if (w != null) nonCell.Add(w);

        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),            // dead schema (forward-tolerance only)
            hakoniwaProfiles = null,                      // dead schema (forward-tolerance only)
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

    // restore order canvas → floating(non-cell) → Strategy Editor (findings 0025 §8 — Hakoniwa step
    // is retired under ADR-0017; the dock windows live in floatingWindows now). Per ADR-0017 §6 and
    // findings 0075 §0 #5, any legacy `panels` / `hakoniwaProfiles` on disk are IGNORED — pre-#99
    // saved layouts reset to the dock default placement (the 5 base windows keep the positions
    // BuildWorkspace spawned them at, and any chart windows land at DockDefaultPlacement slots).
    void ApplyLayout(LayoutDocument doc)
    {
        if (doc == null) return;
        if (doc.canvasView != null) _canvas.ApplyView(doc.canvasView);
        RestoreFloating(doc);
        // #81: cell windows are restored by the coordinator (Open/New/Sync) from the notebook + the
        // cellPositions list — NOT here. Each caller (OnFileOpen / OpenFileNewDefault / Resume) runs
        // the coordinator open + the ReseedFromEditor tail around this geometry restore (restore
        // order canvas -> floating(non-cell) -> cells -> reseed, findings 0025 §8).
    }

    // floating: adopted/existing windows repositioned IN PLACE (never destroyed); only additional
    // saved windows are spawned; ascending zOrder → BringToFront yields contiguous front order.
    // #103 (ADR-0018 / findings 0075 §10): each saved window is ROUTED to its plane by kind —
    // DockShape.IsDockKind → the back-plane _dockWindows, else the front-plane _windows. So a chart
    // saved on disk restores onto _dockLayer (1.0×) and the Order ticket onto _floatingLayer (1.2×),
    // round-tripping the depth. The two controllers re-rank z independently within their own plane.
    void RestoreFloating(LayoutDocument doc)
    {
        var wins = doc.floatingWindows;
        if (wins == null) return;

        // #104 (ADR-0019 D9 / findings 0082 §9): cross-plane group fail-safe — runtime can't form a
        // cross-plane group (snap母集合 is per-plane, ADR-0018), but a hand-edited doc or an old build
        // can. Resolve before any spawn so the loser-plane members restore as singletons; the winner's
        // remnant is dissolved at the tail if it shrunk below 2 visible/live members (shared helper).
        SplitCrossPlaneGroups(wins);

        var sorted = new List<FloatingWindowLayout>(wins);
        sorted.Sort((a, b) => (a?.zOrder ?? 0).CompareTo(b?.zOrder ?? 0));
        foreach (var w in sorted)
        {
            if (w == null || string.IsNullOrEmpty(w.id)) continue;
            // #81: SKIP cell windows — they are owned by the coordinator (cellPositions), never spawned
            // here. A legacy sidecar that still lists strategy_editor in floatingWindows must not spawn
            // an untracked duplicate cell window (it would escape the coordinator's region map).
            if (w.kind == FloatingWindowCatalog.KIND_STRATEGY_EDITOR) continue;
            var ctrl = DockShape.IsDockKind(w.kind) ? _dockWindows : _windows;
            if (ctrl.Has(w.id))
            {
                ctrl.ApplyGeometry(w);
                // #104 F1: legacy-null tolerance — null doc value MUST NOT stomp live group (same rule as
                // FloatingWindowController.Apply existing-entry branch).
                if (!string.IsNullOrEmpty(w.groupId)) ctrl.SetGroupId(w.id, w.groupId);
            }
            else
            {
                ctrl.Spawn(w.kind, w.id, w.x, w.y, w.w, w.h, w.visible, w.groupId);   // #104 Slice A: groupId-aware spawn overload
            }
            ctrl.BringToFront(w.id);
        }

        // #104 Slice F (ADR-0019 D9 / findings 0082 §9): for every groupId mentioned in the (post-split)
        // doc, ask each plane's controller to dissolve if the surviving visible/live count fell below 2.
        // The split can leave the winner with 1 member when the loser plane had all the others — the
        // SHARED dissolve helper (Slice D) handles the chain dissolve identically here.
        var groupsToCheck = new HashSet<string>();
        foreach (var w in wins)
            if (w != null && !string.IsNullOrEmpty(w.groupId)) groupsToCheck.Add(w.groupId);
        foreach (var g in groupsToCheck)
        {
            _windows.DissolveIfShrunkTo(g, 2);
            _dockWindows.DissolveIfShrunkTo(g, 2);
        }
    }

    // #104 (ADR-0019 D9 / findings 0082 §9): resolve same-groupId members across the front/back planes.
    // For each group: count members per plane (kind → plane via DockShape.IsDockKind), pick the majority
    // plane (tie → DOCK plane, since the core members live there and Hakoniwa identity is protected),
    // and clear the LOSER plane members' groupId on the doc entries before spawn/restore. The helper
    // mutates `wins` in place — RestoreFloating then plays back the cleaned doc into the controllers.
    // `public` so the AFK gate (Section30) pins this pure data transformation directly from the
    // Editor assembly without needing reflection or InternalsVisibleTo.
    public static void SplitCrossPlaneGroups(List<FloatingWindowLayout> wins)
    {
        var byGroup = new Dictionary<string, List<FloatingWindowLayout>>();
        foreach (var w in wins)
        {
            if (w == null || string.IsNullOrEmpty(w.groupId)) continue;
            if (!byGroup.TryGetValue(w.groupId, out var list)) byGroup[w.groupId] = list = new List<FloatingWindowLayout>();
            list.Add(w);
        }
        foreach (var kv in byGroup)
        {
            int frontCount = 0, backCount = 0;
            foreach (var w in kv.Value)
            {
                if (DockShape.IsDockKind(w.kind)) backCount++;
                else frontCount++;
            }
            if (frontCount == 0 || backCount == 0) continue;   // single-plane group, no split needed
            bool dockWins = backCount >= frontCount;            // tie ⇒ dock wins (Hakoniwa identity bias)
            foreach (var w in kv.Value)
            {
                bool wIsDock = DockShape.IsDockKind(w.kind);
                if (wIsDock != dockWins) w.groupId = null;   // loser-plane member becomes a singleton
            }
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
        _scenario.Universe.Changed -= SyncChartWindowsToUniverse;   // #99 chart-window sync unsubscribe (no orphan handler)
        ThemeService.Changed -= ApplyViewportTheme;   // viewport-field theme unsubscribe (no orphan handler)
        if (!Application.isBatchMode) Application.wantsToQuit -= OnWantsToQuit;   // #89 quit-confirm unsubscribe
        _notebookRunLane?.Dispose();              // #95 Phase 2 土台: stop the per-cell RUN worker thread
        _host.Stop();                             // 3-7. force_stop → poll stop → bounded join → no Shutdown
        Debug.Log("[BackcastWorkspaceRoot] teardown complete.");
    }

    void OnApplicationQuit() => StopAndDispose();
    void OnDestroy() => StopAndDispose();
}
