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
using UnityEngine;
using UnityEngine.EventSystems;

public class StrategyInputField : TMP_InputField
{
    // #149 (findings 0121): the blinking text caret was INVISIBLE at every zoom (owner HITL 2026-06-26) —
    // typing/Backspace worked (focus was fine) but no caret was ever drawn. Root cause: TMP_InputField
    // creates its caret CanvasRenderer (m_CachedInputRenderer) in EXACTLY ONE place — OnEnable
    // (com.unity.ugui 2.0.0, TMP_InputField.cs:1172) — and ONLY when m_TextComponent != null. The builder
    // adds StrategyInputField via the `new GameObject(typeof(StrategyInputField))` constructor, which fires
    // OnEnable synchronously while textComponent is still null (the builder assigns textComponent on the
    // NEXT line), so the caret renderer was never created and the caret mesh path (UpdateGeometry) early-
    // returns on the null renderer (TMP_InputField.cs:3769) — caret never drawn, at any zoom. The fix builds the editor
    // subtree INACTIVE and wires textComponent BEFORE the field's first enable, so OnEnable fires once with
    // it present (Play mode → caret created); see StrategyEditorContentBuilder.
    //
    // OnEnableCount / TextComponentReadyAtLastEnable are the AFK-observable seam (STRATEGY-62). The caret
    // CanvasRenderer creation itself is `Application.isPlaying`-gated (TMP_InputField.cs:1170), so it cannot
    // be observed in -batchmode -nographics EditMode (the TMP caret/mesh trap, findings 0096) — the real
    // blinking caret is HITL (STRATEGY-18). But OnEnable DOES run in EditMode, so the gate can pin the
    // EXACT condition the caret creation depends on: "the latest OnEnable saw a non-null textComponent".
    // The buggy ordering (field enabled before textComponent was wired) records false (RED); building the
    // subtree inactive and wiring textComponent before the first enable records true (GREEN).
    public int OnEnableCount { get; private set; }
    public bool TextComponentReadyAtLastEnable { get; private set; }

    protected override void OnEnable()
    {
        base.OnEnable();
        OnEnableCount++;
        TextComponentReadyAtLastEnable = textComponent != null;
    }

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

    // #150 (findings 0117 §HITL 続報): the SECOND Escape→revert path. Consuming OnCancel above closed
    // path 1 (the Cancel ACTION), but InputSystemUIInputModule forwards Escape into TWO seams: the Cancel
    // action AND the IMGUI key-pump queue that base.OnUpdateSelected pumps every frame. In that pump,
    // base.KeyPressed(Escape) (com.unity.ugui 2.0.0, TMP_InputField.cs:2276) sets m_WasCanceled=true and
    // returns EditState.Finish; the pump (TMP_InputField.cs:2378) then calls DeactivateInputField(), which
    // does text = m_OriginalText (revert) + blur. So after 6ff73ae, focusing a cell, typing, and pressing
    // Escape STILL discarded the edit — the residual path-2 revert. (#148's Enter only needed OnSubmit
    // because path 2's Enter inserts a newline without deactivating, TMP_InputField.cs:2263 — Escape is
    // asymmetric: path 2 deactivates+reverts.)
    //
    // Fix: for MultiLineNewline we OWN the key pump (override OnUpdateSelected) and SWALLOW Escape key
    // events before base.KeyPressed can see them — so the editor is never deactivated/reverted. Every
    // OTHER event is re-processed exactly as the base pump does (TMP_InputField.cs:2356-2413, via the
    // protected KeyPressed/SendOnSubmit/SelectAll/ForceLabelUpdate primitives) so normal editing,
    // navigation and Windows IME are unaffected. (There is no narrower seam: base KeyPressed/ProcessEvent/
    // DeactivateInputField are all NON-virtual, and Event.PopEvent is a destructive pop with no push-back —
    // so Escape can only be intercepted by re-owning the virtual OnUpdateSelected pump, not by overriding a
    // smaller method or pop-filtering then delegating to base.) UpdateLabel always re-assigns m_TextComponent.text and
    // re-shows the caret (TMP_InputField.cs:3500/3503); the private m_IsTextComponentUpdateRequired flag
    // only gates one extra immediate ForceMeshUpdate that TMP redoes on the next canvas update anyway, so
    // ForceLabelUpdate() is a faithful stand-in. The Settings dialog still opens because it is driven by
    // an INDEPENDENT keyboard poll (BackcastWorkspaceRoot.DriveSettings) that never grabs EventSystem
    // selection — so keeping the field active here does not fight it (AC: ESC still shows Settings).
    // Single-line fields keep TMP's default pump (Escape there SHOULD cancel — search/name fields).
    //
    // EscapeKeyPumpConsumedCount is the AFK-observable seam (STRATEGY-61): -batchmode -nographics has no
    // IMGUI key pump / focus to drive a real OnUpdateSelected, so the gate invokes TryConsumeKeyPumpEscape
    // directly and asserts (i) it swallows a multiline Escape and (ii) when NOT swallowed, the modelled
    // base path (ProcessEvent+DeactivateInputField) reverts — i.e. the swallow is what prevents the loss.
    // The real keystroke→no-revert→focus path stays HITL (STRATEGY-18). Known IME deviations (private
    // m_IsCompositionActive/compositionLength are inaccessible, so neither can be replicated; both are in
    // the HITL/IME domain, STRATEGY-18): (1) the OSX composition-suppression micro-branch
    // (TMP_InputField.cs:2370-2375) is omitted (owner is Windows); (2) the final block's
    // `(m_IsCompositionActive && compositionLength > 0)` half (UUM-100552, TMP_InputField.cs:2409) is
    // dropped — we Use()/refresh only on `consumedEvent`. This is observably benign: that clause only
    // fires on input-LESS composition frames (no KeyDown popped), and the composition preview only CHANGES
    // when a key/conversion arrives — which sets consumedEvent and runs our refresh+Use. A paused-
    // composition frame would only redo an unchanged preview and Use an event carrying no key.
    public int EscapeKeyPumpConsumedCount { get; private set; }

