// StrategyDocument.cs — issue #16 "Strategy Editor" (DURABLE tier, PURE CORE)
//
// The document AUTHORITY (findings 0010 §1/§4/§5): owns source text, the bound
// canonical .py path, the dirty flag, Open/Save, and the IStrategyFileProvider
// contract. UnityEngine-FREE (only System.IO/Text) so the AFK gate drives the whole
// file model headless; the Unity boundary (StrategyEditorView) syncs text via
// InputField.onValueChanged and owns the EditHistory (this class never touches
// history — open/reload clearing and the save boundary are the view's call).
//
// FILE MODEL (findings 0010 §4, owner-locked):
//   Open(path): succeeds only for an EXISTING normal file with a .py extension; keeps
//     the canonical absolute path; reads UTF-8; on success dirty=false. On ANY failure
//     the document is UNCHANGED (no new-file creation, no picker — out of scope).
//   Save():     overwrite the SAME bound path only; UTF-8 ATOMIC write (temp file in
//     the same directory, then replace) so a failed replace leaves the on-disk content
//     intact; dirty=false only on success; on failure text/path/dirty are retained.
//   ResetUnboundEmpty(): the RESTORE-boundary-only "content-not-restored" state
//     (findings 0010 §7) — empty text, no path, not dirty. This is ALSO the state of a
//     freshly spawned editor before any Open. It is NOT a normal Open failure (which
//     leaves the document unchanged); only the restore controller calls it.

using System;
using System.IO;
using System.Text;

public class StrategyDocument : IStrategyFileProvider
{
    static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(/*encoderShouldEmitUTF8Identifier:*/ false);

    string _text = string.Empty;
    string _path;                 // null = unbound; otherwise canonical absolute .py
    bool _dirty;
    bool _openedOrSaved;          // last Open OR Save succeeded (provider condition 3)

    public string Text => _text;
    public string CurrentPath => _path;
    public bool IsDirty => _dirty;
    public bool IsBound => _path != null;

    // Sync from the editing surface. Marks dirty only when the text actually changes
    // (a no-op assignment must not flip a clean buffer to dirty).
    public void SetText(string text)
    {
        text ??= string.Empty;
        if (text == _text) return;
        _text = text;
        _dirty = true;
    }

    // Open an existing .py. Returns false (document UNCHANGED) on any validation/read failure.
    public bool Open(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }

        if (!string.Equals(Path.GetExtension(full), ".py", StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(full)) return false;   // false for a directory or a missing path

        string content;
        try { content = File.ReadAllText(full, Encoding.UTF8); }
        catch { return false; }

        _text = content;
        _path = full;
        _dirty = false;
        _openedOrSaved = true;
        return true;
    }

    // Overwrite the bound path with the current buffer via an atomic temp+replace.
    public bool Save()
    {
        if (_path == null) return false;

        string dir = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(dir)) return false;

        string tmp = Path.Combine(dir, "." + Path.GetFileName(_path) + ".tmp-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(tmp, _text, Utf8NoBom);
            // Atomic replace: if the temp write or the replace throws, the destination is
            // left with its prior content (findings 0010 §3 — replace-failure preserves it).
            if (File.Exists(_path)) File.Replace(tmp, _path, /*destinationBackupFileName:*/ null);
            else File.Move(tmp, _path);
        }
        catch
        {
            TryDelete(tmp);
            return false;   // text/path/dirty retained
        }

        _dirty = false;
        _openedOrSaved = true;
        return true;
    }

    // Restore-boundary reset to unbound-empty (findings 0010 §7). NOT a normal Open failure.
    public void ResetUnboundEmpty()
    {
        _text = string.Empty;
        _path = null;
        _dirty = false;
        _openedOrSaved = false;
    }

    // IStrategyFileProvider — supplyable iff ALL 5 conditions hold (findings 0010 §5).
    public bool TryGetStrategyFile(out string path)
    {
        path = null;
        if (_path == null) return false;                                    // 1. bound
        if (_dirty) return false;                                           // 2. not dirty
        if (!_openedOrSaved) return false;                                  // 3. last Open/Save ok
        if (!Path.IsPathRooted(_path)) return false;                        // 4. canonical absolute...
        if (!string.Equals(Path.GetExtension(_path), ".py", StringComparison.OrdinalIgnoreCase)) return false;  // ...and .py
        if (!File.Exists(_path)) return false;                             // 5. still a normal file now
        path = _path;
        return true;
    }

    static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { /* best-effort cleanup */ }
    }
}
