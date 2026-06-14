// DepthLadderHitlMenu.cs — issue #26 "S9a orderbook 先行" (THROWAWAY, Editor menu)
//
// Spawns the owner-launched Depth Ladder playmode harness EXPLICITLY (no auto-bootstrap), so it
// never collides with the single Play owner (mirrors LiveAdapterTracerHitlMenu). Menu item:
//
//   Tools > Backcast > Depth Ladder HITL
//
// Requires Play mode (the harness renders via OnGUI and runs pythonnet workers under a running
// player loop). Spawns one harness and reuses it if already present.
using UnityEditor;
using UnityEngine;

public static class DepthLadderHitlMenu
{
    const string MENU = "Tools/Backcast/Depth Ladder HITL";

    [MenuItem(MENU)]
    static void Spawn()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Depth Ladder HITL",
                "Enter Play mode first — the harness renders via OnGUI and drives the live loop under a running player loop.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<DepthLadderHitlHarness>();
        if (existing != null)
        {
            Debug.Log("[DEPTH LADDER HITL] already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        var go = new GameObject("DepthLadderHitlHarness");
        go.AddComponent<DepthLadderHitlHarness>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[DEPTH LADDER HITL] spawned.");
    }
}
