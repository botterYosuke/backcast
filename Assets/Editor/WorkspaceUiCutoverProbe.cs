// WorkspaceUiCutoverProbe.cs — issue #76 S6b-β-clean (headless AFK regression gate, findings 0046)
//
// The Python-FREE, render-FREE gate for the transport→reactive UI cutover (findings 0046,
// "S6b-β-clean 設計の木" U1/U3/U4/U5). Run:
//
//   <Unity> -batchmode -nographics -projectPath <repo> \
//           -executeMethod WorkspaceUiCutoverProbe.Run -logFile <log>
//   # expect: [WORKSPACE UI CUTOVER PASS] ... / exit=0
//
// Sections:
//   1. U1 Run-readiness truth table (pure RunReadinessViewModel — the OnRun gate order).
//   2. U1/U4/U5 single Run entry (structural): the adopted editor title bar HAS a Run button; the
//      footer has the mode segments but NO transport buttons (▶/⏭/⏹/speed); the startup tile has NO
//      Run button.
//   3. U3 boot canonical: a no-resume boot opens the canonical v19 marimo into the adopted editor
//      (skipped — logged — when the dev python tree / v19 file is not resolvable in this environment).
//
// It composes the REAL BackcastWorkspaceRoot via reflection (BuildWorkspace — Awake's Python init is
// gated off in batchmode and never reached here), then value-asserts the seams.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class WorkspaceUiCutoverProbe
{
    const string WINDOW_ID = "strategy_editor:region_001";
    const string ResumeKey = "backcast.lastDocument";   // mirrors BackcastWorkspaceRoot.ResumeKey
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_RunReadinessTruthTable()
                ?? Section2_SingleRunEntry()
                ?? Section3_BootCanonical();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[WORKSPACE UI CUTOVER PASS] all sections green.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[WORKSPACE UI CUTOVER FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ── 1. U1 Run-readiness truth table (pure VM; the OnRun gate order). ──
    static string Section1_RunReadinessTruthTable()
    {
        // all gates pass → Run enabled, no reason.
        if (RunReadinessViewModel.Reason(true, false, true, true) != null) return "readiness: all-OK should be runnable";

        // each single block, in isolation.
        if (RunReadinessViewModel.Reason(true, true, true, true) != RunReadinessViewModel.Running) return "readiness: running not blocked";
        if (RunReadinessViewModel.Reason(true, false, false, true) != RunReadinessViewModel.NoStrategy) return "readiness: unsaved strategy not blocked";
        if (RunReadinessViewModel.Reason(true, false, true, false) != RunReadinessViewModel.InvalidScenario) return "readiness: invalid scenario not blocked";
        if (RunReadinessViewModel.Reason(false, false, true, true) != RunReadinessViewModel.NotOwner) return "readiness: not-owner not blocked";

        // precedence (OnRun order: running → no-strategy → invalid-scenario → not-owner).
        if (RunReadinessViewModel.Reason(true, true, false, false) != RunReadinessViewModel.Running) return "readiness: running must win over strategy/scenario";
        if (RunReadinessViewModel.Reason(true, false, false, false) != RunReadinessViewModel.NoStrategy) return "readiness: no-strategy must win over scenario/owner";
        if (RunReadinessViewModel.Reason(false, false, true, false) != RunReadinessViewModel.InvalidScenario) return "readiness: invalid-scenario must win over owner";

        // Evaluate() mirrors Reason() into CanRun / BlockReason.
        var vm = new RunReadinessViewModel();
        vm.Evaluate(true, false, true, true);
        if (!vm.CanRun || vm.BlockReason != null) return "readiness: Evaluate(all-OK) should be CanRun";
        vm.Evaluate(true, false, false, true);
        if (vm.CanRun || vm.BlockReason != RunReadinessViewModel.NoStrategy) return "readiness: Evaluate(unsaved) should block";
        return null;
    }

    // ── 2. U1/U4/U5 single Run entry (structural). ──
    static string Section2_SingleRunEntry()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "BackcastWorkspaceRoot missing in scene";

        // U1: the adopted editor title bar HAS a Run button (built + a "RunButton" GameObject on the bar).
        if (ty.GetField("_editorRunButton", BF).GetValue(root) == null) return "U1: adopted editor title-bar Run button not built";
        var titleInput = ty.GetField("_strategyEditorTitleInput", BF).GetValue(root) as Component;
        if (titleInput == null) return "U1: adopted editor title input missing in scene";
        if (FindChildButton((RectTransform)titleInput.transform, "RunButton") == null)
            return "U1: no 'RunButton' under the adopted editor title bar";

        // U4: the footer has the mode segments and NO replay-transport buttons.
        var footerContainer = ty.GetField("_footerContainer", BF).GetValue(root) as RectTransform;
        if (footerContainer == null) return "U4: footer container missing in scene";
        var footerBtnNames = ButtonNames(footerContainer);
        foreach (var seg in new[] { "btn:Replay", "btn:Manual", "btn:Auto" })
            if (!footerBtnNames.Contains(seg)) return "U4: footer missing mode segment " + seg;
        foreach (var transport in new[] { "btn:▶", "btn:⏸", "btn:⏭", "btn:⏹", "btn:1x", "btn:2x", "btn:5x", "btn:10x", "btn:50x" })
            if (footerBtnNames.Contains(transport)) return "U4: footer still has a retired transport button " + transport;

        // U5: the startup tile has NO Run button.
        var startupTile = ty.GetField("_startupTile", BF).GetValue(root) as RectTransform;
        if (startupTile == null) return "U5: startup tile missing in scene";
        if (ButtonNames(startupTile).Contains("btn:Run Replay")) return "U5: startup tile still has its Run button";

        return null;
    }

    // ── 3. U3 boot canonical: no-resume boot opens the v19 marimo into the adopted editor. ──
    static string Section3_BootCanonical()
    {
        // Resolve the expected canonical path the same way the root does; skip (not fail) when the dev
        // python tree / v19 file is not resolvable here (venv-less CI) — the owner-run AFK has the tree.
        string expected;
        try { expected = Path.Combine(PythonRuntimeLocator.ProjectRoot, BackcastWorkspaceRoot.CanonicalStrategyRelPath); }
        catch (Exception e) { Debug.Log("[WORKSPACE UI CUTOVER] U3 skipped — ProjectRoot unresolved: " + e.Message); return null; }
        if (!File.Exists(expected)) { Debug.Log("[WORKSPACE UI CUTOVER] U3 skipped — v19 marimo not found at " + expected); return null; }

        var root = ComposeRoot(out var ty);
        if (root == null) return "BackcastWorkspaceRoot missing in scene";

        PlayerPrefs.DeleteKey(ResumeKey);   // force the no-resume branch (canonical default)
        ty.GetMethod("ResumeLastDocumentOrDefault", BF).Invoke(root, null);

        // #81: boot decomposes the canonical v19 into cell windows via the coordinator; the NOTEBOOK
        // aggregate (not the editor view) holds the bound `.py` path.
        var notebook = ty.GetField("_notebook", BF).GetValue(root) as MarimoNotebookDocument;
        if (notebook == null) return "U3: notebook missing";
        string path = notebook.CurrentPath;
        if (string.IsNullOrEmpty(path)) return "U3: boot did not bind the notebook to the canonical v19 marimo";
        if (!path.Replace('\\', '/').EndsWith(BackcastWorkspaceRoot.CanonicalStrategyRelPath))
            return "U3: boot opened the wrong canonical file: " + path;
        return null;
    }

    // ---- helpers ----
    static BackcastWorkspaceRoot ComposeRoot(out Type ty)
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        ty = typeof(BackcastWorkspaceRoot);
        if (root == null) return null;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());   // #81: Python-free cell synthesis (canonical open in U3)
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }

    static HashSet<string> ButtonNames(RectTransform rt)
    {
        var names = new HashSet<string>();
        foreach (var b in rt.GetComponentsInChildren<Button>(true)) names.Add(b.gameObject.name);
        return names;
    }

    static Button FindChildButton(RectTransform rt, string name)
    {
        foreach (var b in rt.GetComponentsInChildren<Button>(true))
            if (b.gameObject.name == name) return b;
        return null;
    }
}
