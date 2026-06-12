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
    const string LIBPYTHON    = "/Users/sasac/.local/share/uv/python/cpython-3.13.13-macos-x86_64-none/lib/libpython3.13.dylib";
    const string PYTHONHOME   = "/Users/sasac/.local/share/uv/python/cpython-3.13.13-macos-x86_64-none";
    const string VENV_SITE    = "/Users/sasac/backcast/python/.venv/lib/python3.13/site-packages";
    const string PROJECT_ROOT = "/Users/sasac/backcast/python";

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
            Python.Runtime.Runtime.PythonDLL = LIBPYTHON;
            Environment.SetEnvironmentVariable("PYTHONHOME", PYTHONHOME);
            Environment.SetEnvironmentVariable("PYTHONPATH", VENV_SITE + Path.PathSeparator + PROJECT_ROOT);
            PythonEngine.PythonHome = PYTHONHOME;

            PythonEngine.Initialize();
            engineStarted = true;

            // Main releases the GIL Initialize() holds; main NEVER reacquires it
            // until after the worker has stopped (mirrors the harness).
            ts = PythonEngine.BeginAllowThreads();

            var worker = new Thread(Worker) { IsBackground = true, Name = "S0ProbeWorker" };
            worker.Start();
            workerStopped = worker.Join(60000);   // main only waits; it does NOT take the GIL

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
                using (PyObject sys = Py.Import("sys"))
                using (PyObject sysPath = sys.GetAttr("path"))
                {
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PROJECT_ROOT)).Dispose();
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(VENV_SITE)).Dispose();
                }

                using (PyObject m = Py.Import("spike.s0_backtest"))
                {
                    // S0 AC① core: run the self-failing pin/footer gates on THIS
                    // interpreter first; a wrong wheel raises here (-> _error) instead
                    // of SIGABRT'ing deep in Rust.
                    m.InvokeMethod("run_gates").Dispose();

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
