// UniversePruneProbe.cs — issue #41 "instruments universe prune" (AFK regression gate)
//
// Headless, Python-FREE characterization gate for the #41 prune brain. Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod UniversePruneProbe.Run -logFile <log>
//   # expect: [UNIVERSE PRUNE PASS] ... / exit=0
//
// Pins the #253 stale-snapshot regression: a prune NEVER runs from a fallback/stale source, an
// empty live universe NEVER wipes the registry, and prune ignores the Editable gate. Covers the
// full gate matrix (findings 0041 §4), the PruneRetain contract, the change-gate, and the
// gate→PruneRetain→Changed integration. Production wiring is dormant (NullUniversePruneSource);
// this probe drives the gate with crafted inputs the way a real live/catalog source eventually will.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

public static class UniversePruneProbe
{
    static int _fail;

    static void Check(bool ok, string label)
    {
        if (ok) { Debug.Log("  ok: " + label); return; }
        _fail++;
        Debug.LogError("  FAIL: " + label);
    }

    static UniversePruneInputs Live(UniversePruneSourceKind src, UniverseStatusKind status, string venue, params string[] ids) =>
        new UniversePruneInputs
        {
            Mode = UniverseSourceMode.Live,
            LiveSource = src,
            LiveStatus = status,
            VenueState = venue,
            LiveIds = ids,
            ScenarioEnd = null,
            ReplayStatus = UniverseStatusKind.NotConnected,
            ReplayIds = Array.Empty<string>(),
        };

    static UniversePruneInputs Replay(string end, UniverseStatusKind status, params string[] ids) =>
        new UniversePruneInputs
        {
            Mode = UniverseSourceMode.Replay,
            LiveSource = UniversePruneSourceKind.Unknown,
            LiveStatus = UniverseStatusKind.NotConnected,
            VenueState = "DISCONNECTED",
            LiveIds = Array.Empty<string>(),
            ScenarioEnd = end,
            ReplayStatus = status,
            ReplayIds = ids,
        };

    static InstrumentRegistry Reg(bool editable, params string[] ids)
    {
        var r = new InstrumentRegistry { Editable = true };
        r.ReplaceAll(ids);
        r.Editable = editable;
        return r;
    }

    public static void Run()
    {
        _fail = 0;
        Debug.Log("[universe-prune-probe] start");

        Section1_GateLiveMatrix();
        Section2_GateReplayMatrix();
        Section3_PruneRetainContract();
        Section4_ChangeGate();
        Section5_Integration();

        if (_fail == 0)
        {
            Debug.Log("[UNIVERSE PRUNE PASS] gate matrix + PruneRetain + change-gate + integration verified");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[UNIVERSE PRUNE FAIL] " + _fail + " check(s) failed");
            EditorApplication.Exit(1);
        }
    }

