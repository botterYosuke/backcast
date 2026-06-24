// AddChartLadderJourneyE2ERunner.cs — Journey E2E regression gate for the owner story
// "venue: kabu Station に .env でログイン → 7203 を +Add → Ladder 付チャートを表示" (台本: same-dir
// AddChartLadderJourneyE2ERunner.md). 方針: ADR-0022（subscribe 配線）/ findings 0094.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod AddChartLadderJourneyE2ERunner.Run -logFile <abs>
//   # expect: [E2E ADD CHART LADDER JOURNEY PASS] ... / exit=0  (確認は Bash `grep -a "ADD CHART LADDER"`)
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// WHAT THIS GATES — the UI half of the story that NO existing runner asserts:
//   * LiveSubscribeWiringE2ERunner (SUBWIRE-02) gates the DATA half: [+ Add] in Live → real subscribe →
//     DepthDecoder.HasDepth=true. It only decodes the STATE JSON — it never inspects the spawned chart
//     window or the ladder GameObject.
//   * DepthLadderE2ERunner (DEPTH-01/02) builds its ladders via Universe.ReplaceAll in Replay, then TOGGLES
//     mode on the already-built ladders. It never exercises a chart +Added AFTER Live was entered.
//   * The death-zone: BuildChartContent reads `_lastLadderLive` AT SPAWN to decide whether a freshly +Added
//     chart's ladder is active+inset. A chart added while Live is already on must spawn with its ladder
//     VISIBLE — not hidden-until-the-next-mode-toggle. That spawn-time read is gated here.
//
// This drives the REAL production "+ Add" entry (UniverseSidebarController.AddFromPicker, the SIDEBAR-14
// path) on the REAL BackcastWorkspaceRoot composition, so Universe.Changed → SyncChartWindowsToUniverse →
// SpawnChartWindowAt → BuildChartContent runs exactly as in production. Python-FREE: no InitializePython.
// The LiveSubscribeHook fires (AddFromPicker, Live) but LaneSubscribeSink.Subscribe null-guards on
// host.Lanes (null without InitializePython) → no-op, so the subscribe RPC half is never touched here
// (it is owned by SUBWIRE-02).
//
// VACUITY KILL (delete-the-production-logic litmus, findings 0094): Section2 is a Replay negative control —
// the SAME +Add path with `_lastLadderLive` still false must spawn the ladder HIDDEN + chart full-width.
// If BuildChartContent hardcoded `true` (or dropped the `_lastLadderLive` read), Section2 goes RED; if it
// hardcoded `false`, Section1 (ADDLADDER-04) goes RED. The two sections together pin "the spawn-time
// active/inset state TRACKS _lastLadderLive", not a constant.

