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
