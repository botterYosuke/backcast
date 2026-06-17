// MultiDocLayoutProbe.cs — issue #69 AFK gate for the multi-document layout surface (findings 0048).
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod MultiDocLayoutProbe.Run -logFile <log>
//   # expect: [MULTIDOC LAYOUT PASS] ... / exit=0
//
// Locks the deterministic, scene-free contracts the Save As / Open document model rests on:
//   * LayoutSidecarStore round-trips the "layout" key (panels/canvas/windows/editors) losslessly.
//   * scenario ⇄ layout COEXIST in one <strategy>.json — each store preserves the other's key
//     (the merge that makes the 2-file document model safe; the clobber I feared can't happen).
//   * TryReadLayout is the STRICT Open read: false on missing / malformed / no-layout-key, true
//     on a valid layout key (so Open aborts and keeps the workspace, findings 0048 D4).
//   * StrategyDocument.SaveAs writes the new .py, rebinds, and leaves the old file independent.
// The picker UI + root wiring are HITL; THIS logic is AFK-authoritative. Python-FREE, self-cleaning.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class MultiDocLayoutProbe
{
    static string TempRoot => Path.Combine(Application.temporaryCachePath, "multidoc_layout_probe");

    [MenuItem("Probes/Run MultiDoc Layout Probe")]
    public static void Run()
    {
        string fail = null;
        try
        {
            ResetTempDir();
            fail = Section1_LayoutKeyRoundTrip()
                ?? Section2_CoexistBothDirections()
                ?? Section3_TryReadStrictness()
                ?? Section4_StrategyDocumentSaveAs();
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); }

        if (fail == null)
            Debug.Log("[MULTIDOC LAYOUT PASS] layout-key round-trip + scenario⇄layout coexist (no clobber) " +
                "+ TryReadLayout strictness (missing/malformed/no-key→false, valid→true) + StrategyDocument.SaveAs");
        else
            Debug.LogError("[MULTIDOC LAYOUT FAIL] " + fail);
    }

    // ── S1: WriteLayout → TryReadLayout reproduces the layout dimensions ──
    static string Section1_LayoutKeyRoundTrip()
    {
        string py = Path.Combine(TempRoot, "s1", "strat.py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));

        var doc = MakeDoc(py, panX: 12.5f, panY: -7.5f, zoom: 1.5f, winId: "w1");
        LayoutSidecarStore.WriteLayout(py, doc);

        if (!File.Exists(LayoutSidecarStore.SidecarPathFor(py)))
            return "S1: sidecar <strategy>.json was not created next to the .py";
        if (!LayoutSidecarStore.TryReadLayout(py, out var got) || got == null)
            return "S1: TryReadLayout returned false on a sidecar we just wrote";
        if (got.version != LayoutDocument.CURRENT_VERSION)
            return "S1: version not round-tripped (got " + got.version + ")";
        if (got.canvasView == null || Mathf.Abs(got.canvasView.panX - 12.5f) > 1e-3f || Mathf.Abs(got.canvasView.zoom - 1.5f) > 1e-3f)
            return "S1: canvasView not round-tripped";
        if (got.floatingWindows == null || got.floatingWindows.Count != 1 || got.floatingWindows[0].id != "w1")
            return "S1: floatingWindows not round-tripped";
        if (got.strategyEditors == null || got.strategyEditors.Count != 1 || got.strategyEditors[0].filePath != Path.GetFullPath(py))
            return "S1: strategyEditors filePath not round-tripped";
        return null;
    }

    // ── S2: scenario and layout keys coexist; writing one preserves the other (both orders) ──
    static string Section2_CoexistBothDirections()
    {
        // (a) scenario FIRST, then layout — the layout write must preserve the scenario key.
        string pyA = Path.Combine(TempRoot, "s2a", "strat.py");
        Directory.CreateDirectory(Path.GetDirectoryName(pyA));
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            pyA, new StartupParamsForWrite("2025-01-06", "2025-01-10", "Minute", "1000000"),
            new List<string> { "7203.TSE" });
        LayoutSidecarStore.WriteLayout(pyA, MakeDoc(pyA, 1f, 2f, 1f, "wa"));

        var scnA = ScenarioSidecarStore.ReadScenario(pyA);
        if (scnA == null || scnA.Instruments.Count != 1 || scnA.Instruments[0] != "7203.TSE")
            return "S2a: layout write CLOBBERED the scenario key";
        if (!LayoutSidecarStore.TryReadLayout(pyA, out _))
            return "S2a: layout key missing after coexist write";

        // (b) layout FIRST, then scenario — the scenario write must preserve the layout key.
        string pyB = Path.Combine(TempRoot, "s2b", "strat.py");
        Directory.CreateDirectory(Path.GetDirectoryName(pyB));
        LayoutSidecarStore.WriteLayout(pyB, MakeDoc(pyB, 9f, 8f, 2f, "wb"));
        ScenarioSidecarStore.SetStartupParamsAndInstruments(
            pyB, new StartupParamsForWrite("2025-02-01", "2025-02-05", "Daily", "500000"),
            new List<string> { "6758.TSE" });

        if (!LayoutSidecarStore.TryReadLayout(pyB, out var gotB) || gotB.floatingWindows[0].id != "wb")
            return "S2b: scenario write CLOBBERED the layout key";
        var scnB = ScenarioSidecarStore.ReadScenario(pyB);
        if (scnB == null || scnB.Instruments.Count != 1 || scnB.Instruments[0] != "6758.TSE")
            return "S2b: scenario key missing after coexist write";
        return null;
    }

    // ── S3: TryReadLayout strictness — the Open path's abort signal (findings 0048 D4) ──
    static string Section3_TryReadStrictness()
    {
        string dir = Path.Combine(TempRoot, "s3");
        Directory.CreateDirectory(dir);

        // (a) no sidecar at all → false (Open aborts, keeps the workspace).
        string missing = Path.Combine(dir, "missing.py");
        if (LayoutSidecarStore.TryReadLayout(missing, out _))
            return "S3a: TryReadLayout returned true for a missing sidecar";

        // (b) malformed JSON → false.
        string badPy = Path.Combine(dir, "bad.py");
        File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not json");
        if (LayoutSidecarStore.TryReadLayout(badPy, out _))
            return "S3b: TryReadLayout returned true for malformed JSON";

        // (c) valid JSON but NO layout key (scenario-only sidecar) → false (nothing to apply).
        string scnPy = Path.Combine(dir, "scnonly.py");
        File.WriteAllText(Path.Combine(dir, "scnonly.json"),
            "{\"scenario\":{\"schema_version\":3,\"instruments\":[\"7203.TSE\"]}}");
        if (LayoutSidecarStore.TryReadLayout(scnPy, out _))
            return "S3c: TryReadLayout returned true for a sidecar with no layout key";

        // (d) valid layout key → true.
        string okPy = Path.Combine(dir, "ok.py");
        LayoutSidecarStore.WriteLayout(okPy, MakeDoc(okPy, 0f, 0f, 1f, "wd"));
        if (!LayoutSidecarStore.TryReadLayout(okPy, out var ok) || ok == null)
            return "S3d: TryReadLayout returned false for a valid layout key";

        // (e) PRESENT-but-invalid layout key (empty / version<=0) → false. Strictness must NOT degrade
        // to Default()+true here (that would let Open WIPE the live workspace — findings 0048 D4).
        string emptyPy = Path.Combine(dir, "emptylayout.py");
        File.WriteAllText(Path.Combine(dir, "emptylayout.json"), "{\"layout\":{}}");
        if (LayoutSidecarStore.TryReadLayout(emptyPy, out _))
            return "S3e: TryReadLayout returned true for an empty layout key (must abort, not Default-wipe)";
        string v0Py = Path.Combine(dir, "v0layout.py");
        File.WriteAllText(Path.Combine(dir, "v0layout.json"), "{\"layout\":{\"version\":0}}");
        if (LayoutSidecarStore.TryReadLayout(v0Py, out _))
            return "S3e: TryReadLayout returned true for a version<=0 layout key";
        return null;
    }

    // ── S4: StrategyDocument.SaveAs writes the new .py, rebinds, leaves the old file independent ──
    static string Section4_StrategyDocumentSaveAs()
    {
        string dir = Path.Combine(TempRoot, "s4");
        Directory.CreateDirectory(dir);
        string oldPy = Path.Combine(dir, "old.py");
        File.WriteAllText(oldPy, "# old body\n");

        var doc = new StrategyDocument();
        if (!doc.Open(oldPy)) return "S4: Open(old.py) failed";
        doc.SetText("# forked body\n");

        string newPy = Path.Combine(dir, "new.py");
        if (!doc.SaveAs(newPy)) return "S4: SaveAs(new.py) returned false";
        if (doc.CurrentPath != Path.GetFullPath(newPy)) return "S4: SaveAs did not rebind to the new path";
        if (File.ReadAllText(newPy) != "# forked body\n") return "S4: new .py content wrong";
        if (File.ReadAllText(oldPy) != "# old body\n") return "S4: SaveAs mutated the OLD file (must be independent)";

        // non-.py target is rejected (document unchanged, still bound to new.py).
        if (doc.SaveAs(Path.Combine(dir, "notpy.txt"))) return "S4: SaveAs accepted a non-.py path";
        if (doc.CurrentPath != Path.GetFullPath(newPy)) return "S4: a rejected SaveAs changed the bound path";
        return null;
    }

    // ---- helpers ----
    static LayoutDocument MakeDoc(string py, float panX, float panY, float zoom, string winId)
    {
        return new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),
            canvasView = new CanvasView(panX, panY, zoom),
            floatingWindows = new List<FloatingWindowLayout>
            {
                new FloatingWindowLayout(winId, "StrategyEditor", 10f, 20f, 300f, 200f, 0, true),
            },
            strategyEditors = new List<StrategyEditorState>
            {
                new StrategyEditorState("strategy_editor:region_001", Path.GetFullPath(py)),
            },
        };
    }

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
