// HakoniwaHitlMenu.cs — issue #14 "Hakoniwa split-grid" (THROWAWAY, Editor menu)
//
// Spawns the owner-launched Hakoniwa HITL harness EXPLICITLY (no auto-bootstrap), so it never
// collides with the single-Play-owner (#11, findings 0003 §8). Menu item:
//
//   Tools > Backcast > Hakoniwa HITL
//
// Requires Play mode (pointer drag/scroll need a running EventSystem); spawns one harness and
// reuses it if already present (mirrors InfiniteCanvasHitlMenu).

using UnityEditor;
using UnityEngine;

public static class HakoniwaHitlMenu
{
    const string MENU = "Tools/Backcast/Hakoniwa HITL";

    [MenuItem(MENU)]
    static void Spawn()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Hakoniwa HITL",
                "Enter Play mode first — the harness needs a running EventSystem for drag/scroll input.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<HakoniwaHitlHarness>();
        if (existing != null)
        {
            Debug.Log("[HAKONIWA HITL] already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        var go = new GameObject("HakoniwaHitlHarness");
        go.AddComponent<HakoniwaHitlHarness>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[HAKONIWA HITL] spawned.");
    }
}
