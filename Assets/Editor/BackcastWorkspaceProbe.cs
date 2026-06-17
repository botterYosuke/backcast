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
                ?? Section11_PerModeProfileFlipAndRestore();
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
        if (hako.parent != content) return "HakoniwaRoot is not a child of Content";
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
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        var sidebar = ty.GetField("_sidebarCtrl", BF).GetValue(root) as UniverseSidebarController;
        if (scenario == null || sidebar == null) return "reprime: root internals not found (renamed?)";

        // point the run-gate at a real temp strategy (BoundStrategyFileProvider requires File.Exists).
        ty.GetField("_strategyFile", BF).SetValue(root, strat);

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

    // ── 11. per-mode layout profile flip + restore (#62, findings 0029 §7) — integration smoke ──
    // On the REAL root: a user swaps base order in Replay, flips to Live and swaps DIFFERENTLY, then
    // flips back — each mode's own arrangement is restored (AC1). Then the real CaptureLayout →
    // LayoutStore round-trip → ApplyLayout restores the current mode's profile from disk (AC1/AC2).
    static string Section11_PerModeProfileFlipAndRestore()
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
