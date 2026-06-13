// editor-only throwaway probe for the #18 Windows-leg "long-lived engine thread" spike (2).
// S0LongThreadProbe.cs
//
// Diagnostic ladder + spike (1) results:
//   * C# `new Thread` running BacktestEngine.run  -> segfault DURING run.
//   * python-owned threading.Thread, joined per-run -> run COMPLETES (bars=204),
//     segfault while the per-run thread TERMINATES.
// So the crash is localized to a per-run thread's TERMINATION lifecycle. This
// spike tests the production / S2-spike ownership model: ONE process-lifetime
// daemon engine thread (command queue), backtests marshalled IN, the thread NEVER
// terminated/joined/recreated per-run. The OS reclaims it at process exit (no
// explicit PythonEngine.Shutdown), so the crashing termination path is never hit.
//
//   <UnityEditor> -batchmode -nographics \
//       -projectPath <repo> -executeMethod S0LongThreadProbe.Run -logFile <path>
//
// Exit 0 => PASS, 1 => FAIL. The C# background-thread direct run (S0EditorProbe)
// stays as the RED regression witness (#18 condition 6).
//
// GREEN (owner spec): 3 submits all bars=204; SAME engine-thread id across runs;
// thread idle (queue-wait) after each run; no segfault/deadlock/join-wait; main
// stays GIL-free (heartbeat never stalls); process exits cleanly (no finalize).

