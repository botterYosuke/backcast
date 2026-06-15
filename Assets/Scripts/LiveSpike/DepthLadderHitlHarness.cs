// DepthLadderHitlHarness.cs — issue #26 "S9a orderbook 先行" (THROWAWAY playmode render leg)
//
// The owner-only, DEFAULT-DISABLED real-GPU render leg that complements the headless AFK gate
// (DepthDecodeProbe). The probe value-asserts the durable DepthDecoder under -batchmode; this
// harness lets the owner SEE the bid/ask ladder render on a real Unity frame AND watch it update
// in real time as the mock venue streams fresh depth. It runs the SAME C#-owned InprocLiveServer
// façade drive + mock injection (findings 0011 D2 / 0012), but instead of asserting + exiting it
// renders the decoded DepthSnapshotView via OnGUI each frame.
//
// PLAY OWNERSHIP: spawned ONLY via Tools > Backcast > Depth Ladder HITL (no auto-bootstrap), so it
// never collides with the single Play owner (mirrors LiveAdapterTracerHitlHarness). Enter Play mode,
// run the menu, watch the ladder populate then drift each tick, then exit Play.
//
// RENDER NOTE: the decoder restores WIRE ORDER faithfully (bids 降順 / asks 昇順). For the
// conventional ladder LOOK (best prices hugging the spread) this VIEW reverses the asks for display
// only — a presentation choice, NOT a re-sort of the decoded data.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;
using Python.Runtime;
using Debug = UnityEngine.Debug;

public class DepthLadderHitlHarness : MonoBehaviour
{
    const string IID = "8918.TSE";

    readonly LiveBackendEventSink _sink = new LiveBackendEventSink();   // unused values; keeps engine push path happy

    volatile PyObject _server;
    volatile PyObject _mi;
    volatile bool   _serverReady;
    volatile bool   _stopPolling;
    volatile bool   _pollStopped;
    volatile bool   _driveDone;
    volatile string _driveError;
    volatile string _pollError;
    volatile string _runId;
    volatile string _status = "starting…";
    volatile string _latestStateJson;

    // decoded ladder snapshot — written on main (Pump in Update), read in OnGUI.
    DepthSnapshotView _ladder = DepthSnapshotView.Empty;
    int _updateCount;

    Thread _drive, _poll;
    IntPtr _mainThreadState;
    bool _engineStarted;

    void Start()
    {
        try
        {
            // SINGLE PLAY-OWNER guard (mirrors LiveAdapterTracerHitlHarness): a second
            // PythonEngine.Initialize() on top of a foreign bootstrap double-inits / contends
            // the GIL. If Python is already up, FAIL LOUDLY rather than pile on.
            if (PythonEngine.IsInitialized)
            {
                _driveError = "double-init: PythonEngine already initialized by another bootstrap " +
                              "(only one Play-owner allowed)";
                _status = "ERROR: " + _driveError;
                Debug.LogError("[DEPTH LADDER HITL FAIL] " + _driveError);
                return;
            }

            PythonRuntimeLocator.ConfigureBeforeInitialize();
            PythonEngine.Initialize();
            _engineStarted = true;
            _mainThreadState = PythonEngine.BeginAllowThreads();

            _drive = new Thread(DriveWorker) { IsBackground = true, Name = "DepthLadderHitlDrive" };
            _poll  = new Thread(PollWorker)  { IsBackground = true, Name = "DepthLadderHitlPoll" };
            _drive.Start();
            _poll.Start();
            Debug.Log("[DEPTH LADDER HITL] Initialize OK; workers started; main renders GIL-free.");
        }
        catch (Exception e)
        {
            _driveError = "init: " + e;
            _status = "ERROR: " + e;
            Debug.LogError("[DEPTH LADDER HITL FAIL] init: " + e);
        }
    }

    void Update() => Pump();

    void OnGUI()
    {
        var title = new GUIStyle(GUI.skin.label) { fontSize = 16, richText = false };
        // issue #44: ladder colors source the theme (層2) — OnGUI re-reads each frame so a switch is live.
        var ask   = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = false, normal = { textColor = ThemeService.Current.status.ask } };
        var bid   = new GUIStyle(GUI.skin.label) { fontSize = 15, richText = false, normal = { textColor = ThemeService.Current.status.bid } };
        var mid   = new GUIStyle(GUI.skin.label) { fontSize = 13, richText = false, normal = { textColor = ThemeService.Current.colors.text_muted } };

        GUI.Label(new Rect(20, 16, 1000, 24),
            $"Depth Ladder HITL — mock venue / LiveAuto (backcast #26)   status: {_status}   updates: {_updateCount}", title);

        float x = 30, y = 56, row = 20;
        GUI.Label(new Rect(x, y, 400, row), $"{IID}    price          size", mid); y += row + 4;

        if (!_ladder.HasDepth)
        {
            GUI.Label(new Rect(x, y, 600, row), "(no board — Replay/None or not yet streamed)", mid);
            return;
        }

