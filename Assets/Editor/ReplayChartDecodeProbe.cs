// editor-only throwaway probe for Replay chart (#10) M2 — headless Mono decode gate.
// ReplayChartDecodeProbe.cs
//
// Builds on the S1 (#9) reverse-callback proof: a C# ReplayEventSink is handed to
// the production Python replay seam (engine.inproc_server.InprocLiveServer.
// start_nautilus_replay), the Nautilus backtest streams 68 bars on a Python daemon
// thread under Unity's Mono runtime and duck-calls sink.push_bar(str) per bar.
//
// The DIFFERENCE from S1AdapterSmokeProbe: instead of S1's LooksLikeBarJson
// string-contains proxy, M2 calls the DURABLE typed decoder ReplayBarDecoder.Decode
// on every drained payload and asserts POINT-A value invariants. This matters
// because JsonUtility SILENTLY ZERO-FILLS on a field-name mismatch (no throw), so a
// count-only gate is FALSE-GREEN — only asserting decoded VALUES proves the typed
// model actually binds the snake_case JSON keys under Mono.
//
//   <UnityEditor> -batchmode -nographics \
//       -projectPath /Users/sasac/backcast \
//       -executeMethod ReplayChartDecodeProbe.Run \
//       -logFile <path>
//
// Exit code 0 => PASS, 1 => FAIL. Prints
//   [REPLAY CHART DECODE PASS] decoded=68 ...   or
//   [REPLAY CHART DECODE FAIL] <named invariant>
//
// THREADING is identical to the S1 probe (the proven pattern):
//   * main:   Initialize -> BeginAllowThreads (release main GIL, never reacquire)
//             -> launch LAUNCHER thread -> Join (returns fast) -> poll the sink
//             GIL-free until Completed/Failed/timeout -> decode+validate the drain.
//   * launcher: takes Py.GIL(), imports engine, builds cfg with rust_sink = the C#
//             ReplayEventSink, calls start_nautilus_replay (spawns the daemon and
//             RETURNS), checks success + orphan-free daemon, then RELEASES the GIL.
//   * daemon: the Python backtest thread acquires the GIL on its own, streams bars,
//             calls push_bar on the C# sink (Enqueue+Interlocked only), releases.
//   The main thread MUST stay GIL-free so the daemon can run.
//
// Throwaway gate. cfg/fixture consts mirror S1 (same 68-bar fixture); runtime PATHS
// come from PythonRuntimeLocator. No .meta authored here — Unity generates
// ReplayChartDecodeProbe.cs.meta on the next import.

