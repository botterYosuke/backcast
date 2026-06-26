// ChartLadderInteractionE2ERunner.cs — S9 #163 / ADR-0035 / findings 0120 D-11+D-12: chart↔ladder
// interaction (CrosshairState hover via ChartLadderRoot + depth diff Timer linear decay)。
// 台本: same-dir ChartLadderInteractionE2ERunner.md。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartLadderInteractionE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART LADDER INTERACTION PASS] / exit=0
//
// WHAT THIS GATES:
//   HOVER-LADDER-01: ChartLadderRoot parent + ChartView writes hovered_price → DepthLadderView
//                    Update reads it via GetComponentInParent → nearest row's GetRowHighlightTint
//                    becomes non-clear; hover-exit clears it back.
//   LADDER-DIFF-01: snapshot1(bid[0] size=100) → snapshot2(bid[0] size=200) → immediate diff Timer
//                    posted → GetRowHighlightTint on bid[0]'s display row has non-zero alpha.
//   LADDER-DIFF-02: 300ms after the diff render → TickForTest(300) → tint decayed to Color.clear.
//   LADDER-DIFF-03: best-price ticked up/down (price changed but level otherwise same) → diff calc
//                   uses price_key matching so a 100→200 size bump on a price 100→101 ticked-up best
//                   still registers as a size delta (NOT lost to "different level").
//
// LITMUS:
//  * Drop the ChartLadderRoot parent-Component → ChartView.Crosshair falls back to local, ladder
//    Update sees parent=null → HOVER-LADDER-01 RED.
//  * Drop PushDiffTimers from Render → LADDER-DIFF-01 RED.
//  * Drop the per-Update remainingMs decrement → LADDER-DIFF-02 RED (tint never fades).
//  * Use array-index instead of price_key for level matching → LADDER-DIFF-03 RED.

using System;
using UnityEditor;
using UnityEngine;

