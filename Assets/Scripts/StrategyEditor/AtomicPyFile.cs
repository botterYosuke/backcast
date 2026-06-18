// AtomicPyFile.cs — the atomic temp+replace writer for .py SOURCE files (#16/#81).
//
// The ONE implementation of "write a .py via a temp file in the same directory, then atomically
// replace" with the SOURCE-file contract (distinct from AtomicFile, which writes the sidecar .json):
//   * Guid-suffixed temp (a concurrent writer can't collide on a fixed `.tmp`),
//   * UTF-8 WITHOUT BOM (Python's parser must not see a leading BOM),
//   * a BOOL result — a failed replace leaves the destination's prior content intact (findings 0010
//     §3: replace-failure preserves it), so the caller can retain dirty/path on failure.
// Shared by MarimoNotebookDocument (the #81 notebook aggregate) and the retiring StrategyDocument so
// the .py-write semantics live in one place (a fix is one edit, not two). StrategyEditorProbe §3 pins
// the replace-failure-preserves contract.

using System;
using System.IO;
using System.Text;

public static class AtomicPyFile
{
    static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(/*encoderShouldEmitUTF8Identifier:*/ false);

    public static bool Write(string path, string text)
    {
        string dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return false;

        string tmp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, text, Utf8NoBom);
            if (File.Exists(path)) File.Replace(tmp, path, /*destinationBackupFileName:*/ null);
            else File.Move(tmp, path);
            return true;
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best-effort cleanup */ }
            return false;
        }
    }
}
