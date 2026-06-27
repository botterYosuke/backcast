// JapaneseFontFallbackE2ERunner.cs — issue #16 文字化け regression gate.
//
// THE REGRESSION GUARDED: the Strategy Editor's editing surface AND both output panes (rich + console)
// render through CascadiaMono SDF (StrategyEditorContentBuilder.EditorTmpFont). Cascadia Mono is a
// Latin-only programming face — it has NO CJK glyphs — so before #16 every 日本語 character a user typed
// in the source or print()ed to output rendered as the missing-glyph box □ (perceived as "文字化け";
// the underlying data path was already correct UTF-8/UTF-16 end-to-end — this was a font-coverage gap,
// not an encoding bug). #16 commits an OFL Japanese face (M PLUS 1 Code) as MPLUS1Code SDF and chains it
// onto CascadiaMono SDF's fallbackFontAssetTable (TmpFoundationSetup.GenerateJpFallbackIfMissing). TMP's
// fallback chain then resolves the CJK glyphs that Cascadia lacks.
//
// This gate proves the precondition: the editor's actual font (read off the BUILT surface, not a Resources
// guess) can resolve a CJK codepoint (あ / U+3042) through its fallback chain. Coverage is checked with
// TMP_FontAsset.HasCharacter(searchFallbacks:true, tryAddCharacter:true) — the SAME call TMP makes on the
// real render path to decide whether a glyph is drawable, so a PASS means the editor would actually paint
// あ rather than □. The VISUAL "日本語 shows instead of □" confirmation in the live editor is owner HITL
// (same -nographics boundary as the zoom-crispness gate, findings 0096).
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod JapaneseFontFallbackE2ERunner.Run -logFile <abs>
//   # expect: [E2E JAPANESE FONT FALLBACK PASS] + per-id [E2E JPFONT-0N PASS] / exit=0
//   # 確認は Bash `grep -a "E2E JPFONT"`. compile-only ゲート: -executeMethod を外して error CS\d+ 0 件。
//
// DELETE-THE-LOGIC litmus: clear CascadiaMono SDF's fallbackFontAssetTable (or remove the JP entry / swap
// it for a Latin-only fallback) → JPFONT-01/02 find no CJK-covering fallback → RED. Re-run
// TmpFoundationSetup ("Fix Japanese Font" / -executeMethod Run) re-wires it → GREEN.
//
// SECTIONS:
//   01 — the BUILT editor textComponent's font resolves あ (U+3042) through its fallback chain
//   02 — both output panes (rich + console) resolve あ through their fallback chain too

using System;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;

public static class JapaneseFontFallbackE2ERunner
{
    const char CjkProbe = 'あ';   // HIRAGANA LETTER A (U+3042) — present in any real Japanese face, absent in Latin faces

    public static void Run()
    {
        string fail;
        var hostGo = new GameObject("JpFontFallbackHost", typeof(RectTransform));
        try
        {
            var host = (RectTransform)hostGo.transform;
            var bodyGo = new GameObject("EditorBody", typeof(RectTransform));
            var body = (RectTransform)bodyGo.transform;
            body.SetParent(host, false);
            body.sizeDelta = new Vector2(600f, 400f);

            var view = StrategyEditorContentBuilder.Build(body);
            if (view == null) { fail = "driver: Build returned null"; }
            else
            {
                fail = Section01_EditorFontCoversCjk(body)
                    ?? Section02_OutputPanesCoverCjk(body);
            }
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { UnityEngine.Object.DestroyImmediate(hostGo); }

        if (fail == null)
        {
            Debug.Log("[E2E JAPANESE FONT FALLBACK PASS] issue #16 — editor + output panes resolve あ (U+3042) " +
                      "through CascadiaMono SDF's CJK fallback (M PLUS 1 Code). No more 文字化け □. " +
                      "(Live visual 日本語 confirmation = owner HITL.)");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E JAPANESE FONT FALLBACK FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // =====================================================================================================
    // 01 — the editing surface's font (read off the BUILT StrategyInputField.textComponent, i.e. exactly what
    // the editor renders with) resolves a CJK codepoint through its fallback chain. The primary face is
    // Latin-only Cascadia; PASS requires a fallback font that actually covers あ. (delete-the-logic litmus)
    // =====================================================================================================
    static string Section01_EditorFontCoversCjk(RectTransform body)
    {
        var field = body.GetComponentInChildren<StrategyInputField>(true);
        var editor = field != null ? field.textComponent : null;
        if (editor == null) return "JPFONT-01: editor textComponent missing";
        if (editor.font == null) return "JPFONT-01: editor textComponent has no font asset";

        string e = AssertFallbackCoversCjk(editor.font, "editor textComponent (" + editor.font.name + ")");
        if (e != null) return e;

        Debug.Log("[E2E JPFONT-01 PASS] editor font '" + editor.font.name + "' resolves あ through its CJK fallback chain.");
        return null;
    }

    // =====================================================================================================
    // 02 — the per-cell output panes (rich + console) resolve CJK too, so a print("買い") shows 日本語, not □.
    // =====================================================================================================
    static string Section02_OutputPanesCoverCjk(RectTransform body)
    {
        var field = body.GetComponentInChildren<StrategyInputField>(true);
        var editor = field != null ? field.textComponent : null;

        var outputs = body.GetComponentsInChildren<TMP_Text>(true)
                          .Where(t => t != editor)
                          .ToArray();
        if (outputs.Length < 2)
            return "JPFONT-02: expected >=2 output TMP_Text panes (rich + console), found " + outputs.Length;

        foreach (var t in outputs)
        {
            if (t.font == null) return "JPFONT-02: output pane '" + t.name + "' has no font asset";
            string e = AssertFallbackCoversCjk(t.font, "output pane '" + t.name + "'");
            if (e != null) return e;
        }

        Debug.Log("[E2E JPFONT-02 PASS] " + outputs.Length + " output panes resolve あ through their CJK fallback chain.");
        return null;
    }

    // A primary font "covers CJK" iff TMP's own render-path check can resolve the probe glyph through the
    // chain. HasCharacter(searchFallbacks:true, tryAddCharacter:true) is exactly what TMP calls when laying
    // out text: it walks the fallbackFontAssetTable and, for the dynamic CJK face, loads+adds the glyph from
    // the source cmap (a true result ⇒ the editor would PAINT あ, not □). We first assert the table is
    // non-empty so a dropped-fallback regression reports the precise cause rather than a generic miss.
    // (The primary face — Cascadia — is Latin-only by design, so only a covering FALLBACK can satisfy this;
    // that fallback is exactly the link #16 adds and a bad merge / reimport would drop.)
    static string AssertFallbackCoversCjk(TMP_FontAsset primary, string label)
    {
        var table = primary.fallbackFontAssetTable;
        if (table == null || table.Count == 0)
            return "JPFONT: " + label + " has an EMPTY fallbackFontAssetTable — no CJK fallback, 日本語 renders as □";

        if (primary.HasCharacter(CjkProbe, searchFallbacks: true, tryAddCharacter: true))
            return null;   // covered — TMP's render path resolves あ through the chain

        return "JPFONT: " + label + " fallback chain (" + table.Count + " entr" + (table.Count == 1 ? "y" : "ies") +
               ") cannot resolve あ (U+3042) — 日本語 renders as □. Re-run TmpFoundationSetup to wire MPLUS1Code SDF.";
    }
}
