// ChartUniverseSyncHitlFixture.cs — Issue #123 HITL helper (CHARTSYNC-05, 台本:
// ChartUniverseSyncE2ERunner.md / findings 0095).
//
// The AFK gate (ChartUniverseSyncE2ERunner) proves the orphan-chart despawn headlessly. CHARTSYNC-05 is the
// HITL leg: confirm on REAL pixels that a layout sidecar carrying `chart:<iid>` + an EMPTY universe boots /
// opens with the sidebar reading "No instruments" AND ZERO Chart floating windows — no orphan.
//
// The bug only manifests across a restore→reseed with a MISMATCHED sidecar (layout has chart:X, universe
// empty), which cannot be produced by in-session clicks (live edits always fire Universe.Changed). So this
// menu writes that mismatched fixture document with the REAL on-disk stores (format-correct by construction)
// and optionally arms it as the boot-resume pointer.
//
// HOW TO RUN (owner, in the Unity Editor GUI). The items live under the SAME menu as the other HITL
// tools: Tools ▸ Backcast (= the "Backcast" submenu):
//   1. Tools ▸ Backcast ▸ "Issue123 ChartSync - Generate + arm boot-resume fixture"  (writes the pair,
//      logs the .py path, sets it as the resume document).
//   2. Enter Play mode.  EXPECT: sidebar "No instruments" + NO Chart window (the orphan was despawned).
//      — OR — skip the arm and use File ▸ Open ▸ pick the logged .py for the File→Open path instead.
//   3. Tools ▸ Backcast ▸ "Issue123 ChartSync - Disarm (clear resume pointer)" when done, to restore boot.
// SEE THE BUG (optional contrast): `git stash` the fix line in ReseedFromEditor (or checkout pre-fix),
//   repeat step 2 → the orphan Chart window APPEARS over an empty sidebar. Restore the fix afterward.

using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ChartUniverseSyncHitlFixture
{
    const string RESUME_KEY = "backcast.lastDocument";   // mirror of BackcastWorkspaceRoot.ResumeKey
    const string IID = "7203.TSE";
    static readonly Vector2 CHART_TL = new Vector2(812f, -456f);
    const float CHART_W = 520f, CHART_H = 360f;

    // a stable, owner-visible path (survives Unity restarts; outside the project so Temp cleanup can't eat it).
    static string FixtureDir => Path.Combine(Path.GetTempPath(), "backcast-hitl-123");
    static string FixturePy  => Path.Combine(FixtureDir, "orphan_chart_demo.py");

    // NOTE on the menu path: Unity treats '#' '%' '&' '_' in a MenuItem string as shortcut modifiers
    // ('#' = Shift), so a path segment like "#123" silently mangles the menu — keep the path ASCII and
    // modifier-free. Registered under the same "Tools/Backcast" root as the other HITL menus.
    [MenuItem("Tools/Backcast/Issue123 ChartSync - Generate + arm boot-resume fixture")]
    public static void GenerateAndArm()
    {
        string py = WriteFixture();
        PlayerPrefs.SetString(RESUME_KEY, py);
        PlayerPrefs.Save();
        Debug.Log("[HITL #123] fixture written + ARMED as boot-resume doc:\n  " + py +
                  "\n  → Enter Play mode. EXPECT: sidebar 'No instruments' + ZERO Chart windows (orphan despawned)." +
                  "\n  → Or File ▸ Open this .py for the File→Open path. Disarm via the HITL menu when done.");
        EditorUtility.RevealInFinder(py);
    }

    [MenuItem("Tools/Backcast/Issue123 ChartSync - Generate fixture only (File-Open path)")]
    public static void GenerateOnly()
    {
        string py = WriteFixture();
        Debug.Log("[HITL #123] fixture written (NOT armed):\n  " + py +
                  "\n  → Enter Play mode, then File ▸ Open this .py. EXPECT: sidebar 'No instruments' + ZERO Chart windows.");
        EditorUtility.RevealInFinder(py);
    }

    [MenuItem("Tools/Backcast/Issue123 ChartSync - Disarm (clear resume pointer)")]
    public static void Disarm()
    {
        PlayerPrefs.SetString(RESUME_KEY, "");
        PlayerPrefs.Save();
        Debug.Log("[HITL #123] resume pointer cleared — next Play boots to the untitled File→New default.");
    }

    // Write the document pair: scenario key with an EMPTY universe + layout key carrying chart:7203 at a
    // distinctive rect. Uses the SAME real stores as production (ScenarioSidecarStore / LayoutSidecarStore),
    // so the on-disk shape is correct by construction. Returns the .py path.
    static string WriteFixture()
    {
        Directory.CreateDirectory(FixtureDir);
        File.WriteAllText(FixturePy, "# issue #123 HITL: layout has a chart, universe is empty.\nx = 1\n");

        // scenario key: EMPTY universe (the mismatch — sidebar will read "No instruments").
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            FixturePy, new StartupParamsForWrite("2025-01-06", "2025-01-10", "Daily", "1000000"),
            new List<string>());

        // layout key: a single chart:7203 floating window at a distinctive rect (the orphan to despawn).
        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),
            hakoniwaProfiles = null,
            canvasView = null,
            floatingWindows = new List<FloatingWindowLayout>
            {
                new FloatingWindowLayout(DockShape.ChartId(IID), FloatingWindowCatalog.KIND_CHART,
                    CHART_TL.x, CHART_TL.y, CHART_W, CHART_H, 0, true),
            },
            strategyEditors = new List<StrategyEditorState>(),
            cellPositions = new List<CellPosition>(),
        };
        LayoutSidecarStore.WriteLayout(FixturePy, doc);
        return FixturePy;
    }
}
