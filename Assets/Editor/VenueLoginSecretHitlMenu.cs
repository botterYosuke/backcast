// VenueLoginSecretHitlMenu.cs — issue #21 "Venue login and secret flow" (THROWAWAY, Editor menu)
//
// Spawns the owner-launched real-venue HITL harness EXPLICITLY (no auto-bootstrap), so it never
// collides with the single Play owner (mirrors LiveAdapterTracerHitlMenu). Menu items:
//
//   Tools > Backcast > Venue Login Secret HITL (Tachibana demo)
//   Tools > Backcast > Venue Login Secret HITL (Kabu verify)   [Windows + kabuステーション本体]
//
// Requires Play mode. Records go in docs/findings/0012 §実証結果. kabu is platform-inapplicable
// on macOS (the kabuステーション本体 is Windows-only).
using UnityEditor;
using UnityEngine;

public static class VenueLoginSecretHitlMenu
{
    [MenuItem("Tools/Backcast/Venue Login Secret HITL (Tachibana demo)")]
    static void SpawnTachibana() => Spawn("TACHIBANA");

    [MenuItem("Tools/Backcast/Venue Login Secret HITL (Kabu verify)")]
    static void SpawnKabu() => Spawn("KABU");

    static void Spawn(string venue)
    {
        if (!EditorApplication.isPlaying)
        {
            EditorUtility.DisplayDialog(
                "Venue Login Secret HITL",
                "Enter Play mode first — the harness renders via OnGUI and drives the live loop under a running player loop.",
                "OK");
            return;
        }

        var existing = Object.FindAnyObjectByType<VenueLoginSecretHitlHarness>();
        if (existing != null)
        {
            Debug.Log("[VENUE LOGIN SECRET HITL] already running.");
            Selection.activeGameObject = existing.gameObject;
            return;
        }

        VenueLoginSecretHitlHarness.TargetVenue = venue;
        var go = new GameObject("VenueLoginSecretHitlHarness");
        go.AddComponent<VenueLoginSecretHitlHarness>();
        Object.DontDestroyOnLoad(go);
        Selection.activeGameObject = go;
        Debug.Log("[VENUE LOGIN SECRET HITL] spawned for " + venue + ".");
    }
}
