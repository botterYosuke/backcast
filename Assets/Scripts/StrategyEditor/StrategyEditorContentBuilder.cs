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
// #102 / findings 0076 — body layout is DYNAMIC: the body holds three children vertically in a
// VerticalLayoutGroup — code editor (top, flex + min-height floor) + rich output (middle, auto)
// + console output (bottom, auto).  Empty rich/console blocks deactivate so the LayoutGroup
// skips them (no GameObject, no gap) — an un-run cell shows nothing but the editor at full body
// height.  Each output block caps its preferredHeight at ~45% of the body so the editor is never
// crushed below ~10%; RectMask2D on each block clips text/image overflow inside the cap so the
// window itself never grows (touching window size would void the per-window layout persistence
// schema established by findings 0075).  stderr segments paint amber via UGUI rich-text colour
// tags (marimo `Outputs.css .stderr { color: var(--amber-12); }` parity).

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

    public static StrategyEditorView Build(
        RectTransform body,
        EditHistory history = null, Font font = null, Cell cell = null)
    {
        if (body == null) return null;
        history ??= new EditHistory();
        font ??= BuiltinFont();

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

        // ---- 1) Editor block (StrategyCodeInput) — top, min-height + flex ----

        var inputGo = new GameObject("StrategyCodeInput",
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(StrategyInputField), typeof(LayoutElement));
        var inputRt = (RectTransform)inputGo.transform;
        inputRt.SetParent(body, false);
        inputGo.GetComponent<Image>().color = ThemeService.Current.colors.background;
        var inputLE = inputGo.GetComponent<LayoutElement>();
        inputLE.flexibleHeight = 1f;            // absorbs all space the output blocks do not claim
        inputLE.minHeight = EditorMinFloor;     // updated by _Relayout when the body's height changes

        // Text component (the editing surface) + the syntax mesh effect.
        var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(PythonSyntaxMeshEffect));
        var textRt = (RectTransform)textGo.transform;
        textRt.SetParent(inputRt, false);
        Stretch(textRt);
        var text = textGo.GetComponent<Text>();
        text.font = font;
        text.fontSize = 14;
        text.color = ThemeService.Current.colors.text;     // base (Default) colour — issue #44
        text.supportRichText = false;                       // colouring is per-vertex, NOT tags
        text.alignment = TextAnchor.UpperLeft;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        // Placeholder (#81): the single-cell host-API hint. uGUI shows the placeholder Graphic only
        // while the field is empty; the coordinator sets the hint text for the only cell and clears it
        // otherwise (marimo showPlaceholder = hasOnlyOneCell). Hidden by default.
        var phGo = new GameObject("Placeholder", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        var phRt = (RectTransform)phGo.transform;
        phRt.SetParent(inputRt, false);
        Stretch(phRt);
        var placeholder = phGo.GetComponent<Text>();
        placeholder.font = font;
        placeholder.fontSize = 14;
        placeholder.alignment = TextAnchor.UpperLeft;
        placeholder.horizontalOverflow = HorizontalWrapMode.Overflow;
        placeholder.verticalOverflow = VerticalWrapMode.Overflow;
        var phColor = ThemeService.Current.colors.text; phColor.a = 0.4f;
        placeholder.color = phColor;
        phGo.SetActive(false);

        var effect = textGo.GetComponent<PythonSyntaxMeshEffect>();

        var input = inputGo.GetComponent<StrategyInputField>();
        input.textComponent = text;
        input.placeholder = placeholder;
        input.lineType = InputField.LineType.MultiLineNewline;
        input.characterLimit = 0;

        // Live display offset: when a focused multiline field scrolls, the Text shows only the
        // visible line window starting at VisibleDrawStart; the effect reads it at mesh-build time.
        effect.SetDisplayStartProvider(() => input.VisibleDrawStart);

        // ---- 2) Rich output block — middle, auto-sized, RectMask2D clipping (findings 0076 D2) ----

        var rich = BuildOutputBlock(body, "RichOutputBlock", font, fontSize: 12, supportRichText: false);
        // Rich block hosts the Text from BuildOutputBlock + a RawImage sibling for image/png|jpeg.
        var imgGo = new GameObject("CellOutputImage", typeof(RectTransform), typeof(CanvasRenderer), typeof(RawImage));
        var imgRt = (RectTransform)imgGo.transform;
        imgRt.SetParent(rich.Block, false);
        Stretch(imgRt);
        var image = imgGo.GetComponent<RawImage>();
        image.raycastTarget = false;
        imgGo.SetActive(false);                  // hidden until SetOutput paints an image
        rich.Block.gameObject.SetActive(false);  // hidden until a press produces ANY rich output

        // ---- 3) Console output block — bottom, auto-sized, RectMask2D clipping ----

        var console = BuildOutputBlock(body, "ConsoleOutputBlock", font, fontSize: 12, supportRichText: true);
        console.Block.gameObject.SetActive(false);  // hidden until a press emits stdout/stderr

        var view = inputGo.AddComponent<StrategyEditorView>();
        view.Initialize(
            input, effect, history, cell, placeholder,
            rich.Text, image,
            rich.Block, rich.LayoutElement,
            console.Block, console.Text, console.LayoutElement,
            inputLE);
        return view;
    }

    // Build one collapsible output block (rich or console): a top-level RectTransform with a
    // RectMask2D for clipping, a LayoutElement carrying the preferredHeight (set by the view per
    // SetOutput/SetConsole), and a single child Text that fills the block.  Overflowing content
    // is hidden by the mask (the window never grows past its persisted size — findings 0075).
    // Callers add extra children (e.g. RawImage for the rich block) as siblings of Text.
    static OutputBlock BuildOutputBlock(RectTransform body, string name, Font font, int fontSize, bool supportRichText)
    {
        var blockGo = new GameObject(name,
            typeof(RectTransform), typeof(CanvasRenderer), typeof(Image),
            typeof(RectMask2D), typeof(LayoutElement));
        var blockRt = (RectTransform)blockGo.transform;
        blockRt.SetParent(body, false);
        var bg = blockGo.GetComponent<Image>();
        bg.color = new Color(0f, 0f, 0f, 0f);   // transparent — the window already provides the background
        bg.raycastTarget = false;
        var blockLE = blockGo.GetComponent<LayoutElement>();
        blockLE.flexibleHeight = 0f;
        blockLE.preferredHeight = 0f;            // raised by SetOutput/SetConsole when populated

        // Text fills the block (Stretch); horizontal Wrap so long lines do not overflow sideways,
        // vertical Overflow so the natural text height is what Text.preferredHeight reports — the
        // view reads that to set the block's preferredHeight, and RectMask2D clips any overflow.
        var textGo = new GameObject(name + "Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
        var textRt = (RectTransform)textGo.transform;
        textRt.SetParent(blockRt, false);
        Stretch(textRt);
        textRt.offsetMin = new Vector2(2f, 2f);
        textRt.offsetMax = new Vector2(-2f, -2f);
        var text = textGo.GetComponent<Text>();
        text.font = font;
        text.fontSize = fontSize;
        var col = ThemeService.Current.colors.text; col.a = 0.85f;
        text.color = col;
        text.alignment = TextAnchor.UpperLeft;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.supportRichText = supportRichText;
        text.raycastTarget = false;

        return new OutputBlock
        {
            Block = blockRt,
            Text = text,
            LayoutElement = blockLE,
        };
    }

    static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    static Font BuiltinFont()
    {
        // Unity 6 ships the builtin legacy font as "LegacyRuntime.ttf" (Arial was removed).
        var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        return f != null ? f : Resources.GetBuiltinResource<Font>("Arial.ttf");
    }

    // The pieces of an output block the StrategyEditorView holds to drive its dynamic layout.
    public struct OutputBlock
    {
        public RectTransform Block;
        public Text Text;
        public LayoutElement LayoutElement;
    }

    // Fractional constants exposed for the AFK gate so its layout assertions are not magic numbers
    // (S20 reads them to compute "editor min" and "per-block max" against the body rect).
    public static float EditorMinFloorPx => EditorMinFloor;
    public static float EditorMinFractionOfBody => EditorMinFrac;
    public static float OutputBlockMaxFractionOfBody => OutputMaxPerBlockFrac;
}
