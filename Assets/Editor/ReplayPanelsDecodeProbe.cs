// editor-only throwaway AFK regression gate for Replay panels (#11) M2 —
// headless Mono decode gate for the order / portfolio / run_result side panels.
// ReplayPanelsDecodeProbe.cs
//
// Companion to ReplayChartDecodeProbe (#10 M2, which gates push_bar -> chart).
// This probe gates the PANEL seam: it drives the SAME production replay launcher
// (engine.inproc_server.InprocLiveServer.start_nautilus_replay handed a C#
// ReplayEventSink) but with the TRADING fixture spike_buy_sell.py, so the run
// actually submits a BUY (bar 3 -> fills bar 4) and a SELL (bar 40 -> fills bar
// 41). That makes push_order / push_portfolio / push_run_complete(summary) fire,
// which the no-trade #9/#10 fixture left empty (findings 0003 §3).
//
// WHY VALUE asserts, not count>0 (findings 0003 §3 + #10 POINT-A): JsonUtility
// SILENTLY ZERO-FILLS on a field-name mismatch (no throw), and a CASH-account
// market order that hits a lot/balance reject would silently regress fills to 0.
// So the gate asserts EXPECTED FILL COUNTS structurally (orders==2, one BUY + one
// SELL FILLED, portfolios>=2, run_result.fills_count==2, bars==68 — the same
// expected values as the M0 CPython smoke) AND decodes real VALUES (Side, Qty>0,
// Price>0, an open Position with qty>0/avg_price>0, Equity>0). A count-only gate
// would false-green on a zero-filled DTO; a `>0` gate would false-green if only
// the BUY filled and the SELL was silently dropped.
//
// TWO LAYERS asserted separately (fixture-bug vs panel-bug isolation, §3):
//   * sink layer:  ReplayEventSink.OrdersPushed==2 / PortfoliosPushed>=2 — proves
//                  the fixture really traded (Python side fired the callbacks).
//   * panel layer: drain the JSON queues + ReplayPanelDecoder.Decode* and assert
//                  decoded VALUES — proves the typed model binds the snake_case
//                  keys under Mono (the durable decoder actually works).
//
// LIFECYCLE FSM also gated: ReplayRunLifecycle (the status-panel source of truth)
// is driven Idle->Running->Done off the GENUINE completed sink, plus synthetic
// sinks exercise sticky-terminal, the MarkFailed start-error channel,
// first-terminal-wins, the sink.Failed (push_run_failed) channel, and MarkRunning
// re-arm.
//
//   <UnityEditor> -batchmode -nographics \
//       -projectPath /Users/sasac/backcast \
//       -executeMethod ReplayPanelsDecodeProbe.Run \
//       -logFile <path>
//
// Exit code 0 => PASS, 1 => FAIL. Prints
//   [REPLAY PANELS DECODE PASS] bars=68 ordersPushed=2 ...   or
//   [REPLAY PANELS DECODE FAIL] <named invariant>
//
// THREADING is identical to the proven S1/#10 probe pattern:
//   * main:   Initialize -> BeginAllowThreads (release main GIL, never reacquire)
//             -> launch LAUNCHER thread -> Join (returns fast) -> poll the sink
//             GIL-free until Completed/Failed/timeout -> decode+validate the drain.
//   * launcher: takes Py.GIL(), imports engine, builds cfg with rust_sink = the C#
//             ReplayEventSink, calls start_nautilus_replay (spawns the daemon and
//             RETURNS), checks success + orphan-free daemon, then RELEASES the GIL.
//   * daemon: the Python backtest thread acquires the GIL on its own, streams bars,
//             calls push_bar/push_order/push_portfolio on the C# sink (Enqueue+
//             Interlocked only) and finally push_run_complete, then releases.
//   The main thread MUST stay GIL-free so the daemon can run.
//
// Throwaway gate. cfg window mirrors #10 (same 68-bar 8918.TSE Daily window) but
// the STRATEGY_FILE is the trading fixture. Runtime PATHS come from
// PythonRuntimeLocator. No .meta authored here — Unity generates
// ReplayPanelsDecodeProbe.cs.meta on the next import.

