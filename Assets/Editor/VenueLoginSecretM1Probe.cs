// VenueLoginSecretM1Probe.cs — issue #21 M1 focused gate (throwaway, pure C#)
// docs/findings/0012-venue-login-secret-flow.md (D5/D6). NO pythonnet, NO venue:
// exercises only the durable decode→view-model→connection-state seam with hardcoded
// externally-tagged wire strings. The authoritative AFK gate (mock venue, 3 lanes,
// secret roundtrip) is M4's VenueLoginSecretProbe — this only locks the M1 seam.
//
//   <Unity> -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
//       -executeMethod VenueLoginSecretM1Probe.Run
//
// Exit 0 => PASS ([VENUE LOGIN SECRET M1 PASS]), 1 => FAIL (self-failing gate).
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class VenueLoginSecretM1Probe
{
    static readonly List<string> _fail = new List<string>();

    static void Check(bool cond, string msg)
    {
        if (!cond) _fail.Add(msg);
    }

    public static void Run()
    {
        try
        {
            SecretRequiredDecodesToViewModel();
            LogoutNoticeIsRecordedButNotBadgeAuthority();
            ConnectionBadgeDerivesFromPoll();
            LoginAckIsObservable();
        }
        catch (Exception e)
        {
            _fail.Add("exception: " + e);
        }

        if (_fail.Count == 0)
        {
            Debug.Log("[VENUE LOGIN SECRET M1 PASS] decoder + view-model + connection-state seam");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[VENUE LOGIN SECRET M1 FAIL]\n  - " + string.Join("\n  - ", _fail));
            EditorApplication.Exit(1);
        }
    }

    // D5: SecretRequired is no longer an unknown tag — it decodes to the request
    // envelope (request_id/venue/kind/purpose) and bumps the edge counter.
    static void SecretRequiredDecodesToViewModel()
    {
        var vm = new LivePanelViewModel();
        const string wire =
            "{\"SecretRequired\":{\"request_id\":\"req-42\",\"venue\":\"TACHIBANA\"," +
            "\"kind\":\"second_secret\",\"purpose\":\"PLACE\"}}";
        vm.Apply(wire);

        Check(vm.HasSecretRequired, "SecretRequired not observed");
        Check(vm.SecretRequiredCount == 1, "SecretRequiredCount != 1");
        Check(vm.UnknownTagCount == 0, "SecretRequired fell through to unknown tag");
        var s = vm.LatestSecretRequired;
        Check(s.RequestId == "req-42", "request_id mismatch: " + s.RequestId);
        Check(s.Venue == "TACHIBANA", "venue mismatch: " + s.Venue);
        Check(s.Kind == "second_secret", "kind mismatch: " + s.Kind);
        Check(s.Purpose == "PLACE", "purpose mismatch: " + s.Purpose);
    }

    // D6: VenueLogoutDetected is a NOTICE — recorded on the panel VM, but it must NOT
    // flip the connection badge (which stays on the poll until convergence).
    static void LogoutNoticeIsRecordedButNotBadgeAuthority()
    {
        var vm = new LivePanelViewModel();
        vm.Apply("{\"VenueLogoutDetected\":{\"venue\":\"TACHIBANA\"}}");
        Check(vm.HasVenueLogoutNotice, "VenueLogoutDetected not observed");
        Check(vm.VenueLogoutNoticeCount == 1, "VenueLogoutNoticeCount != 1");
        Check(vm.UnknownTagCount == 0, "VenueLogoutDetected fell through to unknown tag");
        Check(vm.LatestVenueLogoutNotice.Venue == "TACHIBANA", "logout notice venue mismatch");

        // Badge stays connected after a logout NOTICE — only a poll can change it.
        var conn = new VenueConnectionViewModel();
        conn.ApplyStatePoll("{\"venue_state\":\"CONNECTED\",\"venue_id\":\"TACHIBANA\"}");
        Check(conn.IsConnected, "precondition: should be connected after CONNECTED poll");
        conn.ApplyLogoutNotice(vm.LatestVenueLogoutNotice);
        Check(conn.IsConnected, "logout NOTICE wrongly flipped badge to disconnected (D6 violation)");
        Check(conn.VenueId == "TACHIBANA", "logout NOTICE wrongly cleared venue_id");
        Check(conn.LogoutNoticeCount == 1, "LogoutNoticeCount != 1");

        // The next poll IS authoritative and converges the badge to disconnected.
        conn.ApplyStatePoll("{\"venue_state\":\"DISCONNECTED\",\"venue_id\":null}");
        Check(!conn.IsConnected, "poll to DISCONNECTED did not converge badge");
        Check(conn.VenueId == null, "venue_id not cleared on DISCONNECTED poll");
    }

    // D6: the badge derives from get_state_json poll. venue_id is present only in the
    // connected band; AUTHENTICATING/ERROR/DISCONNECTED carry no venue badge.
    static void ConnectionBadgeDerivesFromPoll()
    {
        var conn = new VenueConnectionViewModel();
        Check(!conn.IsConnected, "fresh VM should be disconnected");
        Check(conn.VenueId == null, "fresh VM venue_id should be null");

        conn.ApplyStatePoll("{\"venue_state\":\"AUTHENTICATING\",\"venue_id\":null,\"last_prices\":{}}");
        Check(conn.IsAuthenticating, "AUTHENTICATING not reflected");
        Check(!conn.IsConnected, "AUTHENTICATING must not be connected");
        Check(conn.VenueId == null, "AUTHENTICATING must carry no venue_id");

        conn.ApplyStatePoll("{\"venue_state\":\"SUBSCRIBED\",\"venue_id\":\"KABU\",\"last_prices\":{}}");
        Check(conn.IsConnected, "SUBSCRIBED must be connected");
        Check(conn.VenueId == "KABU", "SUBSCRIBED venue_id mismatch: " + conn.VenueId);

        conn.ApplyStatePoll("{\"venue_state\":\"ERROR\",\"venue_id\":null}");
        Check(conn.IsError, "ERROR not reflected");
        Check(!conn.IsConnected, "ERROR must not be connected");
        Check(conn.VenueId == null, "ERROR must carry no venue_id");

        // tolerate the full nested state object (only top-level scalars bind).
        conn.ApplyStatePoll("{\"venue_state\":\"RECONNECTING\",\"venue_id\":\"TACHIBANA\"," +
                            "\"per_instrument\":{\"8918.TSE\":{\"depth\":{\"bids\":[]}}}}");
        Check(conn.IsConnected, "RECONNECTING must be connected");
        Check(conn.VenueId == "TACHIBANA", "RECONNECTING venue_id mismatch");
        Check(conn.PollCount == 4, "PollCount mismatch: " + conn.PollCount);
    }

    static void LoginAckIsObservable()
    {
        var conn = new VenueConnectionViewModel();
        conn.ApplyLoginAck(false, "AUTH_FAILED");
        Check(conn.HasLoginAck, "login ack not observed");
        Check(!conn.LastLoginAckOk, "failed ack reported ok");
        Check(conn.LastLoginAckError == "AUTH_FAILED", "ack error mismatch");

        conn.ApplyLoginAck(true, "");
        Check(conn.LastLoginAckOk, "ok ack reported failed");
        Check(conn.LastLoginAckError == null, "ok ack should clear error");
    }
}
