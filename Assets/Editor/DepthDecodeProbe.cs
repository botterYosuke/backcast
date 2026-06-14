// DepthDecodeProbe.cs — editor-only AFK gate for #26 "S9a orderbook 先行"
// docs/findings/0012-orderbook-depth-ladder.md. Characterization + realtime E2E for the
// durable DepthDecoder (get_state_json per_instrument[id].depth → bid/ask ladder view).
//
// Two phases, both self-failing (exit 1 on any miss):
//
//   PHASE A — CHARACTERIZATION (pure-string fixtures, NO Python):
//     Drives DepthDecoder.Decode over hand-built state-json fixtures covering every
//     branch of the contract: multi-level asymmetric ladder (bids 降順 / asks 昇順 with
//     sizes), empty board, one-side-missing, depth:null (Replay), instrument absent,
//     per_instrument absent, a STRING-VALUE locator decoy (escaped fake board inside
//     live_last_error), a substring-key decoy, and malformed-json-throws. This is where
//     locator robustness + the ordering contract are pinned, fast and deterministic.
//
//   PHASE B — REALTIME E2E (in-proc MOCK live, mirrors LiveAdapterTracerProbe):
//     C# owns the lifecycle via InprocLiveServer; a poll worker reads get_state_json();
//     main decodes GIL-free with DepthDecoder. The drive worker emits TWO asymmetric
//     depth generations of DIFFERENT shape (gen1: 2 bids / 2 asks, gen2: 1 bid / 3 asks)
//     and we assert FULL REPLACEMENT — gen2's values land AND gen1's old top level (10.0)
//     is GONE — proving the board updates in real time through bus → DepthCache →
//     get_state_json → decoder, not a "saw a render" check.
//
//   <Unity> -batchmode -nographics -quit -projectPath <repo> \
//       -executeMethod DepthDecodeProbe.Run
//
// Exit 0 => PASS ([DEPTH DECODE PASS]), 1 => FAIL.

