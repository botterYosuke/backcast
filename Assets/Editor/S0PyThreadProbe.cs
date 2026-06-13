// editor-only throwaway probe for the #18 Windows-leg "Python-owned engine thread" spike.
// S0PyThreadProbe.cs
//
// The #18 diagnostic ladder isolated the Windows-Mono S0 segfault to running
// BacktestEngine.run on a C#-CREATED (`new Thread`) host thread under pythonnet
// (the foreign thread's state is mishandled by Mono+pythonnet); main-thread and
// Mac-Mono both run it fine. This probe tests the fix hypothesis: run the SAME
// backtest on a CPYTHON-OWNED threading.Thread instead (the ownership model
// S2-spike's engine-owned asyncio loop already uses, GREEN on Windows-Mono).
//
//   <UnityEditor> -batchmode -nographics \
//       -projectPath <repo> -executeMethod S0PyThreadProbe.Run -logFile <path>
//
// Exit 0 => PASS, 1 => FAIL (self-failing gate). The C# background-thread direct
// run (S0EditorProbe.Run) is kept as the RED regression witness (#18 condition 6).
//
// THREADING (mirrors S2SpikeLiveLoopProbe discipline — the proven Win-Mono pattern):
//   * main: Initialize -> BeginAllowThreads (release GIL, stay GIL-free) -> run a
//           GIL-free heartbeat (headless proxy for "main keeps rendering") -> after
//           the worker joins the python engine thread, EndAllowThreads + Shutdown.
//   * W1 (Drive): Py.GIL() -> import + run_gates -> S0EngineSeam.start() (spawns the
//           PYTHON-owned engine thread) -> seam.join() (join releases the GIL while
//           blocking, so the engine thread runs Nautilus) -> read primitive results.
//   The Unity main thread NEVER takes the GIL while Nautilus runs; only C# primitives
//   cross worker->main. Normal shutdown joins the python-owned thread BEFORE the
//   runtime is finalized; on join timeout we FAIL and do NOT finalize.

