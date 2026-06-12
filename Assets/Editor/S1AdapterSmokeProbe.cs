// editor-only throwaway probe for S1 (#9) M2 — headless Mono callback gate.
// S1AdapterSmokeProbe.cs
//
// Proves the S1 core unknown that S0 did NOT exercise: the REVERSE callback.
// A C# ReplayEventSink is handed to the production Python replay seam
// (engine.inproc_server.InprocLiveServer.start_nautilus_replay), and the
// Nautilus backtest — running on a Python DAEMON thread under Unity's Mono
// runtime — duck-calls sink.push_bar(str) for every bar. This mirrors the
// pure-CPython gate python/spike/s1_adapter_smoke.py, but under Mono.
//
//   <UnityEditor> -batchmode -nographics \
//       -projectPath /Users/sasac/backcast \
//       -executeMethod S1AdapterSmokeProbe.Run \
//       -logFile <path>
//
// Exit code 0 => PASS, 1 => FAIL (self-failing gate; lead judges by $?).
// Prints `[ADAPTER SMOKE PASS] pushed=N drained=N parsed_ok` to Console/logfile.
//
// THREADING (the key S1 design point):
//   * main:   Initialize -> BeginAllowThreads (release main GIL, never reacquire)
//             -> launch a LAUNCHER thread -> Join it (it returns fast) -> then
//             poll the sink GIL-free until Completed/Failed/timeout -> drain.
//   * launcher: takes Py.GIL(), imports engine, builds cfg with rust_sink = the
//             C# ReplayEventSink, calls start_nautilus_replay (which spawns the
//             Python daemon backtest thread and RETURNS immediately), verifies
//             success, then RELEASES the GIL by exiting the using block.
//   * daemon: the Python backtest thread (created inside start_nautilus_replay)
//             acquires the GIL on its own, streams bars, and calls push_bar on
//             the C# sink. push_bar only Enqueue+Interlocked (never blocks/throws),
//             so it holds the GIL only briefly per bar.
//   The main thread MUST stay GIL-free the whole time so the daemon can run.
//
// Throwaway: cfg/fixture consts are duplicated from S0EditorProbe on purpose; the
// runtime PATHS now come from PythonRuntimeLocator (M3 move 2 adopted the shared
// resolver — see Assets/Scripts/S1Spike/PythonRuntimeLocator.cs).

using System;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;

public static class S1AdapterSmokeProbe
{
    // Runtime paths (libpython / PYTHONHOME / venv site-packages / project root) now
    // come from PythonRuntimeLocator (M3 move 2) instead of inline consts.

    // cfg fixture (mirrors python/spike/s1_adapter_smoke.py).
    const string STRATEGY_FILE = "/Users/sasac/backcast/python/spike/fixtures/strategies/spike_bar_consumer.py";
    const string CATALOG_PATH  = "/Users/sasac/backcast/python/spike/fixtures/jquants-catalog";
    const string INSTRUMENT    = "8918.TSE";
    const string START_DATE    = "2024-10-01";
    const string END_DATE      = "2025-01-10";
    const string GRANULARITY   = "Daily";
    const long   INITIAL_CASH  = 10_000_000;

    // 68 daily bars at 0.1s/bar (~7s) + Mono cold nautilus import; wait generously.
    const double WAIT_TIMEOUT_S = 60.0;

    // launcher -> main: non-null => the launcher failed (only a C# string crosses).
    static string _startError;

    // The C# sink instance; constructed on main, drained on main, pushed-to by the
    // Python daemon thread under the GIL.
    static ReplayEventSink _sink;

