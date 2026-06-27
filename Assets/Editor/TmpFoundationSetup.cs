// TmpFoundationSetup.cs — #117 TMP(SDF) foundation (headless).
//
// Sets up the project so the Strategy Editor TMP migration (#118-121) has both halves of the
// foundation present and committed, WITHOUT changing any existing look/behavior (#117 は描画方式の
// 切替を*しない*: legacy Text/InputField are still what render until #118+):
//   1. TMP Essential Resources imported → Assets/TextMesh Pro/ (TMP Settings, the SDF Distance-Field
//      shaders, default material + LiberationSans SDF default font).
//   2. an OFL monospace SDF TMP_FontAsset for the editor: Cascadia Mono (SIL OFL 1.1, copied from the
//      system font). SDF is resolution-independent, so glyphs stay crisp across the whole zoom range
//      (0.2–5×) — that is the entire point of the #117-121 epic.
//   3. (#16 文字化け) an OFL Japanese SDF TMP_FontAsset — M PLUS 1 Code (SIL OFL 1.1, committed under
//      Assets/Fonts) — chained onto Cascadia Mono's fallback table so the editor + output panes resolve
//      CJK glyphs instead of the missing-glyph box □. Cascadia is Latin-only; without this, every 日本語
//      character a user types or print()s rendered as □.
//
// Run as ONE batchmode pass WITHOUT -quit (AssetDatabase.ImportPackage is ASYNC even with
// interactive:false — a -quit fires before the import worker writes the assets, leaving TMP_Settings
// null and CreateFontAsset throwing). We wait on importPackageCompleted, then generate the font asset,
// then EditorApplication.Exit from the callback:
//   <Unity> -batchmode -nographics -projectPath <abs> -executeMethod TmpFoundationSetup.Run -logFile <abs>
//   # NOTE: no -quit.  expect: [TMP-SETUP DONE] / exit=0
//
// Idempotent in steady state: once TMP Settings, BOTH SDF assets, and the JP fallback link all exist,
// each step finds its artifact present and no-ops. The FIRST run after a step is added does that step's
// work (creates the asset / wires the fallback) then exits. A missing committed JP .ttf THROWS (Exit 1),
// never a silent green pass.

using System;
using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;

public static class TmpFoundationSetup
{
    const string FontDir = "Assets/Fonts";
    const string TtfPath = "Assets/Fonts/CascadiaMono.ttf";
    const string SystemTtf = @"C:\Windows\Fonts\CascadiaMono.ttf";
    const string TmpSettingsPath = "Assets/TextMesh Pro/Resources/TMP Settings.asset";
    const string FaDir = "Assets/TextMesh Pro/Resources/Fonts & Materials";
    const string FaPath = "Assets/TextMesh Pro/Resources/Fonts & Materials/CascadiaMono SDF.asset";
    // The runtime loader (StrategyEditorContentBuilder.EditorSdfFontResourcesPath) is the single source
    // of truth for the Resources-relative path; this Editor script writes the asset to FaPath above.

    // ---- Japanese/CJK fallback (the editor + output panes only had Cascadia Mono + LiberationSans, both
    // Latin-only, so every 日本語 glyph rendered as the missing-glyph box □ — perceived as "文字化け").
    // M PLUS 1 Code (SIL OFL 1.1) is a monospace CJK code face: ASCII keeps rendering through Cascadia,
    // and TMP's fallback chain resolves the CJK glyphs from this asset. The .ttf + OFL.txt are committed
    // under Assets/Fonts (no system-font dependency, redistributable in the shipped Player build). The
    // bake goes through the shared CreateSdfAsset with a Dynamic atlas, so no CJK glyphs are baked until
    // first use — the fallback chains in without re-baking Cascadia.
    const string JpTtfPath = "Assets/Fonts/MPLUS1Code-Regular.ttf";
    const string JpFaPath  = "Assets/TextMesh Pro/Resources/Fonts & Materials/MPLUS1Code SDF.asset";