using System;
using System.Diagnostics;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class S0LongThreadProbe
{
    const string MODULE        = "spike.s0_backtest";
    const int    RUNS          = 3;
    const double SUBMIT_TIMEOUT_S = 90.0;
    const int    W1_JOIN_MS    = 180000;
    const long   MAX_STALL_MS  = 200;
    const long   EXPECTED_BARS = 204;

    static volatile PyObject _engine;   // long-lived engine handle; never disposed
    static IntPtr _mainThreadState;
    static int _runs = RUNS;            // overridable via S0_RUNS (confound check)

    static volatile string _w1Error;
    static volatile bool   _w1Done;
    static readonly long[] _bars      = new long[RUNS];
    static readonly long[] _threadIds = new long[RUNS];
    static readonly bool[] _waiting   = new bool[RUNS];
    static volatile bool   _allOk = true;
    static volatile string _runError = "";

    public static void Run()
    {
        bool passed        = false;
        bool engineStarted = false;

        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            Debug.Log("[S0 LONGTHREAD MARK] configured");
            PythonEngine.Initialize();
            engineStarted = true;
            Debug.Log("[S0 LONGTHREAD MARK] Initialize OK");

            _mainThreadState = PythonEngine.BeginAllowThreads();   // main GIL-free for the whole run
            Debug.Log("[S0 LONGTHREAD MARK] main released GIL; starting Drive worker");

            // #18 confound check: S0_RUNS overrides the run count (default RUNS).
            // S0_LOG_MODE=error (read in Python) selects log_level=ERROR — only valid
            // with S0_RUNS=1 (multi-run + non-bypass logging hits the logger singleton).
            int envRuns;
            if (int.TryParse(Environment.GetEnvironmentVariable("S0_RUNS"), out envRuns) && envRuns >= 1 && envRuns <= RUNS)
                _runs = envRuns;
            Debug.Log("[S0 LONGTHREAD MARK] runs=" + _runs);

            var w1 = new Thread(Drive) { IsBackground = true, Name = "S0LongThreadW1" };
            w1.Start();

            long maxStallMs = HeartbeatUntil(() => _w1Done, W1_JOIN_MS);

            if (!w1.Join(W1_JOIN_MS))
            {
                Debug.LogError("[S0 LONGTHREAD FAIL] Drive worker did not return within " + (W1_JOIN_MS / 1000) + "s");
                EditorApplication.Exit(1);
                return;
            }
            if (_w1Error != null)
            {
                Debug.LogError("[S0 LONGTHREAD FAIL] " + _w1Error);
                EditorApplication.Exit(1);
                return;
            }

            // --- verdict (owner GREEN conditions) ---
            bool allBars = true, sameThread = true, allWaiting = true;
            long t0 = _threadIds[0];
            for (int i = 0; i < _runs; i++)
            {
                if (_bars[i] != EXPECTED_BARS) allBars = false;
                if (_threadIds[i] != t0 || t0 == 0) sameThread = false;
                if (!_waiting[i]) allWaiting = false;
            }

            if (!_allOk || !string.IsNullOrEmpty(_runError))
            {
                Debug.LogError($"[S0 LONGTHREAD FAIL] a run failed: err={_runError}");
                EditorApplication.Exit(1);
                return;
            }
            if (!allBars)
            {
                Debug.LogError($"[S0 LONGTHREAD FAIL] not all runs bars={EXPECTED_BARS}: [{_bars[0]},{_bars[1]},{_bars[2]}]");
                EditorApplication.Exit(1);
                return;
            }
            if (!sameThread)
            {
                Debug.LogError($"[S0 LONGTHREAD FAIL] engine thread id not stable across runs: [{_threadIds[0]},{_threadIds[1]},{_threadIds[2]}]");
                EditorApplication.Exit(1);
                return;
            }
            if (!allWaiting)
            {
                Debug.LogError($"[S0 LONGTHREAD FAIL] engine thread did not return to wait state after every run: [{_waiting[0]},{_waiting[1]},{_waiting[2]}]");
                EditorApplication.Exit(1);
                return;
            }
            if (maxStallMs >= MAX_STALL_MS)
            {
                Debug.LogError($"[S0 LONGTHREAD FAIL] main heartbeat stalled {maxStallMs}ms (>= {MAX_STALL_MS}ms) — main must stay GIL-free");
                EditorApplication.Exit(1);
                return;
            }

            // NO explicit finalize: the long-lived daemon engine thread is never
            // terminated; the OS reclaims the thread + interpreter at process exit
            // (this is the whole point — the crashing per-run-termination path is
            // never entered). main stays GIL-free; we do NOT EndAllowThreads/Shutdown.
            Debug.Log($"[S0 LONGTHREAD PASS] runs={RUNS} bars=[{_bars[0]},{_bars[1]},{_bars[2]}] " +
                      $"engineThreadId={t0} (stable) waiting=[{_waiting[0]},{_waiting[1]},{_waiting[2]}] " +
                      $"maxStall={maxStallMs}ms (long-lived python-owned engine thread ran nautilus {RUNS}x off-main " +
                      "under Unity Mono; never terminated per-run; main GIL-free; process exits without finalize)");
            passed = true;
        }
        catch (Exception e)
        {
            Debug.LogError("[S0 LONGTHREAD FAIL] driver: " + e);
        }
        finally
        {
            if (engineStarted)
                Debug.Log("[S0 LONGTHREAD] runtime intentionally NOT finalized (long-lived engine thread; OS reclaims at process exit)");
        }

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // W1: GIL -> gates -> start the long-lived engine thread -> submit_backtest x RUNS,
    // reading primitive results after each. submit_backtest blocks in queue.get which
    // releases the GIL, so the long-lived engine thread runs nautilus while main renders.
    static void Drive()
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

                using (PyObject m = Py.Import(MODULE))
                {
                    m.InvokeMethod("run_gates").Dispose();
                    Debug.Log("[S0 LONGTHREAD MARK] gates OK; starting long-lived engine thread");

                    _engine = m.InvokeMethod("get_long_lived_engine");   // kept alive (not disposed)
                    using (PyObject tid = _engine.InvokeMethod("start"))
                        Debug.Log("[S0 LONGTHREAD MARK] engine thread id=" + tid.As<long>());

                    for (int i = 0; i < _runs; i++)
                    {
                        using (PyObject ok = _engine.InvokeMethod("submit_backtest", new PyFloat(SUBMIT_TIMEOUT_S)))
                        {
                            if (!ok.As<bool>())
                            {
                                _allOk = false;
                                using (PyObject er = _engine.InvokeMethod("last_error")) _runError = er.As<string>();
                            }
                        }
                        using (PyObject b  = _engine.InvokeMethod("last_bars"))  _bars[i]      = b.As<long>();
                        using (PyObject t  = _engine.InvokeMethod("thread_id"))  _threadIds[i] = t.As<long>();
                        using (PyObject w  = _engine.InvokeMethod("is_waiting")) _waiting[i]   = w.As<bool>();
                        Debug.Log($"[S0 LONGTHREAD MARK] run {i}: bars={_bars[i]} threadId={_threadIds[i]} waiting={_waiting[i]}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            _w1Error = "Drive: " + e;
        }
        finally
        {
            _w1Done = true;
        }
    }

    static long HeartbeatUntil(Func<bool> done, int budgetMs)
    {
        var total = Stopwatch.StartNew();
        var beat = Stopwatch.StartNew();
        long maxStallMs = 0;
        while (!done() && total.ElapsedMilliseconds < budgetMs)
        {
            long gap = beat.ElapsedMilliseconds;
            if (gap > maxStallMs) maxStallMs = gap;
            beat.Restart();
            Thread.Sleep(2);
        }
        return maxStallMs;
    }
}
