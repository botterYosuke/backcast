// LiveLogoutCoordinator.cs — issue #21 "Venue login and secret flow" (durable tier)
//
// The two-layer logout defense (findings 0012 D7). venue_logout tears down the live
// loop/adapter (runner.aclose()); running it while an order-write is in flight races
// the write and can orphan an order. Defense:
//
//   Wall 1 (UI): CanUserLogout is false while a write is in flight OR the secret modal
//                is open (place is blocked waiting for the secret). The menu disables
//                the disconnect button on this.
//   Wall 2 (control layer): even if Wall 1 is bypassed, RequestLogout() does NOT run
//                teardown while a write is in flight — it DEFERS, and becomes Ready
//                only once the order-write lane drains (write completes or PLACE_TIMEOUT
//                at 40s). The lifecycle path then runs the teardown sequence.
//
// This object is the pure decision core (no threads/pythonnet) so the AFK gate drives
// it deterministically. LiveRpcLanes wires BeginWrite/EndWrite around the order-write
// lane and SetSecretModalOpen from the modal, and polls ConsumePendingLogout() on the
// lifecycle path to run the actual teardown steps (D7 1–6). Thread-safe: all state is
// guarded by a single lock (the lanes call in from multiple threads).
using System;

public class LiveLogoutCoordinator
{
    readonly object _gate = new object();
    int _writesInFlight;
    bool _secretModalOpen;
    bool _logoutPending;     // a logout was requested but is waiting for the write lane
    bool _logoutReady;       // the deferred logout may now run the teardown sequence

    /// Wall 1: the UI disables disconnect while a write is in flight or the secret
    /// modal is open (place blocked on secret).
    public bool CanUserLogout
    {
        get { lock (_gate) { return _writesInFlight == 0 && !_secretModalOpen; } }
    }

    public int WritesInFlight { get { lock (_gate) { return _writesInFlight; } } }
    public bool SecretModalOpen { get { lock (_gate) { return _secretModalOpen; } } }
    public bool LogoutPending { get { lock (_gate) { return _logoutPending; } } }

    /// Bracket each order-write lane call (place/cancel/modify). EndWrite must run in
    /// a finally so a throwing/timing-out write still releases the gate.
    public void BeginWrite()
    {
        lock (_gate) { _writesInFlight++; }
    }

    public void EndWrite()
    {
        lock (_gate)
        {
            if (_writesInFlight > 0) _writesInFlight--;
            PromoteIfDrained();
        }
    }

    /// The secret modal drives this (open while place blocks on the secret).
    public void SetSecretModalOpen(bool open)
    {
        lock (_gate) { _secretModalOpen = open; }
    }

    /// Wall 2. Returns true if teardown may run immediately (no write in flight),
    /// false if it was DEFERRED until the order-write lane drains.
    public bool RequestLogout()
    {
        lock (_gate)
        {
            if (_writesInFlight == 0)
            {
                _logoutReady = true;
                _logoutPending = false;
                return true;
            }
            _logoutPending = true;   // defer — PromoteIfDrained will arm it
            return false;
        }
    }

    /// Lifecycle path polls this; returns true exactly once when a deferred (or
    /// immediate) logout is cleared to run the teardown sequence.
    public bool ConsumePendingLogout()
    {
        lock (_gate)
        {
            if (!_logoutReady) return false;
            _logoutReady = false;
            return true;
        }
    }

    void PromoteIfDrained()
    {
        // caller holds _gate
        if (_logoutPending && _writesInFlight == 0)
        {
            _logoutPending = false;
            _logoutReady = true;
        }
    }
}
