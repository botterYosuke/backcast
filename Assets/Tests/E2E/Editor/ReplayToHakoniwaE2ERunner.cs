// ReplayToHakoniwaE2ERunner.cs — 再生→箱庭更新の E2E 回帰ゲート（台本: 同ディレクトリの
// ReplayToHakoniwaE2ERunner.md）。`Probe` から `E2ERunner` へ昇格（回帰ゲート化した E2E の命名規約）。
//
// The ONE end-to-end gate that stitches the two halves the existing gates leave apart: the Python
// kernel replay (test_replay_duckdb_kernel_afk.py proves the DATA half) and the Unity render path
// (BackcastWorkspaceProbe proves the composition half, but is deliberately Python-FREE — its header
// declares the real Replay streaming + chart render the owner HITL). This probe drives the REAL
// host run on a synthetic DuckDB and pumps the REAL BackcastWorkspaceRoot.Update() so the kernel's
// live get_state_json flows through InstrumentOhlcDecoder → ChartView.Render into the hakoniwa
// chart tile — covering the台本's steps 2-3 (File→Open) + 5-7 (run → kernel replay → 箱庭更新).
//
//   <Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
//           -executeMethod ReplayToHakoniwaE2ERunner.Run -logFile <log>
//   # expect: [E2E REPLAY→HAKONIWA PASS] ... / exit=0
//
// Unlike BackcastWorkspaceProbe this CLAIMS Python directly (host.InitializePython, bypassing the
// batchmode WorkspaceOwnership skip — the same legitimate move KernelTeardownProbe makes). It does
// NOT assert pixels (nographics has no GPU); step 7 is observed at the DATA layer: the decoded
// per-id OHLC count == streamed bars, the root's _chartRendered bookkeeping == that count (proves
// Update's render loop ran), and the ChartView built candle geometry (FirstCandle, proves Render
// actually painted). The run uses the production host call (_host.TryStartRun); the editor/notebook
// gate that resolves the strategy path (steps 4-5) is covered by BackcastWorkspaceProbe S9/S11 — here
// we feed the real kernel-native fixture so the kernel actually trades and streams.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Python.Runtime;

