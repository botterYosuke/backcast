// WorkspaceDepthLadderProbe.cs — issue #57 "orderbook: DepthLadderView を本線 scene に載せ替え"
//
// The headless, Python-FREE AFK gate for the #57 NEW seams (findings 0028 §2). Run:
//
//   <Unity> -batchmode -nographics -projectPath <proj> -executeMethod WorkspaceDepthLadderProbe.Run -logFile <log>
//   # expect: [WORKSPACE DEPTH LADDER PASS] ... / exit=0
//   # (judge by `error CS` 0 + the PASS line — grep -c "error CS" returns exit 1 on 0 matches.)
//
// SECTIONS (findings 0028 §2):
//   1. InstrumentPriceDecoder: per-id price pulled past a decoy string, null on absent/null/non-number,
//      FormatException on malformed (the shared-locator contract, sibling of DepthDecoder/Ohlc).
//   2. mount: each universe instrument's chart tile carries a DepthLadderView in a right strip + a
//      sibling chartArea that hosts the ChartView (drives the REAL root headlessly).
//   3. mode-sync (D1/D3): Replay = ladder hidden + chart full width; Live = ladder shown + chart inset
//      by LADDER_WIDTH.
//   4. per-instrument render (D2/D4): an X-has-depth / Y-no-depth payload renders X's board (best
//      bid/ask + LAST from per_instrument[X].price) while Y stays a placeholder — X's board is NOT
//      shared to Y (single-global regression kill).