using System;
using System.Diagnostics;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class S0PyThreadProbe
{
    const string MODULE          = "spike.s0_backtest";
    const double JOIN_TIMEOUT_S  = 90.0;    // python-side engine-thread join budget
    const int    W1_JOIN_MS      = 120000;  // main's Join of the C# Drive worker
    const long   MAX_STALL_MS    = 200;     // main heartbeat must never stall beyond this
    const long   EXPECTED_BARS   = 204;     // real catalog fixture (8918/6740/3823 DAY)

    // seam handle: created under the GIL on W1, never disposed (process exits).
    static volatile PyObject _seam;
    static IntPtr _mainThreadState;

    // W1 -> main (C# primitives only).
    static volatile string _w1Error;
    static volatile bool   _w1Done;
    static volatile bool   _joined;
    static volatile bool   _ok;
    static long   _bars;
    static long   _fills;
    static double _equity;
    static volatile string _runError = "";

    public static void Run()
    {
        bool passed        = false;
        bool engineStarted = false;

        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            Debug.Log("[S0 PYTHREAD MARK] configured");
            PythonEngine.Initialize();
            engineStarted = true;
            Debug.Log("[S0 PYTHREAD MARK] Initialize OK");

            // Release the GIL Initialize() holds; main stays GIL-free until finalize.
            _mainThreadState = PythonEngine.BeginAllowThreads();
            Debug.Log("[S0 PYTHREAD MARK] main released GIL; starting Drive worker");

            var w1 = new Thread(Drive) { IsBackground = true, Name = "S0PyThreadW1" };
            w1.Start();

            // main heartbeats GIL-free while the python-owned engine thread runs Nautilus.
            long maxStallMs = HeartbeatUntil(() => _w1Done, W1_JOIN_MS);

            bool j1 = w1.Join(W1_JOIN_MS);
            if (!j1)
            {
                Debug.LogError("[S0 PYTHREAD FAIL] Drive worker did not return within " + (W1_JOIN_MS / 1000) + "s");
                EditorApplication.Exit(1);
                return;
            }
            if (_w1Error != null)
            {
                Debug.LogError("[S0 PYTHREAD FAIL] " + _w1Error);
                EditorApplication.Exit(1);
                return;
            }

            // condition 5: engine thread did not join (timeout) => FAIL, do NOT finalize
            // (a live python thread may still be using the interpreter).
            if (!_joined)
            {
                Debug.LogError("[S0 PYTHREAD FAIL] python-owned engine thread did not join within "
                               + JOIN_TIMEOUT_S + "s — runtime NOT finalized");
                EditorApplication.Exit(1);
                return;
            }

            if (!_ok || _bars != EXPECTED_BARS || !string.IsNullOrEmpty(_runError))
            {
                Debug.LogError($"[S0 PYTHREAD FAIL] ok={_ok} bars={_bars} (expected {EXPECTED_BARS}) err={_runError}");
                EditorApplication.Exit(1);
                return;
            }

            if (maxStallMs >= MAX_STALL_MS)
            {
                Debug.LogError($"[S0 PYTHREAD FAIL] main heartbeat stalled {maxStallMs}ms (>= {MAX_STALL_MS}ms) "
                               + "— main was blocked (it must stay GIL-free while the engine thread runs)");
                EditorApplication.Exit(1);
                return;
            }

            // condition 4: the python-owned thread is already joined (W1 did seam.join
            // before returning); no thread holds the GIL, so finalize in order.
            PythonEngine.EndAllowThreads(_mainThreadState);
            PythonEngine.Shutdown();
            engineStarted = false;
            Debug.Log("[S0 PYTHREAD] runtime finalized in order (engine-thread join -> EndAllowThreads + Shutdown) without deadlock");

            Debug.Log($"[S0 PYTHREAD PASS] bars={_bars} fills={_fills} equity={_equity} maxStall={maxStallMs}ms "
                      + "(python-owned engine thread ran nautilus under Unity Mono; main GIL-free)");
            passed = true;
        }
        catch (Exception e)
        {
            Debug.LogError("[S0 PYTHREAD FAIL] driver: " + e);
        }
        finally
        {
            if (engineStarted)
                Debug.Log("[S0 PYTHREAD] runtime NOT finalized (failure path — process exits next)");
        }

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // W1: take the GIL, run gates, spawn the PYTHON-owned engine thread, join it
    // (join releases the GIL so the engine thread runs Nautilus), read results.
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
                    // Pin/footer gates on THIS interpreter first (a wrong wheel raises
                    // here instead of SIGABRT'ing deep in Rust).
                    m.InvokeMethod("run_gates").Dispose();
                    Debug.Log("[S0 PYTHREAD MARK] gates OK; creating seam + spawning python-owned engine thread");

                    using (PyObject seamCls = m.GetAttr("S0EngineSeam"))
                        _seam = seamCls.Invoke();        // kept alive across threads (not disposed)

                    _seam.InvokeMethod("start").Dispose();   // spawn the python engine thread (returns at once)

                    // join() releases the GIL while blocking -> engine thread runs Nautilus
                    // -> on return, results are populated. condition 4: join BEFORE finalize.
                    using (PyObject jr = _seam.InvokeMethod("join", new PyFloat(JOIN_TIMEOUT_S)))
                        _joined = jr.As<bool>();

                    using (PyObject ok = _seam.InvokeMethod("ok"))     _ok = ok.As<bool>();
                    using (PyObject b  = _seam.InvokeMethod("bars"))   Interlocked.Exchange(ref _bars, b.As<long>());
                    using (PyObject f  = _seam.InvokeMethod("fills"))  Interlocked.Exchange(ref _fills, f.As<long>());
                    using (PyObject eq = _seam.InvokeMethod("equity")) _equity = eq.As<double>();
                    using (PyObject er = _seam.InvokeMethod("error"))  _runError = er.As<string>();
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

    // GIL-free heartbeat on main: tracks the worst gap between beats. main never
    // takes the GIL, so a stall means main was genuinely blocked (proxy for a
    // frame stall). Returns the max stall in ms.
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