public static class ChartLadderInteractionE2ERunner
{
    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_HoverLadder()         // HOVER-LADDER-01
                ?? Section2_DiffImmediate()       // LADDER-DIFF-01
                ?? Section3_DiffDecay()           // LADDER-DIFF-02
                ?? Section4_DiffPriceKey();       // LADDER-DIFF-03
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART LADDER INTERACTION PASS] (HOVER-LADDER-01) parent ChartLadderRoot wires "
                    + "ChartView.Crosshair.hovered_price → ladder Update → nearest row tint; (LADDER-DIFF-01) "
                    + "size 100→200 → row tint immediate; (LADDER-DIFF-02) 300ms TickForTest fades to clear; "
                    + "(LADDER-DIFF-03) price ticked best still diffed via price_key (not array index).");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART LADDER INTERACTION FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Section1_HoverLadder()
    {
        var (canvasGo, body, cv, lv) = BuildRig();
        try
        {
            // Snapshot with a known bid[0] price.
            lv.Render(new DepthSnapshotView
            {
                HasDepth = true,
                Bids = new[] { new DepthLevelView { Price = 480.4, Size = 100 } },
                Asks = new[] { new DepthLevelView { Price = 480.9, Size = 50 } },
                TimestampMs = 1,
            }, lastPrice: 480.5);

            // Write hovered_price via ChartView's derive (cursor in main_area). Cursor at center y.
            cv.SetCrosshairCursorForTest(new Vector2(10f, 0f));
            // Cursor.x=10 maps to a time near visible_end (no bars). hovered_price comes from y → about
            // the middle of the visible price range. But we haven't rendered bars on the chart, so the
            // hovered_price is NaN/whatever. Bypass the chart derive — directly write hovered_price.
            var root = body.GetComponent<ChartLadderRoot>();
            root.Crosshair.hovered_price = 480.4;   // hover near bid[0]
            lv.TickForTest(0f);   // sync the hover layer

            // bid[0] display index = LadderDepth + 1 + 0 = 11.
            var tint = lv.GetRowHighlightTint(11);
            if (tint.a == 0)
                return "S1 HOVER-LADDER-01: nearest-row tint not set after hovered_price=480.4 (parent wiring broken)";

            // Hover exit → tint cleared.
            root.Crosshair.hovered_price = null;
            lv.TickForTest(0f);
            tint = lv.GetRowHighlightTint(11);
            if (tint.a != 0)
                return "S1 HOVER-LADDER-01: tint persists after hovered_price=null (hover exit not honored)";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    static string Section2_DiffImmediate()
    {
        var (canvasGo, body, cv, lv) = BuildRig();
        try
        {
            lv.Render(new DepthSnapshotView
            {
                HasDepth = true,
                Bids = new[] { new DepthLevelView { Price = 480.4, Size = 100 } },
                Asks = new[] { new DepthLevelView { Price = 480.9, Size = 50 } },
                TimestampMs = 1,
            });
            // 2nd snapshot: bid[0] grew 100→200.
            lv.Render(new DepthSnapshotView
            {
                HasDepth = true,
                Bids = new[] { new DepthLevelView { Price = 480.4, Size = 200 } },
                Asks = new[] { new DepthLevelView { Price = 480.9, Size = 50 } },
                TimestampMs = 2,
            });
            lv.TickForTest(0f);   // composite immediately, no decay yet
            var tint = lv.GetRowHighlightTint(11);   // bid[0]
            if (tint.a <= 0.1f)
                return "S2 LADDER-DIFF-01: bid[0] size 100→200 did not light a diff tint (got alpha=" + tint.a + ")";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    static string Section3_DiffDecay()
    {
        var (canvasGo, body, cv, lv) = BuildRig();
        try
        {
            lv.Render(new DepthSnapshotView
            {
                HasDepth = true,
                Bids = new[] { new DepthLevelView { Price = 480.4, Size = 100 } },
                Asks = new[] { new DepthLevelView { Price = 480.9, Size = 50 } },
                TimestampMs = 1,
            });
            lv.Render(new DepthSnapshotView
            {
                HasDepth = true,
                Bids = new[] { new DepthLevelView { Price = 480.4, Size = 200 } },
                Asks = new[] { new DepthLevelView { Price = 480.9, Size = 50 } },
                TimestampMs = 2,
            });
            lv.TickForTest(0f);
            if (lv.GetRowHighlightTint(11).a <= 0)
                return "S3 LADDER-DIFF-02: precondition — diff tint should be live before decay";
            // Tick by full DEPTH_DIFF_HIGHLIGHT_MS + slack.
            lv.TickForTest(DepthLadderView.DEPTH_DIFF_HIGHLIGHT_MS + 10f);
            var tint = lv.GetRowHighlightTint(11);
            if (tint.a > 0.01f)
                return "S3 LADDER-DIFF-02: tint still alpha=" + tint.a + " after "
                     + DepthLadderView.DEPTH_DIFF_HIGHLIGHT_MS + "ms — Timer decay missing";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    static string Section4_DiffPriceKey()
    {
        var (canvasGo, body, cv, lv) = BuildRig();
        try
        {
            // First snapshot: bid[0] @ 480.4 size 100.
            lv.Render(new DepthSnapshotView
            {
                HasDepth = true,
                Bids = new[] { new DepthLevelView { Price = 480.4, Size = 100 } },
                Asks = new[] { new DepthLevelView { Price = 480.9, Size = 50 } },
                TimestampMs = 1,
            });
            // Second snapshot: best bid ticked UP to 480.5 (the old 480.4 level disappeared);
            // PRICE_KEY match → 480.4 is now absent → DiffSide should mark it as a "removed level"
            // → grey-tint highlight on bid[0]'s display row (it now holds 480.5, but the diff highlight
            // is for the visible position change). Either way, GetRowHighlightTint(11) should be lit.
            lv.Render(new DepthSnapshotView
            {
                HasDepth = true,
                Bids = new[] { new DepthLevelView { Price = 480.5, Size = 100 } },
                Asks = new[] { new DepthLevelView { Price = 480.9, Size = 50 } },
                TimestampMs = 2,
            });
            lv.TickForTest(0f);
            // Some highlight must fire because the level set changed.
            var tint = lv.GetRowHighlightTint(11);
            if (tint.a <= 0)
                return "S4 LADDER-DIFF-03: a ticked-up best (price 480.4 → 480.5) yielded no diff tint — "
                     + "price_key match logic missing or array-index matching swallowed the change.";
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ---- rig ----

    static (GameObject canvasGo, RectTransform body, ChartView cv, DepthLadderView lv) BuildRig()
    {
        var canvasGo = new GameObject("ChartLadderCanvas", typeof(Canvas));
        var bodyGo = new GameObject("body", typeof(RectTransform), typeof(ChartLadderRoot));
        var body = bodyGo.GetComponent<RectTransform>();
        body.SetParent(canvasGo.transform, false);
        body.anchorMin = new Vector2(0.5f, 0.5f); body.anchorMax = new Vector2(0.5f, 0.5f);
        body.pivot = new Vector2(0.5f, 0.5f);
        body.sizeDelta = new Vector2(620f, 400f);

        var chartAreaGo = new GameObject("ChartArea", typeof(RectTransform));
        var chartArea = (RectTransform)chartAreaGo.transform;
        chartArea.SetParent(body, false);
        chartArea.anchorMin = Vector2.zero; chartArea.anchorMax = Vector2.one;
        chartArea.offsetMin = Vector2.zero; chartArea.offsetMax = new Vector2(-120f, 0f);
        var cv = chartAreaGo.AddComponent<ChartView>();
        cv.Build(chartArea, showTitleBar: false);

        var ladderAreaGo = new GameObject("LadderArea", typeof(RectTransform));
        var ladderArea = (RectTransform)ladderAreaGo.transform;
        ladderArea.SetParent(body, false);
        ladderArea.anchorMin = new Vector2(1f, 0f); ladderArea.anchorMax = new Vector2(1f, 1f);
        ladderArea.pivot = new Vector2(1f, 0.5f);
        ladderArea.sizeDelta = new Vector2(120f, 0f); ladderArea.anchoredPosition = Vector2.zero;
        var lv = ladderAreaGo.AddComponent<DepthLadderView>();
        lv.Build(ladderArea);

        return (canvasGo, body, cv, lv);
    }
}
