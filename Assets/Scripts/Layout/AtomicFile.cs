// AtomicFile.cs — issue #69 simplify (the single atomic temp+replace writer).
//
// The ONE implementation of "write to a temp file in the same directory, then atomically
// replace" — a crash mid-write never leaves the user's file truncated. Extracted from the
// byte-identical copies in ScenarioSidecarStore (#29) and LayoutSidecarStore (#69) so the
// atomic-swap semantics live in one place (a fix to them is now a single edit, not 2–3).
//
// Encoding: File.WriteAllText's default is UTF-8 WITHOUT BOM — the engine reads these
// <strategy>.json sidecars, and a leading BOM breaks Python's json.load. Do not pass an
// encoding that adds a BOM here.
//
// NOTE: StrategyDocument.WriteAtomic deliberately stays separate — it has DISTINCT semantics
// (Guid-suffixed temp + Utf8NoBom for .py source + a bool result), not the sidecar contract.

using System.IO;

public static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        string dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        string tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);   // UTF-8 no BOM (the engine's json.load requires no BOM)
        if (File.Exists(path))
            File.Replace(tmp, path, null);  // single atomic swap on NTFS; no backup kept
        else
            File.Move(tmp, path);
    }
}
