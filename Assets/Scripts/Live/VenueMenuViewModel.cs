// VenueMenuViewModel.cs — issue #21 "Venue login and secret flow" (durable tier)
//
// Presentation logic for the venue connect/disconnect menu (findings 0012 D4/D6/D7).
// The menu owns NO credential form: connect just fires venue_login with
// credentials_source="prompt" for BOTH venues — the Python tkinter dialog collects the
// credentials in-process (#122 removed the subprocess; findings 0093 supersedes 0012 D4).
// The badge is derived from the
// VenueConnectionViewModel poll (D6 canonical), and disconnect is gated by the
// LiveLogoutCoordinator so it is disabled while a write is in flight (D7 Wall 1).
//
// Pure logic (no UGUI) so the AFK gate drives it deterministically; the actual buttons
// + badge GameObjects live in the owner-manual playmode harness and bind to this.
using System;
using System.Collections.Generic;

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
    readonly Func<string, bool> _prodAllowed;

    // prodAllowed: gates the prod connect variants (#42 / findings 0017 §6). Defaults to the
    // SAME env mechanism the login dialog already uses — KABU_ALLOW_PROD / TACHIBANA_ALLOW_PROD
    // == "1" — so the menu reuses the existing guard rather than inventing a new one. Python
    // (PROD_NOT_ALLOWED / require_prod_env) remains the safety authority; this C# read is only
    // UX parity (grey-out). Injectable so the AFK probe drives prod gating deterministically.
    public VenueMenuViewModel(VenueConnectionViewModel conn, LiveLogoutCoordinator coord,
                              Func<string, bool> prodAllowed = null)
    {
        _conn = conn;
        _coord = coord;
        _prodAllowed = prodAllowed ?? DefaultProdAllowed;
    }

    static bool DefaultProdAllowed(string venue)
    {
        switch (venue)
        {
            case "TACHIBANA": return Environment.GetEnvironmentVariable("TACHIBANA_ALLOW_PROD") == "1";
            case "KABU": return Environment.GetEnvironmentVariable("KABU_ALLOW_PROD") == "1";
            default: return false;
        }
    }

    // Connect is offered only when fully disconnected — not mid-auth, not connected.
    public bool CanConnect => !_conn.IsConnected && !_conn.IsAuthenticating;

    // Per-(venue, env) connect enablement for the 4 menu variants. A prod variant is enabled
    // only when the venue is connectable AND its *_ALLOW_PROD flag is set (else grey-out),
    // mirroring the login dialog. Non-prod (demo/verify) is enabled whenever connectable.
    public bool CanConnectEnv(string venue, string env)
    {
        if (!CanConnect) return false;
        if (env == "prod") return _prodAllowed(venue);
        return true;
    }

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

    // Build a connect request for an explicit (venue, env) — the prod-aware path. The env hint
    // flows straight through to venue_login; the in-process prompt dialog owns credential entry
    // (#122/findings 0093, supersedes 0012 D4).
    public VenueConnectRequest BuildConnectRequest(string venue, string env) => new VenueConnectRequest
    {
        Venue = venue,
        CredentialsSource = CredentialsSourceFor(venue),
        EnvironmentHint = env,
    };

    // Disconnect requires a live connection AND a quiet write lane / closed secret modal
    // (D7 Wall 1). This is the UI half of the two-layer logout defense.
    public bool CanDisconnect => _conn.IsConnected && _coord.CanUserLogout;

    /// Both venues use the prompt path — the in-process tkinter dialog owns credential entry.
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
