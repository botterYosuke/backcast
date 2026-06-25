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
// declared scalars on StateDto (currently four: venue_state / venue_id /
// ec_ws_subscribed / last_event_ws_recv_ts_ms — the latter two added in issue #85
// to gate place_order on EC WS handshake success, see findings 0053 §issue#85).
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

    // #85 Q1 (A'): EC WS (注文約定通知 push) handshake 成立シグナル。SUBSCRIBED badge は
    // market-data 購読成立でしか立たないので、market-data 非購読で発注する経路
    // (TachibanaLiveE2ERunner / 将来の自動発注) では SUBSCRIBED を gate にできない。
    // 発注前 fail-fast は IsConnected (CONNECTED) + EcWsSubscribed の AND で判定する。
    public bool EcWsSubscribed { get; private set; }

    // EC WS 直近受信時刻 (UTC ms)。0 = まだ受信していない (sentinel)。Unity JsonUtility は
    // Nullable<long> を扱えないため long + 0 sentinel で表現する (UTC 0 = 1970-01-01、立花が
    // 送り得る最小 ts と衝突しない)。
    public long LastEventWsRecvTsMs { get; private set; }

    /// EC WS で 1 度でもフレームを受信したか。`LastEventWsRecvTsMs > 0` の derived。
    /// staleness 比較 (cancel race re-check 等) で magic-number を散らさないための糖衣。
    public bool HasEventWsRecvTs => LastEventWsRecvTsMs > 0;

    // #34 D5 (findings 0101): 接続中 venue の訂正が cancel+replace (非 atomic・kabu) か。
    // Python (active adapter → get_state_json) が宣言する単一 capability を poll で受ける。
    // 訂正 modal がこれを読んで警告+ack gate を出す (C# は venue 名分岐しない)。未接続=false。
    public bool ModifyIsCancelReplace { get; private set; }

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
        // #85: EC WS handshake signal — null in JSON ⇒ JsonUtility yields default
        // (false / 0). 0 sentinel for last_event_ws_recv_ts_ms means "not yet received".
        public bool ec_ws_subscribed;
        public long last_event_ws_recv_ts_ms;
        // #34 D5: 接続中のみ true になり得る (engine が connected gate 下で emit)。
        public bool modify_is_cancel_replace;
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
        // #85 Q1 (A'): EC WS signal — adapter property の None ⇒ JSON null ⇒ 0/false。
        EcWsSubscribed = d.ec_ws_subscribed;
        LastEventWsRecvTsMs = d.last_event_ws_recv_ts_ms;
        // #34 D5: 訂正 atomicity capability (接続中のみ true・未接続/旧 payload は false)。
        ModifyIsCancelReplace = d.modify_is_cancel_replace;
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
