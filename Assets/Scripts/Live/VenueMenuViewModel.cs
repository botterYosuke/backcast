// VenueMenuViewModel.cs — issue #21 "Venue login and secret flow" (durable tier)
//
// Presentation logic for the venue connect/disconnect menu (findings 0012 D6/D7).
// #181/ADR-0040: the menu owns NO credential form — connect opens the Unity uGUI login
// modal (BackcastWorkspaceRoot.OnVenueConnect → OpenVenueLoginModal → submit_venue_login);
// the retired tkinter "prompt" dialog is gone. The badge is derived from the
// VenueConnectionViewModel poll (D6 canonical), and disconnect is gated by the
// LiveLogoutCoordinator so it is disabled while a write is in flight (D7 Wall 1).
//
// Pure logic (no UGUI) so the AFK gate drives it deterministically.
using System.Collections.Generic;

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

    // Per-(venue, env) connect enablement for the 4 menu variants. ADR-0027: the prod-allow
    // env gate (KABU_ALLOW_PROD / TACHIBANA_ALLOW_PROD grey-out) is abolished — prod is no
    // longer special-cased, so every variant is enabled whenever the venue is connectable.
    // Production接続の可否はユーザーがダイアログで prod を選ぶこと・本物の prod 資格情報・
    // prod 本体の起動だけで決まる (D2)。signature は呼び出し側互換のため据え置く。
    public bool CanConnectEnv(string venue, string env) => CanConnect;

    // The 4 TTWR connect variants, in menu order (findings 0017 §6).
    public static readonly (string Venue, string Env, string Label)[] ConnectVariants =
    {
        ("TACHIBANA", "demo",   "Connect Tachibana (Demo)"),
        ("TACHIBANA", "prod",   "Connect Tachibana (Prod)"),
        ("KABU",      "verify", "Connect kabuStation (Verify)"),
        ("KABU",      "prod",   "Connect kabuStation (Prod)"),
    };

    // ADR-0021: the connect items the Venue menu surfaces, given the LIVE_VENUE filter. A null/empty
    // filter (LIVE_VENUE unset) surfaces ALL variants — plus the credential-less MOCK dev connect, but
    // only in the EDITOR (an unset shipped build must not offer MOCK) — because the menu now rebinds the
    // server's venue at login. An explicit venue (LIVE_VENUE pinned) surfaces ONLY that venue's variants;
    // a pinned MOCK surfaces the MOCK connect in EITHER build (#106 — pinning LIVE_VENUE=MOCK is an
    // explicit choice, so a player build must NOT dead-end on an empty menu). The isEditor gate therefore
    // governs only the UNSET dev-convenience item, never the pinned-MOCK escape hatch. This is
    // PRESENTATIONAL only, NOT an enforcement boundary: the backend still rebinds to any _KNOWN_VENUES
    // venue on a direct venue_login (e.g. ConnectConfigured / a scripted call) — the filter just keeps
    // the menu from offering venues a pinned deployment doesn't intend to surface. Pure logic so the AFK
    // gate drives it without building uGUI; MenuBarView.BuildVenueMenu renders exactly this list.
    public static List<(string Label, string Venue, string Env)> VisibleConnectItems(
        string filterVenue, bool isEditor)
    {
        string f = string.IsNullOrEmpty(filterVenue) ? null : filterVenue.ToUpperInvariant();
        var items = new List<(string, string, string)>();
        if (f == "MOCK" || (f == null && isEditor)) items.Add(("Connect MOCK (dev)", "MOCK", ""));
        foreach (var v in ConnectVariants)
            if (f == null || v.Venue == f) items.Add((v.Label, v.Venue, v.Env));
        return items;
    }

    // Disconnect requires a live connection AND a quiet write lane / closed secret modal
    // (D7 Wall 1). This is the UI half of the two-layer logout defense.
    public bool CanDisconnect => _conn.IsConnected && _coord.CanUserLogout;

    public string EnvironmentHintFor(string venue)
    {
        switch (venue)
        {
            case "TACHIBANA": return "demo";   // tachibana demo (D3)
            case "KABU": return "verify";      // kabu 検証 (D3)
            default: return "";
        }
    }

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
