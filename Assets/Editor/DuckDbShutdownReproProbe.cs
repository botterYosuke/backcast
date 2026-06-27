// DuckDbShutdownReproProbe.cs — issue #170 REPRODUCTION / investigation harness (findings 0126).
//
// NOT a passing regression gate: there is no clean GREEN. This probe is the committed reproducer for
// the macOS shutdown SIGSEGV and the empirical proof of which mitigations work. #170 is BLOCKED on the
// upstream duckdb bug (duckdb/duckdb#13904, #13940 — DuckDBPyConnection's shared_ptr dtor releases the
// GIL during process/interpreter shutdown without a valid threadstate; open through duckdb 1.5.3).
//
// VERDICT = PROCESS EXIT CODE (the segfault is in __cxa_finalize, after every log flushes):
//   0   = no crash      139 = SIGSEGV (0xb0 PyEval_SaveThread ← ~DuckDBPyConnection ← __cxa_finalize)
//
// Empirical matrix (env toggles below; all confirmed on duckdb 1.5.3 / py 3.13.11 in Unity batchmode):
//   no duckdb                                              → exit 0   (clean)
//   duckdb, no restore                          (RED)      → exit 139  (the production crash signature)
//   duckdb + EndAllowThreads restore  (issue "本命")        → exit 139  (re-attach does NOT survive to finalize)
//   duckdb + EndAllowThreads + PythonEngine.Shutdown()     → exit 139  (Py_Finalize doesn't reach the C++ static)
//   duckdb connect()+close() (production pattern)          → exit 139  (residual is duckdb-internal, not the py obj)
//   duckdb + os._exit(0)                                   → exit 0   (ONLY fix: skip __cxa_finalize entirely)
//
// Run:  <Unity> -batchmode -nographics -quit -projectPath <abs> \
//          -executeMethod DuckDbShutdownReproProbe.Run -logFile <abs>
// Env toggles: BACKCAST_170_SCENARIO=default|default_closed|connect_close|connect_leak  (default: default)
//              BACKCAST_170_DUCKDB=0   (CONTROL: no residual)   BACKCAST_170_RESTORE=0  (skip EndAllowThreads)
//              BACKCAST_170_SHUTDOWN=1 (PythonEngine.Shutdown)  BACKCAST_170_OSEXIT=1   (os._exit bypass)
using System;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class DuckDbShutdownReproProbe
{
    public static void Run()
    {
        bool restore = Environment.GetEnvironmentVariable("BACKCAST_170_RESTORE") != "0";
        bool useDuckDb = Environment.GetEnvironmentVariable("BACKCAST_170_DUCKDB") != "0";
        Debug.Log($"[E2E DUCKDB-SHUTDOWN] start; restore={restore} duckdb={useDuckDb} (RED witness when restore=false)");

        IntPtr ts = IntPtr.Zero;
        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            ts = PythonEngine.BeginAllowThreads();   // main GIL-free for the process (InitializePython parity)

            // BACKCAST_170_SCENARIO selects how the residual is created (diagnosis matrix):
            //   default       : duckdb.execute("SELECT 1")            (module default connection, leaked)
            //   default_closed: + duckdb.default_connection().close() (does closing no-op the dtor?)
            //   connect_close : con=duckdb.connect(); con.close()     (PRODUCTION pattern, local)
            //   connect_leak  : builtins._bc=duckdb.connect()         (explicit conn held to finalize)
            string scenario = useDuckDb ? (Environment.GetEnvironmentVariable("BACKCAST_170_SCENARIO") ?? "default") : "none";
            if (scenario != "none")
            {
                string py;
                switch (scenario)
                {
                    case "default_closed": py = "import duckdb\nduckdb.execute('SELECT 1')\nduckdb.default_connection().close()\n"; break;
                    case "connect_close":  py = "import duckdb\nc=duckdb.connect()\nc.execute('SELECT 1')\nc.close()\n"; break;
                    case "connect_leak":   py = "import duckdb,builtins\nbuiltins._bc170=duckdb.connect()\nbuiltins._bc170.execute('SELECT 1')\n"; break;
                    default:               py = "import duckdb\nduckdb.execute('SELECT 1')\n"; break;
                }
                string err = null;
                var worker = new Thread(() =>
                {
                    try
                    {
                        using (Py.GIL())
                        {
                            using (PyObject sys = Py.Import("sys"))
                            using (PyObject sp = sys.GetAttr("path"))
                                sp.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
                            PythonEngine.Exec(py);
                        }
                    }
                    catch (Exception e) { err = e.ToString(); }
                }) { IsBackground = true, Name = "Issue170DuckDbWorker" };
                worker.Start();
                if (!worker.Join(30000)) { Debug.LogError("[E2E DUCKDB-SHUTDOWN FAIL] duckdb worker did not join"); EditorApplication.Exit(1); return; }
                if (err != null) { Debug.LogError("[E2E DUCKDB-SHUTDOWN FAIL] duckdb worker error: " + err); EditorApplication.Exit(1); return; }
                Debug.Log($"[E2E DUCKDB-SHUTDOWN] residual scenario='{scenario}' materialized on worker; main GIL-free");
            }
            else
            {
                Debug.Log("[E2E DUCKDB-SHUTDOWN] CONTROL: no duckdb residual created");
            }

            if (restore)
            {
                // Re-attach the saved threadstate to main (EndAllowThreads). NOTE (findings 0126): this is
                // the issue's "本命" — and it does NOT fix the crash: by __cxa_finalize the threadstate is
                // detached again (Mono teardown ran first), so the duckdb dtor still NULL-derefs. Kept here
                // only as the GIL-holding precondition for the Shutdown experiment below.
                PythonEngine.EndAllowThreads(ts);
                Debug.Log("[E2E DUCKDB-SHUTDOWN] main threadstate restored (EndAllowThreads); main holds GIL");
            }
            else
            {
                Debug.Log("[E2E DUCKDB-SHUTDOWN] RED witness: main threadstate NOT restored");
            }

            if (Environment.GetEnvironmentVariable("BACKCAST_170_SHUTDOWN") == "1")
            {
                // Py_Finalize now (valid threadstate). Empirically STILL crashes: the residual is duckdb's
                // C++-static connection/instance-cache, destroyed at __cxa_finalize beyond Py_Finalize's reach.
                PythonEngine.Shutdown();
                Debug.Log("[E2E DUCKDB-SHUTDOWN] PythonEngine.Shutdown() done");
            }

            if (Environment.GetEnvironmentVariable("BACKCAST_170_OSEXIT") == "1")
            {
                // The ONLY mitigation that works: terminate via os._exit(0) so __cxa_finalize never runs →
                // duckdb's C++-static dtor never executes → no PyEval_SaveThread crash. (issue's "代替"; hacky.)
                Debug.Log("[E2E DUCKDB-SHUTDOWN] os._exit(0): bypassing __cxa_finalize (no duckdb dtor)");
                using (Py.GIL())
                using (PyObject os = Py.Import("os"))
                    os.InvokeMethod("_exit", new PyInt(0));
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[E2E DUCKDB-SHUTDOWN FAIL] driver: " + e);
            EditorApplication.Exit(1);
            return;
        }

        // exit(0): with a duckdb residual present and no os._exit bypass, the dtor segfaults here at
        // __cxa_finalize (exit 139); the verdict is the resulting process exit code.
        EditorApplication.Exit(0);
    }
}
