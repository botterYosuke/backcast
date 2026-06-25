// BackendAvailableInstrumentsProvider.cs — sidebar `+Add` picker supply over the in-proc backend.
//
// Replaces the MockAvailableInstrumentsProvider hardcoded list (BackcastWorkspaceRoot 2026 cutover)
// with the real two-source supply:
//   - Replay → engine.inproc_server.list_instruments("local", replayEndDate)
//             → reads $BACKCAST_JQUANTS_DUCKDB_ROOT/listed_info.duckdb at the point-in-time snapshot
//               with MAX(Date) <= replayEndDate (owner decision 2026-06-21).
//   - Live   → engine.inproc_server.list_instruments("live", "")
//             → store-first venue universe (instruments_store snapshot, tachibana fetch on miss).
//
// CACHING. The picker's Query() runs every BuildList tick while the picker is visible. We cache
// by (mode, endDate) and only re-fetch when the key changes — Slice review F1 made the cache
// SUCCESS-ONLY: transient statuses (SERVER_NOT_READY while the host warms up, LIVE_UNIVERSE_PENDING
// while the instruments scheduler refreshes) DO NOT get written to the cache, so the next tick
// re-fires the fetch and the picker self-heals once the warmup completes. Only Ready / Empty and
// terminal Errors (LOCAL_UNIVERSE_UNAVAILABLE / LIVE_VENUE_NOT_LOGGED_IN / etc.) stick.
//
// SINGLE-FLIGHT. Per-key (`_inFlightKey`), not global (Slice review F3): a Query for a NEW key
// arriving while the previous fetch is still running fires its own fetch immediately, so rapid
// scenario.end scrubs don't waste a round-trip of Loading-flicker.
//
// THREADING. Hitting Python under the GIL on the UI thread would block ~100ms per fetch (DuckDB
// scan of ~4.4k listed rows), so the fetch runs on a background thread. The provider observes
// `WorkspaceEngineHost.IsClosing` and drops the result if teardown started during the fetch
// (Slice review F6), so a late-completing fetch can't write into a stale cache or call into a
// disposed `_server` after venue_logout has run.

using System;
using System.Collections.Generic;
using System.Threading;

public sealed class BackendAvailableInstrumentsProvider : IAvailableInstrumentsProvider
{
    // Throttle re-fires when the last fetch returned a TRANSIENT status, so a permanently broken
    // RPC (dead _server / pythonnet glitch) doesn't spawn a fresh PickerInstrumentFetch every
    // BuildList tick (~60/s) while the picker is visible. 500ms = ~30 frames; user-imperceptible
    // for a warmup that takes hundreds of ms, but caps thread churn at ~2/s in the failure mode.
    const long TRANSIENT_RETRY_COOLDOWN_MS = 500;

    readonly WorkspaceEngineHost _host;
    readonly object _lock = new object();

    string _cachedKey = null;
    AvailableInstrumentsResult _cachedResult;
    string _inFlightKey;                    // null when no fetch running; Slice review F3
    string _transientKey;                   // last key that returned a transient status
    long _transientAtMs;                    // _clock.ElapsedMilliseconds of last transient result

