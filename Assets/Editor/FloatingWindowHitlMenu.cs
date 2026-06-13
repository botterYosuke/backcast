// FloatingWindowHitlMenu.cs — issue #15 "floating windows" (THROWAWAY, Editor menu)
//
// Spawns the owner-launched Floating Window HITL harness EXPLICITLY (no auto-bootstrap), so it
// never collides with the single-Play-owner (#11, findings 0003 §8). Menu item:
//
//   Tools > Backcast > Floating Window HITL
//
// Requires Play mode (pointer drag/scroll need a running EventSystem); spawns one harness and
// reuses it if already present (mirrors HakoniwaHitlMenu / InfiniteCanvasHitlMenu).

using UnityEditor;
using UnityEngine;

public static class FloatingWindowHitlMenu
{
    const string MENU = "Tools/Backcast/Floating Window HITL";

    [MenuItem(MENU)]
    static void Spawn()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Floating Window HITL",
                "Enter Play mode first — the harness needs a running EventSystem for drag/scroll input.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<FloatingWindowHitlHarness>();
        if (existing != null)
        {
            Debug.Log("[FLOATING WINDOW HITL] already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        var go = new GameObject("FloatingWindowHitlHarness");
        go.AddComponent<FloatingWindowHitlHarness>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[FLOATING WINDOW HITL] spawned.");
    }
}
