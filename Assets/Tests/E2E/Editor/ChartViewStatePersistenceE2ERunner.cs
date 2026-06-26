// ChartViewStatePersistenceE2ERunner.cs — S7 #162 / ADR-0034 §7 / findings 0119 D-7: pan/zoom
// state persistence to the layout sidecar (per-chart-window slot)。台本: same-dir
// ChartViewStatePersistenceE2ERunner.md。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartViewStatePersistenceE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART VIEWSTATE PERSIST PASS] / exit=0
//
// WHAT THIS GATES:
//   CHART-PERSIST-01: capture a ChartView ViewState into FloatingWindowLayout.chart_view_state, write +
//               read the sidecar, ApplyViewStateLayout restores translation_ms / cell_width_px /
//               auto_scale verbatim (within float eps for cell_width). Round-trip pin.
//   CHART-PERSIST-02: a sidecar with version=1 (no chart_view_state field on any FW) loads fine — the
//               LayoutDocument's CURRENT_VERSION=2 forward-tolerance leaves chart_view_state=null
//               on each FW; ApplyViewStateLayout(null) is a no-op so freshly-spawned chart keeps
//               ResetView() defaults (auto_scale=true, cell_width=DEFAULT). Old sidecar non-destructive.
//   CHART-PERSIST-03: 2 chart windows of the SAME instrument with DIFFERENT pan/zoom states → 2 separate
//               FloatingWindowLayout entries, each with its own chart_view_state, both round-trip
//               independently. Per-window independence pin.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public static class ChartViewStatePersistenceE2ERunner
{
    static string TempRoot => Path.Combine(Application.temporaryCachePath, "chart_view_state_persist_e2e");

    public static void Run()
    {
        string fail;
        try
        {
            ResetTempDir();
            fail = Section1_RoundTrip()
                ?? Section2_LegacySidecarV1Migrate()
                ?? Section3_PerWindowSidecarSchemaHealth();
        }
        catch (Exception e) { fail = "driver: " + e; }
        finally { TryDeleteDir(TempRoot); }

        if (fail == null)
        {
            Debug.Log("[E2E CHART VIEWSTATE PERSIST PASS] (CHART-PERSIST-01) ViewState round-trips through "
                    + "LayoutSidecarStore; (CHART-PERSIST-02) version=1 sidecar loads cleanly with chart_view_state=null "
                    + "→ ChartView keeps ResetView() defaults; (CHART-PERSIST-03) 2 chart windows on the same iid each "
                    + "round-trip an independent chart_view_state.");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART VIEWSTATE PERSIST FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Section1_RoundTrip()
    {
        var cv = BuildStandaloneChart(out var canvasGo);
        try
        {
            // Pan + zoom to a non-default state.
            cv.PanByPixels(-120f);
            cv.ZoomByScroll(2f, 200f);
            long t0 = cv.ViewState.translation_ms;
            float cw0 = cv.ViewState.cell_width_px;
            bool as0 = cv.ViewState.auto_scale;

            // Build a layout doc containing a chart FW + the captured state.
            string py = WriteStubStrategy("roundtrip");
            var doc = NewDocWithChart("chart:7203.TSE", cv.CaptureViewStateLayout());
            LayoutSidecarStore.WriteLayout(py, doc);

            // Read back, find the chart FW, apply to a fresh chart.
            if (!LayoutSidecarStore.TryReadLayout(py, out var roundTrip) || roundTrip == null)
                return "S1 CHART-PERSIST-01: TryReadLayout returned false on a sidecar we just wrote";
            var fw = roundTrip.FindWindow("chart:7203.TSE");
            if (fw == null) return "S1 CHART-PERSIST-01: FW chart:7203.TSE missing in round-trip doc";
            if (fw.chart_view_state == null)
                return "S1 CHART-PERSIST-01: chart_view_state lost in JsonUtility round-trip (null on read)";
            if (fw.chart_view_state.translation_ms != t0)
                return "S1 CHART-PERSIST-01: translation_ms drift " + fw.chart_view_state.translation_ms + " != " + t0;
            if (Mathf.Abs(fw.chart_view_state.cell_width_px - cw0) > 1e-3f)
                return "S1 CHART-PERSIST-01: cell_width_px drift " + fw.chart_view_state.cell_width_px + " != " + cw0;
            if (fw.chart_view_state.auto_scale != as0)
                return "S1 CHART-PERSIST-01: auto_scale drift " + fw.chart_view_state.auto_scale + " != " + as0;

            // Apply to a fresh chart and confirm restore.
            var cv2 = BuildStandaloneChart(out var canvasGo2);
            try
            {
                cv2.ApplyViewStateLayout(fw.chart_view_state);
                if (cv2.ViewState.translation_ms != t0)
                    return "S1 CHART-PERSIST-01: restored translation_ms wrong";
                if (Mathf.Abs(cv2.ViewState.cell_width_px - cw0) > 1e-3f)
                    return "S1 CHART-PERSIST-01: restored cell_width_px wrong";
                if (cv2.ViewState.auto_scale != as0)
                    return "S1 CHART-PERSIST-01: restored auto_scale wrong";
            }
            finally { UnityEngine.Object.DestroyImmediate(canvasGo2); }
            Debug.Log("[E2E CHART-PERSIST-01 PASS] ViewState round-trips through LayoutSidecarStore with translation_ms/cell_width_px/auto_scale verbatim.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    static string Section2_LegacySidecarV1Migrate()
    {
        // Construct a sidecar with version=1, FW chart entry, and NO chart_view_state field present in
        // JSON (legacy shape from before S7 #162). LayoutDocument v2 reader must load it cleanly and
        // leave chart_view_state=null on the FW (JsonUtility default for reference type).
        string py = WriteStubStrategy("legacy");
        // Hand-write a v1 shape with chart_view_state missing entirely.
        string legacyJson =
            "{\n"
            + "  \"layout\": {\n"
            + "    \"version\": 1,\n"
            + "    \"panels\": [],\n"
            + "    \"floatingWindows\": [\n"
            + "      { \"id\": \"chart:7203.TSE\", \"kind\": \"chart\", \"x\": 0, \"y\": 0, \"w\": 520, \"h\": 360, \"zOrder\": 0, \"visible\": true, \"groupId\": null }\n"
            + "    ],\n"
            + "    \"strategyEditors\": [],\n"
            + "    \"cellPositions\": []\n"
            + "  }\n"
            + "}";
        File.WriteAllText(LayoutSidecarStore.SidecarPathFor(py), legacyJson);
        if (!LayoutSidecarStore.TryReadLayout(py, out var doc) || doc == null)
            return "S2 CHART-PERSIST-02: legacy v1 sidecar failed to load — version-tolerance regression";
        var fw = doc.FindWindow("chart:7203.TSE");
        if (fw == null) return "S2 CHART-PERSIST-02: legacy FW chart:7203.TSE missing after load";
        // JsonUtility quirk: a missing reference-type field hydrates to a new instance with all-zero
        // fields (NOT null). cell_width_px=0 is our "not actually captured" sentinel (real captures
        // clamp cell_width to ≥ MIN=1.0). ApplyViewStateLayout must treat this as a no-op so the
        // fresh chart keeps the ResetView() defaults BuildChartContent left it at.
        if (fw.chart_view_state == null)
        {
            // The day JsonUtility changes its behavior and surfaces null instead, that's also fine
            // (still CHART-PERSIST-02 conformant) — the null guard in ApplyViewStateLayout will no-op.
        }
        else if (fw.chart_view_state.cell_width_px >= 1f)
        {
            return "S2 CHART-PERSIST-02: legacy v1 sidecar somehow surfaced a REAL cell_width_px="
                 + fw.chart_view_state.cell_width_px
                 + " — should be the JsonUtility-zero sentinel (or null) on missing field";
        }

        // ApplyViewStateLayout(zero-sentinel or null) on a fresh chart must be a no-op.
        var cv = BuildStandaloneChart(out var canvasGo);
        try
        {
            float cwBefore = cv.ViewState.cell_width_px;
            bool asBefore = cv.ViewState.auto_scale;
            long tBefore = cv.ViewState.translation_ms;
            cv.ApplyViewStateLayout(fw.chart_view_state);   // zero-sentinel / null → no-op
            if (cv.ViewState.cell_width_px != cwBefore || cv.ViewState.auto_scale != asBefore || cv.ViewState.translation_ms != tBefore)
                return "S2 CHART-PERSIST-02: ApplyViewStateLayout(sentinel) mutated state from cw="
                     + cwBefore + " ts=" + tBefore + " auto=" + asBefore + " — sentinel guard missing";
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
        Debug.Log("[E2E CHART-PERSIST-02 PASS] legacy v1 sidecar loads cleanly; JsonUtility-zero sentinel ApplyViewStateLayout is a no-op.");
        return null;
    }

    static string Section3_PerWindowSidecarSchemaHealth()
    {
        // v1 では production は 1 chart-window/iid（BackcastWorkspaceRoot._chartViews Dictionary keyed by
        // chart:<iid>）。本 section は **sidecar schema** が複数 FW entry に独立 chart_view_state を持てる
        // shape healthy かを確認する。owner 承認: v2 で chart:<iid>#<n> 拡張するときは production と本
        // section を同期して更新する。
        var cvA = BuildStandaloneChart(out var canvasA);
        var cvB = BuildStandaloneChart(out var canvasB);
        try
        {
            cvA.PanByPixels(-100f); cvA.ZoomByScroll(1f, 100f);
            cvB.PanByPixels(-300f); cvB.ZoomByScroll(3f, 200f);
            long tA = cvA.ViewState.translation_ms;
            long tB = cvB.ViewState.translation_ms;
            float cwA = cvA.ViewState.cell_width_px;
            float cwB = cvB.ViewState.cell_width_px;
            if (tA == tB) return "S3 CHART-PERSIST-03: precondition — translations should differ";

            // Build a doc with TWO chart FW entries on the same iid (different window ids).
            string py = WriteStubStrategy("perwindow");
            var doc = NewDocWithCharts(new[]
            {
                ("chart:7203.TSE", cvA.CaptureViewStateLayout()),
                ("chart:7203.TSE#2", cvB.CaptureViewStateLayout()),
            });
            LayoutSidecarStore.WriteLayout(py, doc);

            if (!LayoutSidecarStore.TryReadLayout(py, out var rt) || rt == null)
                return "S3 CHART-PERSIST-03: TryReadLayout returned false";
            var fwA = rt.FindWindow("chart:7203.TSE");
            var fwB = rt.FindWindow("chart:7203.TSE#2");
            if (fwA == null || fwB == null) return "S3 CHART-PERSIST-03: a FW lost on round-trip";
            if (fwA.chart_view_state == null || fwB.chart_view_state == null)
                return "S3 CHART-PERSIST-03: chart_view_state missing on one window after round-trip";
            if (fwA.chart_view_state.translation_ms != tA)
                return "S3 CHART-PERSIST-03: A translation drift";
            if (fwB.chart_view_state.translation_ms != tB)
                return "S3 CHART-PERSIST-03: B translation drift";
            if (Mathf.Abs(fwA.chart_view_state.cell_width_px - cwA) > 1e-3f
                || Mathf.Abs(fwB.chart_view_state.cell_width_px - cwB) > 1e-3f)
                return "S3 CHART-PERSIST-03: cell_width per-window drift";
            if (fwA.chart_view_state.translation_ms == fwB.chart_view_state.translation_ms)
                return "S3 CHART-PERSIST-03: per-window states collapsed into one — independence broken";
            Debug.Log("[E2E CHART-PERSIST-03 PASS] sidecar schema supports independent chart_view_state per FW entry (v1 contract: 1 chart-window per iid; v2 will extend chart:<iid>#<n>).");
            return null;
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(canvasA);
            UnityEngine.Object.DestroyImmediate(canvasB);
        }
    }

    // ---- helpers ----

    static ChartView BuildStandaloneChart(out GameObject canvasGo)
    {
        canvasGo = new GameObject("ChartPersistCanvas", typeof(Canvas));
        var hostGo = new GameObject("ChartHost", typeof(RectTransform));
        var host = hostGo.GetComponent<RectTransform>();
        host.SetParent(canvasGo.transform, false);
        host.anchorMin = new Vector2(0.5f, 0.5f); host.anchorMax = new Vector2(0.5f, 0.5f);
        host.pivot = new Vector2(0.5f, 0.5f);
        host.sizeDelta = new Vector2(620f, 400f);
        var cv = hostGo.AddComponent<ChartView>();
        cv.Build(host, showTitleBar: false);
        // Seed bars so PanByPixels/ZoomByScroll have a basis_ms to work with.
        var bars = new OhlcPoint[200];
        long startMs = 1_700_000_000_000L;
        for (int i = 0; i < bars.Length; i++)
            bars[i] = new OhlcPoint
            {
                open_time_ms = startMs + (long)i * ChartViewState.BASIS_DAILY_MS,
                open = 100, close = 101, high = 102, low = 99, volume = 1000,
            };
        cv.Render(new ReplayBarFrame { Ohlc = bars });
        Canvas.ForceUpdateCanvases();
        return cv;
    }

    static string WriteStubStrategy(string name)
    {
        string py = Path.Combine(TempRoot, name, name + ".py");
        Directory.CreateDirectory(Path.GetDirectoryName(py));
        File.WriteAllText(py, "x = 1\n");
        return py;
    }

    static LayoutDocument NewDocWithChart(string chartId, ChartViewStateLayout cvs) =>
        NewDocWithCharts(new[] { (chartId, cvs) });

    static LayoutDocument NewDocWithCharts((string id, ChartViewStateLayout cvs)[] charts)
    {
        var fws = new List<FloatingWindowLayout>();
        for (int i = 0; i < charts.Length; i++)
        {
            var fw = new FloatingWindowLayout(charts[i].id, FloatingWindowCatalog.KIND_CHART,
                100f + i * 50, 100f + i * 50, 520f, 360f, i, true);
            fw.chart_view_state = charts[i].cvs;
            fws.Add(fw);
        }
        return new LayoutDocument
        {
            version = LayoutDocument.CURRENT_VERSION,
            panels = new List<PanelLayout>(),
            hakoniwaProfiles = null,
            canvasView = null,
            floatingWindows = fws,
            strategyEditors = new List<StrategyEditorState>(),
            cellPositions = new List<CellPosition>(),
        };
    }

    static void ResetTempDir() { TryDeleteDir(TempRoot); Directory.CreateDirectory(TempRoot); }
    static void TryDeleteDir(string d) { try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { } }
}