    readonly Event _keyPumpEvent = new Event();

    // True (and counted) iff this is a key-pump Escape we must swallow for the multiline code editor.
    // Multiline-gated so single-line keeps default cancel; keyCode-gated so ordinary typing/navigation
    // still flows to base.KeyPressed (a blanket swallow would break editing). Side-effecting "TryConsume"
    // mirrors OnCancel/OnSubmit: invoked by the pump below AND directly by the AFK gate.
    public bool TryConsumeKeyPumpEscape(Event e)
    {
        if (lineType == LineType.MultiLineNewline
            && e != null
            && e.rawType == EventType.KeyDown
            && e.keyCode == KeyCode.Escape)
        {
            EscapeKeyPumpConsumedCount++;
            return true;
        }
        return false;
    }

    public override void OnUpdateSelected(BaseEventData eventData)
    {
        // Single-line fields: unchanged — Escape SHOULD cancel/blur a search/name field.
        if (lineType != LineType.MultiLineNewline)
        {
            base.OnUpdateSelected(eventData);
            return;
        }
        if (!isFocused)
            return;

        // Multiline code editor: own the IMGUI key pump so Escape is swallowed before base.KeyPressed
        // (mirror of base OnUpdateSelected at TMP_InputField.cs:2356-2413, with Escape filtered).
        bool consumedEvent = false;
        while (Event.PopEvent(_keyPumpEvent))
        {
            EventType eventType = _keyPumpEvent.rawType;
            if (eventType == EventType.KeyUp)
                continue;

            if (eventType == EventType.KeyDown)
            {
                consumedEvent = true;

                if (TryConsumeKeyPumpEscape(_keyPumpEvent))   // THE FIX: drop Escape; base never deactivates+reverts
                    continue;

                EditState editState = KeyPressed(_keyPumpEvent);
                if (editState == EditState.Finish)
                {
                    // The only non-Escape Finish in a multiline field is Return at the line limit. Mirror base.
                    if (!wasCanceled)
                        SendOnSubmit();
                    DeactivateInputField();
                    break;
                }

                ForceLabelUpdate();
                continue;
            }

            if (eventType == EventType.ValidateCommand || eventType == EventType.ExecuteCommand)
            {
                if (_keyPumpEvent.commandName == "SelectAll")
                {
                    SelectAll();
                    consumedEvent = true;
                }
            }
        }

        if (consumedEvent)
        {
            ForceLabelUpdate();
            eventData?.Use();
        }
    }
}
