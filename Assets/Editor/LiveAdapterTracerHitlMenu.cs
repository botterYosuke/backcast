// LiveAdapterTracerHitlMenu.cs — issue #20 "Live adapter tracer" (THROWAWAY, Editor menu)
//
// Spawns the owner-launched Live Adapter Tracer playmode harness EXPLICITLY (no auto-bootstrap),
// so it never collides with the single Play owner (mirrors StrategyEditorHitlMenu). Menu item:
//
//   Tools > Backcast > Live Adapter Tracer HITL
//
// Requires Play mode (the harness renders via OnGUI and runs pythonnet workers under a running
// player loop). Spawns one harness and reuses it if already present.
using UnityEditor;
using UnityEngine;

public static class LiveAdapterTracerHitlMenu
{
    const string MENU = "Tools/Backcast/Live Adapter Tracer HITL";

    [MenuItem(MENU)]
    static void Spawn()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Live Adapter Tracer HITL",
                "Enter Play mode first — the harness renders via OnGUI and drives the live loop under a running player loop.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<LiveAdapterTracerHitlHarness>();
        if (existing != null)
        {
            Debug.Log("[LIVE ADAPTER TRACER HITL] already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        var go = new GameObject("LiveAdapterTracerHitlHarness");
        go.AddComponent<LiveAdapterTracerHitlHarness>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[LIVE ADAPTER TRACER HITL] spawned.");
    }
}
