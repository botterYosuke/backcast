// StrategyEditorZoomCrispnessE2ERunner.cs — issue #121 release-gate (台本: same-dir
// StrategyEditorZoomCrispnessE2ERunner.md, design tree: docs/findings/0096 §gate).
//
// THE REGRESSION GUARDED: the InfiniteCanvas zooms the Strategy Editor by scaling an ancestor's
// Content.localScale (0.2–5×).  A legacy uGUI Text / InputField rasterizes each glyph into a dynamic-
// font atlas at its DISPLAY pixel size and then the transform stretches that bitmap — so at 5× the
// text is blurry (findings 0096 root cause).  TMP(SDF) renders through a Distance-Field shader that
// reconstructs glyph outlines at the on-screen scale, so it stays crisp at any zoom, independent of
// the transform.  #117–#120 migrated the editing + output + syntax surfaces to TMP/SDF; THIS gate is
// the structural regression net that the editor never silently reverts to the legacy bitmap pipeline.
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod StrategyEditorZoomCrispnessE2ERunner.Run -logFile <abs>
//   # expect: [E2E STRATEGY EDITOR ZOOM CRISPNESS PASS] + per-id [E2E ZOOM-0N PASS] / exit=0
//   # 確認は Bash `grep -a "E2E ZOOM"`. compile-only ゲート: -executeMethod を外して error CS\d+ 0 件。
//
// DELETE-THE-LOGIC litmus: revert the editing surface to a legacy UnityEngine.UI.Text / InputField
// (or an output pane to legacy Text) → ZOOM-01 finds a legacy component in the editor subtree → RED.
// Revert any TMP surface's font to a NON-SDF (bitmap/raster) font asset → ZOOM-02/03 go RED.
//
// HITL boundary (findings 0096): the VISUAL crispness at 5× (a sharp screenshot) is owner-verified —
// headless -nographics cannot rasterize/sample pixels.  This gate proves the STRUCTURAL precondition
// for crispness (SDF pipeline present everywhere, no legacy bitmap-atlas surface) + the scale-
// independence invariant (ZOOM-04: an ancestor localScale does not change the TMP fontSize), which is
// the mechanism that MAKES the screenshot crisp.
//
// SECTIONS:
//   01 — the built editor surface is TMP_InputField + TMP_Text; ZERO legacy uGUI Text / InputField (litmus)
//   02 — editor textComponent + placeholder use an SDF TMP_FontAsset (SDF render mode + Distance Field shader)
//   03 — both output panes (rich + console) are TMP_Text on an SDF font
//   04 — scale-independence: under an ancestor localScale 5×, the editor TMP fontSize is UNCHANGED

using System;
using System.Linq;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

public static class StrategyEditorZoomCrispnessE2ERunner
{
    public static void Run()
    {
        string fail;
        var hostGo = new GameObject("ZoomCrispnessHost", typeof(RectTransform));
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
                fail = Section01_NoLegacySurfaces(body)
                    ?? Section02_EditorFontIsSdf(body)
                    ?? Section03_OutputPanesAreSdf(body)
                    ?? Section04_ScaleIndependence(host, body);
            }
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { UnityEngine.Object.DestroyImmediate(hostGo); }