using System;
using System.Collections;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class WorkspaceDepthLadderProbe
{
    const float EPS = 1e-3f;
    const float LADDER_WIDTH = 120f;   // mirror of BackcastWorkspaceRoot.LADDER_WIDTH (TTWR viewstate)
    const System.Reflection.BindingFlags BF =
        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    public static void Run()
    {
        string fail = null;
        try
        {
            fail = Section1_PriceDecoder()
                ?? Section2to4_MountModeRender();
        }
        catch (Exception e)
        {
            fail = "driver: " + e;
        }

        if (fail == null)
        {
            Debug.Log("[WORKSPACE DEPTH LADDER PASS] price decode + per-tile mount + Live/Replay mode-sync + per-instrument render verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[WORKSPACE DEPTH LADDER FAIL] " + fail);
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
    }

    // ── 1. InstrumentPriceDecoder (shared locator: decoy / null / absent / non-number / malformed) ──
    static string Section1_PriceDecoder()
    {
        // X has a real number price + depth; Y has price but depth null; Z has a NON-number price
        // (string); a decoy "price" lives inside a string value and must not fool the scanner.
        const string state =
            "{\"price\":1.0,\"live_last_error\":\"spurious price: 999.99 in a string\"," +
            "\"per_instrument\":{" +
              "\"X.TSE\":{\"price\":105.5,\"ohlc_points\":null,\"depth\":{\"bids\":[{\"price\":99.0,\"size\":5.0}],\"asks\":[{\"price\":101.0,\"size\":3.0}],\"timestamp_ms\":7}}," +
              "\"Y.TSE\":{\"price\":20.0,\"ohlc_points\":null,\"depth\":null}," +
              "\"Z.TSE\":{\"price\":\"oops\",\"depth\":null}" +
            "}}";

        var x = InstrumentPriceDecoder.Decode(state, "X.TSE");
        if (!x.HasValue || Math.Abs(x.Value - 105.5) > EPS) return "price: X.TSE wrong (decoy leak or miss)";
        var y = InstrumentPriceDecoder.Decode(state, "Y.TSE");
        if (!y.HasValue || Math.Abs(y.Value - 20.0) > EPS) return "price: Y.TSE wrong";

        // non-number price → null (typed mismatch, not a crash).
        if (InstrumentPriceDecoder.Decode(state, "Z.TSE").HasValue) return "price: string price must be null";
        // absent id / no per_instrument / "null" / whitespace → null (no throw).
        if (InstrumentPriceDecoder.Decode(state, "0000.TSE").HasValue) return "price: absent id must be null";
        if (InstrumentPriceDecoder.Decode("{\"price\":1}", "X.TSE").HasValue) return "price: no per_instrument must be null";
        if (InstrumentPriceDecoder.Decode("null", "X.TSE").HasValue) return "price: \"null\" must be null";
        if (InstrumentPriceDecoder.Decode("   ", "X.TSE").HasValue) return "price: whitespace must be null";

        // MALFORMED structure while navigating → FormatException (NOT swallowed).
        bool threw = false;
        try { InstrumentPriceDecoder.Decode("{\"per_instrument\":\"oops\"}", "X.TSE"); }
        catch (FormatException) { threw = true; }
        if (!threw) return "price: malformed (per_instrument not an object) must throw FormatException";
        return null;
    }

    // ── 2-4. drive the REAL root headlessly: build it, spawn 2 chart tiles, then assert mount /
    // mode-sync / per-instrument render via the private seams. ──
    static string Section2to4_MountModeRender()
    {
        EditorSceneManager.OpenScene(BackcastWorkspaceSceneBuilder.ScenePath, OpenSceneMode.Single);
        var root = UnityEngine.Object.FindFirstObjectByType<BackcastWorkspaceRoot>();
        if (root == null) return "depthladder: BackcastWorkspaceRoot missing";

        var ty = typeof(BackcastWorkspaceRoot);
        ty.GetField("_font", BF).SetValue(root, Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"));
        ty.GetMethod("ResolvePaths", BF).Invoke(root, null);
        ty.GetMethod("BuildWorkspace", BF).Invoke(root, null);

        var scenario = ty.GetField("_scenario", BF).GetValue(root) as ScenarioStartupController;
        var depthLadders = ty.GetField("_depthLadders", BF).GetValue(root) as IDictionary;
        var chartAreas = ty.GetField("_chartAreas", BF).GetValue(root) as IDictionary;
        var chartViews = ty.GetField("_chartViews", BF).GetValue(root) as IDictionary;
        var applyMode = ty.GetMethod("ApplyDepthLadderMode", BF);
        var render = ty.GetMethod("RenderDepthLadders", BF);
        if (scenario == null || depthLadders == null || chartAreas == null || chartViews == null
            || applyMode == null || render == null)
            return "depthladder: root internals not found (renamed?)";

        // ── 2. mount: a 2-instrument universe spawns 2 chart tiles, each with a DepthLadderView in a
        // right strip + a sibling chartArea hosting the ChartView. ──
        scenario.Universe.ReplaceAll(new[] { "X.TSE", "Y.TSE" });
        foreach (var id in new[] { "X.TSE", "Y.TSE" })
        {
            var ladder = depthLadders[id] as DepthLadderView;
            var area = chartAreas[id] as RectTransform;
            var cv = chartViews[id] as ChartView;
            if (ladder == null) return "depthladder: no DepthLadderView for " + id;
            if (area == null) return "depthladder: no chartArea for " + id;
            if (cv == null) return "depthladder: no ChartView for " + id;
            // ladder + chartArea are siblings under the SAME tile body (per-tile mount, not orphan).
            if (ladder.transform.parent != area.parent)
                return "depthladder: ladder not a sibling of chartArea (not mounted in the tile) for " + id;
            if (cv.transform != area)
                return "depthladder: ChartView is not on the chartArea for " + id;
        }
        if (depthLadders.Count != 2) return "depthladder: expected exactly 2 ladders (one per instrument)";

        // ── 3. mode-sync: built in Replay → ladder hidden + chart full width; Live → shown + inset. ──
        foreach (var id in new[] { "X.TSE", "Y.TSE" })
        {
            var ladder = depthLadders[id] as DepthLadderView;
            var area = chartAreas[id] as RectTransform;
            if (ladder.gameObject.activeSelf) return "depthladder: ladder must be HIDDEN at build (Replay default) for " + id;
            if (Mathf.Abs(area.offsetMax.x) > EPS) return "depthladder: chart must be FULL width in Replay for " + id;
        }
        applyMode.Invoke(root, new object[] { true });   // → Live
        foreach (var id in new[] { "X.TSE", "Y.TSE" })
        {
            var ladder = depthLadders[id] as DepthLadderView;
            var area = chartAreas[id] as RectTransform;
            if (!ladder.gameObject.activeSelf) return "depthladder: ladder must be SHOWN in Live for " + id;
            if (Mathf.Abs(area.offsetMax.x - (-LADDER_WIDTH)) > EPS) return "depthladder: chart must inset by LADDER_WIDTH in Live for " + id;
        }
        applyMode.Invoke(root, new object[] { false });  // → Replay
        foreach (var id in new[] { "X.TSE", "Y.TSE" })
        {
            var ladder = depthLadders[id] as DepthLadderView;
            var area = chartAreas[id] as RectTransform;
            if (ladder.gameObject.activeSelf) return "depthladder: ladder must HIDE again in Replay for " + id;
            if (Mathf.Abs(area.offsetMax.x) > EPS) return "depthladder: chart must reclaim full width in Replay for " + id;
        }

        // ── 4. per-instrument render: X has depth + price; Y has no depth. Render → X shows a board
        // (best ask/bid + LAST from per_instrument[X].price); Y stays a placeholder (X's board is NOT
        // leaked to Y). ──
        applyMode.Invoke(root, new object[] { true });   // show the ladders so Render targets live views
        const string payload =
            "{\"execution_mode\":\"LiveAuto\",\"per_instrument\":{" +
              "\"X.TSE\":{\"price\":105.0,\"depth\":{" +
                "\"bids\":[{\"price\":99.0,\"size\":5.0},{\"price\":98.0,\"size\":4.0}]," +
                "\"asks\":[{\"price\":101.0,\"size\":3.0},{\"price\":102.0,\"size\":2.0}],\"timestamp_ms\":11}}," +
              "\"Y.TSE\":{\"price\":20.0,\"depth\":null}" +
            "}}";
        render.Invoke(root, new object[] { payload });

        var lx = depthLadders["X.TSE"] as DepthLadderView;
        if (lx.BestAsk() == null || lx.BestBid() == null) return "depthladder: X board not rendered (best bid/ask null)";
        if (lx.LastRow() == null) return "depthladder: X LAST row missing";
        if (lx.LastRow().text != "LAST 105.00") return "depthladder: X LAST not from per_instrument price (got '" + lx.LastRow().text + "')";

        var lyv = depthLadders["Y.TSE"] as DepthLadderView;
        if (lyv.BestAsk() != null || lyv.BestBid() != null || lyv.LastRow() != null)
            return "depthladder: Y (no depth) must be a placeholder — X's board leaked to Y (single-global regression)";
        return null;
    }
}
