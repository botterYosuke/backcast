// LiveDemoRoundtripMenu.cs — issue #23 "Live demo roundtrip" (owner HITL launcher).
// docs/findings/0014-live-demo-roundtrip.md RH5.
//
// Spawns the root-based HITL harness (LiveDemoRoundtripHarness) pointed at a real demo venue, so the
// owner can run the two acceptance roundtrips end-to-end against the MAINLINE BackcastWorkspaceRoot
// (the #23 re-home target). The harness adds only a connect affordance; the live surfaces (Order
// ticket window, Orders/Positions/Run-Result tiles, secret modal) are the root's own re-homed uGUI.
//
//   Manual roundtrip: Connect → (secret modal) → Order ticket MARKET BUY → FILLED → Positions tile →
//     Order ticket LIMIT (rests) → Cancel last → PENDING_CANCEL 受付, then poll-confirmed CANCELED.
//   Auto roundtrip: footer Auto segment → ▶ register+start → order/fill/position in the tiles →
//     leave a resting LIMIT → stop-then-switch → graceful-stop cancels the resting order.
//
// Requires Play mode with BackcastWorkspace as the active scene (the root is the single Python owner
// and renders the live surfaces). kabu requires Windows + a running kabuステーション本体. FILL
// observation requires JST market hours. The AFK seam gate is WorkspaceLiveSeamProbe (headless MOCK).
using UnityEditor;
using UnityEngine;

public static class LiveDemoRoundtripMenu
{
    // This HITL harness connects the CONFIGURED venue via ConnectConfigured (root._venue, resolved from
    // LIVE_VENUE at Awake) — so a real-venue roundtrip still wants LIVE_VENUE=TACHIBANA|KABU set BEFORE
    // Play. ADR-0021: the mainline Venue MENU can now connect any venue at runtime (rebind on login)
    // WITHOUT re-Play; this harness predates that and stays venue-configured for the scripted roundtrip.
    [MenuItem("Tools/Backcast/Live Demo Roundtrip (Tachibana demo)")]
    static void SpawnTachibana() => Spawn("TACHIBANA", "8918.TSE");

    [MenuItem("Tools/Backcast/Live Demo Roundtrip (Kabu verify)")]
    static void SpawnKabu() => Spawn("KABU", "7203.TSE");

    [MenuItem("Tools/Backcast/Live Demo Roundtrip (MOCK)")]
    static void SpawnMock() => Spawn("MOCK", "8918.TSE");

    static void Spawn(string expectedVenue, string instrumentId)
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Live Demo Roundtrip",
                "1) Set LIVE_VENUE=" + expectedVenue + " in .env (default MOCK) — this harness connects the " +
                "configured venue via ConnectConfigured, so choose it before Play.\n" +
                "2) Enter Play mode (BackcastWorkspace), then run this menu again.\n\n" +
                "The root owns Python and renders the live surfaces; this harness only drives venue connect.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<LiveDemoRoundtripHarness>();
        if (existing != null)
        {
            Debug.Log("[LIVE DEMO ROUNDTRIP] harness already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        var root = Object.FindAnyObjectByType<BackcastWorkspaceRoot>();
        if (root != null && root.ConfiguredVenue != expectedVenue)
            Debug.LogWarning("[LIVE DEMO ROUNDTRIP] this item expects LIVE_VENUE=" + expectedVenue +
                             " but the running server is configured for " + root.ConfiguredVenue +
                             ". This harness connects the configured venue (" + root.ConfiguredVenue +
                             "), not " + expectedVenue + " — set LIVE_VENUE=" + expectedVenue +
                             " and re-Play (or use the mainline Venue menu, which rebinds the venue on login).");

        LiveDemoRoundtripHarness.DefaultInstrumentId = instrumentId;
        var go = new GameObject("LiveDemoRoundtripHarness");
        go.AddComponent<LiveDemoRoundtripHarness>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[LIVE DEMO ROUNDTRIP] spawned harness — configured venue=" +
                  (root != null ? root.ConfiguredVenue : "?") + " instrument preset=" + instrumentId + ".");
    }

    [MenuItem("Tools/Backcast/Record [LIVE DEMO ROUNDTRIP PASS]")]
    static void RecordPass()
    {
        // Owner clicks this ONLY after both roundtrips (manual + auto) and the graceful-stop
        // resting-order cancel have been visually confirmed against the real demo venue (the AFK
        // seam gate is WorkspaceLiveSeamProbe; this is the real-venue HITL leg).
        Debug.Log("[LIVE DEMO ROUNDTRIP PASS] manual (place→fill→position, LIMIT→cancel→poll-confirmed CANCELED) " +
                  "+ auto (run→order/fill/position, graceful-stop cancels resting→poll terminal→broker open 0) " +
                  "confirmed on the demo venue via the mainline workspace root. Record OS/Unity/Python/venue/" +
                  "timestamp + screenshots in docs/findings/0014 §実証結果, then close #4.");
    }
}
