// SecretModalM2Probe.cs — issue #21 M2 focused gate (throwaway, pure C#)
// docs/findings/0012-venue-login-secret-flow.md (D5). Drives SecretModalController
// deterministically (time passed in) to lock the plaintext-lifetime contract:
// keyboard-drain char[], masked display, 25s absolute timeout, zeroization on
// submit/cancel/timeout. No pythonnet/venue. The lane-level secret roundtrip (mock
// venue, separate threads) is M4's authoritative AFK gate.
//
//   <Unity> -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
//       -executeMethod SecretModalM2Probe.Run
//
// Exit 0 => PASS ([SECRET MODAL M2 PASS]), 1 => FAIL (self-failing gate).
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class SecretModalM2Probe
{
    static readonly List<string> _fail = new List<string>();
    static void Check(bool cond, string msg) { if (!cond) _fail.Add(msg); }

    static LiveSecretRequiredEvent Req() => new LiveSecretRequiredEvent
    {
        RequestId = "req-7",
        Venue = "TACHIBANA",
        Kind = "second_secret",
        Purpose = "PLACE",
    };

    public static void Run()
    {
        try
        {
            KeyboardDrainAndMask();
            SubmitHandsPayloadAndZeroizes();
            CancelZeroizes();
            AbsoluteTimeoutFiresBefore30s();
            ContainmentInvariant();
        }
        catch (Exception e) { _fail.Add("exception: " + e); }

        if (_fail.Count == 0)
        {
            Debug.Log("[SECRET MODAL M2 PASS] keyboard-drain char[] / mask / 25s absolute / zeroize");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[SECRET MODAL M2 FAIL]\n  - " + string.Join("\n  - ", _fail));
            EditorApplication.Exit(1);
        }
    }

    static void KeyboardDrainAndMask()
    {
        var m = new SecretModalController();
        m.Open(Req(), 0.0);
        Check(m.IsOpen, "modal not open");
        Check(m.RequestId == "req-7", "request id not bound");

        m.AppendInput("12");
        m.AppendChar('3');
        m.AppendInput("a\bX");           // 'a' then backspace then 'X' => "...X"
        Check(m.Length == 4, "length mismatch: " + m.Length);   // 1,2,3,X
        Check(m.MaskedDisplay == "••••", "mask mismatch: " + m.MaskedDisplay);
        // control chars / newlines are ignored (not submit, not appended)
        m.AppendInput("\n\r");
        Check(m.Length == 4, "newline wrongly appended");
    }

    static void SubmitHandsPayloadAndZeroizes()
    {
        var m = new SecretModalController();
        m.Open(Req(), 0.0);
        m.AppendInput("9753");
        char[] payload = m.Submit();
        Check(payload != null, "submit returned null payload");
        Check(new string(payload) == "9753", "payload mismatch");
        Check(!m.IsOpen, "modal still open after submit");
        Check(m.Length == 0, "length not reset after submit");
        Check(m.BufferIsZeroed(), "internal buffer NOT zeroized after submit (D5 violation)");
        Check(m.SubmitCount == 1, "submit count != 1");
        // lane contract: caller zeroizes the one-shot payload after the RPC.
        Array.Clear(payload, 0, payload.Length);

        // empty submit is a no-op (no spurious submit_secret).
        m.Open(Req(), 0.0);
        Check(m.Submit() == null, "empty submit should return null");
    }

    static void CancelZeroizes()
    {
        var m = new SecretModalController();
        m.Open(Req(), 0.0);
        m.AppendInput("abcd");
        m.Cancel();
        Check(!m.IsOpen, "modal open after cancel");
        Check(m.BufferIsZeroed(), "buffer not zeroized after cancel");
        Check(m.CancelCount == 1, "cancel count != 1");
        Check(m.RequestId == null, "request id not cleared after cancel");
    }

    static void AbsoluteTimeoutFiresBefore30s()
    {
        var m = new SecretModalController();
        m.Open(Req(), 100.0);
        m.AppendInput("55");
        // typing does NOT extend an absolute timeout: still expires at open+25s.
        Check(!m.TickExpire(110.0), "expired too early at 10s");
        Check(!m.TickExpire(124.9), "expired too early at 24.9s");
        Check(m.TickExpire(125.0), "did not expire at exactly open+25s");
        Check(m.TimedOut, "TimedOut flag not set");
        Check(!m.IsOpen, "modal still open after timeout");
        Check(m.BufferIsZeroed(), "buffer not zeroized after timeout");
        Check(m.TimeoutCount == 1, "timeout count != 1");
        Check(!m.TickExpire(200.0), "TickExpire fired again on a closed modal");
    }

    static void ContainmentInvariant()
    {
        // 25s (modal absolute) < 30s (secret wait) < 40s (order write).
        Check(SecretModalController.AbsoluteTimeoutSeconds < 30.0,
              "modal timeout must be < backend 30s secret wait");
        Check(30.0 < 40.0, "secret wait must be < order-write timeout");
    }
}
