// BackcastWorkspaceProbe.cs — issue #59 "Backcast workspace root" (headless AFK regression gate)
//
// The headless, Python-FREE, render-FREE gate for the composition root (findings 0025 §11). Run:
//
//   <Unity> -batchmode -nographics -projectPath /Users/sasac/backcast \
//           -executeMethod BackcastWorkspaceProbe.Run -logFile <log>
//   # expect: [BACKCAST WORKSPACE PASS] ... / exit=0
//
// It (re)builds BackcastWorkspace.unity, asserts the authored hierarchy + serialized references +
// build settings, and value-asserts the Python-FREE seams: the 4-dimension layout round-trip, the
// adopt-not-respawn floating restore, the OnceGate teardown idempotency, and the single-Play-owner
// decision (via an INJECTED predicate — never initializes Python). The visual 1-screen check,
// zoomed swap, real Replay streaming, and real Play-stop teardown are the owner-run HITL.
//
// SECTIONS (findings 0025 §11):
//   1. scene built + only enabled build scene + required objects/components/serialized refs
//   2. authored hierarchy (Viewport/Content/{HakoniwaRoot,FloatingWindowLayer}) + chrome OUTSIDE Content
//   3. Hakoniwa Startup=slot0 / Chart=slot1 + swap apply (live RectTransforms)
//   4. non-default 4-dimension disk round-trip (temp path + a real temp .py)
//   5. adopt: scene-authored editor window registered + restored IN PLACE (same instance, not respawned)
//   6. OnceGate: save/force-stop guard fires exactly once across repeated Stop/Dispose
//   7. single-Play-owner decision via injected predicate (no Python)

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

public static class BackcastWorkspaceProbe
{
    const float EPS = 1e-3f;
    const string WINDOW_ID = "strategy_editor:region_001";

    static string TempDir => Path.Combine(Application.temporaryCachePath, "backcast_workspace_probe");
    static string TempLayout => Path.Combine(TempDir, "layout.json");
    static string TempPy => Path.Combine(TempDir, "strategy.py");

    public static void Run()
    {
        string fail = null;
        var spawned = new List<GameObject>();
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
            Directory.CreateDirectory(TempDir);
            File.WriteAllText(TempPy, "# probe strategy\nclass S:\n    pass\n");

            fail = Section1_SceneAndRefs()
                ?? Section2_Hierarchy()
                ?? Section3_HakoniwaSlots(spawned)
                ?? Section4_DiskRoundTrip()
                ?? Section5_AdoptNotRespawn(spawned)
                ?? Section6_OnceGate()
                ?? Section7_Ownership()
                ?? Section8_SharedUniverse()
                ?? Section9_RunCommitRePrimesWriteback()
                ?? Section10_ChartTileFamily()
                ?? Section11_EditorSeedsUniverseAndGatesRun()
                ?? Section12_PerModeProfileFlipAndRestore()
                ?? Section13_ChromeZOrderLayering()
                ?? Section15_SidebarOverflowClipping()   // #84: complements Section13 (clip ↔ z-layer)
                ?? Section14_FileOpenBareStrategy()
                ?? Section16_HakoniwaStageWiring();   // #93/0068 §15: World-Space Stage + perspective RT wiring
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }
        finally
        {
            foreach (var go in spawned) if (go != null) UnityEngine.Object.DestroyImmediate(go);
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
        }

        if (fail == null)
        {
            Debug.Log("[BACKCAST WORKSPACE PASS] all sections green.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[BACKCAST WORKSPACE FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ── 1. assert the COMMITTED scene is the only enabled build scene + refs wired ──
    // Read-only: a regression gate must not mutate source-controlled assets, so it asserts the
    // committed BackcastWorkspace.unity / EditorBuildSettings rather than rebuilding them (the
    // builder is the explicit Tools > Backcast > Build Workspace Scene authoring step).
    static string Section1_SceneAndRefs()
    {
        if (!File.Exists(BackcastWorkspaceSceneBuilder.ScenePath))
            return "scene missing — run Tools > Backcast > Build Workspace Scene first";

        var scenes = EditorBuildSettings.scenes;
        int enabled = 0;
        bool hasOurs = false;
        foreach (var s in scenes) { if (s.enabled) { enabled++; if (s.path == BackcastWorkspaceSceneBuilder.ScenePath) hasOurs = true; } }
        if (!hasOurs) return "BackcastWorkspace.unity not in build settings";
        if (enabled != 1) return $"expected exactly 1 enabled build scene, got {enabled}";
        if (scenes.Length == 0 || scenes[0].path != BackcastWorkspaceSceneBuilder.ScenePath || !scenes[0].enabled)
            return "BackcastWorkspace.unity must be the FIRST enabled build scene";

        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);

        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "BackcastWorkspaceRoot component missing in scene";

        var so = new SerializedObject(root);
        string[] refs =
        {
            "_centerWorkspace", "_footerContainer", "_menuBarView", "_sidebarView",
            "_viewport", "_content", "_inputSurface", "_hakoniwaRoot", "_startupTile",
            // #93 perspective stage: the RT display surface in Content (findings 0068 §15 F1).
            "_hakoniwaRawImage",
            "_chartTile", "_floatingLayer", "_strategyEditorWindow", "_strategyEditorBody",
            "_strategyEditorTitleInput",
            // #23 re-home: three live data tiles + the adopted Order ticket window.
            "_ordersTile", "_positionsTile", "_runResultTile",
            "_orderWindow", "_orderWindowBody", "_orderWindowTitleInput",
        };
        foreach (var r in refs)
        {
            var p = so.FindProperty(r);
            if (p == null) return "serialized field missing on root: " + r;
            if (p.objectReferenceValue == null) return "serialized reference NOT wired: " + r;
        }
        return null;
    }

    // ── 2. authored hierarchy + chrome outside Content ──
    static string Section2_Hierarchy()
    {
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        var so = new SerializedObject(root);
        RectTransform Ref(string n) => so.FindProperty(n).objectReferenceValue as RectTransform;

        var viewport = Ref("_viewport");
        var content = Ref("_content");
        var hako = Ref("_hakoniwaRoot");
        var layer = Ref("_floatingLayer");
        var center = Ref("_centerWorkspace");
        var footer = Ref("_footerContainer");
        var window = Ref("_strategyEditorWindow");
        var body = Ref("_strategyEditorBody");

        if (content.parent != viewport) return "Content is not a child of Viewport";
        // #93 perspective stage (findings 0068 §15 F1): HakoniwaRoot moved OUT of Content onto the
        // World-Space Hakoniwa Stage canvas (Content now holds the RawImage showing the RT). This is the
        // RED→GREEN inversion of the old "HakoniwaRoot is a child of Content" assertion (tdd inversion).
        if (hako.parent == content) return "HakoniwaRoot still a child of Content (must move to the World-Space Hakoniwa Stage canvas, #93/0068 §15)";
        var hakoCanvas = hako.GetComponentInParent<Canvas>();
        if (hakoCanvas == null) return "HakoniwaRoot has no parent Canvas (Stage canvas missing)";
        if (hakoCanvas.renderMode != RenderMode.WorldSpace) return "Hakoniwa Stage canvas is not RenderMode.WorldSpace";
        if (layer.parent != content) return "FloatingWindowLayer is not a child of Content";
        if (window.parent != layer) return "Strategy Editor window is not a child of FloatingWindowLayer";
        if (body.parent != window) return "Strategy Editor body is not a child of the window";
        if (viewport.GetComponent<InfiniteCanvasInputSurface>() == null) return "Viewport missing InfiniteCanvasInputSurface";

        var menu = FindByComponent<MenuBarView>();
        var side = FindByComponent<UniverseSidebarView>();
        if (menu == null) return "MenuBarView missing";
        if (side == null) return "UniverseSidebarView missing";

        // chrome (menu/sidebar/footer) must be OUTSIDE Content (findings 0025 §3/§6).
        if (IsDescendant(menu.transform, content)) return "MenuBar is INSIDE Content (must be chrome, outside)";
        if (IsDescendant(side.transform, content)) return "Sidebar is INSIDE Content (must be chrome, outside)";
        if (IsDescendant(footer, content)) return "Footer is INSIDE Content (must be chrome, outside)";

        // the infinite-space FIELD fills the WHOLE window (full-screen stretch) and is the BACKMOST
        // sibling, so chrome + the uGUI footer draw on top and the skybox is fully covered (owner 2026-06-15).
        if (center.anchorMin != Vector2.zero || center.anchorMax != Vector2.one)
            return "CenterWorkspace field must be full-screen stretch (anchors 0..1)";
        if (center.offsetMin != Vector2.zero || center.offsetMax != Vector2.zero)
            return "CenterWorkspace field must be full-screen (zero offsets)";
        if (center.GetSiblingIndex() != 0)
            return "CenterWorkspace field must be the BACKMOST sibling (chrome overlays on top)";
        return null;
    }

    // ── 3. Hakoniwa Startup=slot0 / Chart=slot1 + swap apply ──
    static string Section3_HakoniwaSlots(List<GameObject> spawned)
    {
        var rootGo = new GameObject("probe_hako", typeof(RectTransform));
        spawned.Add(rootGo);
        var root = (RectTransform)rootGo.transform;
        root.sizeDelta = new Vector2(800f, 600f);
        var startup = NewChild(root, "startup");
        var chart = NewChild(root, "chart");

        var hako = new HakoniwaController(root,
            new Dictionary<string, RectTransform> { { "startup", startup }, { "chart", chart } },
            new[] { "startup", "chart" });

        var cap = hako.Capture();
        var ps = cap.Find("startup");
        var pc = cap.Find("chart");
        if (ps == null || pc == null) return "Hakoniwa capture missing tiles";
        if (ps.slot != 0) return $"Startup must be slot 0 (got {ps.slot})";
        if (pc.slot != 1) return $"Chart must be slot 1 (got {pc.slot})";

        // swap apply: chart→slot0, startup→slot1.
        var swap = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>
            {
                new PanelLayout("chart", 0, true, new LayoutRect(0, 0, 0.5f, 1)),
                new PanelLayout("startup", 1, true, new LayoutRect(0.5f, 0, 1, 1)),
            },
        };
        hako.Apply(swap);
        var cap2 = hako.Capture();
        if (cap2.Find("chart").slot != 0 || cap2.Find("startup").slot != 1) return "Hakoniwa swap apply did not reorder slots";
        return null;
    }

