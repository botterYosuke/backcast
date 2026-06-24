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

public class StrategyInputField : TMP_InputField
{
}
