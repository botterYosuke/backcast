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
                ?? Section7_Ownership();
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
