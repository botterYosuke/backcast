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
// #102 (findings 0079): SetOutput / SetConsole both drive a dynamic re-layout that auto-collapses
// the rich and console blocks to zero height when empty (editor takes the full body) and caps each
// at ~45% of the body height when populated.  Overflow past the cap is solved by a REAL ScrollRect
// per block (findings 0079 §6 D5 — supersedes the prior RectMask2D-clip implementation): Block holds
// a ScrollRect with Viewport + Content (the Text and optional RawImage are children of Content,
// which a ContentSizeFitter + VerticalLayoutGroup sizes to the active child's preferredHeight).
// stderr segments paint amber (marimo `Outputs.css .stderr` parity); stdout paints the default text
// colour.
//
// Bind(cell) (re)points the view at a Cell — used on spawn, on dormant region_001 reuse (a new cell
// in the same GameObject shell), and on Open (the aggregate replaced the cell list). The pure logic
// (highlight, history) is AFK-authoritative; THIS boundary (InputField sync, keys, IME) is HITL.

using System;
using System.Text;
using System.Text.RegularExpressions;
using TMPro;
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

    // #102 Slice 2 (findings 0079): the AFK gate (Section20) reads these to assert dynamic layout
    // — visibility of each output block + the current console text (with amber tags) — without
    // having to walk the GameObject tree from a test.
    public bool RichBlockVisible => _richBlock != null && _richBlock.gameObject.activeSelf;
    public bool ConsoleBlockVisible => _consoleBlock != null && _consoleBlock.gameObject.activeSelf;
    public string CurrentConsoleText => _consoleText != null ? _consoleText.text : null;

    // #102 findings 0079 §6 D5: the per-block ScrollRect — Section21 (audit gaps) reads these to
    // verify overflow becomes scrollable (Content.rect.height > Viewport.rect.height) and that the
    // verticalNormalizedPosition can be moved (proof the user can scroll).
    public ScrollRect RichScrollRect => _richScroll;
    public ScrollRect ConsoleScrollRect => _consoleScroll;

    // #95 Phase 6 Slice 4 (findings 0075 P6-1): fired when an edit is COMMITTED (the field loses focus
    // after a change — onEndEdit, which for a MultiLineNewline field is blur, NOT Enter). The root wires
    // this to NotebookRunController.Restage so an edit/blur re-projects the per-cell stale badges. Null
    // when no restage consumer is attached (e.g. an unbound shell before the root wires it).
    public Action EditCommitted;

    TMP_InputField _input;      // #119: TMP(SDF) editing surface (was UnityEngine.UI.InputField)
    PythonSyntaxMeshEffect _effect;
    EditHistory _history;
    TMP_Text _placeholder;      // #119: host-API hint shown when this is the only cell and it is empty (TMP_Text)
    TMP_Text _output;           // #95 Phase 2 土台 / #118: per-cell RUN output text (TMP_Text/SDF, rich block)
    RawImage _image;            // #95 Phase 6 Slice 5: image/png|jpeg sibling inside the rich block
    LayoutElement _imageLE;     // image's per-child LayoutElement (drives Content height when active)
    Texture2D _tex;             // #95 Phase 6 Slice 5: the decoded image texture we own (freed on replace/clear/destroy)

    // #102 findings 0079 §6 D5: ScrollRect-based output blocks. Block holds the ScrollRect + LE;
    // Viewport masks; Content (with VLG + CSF) sizes to the active child's preferredHeight.  The
    // view caps Block.LE.preferredHeight at body*cap so overflow becomes user-scrollable.
    RectTransform _richBlock;
    RectTransform _richViewport;
    RectTransform _richContent;
    LayoutElement _richLE;
    ScrollRect _richScroll;
    RectTransform _consoleBlock;
    RectTransform _consoleViewport;
    RectTransform _consoleContent;
    TMP_Text _consoleText;      // #118: console stdout/stderr (TMP_Text/SDF)
    LayoutElement _consoleLE;
    ScrollRect _consoleScroll;
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
        TMP_InputField input, PythonSyntaxMeshEffect effect, EditHistory history,
        Cell cell, TMP_Text placeholder,
        TMP_Text output, RawImage image, LayoutElement imageLE,
        RectTransform richBlock, RectTransform richViewport, RectTransform richContent,
        LayoutElement richLE, ScrollRect richScroll,
        RectTransform consoleBlock, RectTransform consoleViewport, RectTransform consoleContent,
        TMP_Text consoleText, LayoutElement consoleLE, ScrollRect consoleScroll,
        LayoutElement editorLE)
    {
        _input = input;
        _effect = effect;
        _history = history;
        _placeholder = placeholder;
        _output = output;
        _image = image;
        _imageLE = imageLE;
        _richBlock = richBlock;
        _richViewport = richViewport;
        _richContent = richContent;
        _richLE = richLE;
        _richScroll = richScroll;
        _consoleBlock = consoleBlock;
        _consoleViewport = consoleViewport;
        _consoleContent = consoleContent;
        _consoleText = consoleText;
        _consoleLE = consoleLE;
        _consoleScroll = consoleScroll;
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
    //   * text/markdown | text/html → the TMP_Text with richText=true, converting the HTML/
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
            // Push texture height into the image's per-child LayoutElement so the rich Content
            // (VLG+CSF) sizes to the image rather than to the now-inactive Text.
            if (_imageLE != null && _image != null && _image.texture != null)
                _imageLE.preferredHeight = _image.texture.height;
            if (_richBlock != null) _richBlock.gameObject.SetActive(true);
            ApplyBlockSize(_richLE, _richContent);
            return;
        }

        if (mt == "text/markdown" || mt == "text/html")
        {
            FreeTexture();
            if (_image != null) _image.gameObject.SetActive(false);
            if (_output != null)
            {
                _output.richText = true;
                _output.text = RichToUnity(mt, string.IsNullOrEmpty(data) ? text : data);
                _output.gameObject.SetActive(true);
            }
            if (_richBlock != null) _richBlock.gameObject.SetActive(true);
            ApplyBlockSize(_richLE, _richContent);
            return;
        }

        // text/plain, no mimetype, or an unsupported type → plain fallback (debug-label the unknown).
        FreeTexture();
        if (_image != null) _image.gameObject.SetActive(false);
        if (_output != null)
        {
            _output.richText = false;   // plain: TMP renders everything literally (no tag parse) — no escaping needed
            bool unknown = !string.IsNullOrEmpty(mt) && mt != "text/plain";
            string body = text ?? string.Empty;
            _output.text = unknown ? "[" + mt + "]\n" + body : body;
            _output.gameObject.SetActive(true);
        }
        if (_richBlock != null) _richBlock.gameObject.SetActive(true);
        ApplyBlockSize(_richLE, _richContent);
    }

    // #102 Slice 2 (findings 0079): render the per-cell stdout/stderr segment list into the console
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
            _consoleText.richText = true;
            _consoleText.text = BuildConsoleRichText(segments);
        }
        if (_consoleBlock != null) _consoleBlock.gameObject.SetActive(true);
        ApplyBlockSize(_consoleLE, _consoleContent);
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
    // #118: with TMP `richText=true`, a raw ``<`` from the user's stdout would still be parsed as a tag
    // (``print("<color=red>")`` would recolour the buffer; ``print("<EOF>")`` shows nothing for an
    // unknown tag), so each segment's payload is wrapped in TMP's ``<noparse>…</noparse>`` so NOTHING
    // inside is interpreted — only OUR amber ``<color>`` (placed OUTSIDE the noparse) survives.  TMP
    // does NOT decode HTML entities, so the UGUI ``&lt;`` trick would have painted a literal ``&lt;`` on
    // screen (a regression); ``<noparse>`` keeps the user's ``<`` visible AND inert (findings 0096 D3).
    static string BuildConsoleRichText(ConsoleSegment[] segments)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < segments.Length; i++)
        {
            var s = segments[i];
            string body = s.Text ?? string.Empty;
            if (body.Length == 0) continue;
            string safe = NoParse(body);
            if (s.Stream == "stderr") { sb.Append("<color=#ffa01c>"); sb.Append(safe); sb.Append("</color>"); }
            else { sb.Append(safe); }
        }
        return sb.ToString();
    }

    // Wrap user-supplied text so a TMP_Text with richText=true interprets NONE of it as a tag.  TMP's
    // ``<noparse>`` disables all tag parsing for its span (color/b/i/size/sprite/link/…), so the user's
    // ``<``/``>`` render literally and inertly.  ``&`` is NOT escaped: TMP never decodes ``&amp;`` either,
    // so a Replace would paint user's ``print("a & b")`` as ``a &amp; b`` — a regression (findings 0079
    // D6, carried to TMP).  A literal ``</noparse>`` in the payload would close the guard early, so a
    // zero-width space is inserted into that token to neutralise it without altering the visible glyphs.
    const string Zwsp = "​";   // zero-width space — invisible, breaks a literal closing token
    static readonly Regex _noparseClose = new Regex("</noparse>", RegexOptions.IgnoreCase);
    static string NoParse(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        // The closing token is essentially never present in real stdout — skip the regex on the common path.
        if (s.IndexOf("</noparse>", StringComparison.OrdinalIgnoreCase) >= 0)
            s = _noparseClose.Replace(s, "</" + Zwsp + "noparse>");
        return "<noparse>" + s + "</noparse>";
    }

    // Set the block's LayoutElement.preferredHeight = clamp(Content.preferredHeight, body * cap).
    // The ScrollRect's Content (VLG+CSF) reports its preferred height via LayoutUtility — that is
    // the natural height of whichever active child (Text or RawImage) is driving it.  When the
    // natural height is below the cap the block sizes to it and no scroll appears; when it exceeds
    // the cap, the block is pinned at the cap and the ScrollRect makes the residual scrollable.
    //
    // bodyH == 0 → layout not yet resolved (the window's first frame); skip the cap so the first
    // paint is visible at natural size and the ScrollRect's RectMask2D still clips overflow until
    // the next SetOutput re-runs the cap math against a real bodyH.
    void ApplyBlockSize(LayoutElement le, RectTransform content)
    {
        if (le == null || _body == null) return;
        // Force the Content's VerticalLayoutGroup + ContentSizeFitter to settle so we read a
        // current preferred height (a fresh paint changes Text.text right before this call).
        if (content != null) LayoutRebuilder.ForceRebuildLayoutImmediate(content);
        float natural = (content != null ? LayoutUtility.GetPreferredHeight(content) : 0f) + 4f;
        float bodyH = _body.rect.height;
        float clamped = bodyH > 0f ? Mathf.Min(natural, bodyH * StrategyEditorContentBuilder.OutputBlockMaxFractionOfBody)
                                   : natural;
        le.preferredHeight = Mathf.Max(0f, clamped);
        if (_editorLE != null && bodyH > 0f) _editorLE.minHeight = ComputeEditorMin(bodyH);
        LayoutRebuilder.MarkLayoutForRebuild(_body);
    }

    static float ComputeEditorMin(float bodyH)
        => Mathf.Max(StrategyEditorContentBuilder.EditorMinFloorPx,
                     bodyH * StrategyEditorContentBuilder.EditorMinFractionOfBody);

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
            // #149 (findings 0121): the caret uses an explicit opaque colour (customCaretColor=true, set by
            // the builder) so it no longer auto-follows textComponent.color — re-seed it from the theme here
            // on every theme switch so the caret stays themed and opaque.
            var caretCol = c.text; caretCol.a = 1f; _input.caretColor = caretCol;
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

    // Convert a text/markdown or text/html payload into the TMP rich-text SUBSET (<b>/<i> — both TMP-native).
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

    // Strips every tag EXCEPT the Unity-native <b>/<i> emphasis HtmlToUnity just produced from
    // <strong>/<em> — a blanket `<[^>]+>` would delete those too, making the emphasis conversion dead
    // (the bug #165 hit: real marimo markdown is rendered HTML, so it takes the HTML leg and lost its
    // bold; synthetic raw-markdown took the MarkdownToUnity leg which has no final strip and kept <b>).
    static readonly Regex _tagRe = new Regex(@"<(?!/?[bi]>)[^>]+>", RegexOptions.IgnoreCase);

    static string HtmlToUnity(string html)
    {
        string s = html;
        // table structure → pipe rows BEFORE the generic tag strip (so cell boundaries survive).
        s = Regex.Replace(s, @"</tr\s*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</t[dh]\s*>", " | ", RegexOptions.IgnoreCase);
        // block breaks → newlines, list items → bullets.
        s = Regex.Replace(s, @"<br\s*/?>|</p>|</div>|</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<li[^>]*>", "• ", RegexOptions.IgnoreCase);
        // inline emphasis → Unity tags. These MUST stay attribute-free bare <b>/<i>: _tagRe's lookahead
        // below preserves exactly those forms, so emitting `<b style=…>` here would let the blanket strip
        // delete the emphasis again (the #165 regression).
        s = Regex.Replace(s, @"<(b|strong)>", "<b>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(b|strong)>", "</b>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"<(i|em)>", "<i>", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"</(i|em)>", "</i>", RegexOptions.IgnoreCase);
        s = _tagRe.Replace(s, string.Empty);   // drop remaining tags but KEEP the converted <b>/<i> (general HTML not reproduced)
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
