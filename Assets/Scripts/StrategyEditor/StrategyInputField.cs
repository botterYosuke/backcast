// StrategyInputField.cs — issue #16 "Strategy Editor" (DURABLE tier, Unity boundary)
//
// A one-line InputField subclass that exposes the VISIBLE display-window start so the syntax
// mesh effect can offset full-source token spans onto the truncated displayed substring
// (findings 0010 §1, the owner-approved fallback the HITL Step-5 scroll failure triggered).
//
// WHY: while focused, legacy InputField scrolls a MULTILINE field by setting its text component
// to `fullText.Substring(m_DrawStart, m_DrawEnd - m_DrawStart)` (InputField.cs UpdateLabel) — so
// the Text the mesh effect colours is only the visible LINE window, starting at m_DrawStart in
// the full document. m_DrawStart changes on scroll WITHOUT an onValueChanged, so it must be read
// LIVE at mesh-build time. m_DrawStart is `protected`, so a subclass reads it directly — NO
// reflection (findings 0010 §1). When unfocused, InputField resets m_DrawStart=0 (whole text),
// so VisibleDrawStart is naturally 0 then.

using UnityEngine.UI;

public class StrategyInputField : InputField
{
    // UTF-16 index into the FULL text of the first character currently shown in the text
    // component (0 when unfocused / not scrolled). The mesh effect adds this to each displayed
    // glyph's local index to recover its full-source index for token lookup.
    public int VisibleDrawStart => m_DrawStart;
}
