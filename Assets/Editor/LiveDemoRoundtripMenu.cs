// LiveDemoRoundtripMenu.cs — issue #23 "Live demo roundtrip" (workstream C, owner HITL launcher).
// docs/findings/0014-live-demo-roundtrip.md D3/D6.
//
// Spawns the PRODUCTION composition (ProductionLiveShell, workstream B) pointed at a real demo
// venue so the owner can run the two acceptance roundtrips end-to-end on a real Unity frame:
//
//   Manual roundtrip: Connect → Order ticket: MARKET BUY → FILLED → Positions reflects the
//     build → Order ticket: LIMIT (far from market, rests) → Cancel last → status shows the
//     取消受付 PENDING_CANCEL, then the GET /orders poll confirms terminal CANCELED
//     (cancel-ACK end-to-end, findings 0014 (a)/(c-1)).
//   Auto roundtrip: switch to Auto → strategy .py → ▶ Register & Start → order/fill/position
//     in the panels → leave a resting LIMIT → ■ Stop → graceful-stop cancels the resting order
//     (receipt → poll-confirmed terminal → broker open 0, findings 0014 (a)/(b) + #22 shutdown).
//
// Requires Play mode (the shell renders via OnGUI and drives the live loop under a running
// player loop). kabu requires Windows + a running kabuステーション本体 (platform-inapplicable on
// macOS). FILL observation requires JST market hours (findings 0012 hit a weekend = no fill).
//
// After BOTH roundtrips pass, click "Record [LIVE DEMO ROUNDTRIP PASS]" and paste the logged
// line + screenshots into docs/findings/0014 §実証結果, then close #4.
using UnityEditor;
using UnityEngine;

public static class LiveDemoRoundtripMenu
{
    [MenuItem("Tools/Backcast/Live Demo Roundtrip (Tachibana demo)")]
    static void SpawnTachibana() => Spawn("TACHIBANA", "8918.TSE");

    [MenuItem("Tools/Backcast/Live Demo Roundtrip (Kabu verify)")]
    static void SpawnKabu() => Spawn("KABU", "7203.TSE");

    // #39 Slice 3 footer HITL: MOCK is the deterministic, closed-market-OK regression leg. The
    // footer (mode segments + LiveAuto ▶) drives the flow now — Connect → "Auto" segment → footer ▶
    // (register→start) → panel reflects the run → press "Replay"/"Manual" to stop-then-switch (the
    // run is torn down FIRST, no orphan). findings 0026 §6.
    [MenuItem("Tools/Backcast/Footer LiveAuto HITL (MOCK)")]
    static void SpawnMock() => Spawn("MOCK", "8918.TSE");

    static void Spawn(string venue, string instrumentId)
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Live Demo Roundtrip",
                "Enter Play mode first — the shell renders via OnGUI and drives the live loop under a running player loop.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<ProductionLiveShell>();
        if (existing != null)
        {
            Debug.Log("[LIVE DEMO ROUNDTRIP] production shell already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        ProductionLiveShell.TargetVenue = venue;
        ProductionLiveShell.DefaultInstrumentId = instrumentId;
        var go = new GameObject("ProductionLiveShell");
        go.AddComponent<ProductionLiveShell>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[LIVE DEMO ROUNDTRIP] spawned production shell for " + venue + " (" + instrumentId + ").");
    }

    [MenuItem("Tools/Backcast/Record [LIVE DEMO ROUNDTRIP PASS]")]
    static void RecordPass()
    {
        // Owner clicks this ONLY after both roundtrips (manual + auto) and the graceful-stop
        // resting-order cancel have been visually confirmed against the real demo venue.
        Debug.Log("[LIVE DEMO ROUNDTRIP PASS] manual (place→fill→position, LIMIT→cancel→poll-confirmed CANCELED) " +
                  "+ auto (run→order/fill/position, graceful-stop cancels resting→poll terminal→broker open 0) " +
                  "confirmed on the demo venue. Record OS/Unity/Python/venue/timestamp + screenshots in " +
                  "docs/findings/0014 §実証結果, then close #4.");
    }
}
