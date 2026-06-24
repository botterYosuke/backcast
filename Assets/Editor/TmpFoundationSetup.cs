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
//
// Run as ONE batchmode pass WITHOUT -quit (AssetDatabase.ImportPackage is ASYNC even with
// interactive:false — a -quit fires before the import worker writes the assets, leaving TMP_Settings
// null and CreateFontAsset throwing). We wait on importPackageCompleted, then generate the font asset,
// then EditorApplication.Exit from the callback:
//   <Unity> -batchmode -nographics -projectPath <abs> -executeMethod TmpFoundationSetup.Run -logFile <abs>
//   # NOTE: no -quit.  expect: [TMP-SETUP DONE] / exit=0
//
// Idempotent: if TMP Settings + the SDF asset already exist, it no-ops and exits immediately.

using System;
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

        var font = AssetDatabase.LoadAssetAtPath<Font>(TtfPath);
        if (font == null) throw new Exception("CascadiaMono.ttf not imported as Font");

        // samplingPointSize 90 + padding 9 + 1024² atlas = a clean SDF spread; SDF reconstructs the
        // outline in-shader so this is crisp at 5× as well as 0.2×. Dynamic population renders glyphs
        // on demand (atlas starts empty), so a future CJK/IME fallback can chain in without re-baking.
        var fa = TMP_FontAsset.CreateFontAsset(font, 90, 9, GlyphRenderMode.SDFAA, 1024, 1024,
                                               AtlasPopulationMode.Dynamic, true);
        if (fa == null) throw new Exception("CreateFontAsset returned null (Include Font Data off?)");
        fa.name = "CascadiaMono SDF";

        Directory.CreateDirectory(FaDir);
        AssetDatabase.CreateAsset(fa, FaPath);

        // Persist the atlas texture + SDF material as sub-assets (mirrors TMP's own Create Font Asset).
        if (fa.atlasTextures != null && fa.atlasTextures.Length > 0 && fa.atlasTextures[0] != null)
        {
            fa.atlasTextures[0].name = "CascadiaMono SDF Atlas";
            AssetDatabase.AddObjectToAsset(fa.atlasTextures[0], fa);
        }
        if (fa.material != null)
            AssetDatabase.AddObjectToAsset(fa.material, fa);

        EditorUtility.SetDirty(fa);
        AssetDatabase.SaveAssets();
        AssetDatabase.ImportAsset(FaPath);
        Debug.Log("[TMP-SETUP] created " + FaPath);
    }
}