        // asks: reversed for display (highest ask top → lowest ask just above the spread).
        for (int k = _ladder.Asks.Count - 1; k >= 0; k--)
        {
            var lv = _ladder.Asks[k];
            GUI.Label(new Rect(x, y, 600, row),
                string.Format(CultureInfo.InvariantCulture, "ASK   {0,10:0.0####}   {1,10:0.###}", lv.Price, lv.Size), ask);
            y += row;
        }
        y += 6;
        GUI.Label(new Rect(x, y, 600, row), "————— spread —————", mid); y += row + 6;
        // bids: wire order (highest bid just below the spread, descending downward).
        for (int k = 0; k < _ladder.Bids.Count; k++)
        {
            var lv = _ladder.Bids[k];
            GUI.Label(new Rect(x, y, 600, row),
                string.Format(CultureInfo.InvariantCulture, "BID   {0,10:0.0####}   {1,10:0.###}", lv.Price, lv.Size), bid);
            y += row;
        }
    }

    // ---- drive: C# owns the live lifecycle, then streams an evolving ladder ----
    void DriveWorker()
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
                _status = "logging in…";

                string strategyId;
                using (PyObject reg = _server.InvokeMethod("register_live_strategy", new PyString(twin), new PyString(twin)))
                {
                    if (!reg["success"].As<bool>()) { _driveError = "register failed: " + reg; return; }
                    strategyId = reg["strategy_id"].As<string>();
                }
                using (PyObject login = _server.InvokeMethod("venue_login", new PyString("MOCK"), new PyString("env"), new PyString("")))
                    if (!login["success"].As<bool>()) { _driveError = "venue_login failed: " + login; return; }
                using (PyObject mode = _server.InvokeMethod("set_execution_mode", new PyString("LiveAuto")))
                    if (!mode["success"].As<bool>()) { _driveError = "set_execution_mode failed: " + mode; return; }
                using (PyObject start = _server.InvokeMethod("start_live_strategy", new PyString(strategyId), new PyString(IID), new PyString("MOCK")))
                {
                    if (!start["success"].As<bool>()) { _driveError = "start failed: " + start; return; }
                    _runId = start["run_id"].As<string>();
                }
                _status = "streaming depth…";
            }

            // Stream an evolving 5x5 ladder so the owner SEES real-time updates. The mid
            // drifts each tick; sizes wobble. ~40 ticks @ 350ms ≈ 14s of live updating.
            for (int t = 0; t < 40 && _driveError == null && !_stopPolling; t++)
            {
                double mid = 1000.0 + Math.Sin(t * 0.30) * 6.0;   // deterministic drift (no RNG)
                var bidP = new StringBuilder(); var bidS = new StringBuilder();
                var askP = new StringBuilder(); var askS = new StringBuilder();
                for (int k = 0; k < 5; k++)
                {
                    if (k > 0) { bidP.Append(','); bidS.Append(','); askP.Append(','); askS.Append(','); }
                    bidP.Append((mid - 0.5 - k).ToString("0.0", CultureInfo.InvariantCulture));
                    askP.Append((mid + 0.5 + k).ToString("0.0", CultureInfo.InvariantCulture));
                    bidS.Append((100 + 10 * k + (t % 5) * 5).ToString(CultureInfo.InvariantCulture));
                    askS.Append((120 + 10 * k + (t % 7) * 4).ToString(CultureInfo.InvariantCulture));
                }
                using (Py.GIL())
                    _mi.InvokeMethod("emit_depth_ladder", _server, new PyInt(t + 1),
                        new PyString(bidP.ToString()), new PyString(bidS.ToString()),
                        new PyString(askP.ToString()), new PyString(askS.ToString())).Dispose();
                Thread.Sleep(350);
            }
            _status = _stopPolling ? "stopping…" : "stream complete — board holds last snapshot";
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
                        if (_runId != null) { try { _server.InvokeMethod("stop_live_strategy", new PyString(_runId)).Dispose(); } catch {} }
                        try { _server.InvokeMethod("set_execution_mode", new PyString("Replay")).Dispose(); } catch {}
                        try { _server.InvokeMethod("venue_logout").Dispose(); } catch {}
                        try { _server.InvokeMethod("close").Dispose(); } catch {}
                    }
                }
                catch (Exception e) { Debug.LogWarning("[DEPTH LADDER HITL] teardown: " + e); }
            }
            if (_driveError != null) _status = "ERROR: " + _driveError;
            _driveDone = true;
        }
    }

    void PollWorker()
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

    // GIL-free on main: decode the latest polled state into the ladder view.
    void Pump()
    {
        string s = _latestStateJson;
        if (string.IsNullOrEmpty(s)) return;
        DepthSnapshotView v = DepthDecoder.Decode(s, IID);
        if (v.HasDepth && (v.TimestampMs != _ladder.TimestampMs || !_ladder.HasDepth))
            _updateCount++;
        _ladder = v;
    }

    bool WaitFlag(Func<bool> cond, int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (cond()) return true;
            Thread.Sleep(5);
        }
        return false;
    }

    void OnDestroy()
    {
        try
        {
            _stopPolling = true;
            bool driveStopped = true;
            if (_drive != null && _drive.IsAlive) driveStopped = _drive.Join(15000);
            if (_poll != null && _poll.IsAlive) _poll.Join(5000);

            if (!driveStopped)
                Debug.LogWarning("[DEPTH LADDER HITL] OnDestroy: drive worker did not stop; skipping Python shutdown to avoid GIL deadlock");
            else if (_engineStarted && _driveDone)
            {
                if (_mainThreadState != IntPtr.Zero) PythonEngine.EndAllowThreads(_mainThreadState);
                PythonEngine.Shutdown();
            }
            else if (_engineStarted)
                Debug.LogWarning("[DEPTH LADDER HITL] OnDestroy: drive did not finish cleanly; skipping Python shutdown (process exits next)");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[DEPTH LADDER HITL] OnDestroy cleanup: " + e);
        }
    }
}
