// editor-only throwaway probe for AC#2 — Backcast Execution Kernel Mono teardown (#24)
// KernelTeardownProbe.cs — ADR-0004 案 C / docs/findings/0008 §7
//
// AC#2: prove the pure-Python Backcast Execution Kernel runs under Unity's Mono
// runtime + pythonnet and TEARS DOWN CLEAN — no segfault / stack-overflow, exit 0.
// This is the leg that headless CPython cannot settle (test_kernel_teardown_mono.py
// proves only the structural precondition: exit 0 + no Rust core). The whole premise
// of 案 C is that with NO nautilus_pyo3 Rust core in the process, the multi-CRT/FLS
// teardown crash (s0-result §1.1–§1.4) cannot occur.
//
//   <UnityEditor> -batchmode -nographics -quit \
//       -projectPath C:\Users\sasai\Documents\backcast \
//       -executeMethod KernelTeardownProbe.Run
//
// Exit code 0 => PASS, 1 => FAIL (self-failing gate; owner judges by $? AND by the
// absence of a fresh %LOCALAPPDATA%\CrashDumps\Unity.exe.*.dmp).
//
// Mirrors S0EditorProbe's GIL discipline: worker takes the GIL, main is GIL-free via
// BeginAllowThreads and heartbeats while joining (proves no GIL stall). The DIFFERENCE
// from S0EditorProbe: this imports the kernel (Nautilus-free) instead of
// spike.s0_backtest (loads nautilus_pyo3), and asserts the Rust core never loaded.
// Throwaway: lives under Assets/Editor/, excluded from player builds.

using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class KernelTeardownProbe
{
    // worker -> main result (only C# primitives cross the thread boundary)
    static bool   _verifyOk;
    static string _verifyMsg;
    static long   _leakedNautilus = -1;   // count of nautilus_trader*/nautilus_pyo3 modules
    static string _error;                  // non-null => worker failed

    public static void Run()
    {
        bool   passed        = false;
        IntPtr ts            = IntPtr.Zero;
        bool   engineStarted = false;
        bool   workerStopped = true;

        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            Debug.Log($"[KERNEL TEARDOWN MARK] configured: dll={PythonRuntimeLocator.LibPython} home={PythonRuntimeLocator.PythonHome} site={PythonRuntimeLocator.VenvSite}");

            PythonEngine.Initialize();
            engineStarted = true;
            Debug.Log("[KERNEL TEARDOWN MARK] PythonEngine.Initialize OK");

            // Main releases the GIL Initialize() holds and NEVER reacquires it until the
            // worker has stopped. While joining it heartbeats — a GIL stall would freeze
            // these logs (AC#2 'heartbeat 無 stall').
            ts = PythonEngine.BeginAllowThreads();
            Debug.Log("[KERNEL TEARDOWN MARK] BeginAllowThreads OK; starting worker");

            var worker = new Thread(Worker) { IsBackground = true, Name = "KernelTeardownWorker" };
            worker.Start();
            int beats = 0;
            while (!worker.Join(500))
            {
                Debug.Log($"[KERNEL TEARDOWN HEARTBEAT] main alive, GIL-free (beat {++beats})");
                if (beats > 120) break;   // 60s cap
            }
            workerStopped = worker.Join(0);

            string err = Volatile.Read(ref _error);
            if (!workerStopped)
            {
                Debug.LogError("[KERNEL TEARDOWN FAIL] worker timeout (did not finish within 60s)");
            }
            else if (err != null)
            {
                Debug.LogError("[KERNEL TEARDOWN FAIL] " + err);
            }
            else if (Volatile.Read(ref _leakedNautilus) != 0)
            {
                Debug.LogError($"[KERNEL TEARDOWN FAIL] Rust core leaked: {_leakedNautilus} nautilus module(s) loaded — AC#2 requires the kernel process to stay Rust-core-free");
            }
            else if (!Volatile.Read(ref _verifyOk))
            {
                Debug.LogError("[KERNEL TEARDOWN FAIL] golden mismatch: " + Volatile.Read(ref _verifyMsg));
            }
            else
            {
                passed = true;
                Debug.Log("[KERNEL TEARDOWN MARK] kernel ran, golden matched, Rust core absent — now exercising teardown");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[KERNEL TEARDOWN FAIL] driver: " + e);
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
                    // THE SUBJECT OF AC#2: this Shutdown + the process exit below are
                    // exactly where the nautilus_pyo3 build crashed (s0-result §1.2/§1.3).
                    // With no Rust core loaded, this must complete cleanly.
                    PythonEngine.Shutdown();
                    Debug.Log("[KERNEL TEARDOWN MARK] PythonEngine.Shutdown OK (clean teardown)");
                }
                else if (engineStarted)
                {
                    Debug.LogWarning("[KERNEL TEARDOWN] worker did not stop; skipping Python shutdown to avoid GIL deadlock");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[KERNEL TEARDOWN] shutdown cleanup: " + e);
            }
        }

        if (passed)
            Debug.Log("[KERNEL TEARDOWN PASS] kernel ran under Unity-Mono, golden matched, Rust core absent, Shutdown clean — confirm exit 0 + NO new Unity.exe crash dump");

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // Background thread: takes the GIL only inside `using`, runs the kernel tracer
    // (Nautilus-free), verifies it against the committed golden, and asserts no Rust
    // core loaded — all via the SAME Python entry points the headless gates use.
    static void Worker()
    {
        try
        {
            using (Py.GIL())
            {
                Debug.Log("[KERNEL TEARDOWN MARK] worker GIL acquired");
                using (PyObject sys = Py.Import("sys"))
                using (PyObject sysPath = sys.GetAttr("path"))
                {
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
                }
                Debug.Log("[KERNEL TEARDOWN MARK] sys.path set; running kernel + golden verify (NO nautilus)");

                // verify_golden.verify() runs the kernel tracer and compares to the
                // committed golden; returns (ok: bool, message: str).
                using (PyObject vg = Py.Import("spike.kernel_golden.verify_golden"))
                using (PyObject res = vg.InvokeMethod("verify"))
                using (PyObject okObj = res[0])
                using (PyObject msgObj = res[1])
                {
                    Volatile.Write(ref _verifyOk, okObj.As<bool>());
                    Volatile.Write(ref _verifyMsg, msgObj.As<string>());
                }
                Debug.Log("[KERNEL TEARDOWN MARK] kernel run + verify done; checking Rust-core purity");

                // purity.leaked_nautilus_modules(sys.modules) -> list of leaked module names.
                using (PyObject sys = Py.Import("sys"))
                using (PyObject modules = sys.GetAttr("modules"))
                using (PyObject purity = Py.Import("spike.kernel_golden.purity"))
                using (PyObject leaked = purity.InvokeMethod("leaked_nautilus_modules", modules))
                {
                    Volatile.Write(ref _leakedNautilus, leaked.Length());
                }
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _error, e.ToString());
        }
    }
}
