// StrategyEditorContentBuilder.cs — issue #16 "Strategy Editor" (DURABLE tier, Unity boundary)
//
// Builds the editable code-buffer subtree INTO a floating-window body and wires it to the cores.
// This is the integration point #15 deliberately left open: the FloatingWindowController owns no
// content factory (findings 0008 §6) — instead the CALLER's window factory, when it sees
// kind == "strategy_editor", calls Build(body, ...) to populate the body (findings 0010 §6). The
// controller boundary is untouched.
//
// Produces a MULTILINE legacy InputField whose text component carries the PythonSyntaxMeshEffect
// (full text in the Text mesh -> displayStart 0; no reflection, findings 0010 §1/§8), registers
// the document as the IStrategyFileProvider under the window id, and returns the StrategyEditorView.

using UnityEngine;
using UnityEngine.UI;

public static class StrategyEditorContentBuilder
{
    public static StrategyEditorView Build(
        RectTransform body, string windowId,
        StrategyProviderRegistry registry,
        StrategyDocument document = null, EditHistory history = null, Font font = null)
    {
        if (body == null) return null;
        document ??= new StrategyDocument();
        history ??= new EditHistory();
        font ??= BuiltinFont();

        // InputField host (fills the body). StrategyInputField exposes the visible draw-window
        // start so the mesh effect can offset token spans when a focused multiline field scrolls.
        var inputGo = new GameObject("StrategyCodeInput", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image), typeof(StrategyInputField));
        var inputRt = (RectTransform)inputGo.transform;
        inputRt.SetParent(body, false);
        Stretch(inputRt);
        inputGo.GetComponent<Image>().color = new Color(0.10f, 0.11f, 0.13f, 1f);

        // Text component (the editing surface) + the syntax mesh effect.
        var textGo = new GameObject("Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text), typeof(PythonSyntaxMeshEffect));
        var textRt = (RectTransform)textGo.transform;
        textRt.SetParent(inputRt, false);
        Stretch(textRt);
        var text = textGo.GetComponent<Text>();
        text.font = font;
        text.fontSize = 14;
        text.color = new Color(0.86f, 0.87f, 0.89f, 1f);   // base (Default) colour
        text.supportRichText = false;                       // colouring is per-vertex, NOT tags
        text.alignment = TextAnchor.UpperLeft;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        var effect = textGo.GetComponent<PythonSyntaxMeshEffect>();

        var input = inputGo.GetComponent<StrategyInputField>();
        input.textComponent = text;
        input.lineType = InputField.LineType.MultiLineNewline;
        input.characterLimit = 0;

        // Live display offset: when a focused multiline field scrolls, the Text shows only the
        // visible line window starting at VisibleDrawStart; the effect reads it at mesh-build time.
        effect.SetDisplayStartProvider(() => input.VisibleDrawStart);

        var view = inputGo.AddComponent<StrategyEditorView>();
        view.Initialize(input, effect, document, history, registry, windowId);
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