    // Owner-driven entry point for an ALREADY-OPEN Editor (batchmode needs the project lock, which a
    // running Editor holds — so the #16 文字化け fix can't be applied head­less while you're working).
    // One click: ensures the Cascadia foundation, then creates the M+ SDF asset and wires it as the
    // Japanese fallback. Idempotent (safe to click twice). After clicking, the editor + output panes
    // resolve 日本語 glyphs immediately (Dynamic atlas populates on first display).
    [MenuItem("Tools/Backcast/Fix Japanese Font (#16 文字化け)")]
    public static void GenerateJpFallbackMenu()
    {
        try
        {
            CopyCascadiaFont();               // no-op if Assets/Fonts/CascadiaMono.ttf already imported
            GenerateSdfFontAssetIfMissing();  // no-op if CascadiaMono SDF already exists
            GenerateJpFallbackIfMissing();    // create MPLUS1Code SDF + chain it as the CJK fallback
            // No trailing SaveAssets/Refresh: the helpers above already SaveAssets + ImportAsset each
            // asset they touch, so a second sweep would be a redundant full-rescan stall on the click.
            Debug.Log("[TMP-SETUP] Japanese font fallback applied — type 日本語 in the Strategy Editor to verify.");
        }
        catch (Exception e)
        {
            Debug.LogError("[TMP-SETUP] Fix Japanese Font menu threw: " + e);
        }
    }

    public static void Run()
    {
        try
        {
            CopyCascadiaFont();

            if (File.Exists(TmpSettingsPath))
            {
                // Essentials already imported — generate the font asset synchronously and exit.
                Debug.Log("[TMP-SETUP] TMP Essential Resources already present.");
                Finish();
                return;
            }

            // Essentials missing — import async, finish in the completion callback.
            AssetDatabase.importPackageCompleted += OnImportCompleted;
            AssetDatabase.importPackageFailed += OnImportFailed;
            Debug.Log("[TMP-SETUP] importing TMP Essential Resources (async, waiting on callback)…");
            TMP_PackageResourceImporter.ImportResources(true, false, false);
            // DO NOT Exit here — the callback drives the rest.
        }
        catch (Exception e)
        {
            Debug.LogError("[TMP-SETUP] Run threw: " + e);
            EditorApplication.Exit(1);
        }
    }

    static void OnImportCompleted(string packageName)
    {
        try
        {
            Debug.Log("[TMP-SETUP] import completed: " + packageName);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            Finish();
        }
        catch (Exception e)
        {
            Debug.LogError("[TMP-SETUP] OnImportCompleted threw: " + e);
            EditorApplication.Exit(1);
        }
    }

    static void Finish()
    {
        GenerateSdfFontAssetIfMissing();
        GenerateJpFallbackIfMissing();
        Debug.Log("[TMP-SETUP DONE]");
        EditorApplication.Exit(0);
    }

    static void OnImportFailed(string packageName, string errorMessage)
    {
        Debug.LogError("[TMP-SETUP] import FAILED (" + packageName + "): " + errorMessage);
        EditorApplication.Exit(1);
    }

    static void CopyCascadiaFont()
    {
        Directory.CreateDirectory(FontDir);
        if (!File.Exists(TtfPath))
        {
            if (!File.Exists(SystemTtf))
                throw new FileNotFoundException("system CascadiaMono.ttf not found", SystemTtf);
            File.Copy(SystemTtf, TtfPath, true);
            AssetDatabase.ImportAsset(TtfPath, ImportAssetOptions.ForceSynchronousImport);
            Debug.Log("[TMP-SETUP] Copied CascadiaMono.ttf -> " + TtfPath);
        }
        // Dynamic TMP atlas needs the source face loadable at runtime → keep font data embedded.
        var ti = AssetImporter.GetAtPath(TtfPath) as TrueTypeFontImporter;
        if (ti != null && !ti.includeFontData) { ti.includeFontData = true; ti.SaveAndReimport(); }
    }

    static void GenerateSdfFontAssetIfMissing()
    {
        if (File.Exists(FaPath)) { Debug.Log("[TMP-SETUP] SDF font asset already exists."); return; }
        CreateSdfAsset(TtfPath, FaPath, "CascadiaMono SDF", "CascadiaMono SDF Atlas");
    }

