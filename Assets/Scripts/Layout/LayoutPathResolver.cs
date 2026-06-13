// LayoutPathResolver.cs — issue #12 "Replay layout" (DURABLE tier)
//
// The thin Unity boundary that resolves the PRODUCTION sidecar path. Kept OUT of
// LayoutStore on purpose (findings §7): LayoutStore takes an EXPLICIT path so the
// AFK probe can hand it a deterministic temp path; only this resolver touches
// Application.persistentDataPath. A single GLOBAL sidecar for now — per-workspace
// is an additive extension when a workspace concept lands.

using System.IO;
using UnityEngine;

public static class LayoutPathResolver
{
    public const string FileName = "layout.json";

    // Application.persistentDataPath/layout.json (findings §8). Production callers
    // resolve here, then pass the result to LayoutStore.Save/Load.
    public static string DefaultPath()
    {
        return Path.Combine(Application.persistentDataPath, FileName);
    }
}
