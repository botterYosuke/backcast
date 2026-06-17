// editor-only throwaway probe for AC#4 — C# decoder reads the kernel sink (#24)
// KernelSinkDecodeProbe.cs — ADR-0004 案 C / docs/findings/0008 §7b
//
// AC#4: the Backcast Execution Kernel's EventSink JSON is read by the EXISTING
// C# decoders WITHOUT modification ("C# 側 decoder 無改修で読める"). This is the
// kernel analogue of ReplayPanelsDecodeProbe (#11), which proves the same for the
// Nautilus replay path. The kernel's push_target duck-types RustBacktestSink, so we
// hand it the SAME C# ReplayEventSink and decode its queues with the unmodified
// ReplayBarDecoder (push_bar -> chart) and ReplayPanelDecoder (push_order /
// push_portfolio / push_run_complete -> panels).
//
//   <UnityEditor> -batchmode -nographics -quit \
//       -projectPath C:\Users\sasai\Documents\backcast \
//       -executeMethod KernelSinkDecodeProbe.Run
//
// Exit 0 => PASS, 1 => FAIL. VALUE asserts (not count>0): JsonUtility SILENTLY
// zero-fills on a field-name mismatch, so a key drift would pass a count gate but
// fail Side/Qty/Price/Equity > 0. The kernel run is SYNCHRONOUS (run_into pushes all
// 73 events then returns), so — unlike the Nautilus daemon path — the probe just
// joins the worker and drains. Throwaway: lives under Assets/Editor/.

