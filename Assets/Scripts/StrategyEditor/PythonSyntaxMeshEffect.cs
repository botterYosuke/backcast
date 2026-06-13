// PythonSyntaxMeshEffect.cs — issue #16 "Strategy Editor" (DURABLE tier, Unity boundary)
//
// Syntax highlighting WITHOUT rich-text tags (findings 0010 §1, owner-locked): a BaseMeshEffect
// that recolours the SAME Text geometry by per-glyph UIVertex.color, driven by PythonHighlighter
// token spans. Because no tags are inserted into the source string, every character index — and
// thus the caret, selection, and InputField editing — stays intact (a sibling rich-text overlay
// could never stay in sync with InputField's internal scroll / IME composition / caret).
//
// GLYPH<->INDEX MAPPING (findings 0010 §8): legacy uGUI Text emits 4 verts per VISIBLE glyph and
// NONE for whitespace/newline, in source order. We walk the displayed string, and for each
// glyph-producing char assign its quad's 4 verts the colour of the token covering that char.
// `displayStart` offsets the displayed string into the FULL source. A FOCUSED multiline
// InputField truncates its text component to the visible LINE window [m_DrawStart, m_DrawEnd)
// (InputField.UpdateLabel) and changes m_DrawStart on SCROLL without an onValueChanged — so the
// offset is read LIVE via SetDisplayStartProvider (wired to StrategyInputField.VisibleDrawStart),
// NOT cached. (HITL Step-5 scroll misalignment, findings 0010 §11, disproved the earlier
// "multiline keeps the full text" assumption.) When no provider is set (the AFK probe), the fixed
// fallback _displayStart=0 is used. Surrogate pairs and IME composition (where vert count and
// UTF-16 index stop corresponding 1:1) remain HITL, not AFK (findings 0010 §9).
//
// SetTokens triggers a mesh rebuild (SetVerticesDirty) so a re-tokenize after an edit recolours.

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Text))]
public class PythonSyntaxMeshEffect : BaseMeshEffect
{
    // Palette (HITL-visual; AFK only asserts that a token glyph differs from base and Default does
    // not). Default == the Text's own colour, so uncovered glyphs are left UNCHANGED.
    public Color keyword    = new Color(0.36f, 0.62f, 0.92f, 1f);
    public Color stringLit  = new Color(0.42f, 0.74f, 0.42f, 1f);
    public Color comment    = new Color(0.50f, 0.52f, 0.55f, 1f);
    public Color number     = new Color(0.82f, 0.62f, 0.92f, 1f);
    public Color decorator  = new Color(0.90f, 0.78f, 0.36f, 1f);
    public Color definition = new Color(0.36f, 0.82f, 0.80f, 1f);

    Text _text;
    List<PythonToken> _tokens;
    int _displayStart;                  // fixed fallback offset (used when no live provider is set)
    Func<int> _displayStartProvider;    // LIVE offset of the displayed substring into the full source

    Text TextComp => _text != null ? _text : (_text = GetComponent<Text>());

    // Push freshly-computed full-source tokens (and the fixed-fallback display offset) and force a
    // recolour. The fallback is used only when no live provider is set (e.g. the AFK probe).
    public void SetTokens(List<PythonToken> tokens, int displayStart = 0)
    {
        _tokens = tokens;
        _displayStart = displayStart < 0 ? 0 : displayStart;
        if (TextComp != null) TextComp.SetVerticesDirty();
    }

    // Supply a LIVE display-start source (StrategyInputField.VisibleDrawStart). A focused multiline
    // InputField truncates its text component to the visible LINE window and changes the start on
    // SCROLL without an onValueChanged, so the offset must be read at mesh-build time, not cached.
    public void SetDisplayStartProvider(Func<int> provider)
    {
        _displayStartProvider = provider;
        if (TextComp != null) TextComp.SetVerticesDirty();
    }

    public override void ModifyMesh(VertexHelper vh)
    {
        if (!IsActive() || _tokens == null || _tokens.Count == 0) return;
        var text = TextComp;
        if (text == null) return;
        string s = text.text;
        if (string.IsNullOrEmpty(s)) return;

        Color baseColor = text.color;
        int vertCount = vh.currentVertCount;
        // Live display offset: a focused multiline InputField shows only [m_DrawStart, m_DrawEnd);
        // its glyph j is full-source char (displayStart + j). Read it now (it changes on scroll).
        int displayStart = _displayStartProvider != null ? _displayStartProvider() : _displayStart;
        if (displayStart < 0) displayStart = 0;

        var v = new UIVertex();
        int rank = 0;   // visible-glyph rank == quad index
        for (int j = 0; j < s.Length; j++)
        {
            if (!ProducesGlyph(s[j])) continue;   // whitespace/newline -> no quad
            int baseVert = rank * 4;
            if (baseVert + 3 >= vertCount) break;  // guard: ran past the populated mesh

            Color c = ColorForIndex(displayStart + j, baseColor);
            for (int k = 0; k < 4; k++)
            {
                vh.PopulateUIVertex(ref v, baseVert + k);
                v.color = c;
                vh.SetUIVertex(v, baseVert + k);
            }
            rank++;
        }
    }

    // Colour of the token covering `fullIndex`, or the base colour when none (Default stays
    // unchanged). Tokens are ascending + non-overlapping, so a linear-then-break scan is fine;
    // a binary search would be a micro-opt for very long lines (HITL only).
    Color ColorForIndex(int fullIndex, Color baseColor)
    {
        var toks = _tokens;
        for (int t = 0; t < toks.Count; t++)
        {
            var tok = toks[t];
            if (fullIndex < tok.start) break;          // ascending: no later token can cover it
            if (fullIndex < tok.End) return ClassColor(tok.cls, baseColor);
        }
        return baseColor;
    }

    Color ClassColor(PythonTokenClass cls, Color baseColor)
    {
        switch (cls)
        {
            case PythonTokenClass.Keyword:    return keyword;
            case PythonTokenClass.String:     return stringLit;
            case PythonTokenClass.Comment:    return comment;
            case PythonTokenClass.Number:     return number;
            case PythonTokenClass.Decorator:  return decorator;
            case PythonTokenClass.Definition: return definition;
            default:                          return baseColor;
        }
    }

    // A character produces a Text glyph quad iff it is not whitespace/newline (findings 0010 §8,
    // owner premise for the ASCII fixed-font AFK fixture).
    static bool ProducesGlyph(char c)
        => !(c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v');
}
