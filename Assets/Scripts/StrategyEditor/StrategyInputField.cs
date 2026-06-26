// StrategyInputField.cs — issue #16 "Strategy Editor" (DURABLE tier, Unity boundary)
//                       + #119 TMP(SDF) editing-surface migration (findings 0096 D5)
//
// The named code-editing surface. Since #119 it derives from TMP_InputField (was UnityEngine.UI.
// InputField) so the editor renders through the TMP SDF pipeline — the shader reconstructs glyph
// outlines, so the InfiniteCanvas zoom (Content.localScale 0.2–5×) stays crisp instead of
// stretching a dynamic-font atlas bitmap (findings 0096 root cause).
//
// WHY a named subclass rather than a bare TMP_InputField: the builder/probe address the editing
// surface by THIS type (a stable seam for future editor-specific input behaviour — e.g. tab/indent
// handling), and the syntax recolour pipeline reads its textComponent's full source directly.
//
// The legacy `VisibleDrawStart`/`m_DrawStart` display-window machinery is GONE: a focused legacy
// multiline InputField truncated its text component to the visible line window [m_DrawStart,
// m_DrawEnd) and scrolled by re-substringing, so the mesh effect had to offset displayed glyphs
// back onto the full source. TMP_InputField keeps the FULL text in its textComponent and scrolls
// by firstVisibleCharacter / vertical offset (it does NOT truncate), so each glyph's
// characterInfo[i].index IS already the full-source index — the offset mechanism is unneeded
// (findings 0096 §#119/#120 refinement).

using TMPro;
using UnityEngine.EventSystems;

public class StrategyInputField : TMP_InputField
{
    // #148 (findings 0116): Enter must stay a NEWLINE in the multiline code editor.
    //
    // The production EventSystem is the new Input System's InputSystemUIInputModule
    // (BackcastWorkspaceSceneBuilder.cs:47), whose `Submit` action is bound to Enter. When this field
    // is focused and the user presses Enter, the module dispatches ISubmitHandler.OnSubmit ALONGSIDE
    // the IMGUI key pump. TMP_InputField.OnSubmit (com.unity.ugui 2.0.0, TMP_InputField.cs:4501) calls
    // DeactivateInputField() UNCONDITIONALLY — it does NOT spare MultiLineNewline fields — so Enter
    // BLURRED the editor (focus left, edit ended) instead of inserting a newline. The newline itself IS
    // produced by the key pump in OnUpdateSelected (TMP_InputField.cs:2263, the MultiLineNewline
    // branch); the only defect was the spurious deactivate.
    //
    // Fix: for MultiLineNewline we CONSUME the Submit (no deactivate) so focus is retained and the
    // pumped newline survives. Single-line fields keep the default submit/deactivate so they still
    // commit on Enter. SubmitConsumedCount is the AFK-observable seam (STRATEGY-59): the gate invokes
    // OnSubmit on a real built field and asserts it took the consume branch rather than deactivating.
    // The real keystroke→visible-newline→focus path stays HITL (STRATEGY-18): -batchmode -nographics
    // has no IMGUI key pump / EventSystem focus to drive a real Enter.
    public int SubmitConsumedCount { get; private set; }

    public override void OnSubmit(BaseEventData eventData)
    {
        if (lineType == LineType.MultiLineNewline)
        {
            SubmitConsumedCount++;
            eventData?.Use();   // handled — the Submit action must not blur the multiline code editor
            return;
        }
        base.OnSubmit(eventData);
    }

    // #148 sibling (findings 0117): Escape must NOT discard the in-progress edit in the code editor.
    //
    // Exact same Input System seam as OnSubmit above. The InputSystemUIInputModule's default `Cancel`
    // action is bound to Escape; while this field is focused the module dispatches ICancelHandler.OnCancel.
    // TMP_InputField.OnCancel (com.unity.ugui 2.0.0, TMP_InputField.cs:4505) sets m_WasCanceled=true and
    // DeactivateInputField()s — and DeactivateInputField then does `text = m_OriginalText`
    // (TMP_InputField.cs:4436, restoreOriginalTextOnEscape defaults true). So Escape REVERTED every edit
    // made since the field was focused AND blurred it: silent data loss, strictly worse than the Enter blur.
    //
    // Fix (owner decision 2026-06-26): for MultiLineNewline, Escape does NOTHING — consume it so the field
    // is neither deactivated nor reverted; the edit and focus are retained. Single-line fields keep the
    // default cancel/revert semantics (a search/name field SHOULD abandon on Escape). CancelConsumedCount
    // is the AFK-observable seam (STRATEGY-60): the gate invokes OnCancel on a real built field and asserts
    // it took the consume branch (no revert) rather than the base deactivate. The real keystroke→focus path
    // stays HITL (STRATEGY-18): -batchmode -nographics has no EventSystem focus to drive a real Escape.
    public int CancelConsumedCount { get; private set; }

    public override void OnCancel(BaseEventData eventData)
    {
        if (lineType == LineType.MultiLineNewline)
        {
            CancelConsumedCount++;
            eventData?.Use();   // handled — Escape must not revert/blur the multiline code editor
            return;
        }
        base.OnCancel(eventData);
    }
}