using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class KernelSinkDecodeProbe
{
    static readonly ReplayEventSink _sink = new ReplayEventSink();
    static string _error;

    const long EXPECTED_BARS            = 68;
    const long EXPECTED_ORDERS          = 2;   // BUY fill + SELL fill
    const long EXPECTED_PORTFOLIOS_MIN  = 2;   // open + close
    const long EXPECTED_FILLS           = 2;

    public static void Run()
    {
        bool   passed        = false;
        IntPtr ts            = IntPtr.Zero;
        bool   engineStarted = false;
        bool   workerStopped = true;

        try
        {
            // #65 decoder-binding round-trip FIRST: pure C# (JsonUtility), no Python/DuckDB — so it
            // runs in any environment, independent of the kernel run below (which needs the S:\ Daily
            // catalog). Locks the field-name binding of the get_portfolio_json / union summary_json
            // fields #65 added (silent zero-fill on a name mismatch is the failure this catches).
            string shapeErr = ValidateReplayPollShape();
            if (shapeErr != null)
            {
                Debug.LogError("[REPLAY POLL SHAPE FAIL] " + shapeErr);
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("[REPLAY POLL SHAPE PASS] get_portfolio_json orders/realized/unrealized + summary total_pnl bind");

            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            engineStarted = true;
            Debug.Log("[KERNEL SINK DECODE MARK] PythonEngine.Initialize OK");

            ts = PythonEngine.BeginAllowThreads();
            var worker = new Thread(Worker) { IsBackground = true, Name = "KernelSinkDecodeWorker" };
            worker.Start();
            workerStopped = worker.Join(120000);

            string err = Volatile.Read(ref _error);
            if (!workerStopped)
            {
                Debug.LogError("[KERNEL SINK DECODE FAIL] worker timeout (did not finish within 120s)");
            }
            else if (err != null)
            {
                Debug.LogError("[KERNEL SINK DECODE FAIL] " + err);
            }
            else
            {
                string v = Validate();   // drains queues + decodes via the real decoders
                if (v != null)
                {
                    Debug.LogError("[KERNEL SINK DECODE FAIL] " + v);
                }
                else
                {
                    passed = true;
                    Debug.Log($"[KERNEL SINK DECODE PASS] bars={EXPECTED_BARS} ordersPushed={_sink.OrdersPushed} " +
                              $"portfoliosPushed={_sink.PortfoliosPushed} fills={EXPECTED_FILLS} " +
                              "(unmodified ReplayBarDecoder/ReplayPanelDecoder read the kernel sink under Unity Mono)");
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[KERNEL SINK DECODE FAIL] driver: " + e);
        }
        finally
        {
            try
            {
                if (engineStarted && workerStopped)
                {
                    if (ts != IntPtr.Zero)
                    {
                        PythonEngine.EndAllowThreads(ts);
                    }
                    PythonEngine.Shutdown();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[KERNEL SINK DECODE] shutdown cleanup: " + e);
            }
        }

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // Worker: takes the GIL, runs the kernel tracer into the C# sink (synchronous —
    // all push_* land in the sink queues before run_into returns).
    static void Worker()
    {
        try
        {
            using (Py.GIL())
            {
                using (PyObject sys = Py.Import("sys"))
                using (PyObject sysPath = sys.GetAttr("path"))
                {
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
                }
                Debug.Log("[KERNEL SINK DECODE MARK] running kernel into C# ReplayEventSink (NO nautilus)");
                using (PyObject m = Py.Import("spike.kernel_golden.run_kernel"))
                using (PyObject sinkPy = PyObject.FromManagedObject(_sink))
                {
                    m.InvokeMethod("run_into", sinkPy).Dispose();
                }
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _error, e.ToString());
        }
    }

    // Drains the C# sink queues and decodes each payload with the UNMODIFIED durable
    // decoders, asserting real VALUES. Returns null on success or a named invariant.
    static string Validate()
    {
        // sink layer — the kernel really pushed the contract
        if (!_sink.Completed)
            return "sink layer: run did not complete (push_run_complete never fired)";
        if (_sink.OrdersPushed != EXPECTED_ORDERS)
            return $"sink layer: OrdersPushed={_sink.OrdersPushed} != {EXPECTED_ORDERS}";
        if (_sink.PortfoliosPushed < EXPECTED_PORTFOLIOS_MIN)
            return $"sink layer: PortfoliosPushed={_sink.PortfoliosPushed} < {EXPECTED_PORTFOLIOS_MIN}";
        if (_sink.Pushed != EXPECTED_BARS)
            return $"sink layer: bars pushed={_sink.Pushed} != {EXPECTED_BARS}";

        // push_bar -> ReplayBarDecoder.Decode (chart decoder): value-assert the last bar
        long barsDrained = 0;
        ReplayBarFrame lastBar = default;
        while (_sink.TryDequeueBar(out string bjson))
        {
            lastBar = ReplayBarDecoder.Decode(bjson);
            barsDrained++;
        }
        if (barsDrained != EXPECTED_BARS)
            return $"bars drained={barsDrained} != {EXPECTED_BARS}";
        if (lastBar.Price <= 0)
            return $"ReplayBarDecoder: last bar Price={lastBar.Price} not > 0 (zero-fill / key mismatch)";
        if (lastBar.Ohlc == null || lastBar.Ohlc.Count == 0)
            return "ReplayBarDecoder: Ohlc empty (key mismatch)";

        // push_order -> ReplayPanelDecoder.DecodeOrder
        long buys = 0, sells = 0, ordersDecoded = 0;
        while (_sink.TryDequeueOrder(out string ojson))
        {
            OrderRow row = ReplayPanelDecoder.DecodeOrder(ojson);
            if (string.IsNullOrEmpty(row.Side))
                return $"order#{ordersDecoded}: Side null/empty (zero-fill / key mismatch)";
            if (row.Side != "BUY" && row.Side != "SELL")
                return $"order#{ordersDecoded}: Side='{row.Side}' not BUY/SELL";
            if (row.Status != "FILLED")
                return $"order#{ordersDecoded}: Status='{row.Status}' != FILLED";
            if (row.Qty <= 0)
                return $"order#{ordersDecoded}: Qty={row.Qty} not > 0 (zero-fill?)";
            if (row.Price <= 0)
                return $"order#{ordersDecoded}: Price={row.Price} not > 0 (zero-fill?)";
            if (row.Side == "BUY") buys++; else sells++;
            ordersDecoded++;
        }
        if (buys != 1 || sells != 1)
            return $"orders: buys={buys} sells={sells} != 1+1 (a fill was silently dropped)";

        // push_portfolio -> ReplayPanelDecoder.DecodePortfolio
        long portfoliosDecoded = 0;
        bool sawOpenPosition = false;
        while (_sink.TryDequeuePortfolio(out string pjson))
        {
            PortfolioSnapshot snap = ReplayPanelDecoder.DecodePortfolio(pjson);
            if (snap.Equity <= 0)
                return $"portfolio#{portfoliosDecoded}: Equity={snap.Equity} not > 0 (zero-fill?)";
            if (snap.Positions != null)
            {
                foreach (PositionRow pos in snap.Positions)
                {
                    if (pos.qty > 0 && pos.avg_price > 0 && !string.IsNullOrEmpty(pos.symbol))
                        sawOpenPosition = true;
                }
            }
            portfoliosDecoded++;
        }
        if (portfoliosDecoded < EXPECTED_PORTFOLIOS_MIN)
            return $"portfolios decoded={portfoliosDecoded} < {EXPECTED_PORTFOLIOS_MIN}";
        if (!sawOpenPosition)
            return "portfolios: no open Position (qty>0 / avg_price>0) decoded (zero-fill?)";

        // push_run_complete(summary) -> ReplayPanelDecoder.DecodeRunResult
        RunResult rr = ReplayPanelDecoder.DecodeRunResult(_sink.Summary);
        if (rr.FillsCount != EXPECTED_FILLS)
            return $"run_result: FillsCount={rr.FillsCount} != {EXPECTED_FILLS}";
        if (rr.EquityPoints != EXPECTED_BARS)
            return $"run_result: EquityPoints={rr.EquityPoints} != {EXPECTED_BARS}";

        // (the get_portfolio_json / union summary_json decoder-binding round-trip runs FIRST in Run(),
        // data-independent — see ValidateReplayPollShape.)
        return null;
    }

    // #65 decoder-binding round-trip for the get_portfolio_json / union summary_json shapes. The
    // literals mirror python/tests/test_get_portfolio_json._RUNNING_SNAPSHOT and the _finalize_run
    // union {fills_count, equity_points, total_pnl, max_drawdown, sharpe, sortino} — Python pinning of
    // those exact shapes lives in the pytest; this is the C# half (does the decoder bind the keys).
    static string ValidateReplayPollShape()
    {
        // positions[].unrealized_pnl is given a NON-ZERO value here (production finalize hardcodes 0.0)
        // purely so a binding failure on that newly-bound field is detectable, not zero-fill-masked.
        const string portfolioJson =
            "{\"buying_power\":900000.0,\"cash\":900000.0,\"equity\":1005000.0," +
            "\"positions\":[{\"symbol\":\"8918.TSE\",\"qty\":100,\"avg_price\":1000.0,\"unrealized_pnl\":5000.0}]," +
            "\"orders\":[{\"symbol\":\"8918.TSE\",\"side\":\"BUY\",\"qty\":100.0,\"price\":1000.0,\"status\":\"FILLED\",\"ts_ms\":1700000000000}]," +
            "\"realized_pnl\":0.0,\"unrealized_pnl\":5000.0}";

        PortfolioSnapshot snap = ReplayPanelDecoder.DecodePortfolio(portfolioJson);
        if (snap.Orders == null || snap.Orders.Count != 1)
            return $"get_portfolio_json: Orders count={snap.Orders?.Count ?? -1} != 1 (orders not bound)";
        PortfolioOrderRow o = snap.Orders[0];
        if (o.status != "FILLED")
            return $"get_portfolio_json: order.status='{o.status}' != FILLED (zero-fill / key mismatch)";
        if (o.side != "BUY" || o.qty <= 0 || o.price <= 0)
            return $"get_portfolio_json: order side/qty/price not bound (side='{o.side}' qty={o.qty} price={o.price})";
        if (snap.UnrealizedPnl <= 0)
            return $"get_portfolio_json: UnrealizedPnl={snap.UnrealizedPnl} not bound (running-view pnl zero-fill)";
        if (snap.Positions == null || snap.Positions.Count != 1 || snap.Positions[0].unrealized_pnl <= 0)
            return "get_portfolio_json: PositionRow.unrealized_pnl not bound (was silently zero-filled pre-#65)";

        // union summary_json: total_pnl is the #65-added field DecodeRunResult must now bind.
        const string summaryJson =
            "{\"fills_count\":2,\"equity_points\":68,\"total_pnl\":-410010.0," +
            "\"max_drawdown\":1234.0,\"sharpe\":0.5,\"sortino\":0.7}";
        RunResult ur = ReplayPanelDecoder.DecodeRunResult(summaryJson);
        if (ur.TotalPnl >= 0.0)
            return $"summary_json: TotalPnl={ur.TotalPnl} not bound (expected -410010; total_pnl zero-fill)";
        if (ur.Sharpe <= 0.0 || ur.Sortino <= 0.0)
            return $"summary_json: sharpe/sortino not bound (sharpe={ur.Sharpe} sortino={ur.Sortino})";

        return null;
    }
}
