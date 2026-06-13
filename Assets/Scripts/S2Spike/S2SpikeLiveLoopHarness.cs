// S2SpikeLiveLoopHarness.cs — issue #7 S2-spike, the RENDER leg (default-disabled).
//
// The headless S2SpikeLiveLoopProbe gives the AFK verdict on the asyncio marshal /
// shutdown seam, but a -batchmode -nographics C# heartbeat only proves a C# loop
// advances — it does NOT exercise Unity's real Update()/PlayerLoop/frame cadence.
// This playmode harness closes that gap: while a C# worker continuously marshals
// work + orders into the engine-owned live loop (and a sub-thread pushes ticks),
// the main thread renders real frames and asserts a steady cadence (≥300 frames,
// post-warmup deltaTime hitch budget), NEVER taking the GIL. issue #7 is fully
// GREEN only after this playmode PASS (the headless probe covers the threading/
// shutdown seam for AFK runs).
//
// PLAY OWNERSHIP (exclusive re-run procedure, NOT a collision): only ONE harness
// may own Play because two unguarded auto-bootstraps would both call
// PythonEngine.Initialize() and race. ReplayPanelsHarness owns Play by default;
// to measure the S2-spike render leg, the owner flips ReplayPanelsHarness's
// auto-bootstrap OFF and AutoBootstrapEnabled below ON, presses Play, reads
// `[S2-SPIKE LIVE LOOP PASS]`, then restores. Same温存 method as S0SpikeHarness.
//
// Shutdown ordering (AC(b)) is modeled on quit: the WORKER runs graceful_stop
// (decision-6 cancel -> stop) + teardown_loop before releasing the GIL; main only
// joins it (~10s) and THEN finalizes the runtime — never blocking in Python.

