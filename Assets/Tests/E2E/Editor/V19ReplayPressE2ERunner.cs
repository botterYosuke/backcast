// V19ReplayPressE2ERunner.cs — v19 Replay「実ボタン press 単一縦串」実データ E2E 回帰ゲート
// （台本: 同ディレクトリの V19ReplayPressE2ERunner.md / 設計の木: docs/findings/0090・前段 0089）。
//
// owner が `v19_morning_cell.py` を *手で開いて ▶ を押す* のと同じ経路を、実 NAS データ上で end-to-end に自動駆動
// する唯一の単一縦串。既存 2 ゲート（V19ReplayLiveE2ERunner = 実 Python/NAS/約定だが press 配線をバイパス＝
// InvokeRunCell 直叩き / NotebookToHakoniwaJourneyE2ERunner = 実 press 配線だが fake executor・同期レーン）が
// どちらも跨がない継ぎ目——実 decompose→synthesize 往復・実 worker thread 上の実 Python・実ファイルでの __file__
// 解決——を 1 本で踏む。production 配線を fake へ差し替えない（実 PythonnetMarimoSynthesizer / 実
// HostNotebookCellExecutor / NotebookRunLane(startWorker:true) / NotebookRunController をそのまま駆動）。
//
//   <Unity> -batchmode -nographics -quit -projectPath . \
//           -executeMethod V19ReplayPressE2ERunner.Run -logFile <abs>
//   # expect: [E2E V19-PRESS PASS] ... fills_count=N
//   # ⚠️ 実 NAS（/Volumes/StockData/jp）を継承するため Bash サンドボックス無効で起動（子プロセスが /Volumes を継承）。
//   #    mount 不在は SKIP（PASS 扱い）= 偽 GREEN を作らない。InitializePython("MOCK") は shutdown segfault で
//   #    exit=139 になり得るが verdict は PASS/FAIL タグで判定（#107 と同型）。確認は Bash `grep -a "E2E V19-PRESS"`。
//
// RED→GREEN litmus（delete-the-production-logic）: findings 0089 の strategyPath inject を外すと worker の実 cold-run で
// _artifacts が FileNotFound → 約定ゼロで V19PRESS-06 RED / decompose→synthesize 往復が壊れて bt.replay セルが消えると
// V19PRESS-05/06 RED / worker lane の build+run 同一スレッド契約が崩れると ContextNotInitializedError で RED。

using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;
using Debug = UnityEngine.Debug;

