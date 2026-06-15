// MenuBarHitlMenu.cs — issue #42 "menu bar（全体メニュー）" (Editor menu, owner-launched)
//
// Spawns the Menu Bar AFK probe / HITL harness EXPLICITLY (no auto-bootstrap) so it never
// collides with the single Play-mode engine owner (mirrors DepthLadderHitlMenu). Menu item:
//
//   Tools > Backcast > Menu Bar HITL (#42)
//
// Requires Play mode: the harness renders via OnGUI and boots the embedded Python engine under
// a running player loop, then runs the AFK assertions (logs [MENU BAR HITL PASS]/[FAIL]).
// Run it in a FRESH Play session with no other engine-owning harness (PythonEngine is single-owner).
using UnityEditor;
using UnityEngine;

public static class MenuBarHitlMenu
{
    const string MENU = "Tools/Backcast/Menu Bar HITL (#42)";

    [MenuItem(MENU)]
    static void Spawn()
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Menu Bar HITL (#42)",
                "Enter Play mode first — the harness renders via OnGUI and boots the live engine under a running player loop.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<MenuBarHitlHarness>();
        if (existing != null)
        {
            Debug.Log("[MENU BAR HITL] already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        var go = new GameObject("MenuBarHitlHarness");
        go.AddComponent<MenuBarHitlHarness>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[MENU BAR HITL] spawned.");
    }
}
