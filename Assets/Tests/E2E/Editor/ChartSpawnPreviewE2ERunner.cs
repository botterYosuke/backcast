// ChartSpawnPreviewE2ERunner.cs — Issue #129 release-gate slice runner (台本: same-dir
// ChartSpawnPreviewE2ERunner.md). 方針: findings 0104 / ADR-0025 / ADR-0016.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartSpawnPreviewE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART SPAWN PREVIEW PASS] / exit=0  (確認は Bash `grep -a "CHART SPAWN PREVIEW"`)
//   # compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// WHAT THIS GATES — the C# WIRING half of #129 (findings 0104 F1/F3). When a Replay chart window
// is spawned (Universe.Changed) OR scenario params are committed (Settings → Scenario commit /
// File→Save / TryStartRun), BackcastWorkspaceRoot.RequestChartPreviewsForAllLiveCharts MUST fire
// the preview RPC for every live chart in _chartViews, forwarding the scenario's current
// start/end/granularity. The Python-side IDLE/Replay guard owns the contract; this runner is
// Python-FREE and captures host.RequestReplayChartPreview calls via TestReplayPreviewOverride.
//
// ⚠️ LIMITATION (findings 0104 第2回帰): this runner does NOT exercise real Python, real DuckDB, or the
// DuckDB-root RESOLUTION. It stays GREEN even when the live engine returns NO_DATA because the resolved
// root is wrong (e.g. a stale/dead PlayerPrefs root poisons os.environ — the actual #129 field failure).
// Real-path coverage lives elsewhere: DuckDbRootSettingsE2ERunner DUCKROOT-05 (stale stored root must
// fail soft, NOT poison os.environ) + pytest test_replay_chart_spawn_preview.py (populate_replay_preview
// against a real DuckDB). Do NOT treat a green here as "preview renders on the owner's machine".
//
// The Python half (engine.populate_replay_preview: D0 mode guard / D2 RUN guard / full-catalog
// history draw / S1 graceful) is gated by python/tests/test_replay_chart_spawn_preview.py
// (PREVIEW-01..11). #156 reopen (findings 0129): the cold preview is DECOUPLED from the scenario
// window — it always draws the instrument's full catalog history (get_date_range), so a valid
// today-relative seed window past a frozen historical snapshot no longer renders empty. PREVIEW-11
// is that regression gate (valid window outside the catalog still draws the full series).
//
// RED→GREEN litmus:
//   * delete the `RequestChartPreviewsForAllLiveCharts();` call at SyncChartWindowsToUniverse tail
//     → CHARTPREVIEW-01 (Universe.Changed) and CHARTPREVIEW-03 (restored-from-layout chart)
//     go RED — the preview hook was not invoked for the new iid.
//   * delete the `_scenario.Committed += RequestChartPreviewsForAllLiveCharts` subscription
//     → CHARTPREVIEW-02 (params-only Commit) goes RED — preview not reseeded after Settings edit.
//   * forward a STALE Start/End instead of _scenario.Params.* → CHARTPREVIEW-04 goes RED — the
//     captured args mismatch the committed scenario.
//
// SCOPE: this is the WIRING gate (which iids fire / with what params / on which seam). Python's
// guard semantics (IDLE/Replay/RUN cleanup/full-range fallback/graceful empty) are gated by the
// sibling pytest. Two-gate split per behavior-to-e2e SKILL: "Python-FREE fake for C# wiring,
// pytest for engine correctness."

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ChartSpawnPreviewE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string IID_A = "7203.TSE";
    const string IID_B = "9984.TSE";
    const string START = "2025-01-06";
    const string END   = "2025-01-10";

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "chart_spawn_preview_e2e");

    // Captured invocations of host.RequestReplayChartPreview, in arrival order. The override on
    // WorkspaceEngineHost (TestReplayPreviewOverride) replaces the pythonnet RPC so this runner is
    // Python-FREE — what gets captured here is exactly what the production code would have shipped
    // to populate_replay_preview.
    sealed class PreviewCall
    {
        public string Iid, Start, End, Granularity;
        public override string ToString() =>
            "(" + Iid + "," + Start + "," + End + "," + Granularity + ")";
    }
    static readonly List<PreviewCall> _calls = new List<PreviewCall>();

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = Section1_UniverseAdd_FiresPreviewForNewIid()           // CHARTPREVIEW-01
                ?? Section2_ScenarioCommit_FiresPreviewForAllCharts()    // CHARTPREVIEW-02
                ?? Section3_RestoredChart_FiresPreviewAtReseedTail()     // CHARTPREVIEW-03
                ?? Section4_ScenarioParams_ForwardedVerbatim();          // CHARTPREVIEW-04
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); }

        if (fail == null)
        {
            Debug.Log("[E2E CHART SPAWN PREVIEW PASS] Replay-mode chart spawn の Python RPC trigger が "
                    + "(a) SyncChartWindowsToUniverse 末尾で全 _chartViews へ発火・"
                    + "(b) ScenarioStartupController.Committed event で全 _chartViews へ発火・"
                    + "(c) RestoreFloating 由来の chart も ReseedFromEditor 末尾 sync で発火・"
                    + "(d) _scenario.Params.Start/End/Granularity を verbatim 転送、を満たす。"
                    + "Python 側の D0/D2/D3/S1 guard は test_replay_chart_spawn_preview.py (PREVIEW-01..07) で gate。findings 0104。");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART SPAWN PREVIEW FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── CHARTPREVIEW-01: Universe gains 7203 → SyncChartWindowsToUniverse spawns chart:7203 and
    //   the tail helper fires RequestReplayChartPreview(7203,…). NON-VACUOUS: the universe starts
    //   empty (no preview should be in _calls yet beyond the BuildWorkspace baseline), then
    //   adding the iid both spawns the chart AND fires preview exactly once for it. ──
    static string Section1_UniverseAdd_FiresPreviewForNewIid()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S1: BackcastWorkspaceRoot missing in scene";
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        if (scenario == null || chartViews == null) return "S1: root seams not built (renamed?)";
        // Pre-seed Params so the helper has Start/End/Granularity to forward.
        scenario.SetStart(START); scenario.SetEnd(END); scenario.SetGranularity(GranularityChoice.Daily);

        // Universe starts empty → no chart, no preview pending for that iid.
        if (chartViews.Count != 0) return "S1: precondition — _chartViews must start empty (got " + chartViews.Count + ")";
        _calls.RemoveAll(c => c.Iid == IID_A);

        scenario.Universe.Add(IID_A);   // Universe.Changed → SyncChartWindowsToUniverse → spawn + helper

        if (!chartViews.Contains(IID_A))
            return "S1 CHARTPREVIEW-01: chart:" + IID_A + " did not spawn on Universe.Add (broken upstream sync)";
        var hits = _calls.FindAll(c => c.Iid == IID_A);
        if (hits.Count == 0)
            return "S1 CHARTPREVIEW-01: preview RPC NOT fired for " + IID_A
                 + " (SyncChartWindowsToUniverse tail helper missing/wrong _host)";
        // Idempotent on params: the helper forwards Params.Start/End/Granularity verbatim. Asserted
        // in detail by Section4 — here we only pin that the SEAM fired with the SAME values for the
        // new iid, no decoy iid leaked, no NRE on a missing _host.
        var last = hits[hits.Count - 1];
        if (last.Start != START || last.End != END || last.Granularity != "Daily")
            return "S1 CHARTPREVIEW-01: forwarded params drifted (got " + last + ")";
        Debug.Log("[E2E CHARTPREVIEW-01 PASS] Universe.Add → spawn + preview RPC fired for new iid with current Params.");
        return null;
    }

    // ── CHARTPREVIEW-02: scenario.Committed (Settings → scenario section Commit) fires the helper
    //   for every live chart, even though Universe membership is UNCHANGED. The owner-identified
    //   hole in "tail-of-sync only" is exactly this: params-only edit (start/end changed but
    //   universe same) would silently leave preview stale without this seam. ──
    static string Section2_ScenarioCommit_FiresPreviewForAllCharts()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S2: BackcastWorkspaceRoot missing in scene";
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        if (scenario == null || chartViews == null) return "S2: root seams not built (renamed?)";

        // Seed a strategy path so Commit can write the sidecar (SetStartupParamsAndInstruments).
        string py = Path.Combine(TempRoot, "commit", "commit.py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "x = 1\n");

        scenario.SetStart(START); scenario.SetEnd(END); scenario.SetGranularity(GranularityChoice.Daily);
        scenario.SetInitialCash("1000000");   // Commit validation requires non-empty cash
        scenario.Universe.Add(IID_A);
        scenario.Universe.Add(IID_B);
        if (chartViews.Count != 2) return "S2: precondition — both charts must spawn (got " + chartViews.Count + ")";

        // Clear the baseline calls accumulated by the universe-add path so this section reads only
        // the Commit-driven invocations.
        _calls.Clear();
        // Mid-flight edit then commit (mirrors Settings → scenario section "Commit" button).
        scenario.SetEnd("2025-02-28");
        bool committed = scenario.Commit(py);
        if (!committed) return "S2 CHARTPREVIEW-02: scenario.Commit returned false (" + DescribeErrors(scenario.Errors) + ")";

        var distinct = new HashSet<string>(_calls.Select(c => c.Iid));
        if (!distinct.Contains(IID_A) || !distinct.Contains(IID_B))
            return "S2 CHARTPREVIEW-02: Commit did not fire preview for all live charts (got {" + string.Join(",", distinct) + "})";
        if (_calls.Any(c => c.End != "2025-02-28"))
            return "S2 CHARTPREVIEW-02: Commit forwarded STALE End (helper should read Params.End AT call time, got " + string.Join(",", _calls.Select(c => c.ToString())) + ")";
        Debug.Log("[E2E CHARTPREVIEW-02 PASS] scenario.Committed event fires preview for ALL live charts with FRESH Params (params-only edit not silent).");
        return null;
    }

    // ── CHARTPREVIEW-03: a chart restored from the layout sidecar (RestoreFloating path) ALSO
    //   gets preview. The ReseedFromEditor tail calls SyncChartWindowsToUniverse() unconditionally
    //   after the restore + reseed, so the helper sees the restored iid in _chartViews and fires.
    //   This is the case that "trigger only at SpawnChartWindowAt" would silently drop (the owner-
    //   identified hole in Option 2 of the trigger seam Q1). ──
    static string Section3_RestoredChart_FiresPreviewAtReseedTail()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S3: BackcastWorkspaceRoot missing in scene";
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        var applyLayout = ty.GetMethod("ApplyLayout", BF);
        var sync = ty.GetMethod("SyncChartWindowsToUniverse", BF);
        if (scenario == null || chartViews == null) return "S3: root seams not built (renamed?)";
        if (applyLayout == null || sync == null) return "S3: ApplyLayout/SyncChartWindowsToUniverse not found (renamed?)";
        scenario.SetStart(START); scenario.SetEnd(END); scenario.SetGranularity(GranularityChoice.Daily);

        // ApplyLayout restores chart:IID_A from the layout sidecar (the path that BYPASSES
        // SyncChartWindowsToUniverse → _chartViews is populated by RestoreFloating's
        // _dockWindows.Spawn → BuildDockContent → BuildChartContent, NOT by SpawnChartWindowAt).
        // This is exactly the case the owner's Q1 refined seam said "trigger only at
        // SpawnChartWindowAt" would silently drop.
        string py = WriteDocWithChart("restored", IID_A);
        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null)
            return "S3 CHARTPREVIEW-03: could not read layout from " + py;
        if (doc.floatingWindows == null || doc.FindWindow(DockShape.ChartId(IID_A)) == null)
            return "S3 CHARTPREVIEW-03: layout round-trip lost chart entry (writer/normalizer regression)";
        applyLayout.Invoke(root, new object[] { doc });
        // Positive control: RestoreFloating populated _chartViews via the restore-path (chart entered
        // _chartViews WITHOUT going through SpawnChartWindowAt). Establishes non-vacuity for the gate:
        // there IS a restored chart in _chartViews for the helper to iterate over.
        if (!chartViews.Contains(IID_A))
            return "S3 CHARTPREVIEW-03: positive-control failed — RestoreFloating did not spawn chart:" + IID_A;
        // Universe must also carry IID_A so the next Sync does NOT despawn the restored chart as
        // an orphan (universe is the SoT for membership — findings 0095). Mirrors the canonical
        // ReseedFromEditor tail's order: seed scenario from sidecar (universe gains IID_A) → Sync
        // (helper iterates _chartViews).
        scenario.Universe.Add(IID_A);
        _calls.Clear();
        sync.Invoke(root, null);   // canonical SyncChartWindowsToUniverse → tail helper → preview RPC

        if (!chartViews.Contains(IID_A))
            return "S3 CHARTPREVIEW-03: restored chart unexpectedly despawned by Sync (universe membership regression?)";
        var hits = _calls.FindAll(c => c.Iid == IID_A);
        if (hits.Count == 0)
            return "S3 CHARTPREVIEW-03: preview RPC NOT fired for the RESTORED chart "
                 + "(helper does not iterate _chartViews, or tail wiring missing — Sync ran but the new iid was not in _calls)";
        Debug.Log("[E2E CHARTPREVIEW-03 PASS] restored-from-layout chart enters _chartViews via RestoreFloating; "
                + "SyncChartWindowsToUniverse tail helper fires preview for it (helper iterates _chartViews SoT, not just newly-spawned iids).");
        return null;
    }

    // ── CHARTPREVIEW-04: Params.Start / Params.End / Granularity round-trip verbatim through the
    //   helper. Empty Start passes through as "" (Python side falls back to date_range — that
    //   contract is gated by PREVIEW-05). Granularity.None coerces to "Daily" (kernel SoT). ──
    static string Section4_ScenarioParams_ForwardedVerbatim()
    {
        var root = ComposeRoot(out var ty);
        if (root == null) return "S4: BackcastWorkspaceRoot missing in scene";
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        var chartViews = ty.GetField("_chartViews", BF)?.GetValue(root) as IDictionary;
        if (scenario == null || chartViews == null) return "S4: root seams not built (renamed?)";

        // (a) Daily + populated start/end → "Daily" / "2025-01-06" / "2025-01-10".
        scenario.SetStart(START); scenario.SetEnd(END); scenario.SetGranularity(GranularityChoice.Daily);
        _calls.Clear();
        scenario.Universe.Add(IID_A);
        var hitDaily = _calls.LastOrDefault(c => c.Iid == IID_A);
        if (hitDaily == null) return "S4 CHARTPREVIEW-04 (Daily): preview not fired";
        if (hitDaily.Start != START || hitDaily.End != END || hitDaily.Granularity != "Daily")
            return "S4 CHARTPREVIEW-04 (Daily): args mismatch — got " + hitDaily;

        // (b) Minute → "Minute".
        scenario.SetGranularity(GranularityChoice.Minute);
        _calls.Clear();
        scenario.Universe.Add(IID_B);
        var hitMinute = _calls.LastOrDefault(c => c.Iid == IID_B);
        if (hitMinute == null) return "S4 CHARTPREVIEW-04 (Minute): preview not fired";
        if (hitMinute.Granularity != "Minute")
            return "S4 CHARTPREVIEW-04 (Minute): granularity not 'Minute' — got " + hitMinute.Granularity;

        // (c) Empty start passes through to "" (Python side does the date_range fallback).
        scenario.SetStart("");
        _calls.Clear();
        scenario.Universe.Remove(IID_A);    // forces a sync
        scenario.Universe.Add(IID_A);       // spawn again → preview
        var hitEmpty = _calls.LastOrDefault(c => c.Iid == IID_A);
        if (hitEmpty == null) return "S4 CHARTPREVIEW-04 (empty start): preview not fired";
        if (hitEmpty.Start != "")
            return "S4 CHARTPREVIEW-04 (empty start): empty Start must round-trip as '', got '" + hitEmpty.Start + "'";

        // (d) GranularityChoice.None → "Daily" fallback (never silently 1-minute).
        scenario.SetGranularity(GranularityChoice.None);
        _calls.Clear();
        scenario.Universe.Remove(IID_B);
        scenario.Universe.Add(IID_B);
        var hitNone = _calls.LastOrDefault(c => c.Iid == IID_B);
        if (hitNone == null) return "S4 CHARTPREVIEW-04 (None gran): preview not fired";
        if (hitNone.Granularity != "Daily")
            return "S4 CHARTPREVIEW-04 (None gran): expected 'Daily' fallback, got '" + hitNone.Granularity + "'";
        Debug.Log("[E2E CHARTPREVIEW-04 PASS] Params.Start/End/Granularity forwarded verbatim; empty start → '', None gran → 'Daily'.");
        return null;
    }

    // ---- helpers ----

    static string WriteDocWithChart(string name, string iid)
    {
        string py = Path.Combine(TempRoot, name, name + ".py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "x = 1\n");
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            py, new StartupParamsForWrite(START, END, "Daily", "1000000"), new List<string> { iid });
        var doc = new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),
            hakoniwaProfiles = null,
            canvasView = null,
            floatingWindows = new List<FloatingWindowLayout>
            {
                new FloatingWindowLayout(DockShape.ChartId(iid), FloatingWindowCatalog.KIND_CHART,
                    812f, -456f, 520f, 360f, 0, true),
            },
            strategyEditors = new List<StrategyEditorState>(),
            cellPositions = new List<CellPosition>(),
        };
        LayoutSidecarStore.WriteLayout(py, doc);
        return py;
    }

    static string DescribeErrors(ScenarioStartupErrors e) =>
        "{start=" + (e.Start ?? "") + " end=" + (e.End ?? "") + " gran=" + (e.Granularity ?? "")
        + " cash=" + (e.InitialCash ?? "") + " uni=" + (e.Universe ?? "") + " cross=" + (e.CrossField ?? "") + "}";

    static BackcastWorkspaceRoot ComposeRoot(out Type ty)
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        ty = typeof(BackcastWorkspaceRoot);
        if (root == null) return null;
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        // Install the Python-FREE preview capture hook on the live host AFTER BuildWorkspace so the
        // real `_host` instance the root will call is the one we instrument. Per-section calls
        // accumulate into _calls; sections clear it at their own boundary.
        var host = ty.GetField("_host", BF)?.GetValue(root) as WorkspaceEngineHost;
        if (host == null) throw new Exception("ComposeRoot: _host is null after BuildWorkspace");
        // TestReplayPreviewOverride is `internal` on a runtime-assembly type, so the editor
        // assembly accesses it via reflection (same model as sibling probes — TestPortfolioJsonOverride
        // in NotebookToHakoniwaJourneyE2ERunner / ReplayRunResultTileE2ERunner). GetField returns null
        // on a rename → surface "renamed?" loudly instead of silently no-opping the hook.
        var hookField = typeof(WorkspaceEngineHost).GetField(
            "TestReplayPreviewOverride", BindingFlags.NonPublic | BindingFlags.Instance);
        if (hookField == null)
            throw new Exception("ComposeRoot: WorkspaceEngineHost.TestReplayPreviewOverride not found (renamed?)");
        Action<string, string, string, string> hook = (iid, s, e, g) => _calls.Add(
            new PreviewCall { Iid = iid, Start = s, End = e, Granularity = g });
        hookField.SetValue(host, hook);
        _calls.Clear();
        return root;
    }

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