    // Bake one SDF TMP_FontAsset from `ttfPath` to `faPath` with the atlas texture + SDF material persisted
    // as sub-assets (mirrors TMP's own Create Font Asset). Shared by the primary editor face (Cascadia) and
    // the CJK fallback (M+) so their bake params CANNOT drift — identical samplingPointSize 90 + padding 9 +
    // 1024² atlas gives a clean SDF spread that reconstructs the outline in-shader (crisp at 0.2×〜5×), and
    // AtlasPopulationMode.Dynamic renders glyphs on demand (atlas starts empty) so the CJK fallback chains
    // in without re-baking. Caller guarantees `ttfPath` is imported with font data embedded.
    static TMP_FontAsset CreateSdfAsset(string ttfPath, string faPath, string assetName, string atlasName)
    {
        var font = AssetDatabase.LoadAssetAtPath<Font>(ttfPath);
        if (font == null) throw new Exception(ttfPath + " not imported as Font (Include Font Data off?)");

        var fa = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024,
                                               AtlasPopulationMode.Dynamic, true);
        if (fa == null) throw new Exception("CreateFontAsset returned null for " + ttfPath + " (Include Font Data off?)");
        fa.name = assetName;

        Directory.CreateDirectory(FaDir);
        AssetDatabase.CreateAsset(fa, faPath);
        if (fa.atlasTextures != null && fa.atlasTextures.Length > 0 && fa.atlasTextures[0] != null)
        {
            fa.atlasTextures[0].name = atlasName;
            AssetDatabase.AddObjectToAsset(fa.atlasTextures[0], fa);
        }
        if (fa.material != null)
            AssetDatabase.AddObjectToAsset(fa.material, fa);

        EditorUtility.SetDirty(fa);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(faPath);
        Debug.Log("[TMP-SETUP] created " + faPath);
        return fa;
    }

    // Generate the M PLUS 1 Code SDF asset (if missing) and chain it onto CascadiaMono SDF's fallback
    // table so the editor + output panes resolve 日本語 glyphs. Idempotent: re-running creates nothing
    // and re-adds nothing once the asset exists and the fallback link is present. The CJK glyphs are
    // NOT pre-baked — AtlasPopulationMode.Dynamic populates the atlas on first use (same as Cascadia).
    static void GenerateJpFallbackIfMissing()
    {
        // THROW, don't log-and-return: a LogError does not change the batchmode exit code, so a silent
        // return would let Run()/Finish() reach Exit(0) with the editor still 日本語-blind (□) — a green
        // verdict for an unapplied fix. A throw propagates to the Exit(1) catch in Run()/OnImportCompleted
        // (and to the menu's own try/catch for the interactive path), so the failure is HONEST.
        if (!File.Exists(JpTtfPath))
            throw new FileNotFoundException(
                "[TMP-SETUP] Japanese fallback font missing — commit " + JpTtfPath +
                " (M+ FONTS, SIL OFL 1.1). The editor stays 日本語-blind (□) without it.", JpTtfPath);

        // Force-import the committed .ttf (mirrors CopyCascadiaFont's ImportAsset) so the importer is ready
        // on a fresh checkout, THEN embed font data — a Dynamic atlas needs the source face loadable at
        // runtime (Player build); without includeFontData the fallback resolves nothing at run time.
        AssetDatabase.ImportAsset(JpTtfPath, ImportAssetOptions.ForceSynchronousImport);
        var jti = AssetImporter.GetAtPath(JpTtfPath) as TrueTypeFontImporter;
        if (jti != null && !jti.includeFontData) { jti.includeFontData = true; jti.SaveAndReimport(); }

        var jpFa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(JpFaPath)
                   ?? CreateSdfAsset(JpTtfPath, JpFaPath, "MPLUS1Code SDF", "MPLUS1Code SDF Atlas");

        // Chain onto CascadiaMono SDF (the font the editor + both output blocks use). The primary face
        // keeps rendering ASCII; TMP only descends into this fallback for glyphs Cascadia lacks (= CJK).
        var code = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FaPath);
        if (code == null)
            throw new Exception("[TMP-SETUP] CascadiaMono SDF not found at " + FaPath +
                                " — run the foundation step (GenerateSdfFontAssetIfMissing) before wiring JP fallback.");
        code.fallbackFontAssetTable ??= new List<TMP_FontAsset>();
        if (!code.fallbackFontAssetTable.Contains(jpFa))
        {
            code.fallbackFontAssetTable.Add(jpFa);
            EditorUtility.SetDirty(code);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(FaPath);
            Debug.Log("[TMP-SETUP] wired MPLUS1Code SDF as CascadiaMono SDF fallback (日本語 now resolvable).");
        }
        else
        {
            Debug.Log("[TMP-SETUP] MPLUS1Code SDF already in CascadiaMono SDF fallback table (no-op).");
        }
    }
}
