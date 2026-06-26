// StrategyEditorContentBuilder.cs — issue #16 "Strategy Editor" / #81 cell-as-floating-window
//                                  + #95 Phase 6 (rich output) / #102 (console + dynamic layout)
//
// Builds the editable code-buffer subtree INTO a floating-window body and wires it to the cores.
// The FloatingWindowController owns no content factory (findings 0008 §6) — the caller's window
// factory, when it sees kind == "strategy_editor", calls Build(body, ...) to populate the body.
// The controller boundary is untouched.
//
// Since #81 (ADR-0013) the produced StrategyEditorView is a FRAGMENT VIEW over a Cell, NOT a file:
// no document, no provider registration (the notebook aggregate is the sole IStrategyFileProvider,
// registered by the coordinator under the logical notebook id). The view binds to `cell` (or stays
// unbound until the coordinator Bind()s one). A placeholder Text carries the single-cell host-API
// hint (shown by uGUI only while the field is empty; the coordinator toggles it per cell count).
//
// #102 / findings 0079 — body layout is DYNAMIC: the body holds three children vertically in a
// VerticalLayoutGroup — code editor (top, flex + min-height floor) + rich output (middle, auto)
// + console output (bottom, auto).  Empty rich/console blocks deactivate so the LayoutGroup
// skips them (no GameObject, no gap) — an un-run cell shows nothing but the editor at full body
// height.  Each output block caps its preferredHeight at ~45% of the body so the editor is never
// crushed below ~10%.  Overflow is solved by a REAL ScrollRect (findings 0079 §6 D5 / AC「ブロック
// 内スクロール」), NOT a clip: each block is a ScrollRect with Viewport + Content (the Text and
// optional RawImage live INSIDE Content, which a ContentSizeFitter + VerticalLayoutGroup sizes to
// the active child's preferredHeight).  When Content > Viewport (the cap), the user scrolls the
// block internally — the window itself never grows (which would void the per-window layout
// persistence schema established by findings 0075).  stderr segments paint amber via UGUI
// rich-text colour tags (marimo `Outputs.css .stderr { color: var(--amber-12); }` parity).

using TMPro;
using UnityEngine;
using UnityEngine.UI;

public static class StrategyEditorContentBuilder
{
    // Layout constants (findings 0076 D2). The editor's min governs against window shrink; the
    // per-block max governs against an output greedily eating the editor; the spacing keeps two
    // active output blocks separable without showing a divider line.
    const float EditorMinFloor = 80f;             // absolute floor — the editor never falls below
    const float EditorMinFrac = 0.30f;            // editor min = max(EditorMinFloor, body * frac)
    const float OutputMaxPerBlockFrac = 0.45f;    // per-block ceiling — leaves >=10% for the editor
    const float BlockSpacingPx = 4f;              // gap between active blocks (uniform UI scale)
    const float ScrollbarWidthPx = 6f;            // narrow vertical scrollbar gutter (findings 0079 §6 D5)

