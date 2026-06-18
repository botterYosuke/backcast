// MenuBarCutoverProbe.cs — issue #42 cutover slice 2 (headless AFK regression gate, findings 0027 §2)
//
// The Python-FREE, render-FREE gate for the venue-integrated menu bar's load-bearing root behaviours
// (findings 0027 D3/D4). Run:
//
//   <Unity> -batchmode -nographics -projectPath <repo> \
//           -executeMethod MenuBarCutoverProbe.Run -logFile <log>
//   # expect: [MENU BAR CUTOVER PASS] ... / exit=0
//
// It composes the REAL BackcastWorkspaceRoot via reflection (BuildWorkspace — Awake's Python init is
// gated off in batchmode and never reached here), then value-asserts the seams that the engine-touching
// HITL cannot prove deterministically. The actual venue login/logout + mode-RPC round-trips are the
// owner-run HITL leg (the host/Python is not started in batchmode).
//
// SECTIONS (findings 0027 §2):
//   1. File→New = full TTWR reset (findings 0017 §4) that HONOURS the adopt invariant (findings 0025 §8):
//      the scene-authored adopted editor is reset-to-empty (NOT destroyed); ADDITIONAL editor windows are
//      destroyed + unregistered; the scenario universe is cleared.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class MenuBarCutoverProbe
{
    const string WINDOW_ID = "strategy_editor:region_001";       // the scene-authored adopted editor
    const string ADDITIONAL_ID = "strategy_editor:region_002";   // an additional, restorable editor window
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    static string TempDir => Path.Combine(Application.temporaryCachePath, "menu_bar_cutover_probe");
    static string TempPy => Path.Combine(TempDir, "adopted_strategy.py");

    public static void Run()
    {
        string fail;
        try { fail = Section1_FileNewFullReset(); }
        catch (Exception e) { fail = "driver: " + e; }
        finally { try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { } }

        if (fail == null)
        {
            Debug.Log("[MENU BAR CUTOVER PASS] all sections green.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[MENU BAR CUTOVER FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ── 1. File→New full reset, adopt-safe (findings 0027 D3, findings 0017 §4, findings 0025 §8) ──
    static string Section1_FileNewFullReset()
    {
        if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
        Directory.CreateDirectory(TempDir);
        File.WriteAllText(TempPy, "# adopted strategy\nclass S:\n    pass\n");

        var root = ComposeRoot(out var ty);
        if (root == null) return "BackcastWorkspaceRoot missing in scene";

        var windows = ty.GetField("_windows", BF).GetValue(root) as FloatingWindowController;
        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        var coordinator = ty.GetField("_coordinator", BF).GetValue(root) as NotebookCellCoordinator;
        var notebook = ty.GetField("_notebook", BF).GetValue(root) as MarimoNotebookDocument;
        if (windows == null || scenario == null || coordinator == null || notebook == null)
            return "root internals not found (renamed?)";

        // ---- setup (#81): a bound 2-cell notebook (region_001 + region_002) and a seeded universe ----
        // Bind the notebook to a real `.py` (the fake synthesizer wraps it as one cell -> cell 0 in
        // region_001), then add a 2nd cell (region_002), so File->New has structure to reset.
        if (!coordinator.Open(TempPy, null)) return "probe setup: notebook Open(TempPy) failed";
        var adoptedRt = windows.RectOf(WINDOW_ID);
        coordinator.AddCell();                                       // 2nd cell -> region_002
        scenario.Universe.ReplaceAll(new[] { "A.TSE", "B.TSE" });

        // ---- pre-assert (non-vacuous: prove there is state TO clear) ----
        if (notebook.CellCount != 2) return "probe setup: expected a 2-cell notebook";
        if (!notebook.IsBound) return "probe setup: notebook not bound to a path";
        if (!windows.Has(ADDITIONAL_ID)) return "probe setup: 2nd cell window (region_002) not spawned";
        if (scenario.Universe.Count != 2) return "probe setup: universe not seeded";

        // ---- act: File→New ----
        ty.GetMethod("OnFileNew", BF).Invoke(root, null);

        // ---- assert: adopt invariant — the scene-authored region_001 is RESET, never destroyed ----
        if (!windows.Has(WINDOW_ID)) return "File→New DESTROYED the region_001 cell window (adopt invariant breached, findings 0025 §8)";
        if (windows.RectOf(WINDOW_ID) != adoptedRt) return "File→New REPLACED the region_001 window (must reset in place)";
        if (notebook.CellCount != 1) return "File→New did not reset the notebook to one empty cell";
        if (notebook.IsBound) return "File→New did not unbind the notebook (ResetUnboundEmpty)";

        // ---- assert: the additional (region_002) cell window despawned ----
        if (windows.Has(ADDITIONAL_ID)) return "File→New did not despawn the region_002 cell window";

        // ---- assert: scenario universe cleared ----
        if (scenario.Universe.Count != 0) return "File→New did not clear the scenario universe";

        return null;
    }

    // Compose the real root headlessly: open the committed scene, then run ResolvePaths + BuildWorkspace
    // (Python-FREE — Awake's Python init is gated off in batchmode and is never invoked here).
    static BackcastWorkspaceRoot ComposeRoot(out Type ty)
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        ty = typeof(BackcastWorkspaceRoot);
        if (root == null) return null;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());   // #81: Python-free cell synthesis (inject BEFORE compose)
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }
}
