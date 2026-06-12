// S0SpikeHarness.cs — issue #2 S0 spike (step 8)
// Verifies the S0 invariant: Nautilus loads under Unity Mono + pythonnet, a C#
// background thread runs a REAL backtest under Py.GIL() in a loop, while the
// main thread keeps rendering >=300 frames WITHOUT ever taking the GIL.
//
// SPIKE NOTE: absolute paths are hardcoded constants below. This component is
// run once by the owner in playmode for S0. Step1 #3 will relativize these via
// StreamingAssets — do NOT generalize here.
//
// TURNKEY (改修A): an auto-bootstrap [RuntimeInitializeOnLoadMethod] spawns this
// component automatically — the owner just opens SampleScene and presses Play.
// No manual GameObject creation / AddComponent is needed.
//
// pythonnet 3.1.0 API used (verified against the shipped Python.Runtime.dll
// [asm 3.1.0.0] and the v3.1.0 source):
//   Python.Runtime.Runtime.PythonDLL            (static set; Runtime.cs:24)
//   PythonEngine.PythonHome                     (set -> Py_SetPythonHome; PythonEngine.cs:103)
//   PythonEngine.Initialize()                   (PythonEngine.cs:181)
//   PythonEngine.BeginAllowThreads() -> IntPtr  (PythonEngine.cs:508)
//   PythonEngine.EndAllowThreads(IntPtr)        (PythonEngine.cs:524)
//   PythonEngine.Shutdown()                     (PythonEngine.cs:368)
//   Py.GIL() -> IDisposable GILState            (Py.cs:13)
//   Py.Import(string) -> PyObject               (Py.cs:111)
//   PyObject.GetAttr(string) -> PyObject        (PyObject.cs:339)
//   PyObject.InvokeMethod(string, params PyObject[]) -> PyObject (PyObject.cs:818)
//   PyObject.this[string] -> PyObject (dict GetItem)            (PyObject.cs:700)
//   PyObject.As<T>() -> T                        (PyObject.cs:184)
//   PyInt(int) / PyString(string) arg wrappers
// NOTE: NO `dynamic` is used. `dynamic` requires Microsoft.CSharp.RuntimeBinder,
// which this Unity 6000.4.11f1 + apiCompat=6 Mono toolchain does NOT provide
// (CS0656). All Python access goes through pythonnet's explicit PyObject API.
//
// Path strategy: we DELIBERATELY do not set PythonEngine.PythonPath — its setter
// calls Py_SetPath, which REPLACES the entire module search path and would hide
// the stdlib. Stdlib is located via PythonHome; the venv site-packages (8-byte
// nautilus) + project root are added with sys.path.insert inside the worker.

using System;
using System.IO;
using System.Threading;
using UnityEngine;
using Python.Runtime;

public class S0SpikeHarness : MonoBehaviour
{
    // --- hardcoded spike paths (Step1 #3: relativize via StreamingAssets) ---
    const string LIBPYTHON    = "/Users/sasac/.local/share/uv/python/cpython-3.13.13-macos-x86_64-none/lib/libpython3.13.dylib";
    const string PYTHONHOME   = "/Users/sasac/.local/share/uv/python/cpython-3.13.13-macos-x86_64-none";
    const string VENV_SITE    = "/Users/sasac/backcast/python/.venv/lib/python3.13/site-packages";
    const string PROJECT_ROOT = "/Users/sasac/backcast/python";
    const int    TARGET_FRAMES = 300;

    // --- cross-thread state: ONLY C# primitives cross the boundary (never a PyObject) ---
    int    _mainFrameCount;   // main increments (Interlocked); worker reads (Volatile)
    long   _lastBars;         // worker writes; main reads
    long   _lastFills;
    double _lastEquity;
    int    _runCount;         // worker increments after each completed backtest
    bool   _workerDone;
    string _workerError;      // non-null => failure (exception / crash on worker)
    bool   _stopRequested;    // OnDestroy => true; worker honors it for early playmode stop

    Thread _worker;
    IntPtr _threadState;
    bool   _engineStarted;
    bool   _resultLogged;

    // TURNKEY auto-bootstrap (改修A): spawn this harness automatically on play so
    // the owner only has to press Play. Runs in player & playmode (incl. batchmode
    // -nographics playmode). Does NOT fire under `-executeMethod` (no playmode is
    // entered) so it never collides with the S0EditorProbe headless probe.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootstrap()
    {
        var go = new GameObject("S0Spike");
        DontDestroyOnLoad(go);
        go.AddComponent<S0SpikeHarness>();
    }

