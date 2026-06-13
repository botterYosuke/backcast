// EditHistory.cs — issue #16 "Strategy Editor" (DURABLE tier, PURE CORE)
//
// The AUTHORITATIVE, headless undo/redo stack — the AFK gate proves THIS (no
// InputField, no Canvas). uGUI's InputField.KeyPressed has NO undo/redo at all
// (findings 0010 §1), so this owns it; the Unity boundary wires the keys
// (Cmd/Ctrl+Z, Cmd/Ctrl+Shift+Z, Ctrl+Y) and restores the returned text+selection.
//
// SNAPSHOT MODEL (findings 0010 §3, owner-locked): InputField.onValueChanged does
// NOT hand us the BEFORE-selection, so the boundary keeps the previous
// {text, anchor, focus} snapshot and calls Record(prev..., cur...). A transaction =
// {beforeText, afterText, before/after selection}; Undo/Redo return the target text
// AND selection for the boundary to restore. During an undo/redo-driven InputField
// update the boundary raises a suppression flag so the resulting onValueChanged does
// NOT re-Record.
//
// BOUNDARY-BASED COALESCING (findings 0010 §3 — NOT time-based, which is non-
// deterministic for an AFK gate):
//   * a run of single-char INSERTS coalesces into one transaction;
//   * a run of single-char DELETES coalesces, but SEPARATELY by direction
//     (backspace vs forward-delete);
//   * a NEWLINE insert is STANDALONE and closes the open group;
//   * paste / selection-replace / IME-commit (any multi-char change) is STANDALONE;
//   * a caret jump (selection present, or non-contiguous caret) starts a new group;
//   * switching kind (insert<->delete, or delete direction) starts a new group;
//   * a fresh edit after undo CLEARS the redo stack;
//   * Save is a group boundary but does NOT clear history (undo still works);
//   * file open/reload CLEARS history;
//   * a no-op (text unchanged — even if only the selection moved) is NOT recorded;
//   * depth is capped at 200 transactions; overflow drops the OLDEST undo.

using System.Collections.Generic;

public class EditHistory
{
    public const int MaxDepth = 200;

    enum Kind { Insert, DeleteBackward, DeleteForward, Standalone }

    class Tx
    {
        public string beforeText;
        public int beforeAnchor, beforeFocus;
        public string afterText;
        public int afterAnchor, afterFocus;
        public Kind kind;
    }

    readonly List<Tx> _undo = new List<Tx>();   // index 0 = oldest, last = top
    readonly List<Tx> _redo = new List<Tx>();
    bool _groupOpen;                             // is the top undo Tx still accepting coalesced edits?

    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    // file open / reload: wipe everything (findings 0010 §3).
    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _groupOpen = false;
    }

    // Save: close the open group so the next edit starts fresh, but KEEP history undoable.
    public void MarkSaveBoundary()
    {
        _groupOpen = false;
    }

    // Record a change from the boundary's previous snapshot to the current InputField state.
    // No-op (text unchanged) is ignored even if the selection moved.
    public void Record(string prevText, int prevAnchor, int prevFocus,
                       string curText, int curAnchor, int curFocus)
    {
        prevText ??= string.Empty;
        curText ??= string.Empty;
        if (prevText == curText) return;   // no-op (selection-only move) -> not recorded

        // any fresh edit invalidates the redo branch.
        _redo.Clear();

        Kind kind = Classify(prevText, curText, prevAnchor, prevFocus, curAnchor, curFocus);

        bool coalesced = false;
        if (_groupOpen && _undo.Count > 0)
        {
            Tx top = _undo[_undo.Count - 1];
            bool noSelectionBefore = prevAnchor == prevFocus;
            bool caretContinuous = noSelectionBefore && prevFocus == top.afterFocus
                                   && prevAnchor == top.afterAnchor;
            if (caretContinuous && kind == top.kind &&
                (kind == Kind.Insert || kind == Kind.DeleteBackward || kind == Kind.DeleteForward))
            {
                top.afterText = curText;
                top.afterAnchor = curAnchor;
                top.afterFocus = curFocus;
                coalesced = true;
            }
        }

        if (!coalesced)
        {
            _undo.Add(new Tx
            {
                beforeText = prevText, beforeAnchor = prevAnchor, beforeFocus = prevFocus,
                afterText = curText, afterAnchor = curAnchor, afterFocus = curFocus,
                kind = kind,
            });
            // a coalescible kind opens a group for the next contiguous edit; a standalone
            // (newline / multi-char) leaves the group CLOSED.
            _groupOpen = kind == Kind.Insert || kind == Kind.DeleteBackward || kind == Kind.DeleteForward;

            if (_undo.Count > MaxDepth) _undo.RemoveAt(0);   // drop oldest
        }
    }

    public bool Undo(out string text, out int anchor, out int focus)
    {
        if (_undo.Count == 0) { text = null; anchor = focus = 0; return false; }
        Tx top = _undo[_undo.Count - 1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(top);
        _groupOpen = false;
        text = top.beforeText;
        anchor = top.beforeAnchor;
        focus = top.beforeFocus;
        return true;
    }

    public bool Redo(out string text, out int anchor, out int focus)
    {
        if (_redo.Count == 0) { text = null; anchor = focus = 0; return false; }
        Tx top = _redo[_redo.Count - 1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(top);
        _groupOpen = false;
        text = top.afterText;
        anchor = top.afterAnchor;
        focus = top.afterFocus;
        return true;
    }

    // Classify a text change via common prefix/suffix diff into a coalescing kind.
    static Kind Classify(string prev, string cur, int prevAnchor, int prevFocus, int curAnchor, int curFocus)
    {
        int pl = prev.Length, cl = cur.Length;
        int p = 0;
        int max = pl < cl ? pl : cl;
        while (p < max && prev[p] == cur[p]) p++;
        int s = 0;
        while (s < (max - p) && prev[pl - 1 - s] == cur[cl - 1 - s]) s++;

        int removedLen = pl - p - s;
        int insertedLen = cl - p - s;

        bool noSelectionBefore = prevAnchor == prevFocus;

        if (removedLen == 0 && insertedLen == 1 && noSelectionBefore)
        {
            return cur[p] == '\n' ? Kind.Standalone : Kind.Insert;   // newline insert is standalone
        }
        if (insertedLen == 0 && removedLen == 1 && noSelectionBefore)
        {
            // backspace moves the caret back (curFocus < prevFocus); forward-delete keeps it.
            return curFocus < prevFocus ? Kind.DeleteBackward : Kind.DeleteForward;
        }
        return Kind.Standalone;   // paste / selection-replace / IME-commit / multi-char
    }
}
