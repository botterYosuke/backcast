// InfiniteCanvasHitlMenu.cs — issue #13 "infinite canvas" (THROWAWAY, Editor menu)
//
// Spawns the owner-launched HITL harness EXPLICITLY (no auto-bootstrap), so it never
// collides with the single-Play-owner (#11, findings 0003 §8). Menu item:
//
//   Tools > Backcast > Infinite Canvas HITL
//
// Requires Play mode (pointer drag/scroll need a running EventSystem); spawns one harness
// and reuses it if already present.

using UnityEditor;
using UnityEngine;

public static class InfiniteCanvasHitlMenu
{
    const string MENU = "Tools/Backcast/Infinite Canvas HITL";

    [MenuItem(MENU)]
    static void Spawn()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Infinite Canvas HITL",
                "Enter Play mode first — the harness needs a running EventSystem for drag/scroll input.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<InfiniteCanvasHitlHarness>();
        if (existing != null)
        {
            Debug.Log("[INFINITE CANVAS HITL] already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        var go = new GameObject("InfiniteCanvasHitlHarness");
        go.AddComponent<InfiniteCanvasHitlHarness>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[INFINITE CANVAS HITL] spawned.");
    }
}