    void Start()
    {
        try
        {
            Python.Runtime.Runtime.PythonDLL = LIBPYTHON;

            // PYTHONHOME locates the stdlib; PYTHONPATH is PREPENDED by CPython init
            // (non-clobbering, unlike Py_SetPath) so the venv + project also resolve.
            Environment.SetEnvironmentVariable("PYTHONHOME", PYTHONHOME);
            Environment.SetEnvironmentVariable("PYTHONPATH", VENV_SITE + Path.PathSeparator + PROJECT_ROOT);
            PythonEngine.PythonHome = PYTHONHOME;

            PythonEngine.Initialize();
            _engineStarted = true;

            // Release the GIL that Initialize() holds on the main thread, so the worker
            // can take it. Main NEVER reacquires the GIL after this point.
            _threadState = PythonEngine.BeginAllowThreads();

            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "S0Worker" };
            _worker.Start();

            Debug.Log("[S0] PythonEngine.Initialize OK; worker started; main proceeds GIL-free.");
        }
        catch (Exception e)
        {
            Volatile.Write(ref _workerError, "init: " + e);
            Debug.LogError("S0 FAIL (init): " + e);
        }
    }

    // Background thread: takes the GIL ONLY inside `using`, runs real backtests in a loop.
    void WorkerLoop()
    {
        try
        {
            // One-time: make the venv (8-byte nautilus) + spike module importable.
            // sys.path.insert is bulletproof regardless of how Initialize computed paths.
            using (Py.GIL())
            using (PyObject sys = Py.Import("sys"))
            using (PyObject sysPath = sys.GetAttr("path"))
            {
                sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PROJECT_ROOT)).Dispose();
                sysPath.InvokeMethod("insert", new PyInt(0), new PyString(VENV_SITE)).Dispose();
            }

            // S0 AC① core: run the self-failing pin/footer gates ONCE on THIS
            // interpreter before any backtest. A wrong (16-byte) wheel raises a
            // Python exception here — caught below -> _workerError -> Update logs
            // S0 FAIL — instead of SIGABRT'ing deep in Rust.
            using (Py.GIL())
            using (PyObject gateMod = Py.Import("spike.s0_backtest"))
            {
                gateMod.InvokeMethod("run_gates").Dispose();
            }

            while (!Volatile.Read(ref _stopRequested) &&
                   Volatile.Read(ref _mainFrameCount) < TARGET_FRAMES)
            {
                using (Py.GIL())
                using (PyObject m = Py.Import("spike.s0_backtest"))
                using (PyObject r = m.InvokeMethod("run_backtest"))
                using (PyObject barsObj = r["bars"])
                using (PyObject fillsObj = r["fills"])
                using (PyObject equityObj = r["final_equity"])
                {
                    Volatile.Write(ref _lastBars, barsObj.As<long>());
                    Volatile.Write(ref _lastFills, fillsObj.As<long>());
                    Volatile.Write(ref _lastEquity, equityObj.As<double>());
                }   // GIL + all PyObjects released here -> main is never blocked
                Interlocked.Increment(ref _runCount);
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _workerError, e.ToString());
        }
        finally
        {
            Volatile.Write(ref _workerDone, true);
        }
    }

    // Main thread: counts frames and reads C# primitives WITHOUT ever touching the GIL.
    void Update()
    {
        int f = Interlocked.Increment(ref _mainFrameCount);

        if (f % 50 == 0)
        {
            Debug.Log($"[S0] frame={f} runs={Volatile.Read(ref _runCount)} " +
                      $"lastBars={Volatile.Read(ref _lastBars)} " +
                      $"lastFills={Volatile.Read(ref _lastFills)} " +
                      $"lastEquity={Volatile.Read(ref _lastEquity)}");
        }

        if (_resultLogged) return;

        string err = Volatile.Read(ref _workerError);
        if (err != null)
        {
            Debug.LogError("S0 FAIL: " + err);
            _resultLogged = true;
            return;
        }

        if (Volatile.Read(ref _workerDone) && f >= TARGET_FRAMES && Volatile.Read(ref _runCount) > 0)
        {
            Debug.Log($"S0 PASS: frames={f} runs={Volatile.Read(ref _runCount)} " +
                      $"bars={Volatile.Read(ref _lastBars)} equity={Volatile.Read(ref _lastEquity)}");
            _resultLogged = true;
        }
    }

    void OnDestroy()
    {
        try
        {
            // Stop the worker first, THEN shut Python down (safe ordering).
            Volatile.Write(ref _stopRequested, true);
            bool workerStopped = true;
            if (_worker != null && _worker.IsAlive)
            {
                // Join returns true if the thread terminated, false on timeout.
                workerStopped = _worker.Join(5000);   // let an in-flight backtest finish & release the GIL
            }

            if (!workerStopped)
            {
                // Worker is still alive mid-backtest, holding the GIL. Reacquiring the
                // GIL on main (EndAllowThreads) to Shutdown would block forever ->
                // deadlock / Unity hang. Skip Python teardown; playmode exit tears the
                // process down anyway (the leak is acceptable for this spike).
                Debug.LogWarning("S0 OnDestroy: worker did not stop in time; skipping Python shutdown to avoid GIL deadlock");
            }
            else if (_engineStarted)
            {
                // Reacquire the GIL on main (restore the saved thread state) before shutdown.
                if (_threadState != IntPtr.Zero)
                {
                    PythonEngine.EndAllowThreads(_threadState);
                }
                PythonEngine.Shutdown();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("S0 OnDestroy cleanup: " + e);
        }
    }
}
