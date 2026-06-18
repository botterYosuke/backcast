// StrategyEditorContentBuilder.cs — issue #16 "Strategy Editor" / #81 cell-as-floating-window
//
// Builds the editable code-buffer subtree INTO a floating-window body and wires it to the cores.
// This is the integration point #15 left open: the FloatingWindowController owns no content factory
// (findings 0008 §6) — the caller's window factory, when it sees kind == "strategy_editor", calls
// Build(body, ...) to populate the body. The controller boundary is untouched.
//
// Since #81 (ADR-0013) the produced StrategyEditorView is a FRAGMENT VIEW over a Cell, NOT a file:
// no document, no provider registration (the notebook aggregate is the sole IStrategyFileProvider,
// registered by the coordinator under the logical notebook id). The view binds to `cell` (or stays
// unbound until the coordinator Bind()s one). A placeholder Text carries the single-cell host-API
// hint (shown by uGUI only while the field is empty; the coordinator toggles it per cell count).

using UnityEngine;
using UnityEngine.UI;

public static class StrategyEditorContentBuilder
{
    public static StrategyEditorView Build(
        RectTransform body,
        EditHistory history = null, Font font = null, Cell cell = null)
    {
        if (body == null) return null;
        history ??= new EditHistory();
        font ??= BuiltinFont();

        // InputField host (fills the body). StrategyInputField exposes the visible draw-window start
        // so the mesh effect can offset token spans when a focused multiline field scrolls.
        var inputGo = new GameObject("StrategyCodeInput", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(StrategyInputField));
        var inputRt = (RectTransform)inputGo.transform;
        inputRt.SetParent(body, false);
        Stretch(inputRt);
        inputGo.GetComponent<Image>().color = ThemeService.Current.colors.background; // issue #44

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

        var view = inputGo.AddComponent<StrategyEditorView>();
        view.Initialize(input, effect, history, cell, placeholder);
        return view;
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
}
