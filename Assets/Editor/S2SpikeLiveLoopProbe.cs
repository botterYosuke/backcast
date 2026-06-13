// S2SpikeLiveLoopProbe.cs — editor-only throwaway probe for S2-spike (issue #7).
//
// The AFK core gate for Step 2's threading seam. Proves the unknown S0 (#2) did
// NOT exercise: a host worker marshals work into an engine-owned, long-lived
// asyncio loop via run_coroutine_threadsafe(coro, loop).result(timeout), and the
// .result() internal GIL hand-off (worker releases the GIL so the loop thread can
// acquire it, run the coro, then the worker re-acquires) is HEALTHY under Unity
// Mono + pythonnet. No venue connection (issue #7: throwaway / venue 実接続なし).
//
//   <UnityEditor> -batchmode -nographics \
//       -projectPath /Users/sasac/backcast \
//       -executeMethod S2SpikeLiveLoopProbe.Run \
//       -logFile <path>
//
// Exit code 0 => PASS, 1 => FAIL (self-failing gate; lead judges by $?).
// Prints `[S2-SPIKE LIVE LOOP PASS] ...` to Console/logfile.
//
// It drives the SAME public seam (python/spike/s2spike_live_loop.py:LiveLoopSeam)
// that the CPython smoke (run_smoke) drives. A Mono-only failure with the CPython
// smoke passing isolates the fault to the pythonnet/Mono GIL seam (mirrors S1 #9).
//
// GREEN = PROMPT COMPLETION, not "no-hang": .result(timeout) raises TimeoutError
// under GIL starvation instead of hanging, so a system that times out every call
// also "doesn't hang". We time each InvokeMethod with a C# Stopwatch over N calls
// and assert elapsed ≈ the coro's intrinsic ~60ms, well below the 5s timeout.
//
// THREADING (the S2-spike design point):
//   * main:  Initialize -> BeginAllowThreads (release main GIL, NEVER reacquire)
//            -> drive worker threads -> run a GIL-FREE heartbeat loop the whole
//            time, asserting it never stalls (headless proxy for "no frame stall";
//            the real-frame leg is the default-disabled S2SpikeLiveLoopHarness).
//   * W1:    Py.GIL() -> start the seam (daemon run_forever loop + tick-pump
//            sub-thread) -> N x marshal_work (Stopwatch each). .result() releases
//            the GIL each call so the loop thread + tick pump + W2 run.
//   * W2:    Py.GIL() -> ONE concurrent marshal_order (Stopwatch). Two C# threads
//            contend for the GIL + the single loop at once (issue #7 AC: 並行 order).
//   * W3:    Py.GIL() -> graceful_stop() (decision-6 cancel -> stop, same marshal).
//            main only Join(10000)s it (the wait is worker-side) so a host quit
//            never ANR-kills the main thread (AC(b)).
//   The seam PyObject is created under the GIL on W1 and kept in a static field so
//   W2/W3/teardown reach it under their own GIL; it is NOT disposed (process exits).
//
// Throwaway: paths come from PythonRuntimeLocator (shared, #9 M3). Python shutdown
// is skipped — the GIL is never reacquired on main, and the process Exit()s next
// (mirrors S0EditorProbe / S1AdapterSmokeProbe rationale).

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class S2SpikeLiveLoopProbe
{
    const string MODULE = "spike.s2spike_live_loop";

    // Thresholds mirror python/spike/s2spike_live_loop.py (owner-confirmed, grill 2026-06-13).
    const int    N_CALLS         = 10;
    const double ELAPSED_LOWER_S = 0.04;   // < intrinsic ~0.06s => below this is a no-op false green
    const double ELAPSED_UPPER_S = 1.0;    // << 5s timeout => prompt, not GIL-starvation under the timeout
    const double MEDIAN_UPPER_S  = 0.25;   // steady-state prompt-ness (cold first call included)
    const int    JOIN_TIMEOUT_MS = 10000;  // AC(b): main Join(10s) of the graceful-stop worker
    const long   MAX_STALL_MS    = 200;    // main heartbeat must never stall beyond this (frame-hitch baseline)

    // seam handle: created under GIL on W1, read under GIL on W2/W3/teardown. Never disposed.
    // volatile so W2's spin-loop reliably observes W1's publish (sibling cross-thread discipline).
    static volatile PyObject _seam;
    // published true only AFTER W1's start() returns (loop running + tick pump up). W2 waits on
    // THIS, not on _seam != null — so it can never call marshal_order on a not-yet-started seam.
    static volatile bool _seamReady;

    // main's saved Python thread-state from BeginAllowThreads, restored at runtime-finalize.
    static IntPtr _mainThreadState;

    // W1 -> main (C# primitives only). _w1Error non-null => W1 failed.
    static volatile string _w1Error;
    static volatile bool   _w1Done;
    static readonly double[] _workElapsed = new double[N_CALLS];
    static long _workCallsRecorded;
    static long _tickCount;

    // W2 -> main.
    static volatile string _orderError;
    static volatile bool   _orderDone;
    static double _orderElapsed;
    static string _orderAck;

    // W3 -> main (graceful_stop post-conditions).
    static volatile string _gsError;
    static volatile bool   _gsCancelRan;
    static long _gsResting = -1;
    static volatile bool   _gsStopped;

    public static void Run()
    {
        bool passed        = false;
        bool engineStarted = false;

        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            engineStarted = true;
            // Release the GIL Initialize() holds on main; main stays GIL-free until the
            // runtime-finalize step (AC(b)), where it restores this saved state to Shutdown.
            _mainThreadState = PythonEngine.BeginAllowThreads();

            // ---- Phase A (AC(a)): marshal_work x N + concurrent order, GIL-free heartbeat ----
            var w1 = new Thread(WorkLoop)  { IsBackground = true, Name = "S2SpikeW1" };
            var w2 = new Thread(OrderCall) { IsBackground = true, Name = "S2SpikeW2" };
            w1.Start();
            w2.Start();

            long maxStallMs = HeartbeatUntil(() => _w1Done && _orderDone, 90000);

            bool j1 = w1.Join(90000);
            bool j2 = w2.Join(90000);
            if (!j1 || !j2)
            {
                Debug.LogError($"[S2-SPIKE LIVE LOOP FAIL] Phase A worker(s) did not return within 90s (w1={j1}, w2={j2})");
                EditorApplication.Exit(1);
                return;
            }

            if (!AssertPhaseA(maxStallMs))
            {
                EditorApplication.Exit(1);
                return;
            }

            // ---- Phase B (AC(b)): worker graceful_stop, main Join(10s) only, THEN teardown ----
            var w3 = new Thread(GracefulStop) { IsBackground = true, Name = "S2SpikeW3" };
            w3.Start();
            bool gsJoined = w3.Join(JOIN_TIMEOUT_MS);

            if (!gsJoined)
            {
                // A live worker is still using Python; finalizing the runtime now is
                // dangerous (AC(b): Join timeout => FAIL, do NOT finalize).
                Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] graceful_stop worker did not return within " +
                               (JOIN_TIMEOUT_MS / 1000) + "s — runtime NOT finalized (live worker still in Python)");
                EditorApplication.Exit(1);
                return;
            }
            if (!AssertPhaseB())
            {
                EditorApplication.Exit(1);
                return;
            }

            // Teardown ONLY after graceful_stop completed + worker joined (AC(b) ordering),
            // plus the reverse-order negative micro-check on an independent instance.
            if (!TeardownAndNegativeCheck())
            {
                EditorApplication.Exit(1);
                return;
            }

            // AC(b) FINAL step — runtime finalize, in order. Every worker has now joined and
            // the loop is torn down, so NO thread holds the GIL; main can reacquire it
            // (EndAllowThreads) and Shutdown() the interpreter. This exercises the full
            // ordering graceful_stop -> worker join -> loop teardown -> runtime finalize. A
            // wrong order (finalize while a worker still held the GIL) would DEADLOCK here;
            // that Shutdown() returns proves the ordering is sound. (On any FAIL path above we
            // Exit(1) WITHOUT finalizing — a live worker may hold the GIL — per AC(b).)
            try
            {
                PythonEngine.EndAllowThreads(_mainThreadState);
                PythonEngine.Shutdown();
                engineStarted = false;   // finalized; the finally must not claim otherwise
            }
            catch (Exception e)
            {
                Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] runtime finalize (EndAllowThreads + Shutdown) threw: " + e);
                EditorApplication.Exit(1);
                return;
            }
            Debug.Log("[S2-SPIKE LIVE LOOP] runtime finalized in order (graceful_stop -> join -> " +
                      "loop teardown -> EndAllowThreads + Shutdown) without deadlock");

            double medianS = Median(_workElapsed);
            Debug.Log($"[S2-SPIKE LIVE LOOP PASS] calls={N_CALLS} median={medianS:0.0000}s " +
                      $"band=[{_workElapsed.Min():0.0000},{_workElapsed.Max():0.0000}]s " +
                      $"order={_orderAck} orderElapsed={_orderElapsed:0.0000}s ticks={_tickCount} " +
                      $"maxStall={maxStallMs}ms cancel_ran=True resting=0 reverse_order_guarded " +
                      "runtime_finalized (asyncio marshal GIL hand-off + shutdown ordering healthy under Unity Mono)");
            passed = true;
        }
        catch (Exception e)
        {
            Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] driver: " + e);
        }
        finally
        {
            // engineStarted is cleared once the runtime is finalized above; if it is still
            // set we took a FAIL path and deliberately did NOT finalize (a worker may hold
            // the GIL). The process exits next either way.
            if (engineStarted)
                Debug.Log("[S2-SPIKE LIVE LOOP] runtime NOT finalized (failure path — worker may hold the GIL; process exits next)");
        }

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // W1: take the GIL, start the seam, marshal_work x N (Stopwatch each), read ticks.
    static void WorkLoop()
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

                PyObject mod     = Py.Import(MODULE);
                PyObject seamCls  = mod.GetAttr("LiveLoopSeam");
                _seam = seamCls.Invoke();                  // kept alive across threads (not disposed)
                seamCls.Dispose();
                mod.Dispose();

                using (PyObject started = _seam.InvokeMethod("start")) { /* daemon loop + tick pump up */ }
                _seamReady = true;   // publish ONLY after start() returns (loop running) — gates W2

                for (int i = 0; i < N_CALLS; i++)
                {
                    var sw = Stopwatch.StartNew();
                    using (PyObject r = _seam.InvokeMethod("marshal_work"))   // .result() releases the GIL here
                    {
                        sw.Stop();
                        long counter = r.As<long>();
                        if (counter <= 0)
                        {
                            _w1Error = $"marshal_work #{i} work counter={counter} (coro did not execute — no-op fast path?)";
                            return;
                        }
                    }
                    _workElapsed[i] = sw.Elapsed.TotalSeconds;
                    Interlocked.Increment(ref _workCallsRecorded);
                }

                using (PyObject t = _seam.InvokeMethod("tick_count"))
                    Interlocked.Exchange(ref _tickCount, t.As<long>());
            }
        }
        catch (Exception e)
        {
            _w1Error = "W1: " + e;
        }
        finally
        {
            _w1Done = true;
        }
    }

    // W2: one concurrent order marshal under its own GIL (contends with W1).
    static void OrderCall()
    {
        try
        {
            // Wait until W1 has fully STARTED the seam (not merely constructed it) so the
            // concurrent order can never hit a not-yet-running loop.
            var sw0 = Stopwatch.StartNew();
            while (!_seamReady && sw0.Elapsed.TotalSeconds < 30 && _w1Error == null)
                Thread.Sleep(5);
            if (!_seamReady)
            {
                _orderError = _w1Error != null
                    ? "W1 failed before the seam was ready: " + _w1Error
                    : "seam not ready (W1 did not start it within 30s)";
                return;
            }

            using (Py.GIL())
            {
                var sw = Stopwatch.StartNew();
                using (PyObject ack = _seam.InvokeMethod("marshal_order"))
                {
                    sw.Stop();
                    _orderAck = ack.As<string>();
                }
                _orderElapsed = sw.Elapsed.TotalSeconds;
            }
        }
        catch (Exception e)
        {
            _orderError = "W2: " + e;
        }
        finally
        {
            _orderDone = true;
        }
    }

    // W3: graceful_stop (cancel -> stop), waits worker-side; main only Join()s it.
    static void GracefulStop()
    {
        try
        {
            using (Py.GIL())
            using (PyObject res = _seam.InvokeMethod("graceful_stop"))
            using (PyObject cancelRan = res["cancel_ran"])
            using (PyObject resting   = res["resting"])
            using (PyObject stopped   = res["stopped"])
            {
                _gsCancelRan = cancelRan.As<bool>();
                Interlocked.Exchange(ref _gsResting, resting.As<long>());
                _gsStopped = stopped.As<bool>();
            }
        }
        catch (Exception e)
        {
            _gsError = "W3: " + e;
        }
    }

    // Teardown (loop stop/join) + reverse-order negative micro-check, both under the GIL
    // on a worker (main stays GIL-free). Returns false on any failure (already logged).
    static bool TeardownAndNegativeCheck()
    {
        string err = null;
        var t = new Thread(() =>
        {
            try
            {
                using (Py.GIL())
                {
                    using (PyObject td = _seam.InvokeMethod("teardown_loop"))
                    {
                        if (!td.As<bool>())
                        {
                            err = "loop thread did not terminate on teardown";
                            return;
                        }
                    }
                    using (PyObject mod = Py.Import(MODULE))
                    using (PyObject neg = mod.InvokeMethod("reverse_order_negative_check"))
                    using (PyObject cancelRan = neg["cancel_ran"])
                    using (PyObject resting   = neg["resting"])
                    using (PyObject schedFail = neg["schedule_failed"])
                    {
                        if (cancelRan.As<bool>() || resting.As<long>() <= 0 || !schedFail.As<bool>())
                            err = "reverse-order negative check did not behave (cancel must NOT run on a closed loop)";
                    }
                }
            }
            catch (Exception e)
            {
                err = "teardown/negative: " + e;
            }
        }) { IsBackground = true, Name = "S2SpikeTeardown" };
        t.Start();
        if (!t.Join(30000))
        {
            // A hung teardown must FAIL — not silently PASS with err still null.
            Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] teardown/negative-check thread did not return within 30s (teardown hung)");
            return false;
        }

        if (err != null)
        {
            Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] " + err);
            return false;
        }
        return true;
    }

    // GIL-free heartbeat on main: increments at a steady cadence and tracks the worst
    // gap between beats. main never takes the GIL, so a stall would mean main was
    // genuinely blocked — the headless proxy for "Unity 安定描画 / no frame stall".
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

    static bool AssertPhaseA(long maxStallMs)
    {
        if (_w1Error != null)    { Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] " + _w1Error); return false; }
        if (_orderError != null) { Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] " + _orderError); return false; }

        if (Interlocked.Read(ref _workCallsRecorded) != N_CALLS)
        {
            Debug.LogError($"[S2-SPIKE LIVE LOOP FAIL] only {Interlocked.Read(ref _workCallsRecorded)}/{N_CALLS} marshal_work calls recorded");
            return false;
        }

        for (int i = 0; i < N_CALLS; i++)
        {
            double dt = _workElapsed[i];
            if (!(dt >= ELAPSED_LOWER_S && dt < ELAPSED_UPPER_S))
            {
                Debug.LogError($"[S2-SPIKE LIVE LOOP FAIL] call #{i} elapsed={dt:0.0000}s " +
                               $"outside [{ELAPSED_LOWER_S}, {ELAPSED_UPPER_S})s");
                return false;
            }
        }

        double medianS = Median(_workElapsed);
        if (medianS >= MEDIAN_UPPER_S)
        {
            Debug.LogError($"[S2-SPIKE LIVE LOOP FAIL] median elapsed={medianS:0.0000}s >= {MEDIAN_UPPER_S}s " +
                           "(not steady-state prompt — GIL hand-off limping)");
            return false;
        }

        if (_orderAck == null || !_orderAck.StartsWith("ACK:"))
        {
            Debug.LogError($"[S2-SPIKE LIVE LOOP FAIL] order result != expected ACK:* (got {(_orderAck ?? "<null>")})");
            return false;
        }
        if (!(_orderElapsed >= ELAPSED_LOWER_S && _orderElapsed < ELAPSED_UPPER_S))
        {
            Debug.LogError($"[S2-SPIKE LIVE LOOP FAIL] order elapsed={_orderElapsed:0.0000}s " +
                           $"outside [{ELAPSED_LOWER_S}, {ELAPSED_UPPER_S})s");
            return false;
        }

        if (Interlocked.Read(ref _tickCount) <= 0)
        {
            Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] tick count=0 — sub-thread tick push never reached " +
                           "the loop (loop starved by the host's .result waits?)");
            return false;
        }

        if (maxStallMs >= MAX_STALL_MS)
        {
            Debug.LogError($"[S2-SPIKE LIVE LOOP FAIL] main heartbeat stalled {maxStallMs}ms (>= {MAX_STALL_MS}ms) " +
                           "— main thread was blocked (it must stay GIL-free while workers marshal)");
            return false;
        }

        return true;
    }

    static bool AssertPhaseB()
    {
        if (_gsError != null) { Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] " + _gsError); return false; }
        if (!_gsCancelRan)
        {
            Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] decision-6 cancel did NOT run through the marshal " +
                           "(cancel_ran=False) — broker resting orders would survive");
            return false;
        }
        if (Interlocked.Read(ref _gsResting) != 0)
        {
            Debug.LogError($"[S2-SPIKE LIVE LOOP FAIL] resting orders not drained (resting={Interlocked.Read(ref _gsResting)})");
            return false;
        }
        if (!_gsStopped)
        {
            Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] stop coroutine did not complete (stopped=False)");
            return false;
        }
        return true;
    }

    static double Median(double[] xs)
    {
        var copy = (double[])xs.Clone();
        Array.Sort(copy);
        int n = copy.Length;
        return (n % 2 == 1) ? copy[n / 2] : 0.5 * (copy[n / 2 - 1] + copy[n / 2]);
    }
}
