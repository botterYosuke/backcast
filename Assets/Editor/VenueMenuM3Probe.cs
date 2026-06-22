// VenueMenuM3Probe.cs — issue #21 M3 focused gate (throwaway, pure C#)
// docs/findings/0012-venue-login-secret-flow.md (D4/D6/D7). Locks the venue-menu
// presentation logic: connect fires prompt for both venues, badge derives from the
// poll, and disconnect is disabled while a write is in flight. No pythonnet/venue.
//
//   <Unity> -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
//       -executeMethod VenueMenuM3Probe.Run
//
// Exit 0 => PASS ([VENUE MENU M3 PASS]), 1 => FAIL (self-failing gate).
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class VenueMenuM3Probe
{
    static readonly List<string> _fail = new List<string>();
    static void Check(bool cond, string msg) { if (!cond) _fail.Add(msg); }

    public static void Run()
    {
        try
        {
            BothVenuesUsePrompt();
            BadgeFollowsPoll();
            ConnectGate();
            DisconnectGatedByWrite();
            ReloginHintOnNotice();
            VenueMenuFilterByLiveVenue();
        }
        catch (Exception e) { _fail.Add("exception: " + e); }

        if (_fail.Count == 0)
        {
            Debug.Log("[VENUE MENU M3 PASS] prompt dispatch / poll badge / write-disable disconnect");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[VENUE MENU M3 FAIL]\n  - " + string.Join("\n  - ", _fail));
            EditorApplication.Exit(1);
        }
    }

    static (VenueMenuViewModel, VenueConnectionViewModel, LiveLogoutCoordinator) Make()
    {
        var conn = new VenueConnectionViewModel();
        var coord = new LiveLogoutCoordinator();
        return (new VenueMenuViewModel(conn, coord), conn, coord);
    }

    // D4: the menu never builds a credential form — both venues fire prompt; the
    // tkinter subprocess collects credentials. Env hints are demo/verify.
    static void BothVenuesUsePrompt()
    {
        var (m, _, _) = Make();
        var t = m.BuildConnectRequest("TACHIBANA");
        Check(t.CredentialsSource == "prompt", "tachibana must use prompt");
        Check(t.EnvironmentHint == "demo", "tachibana env should be demo");
        var k = m.BuildConnectRequest("KABU");
        Check(k.CredentialsSource == "prompt", "kabu must use prompt (NOT env — D4)");
        Check(k.EnvironmentHint == "verify", "kabu env should be verify");
    }

    // D6: badge derives from the poll-canonical connection state.
    static void BadgeFollowsPoll()
    {
        var (m, conn, _) = Make();
        Check(m.BadgeText == "Disconnected", "fresh badge should be Disconnected");
        conn.ApplyStatePoll("{\"venue_state\":\"AUTHENTICATING\",\"venue_id\":null}");
        Check(m.BadgeText == "Connecting…", "AUTHENTICATING badge mismatch: " + m.BadgeText);
        conn.ApplyStatePoll("{\"venue_state\":\"CONNECTED\",\"venue_id\":\"TACHIBANA\"}");
        Check(m.BadgeText == "Connected: TACHIBANA", "CONNECTED badge mismatch: " + m.BadgeText);
        conn.ApplyStatePoll("{\"venue_state\":\"ERROR\",\"venue_id\":null}");
        Check(m.BadgeText == "Connection error", "ERROR badge mismatch: " + m.BadgeText);
    }

    static void ConnectGate()
    {
        var (m, conn, _) = Make();
        Check(m.CanConnect, "disconnected should allow connect");
        conn.ApplyStatePoll("{\"venue_state\":\"AUTHENTICATING\",\"venue_id\":null}");
        Check(!m.CanConnect, "should not offer connect mid-auth");
        conn.ApplyStatePoll("{\"venue_state\":\"CONNECTED\",\"venue_id\":\"KABU\"}");
        Check(!m.CanConnect, "should not offer connect while connected");
    }

    // D7 Wall 1: disconnect disabled while a write is in flight.
    static void DisconnectGatedByWrite()
    {
        var (m, conn, coord) = Make();
        Check(!m.CanDisconnect, "disconnected → cannot disconnect");
        conn.ApplyStatePoll("{\"venue_state\":\"CONNECTED\",\"venue_id\":\"TACHIBANA\"}");
        Check(m.CanDisconnect, "connected + idle → can disconnect");
        coord.BeginWrite();
        Check(!m.CanDisconnect, "in-flight write must disable disconnect (D7 Wall 1)");
        coord.EndWrite();
        Check(m.CanDisconnect, "disconnect re-enabled after write drains");
        coord.SetSecretModalOpen(true);
        Check(!m.CanDisconnect, "open secret modal must disable disconnect");
    }

    // ADR-0021: LIVE_VENUE filters the Venue menu instead of LOCKING the server's venue. Unset → all
    // variants (+ editor-only MOCK dev), because the menu rebinds the venue at login. Pinned → only
    // that venue's variants (presentational — the backend still rebinds any known venue on a direct
    // venue_login). delete-the-filter litmus: if VisibleConnectItems stopped filtering, the pinned
    // cases would leak other venues → RED.
    static void VenueMenuFilterByLiveVenue()
    {
        // unset (null) in the editor → MOCK dev + both TTWR venues (4 variants).
        var all = VenueMenuViewModel.VisibleConnectItems(null, isEditor: true);
        Check(all.Count == 5, "unset/editor should surface MOCK dev + 4 variants, got " + all.Count);
        Check(all.Exists(i => i.Venue == "MOCK"), "unset/editor should include MOCK dev");
        Check(all.Exists(i => i.Venue == "TACHIBANA"), "unset should include Tachibana");
        Check(all.Exists(i => i.Venue == "KABU"), "unset should include kabu");

        // unset in a player build → no MOCK dev item (editor-only), still both real venues.
        var player = VenueMenuViewModel.VisibleConnectItems(null, isEditor: false);
        Check(player.Count == 4, "unset/player should surface 4 variants (no MOCK dev), got " + player.Count);
        Check(!player.Exists(i => i.Venue == "MOCK"), "MOCK dev is editor-only");

        // pinned TACHIBANA → only Tachibana variants; kabu + MOCK hidden.
        var t = VenueMenuViewModel.VisibleConnectItems("TACHIBANA", isEditor: true);
        Check(t.Count == 2, "pinned TACHIBANA should surface 2 variants, got " + t.Count);
        Check(t.TrueForAll(i => i.Venue == "TACHIBANA"), "pinned TACHIBANA must hide other venues");

        // pinned KABU (case-insensitive) → only kabu variants.
        var k = VenueMenuViewModel.VisibleConnectItems("kabu", isEditor: true);
        Check(k.Count == 2, "pinned KABU should surface 2 variants, got " + k.Count);
        Check(k.TrueForAll(i => i.Venue == "KABU"), "pinned KABU must hide other venues");

        // pinned MOCK → exactly the MOCK connect, in EITHER build (#106). Pinning LIVE_VENUE=MOCK is an
        // explicit deployment choice, so a player build must NOT dead-end on an empty menu — the isEditor
        // gate only governs the UNSET dev-convenience item above, never the pinned-MOCK escape hatch.
        var mockEditor = VenueMenuViewModel.VisibleConnectItems("MOCK", isEditor: true);
        Check(mockEditor.Count == 1 && mockEditor[0].Venue == "MOCK",
            "pinned MOCK in editor should surface only the MOCK connect, got " + mockEditor.Count);
        var mockPlayer = VenueMenuViewModel.VisibleConnectItems("MOCK", isEditor: false);
        Check(mockPlayer.Count == 1 && mockPlayer[0].Venue == "MOCK",
            "#106: pinned MOCK in a PLAYER build must surface the MOCK connect (not an empty dead-end menu), got " + mockPlayer.Count);
    }

    static void ReloginHintOnNotice()
    {
        var (m, conn, _) = Make();
        conn.ApplyStatePoll("{\"venue_state\":\"CONNECTED\",\"venue_id\":\"TACHIBANA\"}");
        conn.ApplyLogoutNotice(new LiveVenueLogoutEvent { Venue = "TACHIBANA" });
        // notice does not change the badge (still connected until poll converges)
        Check(m.BadgeText == "Connected: TACHIBANA", "notice wrongly changed badge");
        Check(!m.ShowReloginHint, "hint should wait until poll shows disconnected");
        conn.ApplyStatePoll("{\"venue_state\":\"DISCONNECTED\",\"venue_id\":null}");
        Check(m.ShowReloginHint, "re-login hint should show after poll converges to disconnected");
    }
}
