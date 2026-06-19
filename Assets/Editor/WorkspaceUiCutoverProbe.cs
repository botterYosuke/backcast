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
//   3. U3 boot → File→New blank state: a no-resume boot lands in the File→New state (an untitled,
//      UNBOUND empty notebook), NOT a strategy — strategies open via File→Open (#76 2026-06-19).
//
// (Sections 1 + 2 — the U1 Run-readiness truth table + the U1/U4/U5 single Run entry — were PROMOTED
// to the durable RunButtonE2ERunner (Assets/Tests/E2E/Editor, ADR-0015 wave-2 #3) as its readiness /
// single-entry sections, and removed here so the Run-button surface has ONE canonical runner. This
// probe now pins only the boot→File→New blank state, pending its own surface promotion.)
//
// It composes the REAL BackcastWorkspaceRoot via reflection (BuildWorkspace — Awake's Python init is
// gated off in batchmode and never reached here), then value-asserts the seams.

using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class WorkspaceUiCutoverProbe
{
    const string ResumeKey = "backcast.lastDocument";   // mirrors BackcastWorkspaceRoot.ResumeKey
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section3_BootFileNew();
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

    // ── 3. U3 boot → File→New blank state (#76 2026-06-19): a no-resume boot lands in the File→New
    //       state (an untitled, UNBOUND empty notebook), NOT a strategy. Strategies open via File→Open;
    //       the earlier "boot opens the canonical v19 marimo" is withdrawn. ──
    static string Section3_BootFileNew()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "BackcastWorkspaceRoot missing in scene";

        PlayerPrefs.DeleteKey(ResumeKey);   // force the no-resume branch (File→New default)
        ty.GetMethod("ResumeLastDocumentOrDefault", BF).Invoke(root, null);

        // File→New state = UNBOUND: no document path is bound at boot (the notebook aggregate, not the
        // editor view, holds the bound `.py` path). A bound path would mean a strategy was auto-opened.
        var notebook = ty.GetField("_notebook", BF).GetValue(root) as MarimoNotebookDocument;
        if (notebook == null) return "U3: notebook missing";
        if (!string.IsNullOrEmpty(notebook.CurrentPath))
            return "U3: no-resume boot must be the File→New blank state (unbound), but bound to: " + notebook.CurrentPath;
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
}
