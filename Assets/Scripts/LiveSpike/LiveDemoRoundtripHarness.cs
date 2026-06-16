// LiveDemoRoundtripHarness.cs — issue #23 re-home slice (root-based owner HITL harness, throwaway).
// docs/findings/0014-live-demo-roundtrip.md RH5.
//
// Replaces the retired ProductionLiveShell as the demo-roundtrip harness. It does NOT compose any UI
// of its own beyond a small CONNECT AFFORDANCE: the mainline BackcastWorkspaceRoot already renders
// the re-homed live surfaces (Orders / Positions / Run Result tiles, the Order ticket floating
// window, the secret modal). This harness only drives venue connect for the demo — the mainline
// Venue submenu UI is #42 (findings 0014 RH5). The connect buttons REUSE the durable
// VenueMenuViewModel.ConnectVariants and call BackcastWorkspaceRoot.ConnectVenue (which reuses
// host.VenueLogin); no ProductionLiveShell Connect logic is reimplemented.
//
// Owner roundtrip (real Unity frame, JST market hours for fills):
//   Manual: Connect → (secret modal) → Order ticket MARKET BUY → FILLED → Positions tile → LIMIT
//     (rests) → Cancel last → PENDING_CANCEL 受付 → poll-confirmed CANCELED.
//   Auto: footer Auto segment → ▶ register+start → order/fill/position in the tiles → leave a resting
//     LIMIT → stop-then-switch → graceful-stop cancels the resting order.
// Normal exit (Play stop) tears the host down (cancel resting / venue logout / close).
using UnityEngine;

public sealed class LiveDemoRoundtripHarness : MonoBehaviour
{
    public static string TargetVenue = "MOCK";
    public static string DefaultInstrumentId = "8918.TSE";

    BackcastWorkspaceRoot _root;

    void Start()
    {
        _root = FindAnyObjectByType<BackcastWorkspaceRoot>();
        if (_root == null)
            Debug.LogWarning("[LIVE DEMO ROUNDTRIP] BackcastWorkspaceRoot not found — is BackcastWorkspace the active scene?");
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(12, Screen.height - 200, 360, 180), GUI.skin.box);
        GUILayout.Label("<b>Live Demo Roundtrip</b> (target: " + TargetVenue + " / " + DefaultInstrumentId + ")");

        if (_root == null)
        {
            GUILayout.Label("<color=orange>root not found — enter Play in BackcastWorkspace</color>");
            GUILayout.EndArea();
            return;
        }

        GUILayout.Label("owner=" + _root.IsPythonOwner + "  serverReady=" + _root.ServerReady);
        GUILayout.Label(_root.VenueConnected ? ("<color=lime>Connected: " + _root.VenueId + "</color>")
                                             : "not connected");

        bool canConnect = _root.IsPythonOwner && _root.ServerReady && !_root.VenueConnected;
        GUI.enabled = canConnect;
        if (GUILayout.Button("Connect MOCK (dev)")) _root.ConnectVenue("MOCK", "");
        foreach (var v in VenueMenuViewModel.ConnectVariants)
            if (GUILayout.Button("Connect " + v.Label)) _root.ConnectVenue(v.Venue, v.Env);
        GUI.enabled = true;

        GUILayout.Label("Trade via the workspace: Order ticket window (LiveManual) / footer Auto ▶.");
        GUILayout.EndArea();
    }
}
