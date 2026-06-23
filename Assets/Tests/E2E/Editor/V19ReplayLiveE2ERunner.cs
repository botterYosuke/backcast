// V19ReplayLiveE2ERunner.cs — v19 marimo cell「Replay を実データで回す」実 Python E2E 回帰ゲート
// （台本: 同ディレクトリの V19ReplayLiveE2ERunner.md / 設計の木: docs/findings/0089）。
//
// これまで「実 v19_morning_cell.py を実 NAS データで Replay 実行」を end-to-end で回す AFK ゲートは
// 無かった（#99 で実カーネル Replay ゲート ReplayToHakoniwaE2ERunner が退役して以来の空席）。本ゲートは
// MOCK ではなく owner の実 J-Quants DuckDB（BACKCAST_JQUANTS_DUCKDB_ROOT）を直接読み、実 marimo cell を
// 本番 per-cell RUN の executor（HostNotebookCellExecutor が叩く WorkspaceEngineHost.InvokeRunCell の
// 4-arg pythonnet marshaling）経由で駆動して、cell が自分のアーティファクト（cell 隣接 artifacts/）を
// __file__ 相対で解決し、実約定（top-k 10:00 entry / 14:55 flatten）を出すことを assert する。
//
//   <Unity> -batchmode -nographics -quit -projectPath . \
//           -executeMethod V19ReplayLiveE2ERunner.Run -logFile <abs>
//   # expect: [E2E V19-REPLAY PASS] ... / exit=0
//   # ⚠️ NAS（/Volumes/StockData/jp）を読むため、起動シェルはサンドボックス無効で（子プロセスも /Volumes を
//   #    継承する必要がある）。データmount が無い環境は SKIP（PASS 扱い）= 偽 GREEN を作らない。
//
// ハーネス: WorkspaceEngineHost を単体 new し InitializePython("MOCK") で Python を直 claim（batchmode の
// WorkspaceOwnership スキップを正当に迂回・Tachibana/Kabu Live / KernelTeardownProbe と同型）。Replay は
// live venue を要さないので MOCK で server を立てるだけ。run_cell の DATA 半分（実 engine / 実 bars / 実
// artifact 解決 / 実約定）をゲートする。C# 側の press→controller→lane→executor wiring は Python-FREE の
// NotebookToHakoniwaJourneyE2ERunner が別途ゲート（2 ゲート分割・behavior-to-e2e）。
//
// RED→GREEN litmus: run_cell に strategy_path を流す配線（DataEngineBackend.run_cell が __file__ を inject）
// を外すと、cell の Path(__file__).parent/artifacts が cwd 由来になり FileNotFoundError → 約定ゼロで RED。

using System;
using System.IO;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

public static class V19ReplayLiveE2ERunner
{
    const string VENUE = "MOCK";                // Replay は live venue 不要（server を立てるためだけ）
    const string ENTRY_DAY = "2025-01-06";      // 単一営業日に絞ってゲートを軽く（entry 10:00 / exit 14:55 同日）

    static WorkspaceEngineHost s_host;

