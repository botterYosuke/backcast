// BackendErrorCodes.cs — single C#-side registry of the error_code strings the in-proc Python
// backend returns from list_instruments / venue_login / strategy lifecycle RPCs.
//
// The Python side is authoritative (the strings are LITERALLY produced by `engine._backend_impl`
// and `engine.live.*`). This file mirrors them as `const string` so the C# call sites pattern
// match against a single source of truth and so renames are a one-edit fix on C#.
//
// CONTRACT: when you rename a constant here, also rename the matching string in the cited Python
// site. When you ADD a new code in Python, ADD it here too — the BackendAvailableInstrumentsProvider
// (and any future picker / venue UI) MapError switches against these constants, and adding the
// pair lets the compiler catch missing-case omissions in the switch via the const-folding pattern.
// The Slice review F9 finding is that without this registry, new Python codes silently fall into
// `default:` and surface their raw string in the UI.

public static class BackendErrorCodes
{
    // ---- C#-injected (no Python counterpart) -----------------------------------------------
    // The host returns these BEFORE the Python RPC runs (server not yet built, or the pythonnet
    // call itself threw). The picker provider maps both to user-visible status placeholders.
    public const string ServerNotReady = "SERVER_NOT_READY";
    public const string RpcError = "RPC_ERROR";

    // ---- Local universe (engine._backend_impl._list_instruments_local) ---------------------
    // Source: jquants_listed_info.read_listed_snapshot returned None because
    // BACKCAST_JQUANTS_DUCKDB_ROOT is unset or `<root>/listed_info.duckdb` is missing.
    public const string LocalUniverseUnavailable = "LOCAL_UNIVERSE_UNAVAILABLE";

    // ---- Live universe (engine._backend_impl._list_instruments_live) -----------------------
    // Source codes for the venue-fetch path (instruments_store cold miss / scheduler warming /
    // adapter that doesn't enumerate its instrument master).
    public const string LiveVenueNotLoggedIn = "LIVE_VENUE_NOT_LOGGED_IN";
    public const string LiveUniverseUnsupported = "LIVE_UNIVERSE_UNSUPPORTED";
    public const string LiveUniversePending = "LIVE_UNIVERSE_PENDING";
}