using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using UnityEditor;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public static class DepthDecodeProbe
{
    const string IID          = "8918.TSE";
    const long   MAX_STALL_MS = 200;
    const double EPS          = 1e-6;

    // Interactive entry: run Phase A (pure C#, no Python) from an OPEN Editor and read the
    // result in the Console — does NOT EditorApplication.Exit (that would close the Editor).
    // Use when batchmode is unavailable because the project is already open.
    [MenuItem("Tools/Backcast/Depth Decode Characterization (AFK)")]
    public static void RunCharacterizationMenu()
    {
        string err = RunCharacterization();
        if (err != null)
            Debug.LogError("[DEPTH DECODE FAIL] characterization: " + err);
        else
            Debug.Log("[DEPTH DECODE PASS] characterization (12 fixtures) — pure-C# decoder gate (no Python)");
    }

    // Editor entry: Phase A ONLY (pure C#, NO Python, NO venv). Use this to gate the
    // durable DepthDecoder on a repaired Unity install without staging python/.venv:
    //   <Unity> -batchmode -nographics -quit -projectPath <repo> \
    //       -executeMethod DepthDecodeProbe.RunCharacterizationOnly
    public static void RunCharacterizationOnly()
    {
        string err = RunCharacterization();
        if (err != null)
        {
            Debug.LogError("[DEPTH DECODE FAIL] characterization: " + err);
            EditorApplication.Exit(1);
            return;
        }
        Debug.Log("[DEPTH DECODE PASS] characterization (12 fixtures) — pure-C# decoder gate (no Python)");
        EditorApplication.Exit(0);
    }

    public static void Run()
    {
        // ---- Phase A: pure-string characterization (no Python) ----
        string aErr = RunCharacterization();
        if (aErr != null)
        {
            Debug.LogError("[DEPTH DECODE FAIL] characterization: " + aErr);
            EditorApplication.Exit(1);
            return;
        }
        Debug.Log("[DEPTH DECODE MARK] characterization PASS (12 fixtures)");

        // ---- Phase B: realtime E2E (in-proc MOCK live) ----
        bool passed = false;
        bool engineStarted = false;
        try
        {
            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            engineStarted = true;
            _mainThreadState = PythonEngine.BeginAllowThreads();
            Debug.Log("[DEPTH DECODE MARK] python initialized; main GIL-free; starting drive + poll");

            var drive = new Thread(DriveWorker) { IsBackground = true, Name = "DepthDrive" };
            var poll  = new Thread(PollWorker)  { IsBackground = true, Name = "DepthPoll" };
            drive.Start();
            poll.Start();

            long maxStall = HeartbeatUntil(() => _driveDone, 120000);
            bool heartbeatTimedOut = !_driveDone;
            bool dj = drive.Join(heartbeatTimedOut ? 0 : 2000);
            _stopPolling = true;
            bool pj = poll.Join(heartbeatTimedOut ? 0 : 5000);

            Pump();

            string why = ValidateRealtime(dj, pj, maxStall, heartbeatTimedOut);
            if (why != null)
            {
                Debug.LogError("[DEPTH DECODE FAIL] realtime: " + why);
            }
            else
            {
                passed = true;
                Debug.Log(string.Format(CultureInfo.InvariantCulture,
                    "[DEPTH DECODE PASS] gen1(bids=2 top={0}/{1} asks=2 top={2}/{3}) -> " +
                    "gen2(bids=1 top={4}/{5} asks=3 last={6}/{7}) full-replacement (old 10.0 gone) " +
                    "maxStall={8}ms — decoder restored dict-keyed depth ladder from get_state_json",
                    _g1B0P, _g1B0S, _g1A0P, _g1A0S, _g2B0P, _g2B0S, _g2A2P, _g2A2S, maxStall));
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[DEPTH DECODE FAIL] driver: " + e);
        }
        finally
        {
            try
            {
                if (engineStarted && _driveDone)
                {
                    PythonEngine.EndAllowThreads(_mainThreadState);
                    PythonEngine.Shutdown();
                    Debug.Log("[DEPTH DECODE MARK] PythonEngine.Shutdown OK");
                }
                else if (engineStarted)
                {
                    Debug.LogWarning("[DEPTH DECODE] drive worker did not finish; skipping Shutdown to avoid GIL deadlock");
                }
            }
            catch (Exception e) { Debug.LogWarning("[DEPTH DECODE] shutdown cleanup: " + e); }
        }

        EditorApplication.Exit(passed ? 0 : 1);
    }

    // =================================================================================
    // PHASE A — characterization
    // =================================================================================

    static string RunCharacterization()
    {
        // F1: multi-level asymmetric — bids 降順 (3) / asks 昇順 (2), sizes asserted.
        {
            string j = "{\"price\":10.0,\"per_instrument\":{\"8918.TSE\":{\"price\":10.0," +
                       "\"ohlc_points\":[{\"open\":1.0}]," +
                       "\"depth\":{\"bids\":[{\"price\":10.0,\"size\":100.0}," +
                       "{\"price\":9.9,\"size\":200.0},{\"price\":9.8,\"size\":300.0}]," +
                       "\"asks\":[{\"price\":10.1,\"size\":150.0},{\"price\":10.2,\"size\":250.0}]," +
                       "\"timestamp_ms\":1234}}}}";
            var v = DepthDecoder.Decode(j, IID);
            if (!v.HasDepth) return "F1 HasDepth false";
            if (v.Bids.Count != 3) return $"F1 bids count={v.Bids.Count} != 3";
            if (v.Asks.Count != 2) return $"F1 asks count={v.Asks.Count} != 2";
            if (D(v.Bids[0].Price, 10.0) || D(v.Bids[1].Price, 9.9) || D(v.Bids[2].Price, 9.8))
                return "F1 bids not 降順 10.0/9.9/9.8";
            if (D(v.Bids[0].Size, 100.0) || D(v.Bids[1].Size, 200.0) || D(v.Bids[2].Size, 300.0))
                return "F1 bid sizes wrong";
            if (D(v.Asks[0].Price, 10.1) || D(v.Asks[1].Price, 10.2)) return "F1 asks not 昇順 10.1/10.2";
            if (D(v.Asks[0].Size, 150.0) || D(v.Asks[1].Size, 250.0)) return "F1 ask sizes wrong";
            if (v.TimestampMs != 1234) return $"F1 timestamp_ms={v.TimestampMs} != 1234";
        }

        // F2: empty board (depth present, both sides empty) — HasDepth true, counts 0.
        {
            string j = "{\"per_instrument\":{\"8918.TSE\":{\"depth\":{\"bids\":[],\"asks\":[],\"timestamp_ms\":7}}}}";
            var v = DepthDecoder.Decode(j, IID);
            if (!v.HasDepth) return "F2 HasDepth false (empty board must be HasDepth=true)";
            if (v.Bids.Count != 0 || v.Asks.Count != 0) return "F2 expected empty ladders";
        }

        // F3: one-side missing — bids present, asks key absent → asks empty, no throw.
        {
            string j = "{\"per_instrument\":{\"8918.TSE\":{\"depth\":{\"bids\":[{\"price\":5.0,\"size\":9.0}]}}}}";
            var v = DepthDecoder.Decode(j, IID);
            if (!v.HasDepth) return "F3 HasDepth false";
            if (v.Bids.Count != 1 || D(v.Bids[0].Price, 5.0) || D(v.Bids[0].Size, 9.0)) return "F3 bids wrong";
            if (v.Asks.Count != 0) return "F3 asks should be empty";
        }

        // F4: depth:null (Replay) → Empty (HasDepth false).
        {
            string j = "{\"per_instrument\":{\"8918.TSE\":{\"price\":10.0,\"depth\":null}}}";
            var v = DepthDecoder.Decode(j, IID);
            if (v.HasDepth) return "F4 depth:null must decode to Empty";
            if (v.Bids.Count != 0 || v.Asks.Count != 0) return "F4 expected empty";
        }

        // F5: instrument absent from per_instrument → Empty.
        {
            string j = "{\"per_instrument\":{\"7203.TSE\":{\"depth\":{\"bids\":[{\"price\":1.0,\"size\":1.0}],\"asks\":[]}}}}";
            var v = DepthDecoder.Decode(j, IID);
            if (v.HasDepth) return "F5 absent instrument must be Empty";
        }

        // F6: per_instrument absent entirely → Empty.
        {
            string j = "{\"price\":10.0,\"last_prices\":{\"8918.TSE\":10.0}}";
            var v = DepthDecoder.Decode(j, IID);
            if (v.HasDepth) return "F6 missing per_instrument must be Empty";
        }

        // F7: STRING-VALUE locator decoy — a fake board escaped inside live_last_error
        // BEFORE the real per_instrument. Structure-aware locator must ignore the decoy.
        {
            string j = "{\"live_last_error\":\"per_instrument\\\":{\\\"8918.TSE\\\":{\\\"depth\\\":" +
                       "{\\\"bids\\\":[{\\\"price\\\":999.0,\\\"size\\\":1.0}]}}}\"," +
                       "\"per_instrument\":{\"8918.TSE\":{\"depth\":{\"bids\":[{\"price\":10.0,\"size\":100.0}],\"asks\":[]}}}}";
            var v = DepthDecoder.Decode(j, IID);
            if (!v.HasDepth) return "F7 HasDepth false";
            if (v.Bids.Count != 1 || D(v.Bids[0].Price, 10.0))
                return $"F7 decoy leaked: bids[0].price={ (v.Bids.Count>0 ? v.Bids[0].Price : double.NaN) } != 10.0";
        }

        // F8: substring-key decoy — a longer id ("8918.TSEX") precedes the exact id. Exact
        // key match must skip the decoy member and land on the real one.
        {
            string j = "{\"per_instrument\":{\"8918.TSEX\":{\"depth\":{\"bids\":[{\"price\":999.0,\"size\":1.0}],\"asks\":[]}}," +
                       "\"8918.TSE\":{\"depth\":{\"bids\":[{\"price\":10.0,\"size\":100.0}],\"asks\":[]}}}}";
            var v = DepthDecoder.Decode(j, IID);
            if (!v.HasDepth || v.Bids.Count != 1 || D(v.Bids[0].Price, 10.0))
                return "F8 substring-key decoy matched wrong member";
        }

        // F9: malformed (unterminated depth object) → MUST throw, not silently empty.
        {
            string j = "{\"per_instrument\":{\"8918.TSE\":{\"depth\":{\"bids\":[{\"price\":10.0,\"size\":100.0}";
            bool threw = false;
            try { DepthDecoder.Decode(j, IID); }
            catch (FormatException) { threw = true; }
            if (!threw) return "F9 malformed json must throw FormatException";
        }

        // F10: degenerate inputs → Empty, no throw.
        {
            if (DepthDecoder.Decode(null, IID).HasDepth) return "F10 null → Empty";
            if (DepthDecoder.Decode("", IID).HasDepth) return "F10 empty → Empty";
            if (DepthDecoder.Decode("   ", IID).HasDepth) return "F10 whitespace → Empty";
            if (DepthDecoder.Decode("null", IID).HasDepth) return "F10 \"null\" → Empty";
            if (DepthDecoder.Decode("{\"per_instrument\":{\"8918.TSE\":{\"depth\":{\"bids\":[],\"asks\":[]}}}}", "").HasDepth)
                return "F10 empty instrumentId → Empty";
        }

        // F11: malformed-throw variants that must surface FormatException (not
        // IndexOutOfRange, not silent zero-fill, not silent Empty).
        {
            // (a) truncated right after `"depth":` → value index past EOF.
            if (!Throws("{\"per_instrument\":{\"8918.TSE\":{\"depth\":")) return "F11a truncated-after-colon must throw";
            // (b) count-balanced but mispaired brackets in the depth object.
            if (!Throws("{\"per_instrument\":{\"8918.TSE\":{\"depth\":{]}}}}")) return "F11b mispaired bracket must throw";
            // (c) a `null`-prefixed garbage token where depth is expected.
            if (!Throws("{\"per_instrument\":{\"8918.TSE\":{\"depth\":nullish}}}")) return "F11c nullish must throw";
        }

        // F12: SCRAMBLED wire order must survive VERBATIM — this is the ONLY fixture that
        // actually pins the faithful-passthrough contract. F1-F11 all feed pre-sorted data,
        // so a hidden defensive re-sort (bids desc / asks asc) would pass them all; only
        // scrambled input proves the decoder preserves wire order and never re-sorts.
        {
            string j = "{\"per_instrument\":{\"8918.TSE\":{\"depth\":{" +
                       "\"bids\":[{\"price\":9.8,\"size\":1.0},{\"price\":10.0,\"size\":2.0},{\"price\":9.9,\"size\":3.0}]," +
                       "\"asks\":[{\"price\":10.2,\"size\":4.0},{\"price\":10.1,\"size\":5.0}]}}}}";
            var v = DepthDecoder.Decode(j, IID);
            if (v.Bids.Count != 3 || D(v.Bids[0].Price, 9.8) || D(v.Bids[1].Price, 10.0) || D(v.Bids[2].Price, 9.9))
                return "F12 bids re-sorted (must preserve wire order 9.8/10.0/9.9)";
            if (D(v.Bids[0].Size, 1.0) || D(v.Bids[1].Size, 2.0) || D(v.Bids[2].Size, 3.0))
                return "F12 bid sizes not aligned to wire order";
            if (v.Asks.Count != 2 || D(v.Asks[0].Price, 10.2) || D(v.Asks[1].Price, 10.1))
                return "F12 asks re-sorted (must preserve wire order 10.2/10.1)";
        }

        return null;
    }

    static bool Throws(string j)
    {
        try { DepthDecoder.Decode(j, IID); return false; }
        catch (FormatException) { return true; }
    }

    static bool D(double a, double b) => Math.Abs(a - b) > EPS;

    // =================================================================================
    // PHASE B — realtime E2E (mirrors LiveAdapterTracerProbe lifecycle/teardown)
    // =================================================================================

    static readonly LiveBackendEventSink _sink = new LiveBackendEventSink();

    static volatile PyObject _server;
    static volatile PyObject _mi;
    static volatile bool   _serverReady;
    static volatile string _driveError;
    static volatile bool   _driveDone;
    static volatile string _runId;

    static volatile bool   _stopPolling;
    static volatile bool   _pollStopped;
    static volatile string _pollError;
    static volatile string _latestStateJson;

    static volatile bool _gen1Seen;
    static volatile bool _gen2Seen;
    static IntPtr _mainThreadState;

    // gen captures (written only by main in Pump())
    static double _g1B0P = double.NaN, _g1B0S = double.NaN, _g1B1P = double.NaN;
    static double _g1A0P = double.NaN, _g1A0S = double.NaN, _g1A1P = double.NaN;
    static double _g2B0P = double.NaN, _g2B0S = double.NaN;
    static double _g2A0P = double.NaN, _g2A2P = double.NaN, _g2A2S = double.NaN;
    static int    _g2BCount = -1, _g2ACount = -1;

    static void DriveWorker()
    {
        try
        {
            using (Py.GIL())
            {
                using (PyObject sys = Py.Import("sys"))
                using (PyObject sp = sys.GetAttr("path"))
                {
                    sp.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.ProjectRoot)).Dispose();
                    sp.InvokeMethod("insert", new PyInt(0), new PyString(PythonRuntimeLocator.VenvSite)).Dispose();
                }

                PyObject de;
                using (PyObject coreMod = Py.Import("engine.core"))
                using (PyObject deCls = coreMod.GetAttr("DataEngine"))
                    de = deCls.Invoke();

                using (PyObject sinkPy = PyObject.FromManagedObject(_sink))
                    de.InvokeMethod("set_rust_event_sink", sinkPy).Dispose();

                using (PyObject inproc = Py.Import("engine.inproc_server"))
                using (PyObject srvCls = inproc.GetAttr("InprocLiveServer"))
                    _server = srvCls.Invoke(de, new PyString("MOCK"));
                de.Dispose();

                _mi = Py.Import("spike.live_adapter.mock_inject");
                string twin = _mi.GetAttr("TWIN_PATH").As<string>();
                _serverReady = true;

                string strategyId;
                using (PyObject reg = _server.InvokeMethod("register_live_strategy", new PyString(twin), new PyString(twin)))
                {
                    if (!reg["success"].As<bool>()) { _driveError = "register_live_strategy failed: " + reg; return; }
                    strategyId = reg["strategy_id"].As<string>();
                }
                using (PyObject login = _server.InvokeMethod("venue_login", new PyString("MOCK"), new PyString("env"), new PyString("")))
                    if (!login["success"].As<bool>()) { _driveError = "venue_login failed: " + login; return; }
                using (PyObject mode = _server.InvokeMethod("set_execution_mode", new PyString("LiveAuto")))
                    if (!mode["success"].As<bool>()) { _driveError = "set_execution_mode(LiveAuto) failed: " + mode; return; }
                using (PyObject start = _server.InvokeMethod("start_live_strategy", new PyString(strategyId), new PyString(IID), new PyString("MOCK")))
                {
                    if (!start["success"].As<bool>()) { _driveError = "start_live_strategy failed: " + start; return; }
                    _runId = start["run_id"].As<string>();
                }

                // gen1: 2 bids (10.0/100, 9.9/200), 2 asks (10.1/150, 10.2/250).
                EmitLadder(1, "10.0,9.9", "100.0,200.0", "10.1,10.2", "150.0,250.0");
            }

            if (!WaitFlag(() => _gen1Seen, 20000))
            { if (_driveError == null) _driveError = "timeout waiting for gen1 depth decode"; return; }

            using (Py.GIL())
            {
                // gen2: DIFFERENT shape — 1 bid (11.0/50), 3 asks (11.1/60, 11.2/70, 11.3/80).
                EmitLadder(2, "11.0", "50.0", "11.1,11.2,11.3", "60.0,70.0,80.0");
            }

            if (!WaitFlag(() => _gen2Seen, 20000))
            { if (_driveError == null) _driveError = "timeout waiting for gen2 full-replacement decode"; return; }
        }
        catch (Exception e)
        {
            if (_driveError == null) _driveError = "drive: " + e;
        }
        finally
        {
            _stopPolling = true;
            WaitFlag(() => _pollStopped, 5000);
            if (_server != null)
            {
                try
                {
                    using (Py.GIL())
                    {
                        if (_runId != null)
                            try { _server.InvokeMethod("stop_live_strategy", new PyString(_runId)).Dispose(); } catch {}
                        try { _server.InvokeMethod("set_execution_mode", new PyString("Replay")).Dispose(); } catch {}
                        try { _server.InvokeMethod("venue_logout").Dispose(); } catch {}
                        try { _server.InvokeMethod("close").Dispose(); } catch {}
                    }
                }
                catch (Exception e) { Debug.LogWarning("[DEPTH DECODE] teardown: " + e); }
            }
            _driveDone = true;
        }
    }

    // call under the GIL. CSV strings keep pythonnet InvokeMethod off list-marshalling.
    static void EmitLadder(int i, string bidP, string bidS, string askP, string askS)
    {
        _mi.InvokeMethod("emit_depth_ladder", _server, new PyInt(i),
            new PyString(bidP), new PyString(bidS), new PyString(askP), new PyString(askS)).Dispose();
    }

    static void PollWorker()
    {
        try
        {
            var sw = Stopwatch.StartNew();
            while (!_serverReady && sw.ElapsedMilliseconds < 30000 && _driveError == null && !_stopPolling)
                Thread.Sleep(5);

            while (!_stopPolling)
            {
                if (_serverReady && _server != null)
                {
                    try
                    {
                        using (Py.GIL())
                        using (PyObject s = _server.InvokeMethod("get_state_json"))
                            _latestStateJson = s.As<string>();
                    }
                    catch (Exception e) { _pollError = "poll: " + e; }
                }
                Thread.Sleep(40);
            }
        }
        catch (Exception e) { _pollError = "poll: " + e; }
        finally { _pollStopped = true; }
    }

    // GIL-free on main: decode the latest polled state and capture gen1/gen2 transitions.
    static void Pump()
    {
        string s = _latestStateJson;
        if (string.IsNullOrEmpty(s)) return;

        DepthSnapshotView v = DepthDecoder.Decode(s, IID);
        if (!v.HasDepth) return;

        if (!_gen1Seen &&
            v.Bids.Count == 2 && !D(v.Bids[0].Price, 10.0) && !D(v.Bids[1].Price, 9.9) &&
            v.Asks.Count == 2 && !D(v.Asks[0].Price, 10.1))
        {
            _g1B0P = v.Bids[0].Price; _g1B0S = v.Bids[0].Size; _g1B1P = v.Bids[1].Price;
            _g1A0P = v.Asks[0].Price; _g1A0S = v.Asks[0].Size; _g1A1P = v.Asks[1].Price;
            _gen1Seen = true;
        }

        // gen2 = full replacement: 1 bid @11.0, 3 asks, and gen1's 10.0 top GONE.
        if (_gen1Seen && !_gen2Seen &&
            v.Bids.Count == 1 && !D(v.Bids[0].Price, 11.0) && v.Asks.Count == 3)
        {
            bool oldPresent = false;
            foreach (var b in v.Bids) if (!D(b.Price, 10.0)) oldPresent = true;
            if (!oldPresent)
            {
                _g2B0P = v.Bids[0].Price; _g2B0S = v.Bids[0].Size; _g2BCount = v.Bids.Count;
                _g2A0P = v.Asks[0].Price; _g2A2P = v.Asks[2].Price; _g2A2S = v.Asks[2].Size;
                _g2ACount = v.Asks.Count;
                _gen2Seen = true;
            }
        }
    }

    static long HeartbeatUntil(Func<bool> done, int budgetMs)
    {
        var total = Stopwatch.StartNew();
        var beat  = Stopwatch.StartNew();
        long maxStall = 0;
        while (!done() && total.ElapsedMilliseconds < budgetMs)
        {
            long gap = beat.ElapsedMilliseconds;
            if (gap > maxStall) maxStall = gap;
            beat.Restart();
            Pump();
            Thread.Sleep(2);
        }
        return maxStall;
    }

    // No _driveError short-circuit (unlike a fills-style wait): this is used by the drive
    // worker for both the gen handshakes AND the teardown's `WaitFlag(() => _pollStopped)`.
    // The latter MUST actually wait for the poll worker to exit before close(), so an early
    // _driveError out would let close() race get_state_json() on the error path. (_driveError
    // is only ever set by this same drive thread, so an early-out would be dead for the gen
    // waits anyway.)
    static bool WaitFlag(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    static string ValidateRealtime(bool driveJoined, bool pollJoined, long maxStall, bool heartbeatTimedOut)
    {
        if (_driveError != null) return _driveError;
        if (heartbeatTimedOut)
            return $"heartbeat budget (120s) expired before drive completed (maxStall={maxStall}ms)";
        if (!driveJoined) return "drive worker did not join within budget";
        if (maxStall >= MAX_STALL_MS) return $"main heartbeat stalled {maxStall}ms (>= {MAX_STALL_MS}ms)";
        if (!pollJoined) return "poll worker did not join (teardown ordering)";

        if (!_gen1Seen) return "gen1 depth never decoded (pollError=" + (_pollError ?? "none") + ")";
        if (D(_g1B0P, 10.0) || D(_g1B0S, 100.0) || D(_g1B1P, 9.9)) return "gen1 bids wrong";
        if (D(_g1A0P, 10.1) || D(_g1A0S, 150.0) || D(_g1A1P, 10.2)) return "gen1 asks wrong";

        if (!_gen2Seen) return "gen2 full-replacement never decoded";
        if (_g2BCount != 1 || D(_g2B0P, 11.0) || D(_g2B0S, 50.0)) return "gen2 bid replacement wrong";
        if (_g2ACount != 3 || D(_g2A0P, 11.1) || D(_g2A2P, 11.3) || D(_g2A2S, 80.0)) return "gen2 asks wrong";
        return null;
    }
}
