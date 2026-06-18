// StrategyEditorView.cs — issue #16 "Strategy Editor" (DURABLE tier, Unity boundary)
//
// The thin MonoBehaviour that wires the legacy InputField editing surface to the pure cores
// (findings 0010 §1/§8). The InputField OWNS caret/selection/IME/clipboard/mouse selection; this
// view never re-implements the surface and never forwards raw keys. It:
//   * mirrors InputField.onValueChanged into StrategyDocument.SetText and records into EditHistory
//     via the SNAPSHOT model (the boundary keeps the previous {text, anchor, focus} because
//     onValueChanged does NOT give the before-selection, findings 0010 §3);
//   * re-tokenizes with PythonHighlighter and pushes spans to PythonSyntaxMeshEffect;
//   * wires undo/redo to EditHistory (Cmd/Ctrl+Z, Cmd/Ctrl+Shift+Z, Ctrl+Y) — uGUI InputField has
//     NO undo/redo of its own — raising a suppression flag while it writes InputField.text so the
//     resulting onValueChanged does not re-Record;
//   * registers its document as the IStrategyFileProvider under the window id.
//
// The pure logic (highlight, history, document, restore, registry) is AFK-authoritative; THIS
// boundary (InputField sync, key handling, IME) is the HITL surface (findings 0010 §9).

using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

[RequireComponent(typeof(RectTransform))]
public class StrategyEditorView : MonoBehaviour
{
    public StrategyDocument Document { get; private set; }

    InputField _input;
    PythonSyntaxMeshEffect _effect;
    EditHistory _history;
    StrategyProviderRegistry _registry;
    string _windowId;

    // Snapshot of the InputField state BEFORE the change currently being recorded.
    string _prevText = string.Empty;
    int _prevAnchor, _prevFocus;
    bool _suppress;   // true while WE drive InputField.text (undo/redo/open) -> do not Record

    // Wire the view to its surface + cores. Call once after the subtree is built. `registry`/
    // `windowId` may be null to skip provider registration (e.g. an isolated AFK harness).
    public void Initialize(InputField input, PythonSyntaxMeshEffect effect,
                           StrategyDocument document, EditHistory history,
                           StrategyProviderRegistry registry, string windowId)
    {
        _input = input;
        _effect = effect;
        Document = document;
        _history = history;
        _registry = registry;
        _windowId = windowId;

        _input.onValueChanged.AddListener(OnValueChanged);
        SyncFromDocument();   // initial unbound-empty (or whatever the document already holds)

        if (_registry != null && !string.IsNullOrEmpty(_windowId))
            _registry.Register(_windowId, Document);

        // issue #44: re-theme on a theme switch. This view owns a lifecycle (OnDestroy), so it
        // self-subscribes — the durable editor window re-themes without a separate owner wiring it.
        ThemeService.Changed += ApplyTheme;
        ApplyTheme();
    }

    void OnDestroy()
    {
        if (_input != null) _input.onValueChanged.RemoveListener(OnValueChanged);
        if (_registry != null && !string.IsNullOrEmpty(_windowId))
            _registry.Unregister(_windowId);
        ThemeService.Changed -= ApplyTheme;
    }

    // Repaint the editor surface from the active theme (issue #44): the InputField background,
    // the base text colour, and the syntax palette. Self-subscribed to ThemeService.Changed in
    // Initialize (and called once there).
    public void ApplyTheme()
    {
        var c = ThemeService.Current.colors;
        if (_input != null)
        {
            var img = _input.GetComponent<Image>();
            if (img != null) img.color = c.background;
            if (_input.textComponent != null) _input.textComponent.color = c.text;
        }
        if (_effect != null) _effect.ApplyTheme();
    }

    // ---- editing sync (snapshot model) ----

    void OnValueChanged(string newText)
    {
        if (_suppress) return;   // our own undo/redo/open write — handled explicitly

        int curAnchor = _input.selectionAnchorPosition;
        int curFocus = _input.selectionFocusPosition;

        _history.Record(_prevText, _prevAnchor, _prevFocus, newText, curAnchor, curFocus);
        Document.SetText(newText);
        _prevText = newText;
        _prevAnchor = curAnchor;
        _prevFocus = curFocus;
        Retokenize(newText);
    }

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

