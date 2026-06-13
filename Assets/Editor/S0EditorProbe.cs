// editor-only throwaway probe for S0 headless validation
// S0EditorProbe.cs — issue #2 S0 spike (改修B)
//
// Headless validation of the S0 core unknown: does nautilus_pyo3's Rust core
// LOAD under Unity's Mono runtime + pythonnet, and run a REAL backtest from a
// C# background thread under Py.GIL()? This probe reproduces the harness core
// WITHOUT entering playmode, so the lead can run it fully headless:
//
//   <UnityEditor> -batchmode -nographics -quit \
//       -projectPath /Users/sasac/backcast \
//       -executeMethod S0EditorProbe.Run
//
// Exit code 0 => PASS, 1 => FAIL (self-failing gate; lead judges by $?).
// This file lives under Assets/Editor/, so it is editor-only and excluded from
// player builds automatically. Throwaway: constants are duplicated from the
// harness on purpose (no shared spike infra yet).

using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class S0EditorProbe
{
    // worker -> main result (only C# primitives cross the thread boundary)
    static long   _bars;
    static long   _fills;
    static double _equity;
    static string _error;   // non-null => worker failed

    public static void Run()
    {
        bool   passed        = false;
        IntPtr ts            = IntPtr.Zero;
        bool   engineStarted = false;
        bool   workerStopped = true;

        try
        {
            // Runtime paths come from the shared OS-aware resolver
            // (PythonRuntimeLocator, #9; Windows branch added in #18) instead of
            // the original Mac-only consts. Runs on the Unity main thread (the
            // -executeMethod entry) before Initialize, so it caches the four
            // strings and the worker thread later reads cached values only.
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            Debug.Log($"[S0 PROBE MARK] configured: dll={PythonRuntimeLocator.LibPython} home={PythonRuntimeLocator.PythonHome} site={PythonRuntimeLocator.VenvSite}");

            PythonEngine.Initialize();
            engineStarted = true;
            Debug.Log("[S0 PROBE MARK] PythonEngine.Initialize OK");

            // #18 Windows-leg diagnostic C: S0_MAIN_THREAD=1 runs the backtest on
            // the Unity MAIN thread (no background worker, GIL held from Initialize)
            // to isolate whether the segfault is tied to the C# background-worker
            // thread context (pythonnet thread-state/GIL registration) vs the core
            // run itself. The production design keeps engine work OFF main; this is
            // a diagnostic only.
            if (Environment.GetEnvironmentVariable("S0_MAIN_THREAD") == "1")
            {
                Debug.Log("[S0 PROBE MARK] main-thread mode: running backtest on main thread");
                Worker();   // main holds the GIL from Initialize; Py.GIL() nests safely
                workerStopped = true;
            }
            else
            {
                // Main releases the GIL Initialize() holds; main NEVER reacquires it
                // until after the worker has stopped (mirrors the harness).
                ts = PythonEngine.BeginAllowThreads();
                Debug.Log("[S0 PROBE MARK] BeginAllowThreads OK; starting worker");

                // 64 MiB explicit stack does NOT prevent the segfault under
                // Windows-Mono (thread-stack exhaustion ruled out). Kept but immaterial.
                var worker = new Thread(Worker, 64 * 1024 * 1024) { IsBackground = true, Name = "S0ProbeWorker" };
                worker.Start();
                workerStopped = worker.Join(60000);   // main only waits; it does NOT take the GIL
            }

            string err = Volatile.Read(ref _error);
            if (!workerStopped)
            {
                Debug.LogError("[S0 PROBE FAIL] worker timeout (did not finish within 60s)");
            }
            else if (err != null)
            {
                Debug.LogError("[S0 PROBE FAIL] " + err);
            }
            else
            {
                passed = true;
                Debug.Log($"[S0 PROBE PASS] bars={_bars} fills={_fills} equity={_equity} " +
                          "(nautilus loaded & backtest ran under Unity Mono)");
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[S0 PROBE FAIL] driver: " + e);
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
                else if (engineStarted)
                {
                    // Worker still alive (timeout) -> it holds the GIL. Reacquiring it
                    // to Shutdown would deadlock; skip teardown (the editor process
                    // exits right after via EditorApplication.Exit).
                    Debug.LogWarning("[S0 PROBE] worker did not stop; skipping Python shutdown to avoid GIL deadlock");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("[S0 PROBE] shutdown cleanup: " + e);
            }
        }

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // Background thread: takes the GIL ONLY inside `using`, runs ONE real backtest
    // through the SAME explicit pythonnet PyObject API the harness uses.
    static void Worker()
    {
        try
        {
            using (Py.GIL())
            {
                Debug.Log("[S0 PROBE MARK] worker GIL acquired");
                using (PyObject sys = Py.Import("sys"))
                using (PyObject sysPath = sys.GetAttr("path"))
                {
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
                }
                Debug.Log("[S0 PROBE MARK] sys.path set; importing spike.s0_backtest (loads nautilus_pyo3)");

                using (PyObject m = Py.Import("spike.s0_backtest"))
                {
                    Debug.Log("[S0 PROBE MARK] nautilus import OK; running gates");
                    // S0 AC① core: run the self-failing pin/footer gates on THIS
                    // interpreter first; a wrong wheel raises here (-> _error) instead
                    // of SIGABRT'ing deep in Rust.
                    m.InvokeMethod("run_gates").Dispose();
                    Debug.Log("[S0 PROBE MARK] gates OK; running backtest");

                    using (PyObject r = m.InvokeMethod("run_backtest"))
                    using (PyObject barsObj = r["bars"])
                    using (PyObject fillsObj = r["fills"])
                    using (PyObject equityObj = r["final_equity"])
                    {
                        Volatile.Write(ref _bars,   barsObj.As<long>());
                        Volatile.Write(ref _fills,  fillsObj.As<long>());
                        Volatile.Write(ref _equity, equityObj.As<double>());
                    }
                }
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _error, e.ToString());
        }
    }
}