        if (fail == null)
        {
            Debug.Log("[E2E STRATEGY EDITOR ZOOM CRISPNESS PASS] issue #121 — editor on TMP(SDF) end-to-end: " +
                      "no legacy uGUI Text/InputField (01) / editor font SDF (02) / output panes SDF (03) / " +
                      "fontSize scale-independent under 5× ancestor (04). findings 0096 §gate. " +
                      "(5× visual sharpness screenshot = owner HITL.)");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E STRATEGY EDITOR ZOOM CRISPNESS FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // =====================================================================================================
    // 01 — the built editor surface is TMP, with NO legacy uGUI bitmap surfaces. StrategyInputField now
    // derives from TMP_InputField (NOT UnityEngine.UI.InputField), and every text surface is a TMP_Text,
    // so the whole subtree carries ZERO UnityEngine.UI.Text / InputField. (delete-the-logic litmus)
    // =====================================================================================================
    static string Section01_NoLegacySurfaces(RectTransform body)
    {
        int legacyText = body.GetComponentsInChildren<Text>(true).Length;
        if (legacyText != 0)
            return "ZOOM-01: found " + legacyText + " legacy UnityEngine.UI.Text in the editor subtree (must be 0 — TMP only)";

        int legacyInput = body.GetComponentsInChildren<InputField>(true).Length;
        if (legacyInput != 0)
            return "ZOOM-01: found " + legacyInput + " legacy UnityEngine.UI.InputField (must be 0 — TMP_InputField only)";

        var field = body.GetComponentInChildren<StrategyInputField>(true);
        if (field == null) return "ZOOM-01: no StrategyInputField in the editor subtree";
        if (!(field is TMP_InputField)) return "ZOOM-01: StrategyInputField is not a TMP_InputField";
        if (!(field.textComponent is TMP_Text)) return "ZOOM-01: editing surface textComponent is not a TMP_Text";

        Debug.Log("[E2E ZOOM-01 PASS] editor surface is TMP_InputField + TMP_Text; zero legacy uGUI Text/InputField.");
        return null;
    }

    // =====================================================================================================
    // 02 — the editing surface (textComponent + placeholder) renders through an SDF TMP_FontAsset: an SDF
    // atlasRenderMode AND a Distance-Field shader material. That shader is what reconstructs glyph outlines
    // at any zoom — the crispness mechanism. (Tolerates the production Cascadia SDF or the default SDF
    // fallback; both satisfy SDF-ness, which is the crispness invariant — findings 0096 D1.)
    // =====================================================================================================
    static string Section02_EditorFontIsSdf(RectTransform body)
    {
        var field = body.GetComponentInChildren<StrategyInputField>(true);
        var editor = field != null ? field.textComponent : null;
        if (editor == null) return "ZOOM-02: editor textComponent missing";
        string e = AssertSdf(editor, "editor textComponent");
        if (e != null) return e;

        // The placeholder Graphic (the host-API hint surface) must be SDF too — it shares the zoom.
        var ph = field.placeholder as TMP_Text;
        if (ph == null) return "ZOOM-02: placeholder is not a TMP_Text";
        e = AssertSdf(ph, "placeholder");
        if (e != null) return e;

        Debug.Log("[E2E ZOOM-02 PASS] editor textComponent + placeholder render through an SDF TMP_FontAsset " +
                  "(font=" + editor.font.name + ", renderMode=" + editor.font.atlasRenderMode + ").");
        return null;
    }

    // =====================================================================================================
    // 03 — the per-cell output panes (rich + console, #118) are TMP_Text on an SDF font too, so a 5× zoom
    // keeps RUN output crisp, not just the code. There are exactly 4 TMP surfaces in the editor (editor,
    // placeholder, rich, console); 01/02 cover the first two, this covers the remaining two.
    // =====================================================================================================
    static string Section03_OutputPanesAreSdf(RectTransform body)
    {
        var field = body.GetComponentInChildren<StrategyInputField>(true);
        var editor = field != null ? field.textComponent : null;
        var ph = field != null ? field.placeholder as TMP_Text : null;

        var outputs = body.GetComponentsInChildren<TMP_Text>(true)
                          .Where(t => t != editor && t != ph)
                          .ToArray();
        if (outputs.Length < 2)
            return "ZOOM-03: expected >=2 output TMP_Text panes (rich + console), found " + outputs.Length;

        foreach (var t in outputs)
        {
            string e = AssertSdf(t, "output pane '" + t.name + "'");
            if (e != null) return e;
        }

        Debug.Log("[E2E ZOOM-03 PASS] " + outputs.Length + " output panes (rich/console) render through an SDF font.");
        return null;
    }

    // =====================================================================================================
    // 04 — scale-independence invariant: the InfiniteCanvas zoom scales an ANCESTOR transform's localScale;
    // a TMP_Text's fontSize is a property independent of that transform, so the glyphs are laid out at the
    // SAME point size and the SDF shader supplies the visual upscale (crisp). A legacy Text would instead
    // be re-stretched as a bitmap. We assert the editor fontSize is unchanged after a 5× ancestor scale +
    // a forced TMP regen — documenting the mechanism the HITL screenshot confirms visually.
    // =====================================================================================================
    static string Section04_ScaleIndependence(RectTransform host, RectTransform body)
    {
        var field = body.GetComponentInChildren<StrategyInputField>(true);
        var editor = field != null ? field.textComponent : null;
        if (editor == null) return "ZOOM-04: editor textComponent missing";

        editor.text = "def f():\n    return 1\n";
        editor.ForceMeshUpdate();
        float before = editor.fontSize;
        if (before <= 0f) return "ZOOM-04: editor fontSize is non-positive (" + before + ")";

        host.localScale = new Vector3(5f, 5f, 1f);   // mimic InfiniteCanvas 5× zoom on an ancestor
        editor.ForceMeshUpdate();
        float after = editor.fontSize;

        if (Mathf.Abs(after - before) > 1e-4f)
            return "ZOOM-04: editor fontSize changed under a 5× ancestor scale (" + before + " -> " + after +
                   ") — fontSize must be transform-scale-independent (SDF, not bitmap re-raster)";

        Debug.Log("[E2E ZOOM-04 PASS] editor fontSize (" + after + ") is invariant under a 5× ancestor localScale " +
                  "— SDF shader supplies the zoom, not a transform-stretched bitmap.");
        return null;
    }

    // An SDF TMP font asset = an SDF atlasRenderMode AND a Distance-Field shader on its atlas material.
    static string AssertSdf(TMP_Text t, string label)
    {
        var fa = t.font;
        if (fa == null) return "ZOOM: " + label + " has no font asset";
        if (fa.atlasRenderMode.ToString().IndexOf("SDF", StringComparison.OrdinalIgnoreCase) < 0)
            return "ZOOM: " + label + " font atlasRenderMode is " + fa.atlasRenderMode + " (not an SDF mode) — bitmap pipeline, blurs at zoom";
        var mat = fa.material;
        if (mat == null || mat.shader == null)
            return "ZOOM: " + label + " font has no atlas material/shader";
        if (mat.shader.name.IndexOf("Distance Field", StringComparison.OrdinalIgnoreCase) < 0)
            return "ZOOM: " + label + " font shader is '" + mat.shader.name + "' (not a TMP Distance Field shader)";
        return null;
    }
}