    public static StrategyEditorView Build(
        RectTransform body,
        EditHistory history = null, Cell cell = null)
    {
        if (body == null) return null;
        history ??= new EditHistory();
        // #119: the editor + placeholder now render through EditorTmpFont() (Cascadia SDF); the legacy
        // `Font font` parameter (and BuiltinFont fallback) is gone — the surface is TMP/SDF end-to-end.

        // VerticalLayoutGroup on the body: children stack top→bottom, child-controlled height + width
        // so each LayoutElement's preferredHeight (set by SetOutput/SetConsole) is honoured.  Spacing
        // is applied between ACTIVE children only — a deactivated output block contributes no gap.
        var layout = body.gameObject.GetComponent<VerticalLayoutGroup>();
        if (layout == null) layout = body.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.spacing = BlockSpacingPx;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlHeight = true;
        layout.childControlWidth = true;
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.padding = new RectOffset(0, 0, 0, 0);

        // ---- 1) Editor block (StrategyCodeInput) — top, min-height + flex.  #119 (findings 0096 D5):
        // the editing surface is TMP_InputField + TMP_Text/SDF so the InfiniteCanvas zoom (0.2–5×)
        // stays crisp — the shader reconstructs glyph outlines instead of stretching the dynamic-font
        // atlas bitmap the legacy uGUI Text did (the migration's root cause). ----

        // #149 (findings 0121): build the editor subtree INACTIVE and add StrategyInputField while inactive
        // so its FIRST (and only) OnEnable fires after textComponent is wired (below). TMP_InputField creates
        // its caret CanvasRenderer in OnEnable ONLY when textComponent != null (TMP_InputField.cs:1172); if
        // the field were added active via the GameObject constructor, OnEnable would run with a null
        // textComponent and the caret renderer would never be created -> invisible caret at every zoom
        // (owner HITL 2026-06-26). StrategyInputField is therefore NOT in the constructor type list.
        var inputGo = new GameObject("StrategyCodeInput",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(LayoutElement));
        var inputRt = (RectTransform)inputGo.transform;
        inputRt.SetParent(body, false);
        inputGo.SetActive(false);   // wire textComponent before the field's first enable (see SetActive(true) below)
        inputGo.GetComponent<Image>().color = ThemeService.Current.colors.background;
        var inputLE = inputGo.GetComponent<LayoutElement>();
        inputLE.flexibleHeight = 1f;            // absorbs all space the output blocks do not claim
        inputLE.minHeight = EditorMinFloor;     // updated by _Relayout when the body's height changes

        // TextArea viewport (RectMask2D): TMP_InputField scrolls the text WITHIN this masked area by
        // offsetting the (full) text component rather than truncating it to the visible line window the
        // way the legacy InputField did — so characterInfo[i].index stays the full-source index and the
        // syntax recolour needs no displayStart offset (findings 0096 §#120 refinement).
        var areaGo = new GameObject("TextArea", typeof(RectTransform), typeof(RectMask2D));
        var areaRt = (RectTransform)areaGo.transform;
        areaRt.SetParent(inputRt, false);
        Stretch(areaRt);

        // Text component (the editing surface) + the per-glyph syntax recolour driver. TMP_Text/SDF.
        var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI), typeof(PythonSyntaxMeshEffect));
        var textRt = (RectTransform)textGo.transform;
        textRt.SetParent(areaRt, false);
        Stretch(textRt);
        var text = textGo.GetComponent<TextMeshProUGUI>();
        text.font = EditorTmpFont();                        // Cascadia Mono SDF (#117), default-fallback
        text.fontSize = 14;
        text.color = ThemeService.Current.colors.text;      // base (Default) colour — issue #44
        text.richText = false;                              // colouring is per-vertex, NOT tags
        text.alignment = TextAlignmentOptions.TopLeft;
        text.textWrappingMode = TextWrappingModes.NoWrap;   // code editor: no wrap (parity w/ legacy Overflow)
        text.overflowMode = TextOverflowModes.Overflow;     // the TextArea RectMask2D clips overflow

        // Placeholder (#81): the single-cell host-API hint. TMP_InputField shows the placeholder Graphic
        // only while the field is empty; the coordinator sets the hint text for the only cell and clears
        // it otherwise (marimo showPlaceholder = hasOnlyOneCell). Hidden by default.
        var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        var phRt = (RectTransform)phGo.transform;
        phRt.SetParent(areaRt, false);
        Stretch(phRt);
        var placeholder = phGo.GetComponent<TextMeshProUGUI>();
        placeholder.font = EditorTmpFont();
        placeholder.fontSize = 14;
        placeholder.richText = false;   // host-API hint is literal text; never parse '<...>' as a TMP tag (parity w/ editor)
        placeholder.alignment = TextAlignmentOptions.TopLeft;
        placeholder.textWrappingMode = TextWrappingModes.NoWrap;
        placeholder.overflowMode = TextOverflowModes.Overflow;
        var phColor = ThemeService.Current.colors.text; phColor.a = 0.4f;
        placeholder.color = phColor;
        phGo.SetActive(false);

        var effect = textGo.GetComponent<PythonSyntaxMeshEffect>();

        var input = inputGo.AddComponent<StrategyInputField>();   // added while inactive -> no premature OnEnable
        input.textViewport = areaRt;
        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = TMP_InputField.LineType.MultiLineNewline;
        input.characterLimit = 0;
        input.richText = false;   // user code may contain '<...>'; never parse it as a TMP tag

        // #149 (findings 0121): explicit caret config so the caret survives the InfiniteCanvas zoom and never
        // silently tracks a dimmed text colour. A wider caret (TMP's default caretWidth=1 is sub-pixel at
        // MIN_ZOOM 0.2x) and customCaretColor=true so the caret no longer auto-follows textComponent.color.
        // The opaque caret COLOUR is owned by StrategyEditorView.ApplyTheme — called once from Initialize
        // (below, before this Build returns) and again on every theme switch — so seeding caretColor here too
        // would just be overwritten by that same-frame call. Width + the customCaretColor flag are NOT touched
        // by ApplyTheme, so they stay here.
        input.caretWidth = CaretWidthPx;
        input.customCaretColor = true;

        // #149 ROOT-CAUSE FIX: activate now that textComponent is wired, so the field's first (and only)
        // OnEnable runs with it present -> TMP creates its caret CanvasRenderer (TMP_InputField.cs:1172).
        // Headless -batchmode leaves the Application.isPlaying-gated renderer uncreated, which is expected —
        // the AFK gate (STRATEGY-62) pins the precondition (OnEnable saw textComponent), the real caret is HITL.
        inputGo.SetActive(true);

        // ---- 2) Rich output block — middle, ScrollRect (findings 0079 §6 D5) ----

        var rich = BuildOutputBlock(body, "RichOutputBlock", fontSize: 12, richText: false);
        // Rich Content hosts Text (from BuildOutputBlock) + a RawImage SIBLING for image/png|jpeg.
        // Both children live INSIDE the ScrollRect's Content so the VerticalLayoutGroup +
        // ContentSizeFitter on Content size it from whichever child is active (the other is
        // SetActive(false) and excluded from VLG/CSF measurement).
        var imgGo = new GameObject("CellOutputImage",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage), typeof(LayoutElement));
        var imgRt = (RectTransform)imgGo.transform;
        imgRt.SetParent(rich.Content, false);
        Stretch(imgRt);
        var image = imgGo.GetComponent<RawImage>();
        image.raycastTarget = false;
        var imgLE = imgGo.GetComponent<LayoutElement>();
        imgLE.preferredHeight = 0f;              // raised by SetOutput when an image paints (= texture.height)
        imgGo.SetActive(false);                  // hidden until SetOutput paints an image
        rich.Block.gameObject.SetActive(false);  // hidden until a press produces ANY rich output

        // ---- 3) Console output block — bottom, ScrollRect ----

        var console = BuildOutputBlock(body, "ConsoleOutputBlock", fontSize: 12, richText: true);
        console.Block.gameObject.SetActive(false);  // hidden until a press emits stdout/stderr

        var view = inputGo.AddComponent<StrategyEditorView>();
        view.Initialize(
            input, effect, history, cell, placeholder,
            rich.Text, image, imgLE,
            rich.Block, rich.Viewport, rich.Content, rich.LayoutElement, rich.ScrollRect,
            console.Block, console.Viewport, console.Content, console.Text, console.LayoutElement, console.ScrollRect,
            inputLE);
        return view;
    }

    // Build one collapsible output block (rich or console) as a REAL ScrollRect (findings 0079 §6
    // D5 — supersedes the prior RectMask2D-clip implementation which violated AC「ブロック内スクロール」).
    //
    //   [Block]   RectTransform, transparent Image, LayoutElement (the view caps this at body*0.45),
    //             ScrollRect (vertical-only, AutoHide bar)
    //     ├── [Viewport]   RectTransform stretched to Block, Image (mask graphic), RectMask2D
    //     │     └── [Content]   top-anchored RectTransform, VerticalLayoutGroup + ContentSizeFitter
    //     │           (vertical=PreferredSize) — height tracks the active child's preferredHeight
    //     │           └── Text  (Stretch横; preferredHeight drives Content via VLG)
    //     │           └── (RawImage sibling added by Build() for the rich block)
    //     └── [VerticalScrollbar]  ScrollRect.verticalScrollbar — AutoHide hides it when Content
    //           fits inside Viewport (the cap), shows it when Content overflows.
    //
    // The view's ApplyBlockSize sets Block.LayoutElement.preferredHeight = min(Content.preferredHeight,
    // body*cap). When Content <= cap, Block sized to Content and no scroll is needed; when Content
    // > cap, Block sized to cap and the ScrollRect makes the residual scrollable.
    static OutputBlock BuildOutputBlock(RectTransform body, string name, int fontSize, bool richText)
    {
        // ---- Block: ScrollRect host ----
        var blockGo = new GameObject(name,
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
            typeof(LayoutElement), typeof(ScrollRect));
        var blockRt = (RectTransform)blockGo.transform;
        blockRt.SetParent(body, false);
        var bg = blockGo.GetComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0f);   // transparent — the window already provides the background
        bg.raycastTarget = false;
        var blockLE = blockGo.GetComponent<LayoutElement>();
        blockLE.flexibleHeight = 0f;
        blockLE.preferredHeight = 0f;            // raised by SetOutput/SetConsole when populated
        var scroll = blockGo.GetComponent<ScrollRect>();
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 14f;
        scroll.verticalScrollbarVisibility = ScrollRect.ScrollbarVisibility.AutoHide;

        // ---- Viewport: stretched inside Block, masked ----
        var viewportGo = new GameObject("Viewport",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(RectMask2D));
        var viewportRt = (RectTransform)viewportGo.transform;
        viewportRt.SetParent(blockRt, false);
        Stretch(viewportRt);
        viewportRt.offsetMin = new Vector2(2f, 2f);
        viewportRt.offsetMax = new Vector2(-(2f + ScrollbarWidthPx), -2f);   // leave room for the scrollbar gutter
        var vbg = viewportGo.GetComponent<Image>();
        vbg.color = new Color(0f, 0f, 0f, 0f);   // transparent mask graphic
        vbg.raycastTarget = false;

        // ---- Content: top-anchored, sized by VerticalLayoutGroup + ContentSizeFitter ----
        var contentGo = new GameObject("Content",
            typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
        var contentRt = (RectTransform)contentGo.transform;
        contentRt.SetParent(viewportRt, false);
        // anchor top: x-stretches with viewport, y stays at top so vertical growth is downward
        contentRt.anchorMin = new Vector2(0f, 1f);
        contentRt.anchorMax = new Vector2(1f, 1f);
        contentRt.pivot = new Vector2(0.5f, 1f);
        contentRt.anchoredPosition = Vector2.zero;
        contentRt.sizeDelta = new Vector2(0f, 0f);
        var vlg = contentGo.GetComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(0, 0, 0, 0);
        vlg.spacing = 0f;
        vlg.childAlignment = TextAnchor.UpperLeft;
        vlg.childControlHeight = true;
        vlg.childControlWidth = true;
        vlg.childForceExpandHeight = false;
        vlg.childForceExpandWidth = true;
        var csf = contentGo.GetComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.horizontalFit = ContentSizeFitter.FitMode.Unconstrained;

        // ---- Text: child of Content ----  (#118: TMP_Text / SDF so the output pane stays crisp across
        // the whole zoom range — the shader reconstructs glyph outlines instead of stretching an atlas
        // bitmap.  TMP implements ILayoutElement so the VLG + ContentSizeFitter on Content size to its
        // preferredHeight exactly as the legacy Text did.)
        var textGo = new GameObject(name + "Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(TextMeshProUGUI));
        var textRt = (RectTransform)textGo.transform;
        textRt.SetParent(contentRt, false);
        Stretch(textRt);
        var text = textGo.GetComponent<TextMeshProUGUI>();
        text.font = EditorTmpFont();
        text.fontSize = fontSize;
        var col = ThemeService.Current.colors.text; col.a = 0.85f;
        text.color = col;
        text.alignment = TextAlignmentOptions.TopLeft;
        text.textWrappingMode = TextWrappingModes.Normal;        // wrap (parity with HorizontalWrapMode.Wrap)
        text.overflowMode = TextOverflowModes.Overflow;          // vertical overflow (Content/ScrollRect clips)
        text.richText = richText;
        text.raycastTarget = false;

        // ---- Vertical Scrollbar: gutter on the right edge of Block ----
        var sbGo = new GameObject("VerticalScrollbar",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(Scrollbar));
        var sbRt = (RectTransform)sbGo.transform;
        sbRt.SetParent(blockRt, false);
        sbRt.anchorMin = new Vector2(1f, 0f);
        sbRt.anchorMax = new Vector2(1f, 1f);
        sbRt.pivot = new Vector2(1f, 0.5f);
        sbRt.sizeDelta = new Vector2(ScrollbarWidthPx, -4f);
        sbRt.anchoredPosition = new Vector2(-2f, 0f);
        var sbBg = sbGo.GetComponent<Image>();
        sbBg.color = new Color(1f, 1f, 1f, 0.06f);
        sbBg.raycastTarget = true;
        var scrollbar = sbGo.GetComponent<Scrollbar>();
        scrollbar.direction = Scrollbar.Direction.BottomToTop;
        // Sliding area + handle (UGUI Scrollbar needs an explicit handleRect)
        var slidingGo = new GameObject("SlidingArea", typeof(RectTransform));
        var slidingRt = (RectTransform)slidingGo.transform;
        slidingRt.SetParent(sbRt, false);
        Stretch(slidingRt);
        slidingRt.offsetMin = new Vector2(0f, 0f);
        slidingRt.offsetMax = new Vector2(0f, 0f);
        var handleGo = new GameObject("Handle", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
        var handleRt = (RectTransform)handleGo.transform;
        handleRt.SetParent(slidingRt, false);
        Stretch(handleRt);
        var handleImg = handleGo.GetComponent<Image>();
        handleImg.color = new Color(1f, 1f, 1f, 0.32f);
        handleImg.raycastTarget = true;
        scrollbar.handleRect = handleRt;
        scrollbar.targetGraphic = handleImg;

        // Wire ScrollRect.
        scroll.content = contentRt;
        scroll.viewport = viewportRt;
        scroll.verticalScrollbar = scrollbar;

        return new OutputBlock
        {
            Block = blockRt,
            Viewport = viewportRt,
            Content = contentRt,
            Text = text,
            LayoutElement = blockLE,
            ScrollRect = scroll,
        };
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    // #117/#118: the editor's SDF font asset (Cascadia Mono, OFL — committed by TmpFoundationSetup).
    // Loaded from Resources so a runtime build and the AFK probes resolve the same asset.  Falls back
    // to the TMP project default (LiberationSans SDF) if the Cascadia asset is somehow absent, so a
    // missing-foundation project still renders text instead of throwing.
    public const string EditorSdfFontResourcesPath = "Fonts & Materials/CascadiaMono SDF";

    // Cached so a multi-cell restore (ApplyLayout / File→Open spawns K cell windows) does one resource
    // lookup for the whole process, not 2×K (two output blocks per Build).  The asset never changes at
    // runtime; the field harmlessly pins it against UnloadUnusedAssets.
    static TMP_FontAsset _editorTmpFont;
    static TMP_FontAsset EditorTmpFont()
    {
        if (_editorTmpFont != null) return _editorTmpFont;
        var fa = Resources.Load<TMP_FontAsset>(EditorSdfFontResourcesPath);
        return _editorTmpFont = (fa != null ? fa : (TMP_Settings.instance != null ? TMP_Settings.defaultFontAsset : null));
    }

    // The pieces of an output block the StrategyEditorView holds to drive its dynamic layout.
    // Post-D5 the block is a ScrollRect — the view caps LayoutElement.preferredHeight at body*cap,
    // and the ScrollRect handles overflow via Viewport + Content (the Text lives in Content).
    public struct OutputBlock
    {
        public RectTransform Block;
        public RectTransform Viewport;
        public RectTransform Content;
        public TMP_Text Text;
        public LayoutElement LayoutElement;
        public ScrollRect ScrollRect;
    }

    // #149 (findings 0121): explicit caret width for the code editor. The solid caret quad scales with the
    // InfiniteCanvas zoom (Content.localScale), so the TMP default caretWidth=1 is sub-pixel — invisible —
    // at MIN_ZOOM (0.2x). This value keeps the caret visible across the 0.2-5x zoom range (HITL-tuned; the
    // AFK gate STRATEGY-62 only asserts it is > the invisible default). > 1 by contract.
    public const int CaretWidthPx = 6;

    // Fractional constants exposed for the AFK gate so its layout assertions are not magic numbers
    // (S20 reads them to compute "editor min" and "per-block max" against the body rect).
    public static float EditorMinFloorPx => EditorMinFloor;
    public static float EditorMinFractionOfBody => EditorMinFrac;
    public static float OutputBlockMaxFractionOfBody => OutputMaxPerBlockFrac;
}