        // Redo: Cmd/Ctrl+Shift+Z, or Ctrl+Y. Undo: Cmd/Ctrl+Z (no Shift).
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
    // swallows the resulting onValueChanged), then sync the document, advance the snapshot, and
    // re-tokenize. This is the SINGLE owner of the InputField↔document↔snapshot coupling — undo/redo
    // (ApplyHistoryState) routes through it, and the #81 cell-as-window model (ADR-0013) reuses it for
    // programmatic body edits so every path stays consistent. Restores the FULL selection
    // (anchor+focus), not just a caret: setting caretPosition would collapse the selection to
    // zero-length (Unity's caretPosition setter moves BOTH ends), losing the anchor the transaction
    // recorded; selectionFocusPosition renders the caret at focus.
    void ApplyTextAndSelection(string text, int anchor, int focus)
    {
        _suppress = true;
        _input.text = text;                       // fires onValueChanged -> ignored via _suppress
        _input.selectionAnchorPosition = anchor;
        _input.selectionFocusPosition = focus;
        _suppress = false;

        Document.SetText(text);
        _prevText = text;
        _prevAnchor = anchor;
        _prevFocus = focus;
        Retokenize(text);
    }

    // ---- file ops ----

    public bool Open(string path)
    {
        if (!Document.Open(path)) return false;
        _history.Clear();          // file open clears history (findings 0010 §3)
        SyncFromDocument();
        return true;
    }

    public bool Save()
    {
        bool ok = Document.Save();
        if (ok) _history.MarkSaveBoundary();   // boundary, but history stays undoable
        return ok;
    }

    // Save As (#69): write the buffer to a NEW .py and rebind the document to it (the editor
    // now shows/edits the new file). History stays undoable across the boundary, like Save().
    public bool SaveAs(string newPath)
    {
        bool ok = Document.SaveAs(newPath);
        if (ok) _history.MarkSaveBoundary();
        return ok;
    }

    // Restore-boundary content apply (findings 0010 §7): full replacement, then refresh.
    public void RestoreFrom(StrategyEditorState state)
    {
        StrategyEditorRestore.Apply(Document, state);
        _history.Clear();
        SyncFromDocument();
    }

    // File→New reset (findings 0027 D3): drop to unbound-empty WITHOUT destroying the view, so the
    // scene-authored adopted editor is reset IN PLACE (findings 0025 §8). Text/path cleared, history
    // dropped, and the InputField + token surface re-synced so nothing stale renders. Mirrors
    // RestoreFrom's reset→clear→sync boundary so the InputField never diverges from the document.
    public void ResetUnboundEmpty()
    {
        Document.ResetUnboundEmpty();
        _history.Clear();
        SyncFromDocument();
    }

    // File→New marimo skeleton (#76 S6b-β-clean U2): seed starter template text into the UNBOUND
    // editor (mirrors ResetUnboundEmpty's reset→clear→sync boundary, but with starter text so a fresh
    // workspace is immediately a valid — if unsaved — marimo strategy). History is dropped; the
    // InputField + token surface re-sync so nothing stale renders.
    public void SeedUnbound(string text)
    {
        Document.SeedUnbound(text);
        _history.Clear();
        SyncFromDocument();
    }

    // Push the document's current text into the InputField without recording, and reset the
    // snapshot to match (used after open/restore/unbound-empty).
    void SyncFromDocument()
    {
        string text = Document.Text ?? string.Empty;
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

    // Capture this editor's persistable content state (findings 0010 §7): the canonical .py path,
    // or null when unbound (the caller omits a null from the document).
    public StrategyEditorState CaptureState()
    {
        string path = Document.CurrentPath;
        return string.IsNullOrEmpty(path) ? null : new StrategyEditorState(_windowId, path);
    }
}
