// VizSpikeHarness.cs — issue #8 viz-spike (Phase 4)
//
// Proves the numpy -> GraphicsBuffer zero-copy -> GPU path under Windows / Unity Mono /
// D3D12. A worker thread (the ONLY thread that ever takes the GIL) generates a fresh
// np.sin ndarray every iteration via spike.viz_source.VizSource, and publishes the raw
// data pointer through a CAS 4-state, 3-slot, latest-wins handshake. The main thread
// (GIL-FREE forever after BeginAllowThreads) wraps the live pointer in a NativeArray
// (Allocator.None, AtomicSafetyHandle around SetData) and uploads it to a Structured
// GraphicsBuffer with NO app-layer copy, then renders it via endCameraRendering + DrawProceduralNow
// (LineStrip) + the VizSpike/SineLine StructuredBuffer shader.
//
// LIFETIME (most important): numpy memory is owned by Python; VizSource._slots keeps each
// generation alive until release_frame(gen) is called. release_frame REQUIRES the GIL, so
// it runs ONLY on the worker thread. Main never touches Python — it only reads raw
// pointers / C# primitives. A slot remembers the generation it currently holds; when the
// worker reclaims a slot (FREE->WRITING after main consumed it, or READY->WRITING for a
// latest-wins drop) it releases that old generation under the GIL before producing a new
// one. Thus every generation is released exactly once, on the worker, under the GIL.
//
// CAS 4-state per slot: FREE -> WRITING (worker) -> READY -> READING (main) -> FREE.
//   * worker: claims a FREE slot (or reclaims the OLDEST READY slot when none is free =
//     latest-wins drop), releases the slot's previous generation, generate_frame(), stores
//     ptr/len/gen, CAS WRITING->READY.
//   * main: each Update picks the LATEST READY slot (highest seq), CAS READY->READING,
//     SetData, CAS READING->FREE. The worker releases that generation later on reclaim.
//
// TEARDOWN (mirrors S0 / ReplayPanels): OnDestroy does NOT Shutdown()/EndAllowThreads()
// (the worker may hold the GIL; reacquiring on main could deadlock). Outstanding numpy
// slots leak with the process (interpreter persists; s_pythonBootstrapped reuses it next
// Play) — acceptable for a throwaway spike.
//
// HEADLESS COMPILE GATE: AutoBootstrap is gated OFF (AutoBootstrapEnabled=false) AND guarded
// by Application.isBatchMode, so `Unity -batchmode -nographics -quit` only compiles + quits
// — it never inits Python or renders. Phase 5 flips the flag true (and ReplayPanels OFF).
//
// SPIKE NOTE: absolute Windows paths are hardcoded constants (Step1 #3 will relativize via
// StreamingAssets). PythonRuntimeLocator is Mac-only, so it is NOT used here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using UnityEngine;
using UnityEngine.Rendering;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Python.Runtime;

public class VizSpikeHarness : MonoBehaviour
{
    // --- hardcoded spike constants (Step1 #3: relativize via StreamingAssets) ---
    const string PYTHON_DLL   = @"C:\Users\sasai\AppData\Roaming\uv\python\cpython-3.13-windows-x86_64-none\python313.dll";
    const string PYTHON_HOME  = @"C:\Users\sasai\AppData\Roaming\uv\python\cpython-3.13-windows-x86_64-none";
    const string VENV_SITE    = @"C:\Users\sasai\Documents\backcast\python\.venv\Lib\site-packages";
    const string PROJECT_ROOT = @"C:\Users\sasai\Documents\backcast\python";

    const int    N_POINTS      = 4096;
    const int    SLOT_COUNT    = 3;
    const int    STRIDE        = 4;       // float32
    const int    TARGET_FRAMES = 300;
    const float  HITCH_DT_S    = 0.20f;   // a frame slower than 200ms = a hitch
    const string SHADER_NAME   = "VizSpike/SineLine";

    // slot states
    const int FREE = 0, WRITING = 1, READY = 2, READING = 3;

