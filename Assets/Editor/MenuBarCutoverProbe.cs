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
//      the scene-authored adopted editor is reseeded to the marimo skeleton template (#76 S6b-β-clean U2,
//      NOT destroyed, left untitled); ADDITIONAL editor windows are destroyed + unregistered; the
//      scenario universe is cleared.

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

        var editors = ty.GetField("_editors", BF).GetValue(root) as Dictionary<string, StrategyEditorView>;
        var windows = ty.GetField("_windows", BF).GetValue(root) as FloatingWindowController;
        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        if (editors == null || windows == null || scenario == null) return "root internals not found (renamed?)";

        // ---- setup: a bound+dirty adopted editor, an ADDITIONAL editor window, a seeded universe ----
        if (!editors.TryGetValue(WINDOW_ID, out var adopted) || adopted == null)
            return "adopted editor not built under WINDOW_ID";
        if (!adopted.Open(TempPy)) return "probe setup: adopted editor Open(TempPy) failed";
        var adoptedRt = windows.RectOf(WINDOW_ID);

        windows.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, ADDITIONAL_ID, 10f, 10f, 300f, 200f, true);
        scenario.Universe.ReplaceAll(new[] { "A.TSE", "B.TSE" });

        // ---- pre-assert (non-vacuous: prove there is state TO clear) ----
        if (string.IsNullOrEmpty(adopted.Document.Text)) return "probe setup: adopted editor text not seeded";
        if (adopted.Document.CurrentPath == null) return "probe setup: adopted editor not bound to a path";
        if (!windows.Has(ADDITIONAL_ID) || !editors.ContainsKey(ADDITIONAL_ID)) return "probe setup: additional editor window not spawned/registered";
        if (scenario.Universe.Count != 2) return "probe setup: universe not seeded";

        // ---- act: File→New ----
        ty.GetMethod("OnFileNew", BF).Invoke(root, null);

        // ---- assert: adopt invariant — the scene-authored window is RESET, never destroyed ----
        if (!windows.Has(WINDOW_ID)) return "File→New DESTROYED the adopted editor window (adopt invariant breached, findings 0025 §8)";
        if (windows.RectOf(WINDOW_ID) != adoptedRt) return "File→New REPLACED the adopted editor (must reset in place)";
        if (!editors.TryGetValue(WINDOW_ID, out var adoptedAfter) || adoptedAfter == null) return "adopted editor dropped from _editors";
        // #76 S6b-β-clean U2: File→New now SEEDS the marimo skeleton template (not empty), and leaves the
        // editor UNBOUND (untitled). The seeded text is a marimo app (is_marimo_app_source would be true).
        if (adoptedAfter.Document.Text != MarimoStrategyTemplate.NewStrategy) return "File→New did not seed the marimo skeleton template (U2)";
        if (!adoptedAfter.Document.Text.Contains("import marimo")
            || !adoptedAfter.Document.Text.Contains("marimo.App(")
            || !adoptedAfter.Document.Text.Contains("@app.cell")) return "File→New template is not a marimo app (is_marimo_app_source would be false)";
        if (adoptedAfter.Document.CurrentPath != null) return "File→New did not leave the adopted editor unbound (untitled)";

        // ---- assert: additional editor window destroyed + unregistered ----
        if (windows.Has(ADDITIONAL_ID)) return "File→New did not destroy the ADDITIONAL editor window";
        if (editors.ContainsKey(ADDITIONAL_ID)) return "File→New did not drop the ADDITIONAL editor from _editors";

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
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }
}
