// VenueConnectionViewModel.cs — issue #21 "Venue login and secret flow" (durable tier)
//
// The badge authority for venue connection state. Per findings 0012 D6 / CONTEXT.md
// "venue 接続状態", the SOLE continuous canonical is the get_state_json poll: its
// top-level venue_state (DISCONNECTED/AUTHENTICATING/CONNECTED/SUBSCRIBED/
// RECONNECTING/ERROR) and venue_id (present only while connected — the engine already
// derives it from the connection state to prevent a stale badge).
//
// venue_login's ACK is the immediate login result (set via ApplyLoginAck). A
// VenueLogoutDetected backend event is a NOTICE only (prompt re-login) — it does NOT
// flip the badge to Disconnected; the badge waits for the poll to converge. The poll
// is the single source of truth, so ApplyStatePoll always wins over the notice.
//
// JsonUtility ignores the (many) other fields in get_state_json and binds only the
// two top-level scalars declared on StateDto.
using UnityEngine;

public class VenueConnectionViewModel
{
    // Canonical, poll-derived. Empty venue_id => no venue badge (not connected).
    public string VenueState { get; private set; } = "DISCONNECTED";
    public string VenueId { get; private set; }

    // Connected band — mirrors the engine rule (_backend_impl.py: venue_id is loaded
    // only for these states). The connect/disconnect menu and the logout-disable
    // guard (D7) read IsConnected.
    public bool IsConnected =>
        VenueState == "CONNECTED" || VenueState == "SUBSCRIBED" || VenueState == "RECONNECTING";

    public bool IsAuthenticating => VenueState == "AUTHENTICATING";
    public bool IsError => VenueState == "ERROR";

    public long PollCount { get; private set; }

    // login ACK observability (immediate result; superseded by the next poll).
    public bool HasLoginAck { get; private set; }
    public bool LastLoginAckOk { get; private set; }
    public string LastLoginAckError { get; private set; }

    // re-login NOTICE (does not change the badge — see ApplyLogoutNotice).
    public long LogoutNoticeCount { get; private set; }
    public string LastLogoutNoticeVenue { get; private set; }

    [System.Serializable] class StateDto
    {
        public string venue_state;
        public string venue_id;
    }

    string _lastStateJson;

    /// Canonical badge update from a get_state_json string (D6). Tolerates the full
    /// nested state object; only the two top-level scalars are bound. The poll lane
    /// repeats the same snapshot string between backend changes, so we skip an
    /// identical payload — no wasted JsonUtility parse per frame, and PollCount counts
    /// distinct snapshots rather than call frequency.
    public void ApplyStatePoll(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson)) return;
        if (stateJson == _lastStateJson) return;
        _lastStateJson = stateJson;
        PollCount++;
        var d = JsonUtility.FromJson<StateDto>(stateJson);
        if (d == null) return;
        VenueState = string.IsNullOrEmpty(d.venue_state) ? "DISCONNECTED" : d.venue_state;
        // venue_id is null/absent unless connected — mirror it verbatim (the engine
        // already gates it on the connection band).
        VenueId = string.IsNullOrEmpty(d.venue_id) ? null : d.venue_id;
    }

    /// venue_login RPC ACK — the immediate login result. The badge still defers to
    /// the next ApplyStatePoll; this only exposes the ack for UI feedback.
    public void ApplyLoginAck(bool success, string errorCode)
    {
        HasLoginAck = true;
        LastLoginAckOk = success;
        LastLoginAckError = success ? null : errorCode;
    }

    /// VenueLogoutDetected NOTICE (D6): record it for a re-login hint but DO NOT
    /// touch VenueState/VenueId — the poll remains the canonical badge source.
    public void ApplyLogoutNotice(LiveVenueLogoutEvent ev)
    {
        LogoutNoticeCount++;
        LastLogoutNoticeVenue = ev.Venue;
    }
}