using System;
using System.Diagnostics;
using System.Threading;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public class S2SpikeLiveLoopHarness : MonoBehaviour
{
    const string MODULE        = "spike.s2spike_live_loop";
    const int    TARGET_FRAMES = 300;
    const int    WARMUP_FRAMES = 60;     // ignore cold-start / nautilus-free warmup frames
    const double HITCH_S       = 0.1;    // a post-warmup frame longer than this counts as a hitch
    const double MAX_HITCH_S   = 0.2;    // PASS budget: no single post-warmup frame may exceed this
    const int    MAX_HITCHES   = 5;      // PASS budget: at most this many post-warmup hitches (>HITCH_S)
    const int    ORDER_EVERY   = 5;      // marshal an order every Nth work call (overlap with frames)

    // Play ownership: OFF by default (ReplayPanelsHarness owns Play). Flip ON for the
    // S2-spike render re-run, then restore (see PLAY OWNERSHIP note above).
    const bool AutoBootstrapEnabled = false;

    // --- cross-thread state: ONLY C# primitives cross the boundary ---
    long   _runCount;          // worker increments per marshal_work
    long   _orderRuns;         // worker increments per marshal_order
    long   _tickCount;         // worker mirrors seam.tick_count()
    int    _mainFrameCount;
    string _workerError;
    bool   _stopRequested;
    bool   _workerExited;      // worker set true after graceful_stop + teardown ran
    bool   _shutdownOk;        // worker set true iff graceful_stop post-conditions + teardown held

    Thread _worker;
    IntPtr _threadState;
    bool   _engineStarted;
    bool   _resultLogged;

    // Frame-cadence stats (main thread only).
    double _maxHitchS;
    int    _hitchCount;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootstrap()
    {
        if (!AutoBootstrapEnabled) return;
        var go = new GameObject("S2SpikeLiveLoop");
        DontDestroyOnLoad(go);
        go.AddComponent<S2SpikeLiveLoopHarness>();
    }

    void Start()
    {
        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            _engineStarted = true;
            // Release the GIL Initialize() holds; main NEVER reacquires it until shutdown.
            _threadState = PythonEngine.BeginAllowThreads();

            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "S2SpikeRenderWorker" };
            _worker.Start();

            Debug.Log("[S2-SPIKE LIVE LOOP] render harness: Initialize OK; worker started; main renders GIL-free.");
        }
        catch (Exception e)
        {
            Volatile.Write(ref _workerError, "init: " + e);
            Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] init: " + e);
        }
    }

    // Worker: take the GIL, start the seam, marshal work + periodic orders in a loop
    // until the frame target is reached. Each .result() releases the GIL so the loop
    // thread + tick pump + render proceed. On stop: graceful_stop + teardown (AC(b)),
    // worker-side, before releasing the GIL.
    void WorkerLoop()
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

                PyObject mod    = Py.Import(MODULE);
                PyObject seamCls = mod.GetAttr("LiveLoopSeam");
                PyObject seam    = seamCls.Invoke();
                seamCls.Dispose();
                mod.Dispose();

                using (PyObject started = seam.InvokeMethod("start")) { }

                long iter = 0;
                while (!Volatile.Read(ref _stopRequested) &&
                       Volatile.Read(ref _mainFrameCount) < TARGET_FRAMES)
                {
                    using (PyObject r = seam.InvokeMethod("marshal_work"))
                    {
                        if (r.As<long>() <= 0)
                        {
                            Volatile.Write(ref _workerError, "marshal_work returned work counter <= 0 (no-op coro?)");
                            break;
                        }
                    }
                    Interlocked.Increment(ref _runCount);

                    if (iter % ORDER_EVERY == 0)
                    {
                        using (PyObject ack = seam.InvokeMethod("marshal_order"))
                        {
                            string s = ack.As<string>();
                            if (s == null || !s.StartsWith("ACK:"))
                            {
                                Volatile.Write(ref _workerError, "marshal_order != ACK:* (got " + (s ?? "<null>") + ")");
                                break;
                            }
                        }
                        Interlocked.Increment(ref _orderRuns);
                    }

                    using (PyObject t = seam.InvokeMethod("tick_count"))
                        Interlocked.Exchange(ref _tickCount, t.As<long>());

                    iter++;
                }

                // AC(b): worker-side graceful_stop (decision-6 cancel -> stop) + teardown,
                // before releasing the GIL. main only joins us, then finalizes. Record the
                // post-conditions so the render-leg PASS confirms shutdown actually held
                // (not just that 300 frames rendered).
                try
                {
                    bool cancelRan, stopped, teardownOk;
                    long resting;
                    using (PyObject gs = seam.InvokeMethod("graceful_stop"))
                    using (PyObject cr = gs["cancel_ran"])
                    using (PyObject rs = gs["resting"])
                    using (PyObject st = gs["stopped"])
                    {
                        cancelRan = cr.As<bool>();
                        resting   = rs.As<long>();
                        stopped   = st.As<bool>();
                    }
                    using (PyObject td = seam.InvokeMethod("teardown_loop"))
                        teardownOk = td.As<bool>();

                    if (cancelRan && resting == 0 && stopped && teardownOk)
                        Volatile.Write(ref _shutdownOk, true);
                    else
                        Volatile.Write(ref _workerError,
                            $"shutdown post-conditions failed: cancel_ran={cancelRan} resting={resting} " +
                            $"stopped={stopped} teardownOk={teardownOk}");
                }
                catch (Exception e)
                {
                    Volatile.Write(ref _workerError, "graceful_stop/teardown: " + e);
                }
                seam.Dispose();
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _workerError, e.ToString());
        }
        finally
        {
            Volatile.Write(ref _workerExited, true);
        }
    }

    // Main thread: render frames, track cadence, NEVER take the GIL.
    void Update()
    {
        int f = Interlocked.Increment(ref _mainFrameCount);

        if (f > WARMUP_FRAMES)
        {
            // unscaledDeltaTime: a wall-clock frame interval independent of Time.timeScale —
            // a paused/slowed scene must not under-report a real render hitch.
            double dt = Time.unscaledDeltaTime;
            if (dt > _maxHitchS) _maxHitchS = dt;
            if (dt > HITCH_S) _hitchCount++;
        }

        if (f % 100 == 0)
        {
            Debug.Log($"[S2-SPIKE LIVE LOOP] frame={f} runs={Interlocked.Read(ref _runCount)} " +
                      $"orders={Interlocked.Read(ref _orderRuns)} ticks={Interlocked.Read(ref _tickCount)} " +
                      $"maxHitch={_maxHitchS * 1000:0}ms hitches={_hitchCount}");
        }

        if (_resultLogged) return;

        string err = Volatile.Read(ref _workerError);
        if (err != null)
        {
            Debug.LogError("[S2-SPIKE LIVE LOOP FAIL] " + err);
            _resultLogged = true;
            return;
        }

        // orders/ticks are produced ONLY inside the worker loop, which is bounded by
        // `_mainFrameCount < TARGET_FRAMES` — so orders>0 && ticks>0 inherently proves
        // they completed while real frames were advancing (no separate frame-overlap
        // bookkeeping needed; that earlier mechanism only added a publish race). The
        // final PASS also waits for the worker to EXIT after a clean graceful_stop +
        // teardown (AC(b) shutdown post-conditions), not just for 300 frames.
        if (f >= TARGET_FRAMES &&
            Interlocked.Read(ref _runCount) > 0 &&
            Interlocked.Read(ref _orderRuns) > 0 &&
            Interlocked.Read(ref _tickCount) > 0 &&
            Volatile.Read(ref _workerExited) &&
            Volatile.Read(ref _shutdownOk))
        {
            if (_maxHitchS >= MAX_HITCH_S)
            {
                Debug.LogError($"[S2-SPIKE LIVE LOOP FAIL] max post-warmup frame hitch {_maxHitchS * 1000:0}ms " +
                               $">= {MAX_HITCH_S * 1000:0}ms — render stalled while the host marshaled");
                _resultLogged = true;
                return;
            }
            if (_hitchCount > MAX_HITCHES)
            {
                Debug.LogError($"[S2-SPIKE LIVE LOOP FAIL] {_hitchCount} post-warmup hitches (>{HITCH_S * 1000:0}ms) " +
                               $"exceed budget {MAX_HITCHES} — render cadence not steady");
                _resultLogged = true;
                return;
            }

            Debug.Log($"[S2-SPIKE LIVE LOOP PASS] frames={f} runs={Interlocked.Read(ref _runCount)} " +
                      $"orders={Interlocked.Read(ref _orderRuns)} ticks={Interlocked.Read(ref _tickCount)} " +
                      $"maxHitch={_maxHitchS * 1000:0}ms hitches={_hitchCount} shutdown_ok=True " +
                      "(real Unity frames steady while the host marshaled into the live loop; shutdown post-conditions held)");
            _resultLogged = true;
        }
    }

    void OnDestroy()
    {
        try
        {
            Volatile.Write(ref _stopRequested, true);
            bool workerStopped = true;
            if (_worker != null && _worker.IsAlive)
                workerStopped = _worker.Join(10000);   // AC(b): main joins worker-side graceful_stop (~10s)

            if (!workerStopped)
            {
                // Worker still in Python holding the GIL; reacquiring on main to Shutdown
                // would deadlock. Skip teardown (playmode exit reclaims the process).
                Debug.LogWarning("[S2-SPIKE LIVE LOOP] OnDestroy: worker did not stop in time; skipping Python shutdown to avoid GIL deadlock");
            }
            else if (_engineStarted && Volatile.Read(ref _shutdownOk))
            {
                // Finalize ONLY when the worker reached a clean graceful_stop + teardown_loop
                // (_shutdownOk). If the worker died early (e.g. a marshal threw before teardown),
                // its daemon loop + tick-pump threads may still be alive — finalizing the runtime
                // then would deadlock/crash. The process exit reclaims everything in that case.
                if (_threadState != IntPtr.Zero)
                    PythonEngine.EndAllowThreads(_threadState);
                PythonEngine.Shutdown();
            }
            else if (_engineStarted)
            {
                Debug.LogWarning("[S2-SPIKE LIVE LOOP] OnDestroy: worker exited WITHOUT a clean teardown " +
                                 "(_shutdownOk=false); skipping Python shutdown (live loop/tick threads may remain; process exits next)");
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning("[S2-SPIKE LIVE LOOP] OnDestroy cleanup: " + e);
        }
    }
}