public static class V19ReplayPressE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string VENUE = "MOCK";                // Replay は live venue 不要（server + 実 marimo を立てるためだけ）
    const string ENTRY_DAY = "2025-01-06";      // 単一営業日に絞ってゲートを軽く（entry 10:00 / exit 14:55 同日）
    const int RUN_TIMEOUT_MS = 300000;          // 実 minute backtest（52 universe・1 日）の完了待ち上限
    const int SUMMARY_TIMEOUT_MS = 15000;       // run 完了後 run_summary が poll cache へ載るまでの待ち上限

    static WorkspaceEngineHost s_host;

    public static void Run()
    {
        string fail = null;
        try { fail = Execute(); }
        catch (Exception e) { fail = "driver: " + e; }
        finally
        {
            try { s_host?.Stop(); }
            catch (Exception e) { Debug.LogWarning("[E2E V19-PRESS] host.Stop failed (non-fatal): " + e.Message); }
        }

        if (fail == null)
        {
            Debug.Log("[E2E V19-PRESS PASS] opened v19_morning_cell.py through the REAL marimo decompose, seeded the " +
                      "scenario from its sidecar, pressed the bt.replay cell's REAL ▶ button, and the production worker " +
                      "lane drove the REAL embedded marimo over the REAL J-Quants minute mount: the cell self-loaded its " +
                      "cell-adjacent artifacts via __file__ and traded (top-k 10:00 entry / 14:55 flatten produced fills).");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E V19-PRESS FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // Returns null on PASS/SKIP, else the first failure message.
    static string Execute()
    {
        string repoRoot = Directory.GetParent(Application.dataPath).FullName;   // <repo>/Assets -> <repo>
        string v19Dir = Path.Combine(repoRoot, "python", "strategies", "v19");
        string cellPy = Path.GetFullPath(Path.Combine(v19Dir, "v19_morning_cell.py"));
        string cellJson = Path.Combine(v19Dir, "v19_morning_cell.json");
        if (!File.Exists(cellPy) || !File.Exists(cellJson))
            return "v19_morning_cell.py / .json not found under " + v19Dir;

        // ── DuckDB minute mount: env > .env (EnvConfig). Absent → SKIP (do NOT fabricate a GREEN). ──
        // Probe the WHOLE minute store (any *.duckdb present), not a single hardcoded symbol — a delisted /
        // renamed sentinel must not silently SKIP a mounted store and drop this gate's coverage.
        string duckRoot = Environment.GetEnvironmentVariable("BACKCAST_JQUANTS_DUCKDB_ROOT");
        if (string.IsNullOrEmpty(duckRoot)) duckRoot = EnvConfig.Get("BACKCAST_JQUANTS_DUCKDB_ROOT");
        string minuteDir = string.IsNullOrEmpty(duckRoot) ? null : Path.Combine(duckRoot, "stocks_minute");
        bool mountPresent = !string.IsNullOrEmpty(minuteDir) && Directory.Exists(minuteDir)
                            && Directory.EnumerateFiles(minuteDir, "*.duckdb").Any();
        if (!mountPresent)
        {
            Debug.Log("[E2E V19-PRESS SKIP] J-Quants DuckDB minute mount absent (BACKCAST_JQUANTS_DUCKDB_ROOT=" +
                      (duckRoot ?? "<unset>") + "). Mount the NAS and run sandbox-disabled to exercise this gate.");
            return null;   // skip = PASS (repo convention for real-mount gates)
        }

        // ── compose the REAL workspace root headlessly (editmode OpenScene; Awake does NOT run). ──
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindAnyObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "BackcastWorkspaceRoot missing in scene";
        Type ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));

        // grab the root's OWN host and bring Python up BEFORE BuildWorkspace, so the production
        // PythonnetMarimoSynthesizer / HostNotebookCellExecutor reference a ready server. We do NOT inject a
        // fake synthesizer or fake executor — BuildWorkspace wires the real ones (the whole point of this gate).
        var host = ty.GetField("_host", BF).GetValue(root) as WorkspaceEngineHost;
        if (host == null) return "_host field missing (renamed?)";
        s_host = host;
        host.InitializePython(VENUE);
        if (!host.ServerReady) return "host server not ready after InitializePython";

        // load_replay_data falls back to engine.paths.jquants_duckdb_root() (= os.environ
        // BACKCAST_JQUANTS_DUCKDB_ROOT) — push it into the embedded interpreter so the host's DataEngine
        // reads the REAL mount (mirrors V19ReplayLiveE2ERunner / TachibanaLiveE2ERunner.SetOsEnviron).
        SetOsEnviron("BACKCAST_JQUANTS_DUCKDB_ROOT", duckRoot);

        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);     // compose-root-headlessly seam (no-op seed when unbound)
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);   // wires REAL synthesizer / executor / worker lane / controller
        ty.GetField("_isOwner", BF).SetValue(root, true);        // so the production onClick gate (_isOwner && ServerReady) fires

        // read the production seams (renamed → fail loudly, like NBHAKO).
        var coordinator = ty.GetField("_coordinator", BF)?.GetValue(root) as NotebookCellCoordinator;
        var controller = ty.GetField("_notebookRun", BF)?.GetValue(root) as NotebookRunController;
        var cellButtons = ty.GetField("_cellRunButtons", BF)?.GetValue(root) as IDictionary;
        var scenario = ty.GetField("_scenario", BF)?.GetValue(root) as ScenarioStartupController;
        if (coordinator == null) return "_coordinator not built (renamed?)";
        if (controller == null) return "_notebookRun not built (renamed?)";
        if (cellButtons == null) return "_cellRunButtons not built (renamed?)";
        if (scenario == null) return "_scenario not built (renamed?)";

        // ── V19PRESS-01: open v19 through the production coordinator (REAL marimo decompose_json). ──
        if (!coordinator.Open(cellPy, null))
            return "V19PRESS-01: coordinator.Open(v19_morning_cell.py) failed (decompose) — LastError=" +
                   Trunc(coordinator.Notebook.LastError, 240);
        int cellCount = coordinator.Notebook.Cells.Count;
        if (cellCount < 1)
            return "V19PRESS-01: v19 decomposed to " + cellCount + " cells (expected the real multi-cell notebook)";

        // ── V19PRESS-02: the opened doc seeds the scenario panel from its sidecar (ReseedFromEditor). ──
        ty.GetMethod("ReseedFromEditor", BF).Invoke(root, null);
        if (scenario.Universe.Count <= 0)
            return "V19PRESS-02: opening v19 did not seed the scenario panel from its sidecar (universe is empty) — " +
                   "SeedScenarioFromEditor / sidecar read broke";
        // trim to a single trading day for gate speed (faithful: the user can pick one day). The committed
        // universe + granularity from the sidecar are kept; only the window is narrowed.
        scenario.SetStart(ENTRY_DAY);
        scenario.SetEnd(ENTRY_DAY);

        // ── V19PRESS-03: the strategyPath leg resolves to the REAL v19 .py (findings 0089, non-vacuous). ──
        string strategyPath = ty.GetMethod("BuildNotebookStrategyPath", BF).Invoke(root, null) as string;
        if (string.IsNullOrEmpty(strategyPath))
            return "V19PRESS-03: BuildNotebookStrategyPath returned null after opening v19 — the editor did not bind the " +
                   ".py path, so the marimo __file__ leg is vacuous";
        if (!PathsEqual(strategyPath, cellPy))
            return "V19PRESS-03: BuildNotebookStrategyPath did not resolve to v19_morning_cell.py (got [" + strategyPath + "])";

        // locate the bt.replay cell and its REAL run button (the cell the user actually presses).
        string region = null;
        foreach (var cell in coordinator.Notebook.Cells)
        {
            if (cell.Body != null && cell.Body.Contains("bt.replay")) { region = coordinator.RegionOf(cell); break; }
        }
        if (region == null) return "V19PRESS-04: no bt.replay cell found in the opened v19 notebook";
        var runBtn = cellButtons.Contains(region) ? cellButtons[region] as Button : null;
        if (runBtn == null) return "V19PRESS-04: the bt.replay cell (" + region + ") has no ▶ RUN button (WireCellRunButton)";
        if (GlyphText(runBtn) != "▶") return "V19PRESS-04: precondition — the bt.replay RUN button does not start as ▶";

        // ── V19PRESS-04: press the REAL button → production onClick gate → RunCell → RUNNING (▶→■). ──
        runBtn.onClick.Invoke();
        if (!controller.IsBacktestRunning)
            return "V19PRESS-04: pressing the REAL bt.replay button did not enter RUNNING — the onClick gate " +
                   "(_isOwner && ServerReady) or the running guard (drivesReplay && scenarioJson) did not fire";
        if (GlyphText(runBtn) != "■")
            return "V19PRESS-04: the REAL cell button ▶ did not toggle to ■ while running";

        // ── V19PRESS-05: pump the main-thread drain until the worker lane's real backtest finishes. ──
        var sw = Stopwatch.StartNew();
        while (controller.IsBacktestRunning && sw.ElapsedMilliseconds < RUN_TIMEOUT_MS)
        {
            controller.DrainAndRoute();
            Thread.Sleep(50);
        }
        controller.DrainAndRoute();   // final drain
        if (controller.IsBacktestRunning)
            return "V19PRESS-05: the press-driven backtest did not finish within " + (RUN_TIMEOUT_MS / 1000) +
                   "s (worker hung — possible thread-context / GIL seam regression)";

        // ── V19PRESS-06: the production run-summary poll cache shows real fills (top-k entry/exit traded). ──
        string summaryJson = null;
        string parseError = null;
        int fills = 0;
        var sw2 = Stopwatch.StartNew();
        while (sw2.ElapsedMilliseconds < SUMMARY_TIMEOUT_MS)
        {
            summaryJson = host.RunSummaryJson;
            if (!string.IsNullOrEmpty(summaryJson))
            {
                try
                {
                    var o = JObject.Parse(summaryJson);
                    fills = o.Value<int?>("fills_count") ?? o.Value<int?>("trade_count") ?? 0;
                    parseError = null;
                }
                catch (Exception e) { parseError = e.Message; }   // surfaced distinctly below — never masked as zero-fills
                if (fills > 0) break;
            }
            Thread.Sleep(50);
        }
        if (string.IsNullOrEmpty(summaryJson))
            return "V19PRESS-06: no run_summary surfaced after the press-driven run finished — the cell did not finalize a " +
                   "run (artifacts/__file__ not resolved, or the synthesised source did not drive bt.replay)";
        if (parseError != null)
            return "V19PRESS-06: run_summary surfaced but was UNPARSABLE (" + parseError + ") — a backend run_summary " +
                   "contract/schema break, NOT a zero-fills trading failure; payload=" + Trunc(summaryJson, 300);
        if (fills <= 0)
            return "V19PRESS-06: the press-driven run finalized but produced NO fills (fills_count/trade_count=0) over " +
                   ENTRY_DAY + " — top-k entry/exit did not trade; summary=" + Trunc(summaryJson, 300);

        Debug.Log("[E2E V19-PRESS] real-mount press-driven Replay traded: fills_count=" + fills +
                  ", cells=" + cellCount + ", run_summary=" + Trunc(summaryJson, 300));
        return null;   // PASS
    }

    // os.environ[key]=value via PyString (mirror V19ReplayLiveE2ERunner.SetOsEnviron).
    static void SetOsEnviron(string key, string value)
    {
        using (Python.Runtime.Py.GIL())
        using (var os = Python.Runtime.Py.Import("os"))
        using (var environ = os.GetAttr("environ"))
        using (var k = new Python.Runtime.PyString(key))
        using (var v = new Python.Runtime.PyString(value))
            environ.SetItem(k, v);
    }

    // the run-button glyph text ("▶" idle / "■" running), read off the real button's "RunGlyph" child Text.
    static string GlyphText(Button btn)
    {
        var glyph = btn != null ? btn.transform.Find("RunGlyph") : null;
        var t = glyph != null ? glyph.GetComponent<Text>() : null;
        return t != null ? t.text : null;
    }

    static bool PathsEqual(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    static string Trunc(string s, int n) => s == null ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");
}
