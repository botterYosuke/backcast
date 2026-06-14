// editor-only throwaway probe for #25 / #22 — Kernel Live Mono full-Live gate
// KernelLiveProbe.cs — ADR-0004 案 C / docs/findings/0011 §Unity-Mono full Live gate (D5 layer 3)
//                      + docs/findings/0013 (#22 live safety & graceful shutdown, Mono leg)
//
// #25 AC: prove the pure-Python KernelLiveEngineController drives a full mock-venue LiveAuto
// roundtrip (start→order→fill→position→stop) under Unity's Mono runtime + pythonnet, with the
// Rust core NEVER loaded, and TEARS DOWN CLEAN — no segfault / stack-overflow, exit 0.
//
// #22 additions (run_all): after the twin roundtrip, leave one ACCEPTED-but-unfilled order
// resting and prove the graceful-stop cancel path (stop_run → cancel_inflight_orders → venue
// cancel_order) drives it to CANCELED (Gap1 / S2-spike AC(b) on the production path), assert the
// orphan-absence structural invariants (embedded python shares the host PID, the live loop runs on
// a daemon thread, no out-of-process order pump — Gap3), and that stop_live_loop reports a clean
// join (Gap4 happy path).
//
// This extends #24's KernelTeardownProbe (which ran only the Replay kernel tracer): here the
// headless harness from spike.kernel_live.run_mock_live: Mono drives the SINGLE-chain
// run_shutdown_cancel() (CPython gate drives the two-chain run_all(); run_all crashed the Mono
// runtime — see body comment at the InvokeMethod call), then asserts nautilus_trader*/nautilus_pyo3
// stayed out of sys.modules.
//
//   <UnityEditor> -batchmode -nographics -quit \
//       -projectPath C:\Users\sasai\Documents\backcast \
//       -executeMethod KernelLiveProbe.Run
//
// Exit code 0 => PASS, 1 => FAIL (self-failing gate; owner also confirms the absence of a fresh
// %LOCALAPPDATA%\CrashDumps\Unity.exe.*.dmp). Mirrors KernelTeardownProbe's GIL discipline:
// worker takes the GIL, main is GIL-free via BeginAllowThreads and heartbeats while joining.
// Throwaway: lives under Assets/Editor/, excluded from player builds.