    // ── Section 1: Live triple-gate matrix (the #253 fail-closed core) ──────────────────────────
    static void Section1_GateLiveMatrix()
    {
        Debug.Log("section 1: Live gate matrix");

        // LiveVenue + CONNECTED/SUBSCRIBED + Ready + non-empty → allowlist returned.
        var ok = UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Ready, "SUBSCRIBED", "1301.TSE"));
        Check(ok != null && ok.Count == 1 && ok.Contains("1301.TSE"), "LiveVenue+SUBSCRIBED+Ready+non-empty -> allowlist");
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Ready, "CONNECTED", "1301.TSE")) != null,
            "LiveVenue+CONNECTED -> allowlist");

        // Fallback / stale / unknown sources → null (no prune). #253 core.
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.ReplayCatalogFallback, UniverseStatusKind.Ready, "SUBSCRIBED", "1301.TSE")) == null,
            "ReplayCatalogFallback -> null (no prune)");
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.LocalVenueSnapshot, UniverseStatusKind.Ready, "SUBSCRIBED", "1301.TSE")) == null,
            "LocalVenueSnapshot -> null (#41 本文優先, no prune)");
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.Unknown, UniverseStatusKind.Ready, "SUBSCRIBED", "1301.TSE")) == null,
            "Unknown source -> null");

        // Empty live universe → null (HIGH-1: must not wipe).
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Ready, "SUBSCRIBED")) == null,
            "LiveVenue + empty ids -> null (HIGH-1 no wipe)");

        // Non-Ready statuses → null.
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Loading, "SUBSCRIBED", "1301.TSE")) == null,
            "LiveVenue + Loading -> null");
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Error, "SUBSCRIBED", "1301.TSE")) == null,
            "LiveVenue + Error -> null");
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.NotConnected, "SUBSCRIBED", "1301.TSE")) == null,
            "LiveVenue + NotConnected -> null");

        // Venue not in the strict prune band → null. RECONNECTING is EXCLUDED (badge band would include it).
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Ready, "RECONNECTING", "1301.TSE")) == null,
            "LiveVenue + RECONNECTING -> null (Reconnecting excluded, NOT badge IsConnected)");
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Ready, "DISCONNECTED", "1301.TSE")) == null,
            "LiveVenue + DISCONNECTED -> null");
        Check(UniversePruneGate.CurrentUniverse(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Ready, "AUTHENTICATING", "1301.TSE")) == null,
            "LiveVenue + AUTHENTICATING -> null");

        // The predicate itself.
        Check(UniversePruneGate.IsVenueLiveForPrune("CONNECTED") && UniversePruneGate.IsVenueLiveForPrune("SUBSCRIBED"),
            "IsVenueLiveForPrune accepts CONNECTED/SUBSCRIBED");
        Check(!UniversePruneGate.IsVenueLiveForPrune("RECONNECTING") && !UniversePruneGate.IsVenueLiveForPrune("DISCONNECTED"),
            "IsVenueLiveForPrune rejects RECONNECTING/DISCONNECTED");
    }

    // ── Section 2: Replay gate matrix (no triple-gate — end + Ready + non-empty) ────────────────
    static void Section2_GateReplayMatrix()
    {
        Debug.Log("section 2: Replay gate matrix");

        var ok = UniversePruneGate.CurrentUniverse(Replay("2025-01-10", UniverseStatusKind.Ready, "1301.TSE", "7203.TSE"));
        Check(ok != null && ok.Count == 2, "Replay end set + Ready + non-empty -> allowlist");

        Check(UniversePruneGate.CurrentUniverse(Replay(null, UniverseStatusKind.Ready, "1301.TSE")) == null,
            "Replay end unset -> null (EndUnset)");
        Check(UniversePruneGate.CurrentUniverse(Replay("", UniverseStatusKind.Ready, "1301.TSE")) == null,
            "Replay end empty -> null");
        Check(UniversePruneGate.CurrentUniverse(Replay("2025-01-10", UniverseStatusKind.Loading, "1301.TSE")) == null,
            "Replay Loading -> null");
        Check(UniversePruneGate.CurrentUniverse(Replay("2025-01-10", UniverseStatusKind.Error, "1301.TSE")) == null,
            "Replay Error -> null");
        Check(UniversePruneGate.CurrentUniverse(Replay("2025-01-10", UniverseStatusKind.Empty)) == null,
            "Replay Empty -> null");
        Check(UniversePruneGate.CurrentUniverse(Replay("2025-01-10", UniverseStatusKind.Ready)) == null,
            "Replay Ready but empty ids -> null (no wipe)");
    }

    // ── Section 3: PruneRetain contract (editable bypass + double-defense) ──────────────────────
    static void Section3_PruneRetainContract()
    {
        Debug.Log("section 3: PruneRetain contract");

        // Prunes the universe-outside id.
        var r = Reg(true, "1301.TSE", "CHART_ONLY");
        int changed = 0; Action h = () => changed++; r.Changed += h;
        bool shrank = r.PruneRetain(new HashSet<string> { "1301.TSE" });
        Check(shrank && r.Count == 1 && r.Ids[0] == "1301.TSE", "PruneRetain removes universe-outside id");
        Check(changed == 1, "PruneRetain fires Changed exactly once on a real prune");
        r.Changed -= h;

        // Editable=false still prunes (TTWR prune_runs_even_when_editable_is_false).
        var locked = Reg(false, "1301.TSE", "LOCKED_ONLY");
        Check(locked.PruneRetain(new HashSet<string> { "1301.TSE" }) && locked.Count == 1,
            "PruneRetain runs even when Editable=false (editable gates user edits, not system prune)");

        // Empty allowlist → no-op (callee-side #253 double-defense, never wipe).
        var keep = Reg(true, "1301.TSE", "7203.TSE");
        Check(!keep.PruneRetain(new HashSet<string>()) && keep.Count == 2, "empty allowlist -> no-op (no wipe)");
        Check(!keep.PruneRetain(null) && keep.Count == 2, "null allowlist -> no-op");

        // Empty registry → no-op.
        var empty = Reg(true);
        Check(!empty.PruneRetain(new HashSet<string> { "1301.TSE" }), "empty registry -> no-op");

        // No-op when nothing is outside the allowlist (no spurious Changed).
        var same = Reg(true, "1301.TSE");
        int c2 = 0; Action h2 = () => c2++; same.Changed += h2;
        Check(!same.PruneRetain(new HashSet<string> { "1301.TSE", "9999.TSE" }) && c2 == 0,
            "PruneRetain no-op (nothing to drop) does not fire Changed");
        same.Changed -= h2;
    }

    // ── Section 4: change-gate (inputs_changed parity) ──────────────────────────────────────────
    static void Section4_ChangeGate()
    {
        Debug.Log("section 4: change-gate");

        var r = Reg(true, "1301.TSE", "CHART_ONLY");
        var d = new UniversePruneDriver(r);
        var inputs = Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Ready, "SUBSCRIBED", "1301.TSE");

        Check(d.Tick(inputs) && r.Count == 1, "first tick prunes");
        Check(d.EvaluationCount == 1, "first tick evaluated");
        Check(!d.Tick(inputs), "identical inputs -> no re-evaluation (change-gate)");
        Check(d.EvaluationCount == 1, "no re-eval on unchanged fingerprint");
        Check(d.PruneCount == 1 && !string.IsNullOrEmpty(d.LastPruneLog), "PruneCount/log observable");

        // A membership change in the allowlist re-evaluates.
        var widened = Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Ready, "SUBSCRIBED", "1301.TSE", "7203.TSE");
        Check(!d.Tick(widened), "widened universe: re-evaluates but nothing to drop (no-op prune)");
        Check(d.EvaluationCount == 2, "fingerprint change forced a re-eval");
    }

    // ── Section 5: gate→PruneRetain→Changed integration (chart/depth reflect via Changed) ───────
    static void Section5_Integration()
    {
        Debug.Log("section 5: integration");

        var r = Reg(true, "1301.TSE", "7203.TSE", "CHART_ONLY");
        // Simulate the downstream consumer (SyncChartTilesToUniverse subscribes to Changed in the root).
        var mirror = new List<string>(r.Ids);
        Action sync = () => { mirror = new List<string>(r.Ids); };
        r.Changed += sync;

        var d = new UniversePruneDriver(r);
        d.Tick(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Ready, "SUBSCRIBED", "1301.TSE", "7203.TSE"));

        Check(r.Count == 2 && !r.Ids.Contains("CHART_ONLY"), "integration: CHART_ONLY pruned");
        Check(mirror.Count == 2 && !mirror.Contains("CHART_ONLY"), "integration: downstream mirror followed via Changed");
        r.Changed -= sync;

        // A disconnect mid-session auto-fails the gate (no invalidate-on-disconnect system needed).
        var r2 = Reg(true, "1301.TSE", "EXTRA");
        var d2 = new UniversePruneDriver(r2);
        d2.Tick(Live(UniversePruneSourceKind.LiveVenue, UniverseStatusKind.Ready, "DISCONNECTED", "1301.TSE"));
        Check(r2.Count == 2, "integration: disconnected venue -> no prune (fail-closed, no stale cache)");
    }
}
