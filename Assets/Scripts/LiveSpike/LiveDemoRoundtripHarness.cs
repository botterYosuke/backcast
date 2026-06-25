// LiveDemoRoundtripHarness.cs — issue #23 re-home slice (root-based owner HITL harness, throwaway).
// docs/findings/0014-live-demo-roundtrip.md RH5.
//
// Replaces the retired ProductionLiveShell as the demo-roundtrip harness. It composes NO UI of its own
// beyond a small CONNECT AFFORDANCE: the mainline BackcastWorkspaceRoot already renders the re-homed
// live surfaces (Orders / Positions / Run Result tiles, the Order ticket floating window, the secret
// modal). The harness only drives venue connect — the mainline Venue submenu UI is #42 (RH5).
//
// VENUE comes from LIVE_VENUE env (the root builds the one-per-server venue at Awake; connecting any
// other would hit VENUE_MISMATCH), so the harness offers a SINGLE Connect button for the CONFIGURED
// venue, gated by the durable VenueMenuViewModel.CanConnectEnv (ADR-0027: prod no longer env-gated;
// CanConnectEnv has collapsed to CanConnect) — no per-variant buttons that could drive a mismatched
// venue. INSTRUMENT comes from
// LIVE_INSTRUMENT env (else the menu preset), and is pushed into SelectedSymbol so the Order ticket
// and chart actually point at it (not merely displayed).
//
// Owner roundtrip (real Unity frame, JST market hours for fills):
//   Manual: Connect → (secret modal) → Order ticket MARKET BUY → FILLED → Positions tile → LIMIT
//     (rests) → Cancel last → PENDING_CANCEL 受付 → poll-confirmed CANCELED.
//   Auto: footer Auto segment → ▶ register+start → order/fill/position in the tiles → leave a resting
//     LIMIT → stop-then-switch → graceful-stop cancels the resting order.
// Normal exit (Play stop) tears the host down.
using UnityEngine;

public sealed class LiveDemoRoundtripHarness : MonoBehaviour
{
    // Menu preset (overridden by LIVE_INSTRUMENT env). Venue is NOT a harness field — it is LIVE_VENUE
    // env, resolved by the root at Awake (one-venue-per-server).
    public static string DefaultInstrumentId = "8918.TSE";

    BackcastWorkspaceRoot _root;
    string _focusedIid = "";

    void Start()
    {
        _root = FindAnyObjectByType<BackcastWorkspaceRoot>();
        if (_root == null)
        {
            Debug.LogWarning("[LIVE DEMO ROUNDTRIP] BackcastWorkspaceRoot not found — is BackcastWorkspace the active scene?");
            return;
        }
        // Focus the HITL instrument so the manual ticket / chart actually point at it (LIVE_INSTRUMENT env
        // wins, else the menu preset). The root resolves the venue itself from LIVE_VENUE.
        _focusedIid = EnvConfig.Get("LIVE_INSTRUMENT", DefaultInstrumentId);
        _root.FocusInstrument(_focusedIid);
        Debug.Log("[LIVE DEMO ROUNDTRIP] harness ready — venue=" + _root.ConfiguredVenue + " instrument=" + _focusedIid);
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(12, Screen.height - 180, 360, 160), GUI.skin.box);
        GUILayout.Label("<b>Live Demo Roundtrip</b>");

        if (_root == null)
        {
            GUILayout.Label("<color=orange>root not found — enter Play in BackcastWorkspace</color>");
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label("venue (LIVE_VENUE env): <b>" + _root.ConfiguredVenue + "</b>   instrument: " + _focusedIid);
        GUILayout.Label("owner=" + _root.IsPythonOwner + "  serverReady=" + _root.ServerReady);
        GUILayout.Label(_root.VenueConnected ? ("<color=lime>Connected: " + _root.VenueId + "</color>")
                                             : "not connected");

        GUI.enabled = _root.CanConnectConfigured();
        if (GUILayout.Button("Connect " + _root.ConfiguredVenue)) _root.ConnectConfigured();
        GUI.enabled = true;

        GUILayout.Label("Trade via the workspace: Order ticket (LiveManual) / footer Auto ▶.");
        GUILayout.EndArea();
    }
}
