// StrategyEditorView.cs — issue #16 "Strategy Editor" / #81 cell-as-floating-window (Unity boundary)
//                       + #95 Phase 6 (rich output mimetype routing) / #102 (console + dynamic layout)
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
// #102 (findings 0076): SetOutput / SetConsole both drive a dynamic re-layout that auto-collapses
// the rich and console blocks to zero height when empty (editor takes the full body) and caps each
// at ~45% of the body height when populated (per-block ScrollRect kicks in past the cap).  stderr
// segments paint amber (marimo `Outputs.css .stderr` parity); stdout paints the default text colour.
//
// Bind(cell) (re)points the view at a Cell — used on spawn, on dormant region_001 reuse (a new cell
// in the same GameObject shell), and on Open (the aggregate replaced the cell list). The pure logic
// (highlight, history) is AFK-authoritative; THIS boundary (InputField sync, keys, IME) is HITL.

using System;
using System.Text;
using System.Text.RegularExpressions;
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

    // #95 Phase 6 Slice 5 (findings 0075 P6-2): true when the last SetOutput rendered an IMAGE
    // (image/png|jpeg) into the sibling RawImage rather than the Text pane. The AFK rich-output gate
    // (S19) asserts this to prove image routing (a regression that collapses every mimetype to Text
    // leaves the RawImage inactive). False for text/markdown/html/plain output and when cleared.
    public bool OutputIsImage => _image != null && _image.gameObject.activeSelf;

    // #102 Slice 2 (findings 0076): the AFK gate (Section20) reads these to assert dynamic layout
    // — visibility of each output block + the current console text (with amber tags) — without
    // having to walk the GameObject tree from a test.
    public bool RichBlockVisible => _richBlock != null && _richBlock.gameObject.activeSelf;
    public bool ConsoleBlockVisible => _consoleBlock != null && _consoleBlock.gameObject.activeSelf;
    public string CurrentConsoleText => _consoleText != null ? _consoleText.text : null;

    // #95 Phase 6 Slice 4 (findings 0075 P6-1): fired when an edit is COMMITTED (the field loses focus
    // after a change — onEndEdit, which for a MultiLineNewline field is blur, NOT Enter). The root wires
    // this to NotebookRunController.Restage so an edit/blur re-projects the per-cell stale badges. Null
    // when no restage consumer is attached (e.g. an unbound shell before the root wires it).
    public Action EditCommitted;

    InputField _input;
    PythonSyntaxMeshEffect _effect;
    EditHistory _history;
    Text _placeholder;          // host-API hint shown when this is the only cell and it is empty
    Text _output;               // #95 Phase 2 土台: per-cell RUN output text (in the rich block)
    RawImage _image;            // #95 Phase 6 Slice 5: image/png|jpeg sibling inside the rich block
    Texture2D _tex;             // #95 Phase 6 Slice 5: the decoded image texture we own (freed on replace/clear/destroy)

    // #102 Slice 2: the dynamic layout pieces (findings 0076 D2).
    RectTransform _richBlock;
    LayoutElement _richLE;
    RectTransform _consoleBlock;
    Text _consoleText;
    LayoutElement _consoleLE;
    LayoutElement _editorLE;
    RectTransform _body;

    // Snapshot of the InputField state BEFORE the change currently being recorded.
    string _prevText = string.Empty;
    int _prevAnchor, _prevFocus;
    bool _suppress;   // true while WE drive InputField.text (undo/redo/bind) -> do not Record

    // Wire the view to its surface + cores. Call once after the subtree is built. `cell` may be null
    // (an unbound shell — e.g. the adopted region_001 before the notebook binds cell 0); Bind() points
    // it at a real cell later. `placeholder` is optional (the single-cell host-API hint).
    public void Initialize(
        InputField input, PythonSyntaxMeshEffect effect, EditHistory history,
        Cell cell, Text placeholder,
        Text output, RawImage image,
        RectTransform richBlock, LayoutElement richLE,
        RectTransform consoleBlock, Text consoleText, LayoutElement consoleLE,
        LayoutElement editorLE)
    {
        _input = input;
        _effect = effect;
        _history = history;
        _placeholder = placeholder;
        _output = output;
        _image = image;
        _richBlock = richBlock;
        _richLE = richLE;
        _consoleBlock = consoleBlock;
        _consoleText = consoleText;
        _consoleLE = consoleLE;
        _editorLE = editorLE;
        // The StrategyCodeInput we live on is a child of the body — the VerticalLayoutGroup is on
        // the body itself, so that is what we mark for rebuild after sizing.
        _body = transform.parent as RectTransform;
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
        FreeTexture();   // #95 P6 S5: release the decoded image texture we own
    }

    // (Re)bind this view to a Cell: swap the bound cell, drop history (a different cell's edits are
    // not this cell's undo stack — marimo per-CodeMirror history), and re-sync the surface so nothing
    // stale renders. Used on spawn, dormant region_001 reuse, and Open (cell list replaced).
    public void Bind(Cell cell)
    {
        BoundCell = cell;
        _history.Clear();
        SetOutput(null);              // a different cell's run output is not this cell's — clear on rebind
        SetConsole(null);             // ditto for console (#102 AC: cell rebind clears console)
        SyncFromCell();
    }

    // #95 Phase 6 Slice 5 (findings 0075 P6-2): render this cell's per-cell RUN output by mimetype.
    //   * image/png | image/jpeg → decode `data` (a `data:` URL or raw base64) into the sibling
    //     RawImage (Texture2D.LoadImage); the Text pane hides.
    //   * text/markdown | text/html → the legacy Text with supportRichText, converting the HTML/
    //     markdown subset (headings/bold/italic/bullets, <table>→pipe rows) to Unity rich-text tags.
    //   * text/plain | unsupported | no mimetype → the plain `text` projection (tag-stripped by the
    //     backend), with an unsupported mimetype labelled for debug visibility.
    // Null/empty content hides the RICH block (#102: the dynamic layout collapses it to zero height).
    // Cheap, idempotent.  Back-compat: SetOutput(null) clears (the old single-arg call site on Bind
    // still compiles).
    public void SetOutput(string text, string mimetype = null, string data = null)
    {
        string mt = mimetype ?? string.Empty;
        bool empty = string.IsNullOrEmpty(text) && string.IsNullOrEmpty(data);
        if (empty)
        {
            FreeTexture();
            if (_image != null) _image.gameObject.SetActive(false);
            if (_output != null) _output.gameObject.SetActive(false);
            if (_richBlock != null) _richBlock.gameObject.SetActive(false);
            RefreshEditorMin();   // the body may have resized since the last paint — keep editor min current
            return;
        }

        if ((mt == "image/png" || mt == "image/jpeg") && TryDecodeImage(data))
        {
            if (_image != null) _image.gameObject.SetActive(true);
            if (_output != null) _output.gameObject.SetActive(false);
            if (_richBlock != null) _richBlock.gameObject.SetActive(true);
            ApplyBlockSize(_richLE, _output, _image);
            return;
        }

        if (mt == "text/markdown" || mt == "text/html")
        {
            FreeTexture();
            if (_image != null) _image.gameObject.SetActive(false);
            if (_output != null)
            {
                _output.supportRichText = true;
                _output.text = RichToUnity(mt, string.IsNullOrEmpty(data) ? text : data);
                _output.gameObject.SetActive(true);
            }
            if (_richBlock != null) _richBlock.gameObject.SetActive(true);
            ApplyBlockSize(_richLE, _output, _image);
            return;
        }

        // text/plain, no mimetype, or an unsupported type → plain fallback (debug-label the unknown).
        FreeTexture();
        if (_image != null) _image.gameObject.SetActive(false);
        if (_output != null)
        {
            _output.supportRichText = false;
            bool unknown = !string.IsNullOrEmpty(mt) && mt != "text/plain";
            string body = text ?? string.Empty;
            _output.text = unknown ? "[" + mt + "]\n" + body : body;
            _output.gameObject.SetActive(true);
        }
        if (_richBlock != null) _richBlock.gameObject.SetActive(true);
        ApplyBlockSize(_richLE, _output, _image);
    }

    // #102 Slice 2 (findings 0076): render the per-cell stdout/stderr segment list into the console
    // block.  Segments arrive in arrival order with adjacent-same-stream already collapsed (marimo
    // cell.ts:133 / collapseConsoleOutputs.tsx parity).  Empty / null hides the block (dynamic
    // layout collapses it to zero height — the editor reclaims the room).  stderr paints amber via
    // UGUI rich-text colour tags (marimo `.stderr { color: var(--amber-12); }`); stdout uses the
    // default text colour.  Cheap, idempotent.
    public void SetConsole(ConsoleSegment[] segments)
    {
        if (segments == null || segments.Length == 0)
        {
            if (_consoleText != null) _consoleText.text = string.Empty;
            if (_consoleBlock != null) _consoleBlock.gameObject.SetActive(false);
            RefreshEditorMin();   // body may have resized — keep editor min current on the empty path too
            return;
        }
        if (_consoleText != null)
        {
            _consoleText.text = BuildConsoleRichText(segments);
            _consoleText.supportRichText = true;
        }
        if (_consoleBlock != null) _consoleBlock.gameObject.SetActive(true);
        ApplyBlockSize(_consoleLE, _consoleText, null);
    }

    // Lightweight body-resize follow-up: when SetOutput/SetConsole take the empty branch (block
    // deactivated, no preferredHeight to clamp), the editor minHeight is still derived from
    // body.rect.height, so a window resize between presses must not leave a stale value pinning
    // the editor at the pre-resize floor.  Called from both empty paths.
    void RefreshEditorMin()
    {
        if (_editorLE == null || _body == null) return;
        float bodyH = _body.rect.height;
        if (bodyH > 0f) _editorLE.minHeight = ComputeEditorMin(bodyH);
        LayoutRebuilder.MarkLayoutForRebuild(_body);
    }

    // Build the console pane's rich-text string: per-stream colour tags + arrival order preserved.
    // amber-12 ≈ #ffa01c per Radix (marimo Outputs.css references the var directly); we hardcode the
    // hex so the console paints amber even when ThemeService has not loaded an amber-typed palette.
    // ``supportRichText=true`` on the Text means a raw ``<`` from the user's stdout would be parsed
    // as a tag (``print("<EOF>")`` would vanish entirely or open an unbalanced tag that swallows the
    // rest of the buffer), so each segment's payload is escaped first — only OUR colour tag survives.
    static string BuildConsoleRichText(ConsoleSegment[] segments)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            string body = s.Text ?? string.Empty;
            if (body.Length == 0) continue;
            string safe = EscapeForUguiRichText(body);
            if (s.Stream == "stderr") { sb.Append("<color=#ffa01c>"); sb.Append(safe); sb.Append("</color>"); }
            else { sb.Append(safe); }
        }
        return sb.ToString();
    }

    // Escape user-supplied text for a UGUI Text with supportRichText=true. UGUI parses ``<...>``
    // sequences as tags (color/b/i/size/material/quad); the safest neutralisation is to break the
    // tag open by replacing ``<`` with the entity ``&lt;``.  UGUI does NOT decode entities, so the
    // output renders as ``&lt;`` literally — visually distinguishable from a real tag and stable
    // across versions.  ``&`` is escaped too so ``&lt;`` payloads round-trip unambiguously.
    static string EscapeForUguiRichText(string s)
        => s == null ? string.Empty : s.Replace("&", "&amp;").Replace("<", "&lt;");

    // Set the rich block's LayoutElement.preferredHeight = clamp(measured content height,
    // [_editor min preserved], body * OutputBlockMaxFractionOfBody) — the VerticalLayoutGroup on the
    // body then places the block at that height and the editor gets the residual.  RawImage's
    // contribution defers to the texture's natural height capped at the same fraction.
    // One per-block sizing pass; called by SetOutput, SetConsole, and the empty paths so the editor
    // min is kept consistent with the current body height even when the block deactivates.  The cap
    // is body.height * OutputBlockMaxFractionOfBody — if the body's rect is not yet resolved
    // (bodyH==0 on the same frame as the window's first build), the cap collapses to 0 and the block
    // would pin at 0px; we skip the cap in that case and let the natural height drive (the block's
    // RectMask2D still clips overflow, and the next layout pass + SetOutput will re-apply the cap).
    // For the rich block, RawImage's texture.height takes precedence when an image is showing.
    void ApplyBlockSize(LayoutElement le, Text text, RawImage img)
    {
        if (le == null || _body == null) return;
        float bodyH = _body.rect.height;
        float natural;
        if (img != null && img.gameObject.activeSelf && img.texture != null)
            natural = img.texture.height + 4f;
        else
            natural = MeasureTextPreferredHeight(text) + 4f;
        // bodyH == 0 → layout not yet resolved; skip the cap so the first paint is visible and the
        // RectMask2D clips overflow until the next SetOutput re-runs the cap math against a real bodyH.
        float clamped = bodyH > 0f ? Mathf.Min(natural, bodyH * StrategyEditorContentBuilder.OutputBlockMaxFractionOfBody)
                                   : natural;
        le.preferredHeight = Mathf.Max(0f, clamped);
        if (_editorLE != null && bodyH > 0f) _editorLE.minHeight = ComputeEditorMin(bodyH);
        LayoutRebuilder.MarkLayoutForRebuild(_body);
    }

    static float ComputeEditorMin(float bodyH)
        => Mathf.Max(StrategyEditorContentBuilder.EditorMinFloorPx,
                     bodyH * StrategyEditorContentBuilder.EditorMinFractionOfBody);

    static float MeasureTextPreferredHeight(Text t)
        => t == null ? 0f : t.preferredHeight;

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

    // ---- rich-text projection (findings 0075 P6-2) ----

    // Convert a text/markdown or text/html payload into the legacy Text rich-text SUBSET (<b>/<i>).
    // P6-2 keeps this deliberately small: headings/bold/italic/bullets for markdown, and for HTML a
    // <table>→pipe-row projection with the rest tag-stripped (general HTML is NOT reproduced — owner:
    // that is a separate project). marimo emits text/markdown as rendered HTML, so a payload with
    // tags always takes the HTML leg; a raw-markdown payload takes the markdown leg.
    static string RichToUnity(string mimetype, string data)
    {
        if (string.IsNullOrEmpty(data)) return string.Empty;
        bool looksHtml = data.IndexOf('<') >= 0 && data.IndexOf('>') >= 0;
        return (mimetype == "text/html" || looksHtml) ? HtmlToUnity(data) : MarkdownToUnity(data);
    }

    static readonly Regex _tagRe = new Regex("<[^>]+>");

    static string HtmlToUnity(string html)
    {
        string s = html;
        // table structure → pipe rows BEFORE the generic tag strip (so cell boundaries survive).
        s = Regex.Replace(s, @"</tr\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</t[dh]\s*>", " | ", RegexOptions.IgnoreCase);
        // block breaks → newlines, list items → bullets.
        s = Regex.Replace(s, @"<br\s*/?>|</p>|</div>|</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
        // inline emphasis → Unity tags.
        s = Regex.Replace(s, @"<(b|strong)>", "<b>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(b|strong)>", "</b>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<(i|em)>", "<i>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(i|em)>", "</i>", RegexOptions.IgnoreCase);
        s = _tagRe.Replace(s, string.Empty);   // drop every remaining tag (general HTML not reproduced)
        return Unescape(s).Trim();
    }

    static string MarkdownToUnity(string md)
    {
        string s = md;
        s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s+(.*)$", "<b>$1</b>", RegexOptions.Multiline);
        s = Regex.Replace(s, @"(\*\*|__)(.+?)\1", "<b>$2</b>");
        s = Regex.Replace(s, @"(?<!\*)\*(?!\*)(.+?)\*|_(.+?)_", "<i>$1$2</i>");
        s = Regex.Replace(s, @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
        s = s.Replace("`", string.Empty);   // inline code: the pane is already monospace
        return s.Trim();
    }

    void FreeTexture()
    {
        if (_tex == null) return;
        if (_image != null) _image.texture = null;
        if (Application.isPlaying) Destroy(_tex); else DestroyImmediate(_tex);
        _tex = null;
    }

    // Decode a base64 PNG/JPEG (a `data:<mime>;base64,XXXX` URL — matplotlib/mo.image — or raw
    // base64) into the owned Texture2D and bind it to the RawImage. Returns false on a malformed
    // payload or a missing RawImage so SetOutput falls back to the text pane (never throws).
    bool TryDecodeImage(string data)
    {
        if (_image == null || string.IsNullOrEmpty(data)) return false;
        string b64 = data;
        int comma = b64.IndexOf("base64,", StringComparison.Ordinal);
        if (comma >= 0) b64 = b64.Substring(comma + "base64,".Length);
        else if (b64.StartsWith("data:", StringComparison.Ordinal)) return false;   // a non-base64 data URL
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64.Trim()); }
        catch (FormatException) { return false; }
        FreeTexture();
        var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        if (!tex.LoadImage(bytes)) { Destroy(tex); return false; }
        _tex = tex;
        _image.texture = _tex;
        return true;
    }

    static string Unescape(string s) =>
        s.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&quot;", "\"")
         .Replace("&#39;", "'").Replace("&nbsp;", " ").Replace("&amp;", "&");
}
