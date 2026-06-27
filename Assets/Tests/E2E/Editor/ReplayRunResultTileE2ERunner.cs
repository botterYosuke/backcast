// ReplayRunResultTileE2ERunner.cs — Surface E2E (Panel category) for the Run Result POPUP
// (#172/#173 / ADR-0037 / findings 0125). 台本: ReplayRunResultTileE2ERunner.md.
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod ReplayRunResultTileE2ERunner.Run -logFile <log>
//   # expect: [REPLAY RUNRESULT TILE PASS] ... / exit=0
//
// なぜ存在するか:
//   run_result は元々 back-plane の dock base singleton（DockLayer 1.0×）だった。ADR-0037 で
//   **screen-anchored な右上ポップアップ**（RunResultPopup）へ cutover した（#172）。表示は content-derived、
//   × close で dismiss latch（#173）。本 runner は (a) running↔full-stats のテキスト描画（#100① の再実行
//   stale ガード含む・format 関数は無改変＝findings 0125 D5）、(b) content-derived 可視、(c) **LiveManual で
//   出さない＝live hasContent を LiveAuto に scoping する D3 の sticky-flag ガード**、(d) **永続化しない**
//   （CaptureLayout 非対象＝D6）、(e) × close + 同一 run dismiss latch（D7）、(f) **対称再 arm**（Replay の
//   falling→rising / LiveAuto の run_id 変化＝D8・#164 の片側欠落死角を pin）を AFK で固定する。
//
// 2 ゲート分割（behavior-to-e2e: DATA 経路の C#↔Python 跨ぎ）:
//   * DATA 半分（#100① = engine.last_run_summary を run-begin で clear する source 挙動）は Python e2e が正本:
//     python/tests/test_notebook_replay_afk.py::test_run_summary_cleared_at_run_begin_... (findings 0077 §2)。
//   * RENDER + 可視 + latch 半分 = Unity でしか証明できない。本 runner がそれ。
//
// Python-FREE: 内容は WorkspaceEngineHost.TestPortfolioJsonOverride / TestRunSummaryJsonOverride を、live の
// telemetry は host.Panel.Apply(<wire>) を、mode は FooterModeViewModel.ApplyPoll を直接駆動。実 backend / _lanes は起こさない。
//
// RED litmus（delete-the-production-logic）:
//   * PushReplayTiles の running/complete 分岐を complete 固定に潰す → RRT-02/04 RED。
//   * DriveRunResultPopup の `&& DisplayMode==LiveAuto`（D3）を外す → RRT-06（LiveManual stale 漏れ）RED。
//   * hasContent の `|| HasTelemetry` 半分 or telemetry run_id fallback を外す → RRT-06T RED（telemetry-only run が出ない／再 arm せず）。
//   * run_result を再び controller window 化（CaptureLayout に乗る）→ RRT-07 RED。
//   * popup overlay を Content（infinite canvas）配下に再 parent する → RRT-10 RED（pan で動く構造退行）。
//   * × close の latch を no-op に → RRT-08 RED。
//   * LiveAuto 再 arm を run_id 変化でなく boolean falling edge にする → RRT-09B RED（2nd run で再出現せず）。

using System;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;

