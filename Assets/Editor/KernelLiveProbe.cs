// editor-only throwaway probe for #25 — Kernel Live Foundation Mono full-Live gate
// KernelLiveProbe.cs — ADR-0004 案 C / docs/findings/0010 §Unity-Mono full Live gate (D5 layer 3)
//
// #25 AC: prove the pure-Python KernelLiveEngineController drives a full mock-venue LiveAuto
// roundtrip (start→order→fill→position→stop) under Unity's Mono runtime + pythonnet, with the
// Rust core NEVER loaded, and TEARS DOWN CLEAN — no segfault / stack-overflow, exit 0.
//
// This extends #24's KernelTeardownProbe (which ran only the Replay kernel tracer): here the
// SAME headless harness the CPython purity gate uses (spike.kernel_live.run_mock_live.run())
// is exercised in Mono — login → LiveRunner → attach → 40 bar 注入 → 2 fills → flat → detach —
// then asserts nautilus_trader*/nautilus_pyo3 stayed out of sys.modules.
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
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class KernelLiveProbe
{
    // worker -> main result (only C# primitives cross the thread boundary)
    static long   _fills    = -1;
    static double _finalNet = double.NaN;
    static double _realized = double.NaN;
    static long   _leaked   = -1;     // count of nautilus_trader*/nautilus_pyo3 modules
    static string _error;             // non-null => worker failed

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
            else if (_fills != 2 || _finalNet != 0.0 || _realized != 200.0)
            {
                Debug.LogError($"[KERNEL LIVE FAIL] roundtrip mismatch: fills={_fills} final_net={_finalNet} realized={_realized} (want 2 / 0 / 200)");
            }
            else
            {
                passed = true;
                Debug.Log("[KERNEL LIVE MARK] LiveAuto roundtrip OK, Rust core absent — now exercising teardown");
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
                if (engineStarted && workerStopped)
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
                    Debug.LogWarning("[KERNEL LIVE] worker did not stop; skipping Python shutdown to avoid GIL deadlock");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[KERNEL LIVE] shutdown cleanup: " + e);
            }
        }

        if (passed)
            Debug.Log("[KERNEL LIVE PASS] full mock LiveAuto ran under Unity-Mono (start→order→fill→position→stop), Rust core absent, Shutdown clean — confirm exit 0 + NO new Unity.exe crash dump");

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

                using (PyObject mod = Py.Import("spike.kernel_live.run_mock_live"))
                using (PyObject res = mod.InvokeMethod("run"))
                {
                    using (PyObject k = new PyString("fills"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _fills, v.As<long>());
                    using (PyObject k = new PyString("final_net"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _finalNet, v.As<double>());
                    using (PyObject k = new PyString("realized"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _realized, v.As<double>());
                    using (PyObject k = new PyString("leaked"))
                    using (PyObject v = res.GetItem(k)) Volatile.Write(ref _leaked, v.Length());
                }
                Debug.Log("[KERNEL LIVE MARK] roundtrip done; result captured");
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _error, e.ToString());
        }
    }
}