    public static void Run()
    {
        bool passed        = false;
        bool engineStarted = false;

        try
        {
            // M3: one call replaces the 4 inline lines (PythonDLL / PYTHONHOME env /
            // PYTHONPATH env / PythonEngine.PythonHome). Runs on the main thread here
            // (Run() is the -executeMethod entry), so it also forces main-thread path
            // resolution + caching before the launcher thread reads the properties.
            PythonRuntimeLocator.ConfigureBeforeInitialize();

            PythonEngine.Initialize();
            engineStarted = true;

            // Release the GIL Initialize() holds on main and NEVER reacquire it: the
            // launcher takes it briefly, then the Python daemon backtest thread needs
            // it free to run + call push_bar. (Return value intentionally discarded —
            // this throwaway gate Exit()s the process, so no EndAllowThreads.)
            PythonEngine.BeginAllowThreads();

            _sink = new ReplayEventSink();

            var launcher = new Thread(Launcher) { IsBackground = true, Name = "S1ProbeLauncher" };
            launcher.Start();
            bool launcherStopped = launcher.Join(60000); // returns fast once the daemon is spawned

            string startErr = Volatile.Read(ref _startError);
            if (!launcherStopped)
            {
                Debug.LogError("[ADAPTER SMOKE FAIL] launcher thread did not return within 60s (import/start hung under Mono)");
            }
            else if (startErr != null)
            {
                Debug.LogError("[ADAPTER SMOKE FAIL] " + startErr);
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
                    Debug.LogError("[ADAPTER SMOKE FAIL] push_run_failed: " + _sink.Error);
                }
                else if (!_sink.Completed)
                {
                    Debug.LogError($"[ADAPTER SMOKE FAIL] timed out after {WAIT_TIMEOUT_S:0}s " +
                                   $"waiting for run completion (pushed so far={_sink.Pushed})");
                }
                else
                {
                    // push_run_complete is the LAST callback (after every push_bar),
                    // so once Completed it is safe to drain on main, GIL-free.
                    long pushed = _sink.Pushed;
                    long drained = 0;
                    int  parseFailures = 0;
                    string firstBad = null;
                    while (_sink.TryDequeueBar(out string payload))
                    {
                        if (LooksLikeBarJson(payload))
                        {
                            drained++;
                        }
                        else
                        {
                            parseFailures++;
                            if (firstBad == null) firstBad = Describe(payload);
                        }
                    }

                    if (parseFailures > 0)
                    {
                        Debug.LogError($"[ADAPTER SMOKE FAIL] {parseFailures} payload(s) failed parse check; first={firstBad}");
                    }
                    else if (pushed <= 0)
                    {
                        Debug.LogError("[ADAPTER SMOKE FAIL] no bars pushed (pushed=0) — replay produced no push_bar callbacks");
                    }
                    else if (pushed != drained)
                    {
                        Debug.LogError($"[ADAPTER SMOKE FAIL] pushed={pushed} != drained={drained}");
                    }
                    else
                    {
                        passed = true;
                        Debug.Log($"[ADAPTER SMOKE PASS] pushed={pushed} drained={drained} parsed_ok " +
                                  "(Python->C# push_bar callback ran under Unity Mono)");
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[ADAPTER SMOKE FAIL] driver: " + e);
        }
        finally
        {
            // The Python daemon backtest thread's lifecycle is nondeterministic
            // relative to here; reacquiring the GIL to Shutdown could deadlock
            // against it. This throwaway gate Exit()s immediately (OS reclaims the
            // interpreter), so skip EndAllowThreads/Shutdown — mirrors the S0 probe's
            // timeout-branch rationale.
            if (engineStarted)
            {
                Debug.Log("[ADAPTER SMOKE] skipping Python shutdown (daemon lifecycle nondeterministic; process exits next)");
            }
        }

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // Launcher thread: takes the GIL, drives the production replay seam exactly like
    // python/spike/s1_adapter_smoke.py (DataEngine + InprocLiveServer.start_nautilus_replay),
    // then RELEASES the GIL by exiting the using block so the daemon can run.
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

                    // Orphan-free gate (S1 #9 AC; ADR-0001 d3 "Unity が死ねば同一プロセスの
                    // Python も即死, orphan は構造的に存在し得ない"). The Replay worker spawned
                    // by start_nautilus_replay MUST be a Python *daemon* thread, so it can never
                    // block process exit when Unity quits (no orphan / leftover thread). We check
                    // here, GIL held, while the worker is freshly started and alive (68 bars @
                    // ~0.1s/bar => it cannot have finished yet). If the engine ever flips
                    // daemon=True -> daemon=False this assertion fails the smoke gate (exit 1).
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
                                Debug.Log("[ADAPTER SMOKE] orphan-free OK: 'backtest-runner' is a daemon thread " +
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

    // Cheap "is this real bar JSON" proxy (no JSON lib under this Mono toolchain).
    // The bar payload is json.dumps({price,timestamp,timestamp_ms,history,ohlc_points,
    // per_instrument}) from engine.live.gui_bridge_actor; an empty / truncated /
    // non-object payload fails this check.
    static bool LooksLikeBarJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        string t = s.Trim();
        if (t.Length < 2 || t[0] != '{' || t[t.Length - 1] != '}') return false;
        return t.Contains("\"timestamp_ms\"") && t.Contains("\"ohlc_points\"");
    }

    static string Describe(string s)
    {
        if (s == null) return "<null>";
        string head = s.Length > 60 ? s.Substring(0, 60) + "..." : s;
        return $"len={s.Length} [{head}]";
    }
}
