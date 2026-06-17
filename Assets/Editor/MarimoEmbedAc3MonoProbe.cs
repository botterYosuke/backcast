// MarimoEmbedAc3MonoProbe.cs — editor-only throwaway probe for #76 AC3 (Mono leg).
//
// The genuinely-NEW risk beyond S0 (#2) / S2 (#7): does marimo's WHOLE import graph
// — now including a Rust extension (loro), msgspec, pydantic-core, starlette — LOAD
// under Unity Mono + pythonnet, and can ONE embed reactive drain RUN, without
// crashing Mono or stalling the frame? S2 owns the GIL ping-pong band (findings
// 0005 §8.1); this leg adds ONLY the marimo load+run (spike-0 residual (a)).
//
//   <UnityEditor> -batchmode -nographics \
//       -projectPath /Users/sasac/backcast \
//       -executeMethod MarimoEmbedAc3MonoProbe.Run \
//       -logFile <path>
//
// Exit 0 => PASS, 1 => FAIL. Prints `[MARIMO-EMBED AC3 MONO PASS] ...`.
//
// It drives python/spike/marimo_embed/mono_smoke.py:run_one_drain — the SAME
// headless-context + mo.state drain the CPython legs (ac1/ac2/ac3_async) exercise,
// so a Mono-only failure isolates the fault to the pythonnet/Mono load seam
// (mirrors S1/S2 CPython-smoke + Mono-probe split, #9 / #7).
//
// THREADING (minimal, S2-derived):
//   * main: Initialize -> BeginAllowThreads (release main GIL, never reacquire) ->
//           spawn W1 -> run a GIL-FREE heartbeat the whole time, asserting it never
//           stalls beyond MAX_STALL_MS (headless proxy for "frame keeps ticking").
//   * W1:   Py.GIL() -> insert sys.path (project root, venv site, marimo fork) ->
//           import the smoke module -> run_one_drain(close) once -> publish result.
// Python shutdown is skipped (GIL never reacquired on main; process Exit()s next) —
// mirrors S0/S1/S2 probe rationale.

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class MarimoEmbedAc3MonoProbe
{
    const string MODULE = "spike.marimo_embed.mono_smoke";

    const double CLOSE        = 123.5;          // run_one_drain input
    const double EXPECTED     = 2.0 * 123.5 + 1.0;  // mirror mono_smoke.expected()
    const long   MAX_STALL_MS = 200;            // main heartbeat must never stall beyond this
    const int    W1_BUDGET_MS = 120000;         // marimo import+drain can be cold; generous

    static volatile string _w1Error;
    static volatile bool   _w1Done;
    static double _w1Result = double.NaN;
    static double _w1ImportS;
    static double _w1DrainS;

    public static void Run()
    {
        bool passed = false;
        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            PythonEngine.BeginAllowThreads();  // main stays GIL-free; never reacquired

            var w1 = new Thread(DrainOnce) { IsBackground = true, Name = "MarimoAc3W1" };
            w1.Start();

            long maxStallMs = HeartbeatUntil(() => _w1Done, W1_BUDGET_MS);
            bool joined = w1.Join(5000);

            if (!joined)
            {
                Debug.LogError($"[MARIMO-EMBED AC3 MONO FAIL] worker did not return within {W1_BUDGET_MS}ms "
                               + "(marimo import or drain hung under Mono)");
                EditorApplication.Exit(1);
                return;
            }
            if (_w1Error != null)
            {
                Debug.LogError($"[MARIMO-EMBED AC3 MONO FAIL] worker error: {_w1Error}");
                EditorApplication.Exit(1);
                return;
            }
            if (Math.Abs(_w1Result - EXPECTED) > 1e-9)
            {
                Debug.LogError($"[MARIMO-EMBED AC3 MONO FAIL] drain result {_w1Result} != expected {EXPECTED}");
                EditorApplication.Exit(1);
                return;
            }
            if (maxStallMs > MAX_STALL_MS)
            {
                Debug.LogError($"[MARIMO-EMBED AC3 MONO FAIL] main heartbeat stalled {maxStallMs}ms "
                               + $"> {MAX_STALL_MS}ms (frame would hitch)");
                EditorApplication.Exit(1);
                return;
            }

            passed = true;
            Debug.Log($"[MARIMO-EMBED AC3 MONO PASS] marimo import+drain under Mono OK: "
                      + $"result={_w1Result} (expected {EXPECTED}), import={_w1ImportS:F2}s, "
                      + $"drain={_w1DrainS:F3}s, main maxStall={maxStallMs}ms (<= {MAX_STALL_MS}ms)");
        }
        catch (Exception e)
        {
            Debug.LogError($"[MARIMO-EMBED AC3 MONO FAIL] unhandled: {e}");
        }
        finally
        {
            EditorApplication.Exit(passed ? 0 : 1);
        }
    }

    static void DrainOnce()
    {
        try
        {
            using (Py.GIL())
            {
                // NOTE (S3/ADR-0012): marimo is now a REGULAR prod dependency (PyPI, in the
                // venv site-packages), not the editable fork this AC3 spike probe was taken
                // against — so this fork sys.path injection is vestigial. Kept as frozen spike
                // evidence; below reflects the spike-time setup.
                // marimo is installed EDITABLE from the fork (pyproject [tool.uv.sources]);
                // it is reachable in the venv only via a .pth that Mono's PYTHONPATH wiring
                // does not process, so we put the fork on sys.path directly. DERIVED from the
                // project layout (ProjectRoot=<repo>/python => fork=<repo>/../marimo), matching
                // PythonRuntimeLocator's "moving the repo cannot stale the dev paths" rule.
                string marimoFork = Path.GetFullPath(
                    Path.Combine(PythonRuntimeLocator.ProjectRoot, "..", "..", "marimo"));
                using (PyObject sys = Py.Import("sys"))
                using (PyObject sysPath = sys.GetAttr("path"))
                {
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(marimoFork)).Dispose();
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
                    sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
                }

                var swImport = Stopwatch.StartNew();
                PyObject mod = Py.Import(MODULE);   // <- marimo graph (incl. loro Rust ext) loads HERE
                swImport.Stop();
                _w1ImportS = swImport.Elapsed.TotalSeconds;

                var swDrain = Stopwatch.StartNew();
                using (PyObject fn = mod.GetAttr("run_one_drain"))
                using (PyObject r = fn.Invoke(new PyFloat(CLOSE)))
                {
                    swDrain.Stop();
                    _w1DrainS = swDrain.Elapsed.TotalSeconds;
                    _w1Result = r.As<double>();
                }
                mod.Dispose();
            }
        }
        catch (Exception e)
        {
            _w1Error = e.ToString();
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