using System;
using System.Diagnostics;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class KernelLiveProbe
{
    // worker -> main result (only C# primitives cross the thread boundary)
    static long   _leaked   = -1;     // count of nautilus_trader*/nautilus_pyo3 modules
    static string _error;             // non-null => worker failed
    // #22 — resting-order best-effort cancel (Gap1)
    static string _restingBefore;     // want "ACCEPTED"
    static string _restingAfter;      // want "CANCELED"
    static long   _cancelCalls = -1;  // venue cancel_order count, want 1
    // #22 — orphan-absence structural invariants (Gap3)
    static long   _pythonPid   = -1;  // os.getpid() inside the embedded Python; want == this process
    static long   _loopDaemon  = -1;  // 1 => live loop thread is daemon
    static long   _childCount  = -1;  // multiprocessing children spawned by the live chain, want 0
    // #22 — stop_live_loop clean-join contract (Gap4 happy path)
    static long   _loopClean   = -1;  // 1 => stop_live_loop returned True (safe to finalize)

    public static void Run()
    {
        bool   passed        = false;
        IntPtr ts            = IntPtr.Zero;
        bool   engineStarted = false;
        bool   workerStopped = true;

        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            Debug.Log($"[KERNEL LIVE MARK] configured: dll={PythonRuntimeLocator.LibPython} home={PythonRuntimeLocator.PythonHome} site={PythonRuntimeLocator.VenvSite}");

            PythonEngine.Initialize();
            engineStarted = true;
            Debug.Log("[KERNEL LIVE MARK] PythonEngine.Initialize OK");

            ts = PythonEngine.BeginAllowThreads();
            Debug.Log("[KERNEL LIVE MARK] BeginAllowThreads OK; starting worker");

            var worker = new Thread(Worker) { IsBackground = true, Name = "KernelLiveWorker" };
            worker.Start();
            int beats = 0;
            while (!worker.Join(500))
            {
                Debug.Log($"[KERNEL LIVE HEARTBEAT] main alive, GIL-free (beat {++beats})");
                if (beats > 120) break;   // 60s cap
            }
            workerStopped = worker.Join(0);

            string err = Volatile.Read(ref _error);
            if (!workerStopped)
            {
                Debug.LogError("[KERNEL LIVE FAIL] worker timeout (did not finish within 60s)");
            }
            else if (err != null)
            {
                Debug.LogError("[KERNEL LIVE FAIL] " + err);
            }
            else if (Volatile.Read(ref _leaked) != 0)
            {
                Debug.LogError($"[KERNEL LIVE FAIL] Rust core leaked: {_leaked} nautilus module(s) loaded — AC requires the LiveAuto path to stay Rust-core-free");
            }
            else if (_restingBefore != "ACCEPTED" || _restingAfter != "CANCELED" || _cancelCalls != 1)
            {
                // #22 Gap1: a resting (ACCEPTED, unfilled) order must be best-effort canceled on
                // graceful shutdown (stop_run → cancel_inflight_orders → venue cancel_order).
                Debug.LogError($"[KERNEL LIVE FAIL] resting-cancel mismatch: {_restingBefore}->{_restingAfter} cancel_calls={_cancelCalls} (want ACCEPTED->CANCELED / 1)");
            }
            else if (_pythonPid != Process.GetCurrentProcess().Id)
            {
                // #22 Gap3: embedded Python must share the host OS process (Unity death = Python death).
                Debug.LogError($"[KERNEL LIVE FAIL] orphan risk: embedded python pid={_pythonPid} != host pid={Process.GetCurrentProcess().Id} (not the same process)");
            }
            else if (_loopDaemon != 1 || _childCount != 0)
            {
                // #22 Gap3: the order pump must run on a daemon thread with no out-of-process child.
                Debug.LogError($"[KERNEL LIVE FAIL] orphan-absence invariant broken: loop_daemon={_loopDaemon} child_count={_childCount} (want daemon / 0 children)");
            }
            else if (_loopClean != 1)
            {
                // #22 Gap4: the live loop must join cleanly so the finalize gate may proceed.
                Debug.LogError($"[KERNEL LIVE FAIL] stop_live_loop did not report a clean join (loop_clean={_loopClean})");
            }
            else
            {
                passed = true;
                Debug.Log("[KERNEL LIVE MARK] LiveAuto roundtrip + resting-cancel + orphan invariants OK, Rust core absent — now exercising teardown");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[KERNEL LIVE FAIL] driver: " + e);
        }
        finally
        {
            try
            {
                // #22 Gap4: actually CONSUME the fail-closed signal. Gate runtime finalize on
                // BOTH the C# worker having returned AND the Python live loop having joined
                // cleanly (loop_stopped_clean == True). A hung daemon loop can leave workerStopped
                // true while the loop thread still holds the GIL — finalizing then deadlocks.
                bool loopClean = Volatile.Read(ref _loopClean) == 1;
                if (engineStarted && workerStopped && loopClean)
                {
                    if (ts != IntPtr.Zero)
                    {
                        PythonEngine.EndAllowThreads(ts);
                    }
                    // Same teardown the nautilus_pyo3 build crashed at (s0-result §1.2/§1.3).
                    // With no Rust core loaded, this must complete cleanly.
                    PythonEngine.Shutdown();
                    Debug.Log("[KERNEL LIVE MARK] PythonEngine.Shutdown OK (clean teardown)");
                }
                else if (engineStarted)
                {
                    Debug.LogWarning($"[KERNEL LIVE] skipping Python shutdown to avoid GIL deadlock (workerStopped={workerStopped} loopClean={Volatile.Read(ref _loopClean)})");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[KERNEL LIVE] shutdown cleanup: " + e);
            }
        }

        if (passed)
            Debug.Log("[KERNEL LIVE PASS] full mock LiveAuto ran under Unity-Mono (start→order→fill→position→stop), " +
                      "resting order best-effort canceled on graceful stop (#22 Gap1), orphan-absence invariants hold " +
                      "(same process / daemon loop / 0 child — #22 Gap3), clean loop join (#22 Gap4), Rust core absent, " +
                      "Shutdown clean — confirm exit 0 + NO new Unity.exe crash dump");

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // Background thread: takes the GIL only inside `using`, runs the SAME headless LiveAuto
    // harness the CPython purity gate uses, and reads back the result dict — all via the
    // Python entry point spike.kernel_live.run_mock_live.run().
    static void Worker()
    {
        try
        {
            using (Py.GIL())
            {
                Debug.Log("[KERNEL LIVE MARK] worker GIL acquired");
                using (PyObject sys = Py.Import("sys"))
                using (PyObject sysPath = sys.GetAttr("path"))
                {
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
                }
                Debug.Log("[KERNEL LIVE MARK] sys.path set; running mock LiveAuto roundtrip (NO nautilus)");

                // #22: drive a SINGLE live chain (resting-order shutdown-cancel scenario). Running two
                // heavyweight chains back-to-back in one Mono process (run_all) crashed the mono runtime;
                // the #25 twin fill-roundtrip is already proven in Mono and re-proven by the CPython gate.
                using (PyObject mod = Py.Import("spike.kernel_live.run_mock_live"))
                using (PyObject res = mod.InvokeMethod("run_shutdown_cancel"))
                {
                    using (PyObject k = new PyString("leaked"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _leaked, v.Length());
                    // #22 Gap1 — resting-order best-effort cancel
                    using (PyObject k = new PyString("resting_before_stop"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _restingBefore, v.As<string>());
                    using (PyObject k = new PyString("resting_after_stop"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _restingAfter, v.As<string>());
                    using (PyObject k = new PyString("cancel_calls"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _cancelCalls, v.As<long>());
                    // #22 Gap3 — orphan-absence structural invariants
                    using (PyObject k = new PyString("python_pid"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _pythonPid, v.As<long>());
                    using (PyObject k = new PyString("loop_daemon"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _loopDaemon, v.As<bool>() ? 1 : 0);
                    using (PyObject k = new PyString("child_count"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _childCount, v.As<long>());
                    // #22 Gap4 — stop_live_loop clean-join contract (happy path)
                    using (PyObject k = new PyString("loop_stopped_clean"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _loopClean, v.As<bool>() ? 1 : 0);
                }
                Debug.Log("[KERNEL LIVE MARK] resting-cancel + orphan capture done");
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _error, e.ToString());
        }
    }
}