public static class ReplayToHakoniwaE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string INSTRUMENT = "8918.TSE";
    const int N_BARS = 50;                 // mirrors test_replay_duckdb_kernel_afk._N_BARS (> SELL_AT_BAR=40)
    const int PUMP_SLEEP_MS = 50;          // main-loop cadence while the lanes poll get_state_json
    const int PUMP_CAP = 1200;             // PUMP_CAP * PUMP_SLEEP_MS = 60s cap on the run (mirrors KernelTeardownProbe)
    const int SETTLE_FRAMES = 10;          // extra Update() frames after RunFinished to render the final snapshot
    const int SETTLE_SLEEP_MS = 20;

    static WorkspaceEngineHost s_host;

    static string TempDir => Path.Combine(Application.temporaryCachePath, "replay_to_hakoniwa_probe");
    static string DuckRoot => Path.Combine(TempDir, "duckdb");
    static string StrategyPy => Path.Combine(TempDir, "kernel_spike_buy_sell.py");

    public static void Run()
    {
        string fail = null;
        try
        {
            if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true);
            Directory.CreateDirectory(DuckRoot);
            fail = Execute();
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }
        finally
        {
            // teardown like production (force_stop + lanes/launcher join + server close; interpreter
            // left alive per ADR-0001). Never PythonEngine.Shutdown — the process exits below.
            try { s_host?.Stop(); }
            catch (Exception e) { Debug.LogWarning("[E2E REPLAY→HAKONIWA] host.Stop failed (non-fatal): " + e.Message); }
            try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, true); } catch { }
        }

        if (fail == null)
        {
            Debug.Log("[E2E REPLAY→HAKONIWA PASS] real kernel streamed " + N_BARS +
                      " bars into the hakoniwa chart tile via the production render path.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E REPLAY→HAKONIWA FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // Returns null on PASS, else the first failure message. Sets s_host so Run's finally can tear down.
    static string Execute()
    {
        var ty = typeof(BackcastWorkspaceRoot);

        // ── step 1: compose the real workspace root (Python-FREE) + CLAIM Python on this host ──
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "BackcastWorkspaceRoot missing in scene";

        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        root.SetSynthesizer(new FakeMarimoSynthesizer());       // Python-free cell synthesis (open path only)
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);
        ty.GetField("_isOwner", BF).SetValue(root, true);       // Update()'s render slice runs only for the owner

        var host = ty.GetField("_host", BF).GetValue(root) as WorkspaceEngineHost;
        if (host == null) return "could not read _host";
        s_host = host;

        // Claim Python on THIS host (bypassing the batchmode WorkspaceOwnership skip). Unlike
        // KernelTeardownProbe's bare PythonEngine.Initialize, we go through host.InitializePython
        // because we need the persistent server + lanes that stream get_state_json — the run's whole
        // point. The synthetic DuckDB root is injected via os.environ in BuildSyntheticDuckDb (read
        // lazily by load_replay_data over the .env mount), so nothing here touches the owner data.
        host.InitializePython("MOCK");
        if (!host.ServerReady) return "host server not ready after InitializePython";

        // ── build the synthetic per-symbol DuckDB + the strategy fixture (mirror the pytest gate) ──
        BuildSyntheticDuckDb();                                 // writes <DuckRoot>/stocks_daily/8918.duckdb + os.environ
        File.Copy(FixtureSource(), StrategyPy);                 // kernel-native fixture (inline SCENARIO, sys.path imports)

        // ── steps 2-3: File→Open the fixture .py — inline SCENARIO seeds the universe, spawning the
        // chart:8918.TSE tile (the #60 universe→tile wiring, BackcastWorkspaceProbe S10/S11). ──
        root.SetFileDialog(new StubFileDialog { NextResult = StrategyPy });
        ty.GetMethod("OnFileOpen", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        if (scenario == null) return "could not read _scenario";
        var ids = new List<string>(scenario.Universe.Ids);
        if (ids.Count != 1 || ids[0] != INSTRUMENT)
            return "File→Open did not seed the universe from the inline SCENARIO (got [" + string.Join(",", ids) + "])";
        var chartViews = ty.GetField("_chartViews", BF).GetValue(root) as IDictionary;
        if (chartViews == null || !chartViews.Contains(INSTRUMENT))
            return "chart tile for " + INSTRUMENT + " not spawned after open (universe→tile wiring)";

        // ── step 5: start the REAL run (the production host call OnRun makes at line 869) ──
        var req = new WorkspaceEngineHost.RunRequest
        {
            Instruments = ids.ToArray(),
            Start = scenario.Params.Start,
            End = scenario.Params.End,
            Granularity = ScenarioStartupParams.GranularityToString(scenario.Params.Granularity),
            StrategyPath = StrategyPy,
        };
        if (!host.TryStartRun(req)) return "host.TryStartRun refused (serverReady/running guard)";

        // ── steps 6-7: pump the REAL Update() while the kernel streams. Main is GIL-free
        // (BeginAllowThreads in InitializePython), so the lanes poll get_state_json on their own
        // threads and Update() reads the accumulating snapshot through _host.LatestStateJson. ──
        var update = ty.GetMethod("Update", BF);
        int beats = 0;
        while (!host.RunFinished && beats < PUMP_CAP)
        {
            update.Invoke(root, null);
            Thread.Sleep(PUMP_SLEEP_MS);
            if (++beats % 20 == 0)
                Debug.Log($"[E2E REPLAY→HAKONIWA] pumping (beat {beats}, running={host.IsRunning})");
        }
        if (!host.RunFinished) return "run did not finish within 60s";
        for (int i = 0; i < SETTLE_FRAMES; i++) { update.Invoke(root, null); Thread.Sleep(SETTLE_SLEEP_MS); }  // render the final snapshot

        if (host.StartError != null) return "run failed: " + host.StartError;

        // ── assert the step 6→7 stitch ──
        string state = host.LatestStateJson;
        if (string.IsNullOrEmpty(state)) return "LatestStateJson empty after run";
        InstrumentOhlcFrame frame = InstrumentOhlcDecoder.Decode(state, INSTRUMENT);
        if (!frame.HasSeries) return "decoded state has no per-instrument series for " + INSTRUMENT;
        int bars = frame.Ohlc != null ? frame.Ohlc.Count : 0;
        if (bars != N_BARS)
            return $"kernel streamed {bars} bars into state JSON, expected {N_BARS} (exactly-once)";

        // production render bookkeeping: Update's loop sets _chartRendered[id]=count ONLY after
        // ChartView.Render — so this == N proves the real render path executed (delete Render → no entry).
        var chartRendered = ty.GetField("_chartRendered", BF).GetValue(root) as IDictionary;
        if (chartRendered == null || !chartRendered.Contains(INSTRUMENT))
            return "Update() never rendered the chart tile (_chartRendered missing " + INSTRUMENT + ")";
        int rendered = (int)chartRendered[INSTRUMENT];
        if (rendered != N_BARS)
            return $"_chartRendered[{INSTRUMENT}]={rendered}, expected {N_BARS} (render loop count drift)";

        // the ChartView actually built candle geometry from the frame (the fixture's bars are all
        // bullish: close=open+2) — delete ChartView.Render's body and FirstCandle goes null.
        var cv = chartViews[INSTRUMENT] as ChartView;
        if (cv == null) return "ChartView for " + INSTRUMENT + " missing";
        if (cv.FirstCandle(true) == null)
            return "ChartView built no bullish candle — Render did not paint the streamed bars";

        return null;
    }

    // build <DuckRoot>/stocks_daily/8918.duckdb with N ascending daily bars (Code=8918), and hard-set
    // os.environ so load_replay_data's lazy jquants_duckdb_root() resolves OUR root over the .env mount.
    // Schema mirrors the real J-Quants daily file (Date, Code, OHLCV) — identical to the pytest gate.
    static void BuildSyntheticDuckDb()
    {
        string rootFwd = DuckRoot.Replace("\\", "/");
        string script =
            "import os, datetime, duckdb\n" +
            "root = r'__ROOT__'\n" +
            "os.environ['BACKCAST_JQUANTS_DUCKDB_ROOT'] = root\n" +
            "d = os.path.join(root, 'stocks_daily')\n" +
            "os.makedirs(d, exist_ok=True)\n" +
            "con = duckdb.connect(os.path.join(d, '8918.duckdb'))\n" +
            "con.execute('CREATE TABLE stocks_daily (Date DATE, Code VARCHAR, Open BIGINT, High BIGINT, Low BIGINT, Close BIGINT, Volume BIGINT)')\n" +
            "day0 = datetime.date(2024, 10, 1)\n" +
            "con.executemany('INSERT INTO stocks_daily VALUES (?, ?, ?, ?, ?, ?, ?)', [\n" +
            "    (day0 + datetime.timedelta(days=i), '8918', 1000 + i, 1005 + i, 995 + i, 1002 + i, 1000 + i)\n" +
            "    for i in range(" + N_BARS + ")])\n" +
            "con.close()\n";
        script = script.Replace("__ROOT__", rootFwd);
        using (Py.GIL()) PythonEngine.Exec(script);
    }

    // the kernel-native fixture the pytest gate runs (BUY bar 3 / SELL bar 40); imports are sys.path-based
    // (engine.kernel.*), so a temp copy loads fine once InitializePython has inserted ProjectRoot.
    static string FixtureSource()
        => Path.Combine(PythonRuntimeLocator.ProjectRoot, "spike", "fixtures", "strategies", "kernel_spike_buy_sell.py");
}
