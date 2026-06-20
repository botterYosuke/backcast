// StrategyEditorView.cs — issue #16 "Strategy Editor" / #81 cell-as-floating-window (Unity boundary)
//
// The thin MonoBehaviour that wires the legacy InputField editing surface to the pure cores. Since
// #81 (ADR-0013) it is a FRAGMENT VIEW over ONE Cell (marimo `cell-flow-node.tsx` reads
// `useCellData().code`), NOT a file: it has no path, no Open/Save, no provider registration. The
// notebook aggregate owns the `.py`/dirty/Save/Open and IS the IStrategyFileProvider; this view only
// edits its bound Cell's BODY, and the aggregate marks itself dirty via the Cell's body-changed hook.
//
// It still:
//   * mirrors InputField.onValueChanged into Cell.SetBody and records into EditHistory via the
//     SNAPSHOT model (the boundary keeps the previous {text, anchor, focus} because onValueChanged
//     does NOT give the before-selection, findings 0010 §3) — EditHistory is PER cell window
//     (marimo: each cell is an independent CodeMirror with independent history);
//   * re-tokenizes with PythonHighlighter and pushes spans to PythonSyntaxMeshEffect;
//   * wires undo/redo to EditHistory (Cmd/Ctrl+Z, Cmd/Ctrl+Shift+Z, Ctrl+Y), raising a suppression
//     flag while it writes InputField.text so the resulting onValueChanged does not re-Record.
//
// Bind(cell) (re)points the view at a Cell — used on spawn, on dormant region_001 reuse (a new cell
// in the same GameObject shell), and on Open (the aggregate replaced the cell list). The pure logic
// (highlight, history) is AFK-authoritative; THIS boundary (InputField sync, keys, IME) is HITL.

using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[RequireComponent(typeof(RectTransform))]
public class StrategyEditorView : MonoBehaviour
{
    public Cell BoundCell { get; private set; }

    // #95 Phase 2 土台: the per-cell RUN output currently shown (test/probe observability; the
    // production path is SetOutput). Null when there is no output pane.
    public string CurrentOutput => _output != null ? _output.text : null;

    // #95 Phase 6 Slice 4 (findings 0075 P6-1): fired when an edit is COMMITTED (the field loses focus
    // after a change — onEndEdit, which for a MultiLineNewline field is blur, NOT Enter). The root wires
    // this to NotebookRunController.Restage so an edit/blur re-projects the per-cell stale badges. Null
    // when no restage consumer is attached (e.g. an unbound shell before the root wires it).
    public Action EditCommitted;

    InputField _input;
    PythonSyntaxMeshEffect _effect;
    EditHistory _history;
    Text _placeholder;          // host-API hint shown when this is the only cell and it is empty
    Text _output;               // #95 Phase 2 土台: per-cell RUN output text (below the editor)

    // Snapshot of the InputField state BEFORE the change currently being recorded.
    string _prevText = string.Empty;
    int _prevAnchor, _prevFocus;
    bool _suppress;   // true while WE drive InputField.text (undo/redo/bind) -> do not Record

    // Wire the view to its surface + cores. Call once after the subtree is built. `cell` may be null
    // (an unbound shell — e.g. the adopted region_001 before the notebook binds cell 0); Bind() points
    // it at a real cell later. `placeholder` is optional (the single-cell host-API hint).
    public void Initialize(InputField input, PythonSyntaxMeshEffect effect, EditHistory history,
                           Cell cell = null, Text placeholder = null, Text output = null)
    {
        _input = input;
        _effect = effect;
        _history = history;
        _placeholder = placeholder;
        _output = output;
        BoundCell = cell;

        _input.onValueChanged.AddListener(OnValueChanged);
        _input.onEndEdit.AddListener(OnEditCommitted);   // #95 P6 S4: blur -> restage (per-cell stale)
        SyncFromCell();   // initial: the bound cell's body, or empty when unbound

        ThemeService.Changed += ApplyTheme;
        ApplyTheme();
    }

    void OnDestroy()
    {
        if (_input != null)
        {
            _input.onValueChanged.RemoveListener(OnValueChanged);
            _input.onEndEdit.RemoveListener(OnEditCommitted);
        }
        ThemeService.Changed -= ApplyTheme;
    }

    // (Re)bind this view to a Cell: swap the bound cell, drop history (a different cell's edits are
    // not this cell's undo stack — marimo per-CodeMirror history), and re-sync the surface so nothing
    // stale renders. Used on spawn, dormant region_001 reuse, and Open (cell list replaced).
    public void Bind(Cell cell)
    {
        BoundCell = cell;
        _history.Clear();
        SetOutput(null);   // a different cell's run output is not this cell's — clear on rebind
        SyncFromCell();
    }

