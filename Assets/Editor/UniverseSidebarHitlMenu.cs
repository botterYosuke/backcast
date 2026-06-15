// UniverseSidebarHitlMenu.cs — issue #31 "instrument picker / universe sidebar" (Editor menu)
//
// Spawns the owner-launched Universe Sidebar playmode harness EXPLICITLY (no auto-bootstrap),
// so it never collides with the single Play owner (mirrors DepthLadderHitlMenu). Menu item:
//
//   Tools > Backcast > Universe Sidebar HITL
//
// Requires Play mode (the harness renders the sidebar/picker via OnGUI). Spawns one harness
// and reuses it if already present. Python-FREE (mock supply) — no live loop.
using UnityEditor;
using UnityEngine;

public static class UniverseSidebarHitlMenu
{
    const string MENU = "Tools/Backcast/Universe Sidebar HITL";

    [MenuItem(MENU)]
    static void Spawn()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Universe Sidebar HITL",
                "Enter Play mode first — the harness renders the sidebar/picker via OnGUI.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<UniverseSidebarHitlHarness>();
        if (existing != null)
        {
            Debug.Log("[UNIVERSE SIDEBAR HITL] already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        var go = new GameObject("UniverseSidebarHitlHarness");
        go.AddComponent<UniverseSidebarHitlHarness>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[UNIVERSE SIDEBAR HITL] spawned.");
    }
}