    // per-slot arrays (Interlocked CAS on _state[i]; the rest are written under WRITING and
    // published via the Volatile state store, read under the READING/READY acquire).
    readonly int[]  _state = new int[SLOT_COUNT];
    readonly long[] _gen   = new long[SLOT_COUNT];  // generation currently stored (0 = none)
    readonly long[] _ptr   = new long[SLOT_COUNT];
    readonly int[]  _len   = new int[SLOT_COUNT];
    readonly long[] _seq   = new long[SLOT_COUNT];  // claim order: latest=main consumes, oldest=worker drops
    long _seqCounter;

    // bootstrap / threading
    Thread _worker;
    bool _stopRequested;   // accessed via Volatile.Read/Write below; no volatile keyword (CS0420 with ref args)
    string _fatalError;   // non-null => latch FAIL (worker or main may set)
    IntPtr _threadState;
    static bool s_pythonBootstrapped;

    // the live VizSource instance (worker-only; kept alive for the process, NOT disposed)
    PyObject _viz;

    // env info captured by the worker under the GIL
    volatile string _pyVersion = "?";
    volatile string _npVersion = "?";

    // diagnostics
    long _generated;            // worker generate_frame successes (mirrors VizSource.generated)
    long _dropped;              // latest-wins drops (READY reclaimed, never uploaded)
    long _setDataCalls;         // SetData invocations
    long _uploadedGenerations;  // generations main consumed (assert 6 cross-check)
    long _uploadedBytes;        // running total of bytes SetData'd (assert 8)

    // main-thread render / measure state
    GraphicsBuffer _gpuBuffer;
    Material _material;
    int   _frameCount;
    int   _renderedFrames;      // draw-callback frames (assert 7 = draw continuity; main never stops drawing)
    int   _lastRenderedFrame = -1; // de-dupe rendered count if >1 game camera fires per frame
    long  _lastUploadedGen;     // gen of the most recent successful upload (read by the draw hook)
    long  _lastDrawnGen;        // last uploaded gen the draw hook has already counted as drawn
    int   _distinctDrawn;       // distinct uploaded generations that reached a draw callback (GREEN gate)
    bool  _haveUploadedOnce;
    int   _hitchFrames;
    float _maxDtAfterStart;
    bool  _ptrAliasOk = true;
    readonly List<double> _setDataMicros = new List<double>();
    bool  _passLogged, _failLogged;

    // warmup gate: after the first normal draw, run WARMUP_FRAMES rendered frames, then reset
    // these baselines so generated/uploaded/rendered/hitches are all measured post-warmup.
    const int WARMUP_FRAMES = 30;
    bool _measureStarted;
    long _genBaseline, _uploadedBaseline, _upGenBaseline, _bytesBaseline, _droppedBaseline;
    int  _renderedBaseline, _distinctDrawnBaseline;

    // TURNKEY auto-bootstrap — gated OFF + batchmode-guarded (see header). Phase 5 flips true.
    const bool AutoBootstrapEnabled = false;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void AutoBootstrap()
    {
        if (!AutoBootstrapEnabled) return;     // Phase 5 owns Play (ReplayPanels OFF then)
        if (Application.isBatchMode) return;   // headless compile gate never inits Python / renders
        var go = new GameObject("VizSpike");
        DontDestroyOnLoad(go);
        go.AddComponent<VizSpikeHarness>();
    }