using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class AddChartLadderJourneyE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const float EPS = 1e-3f;
    const float LADDER_WIDTH = 120f;   // mirror of BackcastWorkspaceRoot.LADDER_WIDTH
    const string IID = "7203.TSE";     // owner story: Toyota chart
    const string IID2 = "9984.TSE";    // second add (still Live) — softbank

    // A Live-mode poll snapshot: execution_mode LiveManual + venue CONNECTED. Feeding this to
    // FooterModeViewModel.ApplyPoll flips DisplayMode to LiveManual so DriveDepthLadders sees isLive=true.
    const string LIVE_POLL = "{\"execution_mode\":\"LiveManual\",\"venue_state\":\"CONNECTED\"}";

    sealed class StubSp : IStrategyFileProvider
    {
        public bool TryGetStrategyFile(out string path) { path = null; return false; }
    }

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_AddInLiveSpawnsVisibleLadder()   // ADDLADDER-01..04
                ?? Section2_AddInReplaySpawnsHiddenLadder();  // ADDLADDER-05 (non-vacuity control)
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E ADD CHART LADDER JOURNEY PASS] +Add 7203 while Live (after .env kabu login = "
                    + "KABU-LIVE-01) spawns a chart window via the REAL AddFromPicker→Universe.Changed→"
                    + "SyncChartWindowsToUniverse path, the tile composes ChartView + chartArea + sibling "
                    + "DepthLadderView, and the ladder is ACTIVE + chart inset by LADDER_WIDTH at spawn time "
                    + "(BuildChartContent reads _lastLadderLive). Replay negative control: the same +Add path "
                    + "spawns the ladder HIDDEN + chart full-width (proves the spawn-time state tracks "
                    + "_lastLadderLive, non-vacuous). DATA half (real subscribe→board render) = SUBWIRE-02; "
                    + "real kabu .env login + 実板 = KABU-LIVE-01/02 (HITL). findings 0094.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E ADD CHART LADDER JOURNEY FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── Section 1 — Live: empty universe → enter Live → [+ Add] 7203 → chart-with-visible-ladder ──
    // Covers: ADDLADDER-01 (enter Live on empty universe), ADDLADDER-02 ([+ Add] → membership + chart spawn),
    //         ADDLADDER-03 (tile composes ChartView + chartArea + sibling DepthLadderView),
    //         ADDLADDER-04 (spawn-time ladder ACTIVE + chart inset — the central new invariant).
    static string Section1_AddInLiveSpawnsVisibleLadder()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S1: BackcastWorkspaceRoot missing in scene";

        var sidebar = ty.GetField("_sidebarCtrl", BF)?.GetValue(root) as UniverseSidebarController;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var footerMode = ty.GetField("_footerMode", BF)?.GetValue(root) as FooterModeViewModel;
        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var depthLadders = ty.GetField("_depthLadders", BF)?.GetValue(root) as IDictionary;
        var chartAreas = ty.GetField("_chartAreas", BF)?.GetValue(root) as IDictionary;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var lastLadderLive = ty.GetField("_lastLadderLive", BF);
        var driveDepth = ty.GetMethod("DriveDepthLadders", BF);
        if (sidebar == null || scenario == null || footerMode == null || dockWindows == null
            || depthLadders == null || chartAreas == null || chartViews == null
            || lastLadderLive == null || driveDepth == null)
            return "S1: root seams not built (renamed? _sidebarCtrl/_footerMode/_dockWindows/_depthLadders/_chartAreas/_chartViews/_lastLadderLive/DriveDepthLadders)";

        // ── ADDLADDER-01: empty universe → no chart windows, no ladders; then enter Live. ──
        if (scenario.Universe.Ids.Count != 0) return "S1 ADDLADDER-01: universe must start empty";
        if (depthLadders.Count != 0) return "S1 ADDLADDER-01: precondition — no ladders before any +Add";
        if (dockWindows.RectOf(DockShape.ChartId(IID)) != null)
            return "S1 ADDLADDER-01: precondition — chart:" + IID + " must not exist before +Add";
        if ((bool)lastLadderLive.GetValue(root))
            return "S1 ADDLADDER-01: precondition — _lastLadderLive must be false (Replay) before entering Live";

        // Enter Live via the real path: feed a LiveManual poll then drive the per-frame ladder sync. With
        // zero ladders this only flips _lastLadderLive=true (DriveDepthLadders loops zero ladders harmlessly
        // and returns before touching host state — Python-FREE).
        footerMode.ApplyPoll(LIVE_POLL);
        if (footerMode.DisplayMode != FooterModeViewModel.LiveManual)
            return "S1 ADDLADDER-01: ApplyPoll(LiveManual) did not flip DisplayMode (got " + footerMode.DisplayMode + ")";
        driveDepth.Invoke(root, null);
        if (!(bool)lastLadderLive.GetValue(root))
            return "S1 ADDLADDER-01: DriveDepthLadders did not latch _lastLadderLive=true after entering Live";
        Debug.Log("[E2E ADDLADDER-01 PASS] empty universe → enter Live latched _lastLadderLive=true (0 chart/0 ladder precondition).");

        // ── ADDLADDER-02: drive the REAL "+ Add" entry (AddFromPicker) → membership + chart spawn. ──
        bool added = sidebar.AddFromPicker(IID, UniverseSourceMode.Live, new StubSp(), 1);
        if (!added) return "S1 ADDLADDER-02: AddFromPicker(" + IID + ") returned false (not added)";
        if (!scenario.Universe.Ids.Contains(IID))
            return "S1 ADDLADDER-02: " + IID + " not in universe after +Add";
        var rect = dockWindows.RectOf(DockShape.ChartId(IID));
        if (rect == null) return "S1 ADDLADDER-02: chart window chart:" + IID + " not spawned by Universe.Changed→Sync";
        Debug.Log("[E2E ADDLADDER-02 PASS] AddFromPicker(7203,Live) → universe membership + chart window spawned.");

        // ── ADDLADDER-03: the tile composes ChartView + chartArea + a sibling DepthLadderView. ──
        var ladder = depthLadders[IID] as DepthLadderView;
        var area = chartAreas[IID] as RectTransform;
        var cv = chartViews[IID] as ChartView;
        if (ladder == null) return "S1 ADDLADDER-03: no DepthLadderView mounted for " + IID;
        if (area == null) return "S1 ADDLADDER-03: no chartArea for " + IID;
        if (cv == null) return "S1 ADDLADDER-03: no ChartView for " + IID;
        if (ladder.transform.parent != area.parent)
            return "S1 ADDLADDER-03: ladder is not a sibling of chartArea (not mounted in the tile) for " + IID;
        if (cv.transform != area)
            return "S1 ADDLADDER-03: ChartView is not on the chartArea for " + IID;
        Debug.Log("[E2E ADDLADDER-03 PASS] spawned tile composes ChartView + chartArea + sibling DepthLadderView.");

        // ── ADDLADDER-04: the central invariant — spawned-in-Live ladder is ACTIVE + chart inset. ──
        if (!ladder.gameObject.activeSelf)
            return "S1 ADDLADDER-04: ladder spawned HIDDEN despite Live entered before +Add "
                 + "(BuildChartContent did not read _lastLadderLive=true at spawn)";
        if (Mathf.Abs(area.offsetMax.x - (-LADDER_WIDTH)) > EPS)
            return "S1 ADDLADDER-04: chart not inset by LADDER_WIDTH at spawn (offsetMax.x=" + area.offsetMax.x + ", want " + (-LADDER_WIDTH) + ")";

        // Second add while STILL Live must also spawn active+inset (the read is per-spawn, not one-shot).
        bool added2 = sidebar.AddFromPicker(IID2, UniverseSourceMode.Live, new StubSp(), 2);
        if (!added2) return "S1 ADDLADDER-04: second AddFromPicker(" + IID2 + ") returned false";
        var ladder2 = depthLadders[IID2] as DepthLadderView;
        var area2 = chartAreas[IID2] as RectTransform;
        if (ladder2 == null || area2 == null) return "S1 ADDLADDER-04: second tile " + IID2 + " not composed";
        if (!ladder2.gameObject.activeSelf)
            return "S1 ADDLADDER-04: second Live-spawned ladder (" + IID2 + ") hidden — spawn read is one-shot, not per-spawn";
        if (Mathf.Abs(area2.offsetMax.x - (-LADDER_WIDTH)) > EPS)
            return "S1 ADDLADDER-04: second Live-spawned chart (" + IID2 + ") not inset";
        Debug.Log("[E2E ADDLADDER-04 PASS] Live-spawned ladder ACTIVE + chart inset by LADDER_WIDTH at spawn (per-spawn, 2 tiles).");

        return null;
    }

    // ── Section 2 — Replay negative control: the SAME +Add path with _lastLadderLive=false spawns the
    //    ladder HIDDEN + chart full-width. This is the non-vacuity floor for ADDLADDER-04: if the active/
    //    inset state were a constant `true`, this section goes RED. ──
    // Covers: ADDLADDER-05
    static string Section2_AddInReplaySpawnsHiddenLadder()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S2: BackcastWorkspaceRoot missing in scene";

        var sidebar = ty.GetField("_sidebarCtrl", BF)?.GetValue(root) as UniverseSidebarController;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var dockWindows = ty.GetField("_dockWindows", BF)?.GetValue(root) as FloatingWindowController;
        var depthLadders = ty.GetField("_depthLadders", BF)?.GetValue(root) as IDictionary;
        var chartAreas = ty.GetField("_chartAreas", BF)?.GetValue(root) as IDictionary;
        var lastLadderLive = ty.GetField("_lastLadderLive", BF);
        if (sidebar == null || scenario == null || dockWindows == null
            || depthLadders == null || chartAreas == null || lastLadderLive == null)
            return "S2: root seams not built (renamed?)";

        // Default mode is Replay — _lastLadderLive false; DO NOT enter Live.
        if ((bool)lastLadderLive.GetValue(root))
            return "S2 ADDLADDER-05: precondition — root must be in Replay (_lastLadderLive false)";

        bool added = sidebar.AddFromPicker(IID, UniverseSourceMode.Replay, new StubSp(), 1);
        if (!added) return "S2 ADDLADDER-05: AddFromPicker(" + IID + ", Replay) returned false";
        var rect = dockWindows.RectOf(DockShape.ChartId(IID));
        if (rect == null) return "S2 ADDLADDER-05: chart window not spawned in Replay (+Add still spawns the chart)";

        var ladder = depthLadders[IID] as DepthLadderView;
        var area = chartAreas[IID] as RectTransform;
        if (ladder == null || area == null) return "S2 ADDLADDER-05: tile not composed for " + IID;

        // The crux: the SAME spawn path, with _lastLadderLive=false, must hide the ladder + full-width chart.
        if (ladder.gameObject.activeSelf)
            return "S2 ADDLADDER-05: ladder spawned VISIBLE in Replay — active state is a constant, "
                 + "not tracking _lastLadderLive (ADDLADDER-04 would be vacuous)";
        if (Mathf.Abs(area.offsetMax.x) > EPS)
            return "S2 ADDLADDER-05: chart not full-width in Replay (offsetMax.x=" + area.offsetMax.x + ", want 0)";
        Debug.Log("[E2E ADDLADDER-05 PASS] Replay +Add spawns ladder HIDDEN + chart full-width (non-vacuity: spawn state tracks _lastLadderLive).");

        return null;
    }

    // ---- root composition (same pattern as ChartPlacementJourney / LiveSubscribeWiring) ----
    static BackcastWorkspaceRoot ComposeRoot(out Type ty)
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        ty = typeof(BackcastWorkspaceRoot);
        if (root == null) return null;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());   // #81: Python-free cell synthesis
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        return root;
    }
}