    // #95 Phase 2 土台: show this cell's per-cell RUN output (text repr; rich output is Phase 6).
    // Null/empty hides the pane so an un-run cell shows no stale/empty box. Cheap, idempotent.
    public void SetOutput(string text)
    {
        if (_output == null) return;
        _output.text = text ?? string.Empty;
        _output.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    // Show/hide the single-cell host-API placeholder hint (marimo showPlaceholder = hasOnlyOneCell).
    // The coordinator calls this with the hint text for the only remaining cell, or null otherwise.
    // The hint is NEVER written into the body (no seed焼き込み, findings 0050) — it is a placeholder
    // Graphic that uGUI shows only while the field is empty.
    public void SetPlaceholderHint(string hint)
    {
        if (_placeholder == null) return;
        _placeholder.text = hint ?? string.Empty;
        _placeholder.gameObject.SetActive(!string.IsNullOrEmpty(hint));
    }

    public void ApplyTheme()
    {
        var c = ThemeService.Current.colors;
        if (_input != null)
        {
            var img = _input.GetComponent<Image>();
            if (img != null) img.color = c.background;
            if (_input.textComponent != null) _input.textComponent.color = c.text;
        }
        if (_placeholder != null) { var pc = c.text; pc.a = 0.4f; _placeholder.color = pc; }
        if (_effect != null) _effect.ApplyTheme();
    }

    // ---- editing sync (snapshot model) ----

    void OnValueChanged(string newText)
    {
        if (_suppress) return;   // our own undo/redo/bind write — handled explicitly

        int curAnchor = _input.selectionAnchorPosition;
        int curFocus = _input.selectionFocusPosition;

        _history.Record(_prevText, _prevAnchor, _prevFocus, newText, curAnchor, curFocus);
        BoundCell?.SetBody(newText);   // -> aggregate dirty (the cell's body-changed hook)
        _prevText = newText;
        _prevAnchor = curAnchor;
        _prevFocus = curFocus;
        Retokenize(newText);
    }

    // #95 Phase 6 Slice 4 (findings 0075 P6-1): the field committed an edit (blur). Notify the root so
    // it re-projects the per-cell stale badges. onEndEdit ALSO fires on Enter for a single-line field,
    // but this field is MultiLineNewline (Enter inserts a newline), so this is blur-only — marimo-like.
    // Cheap: the restage is a no-op when the source is unchanged (the backend diff-registers).
    void OnEditCommitted(string _) => EditCommitted?.Invoke();

    void Retokenize(string text)
    {
        if (_effect != null) _effect.SetTokens(PythonHighlighter.Tokenize(text));
    }

    // ---- undo/redo keys ----

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null || _input == null || !_input.isFocused) return;

        bool ctrl = kb.leftCtrlKey.isPressed || kb.rightCtrlKey.isPressed;
        bool cmd = kb.leftCommandKey.isPressed || kb.rightCommandKey.isPressed;
        bool shift = kb.leftShiftKey.isPressed || kb.rightShiftKey.isPressed;
        bool primary = ctrl || cmd;   // Cmd on macOS, Ctrl on Windows/Linux

        if (primary && shift && kb.zKey.wasPressedThisFrame) { DoRedo(); return; }
        if (ctrl && kb.yKey.wasPressedThisFrame) { DoRedo(); return; }
        if (primary && !shift && kb.zKey.wasPressedThisFrame) { DoUndo(); return; }
    }

    public void DoUndo()
    {
        if (_history.Undo(out string text, out int a, out int f)) ApplyHistoryState(text, a, f);
    }

    public void DoRedo()
    {
        if (_history.Redo(out string text, out int a, out int f)) ApplyHistoryState(text, a, f);
    }

    void ApplyHistoryState(string text, int anchor, int focus) => ApplyTextAndSelection(text, anchor, focus);

    // Drive the InputField to `text` with the given selection WITHOUT recording (the _suppress flag
    // swallows the resulting onValueChanged), then sync the cell, advance the snapshot, and
    // re-tokenize. The SINGLE owner of the InputField<->cell<->snapshot coupling: undo/redo
    // (ApplyHistoryState) routes through it. Restores the FULL selection (anchor+focus), not just a
    // caret: setting caretPosition would collapse the selection (Unity's caretPosition setter moves
    // BOTH ends), losing the recorded anchor; selectionFocusPosition renders the caret at focus.
    void ApplyTextAndSelection(string text, int anchor, int focus)
    {
        _suppress = true;
        _input.text = text;                       // fires onValueChanged -> ignored via _suppress
        _input.selectionAnchorPosition = anchor;
        _input.selectionFocusPosition = focus;
        _suppress = false;

        BoundCell?.SetBody(text);
        _prevText = text;
        _prevAnchor = anchor;
        _prevFocus = focus;
        Retokenize(text);
    }

    // Push the bound cell's body into the InputField without recording, and reset the snapshot to
    // match (used after Bind / initial wire). Unbound -> empty surface.
    void SyncFromCell()
    {
        string text = BoundCell?.Body ?? string.Empty;
        _suppress = true;
        if (_input != null)
        {
            _input.text = text;
            _input.caretPosition = 0;
            _input.selectionAnchorPosition = 0;
            _input.selectionFocusPosition = 0;
        }
        _suppress = false;

        _prevText = text;
        _prevAnchor = 0;
        _prevFocus = 0;
        Retokenize(text);
    }
}
