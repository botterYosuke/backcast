// PythonSyntaxMeshEffect.cs — issue #16 "Strategy Editor" / #120 TMP(SDF) recolour (findings 0096 D4)
//
// Syntax highlighting WITHOUT rich-text tags (findings 0010 §1 / 0096 D4, owner-locked): a behaviour
// that recolours the SAME TMP geometry by per-glyph vertex colour, driven by PythonHighlighter token
// spans. Because no tags are inserted into the source string, every character index — and thus the
// caret, selection, and TMP_InputField editing — stays intact (a sibling rich-text overlay could
// never stay in sync with the field's internal scroll / IME composition / caret).
//
// TMP RECOLOUR HOOK (findings 0096 D4): TMP regenerates its mesh on every edit/scroll/caret-blink and
// fires `TMP_Text.OnPreRenderText(textInfo)` AFTER laying out the glyphs (base colour applied) but
// BEFORE the canvas upload (TextMeshProUGUI.GenerateTextMesh: OnPreRenderText → m_mesh.colors32 = … →
// canvasRenderer.SetMesh). So we override the per-glyph colours in that callback and they upload with
// no extra UpdateVertexData call — and the recolour survives every TMP regen for free (no SetVerticesDirty
// races like the legacy BaseMeshEffect.ModifyMesh path).
//
// GLYPH<->INDEX MAPPING (findings 0096 §#120 refinement): TMP keeps the FULL source text in the text
// component (it does NOT truncate to the visible line window the way a focused legacy multiline
// InputField did), so `textInfo.characterInfo[i].index` is ALREADY the full-source UTF-16 index —
// token lookup is direct, and the legacy `displayStart` offset machinery (StrategyInputField.
// VisibleDrawStart) is gone. We walk visible characters, and for each we paint the 4 verts of its quad
// (`characterInfo[i].vertexIndex` in `meshInfo[characterInfo[i].materialReferenceIndex].colors32`) the
// colour of the token covering `characterInfo[i].index`. Fallback glyphs land on a different material/
// sub-mesh, so we index meshInfo by the per-character materialReferenceIndex (multi-atlas safe).
//
// SetTokens forces a mesh update so a re-tokenize after an edit recolours immediately (deterministic
// for the AFK probe, which does not pump frames). Surrogate pairs and IME composition remain HITL
// (findings 0010 §9): TMP maps a surrogate pair to one characterInfo entry whose .index is the leading
// unit, which still falls inside the covering token, so ASCII source colours correctly AFK.

using System.Collections.Generic;
using TMPro;
using UnityEngine;

[RequireComponent(typeof(TextMeshProUGUI))]
public class PythonSyntaxMeshEffect : MonoBehaviour
{
    // Palette (HITL-visual; AFK only asserts that a token glyph differs from base and Default does
    // not). Default == the TMP_Text's own colour, so uncovered glyphs are left UNCHANGED.
    // Issue #44: sourced from ThemeService.Current.syntax (findings 0020 mapping) — Decorator has
    // no TTWR role so it borrows `type_`; Definition (def/class name) maps to `function`. Set in
    // OnEnable and re-pulled by ApplyTheme() on a theme switch.
    public Color keyword;
    public Color stringLit;
    public Color comment;
    public Color number;
    public Color decorator;
    public Color definition;

    TMP_Text _text;
    List<PythonToken> _tokens;
    bool _subscribed;

    TMP_Text TextComp => _text != null ? _text : (_text = GetComponent<TMP_Text>());

    // Subscribe to the recolour hook lazily + idempotently. OnEnable is not guaranteed to fire in a
    // headless EditMode harness (cf. ThemeProbe), so SetTokens/ApplyTheme also call this before any
    // ForceMeshUpdate — otherwise the AFK recolour would silently never run.
    void EnsureSubscribed()
    {
        if (_subscribed || TextComp == null) return;
        _text.OnPreRenderText += ApplyTokenColours;
        _subscribed = true;
    }

    // Pull the syntax palette from the active theme and recolour (issue #44). Called from OnEnable so
    // freshly-added effects are themed, and by the owning harness on ThemeService.Changed. type_ ←
    // Decorator, function ← Definition (findings 0020).
    public void ApplyTheme()
    {
        var sx = ThemeService.Current.syntax;
        keyword = sx.keyword;
        stringLit = sx.str;
        comment = sx.comment;
        number = sx.number;
        decorator = sx.type_;
        definition = sx.function;
        Recolour();
    }

    void OnEnable()
    {
        EnsureSubscribed();
        ApplyTheme();
    }

    void OnDisable()
    {
        if (_text != null) _text.OnPreRenderText -= ApplyTokenColours;
        _subscribed = false;
    }

    // Push freshly-computed full-source tokens and force a recolour. No display offset: TMP holds the
    // full text, so characterInfo[i].index is the full-source index (findings 0096 §#120 refinement).
    public void SetTokens(List<PythonToken> tokens)
    {
        _tokens = tokens;
        Recolour();
    }

    // Force TMP to regenerate the mesh — that fires OnPreRenderText, which re-applies our colours.
    // Skip when no font is assigned (nothing to lay out / recolour): a fontless TMP_Text — e.g. the
    // headless ThemeProbe which only checks the palette fields — would otherwise log a TMP warning.
    void Recolour()
    {
        EnsureSubscribed();
        if (TextComp != null && _text.font != null) _text.ForceMeshUpdate();
    }

    // Recolour hook: TMP has laid out the glyphs (base colour) and is about to upload — override the
    // per-glyph vertex colours in place (uploaded directly afterward; no UpdateVertexData needed).
    // Public so the headless AFK gate can drive it with a synthetic TMP_TextInfo (the legacy probe fed
    // a synthetic VertexHelper to ModifyMesh the same way — -nographics does not run TMP's canvas mesh
    // generation, so we assert the recolour write against a deterministic textInfo).
    public void ApplyTokenColours(TMP_TextInfo textInfo)
    {
        if (textInfo == null || _tokens == null || _tokens.Count == 0) return;
        Color baseColor = TextComp != null ? _text.color : Color.white;

        int count = textInfo.characterCount;
        for (int i = 0; i < count; i++)
        {
            var ci = textInfo.characterInfo[i];
            if (!ci.isVisible) continue;                      // whitespace/newline -> no quad

            int matIdx = ci.materialReferenceIndex;
            if (matIdx < 0 || matIdx >= textInfo.meshInfo.Length) continue;
            var colors = textInfo.meshInfo[matIdx].colors32;
            int vi = ci.vertexIndex;
            if (colors == null || vi + 3 >= colors.Length) continue;   // guard: past the populated mesh

            Color32 c = ColorForIndex(ci.index, baseColor);
            colors[vi + 0] = c;
            colors[vi + 1] = c;
            colors[vi + 2] = c;
            colors[vi + 3] = c;
        }
    }

    // Colour of the token covering `fullIndex`, or the base colour when none (Default stays
    // unchanged). Tokens are ascending + non-overlapping, so a linear-then-break scan is fine.
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
}