    // ── 4. non-default 4-dimension disk round-trip ──
    static string Section4_DiskRoundTrip()
    {
        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>
            {
                new PanelLayout("chart", 0, true, new LayoutRect(0f, 0f, 0.5f, 1f)),
                new PanelLayout("startup", 1, true, new LayoutRect(0.5f, 0f, 1f, 1f)),
            },
            canvasView = new CanvasView(123f, -45f, 1.5f),
            floatingWindows = new List<FloatingWindowLayout>
            {
                new FloatingWindowLayout(WINDOW_ID, "strategy_editor", -120f, 80f, 640f, 480f, 0, true),
            },
            strategyEditors = new List<StrategyEditorState>
            {
                new StrategyEditorState(WINDOW_ID, TempPy),
            },
        };

        LayoutStore.Save(doc, TempLayout);
        if (!File.Exists(TempLayout)) return "layout sidecar not written";
        var loaded = LayoutStore.Load(TempLayout);

        if (!LayoutDocument.StructurallyEqual(loaded, doc, EPS)) return "4-dimension disk round-trip not structurally equal";
        // non-vacuous: loaded must DIFFER from Default (proves we persisted the edits).
        if (LayoutDocument.StructurallyEqual(loaded, LayoutDocument.Default(), EPS)) return "round-trip collapsed to Default (vacuous)";
        // explicit per-dimension proof.
        if (loaded.canvasView == null || Mathf.Abs(loaded.canvasView.zoom - 1.5f) > EPS) return "canvasView zoom not persisted";
        if (loaded.floatingWindows == null || loaded.floatingWindows.Count != 1) return "floatingWindows not persisted";
        if (loaded.strategyEditors == null || loaded.strategyEditors.Count != 1 || loaded.strategyEditors[0].filePath != TempPy)
            return "strategyEditors open-file not persisted";
        return null;
    }

    // ── 5. adopt: scene-authored window registered + restored in place (same instance, not respawned) ──
    static string Section5_AdoptNotRespawn(List<GameObject> spawned)
    {
        var layerGo = new GameObject("probe_layer", typeof(RectTransform));
        spawned.Add(layerGo);
        var layer = (RectTransform)layerGo.transform;

        int factoryCalls = 0;
        var windows = new FloatingWindowController(layer, FloatingWindowCatalog.Default(),
            (spec, id) => { factoryCalls++; var go = new GameObject("spawn_" + id, typeof(RectTransform)); var rt = (RectTransform)go.transform; return rt; },
            go => UnityEngine.Object.DestroyImmediate(go));

        // a pre-existing (scene-authored) window.
        var authoredGo = new GameObject("authored_window", typeof(RectTransform));
        var authored = (RectTransform)authoredGo.transform;
        authored.anchoredPosition = new Vector2(-300f, 220f);
        authored.sizeDelta = new Vector2(620f, 460f);

        var adopted = windows.Adopt(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, WINDOW_ID, authored);
        if (adopted != authored) return "Adopt did not return the authored instance";
        if (!windows.Has(WINDOW_ID)) return "adopted window not registered";
        if (factoryCalls != 0) return "Adopt must NOT call the spawn factory";

        // restore (root-style): ApplyGeometry for the present id; never destroy.
        var restored = new FloatingWindowLayout(WINDOW_ID, "strategy_editor", -120f, 80f, 640f, 480f, 0, true);
        if (!windows.ApplyGeometry(restored)) return "ApplyGeometry returned false for adopted window";
        if (windows.RectOf(WINDOW_ID) != authored) return "adopted window was REPLACED (must restore in place)";
        if (authored == null) return "adopted window was destroyed";
        if (Mathf.Abs(authored.anchoredPosition.x - (-120f)) > EPS) return "adopted geometry not applied (x)";
        if (factoryCalls != 0) return "restore-in-place must not spawn";

        // an ADDITIONAL saved window IS spawned via the factory.
        var added = windows.Spawn("strategy_editor", "strategy_editor:region_002", 10f, 10f, 300f, 200f, true);
        if (added == null) return "additional saved window not spawned";
        if (factoryCalls != 1) return "additional window must use the spawn factory exactly once";
        if (windows.Count != 2) return "expected 2 windows after adopt + spawn";
        return null;
    }

    // ── 6. OnceGate: guarded action fires exactly once across repeated calls ──
    static string Section6_OnceGate()
    {
        var gate = new OnceGate();
        int guarded = 0;
        for (int i = 0; i < 5; i++) if (gate.TryEnter()) guarded++;
        if (guarded != 1) return $"OnceGate guarded action fired {guarded} times (expected 1)";
        if (!gate.Entered) return "OnceGate.Entered false after entry";
        return null;
    }

    // ── 7. single-Play-owner decision via injected predicate (no Python) ──
    static string Section7_Ownership()
    {
        // (ownPlay, isBatchMode, pythonAlreadyInitialized, weAlreadyOwn)
        if (!WorkspaceOwnership.ShouldClaim(true, false, false, false))
            return "root should claim when owner + interactive + free interpreter";
        if (WorkspaceOwnership.ShouldClaim(true, false, true, false))
            return "root must DECLINE when Python already owned by ANOTHER (per-part HITL safe-refuse mirror)";
        if (!WorkspaceOwnership.ShouldClaim(true, false, true, true))
            return "root must RECLAIM when it already owns Python (re-Play without domain reload)";
        if (WorkspaceOwnership.ShouldClaim(false, false, false, false))
            return "root must DECLINE when _ownPlay is off";
        if (WorkspaceOwnership.ShouldClaim(true, true, false, false))
            return "root must DECLINE in batchmode (no Python init in the headless gate)";
        return null;
    }

    // ── 8. single universe per workspace: the sidebar edits the SAME InstrumentRegistry that
    // OnRun reads (_scenario.Universe). THE #59 配線漏れ kill — before the fix the root handed the
    // sidebar a fresh `new InstrumentRegistry()`, so sidebar +Add/×remove never reached the run set.
    // Headlessly composes the real root (Python-FREE: Awake's Python init is gated off in batchmode;
    // here we invoke only ResolvePaths + BuildWorkspace, which never touch Python). ──
    static string Section8_SharedUniverse()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "BackcastWorkspaceRoot missing for shared-universe check";

        var ty = typeof(BackcastWorkspaceRoot);
        const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

        var fontField = ty.GetField("_font", BF);
        var resolve = ty.GetMethod("ResolvePaths", BF);
        var build = ty.GetMethod("BuildWorkspace", BF);
        if (fontField == null || resolve == null || build == null) return "root internals not found (renamed?)";

        fontField.SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        resolve.Invoke(root, null);
        build.Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var sidebar = ty.GetField("_sidebarCtrl", BF)?.GetValue(root) as UniverseSidebarController;
        if (scenario == null) return "could not read _scenario";
        if (sidebar == null) return "could not read _sidebarCtrl";

        // THE kill: same instance, not two phantom registries.
        if (!ReferenceEquals(sidebar.Registry, scenario.Universe))
            return "sidebar registry is NOT _scenario.Universe (配線漏れ: one-universe-per-workspace breached)";

        // non-vacuous: an edit on the run-side SoT is visible through the sidebar's rows.
        const string probe = "PROBE.TSE";
        scenario.Universe.Add(probe);
        bool seen = false;
        foreach (var r in sidebar.Rows()) if (r.Id == probe) seen = true;
        scenario.Universe.Remove(probe);
        if (!seen) return "scenario universe edit not visible through sidebar (registries diverged)";
        return null;
    }

    // ── 9. Run-Commit RE-PRIMES the sidebar writeback (#59 cross-writer staleness). The startup-tile
    // text edits the SHARED registry WITHOUT flushing; Commit (TryStartRun) writes those instruments to
    // the sidecar, but the sidebar's writeback still holds the PRE-Commit set as _lastFlushed. Without a
    // re-prime, a later sidebar edit that nets back to a pre-Commit set sees cur == _lastFlushed, SKIPS
    // the flush, and the sidecar keeps a phantom instrument (silent disk divergence). Drives the REAL
    // root's OnRun headlessly: in batchmode the root is NOT owner, so OnRun commits + re-primes, then
    // bails at the _isOwner gate BEFORE touching the Host/Python. ──
    static string Section9_RunCommitRePrimesWriteback()
    {
        string strat = Path.Combine(TempDir, "run_reprime_strategy.py");
        File.WriteAllText(strat, "# probe\nclass S:\n    pass\n");

        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "reprime: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());   // #81: Python-free cell synthesis
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        var sidebar = ty.GetField("_sidebarCtrl", BF).GetValue(root) as UniverseSidebarController;
        if (scenario == null || sidebar == null) return "reprime: root internals not found (renamed?)";

        // #78/#81: OnRun's run-gate reads the NOTEBOOK via RegistryStrategyFileProvider (not an env
        // _strategyFile) — bind the notebook to a real temp strategy so the gate is supplyable.
        var coordinator = ty.GetField("_coordinator", BF).GetValue(root) as NotebookCellCoordinator;
        if (coordinator == null) return "reprime: coordinator missing";
        if (!coordinator.Open(strat, null)) return "reprime: notebook failed to Open the temp strategy";

        // a VALID scenario whose universe settled to [A], then a TILE add of B with NO flush (text edits
        // never flush) -> registry=[A,B] while the writeback's _lastFlushed is still [A] (the stale state).
        scenario.SetStart("2024-01-01");
        scenario.SetEnd("2024-06-01");
        scenario.SetGranularity(GranularityChoice.Daily);
        scenario.SetInitialCash("1000000");
        scenario.Universe.ReplaceAll(new[] { "A.TSE" });
        sidebar.PrimeWritebackFromCurrent();                 // _lastFlushed = [A]
        scenario.Universe.Add("B.TSE");                      // tile add, no flush -> [A,B] vs _lastFlushed [A]

        // drive the REAL OnRun: Commit writes the sidecar [A,B]; the fix re-primes _lastFlushed=[A,B];
        // then (not owner in batchmode) it returns before the Host/Python path.
        ty.GetMethod("OnRun", BF).Invoke(root, null);

        // the gate must have been Ready — proves OnRun reached the re-prime line, not an early bail.
        var afterCommit = ScenarioSidecarStore.ReadScenario(strat);
        if (afterCommit == null || afterCommit.Instruments.Count != 2)
            return "reprime: OnRun did not Commit [A,B] (run-gate not Ready — probe setup broke)";

        // the kill: a sidebar × of B (which flushes) must now WRITE the sidecar back to [A]. Without the
        // re-prime, _lastFlushed==cur==[A] -> Flush SKIPS -> the sidecar keeps the phantom B.
        sidebar.Remove("B.TSE", UniverseSourceMode.Replay, new BoundStrategyFileProvider(strat));
        var afterRemove = ScenarioSidecarStore.ReadScenario(strat);
        if (afterRemove == null) return "reprime: sidecar missing after remove";
        if (afterRemove.Instruments.Count != 1 || afterRemove.Instruments[0] != "A.TSE")
            return "reprime: sidebar × did NOT flush (writeback stale: _lastFlushed not re-primed at Commit -> phantom B on disk)";
        return null;
    }

    // ── 10. chart tile family (#60): chart:<id> tiles track the universe SoT (spawn/despawn on
    // InstrumentRegistry.Changed), base is [startup] only (the fixed "chart" tile is retired), and the
    // membership orchestrator grows the box derived from n. Drives the REAL root headlessly. ──
    static string Section10_ChartTileFamily()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "charttile: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        var hako = ty.GetField("_hako", BF).GetValue(root) as HakoniwaController;
        var chartViews = ty.GetField("_chartViews", BF).GetValue(root) as System.Collections.IDictionary;
        var hakoRoot = ty.GetField("_hakoniwaRoot", BF).GetValue(root) as RectTransform;
        if (scenario == null || hako == null || chartViews == null || hakoRoot == null)
            return "charttile: root internals not found (renamed?)";

        // #61: base = Replay shape [startup, buying_power, orders, positions, run_result] (5 tiles);
        // the old fixed "chart" tile is retired (not in the controller order).
        if (hako.SlotOf("startup") < 0) return "charttile: base startup tile missing";
        if (hako.SlotOf("chart") >= 0) return "charttile: retired fixed 'chart' tile still in the grid";
        foreach (var id in HakoniwaBaseTiles.PanelOrder)
            if (hako.SlotOf(id) < 0) return "charttile: #61 base panel tile missing: " + id;

        // membership tracks the universe SoT: a known 2-instrument universe spawns 2 chart tiles.
        scenario.Universe.ReplaceAll(new[] { "AAA.TSE", "BBB.TSE" });
        if (hako.SlotOf("chart:AAA.TSE") < 0 || hako.SlotOf("chart:BBB.TSE") < 0)
            return "charttile: chart tiles not spawned for universe instruments (Changed not wired?)";
        if (!chartViews.Contains("AAA.TSE") || !chartViews.Contains("BBB.TSE"))
            return "charttile: ChartView not created per instrument";
        // 5 base (Replay) + 2 chart = 7.
        if (hako.Count != 7) return "charttile: expected 5 base + 2 chart = 7 tiles, got " + hako.Count;

        // box-grow: the box SIZE is derived from n (orchestrator-applied, NOT persisted).
        var min = new Vector2(280f, 180f); var def = new Vector2(700f, 450f);
        var expected = HakoniwaGridMath.ComputeBoxSize(hako.Count, min, 0f, def);
        if ((hakoRoot.sizeDelta - expected).sqrMagnitude > EPS)
            return "charttile: box-grow not applied (got " + hakoRoot.sizeDelta + ", expected " + expected + ")";

        // remove one instrument -> its chart tile despawns; box re-derives.
        scenario.Universe.Remove("AAA.TSE");
        if (hako.SlotOf("chart:AAA.TSE") >= 0) return "charttile: removed instrument's chart tile still present";
        if (chartViews.Contains("AAA.TSE")) return "charttile: removed instrument's ChartView not cleared";
        var expected2 = HakoniwaGridMath.ComputeBoxSize(hako.Count, min, 0f, def);
        if ((hakoRoot.sizeDelta - expected2).sqrMagnitude > EPS) return "charttile: box-grow not re-applied after despawn";
        return null;
    }

    // ── 11. #78 WYSIWYR: the universe is seeded from THE EDITOR's .py (sidecar ?? inline fallback —
    // the #66 mechanism, re-homed from the env-default to the loaded editor, findings 0044 §2-3), and an
    // UNBOUND editor seeds NOTHING and blocks Run (the "未ロード→走らない" guarantee). Drives the real
    // SeedScenarioFromEditor seam + TryStartRun via the editor's registered RegistryStrategyFileProvider.
    // This INVERTS the old #66 env-seed assertion (which read BACKCAST_HITL_STRATEGY): Run/seed now follow
    // the editor, never an env path. ──
    static string Section11_EditorSeedsUniverseAndGatesRun()
    {
        var ty = typeof(BackcastWorkspaceRoot);
        const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

        // helper: compose the root headlessly (no Python; no RestoreLayout so the adopted editor is unbound).
        const string NOTEBOOK_ID = "strategy_editor:notebook";   // #81: the run path resolves the notebook here
        BackcastWorkspaceRoot Compose()
        {
            EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
            var r = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
            if (r == null) return null;
            ty.GetField("_font", BF).SetValue(r, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            r.SetSynthesizer(new FakeMarimoSynthesizer());        // #81: Python-free cell synthesis
            ty.GetMethod("BuildWorkspace", BF).Invoke(r, null);   // registers the notebook aggregate into _registry
            return r;
        }
        NotebookCellCoordinator Coord(BackcastWorkspaceRoot r) => ty.GetField("_coordinator", BF).GetValue(r) as NotebookCellCoordinator;
        void Seed(BackcastWorkspaceRoot r) => ty.GetMethod("SeedScenarioFromEditor", BF).Invoke(r, null);
        void Reseed(BackcastWorkspaceRoot r) => ty.GetMethod("ReseedFromEditor", BF).Invoke(r, null);

        // (a) UNBOUND editor: SeedScenarioFromEditor is a NO-OP (universe empty) and Run is BlockedNoStrategy.
        var root0 = Compose();
        if (root0 == null) return "editor-seed: BackcastWorkspaceRoot missing (unbound leg)";
        var scenario0 = ty.GetField("_scenario", BF).GetValue(root0) as ScenarioStartupController;
        var registry0 = ty.GetField("_registry", BF).GetValue(root0) as StrategyProviderRegistry;
        if (scenario0 == null || registry0 == null) return "editor-seed: could not read _scenario/_registry";
        Seed(root0);
        if (scenario0.Universe.Count != 0)
            return "editor-seed: UNBOUND editor seeded a non-empty universe (got [" +
                   string.Join(",", scenario0.Universe.Ids) + "]) — #78: nothing loaded must seed nothing";
        var gate0 = scenario0.TryStartRun(new RegistryStrategyFileProvider(registry0, NOTEBOOK_ID));
        if (gate0.Gate != RunGate.BlockedNoStrategy)
            return "editor-seed: UNBOUND notebook did NOT block Run (gate=" + gate0.Gate + ") — #78: 空エディタ→Run封鎖";

        // (b) inline-only: bind the editor to a .py with an inline SCENARIO and NO sidecar → seed from inline.
        string inlinePy = Path.Combine(TempDir, "inline_only.py");
        File.WriteAllText(inlinePy,
            "SCENARIO = {\n" +
            "    'schema_version': 2,\n" +
            "    'instruments': ['WIRE.TSE', 'WIRE2.TSE'],\n" +
            "    'start': '2024-01-01',\n" +
            "    'end': '2024-02-01',\n" +
            "    'granularity': 'Daily',\n" +
            "    'initial_cash': 1_000_000,\n" +
            "}\n");
        string inlineSidecar = ScenarioSidecarStore.SidecarPathFor(inlinePy);
        if (File.Exists(inlineSidecar)) File.Delete(inlineSidecar);   // ensure no sidecar shadows the inline

        var root1 = Compose();
        if (root1 == null) return "editor-seed: BackcastWorkspaceRoot missing (inline leg)";
        var scenario1 = ty.GetField("_scenario", BF).GetValue(root1) as ScenarioStartupController;
        var coord1 = Coord(root1);
        if (scenario1 == null || coord1 == null) return "editor-seed: could not read _scenario / coordinator";
        if (!coord1.Open(inlinePy, null)) return "editor-seed: notebook failed to Open the inline .py";
        Seed(root1);
        var ids = scenario1.Universe.Ids;
        if (ids.Count != 2 || ids[0] != "WIRE.TSE" || ids[1] != "WIRE2.TSE")
            return "editor-seed: universe NOT seeded from the EDITOR's inline SCENARIO (got [" +
                   string.Join(",", ids) + "]) — #78: Run/seed must follow the editor";
        if (scenario1.Params.Start != "2024-01-01" || scenario1.Params.End != "2024-02-01")
            return "editor-seed: window not seeded from the editor's inline SCENARIO";

        // (c) sidecar wins over inline (Populate: ReadScenario ?? fallback), keyed to the EDITOR's .py.
        string bothPy = Path.Combine(TempDir, "sidecar_wins.py");
        File.WriteAllText(bothPy,
            "SCENARIO = {'schema_version': 2, 'instruments': ['INLINE.TSE'], 'start': '2020-01-01', " +
            "'end': '2020-02-01', 'granularity': 'Daily', 'initial_cash': 1000000}\n");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            bothPy, new StartupParamsForWrite("2030-01-01", "2030-02-01", "Minute", "9000000"),
            new[] { "SIDECAR.TSE" });

        var root2 = Compose();
        if (root2 == null) return "editor-seed: BackcastWorkspaceRoot missing (sidecar-wins leg)";
        var scenario2 = ty.GetField("_scenario", BF).GetValue(root2) as ScenarioStartupController;
        var coord2 = Coord(root2);
        if (scenario2 == null || coord2 == null) return "editor-seed: could not read _scenario / coordinator (sidecar-wins)";
        if (!coord2.Open(bothPy, null)) return "editor-seed: notebook failed to Open the sidecar-wins .py";
        Seed(root2);
        var ids2 = scenario2.Universe.Ids;
        if (ids2.Count != 1 || ids2[0] != "SIDECAR.TSE")
            return "editor-seed: sidecar did NOT win over inline (got [" + string.Join(",", ids2) + "])";

        // (d) FULL open path (#81): opening the notebook document binds it to inlinePy, then the open
        // caller's ReseedFromEditor tail seeds the universe AND re-syncs the Startup tile's Start field
        // (the seed runs AFTER BuildWorkspace's SyncFieldsFromController, so the tile scalars — which have
        // no Changed event, unlike universe — would otherwise stay blank while Run uses the seeded Params,
        // findings 0044 §6). The reseed moved OUT of ApplyLayout into the open callers under #81, so this
        // drives coordinator.Open + ReseedFromEditor (exactly what OnFileOpen / Resume now do).
        var root3 = Compose();
        if (root3 == null) return "editor-seed: BackcastWorkspaceRoot missing (restore leg)";
        var scenario3 = ty.GetField("_scenario", BF).GetValue(root3) as ScenarioStartupController;
        var tile3 = ty.GetField("_tile", BF).GetValue(root3) as ScenarioStartupTile;
        var coord3 = Coord(root3);
        if (scenario3 == null || tile3 == null || coord3 == null) return "editor-seed: could not read _scenario / _tile / coordinator (restore leg)";
        if (!coord3.Open(inlinePy, null)) return "editor-seed: notebook failed to Open the inline .py (restore leg)";
        Reseed(root3);
        var ids3 = scenario3.Universe.Ids;
        if (ids3.Count != 2 || ids3[0] != "WIRE.TSE")
            return "editor-seed: ApplyLayout did not seed the universe from the restored editor (got [" +
                   string.Join(",", ids3) + "])";
        var startField = typeof(ScenarioStartupTile)
            .GetField("_startField", BF)?.GetValue(tile3) as UnityEngine.UI.InputField;
        if (startField == null) return "editor-seed: could not read tile _startField";
        if (startField.text != "2024-01-01")
            return "editor-seed: Startup tile Start field NOT re-synced after restore-seed (shows [" +
                   startField.text + "], Params=" + scenario3.Params.Start + ") — WYSIWYR break (findings 0044 §6)";
        return null;
    }

    // ── 14. File→Open opens a BARE strategy .py (no layout key) — #80 intent / findings 0051 ──
    // The owner's repro: File→Open a fresh v19 whose <strategy>.json carries ONLY a "scenario" key (no
    // "layout"). The OLD strict read ABORTED ("無効な layout"); the fix OPENS it bare (keep the current
    // geometry, reseed so Run unblocks). A CORRUPT sidecar is ALSO opened bare (owner D3) — never abort.
    // D4's no-wipe guarantee still holds: geometry is touched ONLY by ApplyLayout, which a bare open skips.
    // Drives the real OnFileOpen (StubFileDialog → the picked .py), not a hand-called coordinator.Open.
    static string Section14_FileOpenBareStrategy()
    {
        var ty = typeof(BackcastWorkspaceRoot);
        const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

        BackcastWorkspaceRoot Compose()
        {
            EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
            var r = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
            if (r == null) return null;
            ty.GetField("_font", BF).SetValue(r, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
            r.SetSynthesizer(new FakeMarimoSynthesizer());        // Python-free cell synthesis
            ty.GetMethod("BuildWorkspace", BF).Invoke(r, null);   // _coordinator / _scenario / _tile
            return r;
        }
        string CurrentPath(BackcastWorkspaceRoot r) => ty.GetField("_currentLayoutPath", BF).GetValue(r) as string;
        ScenarioStartupController Scenario(BackcastWorkspaceRoot r) => ty.GetField("_scenario", BF).GetValue(r) as ScenarioStartupController;
        void FileOpen(BackcastWorkspaceRoot r) => ty.GetMethod("OnFileOpen", BF).Invoke(r, null);

        // (a) scenario-only sidecar = the v19 shape (<strategy>.json has "scenario", NO "layout").
        string barePy = Path.Combine(TempDir, "open_bare.py");
        File.WriteAllText(barePy, "x = 1\n");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            barePy, new StartupParamsForWrite("2025-03-03", "2025-03-07", "Daily", "1000000"),
            new[] { "BARE.TSE" });
        if (LayoutSidecarStore.TryReadLayout(barePy, out _))   // precondition: must NOT have a layout key
            return "S14a: precondition broken — scenario-only sidecar unexpectedly carries a layout key";

        var rootA = Compose();
        if (rootA == null) return "S14a: BackcastWorkspaceRoot missing";
        rootA.SetFileDialog(new StubFileDialog { NextResult = barePy });
        FileOpen(rootA);

        if (CurrentPath(rootA) != Path.GetFullPath(barePy))
            return "S14a: File→Open ABORTED a scenario-only .py (currentPath=[" + CurrentPath(rootA) +
                   "]) — #80/0051: a bare v19 (no layout key) must OPEN, not abort";
        var idsA = Scenario(rootA).Universe.Ids;
        if (idsA.Count != 1 || idsA[0] != "BARE.TSE")
            return "S14a: bare open did NOT reseed the universe from the scenario sidecar (got [" +
                   string.Join(",", idsA) + "]) — Run would stay blocked";

        // (b) CORRUPT sidecar JSON → STILL opens bare (owner D3); universe stays empty (unreadable).
        string corruptPy = Path.Combine(TempDir, "open_corrupt.py");
        File.WriteAllText(corruptPy, "y = 2\n");
        File.WriteAllText(ScenarioSidecarStore.SidecarPathFor(corruptPy), "{ not json");

        var rootB = Compose();
        if (rootB == null) return "S14b: BackcastWorkspaceRoot missing";
        rootB.SetFileDialog(new StubFileDialog { NextResult = corruptPy });
        FileOpen(rootB);

        if (CurrentPath(rootB) != Path.GetFullPath(corruptPy))
            return "S14b: File→Open ABORTED a corrupt-sidecar .py (currentPath=[" + CurrentPath(rootB) +
                   "]) — owner D3: a corrupt sidecar must STILL open bare (no abort); the Run gate blocks";
        if (Scenario(rootB).Universe.Count != 0)
            return "S14b: corrupt sidecar seeded a universe ([" + string.Join(",", Scenario(rootB).Universe.Ids) +
                   "]) — must be empty (sidecar unreadable, no inline)";

        // (c) STRUCTURALLY corrupt sidecar: VALID JSON but a wrong-type "start" makes FromJObject's
        // (string) cast throw ArgumentException — NOT a ScenarioSidecarException. TryReadScenario's bare
        // catch must still degrade (no crash), or the D3 guarantee only covers malformed-JSON corruption.
        string structPy = Path.Combine(TempDir, "open_struct.py");
        File.WriteAllText(structPy, "z = 3\n");
        File.WriteAllText(ScenarioSidecarStore.SidecarPathFor(structPy), "{\"scenario\":{\"start\":{}}}");

        var rootC = Compose();
        if (rootC == null) return "S14c: BackcastWorkspaceRoot missing";
        rootC.SetFileDialog(new StubFileDialog { NextResult = structPy });
        FileOpen(rootC);   // must NOT throw out of OnFileOpen

        if (CurrentPath(rootC) != Path.GetFullPath(structPy))
            return "S14c: File→Open ABORTED/crashed on a structurally-corrupt sidecar (currentPath=[" +
                   CurrentPath(rootC) + "]) — TryReadScenario must catch the non-JSON ArgumentException too";
        if (Scenario(rootC).Universe.Count != 0)
            return "S14c: structurally-corrupt sidecar seeded a universe ([" +
                   string.Join(",", Scenario(rootC).Universe.Ids) + "]) — must be empty";
        return null;
    }

    // ── 12. per-mode layout profile flip + restore (#62, findings 0029 §7) — integration smoke ──
    // On the REAL root: a user swaps base order in Replay, flips to Live and swaps DIFFERENTLY, then
    // flips back — each mode's own arrangement is restored (AC1). Then the real CaptureLayout →
    // LayoutStore round-trip → ApplyLayout restores the current mode's profile from disk (AC1/AC2).
    static string Section12_PerModeProfileFlipAndRestore()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "profile: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        var hako = ty.GetField("_hako", BF).GetValue(root) as HakoniwaController;
        var sync = ty.GetMethod("SyncBaseTilesToMode", BF);
        var capture = ty.GetMethod("CaptureLayout", BF);
        var applyLayout = ty.GetMethod("ApplyLayout", BF);
        if (scenario == null || hako == null || sync == null || capture == null || applyLayout == null)
            return "profile: root internals not found (renamed?)";

        scenario.Universe.ReplaceAll(new[] { "AAA.TSE", "BBB.TSE" });   // Replay shape: 5 base + 2 chart

        // (1) Replay: swap buying_power <-> orders → a distinct Replay arrangement.
        if (!hako.Swap(hako.SlotOf("buying_power"), hako.SlotOf("orders"))) return "profile: Replay base swap failed";
        var replayWanted = new[] { "startup", "orders", "buying_power", "positions", "run_result" };
        string e = AssertBaseOrder(hako, replayWanted); if (e != null) return "profile(Replay swap): " + e;

        // (2) → Live: stashes the Replay profile; canonical Live base. Make a DIFFERENT Live swap
        // (run_result to the front).
        sync.Invoke(root, new object[] { true });
        if (hako.SlotOf("startup") >= 0) return "profile→Live: startup must leave";
        if (!hako.Swap(hako.SlotOf("run_result"), hako.SlotOf("buying_power"))) return "profile: Live base swap failed";
        var liveWanted = new[] { "run_result", "orders", "positions", "buying_power" };
        e = AssertBaseOrder(hako, liveWanted); if (e != null) return "profile(Live swap): " + e;

        // (3) → Replay: the Replay profile must be RESTORED (not canonical, not the Live order).
        sync.Invoke(root, new object[] { false });
        e = AssertBaseOrder(hako, replayWanted); if (e != null) return "profile(Replay restored): " + e;

        // (4) → Live again: the Live profile must be RESTORED independently.
        sync.Invoke(root, new object[] { true });
        e = AssertBaseOrder(hako, liveWanted); if (e != null) return "profile(Live restored): " + e;

        // (5) disk path: capture (active = Live) → save → load → ApplyLayout adopts BOTH profiles.
        var doc = capture.Invoke(root, null) as LayoutDocument;
        if (doc == null || doc.hakoniwaProfiles == null) return "profile: CaptureLayout did not emit hakoniwaProfiles";
        string path = System.IO.Path.Combine(Application.temporaryCachePath, "workspace_profile_probe.json");
        if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
        LayoutStore.Save(doc, path);
        var loaded = LayoutStore.Load(path);
        // perturb the live order, then restore from disk and adopt the per-mode profiles.
        hako.Swap(0, 1);
        applyLayout.Invoke(root, new object[] { loaded });
        e = AssertBaseOrder(hako, liveWanted); if (e != null) return "profile(disk restore Live): " + e;
        // NON-VACUOUS: the doc's `panels` mirror equals the Live order, so flipping to Replay and getting
        // replayWanted back can ONLY come from the persisted hakoniwaProfiles.replay sub-profile (a dropped
        // field would seed Replay from the Live mirror → liveWanted, failing here). Then back to Live.
        sync.Invoke(root, new object[] { false });
        e = AssertBaseOrder(hako, replayWanted); if (e != null) return "profile(disk restore Replay — distinct per-mode persisted): " + e;
        sync.Invoke(root, new object[] { true });
        e = AssertBaseOrder(hako, liveWanted); if (e != null) return "profile(disk re-flip Live): " + e;
        System.IO.File.Delete(path);
        return null;
    }

    // ── 13. chrome z-order layering (#77) — the menu dropdown must draw IN FRONT of the sidebar ──
    // The #77 bug: menu + sidebar were BOTH OnGUI (IMGUI); GUI.depth is ignored in a single-camera
    // Screen-Space setup, so the sidebar (later OnGUI) overpainted the dropdown. The fix uGUI-ifies
    // BOTH chrome views onto their OWN nested ScreenSpaceOverlay-sorted Canvas (overrideSorting), so
    // z-order is DETERMINISTIC via sortingOrder (uGUI), not IMGUI execution order. This asserts the
    // structural contract that makes the dropdown render-in-front + the input-bleed class vanish:
    //   field/windows(0) < sidebar < menu+dropdown < secret modal(1000).
    // EventSystem resolves a click to the TOP raycaster only, so menu>sidebar also kills the bleed
    // where the dropdown overlaps the sidebar. The pixel z-order + click-through is the owner HITL.
    static string Section13_ChromeZOrderLayering()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "zorder: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var menu = UnityEngine.Object.FindFirstObjectByType<MenuBarView>();
        var side = UnityEngine.Object.FindFirstObjectByType<UniverseSidebarView>();
        var secret = UnityEngine.Object.FindFirstObjectByType<SecretModalOverlay>();
        if (menu == null) return "zorder: MenuBarView missing";
        if (side == null) return "zorder: UniverseSidebarView missing";
        if (secret == null) return "zorder: SecretModalOverlay missing";

        // #84 (findings 0053): the footer is now a first-class chrome layer with its OWN override-sorting
        // Canvas — locate it via the _footerContainer field on the root (the bar GameObject is where
        // WorkspaceFooterView.Build promoted the Canvas).
        var ft = typeof(BackcastWorkspaceRoot).GetField("_footerContainer", BF);
        var footerRt = ft != null ? ft.GetValue(root) as RectTransform : null;
        if (footerRt == null) return "zorder: BackcastWorkspaceRoot._footerContainer missing (footer not authored?)";

        // chrome views must be uGUI (own overrideSorting Canvas + raycaster), NOT IMGUI / NOT main Canvas.
        string e = AssertChromeCanvas(menu.gameObject, "MenuBarView", out var menuCanvas); if (e != null) return e;
        e = AssertChromeCanvas(side.gameObject, "UniverseSidebarView", out var sideCanvas); if (e != null) return e;
        e = AssertChromeCanvas(footerRt.gameObject, "WorkspaceFooterView (footer container)", out var footerCanvas); if (e != null) return e;

        var secretCanvas = secret.GetComponent<Canvas>();
        if (secretCanvas == null) return "zorder: SecretModalOverlay has no Canvas";

        // the layering contract (findings 0045 + 0053): sidebar > 0, footer > sidebar (footer always
        // visible over an overflowing sidebar — #84), menu > footer (dropdowns still cover the footer
        // band per desktop semantics), secret modal > menu (modal stays topmost). Values are derived;
        // only the RELATIONS are gated.
        if (sideCanvas.sortingOrder <= 0)
            return $"zorder: sidebar sortingOrder must be > 0 chrome layer (got {sideCanvas.sortingOrder})";
        if (footerCanvas.sortingOrder <= sideCanvas.sortingOrder)
            return $"zorder: footer sortingOrder ({footerCanvas.sortingOrder}) must be > sidebar ({sideCanvas.sortingOrder}) so the status bar can't be hidden by overflowing sidebar content (#84)";
        if (menuCanvas.sortingOrder <= footerCanvas.sortingOrder)
            return $"zorder: menu sortingOrder ({menuCanvas.sortingOrder}) must be > footer ({footerCanvas.sortingOrder}) so the dropdown draws over the footer band";
        if (secretCanvas.sortingOrder <= menuCanvas.sortingOrder)
            return $"zorder: secret modal sortingOrder ({secretCanvas.sortingOrder}) must be > menu ({menuCanvas.sortingOrder}) so the modal stays topmost";
        return null;
    }

    // #84 (findings 0053): structural overflow clipping inside the sidebar. The sidebar is constrained
    // BOTH by its own RectTransform (inset above the footer in the scene) AND by RectMask2D viewports
    // around rows and picker list — so even a 1000-instrument universe cannot escape the sidebar's
    // visual bounds. The dual guarantee (this + Section13's footer-above-sidebar z-layer) makes
    // #84 structurally non-regressible from either side of the contract.
    static string Section15_SidebarOverflowClipping()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "sidebar-clip: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var side = UnityEngine.Object.FindFirstObjectByType<UniverseSidebarView>();
        if (side == null) return "sidebar-clip: UniverseSidebarView missing";

        // rows ScrollRect with a RectMask2D viewport must exist (overflow can never escape).
        var rowsScroll = side.GetComponentInChildren<UnityEngine.UI.ScrollRect>(true);
        if (rowsScroll == null)
            return "sidebar-clip: rows ScrollRect missing (sidebar rows would overflow the sidebar rect, #84 regression)";
        if (rowsScroll.viewport == null)
            return "sidebar-clip: rows ScrollRect.viewport unwired";
        if (rowsScroll.viewport.GetComponent<UnityEngine.UI.RectMask2D>() == null)
            return "sidebar-clip: rows ScrollRect.viewport has no RectMask2D (rows would still overflow visually)";
        if (rowsScroll.horizontal || !rowsScroll.vertical)
            return $"sidebar-clip: rows ScrollRect must be vertical-only (horizontal={rowsScroll.horizontal} vertical={rowsScroll.vertical})";

        // The sidebar's own RectTransform must still be inset above the footer (the scene-authored
        // bottom inset == FOOTER_H = 40px; this is the OTHER half of the dual guarantee — without it,
        // the sidebar Canvas itself would extend into the footer band). Shape-agnostic check via world
        // corners (anchor/pivot/sizeDelta combinations the future scene-author may pick don't matter —
        // what matters is that the sidebar's bottom edge sits ABOVE the footer's top edge).
        var ft2 = typeof(BackcastWorkspaceRoot).GetField("_footerContainer", BF);
        var fRt = ft2 != null ? ft2.GetValue(root) as RectTransform : null;
        if (fRt == null) return "sidebar-clip: BackcastWorkspaceRoot._footerContainer missing";
        var sideRt = side.GetComponent<RectTransform>();
        var sideCorners = new Vector3[4];
        var footerCorners = new Vector3[4];
        // F6 (#84 round-2 review): in batchmode the Canvas layout pass is deferred until end-of-frame,
        // so RectTransform.GetWorldCorners on a just-built scene returns stale (often-zero) world coords
        // → the comparison trivially passes and the gate becomes a no-op. ForceUpdateCanvases runs the
        // layout pass synchronously so the world corners reflect the actual geometry under test.
        Canvas.ForceUpdateCanvases();
        sideRt.GetWorldCorners(sideCorners);     // [0]=BL [1]=TL [2]=TR [3]=BR (world coords)
        fRt.GetWorldCorners(footerCorners);
        float sidebarBottom = sideCorners[0].y;
        float footerTop = footerCorners[1].y;
        if (sidebarBottom < footerTop - 0.5f)
            return $"sidebar-clip: sidebar bottom (worldY={sidebarBottom:F1}) extends BELOW footer top (worldY={footerTop:F1}); sidebar must be inset above the footer";
        return null;
    }

    // ── 16. #93 perspective stage scene wiring (findings 0068 §15) ──
    // The runtime diorama plumbing: HakoniwaRoot lives on a World-Space "Hakoniwa Stage" Canvas on a
    // DEDICATED layer; a perspective camera renders ONLY that layer into a transparent RenderTexture; a
    // RawImage in Content shows the RT (the photo). The Main camera EXCLUDES the stage layer (RT-exclusive,
    // no double-draw). Camera FOV + board tilt derive from HakoniwaStageMath.StageParams.Default — the math
    // SoT, no scene magic numbers (§7 invariant discipline / §15 F4). Deep projection fidelity is the math
    // probe (HakoniwaPerspectiveStageProbe) + HITL; this gates the SCENE WIRING only.
    static string Section16_HakoniwaStageWiring()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "stage-wiring: BackcastWorkspaceRoot missing";
        var so = new SerializedObject(root);
        RectTransform Ref(string n) => so.FindProperty(n)?.objectReferenceValue as RectTransform;

        var content = Ref("_content");
        var hako = Ref("_hakoniwaRoot");
        if (content == null || hako == null) return "stage-wiring: _content / _hakoniwaRoot missing";

        // (1) RawImage: wired, a RawImage, child of Content (the old HakoniwaRoot slot — §15 F1).
        var rawProp = so.FindProperty("_hakoniwaRawImage");
        if (rawProp == null) return "stage-wiring: _hakoniwaRawImage serialized field missing on root (§15 F1)";
        var raw = rawProp.objectReferenceValue as UnityEngine.UI.RawImage;
        if (raw == null) return "stage-wiring: _hakoniwaRawImage not wired to a RawImage (§15 F1)";
        if (raw.transform.parent != content) return "stage-wiring: RawImage is not a child of Content (must take the old HakoniwaRoot slot)";

        // (2) Stage canvas: HakoniwaRoot's parent Canvas is World-Space on a DEDICATED (non-Default) layer.
        var stageCanvas = hako.GetComponentInParent<Canvas>();
        if (stageCanvas == null) return "stage-wiring: HakoniwaRoot has no parent Canvas (Stage canvas missing)";
        if (stageCanvas.renderMode != RenderMode.WorldSpace) return "stage-wiring: Stage canvas is not RenderMode.WorldSpace";
        int hakoLayer = hako.gameObject.layer;
        if (hakoLayer == 0) return "stage-wiring: HakoniwaRoot still on the Default layer (needs a dedicated Hakoniwa layer, §15 F2)";
        int hakoMask = 1 << hakoLayer;

        // (3) the RT the RawImage displays.
        var rt = raw.texture as RenderTexture;
        if (rt == null) return "stage-wiring: RawImage.texture is not a RenderTexture (RT not wired, §15 F1)";

        // (4) perspective camera renders ONLY the Hakoniwa layer into THAT RT; FOV from StageParams.Default.
        Camera stageCam = null;
        foreach (var c in UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None))
            if (c.targetTexture == rt) { stageCam = c; break; }
        if (stageCam == null) return "stage-wiring: no Camera renders into the RawImage's RT (perspective stage camera missing, §15 F2)";
        if (stageCam.orthographic) return "stage-wiring: stage camera is orthographic (perspective required, §15 F4)";
        if (stageCam.cullingMask != hakoMask)
            return $"stage-wiring: stage camera cullingMask must be the Hakoniwa layer ONLY (got {stageCam.cullingMask}, expected {hakoMask}, §15 F2)";

        var p = HakoniwaStageMath.StageParams.Default(rt.width, rt.height);
        if (Mathf.Abs(stageCam.fieldOfView - p.fovDeg) > 0.1f)
            return $"stage-wiring: stage camera FOV {stageCam.fieldOfView} != StageParams.Default {p.fovDeg} (camera derives from the math SoT, §15 F4)";

        // (5) board tilt derives from StageParams.Default — location-agnostic (canvas or root carries it, §15 F3).
        var wantTilt = Quaternion.Euler(p.pitchDeg, p.yawDeg, 0f);
        if (Quaternion.Angle(hako.rotation, wantTilt) > 0.5f)
            return $"stage-wiring: HakoniwaRoot world rotation {hako.rotation.eulerAngles} != Euler({p.pitchDeg},{p.yawDeg},0) from StageParams.Default (§15 F3/F4)";

        // (5b) board WORLD dimensions derive from StageParams.Default (§15 F4: "board 寸法も Default 由来").
        // HakoniwaRoot is a center-anchored box (rect.size == sizeDelta) on the scaled Stage canvas, so its
        // world size = rect.size * lossyScale must equal (boardW, boardH) from the math SoT.
        float boardWorldW = hako.rect.width * hako.lossyScale.x;
        float boardWorldH = hako.rect.height * hako.lossyScale.y;
        if (Mathf.Abs(boardWorldW - p.boardW) > 0.01f || Mathf.Abs(boardWorldH - p.boardH) > 0.01f)
            return $"stage-wiring: HakoniwaRoot world board {boardWorldW:F2}x{boardWorldH:F2} != StageParams.Default {p.boardW}x{p.boardH} (board dims must derive from the math SoT, §15 F4)";

        // (6) Main camera EXCLUDES the Hakoniwa layer (RT-exclusive, no double-draw — §15 F2).
        var mainCam = Camera.main;
        if (mainCam == null) return "stage-wiring: Main camera missing";
        if ((mainCam.cullingMask & hakoMask) != 0)
            return "stage-wiring: Main camera does NOT exclude the Hakoniwa layer (double-draw / RT not exclusive, §15 F2)";

        // (7) the RawImage (the RT photo) must be on a layer the MAIN camera renders — i.e. NOT the
        // Hakoniwa stage layer (else it is excluded from Main and/or recurses into the RT, §15 F1/F2).
        if ((mainCam.cullingMask & (1 << raw.gameObject.layer)) == 0)
            return $"stage-wiring: RawImage (RT photo) layer {raw.gameObject.layer} is not rendered by the Main camera — diorama photo would be invisible (§15 F1/F2)";
        return null;
    }

    // a chrome view is uGUI when its GameObject carries an override-sorting Canvas + a GraphicRaycaster
    // (so the EventSystem hit-tests it and sortingOrder controls draw order — the #77 cure).
    static string AssertChromeCanvas(GameObject go, string what, out Canvas canvas)
    {
        canvas = go.GetComponent<Canvas>();
        if (canvas == null) return $"zorder: {what} has no Canvas (still IMGUI? #77 uGUI-ification missing)";
        if (!canvas.overrideSorting) return $"zorder: {what} Canvas.overrideSorting must be true (else sortingOrder is ignored)";
        if (go.GetComponent<UnityEngine.UI.GraphicRaycaster>() == null) return $"zorder: {what} has no GraphicRaycaster (clicks won't hit the uGUI chrome)";
        return null;
    }

    // assert the base tiles (non-chart) appear in EXACTLY `wanted` order at the front, charts after.
    static string AssertBaseOrder(HakoniwaController hako, string[] wanted)
    {
        for (int i = 0; i < wanted.Length; i++)
            if (hako.SlotOf(wanted[i]) != i)
                return wanted[i] + " expected slot " + i + ", got " + hako.SlotOf(wanted[i]) + " (order [" + string.Join(",", hako.Order) + "])";
        // every chart id must sit after the base region.
        for (int i = 0; i < hako.Order.Count; i++)
            if (HakoniwaBaseTiles.IsChartId(hako.Order[i]) && i < wanted.Length)
                return "chart tile " + hako.Order[i] + " sits inside the base region";
        return null;
    }

    // ── helpers ──
    static RectTransform NewChild(RectTransform parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        return rt;
    }

    static T FindByComponent<T>() where T : Component
    {
        return UnityEngine.Object.FindFirstObjectByType<T>();
    }

    static bool IsDescendant(Transform child, Transform ancestor)
    {
        for (Transform t = child; t != null; t = t.parent) if (t == ancestor) return true;
        return false;
    }
}
