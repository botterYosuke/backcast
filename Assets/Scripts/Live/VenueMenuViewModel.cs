// VenueMenuViewModel.cs — issue #21 "Venue login and secret flow" (durable tier)
//
// Presentation logic for the venue connect/disconnect menu (findings 0012 D4/D6/D7).
// The menu owns NO credential form: connect just fires venue_login with
// credentials_source="prompt" for BOTH venues — the Python login_dialog_runner tkinter
// subprocess collects the credentials (D4). The badge is derived from the
// VenueConnectionViewModel poll (D6 canonical), and disconnect is gated by the
// LiveLogoutCoordinator so it is disabled while a write is in flight (D7 Wall 1).
//
// Pure logic (no UGUI) so the AFK gate drives it deterministically; the actual buttons
// + badge GameObjects live in the owner-manual playmode harness and bind to this.
using System;

public struct VenueConnectRequest
{
    public string Venue;              // "TACHIBANA" | "KABU"
    public string CredentialsSource;  // always "prompt" (D4)
    public string EnvironmentHint;    // tachibana=demo, kabu=verify (D3 defaults)
}

public class VenueMenuViewModel
{
    readonly VenueConnectionViewModel _conn;
    readonly LiveLogoutCoordinator _coord;

    public VenueMenuViewModel(VenueConnectionViewModel conn, LiveLogoutCoordinator coord)
    {
        _conn = conn;
        _coord = coord;
    }

    // Connect is offered only when fully disconnected — not mid-auth, not connected.
    public bool CanConnect => !_conn.IsConnected && !_conn.IsAuthenticating;

    // Disconnect requires a live connection AND a quiet write lane / closed secret modal
    // (D7 Wall 1). This is the UI half of the two-layer logout defense.
    public bool CanDisconnect => _conn.IsConnected && _coord.CanUserLogout;

    /// Both venues use the prompt path — the tkinter subprocess owns credential entry.
    public string CredentialsSourceFor(string venue) => "prompt";

    public string EnvironmentHintFor(string venue)
    {
        switch (venue)
        {
            case "TACHIBANA": return "demo";   // tachibana demo (D3)
            case "KABU": return "verify";      // kabu 検証 (D3)
            default: return "";
        }
    }

    public VenueConnectRequest BuildConnectRequest(string venue) => new VenueConnectRequest
    {
        Venue = venue,
        CredentialsSource = CredentialsSourceFor(venue),
        EnvironmentHint = EnvironmentHintFor(venue),
    };

    /// Badge text derived from the poll-canonical connection state (D6). A logout NOTICE
    /// surfaces a re-login hint without changing the underlying state (badge waits poll).
    public string BadgeText
    {
        get
        {
            switch (_conn.VenueState)
            {
                case "AUTHENTICATING": return "Connecting…";
                case "CONNECTED":
                case "SUBSCRIBED":
                case "RECONNECTING":
                    return _conn.VenueId != null ? $"Connected: {_conn.VenueId}" : "Connected";
                case "ERROR": return "Connection error";
                default: return "Disconnected";
            }
        }
    }

    /// True when a VenueLogoutDetected notice is newer than the last connected badge —
    /// the menu shows a "session lost — reconnect" hint while the poll catches up (D6).
    public bool ShowReloginHint => _conn.LogoutNoticeCount > 0 && !_conn.IsConnected;
}
