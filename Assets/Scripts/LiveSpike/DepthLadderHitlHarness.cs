// DepthLadderHitlHarness.cs — issue #26 "S9a orderbook 先行" (THROWAWAY playmode render leg)
//
// The owner-only, DEFAULT-DISABLED real-GPU render leg that complements the headless AFK gate
// (DepthDecodeProbe). The probe value-asserts the durable DepthDecoder under -batchmode; this
// harness lets the owner SEE the bid/ask ladder render on a real Unity frame AND watch it update
// in real time as the mock venue streams fresh depth. It runs the SAME C#-owned InprocLiveServer
// façade drive + mock injection (findings 0011 D2 / 0012).
//
// #54 (findings 0024): the ladder render was extracted out of this harness's OnGUI into the
// production uGUI component DepthLadderView. This harness now BUILDS a Canvas + DepthLadderView and
// feeds it the decoded DepthSnapshotView each changed tick — OnGUI is fully removed (the diagnostic
// status line is a uGUI Text now, mirroring how #53 left ScenarioStartupHitlHarness OnGUI-free).
//
// PLAY OWNERSHIP: spawned ONLY via Tools > Backcast > Depth Ladder HITL (no auto-bootstrap), so it
// never collides with the single Play owner (mirrors LiveAdapterTracerHitlHarness). Enter Play mode,
// run the menu, watch the ladder populate then drift each tick, then exit Play.
//
// RENDER NOTE: the decoder restores WIRE ORDER faithfully (bids 降順 / asks 昇順). For the
// conventional ladder LOOK (best prices hugging the spread) DepthLadderView reverses the asks for
// display only — a presentation choice, NOT a re-sort of the decoded data.
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;
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

    // decoded ladder snapshot — written on main (Pump in Update), rendered via DepthLadderView.
    DepthSnapshotView _ladder = DepthSnapshotView.Empty;
    int _updateCount;

    // #54: production ladder widget + a uGUI status line (no OnGUI). Built on main in Start().
    DepthLadderView _ladderView;
    Text _statusText;

    Thread _drive, _poll;
    IntPtr _mainThreadState;
    bool _engineStarted;

    void Start()
    {
        BuildUi();   // #54: build the uGUI ladder + status line up front so init errors still show.
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

    // #54: build the uGUI scene — a Canvas with the production DepthLadderView and a status line.
    // Replaces the old OnGUI render (findings 0024). Built on main in Start().
    void BuildUi()
    {
        var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        var canvasGo = new GameObject("DepthLadderCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        canvasGo.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;

        var root = new GameObject("Root", typeof(RectTransform)).GetComponent<RectTransform>();
        root.SetParent(canvasGo.transform, false);
        root.anchorMin = Vector2.zero; root.anchorMax = Vector2.one;
        root.offsetMin = Vector2.zero; root.offsetMax = Vector2.zero;

        // status line (top strip) — uGUI Text. Color reads the ACTIVE theme at build (instance read,
        // not a type-init static capture). This harness has no theme toggle, so there is no runtime
        // switch to follow; the production ladder colors live in DepthLadderView, which self-subscribes.
        var statusGo = new GameObject("status", typeof(RectTransform), typeof(Text));
        var statusRt = statusGo.GetComponent<RectTransform>();
        statusRt.SetParent(root, false);
        statusRt.anchorMin = new Vector2(0f, 1f); statusRt.anchorMax = new Vector2(1f, 1f);
        statusRt.pivot = new Vector2(0.5f, 1f);
        statusRt.sizeDelta = new Vector2(-24f, 24f); statusRt.anchoredPosition = new Vector2(0f, -8f);
        _statusText = statusGo.GetComponent<Text>();
        _statusText.font = font; _statusText.fontSize = 15; _statusText.supportRichText = false;
        _statusText.alignment = TextAnchor.MiddleLeft;
        _statusText.horizontalOverflow = HorizontalWrapMode.Overflow;
        _statusText.color = ThemeService.Current.colors.text;

        // ladder area below the status strip (left-aligned column).
        var areaGo = new GameObject("ladder", typeof(RectTransform));
        var area = areaGo.GetComponent<RectTransform>();
        area.SetParent(root, false);
        area.anchorMin = new Vector2(0.02f, 0f); area.anchorMax = new Vector2(0.5f, 1f);
        area.offsetMin = new Vector2(0f, 8f); area.offsetMax = new Vector2(0f, -36f);
        _ladderView = areaGo.AddComponent<DepthLadderView>();
        _ladderView.Build(area);
        _ladderView.Render(_ladder);   // initial "(no board)" until the first snapshot arrives.
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

    // GIL-free on main: decode the latest polled state and feed the production DepthLadderView.
    void Pump()
    {
        if (_statusText != null)
            _statusText.text = $"{IID} — mock venue / LiveAuto (backcast #54)   status: {_status}   updates: {_updateCount}";

        string s = _latestStateJson;
        if (string.IsNullOrEmpty(s)) return;
        DepthSnapshotView v = DepthDecoder.Decode(s, IID);

        // "board changed" vs the currently-shown _ladder (compared BEFORE reassigning). The mock
        // advances timestamp_ms each tick (HITL observed updates:40), so this fires every drift tick;
        // the HasDepth flip also catches the Live↔Replay (board↔no-board) transition.
        bool changed = v.HasDepth != _ladder.HasDepth || v.TimestampMs != _ladder.TimestampMs;
        if (v.HasDepth && changed) _updateCount++;
        _ladder = v;

        // Retained uGUI: re-render only on a changed board (avoid per-frame rebuild). The mock streams
        // DEPTH (no trades), so there is no real LAST trade price — feed the best bid/ask MID as a
        // visual stand-in so the TTWR LAST row is exercised and drifts with the board (#54 follow-up).
        if (_ladderView != null && changed)
        {
            double? mid = (v.HasDepth && v.Bids.Count > 0 && v.Asks.Count > 0)
                ? (v.Bids[0].Price + v.Asks[0].Price) / 2.0
                : (double?)null;
            _ladderView.Render(v, mid);
        }
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
