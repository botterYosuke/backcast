// ExecutorOrphanAbsenceAssert.cs — issue #33 build-leg orphan-absence sanity check.
//
// ADR-0001 decision 3 says the EXECUTOR (Python order pump in the embedded engine)
// cannot orphan: it lives on a daemon thread in the same PID as the host, so when the
// host dies the executor dies with it. In the Editor this is verified by existing
// probes (findings 0001 / 0013). #33 carries the same invariant into the deploy leg
// — the asset bundle layout (StreamingAssets/PythonRuntime/{cpython, python}) doesn't
// change the embedding shape, but a misconfigured Locator could.
//
// This assert runs right after PythonEngine.Initialize() + BeginAllowThreads() to
// catch a misconfigured deploy (e.g. PYTHONHOME pointing somewhere unexpected) BEFORE
// the host starts orchestrating Replay/Live runs. It is the literal-text test of
// ADR-0001 d3 the build leg AC asks for.
//
// Scope (#33 grill 2026-06-18):
//   * In scope: executor PID identity + no multiprocessing children at startup. This
//     verifies the in-proc embedding is real for the bundled venv.
//   * NOT in scope: login subprocess hygiene — moot since #122 removed the login subprocess
//     (the dialog now runs in-process on a dedicated thread; findings 0093). The old #82
//     host-crash hygiene for that subprocess no longer applies.

using System;
using System.Diagnostics;
using Python.Runtime;

public static class ExecutorOrphanAbsenceAssert
{
    // Call ONCE after PythonEngine.Initialize() + BeginAllowThreads(), on main, before
    // any engine work runs. Throws InvalidOperationException with an actionable message
    // when the embedding is not in-proc, so a deploy bug surfaces here instead of
    // hours later as silent venue-side or risk-side strangeness.
    public static void AssertInProcParity()
    {
        int hostPid = Process.GetCurrentProcess().Id;
        int pyPid;
        int activeChildren;

        using (Py.GIL())
        using (PyObject os = Py.Import("os"))
        using (PyObject mp = Py.Import("multiprocessing"))
        {
            using (PyObject pidObj = os.InvokeMethod("getpid"))
                pyPid = pidObj.As<int>();
            using (PyObject children = mp.InvokeMethod("active_children"))
                activeChildren = (int)children.Length();
        }

        if (pyPid != hostPid)
            throw new InvalidOperationException(
                $"ADR-0001 d3 violated: embedded Python PID ({pyPid}) != host PID ({hostPid}). " +
                "Python is not in-proc with the host — the bundled runtime is misconfigured " +
                "(check PythonRuntimeLocator PYTHONHOME / libpython resolution).");

        if (activeChildren != 0)
            throw new InvalidOperationException(
                $"ADR-0001 d3 violated: multiprocessing.active_children() == {activeChildren} at startup. " +
                "An executor subprocess exists, which the in-proc embedding forbids. " +
                "Check for residual children from a prior run (orphan from host crash?) or a " +
                "module that spawned mp workers on import.");
    }
}