    // Monotonic ms source for the transient-retry cooldown. Was Environment.TickCount64, which the
    // Unity .NET profile doesn't expose; Stopwatch.ElapsedMilliseconds is the same monotonic-long-ms
    // contract (only the delta between two reads is used, so a shared static clock is fine).
    static readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    public BackendAvailableInstrumentsProvider(WorkspaceEngineHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public AvailableInstrumentsResult Query(UniverseSourceMode mode, string replayEndDate)
    {
        // Replay no longer blocks on an unset scenario.end (owner request 2026-06-22, findings 0084):
        // an empty end is sent to the backend as "", which serves the LATEST listed_info snapshot so
        // the picker shows the current universe instead of "Set scenario.end first". A set end that
        // predates every snapshot likewise falls back to latest (engine `_list_instruments_local`).
        // A set, in-range end still scopes point-in-time. (The prune gate keeps its OWN EndUnset→no-prune
        // guard — UniversePruneGate — so this is picker-only and does not widen any destructive prune.)
        string source = mode == UniverseSourceMode.Replay ? "local" : "live";
        string endDate = mode == UniverseSourceMode.Replay ? (replayEndDate ?? "") : "";
        string key = source + "|" + endDate;

        lock (_lock)
        {
            if (key == _cachedKey)
                return _cachedResult;
            if (_inFlightKey == key)
                return AvailableInstrumentsResult.Loading;
            // Throttle re-fires after a transient (server-warmup / RPC glitch) result so a
            // permanently broken backend doesn't spawn a thread every BuildList tick.
            if (_transientKey == key && (_clock.ElapsedMilliseconds - _transientAtMs) < TRANSIENT_RETRY_COOLDOWN_MS)
                return AvailableInstrumentsResult.Loading;
            _inFlightKey = key;
        }

        // Background fetch — never block the UI thread under the GIL. The picker's next BuildList
        // tick (re-Query) will read the populated cache.
        var t = new Thread(() => Fetch(source, endDate, key)) { IsBackground = true, Name = "PickerInstrumentFetch" };
        t.Start();
        return AvailableInstrumentsResult.Loading;
    }

    void Fetch(string source, string endDate, string key)
    {
        AvailableInstrumentsResult result;
        bool isTransient;
        try
        {
            var rpc = _host.InvokeListInstruments(source, endDate);
            if (rpc.Success)
            {
                result = rpc.InstrumentIds.Length == 0
                    ? AvailableInstrumentsResult.Empty
                    : AvailableInstrumentsResult.Ready(rpc.InstrumentIds);
                isTransient = false;
            }
            else
            {
                result = MapError(rpc.ErrorCode, out isTransient);
            }
        }
        catch (Exception ex)
        {
            // An unexpected exception (PythonException etc.) is conservatively treated as transient
            // so a one-off pythonnet glitch doesn't permanently poison the cache.
            result = AvailableInstrumentsResult.Error(ex.Message);
            isTransient = true;
        }

        // Slice review F6: drop late results from after teardown started — the cache is about to
        // be discarded and the host's _server is about to be disposed; nothing should observe us.
        if (_host.IsClosing)
        {
            lock (_lock) { if (_inFlightKey == key) _inFlightKey = null; }
            return;
        }

        lock (_lock)
        {
            // Slice review F1: skip the cache write for transient statuses so the next Query re-
            // fires the fetch — the picker recovers automatically once the host / scheduler finishes
            // warming up. Only success and terminal errors stick.
            if (isTransient)
            {
                _transientKey = key;
                _transientAtMs = _clock.ElapsedMilliseconds;
            }
            else
            {
                _cachedKey = key;
                _cachedResult = result;
            }
            if (_inFlightKey == key) _inFlightKey = null;
        }
    }

    // `isTransient` lets the caller skip caching for warm-up statuses (Slice review F1).
    // `public` so the AFK E2E runner can gate the error-code→status mapping directly (the load-
    // bearing distinction between NotConnected and Unsupported lives here — findings 0103).
    public static AvailableInstrumentsResult MapError(string errorCode, out bool isTransient)
    {
        switch (errorCode ?? "")
        {
            case BackendErrorCodes.ServerNotReady:
            case BackendErrorCodes.LiveUniversePending:
                isTransient = true;
                return AvailableInstrumentsResult.Loading;
            case BackendErrorCodes.RpcError:
                // Pythonnet/marshalling glitch — likely one-off; retry on the next Query tick.
                isTransient = true;
                return AvailableInstrumentsResult.Error(errorCode);
            case BackendErrorCodes.LiveVenueNotLoggedIn:
                isTransient = false;
                return AvailableInstrumentsResult.NotConnected;
            case BackendErrorCodes.LiveUniverseUnsupported:
                // The venue IS logged in but cannot enumerate instruments (kabu MVP). Do NOT
                // collapse into NotConnected — that produced the "Connected: KABU" badge vs.
                // "Venue not connected" sidebar contradiction (findings 0103).
                isTransient = false;
                return AvailableInstrumentsResult.Unsupported;
            case BackendErrorCodes.LocalUniverseUnavailable:
                isTransient = false;
                return AvailableInstrumentsResult.Error("BACKCAST_JQUANTS_DUCKDB_ROOT / listed_info.duckdb not configured");
            default:
                isTransient = false;
                return AvailableInstrumentsResult.Error(string.IsNullOrEmpty(errorCode) ? "unknown error" : errorCode);
        }
    }
}
