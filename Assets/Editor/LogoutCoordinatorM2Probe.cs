// LogoutCoordinatorM2Probe.cs — issue #21 M2 focused gate (throwaway, pure C#)
// docs/findings/0012-venue-login-secret-flow.md (D7). Locks the two-layer logout
// defense decision core deterministically (no threads/pythonnet). The threaded
// teardown sequence over a mock venue is M4's authoritative AFK gate.
//
//   <Unity> -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
//       -executeMethod LogoutCoordinatorM2Probe.Run
//
// Exit 0 => PASS ([LOGOUT COORDINATOR M2 PASS]), 1 => FAIL (self-failing gate).
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public static class LogoutCoordinatorM2Probe
{
    static readonly List<string> _fail = new List<string>();
    static void Check(bool cond, string msg) { if (!cond) _fail.Add(msg); }

    public static void Run()
    {
        try
        {
            Wall1_UiDisable();
            Wall2_DeferUntilWriteDrains();
            ImmediateWhenIdle();
            SecretModalBlocksLogout();
        }
        catch (Exception e) { _fail.Add("exception: " + e); }

        if (_fail.Count == 0)
        {
            Debug.Log("[LOGOUT COORDINATOR M2 PASS] two-layer logout defense (UI disable + defer)");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[LOGOUT COORDINATOR M2 FAIL]\n  - " + string.Join("\n  - ", _fail));
            EditorApplication.Exit(1);
        }
    }

    // Wall 1: disconnect disabled while a write is in flight.
    static void Wall1_UiDisable()
    {
        var c = new LiveLogoutCoordinator();
        Check(c.CanUserLogout, "idle should allow logout");
        c.BeginWrite();
        Check(!c.CanUserLogout, "write in flight must disable logout (Wall 1)");
        c.EndWrite();
        Check(c.CanUserLogout, "logout should re-enable after write drains");
    }

    // Wall 2: a logout requested mid-write DEFERS, then becomes ready when the write
    // lane drains — teardown never runs concurrently with the write.
    static void Wall2_DeferUntilWriteDrains()
    {
        var c = new LiveLogoutCoordinator();
        c.BeginWrite();                       // place in flight (e.g. blocked on secret)
        bool immediate = c.RequestLogout();   // Wall 1 bypassed somehow
        Check(!immediate, "RequestLogout during write must defer, not run immediately");
        Check(c.LogoutPending, "deferred logout should be pending");
        Check(!c.ConsumePendingLogout(), "teardown must NOT be ready while write in flight");

        c.EndWrite();                         // write completes / PLACE_TIMEOUT
        Check(!c.LogoutPending, "pending should clear once drained");
        Check(c.ConsumePendingLogout(), "teardown must become ready after write drains");
        Check(!c.ConsumePendingLogout(), "ConsumePendingLogout must fire only once");
    }

    static void ImmediateWhenIdle()
    {
        var c = new LiveLogoutCoordinator();
        bool immediate = c.RequestLogout();
        Check(immediate, "idle RequestLogout should run immediately");
        Check(c.ConsumePendingLogout(), "idle logout should be ready to tear down");
    }

    // Secret modal open (place blocked on secret) also disables logout (Wall 1).
    static void SecretModalBlocksLogout()
    {
        var c = new LiveLogoutCoordinator();
        c.SetSecretModalOpen(true);
        Check(!c.CanUserLogout, "open secret modal must disable logout");
        c.SetSecretModalOpen(false);
        Check(c.CanUserLogout, "closing secret modal should re-enable logout");
    }
}
