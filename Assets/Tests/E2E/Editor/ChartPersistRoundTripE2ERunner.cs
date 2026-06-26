// ChartPersistRoundTripE2ERunner.cs — H6 / S7 #162 follow-up: end-to-end persistence round-trip
// via REAL BackcastWorkspaceRoot. The standalone-ChartView round-trip in
// ChartViewStatePersistenceE2ERunner.Section1 doesn't exercise the BackcastWorkspaceRoot apply
// path (CaptureChartViewStates / ApplyChartViewStates); this runner closes that gap.
//
//   <Unity> -batchmode -nographics -quit -projectPath . \
//           -executeMethod ChartPersistRoundTripE2ERunner.Run -logFile <log>
//   # expect: [E2E CHART PERSIST ROUND PASS] / exit=0
//
// SCOPE: 1 chart-window per iid (owner-confirmed v1 constraint — _chartViews keyed by chart:<iid>
// only allows one). Multi-window-per-iid is a v2 slice.

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class ChartPersistRoundTripE2ERunner
{
    const BindingFlags BF = BindingFlags.NonPublic | BindingFlags.Instance;
    const string IID = "7203.TSE";
    const int TOTAL_BARS = 1000;

    static string TempRoot => Path.Combine(Application.temporaryCachePath, "chart_persist_roundtrip_e2e");

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = Section1_RoundTripViaRoot();   // CHART-PERSIST-ROUND-01
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); }

        if (fail == null)
        {
            Debug.Log("[E2E CHART PERSIST ROUND PASS] (CHART-PERSIST-ROUND-01) round-trip via real BackcastWorkspaceRoot lifecycle.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART PERSIST ROUND FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    // ── CHART-PERSIST-ROUND-01: real-root capture → sidecar → re-build → real-root apply round-trip. ──
    static string Section1_RoundTripViaRoot()
    {
        // (1) build real root, seed universe → chart spawns; render bars + pan/zoom; capture state.
        string err = BuildRoot(out var root, out var chartViews);
        if (err != null) return err;
        var cv = chartViews[IID] as ChartView;
        if (cv == null) return "S1: chart for " + IID + " not spawned by SyncChartWindowsToUniverse";

        cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(TOTAL_BARS) });
        Canvas.ForceUpdateCanvases();
        cv.PanByPixels(-180f);
        cv.ZoomByScroll(2f, 200f);
        long expectedTranslation = cv.ViewState.translation_ms;
        float expectedCellWidth = cv.ViewState.cell_width_px;
        bool expectedAuto = cv.ViewState.auto_scale;
        if (expectedAuto) return "S1: precondition — auto_scale should be false after pan+zoom";
        if (Mathf.Approximately(expectedCellWidth, ChartViewState.DEFAULT_CELL_WIDTH_PX))
            return "S1: precondition — cell_width must differ from DEFAULT after zoom";

        // Build a layout doc via reflection: CaptureLayout() builds the FW list (including the
        // chart:<iid> entry from the spawn), then CaptureChartViewStates(doc) fills chart_view_state
        // from the live ChartView. WriteLayout serializes to the sidecar next to our stub .py.
        var rootTy = typeof(BackcastWorkspaceRoot);
        var captureLayout = rootTy.GetMethod("CaptureLayout", BF);
        var captureChartViewStates = rootTy.GetMethod("CaptureChartViewStates", BF);
        var applyChartViewStates = rootTy.GetMethod("ApplyChartViewStates", BF);
        if (captureLayout == null || captureChartViewStates == null || applyChartViewStates == null)
            return "S1: CaptureLayout / CaptureChartViewStates / ApplyChartViewStates not found (renamed?)";

        var doc = captureLayout.Invoke(root, null) as LayoutDocument;
        if (doc == null) return "S1: CaptureLayout returned null";
        captureChartViewStates.Invoke(root, new object[] { doc });

        // Sanity: the chart:<iid> FW exists AND its chart_view_state was just stamped by Capture.
        string chartId = "chart:" + IID;
        var fw = doc.FindWindow(chartId);
        if (fw == null) return "S1: " + chartId + " missing from CaptureLayout (chart window not in floatingWindows)";
        if (fw.chart_view_state == null)
            return "S1: chart_view_state null after CaptureChartViewStates — the chart_view_state stamp didn't run for " + chartId;
        if (fw.chart_view_state.translation_ms != expectedTranslation)
            return "S1: captured translation_ms=" + fw.chart_view_state.translation_ms + " != live " + expectedTranslation;
        if (Mathf.Abs(fw.chart_view_state.cell_width_px - expectedCellWidth) > 1e-3f)
            return "S1: captured cell_width_px=" + fw.chart_view_state.cell_width_px + " != live " + expectedCellWidth;

        string py = WriteStubStrategy("roundtrip");
        LayoutSidecarStore.WriteLayout(py, doc);

        // (2) tear root down by reopening the scene; the new BackcastWorkspaceRoot is a fresh
        // instance with empty _chartViews. Re-build, re-seed the same universe so the chart respawns
        // at ResetView() defaults.
        err = BuildRoot(out var root2, out var chartViews2);
        if (err != null) return "S1 (rebuild): " + err;
        var cv2 = chartViews2[IID] as ChartView;
        if (cv2 == null) return "S1 (rebuild): chart for " + IID + " not respawned";
        // Render some bars so basis_ms is non-null (Apply path uses translation/cell_width directly).
        cv2.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(TOTAL_BARS) });
        Canvas.ForceUpdateCanvases();
        if (!cv2.ViewState.auto_scale)
            return "S1 (rebuild): fresh chart should have auto_scale=true after Render — preconditions";
        if (cv2.ViewState.translation_ms == expectedTranslation)
            return "S1 (rebuild): fresh chart somehow already at the saved translation — Apply round-trip is vacuous";

        // (3) read sidecar back, hand it to ApplyChartViewStates via reflection, assert the fresh
        // ChartView now matches the captured pan/zoom state.
        if (!LayoutSidecarStore.TryReadLayout(py, out var doc2) || doc2 == null)
            return "S1 (rebuild): TryReadLayout returned false on the sidecar we just wrote";
        var fw2 = doc2.FindWindow(chartId);
        if (fw2 == null) return "S1 (rebuild): " + chartId + " lost in sidecar round-trip";
        if (fw2.chart_view_state == null)
            return "S1 (rebuild): chart_view_state null after sidecar round-trip — JsonUtility binding regression";

        applyChartViewStates.Invoke(root2, new object[] { doc2 });

        if (cv2.ViewState.translation_ms != expectedTranslation)
            return "S1 (apply): translation_ms=" + cv2.ViewState.translation_ms + " != saved " + expectedTranslation
                 + " — ApplyChartViewStates didn't restore on a real BackcastWorkspaceRoot";
        if (Mathf.Abs(cv2.ViewState.cell_width_px - expectedCellWidth) > 1e-3f)
            return "S1 (apply): cell_width_px=" + cv2.ViewState.cell_width_px + " != saved " + expectedCellWidth;
        if (cv2.ViewState.auto_scale != expectedAuto)
            return "S1 (apply): auto_scale=" + cv2.ViewState.auto_scale + " != saved " + expectedAuto;

        Debug.Log("[E2E CHART-PERSIST-ROUND-01 PASS] BackcastWorkspaceRoot CaptureChartViewStates → sidecar → re-build → ApplyChartViewStates round-trips translation_ms/cell_width_px/auto_scale.");
        return null;
    }

    // ---- helpers ----

    // Build the REAL BackcastWorkspaceRoot headlessly (mirrors DepthLadderE2ERunner.BuildRoot pattern).
    // Re-opens the scene each call so the prior root is torn down and a fresh instance is constructed —
    // exactly what the apply-side leg of the round-trip needs.
    static string BuildRoot(out BackcastWorkspaceRoot root, out IDictionary chartViews)
    {
        chartViews = null;
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "build: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        chartViews = ty.GetField("_chartViews", BF).GetValue(root) as IDictionary;
        if (scenario == null || chartViews == null) return "build: root internals not found (renamed?)";

        // Replace universe → InstrumentRegistry.Changed → SyncChartWindowsToUniverse spawns chart:<iid>.
        scenario.Universe.ReplaceAll(new[] { IID });
        if (!chartViews.Contains(IID))
            return "build: chart " + IID + " not spawned after Universe.ReplaceAll (SyncChartWindowsToUniverse regression?)";
        return null;
    }

    static OhlcPoint[] SyntheticDaily(int n)
    {
        var arr = new OhlcPoint[n];
        long startMs = 1_700_000_000_000L;
        double basePrice = 100.0;
        for (int i = 0; i < n; i++)
        {
            double drift = ((i % 7) - 3) * 0.5;
            double o = basePrice + drift;
            double c = basePrice + drift + (i % 2 == 0 ? +0.3 : -0.3);
            arr[i] = new OhlcPoint
            {
                open_time_ms = startMs + (long)i * ChartViewState.BASIS_DAILY_MS,
                open = o, close = c,
                high = Math.Max(o, c) + 0.4, low = Math.Min(o, c) - 0.4, volume = 1000.0 + i,
            };
        }
        return arr;
    }

    static string WriteStubStrategy(string name)
    {
        string py = Path.Combine(TempRoot, name, name + ".py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "x = 1\n");
        return py;
    }

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
