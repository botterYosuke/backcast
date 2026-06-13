// StrategyEditorHitlMenu.cs — issue #16 "Strategy Editor" (THROWAWAY, Editor menu)
//
// Spawns the owner-launched Strategy Editor HITL harness EXPLICITLY (no auto-bootstrap), so it
// never collides with the single-Play-owner (#11, findings 0003 §8). Menu item:
//
//   Tools > Backcast > Strategy Editor HITL
//
// Requires Play mode (text entry / IME / pointer need a running EventSystem); spawns one harness
// and reuses it if already present (mirrors FloatingWindowHitlMenu).

using UnityEditor;
using UnityEngine;

public static class StrategyEditorHitlMenu
{
    const string MENU = "Tools/Backcast/Strategy Editor HITL";

    [MenuItem(MENU)]
    static void Spawn()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Strategy Editor HITL",
                "Enter Play mode first — the harness needs a running EventSystem for text entry / IME / pointer input.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<StrategyEditorHitlHarness>();
        if (existing != null)
        {
            Debug.Log("[STRATEGY EDITOR HITL] already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        var go = new GameObject("StrategyEditorHitlHarness");
        go.AddComponent<StrategyEditorHitlHarness>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[STRATEGY EDITOR HITL] spawned.");
    }
}