public static class ReplayRunResultTileE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;

    // Self-consistent Replay portfolio snapshot: equity(155321) = cash(54321) + position MV(50×2002),
    // BUY 50 @2000 FILLED. Only needs to be non-empty + recognizable for the running-view render.
    const string PortfolioRun1 =
        "{\"buying_power\":54321.0,\"cash\":54321.0,\"equity\":155321.0," +
        "\"positions\":[{\"symbol\":\"7203.TSE\",\"qty\":50,\"avg_price\":2000.0,\"unrealized_pnl\":100.0}]," +
        "\"orders\":[{\"symbol\":\"7203.TSE\",\"side\":\"BUY\",\"qty\":50.0,\"price\":2000.0,\"status\":\"FILLED\",\"ts_ms\":2}]," +
        "\"realized_pnl\":0.0,\"unrealized_pnl\":100.0}";

    // run2's running snapshot — a DIFFERENT portfolio string (one more fill) so the dual-payload gate
    // re-renders even though we also clear the summary. Mirrors a real 2nd run streaming fresh fills.
    const string PortfolioRun2Streaming =
        "{\"buying_power\":54000.0,\"cash\":54000.0,\"equity\":155000.0," +
        "\"positions\":[{\"symbol\":\"7203.TSE\",\"qty\":50,\"avg_price\":2000.0,\"unrealized_pnl\":80.0}]," +
        "\"orders\":[" +
        "{\"symbol\":\"7203.TSE\",\"side\":\"BUY\",\"qty\":50.0,\"price\":2000.0,\"status\":\"FILLED\",\"ts_ms\":2}," +
        "{\"symbol\":\"7203.TSE\",\"side\":\"BUY\",\"qty\":10.0,\"price\":2001.0,\"status\":\"FILLED\",\"ts_ms\":3}]," +
        "\"realized_pnl\":0.0,\"unrealized_pnl\":80.0}";

    const string SummaryRun1 =
        "{\"fills_count\":2,\"equity_points\":68,\"total_pnl\":-410010.0," +
        "\"max_drawdown\":1234.0,\"sharpe\":0.5,\"sortino\":0.7}";

    const string SummaryRun2 =
        "{\"fills_count\":3,\"equity_points\":70,\"total_pnl\":12345.0," +
        "\"max_drawdown\":222.0,\"sharpe\":1.25,\"sortino\":1.5}";

    // Live lifecycle wire envelopes (PeelTag = {"<Tag>": <inner>}). run_id differs so RRT-09B can prove
    // the LiveAuto re-arm keys off run_id (the sticky-flag-safe signal), not a boolean falling edge.
    static string LifecycleWire(string runId, string status) =>
        "{\"LiveStrategyEvent\":{\"run_id\":\"" + runId + "\",\"strategy_id\":\"s\",\"status\":\"" + status + "\",\"ts_ms\":1}}";

    // Telemetry wire envelope (tag "LiveStrategyTelemetry"). Drives the HasTelemetry / telemetry-run_id path
    // with NO lifecycle — the telemetry-only leg (RRT-06T) that pins the `|| HasTelemetry` half of hasContent
    // and the `: p.HasTelemetry ? LatestTelemetry.RunId` re-arm fallback (findings 0125 F8 / code-review [Fix]).
    static string TelemetryWire(string runId, double realized, double unrealized, int orders, int fills) =>
        "{\"LiveStrategyTelemetry\":{\"run_id\":\"" + runId + "\",\"strategy_id\":\"s\",\"realized_pnl\":" + realized +
        ",\"unrealized_pnl\":" + unrealized + ",\"order_count\":" + orders + ",\"fill_count\":" + fills + ",\"ts_ms\":1}}";

    public static void Run()
    {
        string fail = null;
        try
        {
            fail = RunSections();
            if (fail == null) fail = RunLiveScopeAndLatchSections();   // #172 D3 + #173 D7/D8
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[REPLAY RUNRESULT TILE PASS] RRT-01 empty→hidden / RRT-02 running→visible / RRT-03 full-stats / " +
                      "RRT-04 #100① rerun→running (not stale) / RRT-05 rerun→full-stats / " +
                      "RRT-06 LiveAuto visible · LiveManual hidden (sticky-flag anti-stale, D3) / " +
                      "RRT-06T telemetry-only leg (|| HasTelemetry + telemetry run_id fallback) / " +
                      "RRT-07 no-persist (absent from CaptureLayout, D6) / " +
                      "RRT-10 screen-anchored ScreenSpaceOverlay outside Content (D1) / " +
                      "RRT-08 × close latches · same-run stays dismissed (D7) / " +
                      "RRT-09 symmetric re-arm (Replay rising · LiveAuto run_id, D8) verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[REPLAY RUNRESULT TILE FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ── RRT-01..05: content-derived render + visibility in the REPLAY shape (override seam). ──
    static string RunSections()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindAnyObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "BackcastWorkspaceRoot missing from scene";

        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var host = ty.GetField("_host", BF).GetValue(root) as WorkspaceEngineHost;
        var liveShape = ty.GetField("_lastLiveShape", BF);
        var refresh = ty.GetMethod("RefreshLiveTiles", BF);               // per-frame driver → PushReplayTiles in Replay
        var drivePopup = ty.GetMethod("DriveRunResultPopup", BF);         // #172/#173: visibility + latch
        var rrView = ty.GetField("_runResultView", BF)?.GetValue(root);
        var popup = ty.GetField("_runResultPopup", BF)?.GetValue(root);
        var hostTy = typeof(WorkspaceEngineHost);
        var pfOverride = hostTy.GetField("TestPortfolioJsonOverride", BF);
        var smOverride = hostTy.GetField("TestRunSummaryJsonOverride", BF);
        if (host == null || liveShape == null || refresh == null || pfOverride == null || smOverride == null)
            return "root internals not found (renamed?) — host/_lastLiveShape/RefreshLiveTiles/overrides";
        if (drivePopup == null)
            return "RRT: DriveRunResultPopup not found (renamed? — the popup visibility/latch driver is missing)";
        if (rrView == null)
            return "run_result view not wired by BuildWorkspace (_runResultView is null) — the whole surface is uncovered";
        if (popup == null)
            return "RRT: _runResultPopup not built by BuildWorkspace — the popup surface is uncovered";

        liveShape.SetValue(root, false);   // Replay shape → RefreshLiveTiles routes to PushReplayTiles

        Action drive = () => { refresh.Invoke(root, null); drivePopup.Invoke(root, null); };
        Action<string> pf = v => pfOverride.SetValue(host, v);
        Action<string> sm = v => smOverride.SetValue(host, v);

        try
        {
            // ── RRT-01: no portfolio committed → popup HIDDEN (honest-empty no longer paints "(no data)" text). ──
            pf(null); sm(null); drive();
            if (PopupVisible(popup))
                return "RRT-01 empty: the popup is VISIBLE before any run — honest-empty must hide the card (content-derived)";

            // ── RRT-02: run1 streaming (portfolio present, summary still empty) → popup VISIBLE + RUNNING view. ──
            pf(PortfolioRun1); sm(null); drive();
            if (!PopupVisible(popup))
                return "RRT-02 running: portfolio-present did NOT show the popup (content-derived visibility broken)";
            string running1 = ViewText(rrView);
            if (running1 == null || !running1.Contains("running"))
                return "RRT-02 running: portfolio-present + empty-summary did NOT render the running view (got: " + running1 + ")";
            if (running1.Contains("fills:"))
                return "RRT-02 running: running view leaked full-stats text 'fills:' (branch collapsed? got: " + running1 + ")";

            // ── RRT-03: run1 completes (summary published) → FULL-STATS view (DecodeRunResult bound). ──
            sm(SummaryRun1); drive();
            if (!PopupVisible(popup)) return "RRT-03 full-stats: popup hidden while a completed run has content";
            string full1 = ViewText(rrView);
            if (full1 == null || full1.Contains("running"))
                return "RRT-03 full-stats: summary inject did NOT switch off the running view (dual-gate regressed? got: " + full1 + ")";
            if (!full1.Contains("fills:2"))
                return "RRT-03 full-stats: missing fills:2 (got: " + full1 + ")";
            if (!full1.Contains("-410010"))
                return "RRT-03 full-stats: missing total_pnl -410010 (TotalPnl unbound / zero-filled; got: " + full1 + ")";

            // ── RRT-04 (#100① GATE): bt.replay pressed a 2nd time → run2 begins, summary cleared at run-begin
            // (override→"") WHILE the portfolio streams fresh fills. The view MUST return to RUNNING — NOT keep
            // run1's stale full-stats. Render-layer half of findings 0077 D1. ──
            pf(PortfolioRun2Streaming); sm(""); drive();
            if (!PopupVisible(popup)) return "RRT-04 #100①: popup hidden while run2 is streaming content";
            string rerunRunning = ViewText(rrView);
            if (rerunRunning == null || !rerunRunning.Contains("running"))
                return "RRT-04 #100①: a 2nd run with a cleared summary did NOT return to the running view — " +
                       "run1's stale full-stats persisted into run2 (the #100 bug; got: " + rerunRunning + ")";
            if (rerunRunning.Contains("fills:2") || rerunRunning.Contains("-410010"))
                return "RRT-04 #100①: run1's stale full-stats (fills:2 / -410010) leaked into run2's running view (got: " + rerunRunning + ")";

            // ── RRT-05: run2 completes (new summary) → full-stats again, with run2's numbers (cycle repeatable). ──
            sm(SummaryRun2); drive();
            string full2 = ViewText(rrView);
            if (full2 == null || full2.Contains("running"))
                return "RRT-05 rerun-complete: run2 summary did NOT switch back to full-stats (got: " + full2 + ")";
            if (!full2.Contains("fills:3") || !full2.Contains("12345"))
                return "RRT-05 rerun-complete: full-stats did not show run2's numbers fills:3 / 12345 (stale run1? got: " + full2 + ")";
        }
        finally
        {
            pf(null); sm(null);
        }
        return null;
    }

    // ── RRT-06..09: LiveAuto-scoped visibility (D3 anti-stale) + no-persist (D6) + × latch (D7) + symmetric
    // re-arm (D8). Drives the REAL root: DriveRunResultPopup + FooterModeViewModel.ApplyPoll + host.Panel.Apply
    // + the override seam. A fresh scene build. ──
    static string RunLiveScopeAndLatchSections()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindAnyObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "RRT-06: BackcastWorkspaceRoot missing in scene";
        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var host = ty.GetField("_host", BF).GetValue(root) as WorkspaceEngineHost;
        var footerMode = ty.GetField("_footerMode", BF)?.GetValue(root);
        var liveShape = ty.GetField("_lastLiveShape", BF);
        var drivePopup = ty.GetMethod("DriveRunResultPopup", BF);
        var refresh = ty.GetMethod("RefreshLiveTiles", BF);   // drives PushLiveTiles (live body text via Refresh(p))
        var popup = ty.GetField("_runResultPopup", BF)?.GetValue(root);
        var rrView = ty.GetField("_runResultView", BF)?.GetValue(root);
        if (host == null) return "RRT-06: _host not built (renamed?)";
        if (footerMode == null) return "RRT-06: _footerMode not built (renamed?)";
        if (drivePopup == null) return "RRT-06: DriveRunResultPopup not found (renamed?)";
        if (refresh == null) return "RRT-06: RefreshLiveTiles not found (renamed?)";
        if (popup == null) return "RRT-06: _runResultPopup not built by BuildWorkspace";
        if (rrView == null) return "RRT-06: _runResultView not built by BuildWorkspace";
        var applyPoll = footerMode.GetType().GetMethod("ApplyPoll");
        if (applyPoll == null) return "RRT-06: FooterModeViewModel.ApplyPoll not found (renamed?)";
        var hostTy = typeof(WorkspaceEngineHost);
        var pfOverride = hostTy.GetField("TestPortfolioJsonOverride", BF);
        var smOverride = hostTy.GetField("TestRunSummaryJsonOverride", BF);
        if (pfOverride == null || smOverride == null) return "RRT-06: host override fields not found (renamed?)";

        void Poll(string mode) => applyPoll.Invoke(footerMode, new object[] { "{\"execution_mode\":\"" + mode + "\",\"venue_state\":\"CONNECTED\"}" });
        void Drive() => drivePopup.Invoke(root, null);
        void DriveLiveText() => refresh.Invoke(root, null);   // live shape → PushLiveTiles → _runResultView.Refresh(p)
        void Shape(bool live) => liveShape.SetValue(root, live);
        Action<string> pf = v => pfOverride.SetValue(host, v);
        Action<string> sm = v => smOverride.SetValue(host, v);
        void ApplyLifecycle(string runId, string status) => host.Panel.Apply(LifecycleWire(runId, status));
        void ApplyTelemetry(string runId, double r, double u, int o, int f) => host.Panel.Apply(TelemetryWire(runId, r, u, o, f));

        // ── RRT-06T (D3 / D8, the telemetry-only LiveAuto leg): a run that streams TELEMETRY before any
        // lifecycle (HasTelemetry=true, HasLifecycle=false) still shows the popup with its telemetry body
        // text, and its run_id (the telemetry fallback) arms + re-arms the latch. Pins the `|| HasTelemetry`
        // half of hasContent AND the `: p.HasTelemetry ? LatestTelemetry.RunId` re-arm fallback (findings
        // 0125 F8 / code-review [Fix]) — deleting either leaves this leg uncovered. Runs FIRST, while the
        // sticky HasLifecycle is still false (a later run can never reach the telemetry branch — F4). ──
        Shape(true);
        Poll("LiveAuto");
        ApplyTelemetry("run-T", 11.0, 22.0, 3, 2);   // HasTelemetry=true; HasLifecycle stays false
        DriveLiveText();
        Drive();
        if (!PopupVisible(popup))
            return "RRT-06T: a telemetry-only LiveAuto run did NOT show the popup (the `|| HasTelemetry` half of hasContent is broken)";
        string teleText = ViewText(rrView);
        if (teleText == null || !teleText.Contains("fills=2") || teleText.Contains("run="))
            return "RRT-06T: telemetry-only popup body wrong — expected telemetry stats (fills=2) with NO lifecycle 'run=' line (got: " + teleText + ")";
        InvokeClose(popup); Drive();
        if (PopupVisible(popup)) return "RRT-06T: × close did not hide the telemetry-only popup";
        ApplyTelemetry("run-U", 5.0, 6.0, 1, 1);   // a NEW run_id via telemetry alone (no lifecycle ever)
        Drive();
        if (!PopupVisible(popup))
            return "RRT-06T (D8): a new telemetry-only run_id did NOT re-arm a dismissed popup — the telemetry run_id " +
                   "fallback is missing (re-arm would never fire for a lifecycle-less run)";
        Debug.Log("[E2E RRT-06T PASS] telemetry-only LiveAuto run shows the popup + a new telemetry run_id re-arms it (|| HasTelemetry + telemetry run_id fallback)");
        ResetLatch(root, ty);   // clean slate for RRT-06 (the lifecycle-driven gate)

        // ── RRT-06 (D3, the sticky-flag anti-stale gate): a LiveAuto run shows the popup with its run text;
        // flipping to LiveManual HIDES it — even though LivePanelViewModel's flags are sticky (still true).
        // Without the `&& DisplayMode==LiveAuto` scope the stale LiveAuto telemetry would leak into LiveManual.
        // Also pins the live BODY TEXT wiring (PushLiveTiles → Refresh(p) → FormatRunResult into the popup
        // body) — a visible-but-empty popup would otherwise pass a visibility-only gate. ──
        Shape(true);
        Poll("LiveAuto");
        ApplyLifecycle("run-A", "RUNNING");       // host.Panel.HasLifecycle=true, RunId="run-A"
        DriveLiveText();                          // PushLiveTiles writes FormatRunResult(p) into the popup body
        Drive();
        if (!PopupVisible(popup)) return "RRT-06: LiveAuto run with telemetry did NOT show the popup";
        string liveText = ViewText(rrView);
        if (liveText == null || !liveText.Contains("run-A"))
            return "RRT-06: LiveAuto popup is visible but its body text is empty/stale — the PushLiveTiles→Refresh(p)→FormatRunResult wiring is broken (got: " + liveText + ")";
        Poll("LiveManual");
        Drive();
        if (PopupVisible(popup))
            return "RRT-06 (D3): popup VISIBLE in LiveManual after a LiveAuto run — the sticky flags leaked STALE telemetry " +
                   "(the live hasContent must be scoped to DisplayMode==LiveAuto)";
        Poll("LiveAuto");
        Drive();
        if (!PopupVisible(popup)) return "RRT-06: returning to LiveAuto did NOT re-show the popup (content still present)";

        // RRT-06 guard (the load-bearing `if(!IsNullOrEmpty(runId))` in DriveRunResultPopup): after a DISMISS,
        // a LiveAuto→LiveManual→LiveAuto round-trip on the SAME run must NOT re-arm the popup. A naive
        // `_runResultLastRunId = runId` (unconditional) would null the tracker in LiveManual, then see
        // run-A != null on return and spuriously re-show a popup the user dismissed for the still-running run.
        InvokeClose(popup);
        Drive();
        if (PopupVisible(popup)) return "RRT-06 guard: × close did not hide the LiveAuto popup";
        Poll("LiveManual"); Drive();
        Poll("LiveAuto"); Drive();   // same run-A, no new Apply
        if (PopupVisible(popup))
            return "RRT-06 guard: a LiveManual round-trip spuriously RE-ARMED a dismissed popup on the SAME run " +
                   "(the run_id tracker must not be clobbered to null in LiveManual — only a genuinely new run re-arms)";
        Debug.Log("[E2E RRT-06 PASS] popup shows in LiveAuto with run text, hides in LiveManual despite sticky flags (D3); a mode round-trip does not spuriously re-arm a dismissed popup");

        // ── RRT-07 (D6, no-persist): run_result must be ABSENT from CaptureLayout — the popup does not ride
        // floatingWindows (it is not a controller window). A regression that re-windowed run_result would
        // make it reappear here. ──
        var captureLayout = ty.GetMethod("CaptureLayout", BF);
        if (captureLayout == null) return "RRT-07 premise: CaptureLayout not found (renamed?)";
        var doc = captureLayout.Invoke(root, null);
        var captured = doc?.GetType().GetField("floatingWindows")?.GetValue(doc) as System.Collections.IEnumerable;
        if (captured == null) return "RRT-07: CaptureLayout().floatingWindows was null (capture path broken?)";
        foreach (var entry in captured)
        {
            var et = entry.GetType();
            if ((et.GetField("id")?.GetValue(entry) as string) == "run_result")
                return "RRT-07 (D6): run_result is present in CaptureLayout — the popup must NOT be persisted " +
                       "(it was re-windowed / re-added to a controller?)";
        }
        Debug.Log("[E2E RRT-07 PASS] run_result is absent from CaptureLayout — the popup is not persisted (D6)");

        // ── RRT-10 (D1, screen-anchored / pan-invariant STRUCTURAL pin): the popup card must live under a
        // ScreenSpaceOverlay Canvas parented DIRECTLY to the workspace root transform — NOT under Content
        // (the parallax infinite-canvas). Pan-invariance is the whole point of the cutover. A regression
        // re-parenting it under Content would still pass RRT-07 (it rides no controller → absent from
        // CaptureLayout either way); only this structural assert catches "the popup pans with the canvas". ──
        var popupRoot = popup.GetType().GetField("_root", BF).GetValue(popup) as GameObject;
        if (popupRoot == null) return "RRT-10: popup _root not built";
        var overlayTf = popupRoot.transform.parent;
        if (overlayTf == null) return "RRT-10: popup card has no overlay Canvas parent";
        var popupCanvas = overlayTf.GetComponent<Canvas>();
        if (popupCanvas == null) return "RRT-10: popup overlay has no Canvas component";
        if (popupCanvas.renderMode != RenderMode.ScreenSpaceOverlay)
            return "RRT-10 (D1): popup Canvas is not ScreenSpaceOverlay — it would not be screen-anchored (got: " + popupCanvas.renderMode + ")";
        if (overlayTf.parent != root.transform)
            return "RRT-10 (D1): popup overlay is NOT a direct child of the workspace root — if it sits under Content " +
                   "it pans with the infinite canvas (the screen-anchored seam is broken)";
        Debug.Log("[E2E RRT-10 PASS] popup is a ScreenSpaceOverlay Canvas parented to the root (outside Content) — screen-anchored / pan-invariant (D1)");

        // ── RRT-08 (D7, × close latch + same-run stays dismissed). Replay shape via the override seam. ──
        ResetLatch(root, ty);
        Shape(false);
        pf(PortfolioRun1); sm(null);
        DriveReplay(root, ty); Drive();
        if (!PopupVisible(popup)) return "RRT-08 setup: popup not visible before the close";
        InvokeClose(popup);                         // simulate the × click (OnClose → _runResultDismissed=true)
        Drive();
        if (PopupVisible(popup)) return "RRT-08 (D7): × close did not hide the popup (the dismiss latch is not honored)";
        sm(SummaryRun1);                            // same run: running → complete transition (portfolio unchanged)
        DriveReplay(root, ty); Drive();
        if (PopupVisible(popup))
            return "RRT-08 (D7): popup re-appeared on the running→complete transition of the SAME run — a dismiss must hold for the whole run";
        Debug.Log("[E2E RRT-08 PASS] × close latches; same-run running→complete stays dismissed (D7)");

        // ── RRT-09A (D8, Replay re-arm): after a dismiss, the portfolio falling to honest-empty then rising on
        // the NEXT run re-arms the latch → the popup re-appears. ──
        pf(null); DriveReplay(root, ty); Drive();   // honest-empty (falling edge)
        if (PopupVisible(popup)) return "RRT-09A setup: popup visible while honest-empty between runs";
        pf(PortfolioRun2Streaming); sm(null); DriveReplay(root, ty); Drive();   // next run injects content (rising)
        if (!PopupVisible(popup))
            return "RRT-09A (D8): the next Replay run (portfolio re-injected) did NOT re-show a dismissed popup — Replay re-arm is broken";
        Debug.Log("[E2E RRT-09A PASS] Replay re-arm: next run (honest-empty→content rising edge) re-shows a dismissed popup (D8)");

        // ── RRT-09B (D8, LiveAuto re-arm — the #164 symmetry trap): after a dismiss, a NEW LiveAuto run
        // (different run_id) re-arms the latch. The flags are STICKY so a boolean falling edge never appears
        // between two runs — keying off run_id is what keeps re-arm symmetric with Replay. ──
        ResetLatch(root, ty);
        pf(null); sm(null);
        Shape(true);
        Poll("LiveAuto");
        ApplyLifecycle("run-A", "RUNNING");
        Drive();
        if (!PopupVisible(popup)) return "RRT-09B setup: first LiveAuto run did not show the popup";
        InvokeClose(popup);
        Drive();
        if (PopupVisible(popup)) return "RRT-09B setup: × close did not hide the popup in LiveAuto";
        ApplyLifecycle("run-B", "RUNNING");         // a genuinely NEW run (run_id changes; HasLifecycle stays sticky-true)
        Drive();
        if (!PopupVisible(popup))
            return "RRT-09B (D8): a NEW LiveAuto run (run_id changed) did NOT re-show a dismissed popup — the re-arm " +
                   "is keying off a boolean falling edge (which never fires for sticky flags) instead of run_id (#164 trap)";
        Debug.Log("[E2E RRT-09B PASS] LiveAuto re-arm: a new run_id re-shows a dismissed popup despite sticky flags (D8 symmetry)");

        return null;
    }

    // Run the Replay tile poll once so the popup body text tracks the override before DriveRunResultPopup
    // reads visibility (mirrors the real Update order: RefreshLiveTiles before DriveRunResultPopup).
    static void DriveReplay(BackcastWorkspaceRoot root, Type ty)
        => ty.GetMethod("RefreshLiveTiles", BF).Invoke(root, null);

    // Reset the session-only dismiss latch + its per-mode edge trackers so a sub-scenario is self-contained.
    static void ResetLatch(BackcastWorkspaceRoot root, Type ty)
    {
        ty.GetField("_runResultDismissed", BF).SetValue(root, false);
        ty.GetField("_runResultPrevReplayHasContent", BF).SetValue(root, false);
        ty.GetField("_runResultLastRunId", BF).SetValue(root, null);
    }

    // Simulate the × click: invoke the popup's OnClose action (exactly what the close Button's onClick calls).
    static void InvokeClose(object popup)
    {
        var onClose = popup.GetType().GetField("OnClose").GetValue(popup) as Action;
        onClose?.Invoke();
    }

    static bool PopupVisible(object popup)
    {
        if (popup == null) return false;
        var p = popup.GetType().GetProperty("IsVisible");
        return p != null && (bool)p.GetValue(popup);
    }

    // Read the rendered Text of a LivePanelTileView via its private _content field.
    static string ViewText(object view)
    {
        if (view == null) return null;
        var content = view.GetType().GetField("_content", BF)?.GetValue(view) as Text;
        return content != null ? content.text : null;
    }
}