using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class ReplayChartDecodeProbe
{
    // cfg fixture (SAME 68-bar fixture as S1 / python/spike/s1_adapter_smoke.py).
    const string STRATEGY_FILE = "/Users/sasac/backcast/python/spike/fixtures/strategies/spike_bar_consumer.py";
    const string CATALOG_PATH  = "/Users/sasac/backcast/python/spike/fixtures/jquants-catalog";
    const string INSTRUMENT    = "8918.TSE";
    const string START_DATE    = "2024-10-01";
    const string END_DATE      = "2025-01-10";
    const string GRANULARITY   = "Daily";
    const long   INITIAL_CASH  = 10_000_000;

    // The whole replay is the same 68 daily bars S1 confirmed.
    const long   EXPECTED_BARS = 68;

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
            // it free to run + call push_bar. (Return value intentionally discarded —
            // this throwaway gate Exit()s the process, so no EndAllowThreads.)
            PythonEngine.BeginAllowThreads();

            _sink = new ReplayEventSink();

            var launcher = new Thread(Launcher) { IsBackground = true, Name = "ReplayChartDecodeLauncher" };
            launcher.Start();
            bool launcherStopped = launcher.Join(60000); // returns fast once the daemon is spawned

            string startErr = Volatile.Read(ref _startError);
            if (!launcherStopped)
            {
                Debug.LogError("[REPLAY CHART DECODE FAIL] launcher thread did not return within 60s (import/start hung under Mono)");
            }
            else if (startErr != null)
            {
                Debug.LogError("[REPLAY CHART DECODE FAIL] " + startErr);
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
                    Debug.LogError("[REPLAY CHART DECODE FAIL] push_run_failed: " + _sink.Error);
                }
                else if (!_sink.Completed)
                {
                    Debug.LogError($"[REPLAY CHART DECODE FAIL] timed out after {WAIT_TIMEOUT_S:0}s " +
                                   $"waiting for run completion (pushed so far={_sink.Pushed})");
                }
                else
                {
                    // push_run_complete is the LAST callback, so once Completed the
                    // full bar stream is enqueued — safe to drain on main, GIL-free.
                    long pushed   = _sink.Pushed;
                    long decoded  = 0;
                    string firstFail = null;
                    long prevFrameLastOpenTime = long.MinValue; // cross-frame monotonic
                    ReplayBarFrame finalFrame = default;

                    while (_sink.TryDequeueBar(out string payload))
                    {
                        decoded++;
                        // Decode throws on MALFORMED json by contract — left uncaught
                        // here so the outer try surfaces it as a real bug, not green.
                        ReplayBarFrame frame = ReplayBarDecoder.Decode(payload);
                        finalFrame = frame;

                        if (firstFail == null)
                        {
                            string f = ValidatePerFrame(frame, ref prevFrameLastOpenTime);
                            if (f != null) firstFail = $"frame#{decoded}: {f}";
                        }
                    }

                    if (firstFail != null)
                    {
                        Debug.LogError("[REPLAY CHART DECODE FAIL] per-frame invariant: " + firstFail);
                    }
                    else if (pushed <= 0)
                    {
                        Debug.LogError("[REPLAY CHART DECODE FAIL] no bars pushed (pushed=0) — replay produced no push_bar callbacks");
                    }
                    else if (pushed != decoded)
                    {
                        Debug.LogError($"[REPLAY CHART DECODE FAIL] pushed={pushed} != decoded={decoded}");
                    }
                    else if (decoded != EXPECTED_BARS)
                    {
                        Debug.LogError($"[REPLAY CHART DECODE FAIL] decoded={decoded} != expected {EXPECTED_BARS} (fixture changed?)");
                    }
                    else
                    {
                        string deep = ValidateFinalSeries(finalFrame, decoded);
                        if (deep != null)
                        {
                            Debug.LogError("[REPLAY CHART DECODE FAIL] final-series invariant: " + deep);
                        }
                        else
                        {
                            passed = true;
                            Debug.Log($"[REPLAY CHART DECODE PASS] decoded={decoded} " +
                                      $"finalOhlcPoints={finalFrame.Ohlc.Count} " +
                                      "(ReplayBarDecoder.Decode bound real OHLC values under Unity Mono)");
                        }
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[REPLAY CHART DECODE FAIL] driver: " + e);
        }
        finally
        {
            // The Python daemon backtest thread's lifecycle is nondeterministic
            // relative to here; reacquiring the GIL to Shutdown could deadlock
            // against it. This throwaway gate Exit()s immediately (OS reclaims the
            // interpreter), so skip EndAllowThreads/Shutdown — mirrors the S1 probe.
            if (engineStarted)
            {
                Debug.Log("[REPLAY CHART DECODE] skipping Python shutdown (daemon lifecycle nondeterministic; process exits next)");
            }
        }

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // Per-frame value gate: every drained frame must decode to a non-empty cumulative
    // series whose LAST point holds real (non-zero-filled) values. A JsonUtility
    // field-name mismatch zero-fills every field, so this fails on frame#1 (close<=0)
    // instead of false-greening. Also checks the frame's last open_time_ms advances
    // across frames (catches scrambled frame ordering). Returns null if OK.
    static string ValidatePerFrame(ReplayBarFrame frame, ref long prevFrameLastOpenTime)
    {
        var pts = frame.Ohlc;
        if (pts == null || pts.Count == 0)
            return "Ohlc empty (missing ohlc_points or zero-filled DTO)";

        OhlcPoint last = pts[pts.Count - 1];
        if (last.close <= 0)
            return $"last point close={last.close} not > 0 (likely zero-fill / field-name mismatch)";
        if (last.open_time_ms <= 0)
            return $"last point open_time_ms={last.open_time_ms} not > 0";
        if (last.high < Math.Max(last.open, last.close))
            return $"last point high={last.high} < max(open={last.open},close={last.close})";
        if (last.low > Math.Min(last.open, last.close))
            return $"last point low={last.low} > min(open={last.open},close={last.close})";
        if (last.open_time_ms < prevFrameLastOpenTime)
            return $"cross-frame open_time_ms regressed {prevFrameLastOpenTime} -> {last.open_time_ms}";

        prevFrameLastOpenTime = last.open_time_ms;
        return null;
    }

    // Deep gate on the FINAL cumulative frame: it carries the full series, so its
    // point count must equal the bar count, every point must hold real positive OHLC
    // values with high/low bounding open/close, and open_time_ms must be monotonic
    // non-decreasing across the whole series. Returns null if OK.
    static string ValidateFinalSeries(ReplayBarFrame frame, long expectedPoints)
    {
        var pts = frame.Ohlc;
        if (pts.Count != expectedPoints)
            return $"final frame ohlc_points={pts.Count} != {expectedPoints} (cumulative series should hold every bar)";

        long prev = long.MinValue;
        for (int i = 0; i < pts.Count; i++)
        {
            OhlcPoint p = pts[i];
            if (p.open_time_ms <= 0) return $"point[{i}] open_time_ms={p.open_time_ms} not > 0";
            if (p.open  <= 0)        return $"point[{i}] open={p.open} not > 0";
            if (p.high  <= 0)        return $"point[{i}] high={p.high} not > 0";
            if (p.low   <= 0)        return $"point[{i}] low={p.low} not > 0";
            if (p.close <= 0)        return $"point[{i}] close={p.close} not > 0";
            if (p.high < Math.Max(p.open, p.close))
                return $"point[{i}] high={p.high} < max(open={p.open},close={p.close})";
            if (p.low > Math.Min(p.open, p.close))
                return $"point[{i}] low={p.low} > min(open={p.open},close={p.close})";
            if (p.open_time_ms < prev)
                return $"point[{i}] open_time_ms={p.open_time_ms} < prev={prev} (not monotonic non-decreasing)";
            prev = p.open_time_ms;
        }
        return null;
    }

    // Launcher thread: takes the GIL, drives the production replay seam exactly like
    // python/spike/s1_adapter_smoke.py (DataEngine + InprocLiveServer.start_nautilus_replay),
    // verifies the worker is a daemon (orphan-free, kept from S1 as free regression
    // coverage), then RELEASES the GIL by exiting the using block so the daemon runs.
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
                    // while the worker is freshly started and alive (68 bars @ ~0.1s/bar).
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
                                Debug.Log("[REPLAY CHART DECODE] orphan-free OK: 'backtest-runner' is a daemon thread " +
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