    public static void Run()
    {
        string fail = null;
        try { fail = Execute(); }
        catch (Exception e) { fail = "driver: " + e; }
        finally
        {
            try { s_host?.Stop(); }
            catch (Exception e) { Debug.LogWarning("[E2E V19-REPLAY] host.Stop failed (non-fatal): " + e.Message); }
        }

        if (fail == null)
        {
            Debug.Log("[E2E V19-REPLAY PASS] ran v19_morning_cell.py through the production per-cell RUN seam over " +
                      "the REAL J-Quants minute mount: the cell self-loaded its cell-adjacent artifacts via __file__ " +
                      "and traded the real engine (top-k 10:00 entry / 14:55 flatten produced fills).");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E V19-REPLAY FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // Returns null on PASS/SKIP, else the first failure message.
    static string Execute()
    {
        string repoRoot = Directory.GetParent(Application.dataPath).FullName;   // <repo>/Assets -> <repo>
        string v19Dir = Path.Combine(repoRoot, "python", "strategies", "v19");
        string cellPy = Path.Combine(v19Dir, "v19_morning_cell.py");
        string cellJson = Path.Combine(v19Dir, "v19_morning_cell.json");
        if (!File.Exists(cellPy) || !File.Exists(cellJson))
            return "v19_morning_cell.py / .json not found under " + v19Dir;

        // ── DuckDB minute mount: env > .env (EnvConfig). Absent → SKIP (do NOT fabricate a GREEN). ──
        string duckRoot = Environment.GetEnvironmentVariable("BACKCAST_JQUANTS_DUCKDB_ROOT");
        if (string.IsNullOrEmpty(duckRoot)) duckRoot = EnvConfig.Get("BACKCAST_JQUANTS_DUCKDB_ROOT");
        string sampleFile = string.IsNullOrEmpty(duckRoot)
            ? null : Path.Combine(duckRoot, "stocks_minute", "6920.duckdb");
        if (string.IsNullOrEmpty(duckRoot) || !File.Exists(sampleFile))
        {
            Debug.Log("[E2E V19-REPLAY SKIP] J-Quants DuckDB minute mount absent (BACKCAST_JQUANTS_DUCKDB_ROOT=" +
                      (duckRoot ?? "<unset>") + "). Mount the NAS and run sandbox-disabled to exercise this gate.");
            return null;   // skip = PASS (repo convention for real-mount gates)
        }

        // ── live-configured server を venue=MOCK で構築（Python を直 claim） ──
        var host = new WorkspaceEngineHost();
        s_host = host;
        host.InitializePython(VENUE);
        if (!host.ServerReady) return "host server not ready after InitializePython";

        // load_replay_data falls back to engine.paths.jquants_duckdb_root() (= os.environ
        // BACKCAST_JQUANTS_DUCKDB_ROOT) — push it into the embedded interpreter's environ so the host's
        // DataEngine reads the REAL mount (mirrors TachibanaLiveE2ERunner.SetOsEnviron for creds).
        SetOsEnviron("BACKCAST_JQUANTS_DUCKDB_ROOT", duckRoot);

        // committed scenario (the .json sidecar's `scenario` key — the universe the user committed),
        // trimmed to a single trading day for gate speed.
        var doc = JObject.Parse(File.ReadAllText(cellJson));
        var scenario = doc["scenario"] as JObject;
        if (scenario == null) return "v19_morning_cell.json has no `scenario` key";
        scenario["start"] = ENTRY_DAY;
        scenario["end"] = ENTRY_DAY;
        string scenarioJson = scenario.ToString(Newtonsoft.Json.Formatting.None);
        string source = File.ReadAllText(cellPy);

        // Locate the bt.replay cell by CONTENT (not a hardcoded index) so a cell insert/reorder in
        // v19_morning_cell.py can't silently press the wrong cell → false RED (mirrors the pytest
        // sibling's _pressed_replay_index).  marimo orders cells by @app.cell appearance, which is the
        // same order load_app_from_text yields, so the @app.cell-segment index IS the press index.
        var cellSegs = source.Split(new[] { "@app.cell" }, StringSplitOptions.None);
        int pressed = -1;
        for (int i = 1; i < cellSegs.Length; i++)
            if (cellSegs[i].Contains("bt.replay")) { pressed = i - 1; break; }
        if (pressed < 0) return "no bt.replay cell found in v19_morning_cell.py";

        // ── drive the production per-cell RUN marshaling (4-arg InvokeRunCell): real backend.run_cell
        //    → _build_notebook_bt (real load_replay_data over the NAS) → marimo cold-run sets __file__
        //    from strategy_path → _artifacts self-loads → bt.replay() streams real bars → fills. ──
        string raw = host.InvokeRunCell(source, pressed, scenarioJson, cellPy);
        if (string.IsNullOrEmpty(raw)) return "InvokeRunCell returned null (server not ready / Python error)";

        JObject outObj;
        try { outObj = JObject.Parse(raw); }
        catch (Exception e) { return "run_cell returned unparsable JSON: " + e.Message + " :: " + Trunc(raw, 300); }

        if (!(outObj.Value<bool?>("ok") ?? false))
            return "run_cell ok=false: " + (outObj.Value<string>("error") ?? Trunc(raw, 300));

        // No cell silently FileNotFounds on its artifacts (the RED symptom of the __file__ bug).
        if (outObj["ran"] is JArray ran)
        {
            foreach (var r in ran)
            {
                string o = r.Value<string>("output") ?? "";
                // RED symptom of the __file__ bug: plain-Python marimo → FileNotFoundError (cwd-derived
                // __file__); the Unity embedded kernel → TypeError (Path(None)) because __main__ has no
                // __file__.  Catch both so the message is clear in either environment (the run_summary /
                // fills checks below are the robust backstop regardless).
                if (o.Contains("FileNotFoundError") || o.Contains("No such file") ||
                    (o.Contains("TypeError") && o.Contains("os.PathLike")))
                    return "a cell failed to self-load its artifacts (the __file__ bug is back): " + Trunc(o, 240);
            }
        }

        // It finalized a run summary AND actually traded the real engine over real bars.
        var summary = outObj["run_summary"] as JObject;
        if (summary == null)
            return "run never finalized a run_summary — the cell did not drive the engine (ran=" + Trunc(raw, 240) + ")";
        int fills = summary.Value<int?>("fills_count") ?? summary.Value<int?>("trade_count") ?? 0;
        if (fills <= 0)
            return "the cell loaded its artifacts but produced NO fills (fills_count/trade_count=0) over " +
                   ENTRY_DAY + " — top-k entry/exit did not trade; summary=" + Trunc(summary.ToString(), 300);

        Debug.Log("[E2E V19-REPLAY] real-mount Replay traded: fills_count=" + fills +
                  ", run_summary=" + Trunc(summary.ToString(Newtonsoft.Json.Formatting.None), 300));
        return null;   // PASS
    }

    // os.environ[key]=value via PyString (mirror TachibanaLiveE2ERunner.SetOsEnviron).
    static void SetOsEnviron(string key, string value)
    {
        using (Python.Runtime.Py.GIL())
        using (var os = Python.Runtime.Py.Import("os"))
        using (var environ = os.GetAttr("environ"))
        using (var k = new Python.Runtime.PyString(key))
        using (var v = new Python.Runtime.PyString(value))
            environ.SetItem(k, v);
    }

    static string Trunc(string s, int n) => s == null ? "" : (s.Length <= n ? s : s.Substring(0, n) + "…");
}