    void Start()
    {
        try
        {
            // GPU resources first (main-thread, no Python).
            _gpuBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, N_POINTS, STRIDE);
            Shader sh = Shader.Find(SHADER_NAME);
            if (sh == null) { LatchFail("shader not found: " + SHADER_NAME); return; }
            _material = new Material(sh);
            _material.SetBuffer("_Buf", _gpuBuffer);
            _material.SetInt("_Count", N_POINTS);

            // URP: immediate-mode Graphics.RenderPrimitives is NOT injected into the SRP draw
            // loop, so the sine never appeared. Draw via endCameraRendering + SetPass instead.
            RenderPipelineManager.endCameraRendering += OnEndCameraRendering;

            if (!s_pythonBootstrapped)
            {
                if (PythonEngine.IsInitialized)
                {
                    Fail("double-init: PythonEngine already initialized by another bootstrap");
                    Debug.LogError("[VIZ SPIKE] FAIL: " + Volatile.Read(ref _fatalError));
                    return;
                }

                Python.Runtime.Runtime.PythonDLL = PYTHON_DLL;
                Environment.SetEnvironmentVariable("PYTHONHOME", PYTHON_HOME);
                Environment.SetEnvironmentVariable("PYTHONPATH", VENV_SITE + Path.PathSeparator + PROJECT_ROOT);
                PythonEngine.PythonHome = PYTHON_HOME;

                PythonEngine.Initialize();
                // Release the GIL Initialize() holds on main; main NEVER reacquires it.
                _threadState = PythonEngine.BeginAllowThreads();
                s_pythonBootstrapped = true;
                Debug.Log("[VIZ SPIKE] PythonEngine.Initialize OK; main is GIL-free; worker starting.");
            }
            else
            {
                Debug.Log("[VIZ SPIKE] reusing the already-initialized interpreter (repeat Play; no re-Init).");
            }

            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "VizSpikeWorker" };
            _worker.Start();
        }
        catch (Exception e)
        {
            Fail("init: " + e);
            Debug.LogError("[VIZ SPIKE] FAIL (init): " + e);
        }
    }

    // Worker: the ONLY thread that takes the GIL. Imports the producer, runs the self-failing
    // gate once, then loops generate_frame() + release_frame() under the GIL, publishing raw
    // pointers through the CAS handshake.
    void WorkerLoop()
    {
        try
        {
            using (Py.GIL())
            using (PyObject sys = Py.Import("sys"))
            using (PyObject sysPath = sys.GetAttr("path"))
            {
                sysPath.InvokeMethod("insert", new PyInt(0), new PyString(PROJECT_ROOT)).Dispose();
                sysPath.InvokeMethod("insert", new PyInt(0), new PyString(VENV_SITE)).Dispose();
            }

            using (Py.GIL())
            {
                using (PyObject platformMod = Py.Import("platform"))
                using (PyObject pv = platformMod.InvokeMethod("python_version"))
                    _pyVersion = pv.As<string>();
                using (PyObject np = Py.Import("numpy"))
                using (PyObject nv = np.GetAttr("__version__"))
                    _npVersion = nv.As<string>();

                using (PyObject mod = Py.Import("spike.viz_source"))
                {
                    // self-failing producer gate once on THIS interpreter (raises -> caught below)
                    mod.InvokeMethod("run_gates").Dispose();
                    using (PyObject cls = mod.GetAttr("VizSource"))
                        _viz = cls.Invoke(new PyInt(N_POINTS));   // kept alive (process lifetime)
                }
            }

            while (!Volatile.Read(ref _stopRequested))
            {
                int idx = -1;
                bool isDrop = false;

                // 1) prefer a FREE slot.
                for (int i = 0; i < SLOT_COUNT; i++)
                {
                    if (Interlocked.CompareExchange(ref _state[i], WRITING, FREE) == FREE) { idx = i; break; }
                }

                // 2) else latest-wins: reclaim the OLDEST READY slot (drop its un-uploaded gen).
                if (idx < 0)
                {
                    int bestI = -1; long bestSeq = long.MaxValue;
                    for (int i = 0; i < SLOT_COUNT; i++)
                    {
                        if (Volatile.Read(ref _state[i]) == READY && _seq[i] < bestSeq) { bestSeq = _seq[i]; bestI = i; }
                    }
                    if (bestI >= 0 && Interlocked.CompareExchange(ref _state[bestI], WRITING, READY) == READY)
                    {
                        idx = bestI; isDrop = true;
                    }
                }

                if (idx < 0) { Thread.Sleep(1); continue; }   // all slots READING (main busy) — retry

                long oldGen = _gen[idx];

                try
                {
                    using (Py.GIL())
                    {
                        // Release the generation previously held by this slot (worker + GIL only).
                        // FREE-reclaim => that gen was already uploaded; READY-reclaim => dropped.
                        if (oldGen != 0)
                            _viz.InvokeMethod("release_frame", new PyInt(oldGen)).Dispose();

                        using (PyObject meta = _viz.InvokeMethod("generate_frame"))
                        using (PyObject genO  = meta["generation"])
                        using (PyObject ptrO  = meta["ptr"])
                        using (PyObject lenO  = meta["length"])
                        using (PyObject itemO = meta["itemsize"])
                        using (PyObject dtO   = meta["dtype"])
                        using (PyObject ccO   = meta["c_contiguous"])
                        {
                            long g  = genO.As<long>();
                            long p  = ptrO.As<long>();
                            int  l  = lenO.As<int>();
                            int  it = itemO.As<int>();
                            string dt = dtO.As<string>();
                            bool cc = ccO.As<bool>();

                            // asserts 1-4 — worker, under the GIL, on the meta we received.
                            if (dt != "float32")        { Fail("assert1 dtype=" + dt); return; }
                            if (!cc)                    { Fail("assert2 not-C-contiguous"); return; }
                            if (it != 4)                { Fail("assert3 itemsize=" + it); return; }
                            if (p == 0 || (p % 4) != 0) { Fail("assert4 ptr=" + p); return; }

                            _ptr[idx] = p;
                            _len[idx] = l;
                            _gen[idx] = g;
                            _seq[idx] = Interlocked.Increment(ref _seqCounter);
                            Interlocked.Increment(ref _generated);
                        }
                    } // GIL released here -> main is never blocked
                }
                catch (Exception e) { Fail("worker generate: " + e); return; }

                if (isDrop) Interlocked.Increment(ref _dropped);

                Volatile.Write(ref _state[idx], READY);   // publish: WRITING -> READY
                Thread.Sleep(1);                          // modest cadence (>> display rate)
            }
        }
        catch (Exception e)
        {
            Fail("worker init: " + e);
        }
    }

    void Update()
    {
        _frameCount++;

        string err = Volatile.Read(ref _fatalError);
        if (err != null) { LatchFail(err); }

        ConsumeLatestReady();

        // Warmup: after the first NORMAL draw, let WARMUP_FRAMES rendered frames pass, then
        // reset all counters so the bootstrap/first-frame hitches are excluded from the gate.
        // _renderedFrames only advances once endCameraRendering has actually drawn the sine,
        // so reaching WARMUP_FRAMES proves drawing is live before measurement begins.
        if (!_measureStarted && _renderedFrames >= WARMUP_FRAMES)
        {
            _genBaseline           = Interlocked.Read(ref _generated);
            _uploadedBaseline      = Interlocked.Read(ref _setDataCalls);
            _upGenBaseline         = Interlocked.Read(ref _uploadedGenerations);
            _bytesBaseline         = Interlocked.Read(ref _uploadedBytes);
            _droppedBaseline       = Interlocked.Read(ref _dropped);
            _renderedBaseline      = _renderedFrames;
            _distinctDrawnBaseline = _distinctDrawn;
            _hitchFrames = 0; _maxDtAfterStart = 0f; _setDataMicros.Clear();
            _measureStarted = true;
        }

        // Cadence is measured only AFTER warmup reset (bootstrap + warmup excluded).
        if (_measureStarted)
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > _maxDtAfterStart) _maxDtAfterStart = dt;
            if (dt > HITCH_DT_S) _hitchFrames++;
        }

        if (_frameCount % 50 == 0)
        {
            Debug.Log($"[VIZ SPIKE] frame={_frameCount} gen={Interlocked.Read(ref _generated)} " +
                      $"uploaded={Interlocked.Read(ref _setDataCalls)} dropped={Interlocked.Read(ref _dropped)} " +
                      $"hitches={_hitchFrames}");
        }

        TryLogResult();
    }

    // Pick the LATEST ready slot (freshest), CAS READY->READING, upload once, CAS READING->FREE.
    void ConsumeLatestReady()
    {
        int bestI = -1; long bestSeq = long.MinValue;
        for (int i = 0; i < SLOT_COUNT; i++)
        {
            if (Volatile.Read(ref _state[i]) == READY && _seq[i] > bestSeq) { bestSeq = _seq[i]; bestI = i; }
        }
        if (bestI < 0) return;
        if (Interlocked.CompareExchange(ref _state[bestI], READING, READY) != READY) return; // worker reclaimed it

        long ptr = _ptr[bestI];
        int  len = _len[bestI];

        try
        {
            DoUpload(ptr, len);
            Interlocked.Increment(ref _uploadedGenerations);   // success path only (skipped if DoUpload threw)
            _lastUploadedGen = _gen[bestI];                    // record uploaded gen so the draw hook can count it distinct
        }
        catch (Exception e)
        {
            // A SetData / upload exception must LATCH FAIL — never be swallowed, or 300 later
            // successes would produce a false PASS.
            Fail("upload: " + e);
        }
        finally
        {
            // Reading -> Free, ALWAYS — even if SetData / the alias-check threw — so the worker can
            // reclaim the slot instead of it leaking in READING. The gen STAYS in _gen[bestI]; the
            // worker releases it on reclaim.
            Volatile.Write(ref _state[bestI], FREE);
        }
    }

    // Zero-copy upload: wrap the live numpy pointer in a NativeArray (Allocator.None) with a
    // per-call AtomicSafetyHandle, prove it aliases the numpy ptr (assert 5), SetData, release.
    unsafe void DoUpload(long ptr, int len)
    {
        NativeArray<float> na = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<float>((void*)ptr, len, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
        AtomicSafetyHandle handle = AtomicSafetyHandle.Create();
        NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref na, handle);
#endif
        try
        {
            // assert 5: the NativeArray aliases the numpy buffer (no app-layer copy).
            long naPtr = (long)NativeArrayUnsafeUtility.GetUnsafeReadOnlyPtr(na);
            if (naPtr != ptr) { _ptrAliasOk = false; Fail($"assert5 ptr-alias na={naPtr} meta={ptr}"); return; }

            var sw = System.Diagnostics.Stopwatch.StartNew();
            _gpuBuffer.SetData(na);
            sw.Stop();
            double micros = (double)sw.ElapsedTicks / System.Diagnostics.Stopwatch.Frequency * 1e6;

            Interlocked.Increment(ref _setDataCalls);
            Interlocked.Add(ref _uploadedBytes, (long)len * STRIDE);
            _setDataMicros.Add(micros);

            if (!_haveUploadedOnce) _haveUploadedOnce = true;
        }
        finally
        {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            AtomicSafetyHandle.Release(handle);
#endif
        }
    }

    void TryLogResult()
    {
        if (_passLogged || _failLogged) return;

        string err = Volatile.Read(ref _fatalError);
        if (err != null) { LatchFail(err); return; }

        // Nothing is gated until the warmup reset has happened (i.e. the sine has actually
        // been drawn for WARMUP_FRAMES frames). PASS therefore implies real rendering.
        if (!_measureStarted) return;

        long generated     = Interlocked.Read(ref _generated) - _genBaseline;
        long uploaded      = Interlocked.Read(ref _setDataCalls) - _uploadedBaseline;
        long rendered      = _renderedFrames - _renderedBaseline;
        long distinctDrawn = _distinctDrawn - _distinctDrawnBaseline;
        if (generated < TARGET_FRAMES || uploaded < TARGET_FRAMES || rendered < TARGET_FRAMES ||
            distinctDrawn < TARGET_FRAMES) return;

        long upGen = Interlocked.Read(ref _uploadedGenerations) - _upGenBaseline;
        long bytes = Interlocked.Read(ref _uploadedBytes) - _bytesBaseline;

        // final asserts 6, 7, 8 + D3D12 backend + non-stall + alias latch (all post-warmup deltas)
        if (uploaded != upGen) { LatchFail($"assert6 setDataCalls={uploaded} uploadedGenerations={upGen}"); return; }
        // assert7 (draw-callback CONTINUITY, not per-upload draw proof): the main thread keeps issuing
        // draw callbacks while uploads happen — renderedFrames must keep pace with setDataCalls, allowing
        // ONE in-flight upload not yet drawn (ConsumeLatestReady bumps setDataCalls in-frame during Update,
        // but the matching OnEndCameraRendering fires AFTER Update, so at a boundary uploaded==rendered+1
        // for one pipeline-depth frame; anything beyond +1 means main stopped drawing -> fail).
        // Proof that each UPLOADED generation actually reached a draw is carried by distinctDrawn below,
        // NOT by this counter (renderedFrames also advances on identical-content redraws).
        if (uploaded > rendered + 1) { LatchFail($"assert7 setDataCalls={uploaded} renderedFrames={rendered}"); return; }
        long expectBytes = uploaded * (long)N_POINTS * STRIDE;
        if (bytes != expectBytes) { LatchFail($"assert8 bytes={bytes} expect={expectBytes}"); return; }
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12) { LatchFail("D3D12 expected, got " + SystemInfo.graphicsDeviceType); return; }
        if (!_ptrAliasOk) { LatchFail("ptrAlias not OK"); return; }
        if (_hitchFrames != 0) { LatchFail($"hitches={_hitchFrames} maxDt={_maxDtAfterStart * 1000f:0.#}ms"); return; }

        Percentiles(out double p50, out double p95, out double max);
        long dropped = Interlocked.Read(ref _dropped) - _droppedBaseline;   // post-warmup drops (same window as gen/uploaded/rendered)

        _passLogged = true;
        Debug.Log($"[VIZ SPIKE PASS] python={_pyVersion} numpy={_npVersion} points={N_POINTS}");
        Debug.Log($"gen={generated} uploaded={uploaded} rendered={rendered} distinctDrawn={distinctDrawn} dropped={dropped} frames={_frameCount}");
        Debug.Log($"maxDt={_maxDtAfterStart * 1000f:0.#}ms hitches=0 uploadP50={p50:0}us uploadP95={p95:0}us uploadMax={max:0}us");
        Debug.Log($"ptrAlias=OK setDataCalls={uploaded} bytes={bytes} D3D12=OK");
    }

    void Percentiles(out double p50, out double p95, out double max)
    {
        p50 = p95 = max = 0;
        var copy = new List<double>(_setDataMicros);
        if (copy.Count == 0) return;
        copy.Sort();
        max = copy[copy.Count - 1];
        p50 = copy[Mathf.Min(copy.Count - 1, (int)(copy.Count * 0.50))];
        p95 = copy[Mathf.Min(copy.Count - 1, (int)(copy.Count * 0.95))];
    }

    void Fail(string which)
    {
        Volatile.Write(ref _fatalError, which);
        Volatile.Write(ref _stopRequested, true);
    }

    void LatchFail(string which)
    {
        if (_failLogged) return;
        _failLogged = true;
        Debug.LogError("[VIZ SPIKE FAIL] " + which);
    }

    // URP draw hook: Graphics.RenderPrimitives never showed under URP because immediate-mode
    // primitives are not part of the ScriptableRenderContext submission. We instead draw the
    // procedural line strip right after the camera finishes, selecting pass 0 explicitly and
    // issuing DrawProceduralNow so it lands on the camera target (ZTest Always => on top).
    void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
    {
        if (cam.cameraType != CameraType.Game) return;
        if (!_haveUploadedOnce || _material == null || _gpuBuffer == null) return;
        _material.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.LineStrip, N_POINTS);
        if (_lastRenderedFrame != _frameCount) { _lastRenderedFrame = _frameCount; _renderedFrames++; }
        // distinctDrawn: count each UPLOADED generation the first time it is the live buffer content at
        // a draw callback. Unlike _renderedFrames (which also bumps on identical-content redraws), this
        // proves each uploaded gen reached the GPU draw path. Both run on the main thread (no sync).
        if (_lastUploadedGen != 0 && _lastUploadedGen != _lastDrawnGen)
        {
            _lastDrawnGen = _lastUploadedGen;
            _distinctDrawn++;
        }
    }

    void OnDestroy()
    {
        RenderPipelineManager.endCameraRendering -= OnEndCameraRendering;
        Volatile.Write(ref _stopRequested, true);
        try { _worker?.Join(2000); } catch { }

        // DELIBERATELY no PythonEngine.Shutdown() / EndAllowThreads() — the worker may hold the
        // GIL; reacquiring on main to Shutdown could deadlock. Outstanding numpy slots leak with
        // the process (interpreter persists; s_pythonBootstrapped reuses it next Play).
        if (_gpuBuffer != null) { _gpuBuffer.Release(); _gpuBuffer = null; }
        if (_material != null) { Destroy(_material); _material = null; }
        Debug.Log("[VIZ SPIKE] OnDestroy: worker stop-requested; interpreter left alive (no GIL reacquire).");
    }
}