using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class ReplayPanelsDecodeProbe
{
    // cfg fixture: the TRADING fixture (#11). Same 68-bar window as #9/#10, but it
    // submits a BUY at bar 3 and a SELL at bar 40 so the panel seams actually fire.
    const string STRATEGY_FILE = "/Users/sasac/backcast/python/spike/fixtures/strategies/spike_buy_sell.py";
    const string CATALOG_PATH  = "/Users/sasac/backcast/python/spike/fixtures/jquants-catalog";
    const string INSTRUMENT    = "8918.TSE";
    const string START_DATE    = "2024-10-01";
    const string END_DATE      = "2025-01-10";
    const string GRANULARITY   = "Daily";
    const long   INITIAL_CASH  = 10_000_000;

    // Expected structural values (identical to the M0 CPython smoke for this fixture).
    const long EXPECTED_BARS           = 68; // same daily window as #9/#10
    const long EXPECTED_ORDERS         = 2;  // BUY fill + SELL fill (OrderFilled only)
    const long EXPECTED_PORTFOLIOS_MIN = 2;  // PositionOpened -> ... -> Closed (>=2)
    const long EXPECTED_FILLS          = 2;  // run summary fills_count

    // 68 daily bars at 0.1s/bar (~7s) + Mono cold nautilus import; wait generously.
    const double WAIT_TIMEOUT_S = 60.0;

    // launcher -> main: non-null => the launcher failed (only a C# string crosses).
    static string _startError;

    // The C# sink; constructed on main, drained on main, pushed-to by the Python
    // daemon thread under the GIL.
    static ReplayEventSink _sink;

    public static void Run()
    {
        bool passed        = false;
        bool engineStarted = false;

        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();

            PythonEngine.Initialize();
            engineStarted = true;

            // Release the GIL Initialize() holds on main and NEVER reacquire it: the
            // launcher takes it briefly, then the Python daemon backtest thread needs
            // it free to run + call the push_* callbacks. (Return value intentionally
            // discarded — this throwaway gate Exit()s the process, so no EndAllowThreads.)
            PythonEngine.BeginAllowThreads();

            _sink = new ReplayEventSink();

            var launcher = new Thread(Launcher) { IsBackground = true, Name = "ReplayPanelsDecodeLauncher" };
            launcher.Start();
            bool launcherStopped = launcher.Join(60000); // returns fast once the daemon is spawned

            string startErr = Volatile.Read(ref _startError);
            if (!launcherStopped)
            {
                Debug.LogError("[REPLAY PANELS DECODE FAIL] launcher thread did not return within 60s (import/start hung under Mono)");
            }
            else if (startErr != null)
            {
                Debug.LogError("[REPLAY PANELS DECODE FAIL] " + startErr);
            }
            else
            {
                // Poll GIL-free until the daemon signals completion or failure.
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (!_sink.Completed && !_sink.Failed && sw.Elapsed.TotalSeconds < WAIT_TIMEOUT_S)
                {
                    Thread.Sleep(50);
                }

                if (_sink.Failed)
                {
                    Debug.LogError("[REPLAY PANELS DECODE FAIL] push_run_failed: " + _sink.Error);
                }
                else if (!_sink.Completed)
                {
                    Debug.LogError($"[REPLAY PANELS DECODE FAIL] timed out after {WAIT_TIMEOUT_S:0}s " +
                                   $"waiting for run completion (bars pushed so far={_sink.Pushed})");
                }
                else
                {
                    // push_run_complete is the LAST callback, so once Completed the full
                    // bar/order/portfolio stream is enqueued and Summary is set — safe to
                    // observe + drain on main, GIL-free.
                    string fail = ValidateAll();
                    if (fail != null)
                    {
                        Debug.LogError("[REPLAY PANELS DECODE FAIL] " + fail);
                    }
                    else
                    {
                        passed = true;
                        Debug.Log($"[REPLAY PANELS DECODE PASS] bars={EXPECTED_BARS} " +
                                  $"ordersPushed={_sink.OrdersPushed} portfoliosPushed={_sink.PortfoliosPushed} " +
                                  $"fills={EXPECTED_FILLS} orders=[1 BUY + 1 SELL FILLED] " +
                                  "lifecycle=Idle->Running->Done " +
                                  "(sink-layer + panel-layer Decode* + ReplayRunLifecycle FSM all GREEN under Unity Mono)");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[REPLAY PANELS DECODE FAIL] driver: " + e);
        }
        finally
        {
            // The Python daemon backtest thread's lifecycle is nondeterministic
            // relative to here; reacquiring the GIL to Shutdown could deadlock against
            // it. This throwaway gate Exit()s immediately (OS reclaims the interpreter),
            // so skip EndAllowThreads/Shutdown — mirrors the S1/#10 probes.
            if (engineStarted)
            {
                Debug.Log("[REPLAY PANELS DECODE] skipping Python shutdown (daemon lifecycle nondeterministic; process exits next)");
            }
        }

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // Runs every gate, returns the first failure string (or null on full PASS).
    // Order is deliberate: FSM first (pure, no drain), then SINK-LAYER counts
    // (producer counters, read before draining), then PANEL-LAYER decode (drains
    // the queues). Sink-layer failing first isolates "fixture didn't trade" from
    // "panel didn't decode".
    static string ValidateAll()
    {
        // --- lifecycle FSM (uses the genuine completed sink for the Done edge) ---
        string lc = ValidateLifecycle(_sink);
        if (lc != null) return "lifecycle FSM: " + lc;

        // --- sink layer: did the fixture actually trade? (producer counters) ---
        long ordersPushed     = _sink.OrdersPushed;
        long portfoliosPushed = _sink.PortfoliosPushed;
        long barsPushed       = _sink.Pushed;

        if (ordersPushed != EXPECTED_ORDERS)
            return $"sink layer: OrdersPushed={ordersPushed} != expected {EXPECTED_ORDERS} " +
                   "(fixture did not fill exactly 1 BUY + 1 SELL — lot/balance reject or schedule regression)";
        if (portfoliosPushed < EXPECTED_PORTFOLIOS_MIN)
            return $"sink layer: PortfoliosPushed={portfoliosPushed} < expected >= {EXPECTED_PORTFOLIOS_MIN} " +
                   "(no PositionOpened/Changed/Closed sequence)";
        if (barsPushed != EXPECTED_BARS)
            return $"sink layer: bars pushed={barsPushed} != expected {EXPECTED_BARS} (fixture window changed?)";

        // bars drain just confirms the same window streamed (chart VALUE decode is
        // #10's probe; here we only need the count to match the producer counter).
        long barsDrained = 0;
        while (_sink.TryDequeueBar(out _)) barsDrained++;
        if (barsDrained != barsPushed)
            return $"sink layer: barsDrained={barsDrained} != barsPushed={barsPushed}";

        // --- panel layer: decode the JSON to real typed VALUES ---
        string of = ValidateOrders();
        if (of != null) return "panel layer orders: " + of;

        string pf = ValidatePortfolios();
        if (pf != null) return "panel layer portfolios: " + pf;

        string rf = ValidateRunResult(_sink.Summary);
        if (rf != null) return "panel layer run_result: " + rf;

        return null;
    }

    // Drains the order queue, ReplayPanelDecoder.DecodeOrder each, and asserts real
    // values. DecodeOrder throws on MALFORMED json by contract — left uncaught so the
    // outer try surfaces it as a real bug, not green. Catches JsonUtility zero-fill
    // (Side null/empty, Qty/Price 0) and a silently-dropped SELL (side mix != 1+1).
    static string ValidateOrders()
    {
        long decoded = 0;
        int buys = 0, sells = 0;
        while (_sink.TryDequeueOrder(out string payload))
        {
            decoded++;
            OrderRow row = ReplayPanelDecoder.DecodeOrder(payload);
            if (string.IsNullOrEmpty(row.Side))
                return $"order#{decoded}: Side null/empty (zero-fill / field-name mismatch)";
            if (row.Side != "BUY" && row.Side != "SELL")
                return $"order#{decoded}: Side='{row.Side}' not BUY/SELL";
            if (row.Status != "FILLED")
                return $"order#{decoded}: Status='{row.Status}' not FILLED (orders are OrderFilled only)";
            if (row.Qty <= 0)
                return $"order#{decoded}: Qty={row.Qty} not > 0 (zero-fill?)";
            if (row.Price <= 0)
                return $"order#{decoded}: Price={row.Price} not > 0 (zero-fill?)";
            if (string.IsNullOrEmpty(row.Symbol))
                return $"order#{decoded}: Symbol empty (zero-fill)";
            if (row.Side == "BUY") buys++; else sells++;
        }
        if (decoded != EXPECTED_ORDERS)
            return $"decoded order rows={decoded} != expected {EXPECTED_ORDERS}";
        if (buys != 1 || sells != 1)
            return $"order side mix buys={buys} sells={sells}, expected exactly 1 BUY + 1 SELL";
        return null;
    }

    // Drains the portfolio queue, ReplayPanelDecoder.DecodePortfolio each, and asserts
    // at least EXPECTED_PORTFOLIOS_MIN snapshots, each with Equity>0 (zero-fill guard),
    // and at least one snapshot carrying a real OPEN position (qty>0, symbol set,
    // avg_price>0). PositionRow is the snake_case array-element DTO that JsonUtility
    // binds directly — an open position with qty>0 proves the nested array bound.
    static string ValidatePortfolios()
    {
        long decoded = 0;
        bool sawOpenPosition = false;
        while (_sink.TryDequeuePortfolio(out string payload))
        {
            decoded++;
            PortfolioSnapshot snap = ReplayPanelDecoder.DecodePortfolio(payload);
            if (snap.Equity <= 0)
                return $"portfolio#{decoded}: Equity={snap.Equity} not > 0 (zero-fill?)";
            if (snap.Positions == null)
                return $"portfolio#{decoded}: Positions null";
            for (int i = 0; i < snap.Positions.Count; i++)
            {
                PositionRow p = snap.Positions[i];
                if (p.qty > 0)
                {
                    if (string.IsNullOrEmpty(p.symbol))
                        return $"portfolio#{decoded}: open position symbol empty (zero-fill)";
                    if (p.avg_price <= 0)
                        return $"portfolio#{decoded}: open position avg_price={p.avg_price} not > 0 (zero-fill?)";
                    sawOpenPosition = true;
                }
            }
        }
        if (decoded < EXPECTED_PORTFOLIOS_MIN)
            return $"decoded portfolio snapshots={decoded} < expected >= {EXPECTED_PORTFOLIOS_MIN}";
        if (!sawOpenPosition)
            return "no portfolio snapshot carried an open position (qty>0 with symbol+avg_price>0) — zero-fill or no real position";
        return null;
    }

    // ReplayPanelDecoder.DecodeRunResult on push_run_complete's summary. fills_count
    // binding to 2 proves the whole RunResultDto bound (a zero-fill gives 0).
    // equity_points>0 is a cheap second-field zero-fill guard.
    static string ValidateRunResult(string summary)
    {
        RunResult rr = ReplayPanelDecoder.DecodeRunResult(summary);
        if (rr.FillsCount != EXPECTED_FILLS)
            return $"RunResult.FillsCount={rr.FillsCount} != expected {EXPECTED_FILLS} " +
                   "(silent zero-fill summary, or a fill was dropped)";
        if (rr.EquityPoints <= 0)
            return $"RunResult.EquityPoints={rr.EquityPoints} not > 0 (zero-fill?)";
        return null;
    }

    // Drives ReplayRunLifecycle (the status-panel source of truth) through every edge.
    // The Done edge uses the GENUINE completed sink so this proves the real run drives
    // Idle->Running->Done; the Failed edges use synthetic sinks (push_run_failed is a
    // public sink method) so the daemon need not actually fail. Returns null on PASS.
    static string ValidateLifecycle(ReplayEventSink completedSink)
    {
        // (a) real run: Idle -> Running -> Done off the genuine completed sink
        var lc = new ReplayRunLifecycle();
        if (lc.Status != RunStatus.Idle)
            return $"initial Status={lc.Status} != Idle";
        lc.MarkRunning();
        if (lc.Status != RunStatus.Running)
            return $"after MarkRunning Status={lc.Status} != Running";
        lc.Observe(completedSink); // completedSink.Completed == true
        if (lc.Status != RunStatus.Done)
            return $"after Observe(completed sink) Status={lc.Status} != Done";

        // sticky-terminal: further Observe / MarkFailed are no-ops once Done
        lc.Observe(completedSink);
        lc.MarkFailed("late-error");
        if (lc.Status != RunStatus.Done)
            return $"sticky-terminal violated: Done regressed to {lc.Status}";
        if (lc.FailureReason != null)
            return $"Done carried a FailureReason ({lc.FailureReason})";

        // (b) MarkFailed start-error channel + first-terminal-wins
        var lc2 = new ReplayRunLifecycle();
        lc2.MarkRunning();
        lc2.MarkFailed("start-error");
        if (lc2.Status != RunStatus.Failed)
            return $"after MarkFailed Status={lc2.Status} != Failed";
        if (lc2.FailureReason != "start-error")
            return $"MarkFailed FailureReason={lc2.FailureReason} != 'start-error'";
        lc2.MarkFailed("second");
        if (lc2.FailureReason != "start-error")
            return "first-terminal-wins violated: a second MarkFailed overwrote FailureReason";

        // (c) sink.Failed channel via a synthetic failed sink (push_run_failed)
        var failedSink = new ReplayEventSink();
        failedSink.push_run_failed("daemon-fail");
        var lc3 = new ReplayRunLifecycle();
        lc3.MarkRunning();
        lc3.Observe(failedSink);
        if (lc3.Status != RunStatus.Failed)
            return $"after Observe(failed sink) Status={lc3.Status} != Failed";
        if (lc3.FailureReason != "daemon-fail")
            return $"Observe(failed) FailureReason={lc3.FailureReason} != 'daemon-fail'";

        // (d) re-arm: MarkRunning resets a terminal lifecycle to Running, clears reason
        lc3.MarkRunning();
        if (lc3.Status != RunStatus.Running)
            return $"after re-arm MarkRunning Status={lc3.Status} != Running";
        if (lc3.FailureReason != null)
            return $"re-arm did not clear FailureReason ({lc3.FailureReason})";

        return null;
    }

    // Launcher thread: takes the GIL, drives the production replay seam exactly like
    // python/spike/s1_adapter_smoke.py (DataEngine + InprocLiveServer.start_nautilus_replay)
    // but with the TRADING fixture, verifies the worker is a daemon (orphan-free,
    // ADR-0001 d3 / S1 #9 AC), then RELEASES the GIL by exiting the using block so the
    // daemon runs.
    static void Launcher()
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

                using (PyObject coreMod   = Py.Import("engine.core"))
                using (PyObject inprocMod  = Py.Import("engine.inproc_server"))
                using (PyObject dataEngCls = coreMod.GetAttr("DataEngine"))
                using (PyObject inprocCls  = inprocMod.GetAttr("InprocLiveServer"))
                using (PyObject dataEngine = dataEngCls.Invoke())
                using (PyObject server     = inprocCls.Invoke(dataEngine))
                using (PyObject sinkPy     = PyObject.FromManagedObject(_sink))
                using (PyList instruments  = new PyList())
                using (PyDict cfg          = new PyDict())
                {
                    instruments.Append(new PyString(INSTRUMENT));

                    cfg.SetItem("strategy_file", new PyString(STRATEGY_FILE));
                    cfg.SetItem("instruments",   instruments);
                    cfg.SetItem("start_date",    new PyString(START_DATE));
                    cfg.SetItem("end_date",      new PyString(END_DATE));
                    cfg.SetItem("granularity",   new PyString(GRANULARITY));
                    cfg.SetItem("initial_cash",  new PyInt(INITIAL_CASH));
                    cfg.SetItem("catalog_path",  new PyString(CATALOG_PATH));
                    cfg.SetItem("rust_sink",     sinkPy);

                    using (PyObject result  = server.InvokeMethod("start_nautilus_replay", cfg))
                    using (PyObject success = result["success"])
                    {
                        if (!success.As<bool>())
                        {
                            using (PyObject ec = result["error_code"])
                            using (PyObject em = result["error_message"])
                            {
                                Volatile.Write(ref _startError,
                                    $"start_nautilus_replay rejected: error_code={ec.As<string>()} " +
                                    $"error_message={em.As<string>()}");
                            }
                        }
                    }

                    // Orphan-free gate (S1 #9 AC; ADR-0001 d3): the Replay worker spawned
                    // by start_nautilus_replay MUST be a Python *daemon* thread so it can
                    // never block process exit when Unity quits. Checked here, GIL held,
                    // while the worker is freshly started and alive.
                    if (Volatile.Read(ref _startError) == null)
                    {
                        using (PyObject threadingMod = Py.Import("threading"))
                        using (PyObject enumResult    = threadingMod.InvokeMethod("enumerate"))
                        using (PyList   threads       = new PyList(enumResult))
                        {
                            bool   runnerFound  = false;
                            bool   runnerDaemon = false;
                            var    roster       = new System.Text.StringBuilder();
                            foreach (PyObject th in threads)
                            {
                                using (PyObject nameObj   = th.GetAttr("name"))
                                using (PyObject daemonObj = th.GetAttr("daemon"))
                                {
                                    string name   = nameObj.As<string>();
                                    bool   daemon = daemonObj.As<bool>();
                                    if (roster.Length > 0) roster.Append(", ");
                                    roster.Append(name).Append(daemon ? "(daemon)" : "(NON-daemon)");
                                    if (name == "backtest-runner")
                                    {
                                        runnerFound  = true;
                                        runnerDaemon = daemon;
                                    }
                                }
                                th.Dispose();
                            }

                            if (!runnerFound)
                            {
                                Volatile.Write(ref _startError,
                                    "orphan-free check: 'backtest-runner' thread not found after start " +
                                    "(threads=[" + roster + "])");
                            }
                            else if (!runnerDaemon)
                            {
                                Volatile.Write(ref _startError,
                                    "orphan-free check: 'backtest-runner' is NOT a daemon thread — it would " +
                                    "block Unity process exit (orphan/leftover-thread risk). threads=[" + roster + "]");
                            }
                            else
                            {
                                Debug.Log("[REPLAY PANELS DECODE] orphan-free OK: 'backtest-runner' is a daemon thread " +
                                          "(dies with the process; cannot block Unity exit). threads=[" + roster + "]");
                            }
                        }
                    }
                }
            } // GIL released here -> the Python daemon backtest thread can now run.
        }
        catch (Exception e)
        {
            Volatile.Write(ref _startError, "launcher: " + e);
        }
    }
}
