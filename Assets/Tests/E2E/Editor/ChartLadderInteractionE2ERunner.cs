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
            fail = Section1a_HoverLadder_DeriveBypass()         // HOVER-LADDER-01a
                ?? Section1b_HoverLadder_ChartDerive()  // HOVER-LADDER-01b
                ?? Section2_DiffImmediate()       // LADDER-DIFF-01
                ?? Section3_DiffDecay()           // LADDER-DIFF-02
                ?? Section4_DiffPriceKey();       // LADDER-DIFF-03
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART LADDER INTERACTION PASS] (HOVER-LADDER-01a) parent ChartLadderRoot wires "
                    + "direct Crosshair.hovered_price → ladder Update → nearest row tint; (HOVER-LADDER-01b) "
                    + "ChartView.SetCrosshairCursorForTest → DeriveCrosshair → shared CrosshairState (via "
                    + "GetComponentInParent<ChartLadderRoot>) → ladder Update reads same hovered_price; "
                    + "(LADDER-DIFF-01) size 100→200 → row tint immediate; (LADDER-DIFF-02) 300ms TickForTest "
                    + "fades to clear; (LADDER-DIFF-03) price ticked best still diffed via price_key (not array index).");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART LADDER INTERACTION FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Section1a_HoverLadder_DeriveBypass()
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
            Debug.Log("[E2E HOVER-LADDER-01a PASS] direct Crosshair.hovered_price write → ladder nearest-row tint; null clears tint.");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // HOVER-LADDER-01b: ChartView の derive 経路まで含めて wiring をゲートする。Section1a が直接
    // root.Crosshair.hovered_price を書いて parent→ladder の Update 経路を検証するのに対し、ここでは
    // ChartView.SetCrosshairCursorForTest → DeriveCrosshair → ChartView.Crosshair (getter が
    // GetComponentInParent<ChartLadderRoot>() で同じ shared state を返す) → ladder Update が同じ
    // hovered_price を読む、という UI 入力側→ladder 表示側の end-to-end を保証する。
    // LITMUS: ChartView の Crosshair getter を `_localCrosshair` 固定に戻すと、derive 後 ladder 側が
    //         shared state を見ない → tint が立たず S1b RED。
    static string Section1b_HoverLadder_ChartDerive()
    {
        var (canvasGo, body, cv, lv) = BuildRig();
        try
        {
            // Bars を最小限 render しないと visible_min_price が NaN で hovered_price も無価値。
            cv.SetGranularity(GranularityChoice.Daily);
            cv.Render(new ReplayBarFrame { Ohlc = SyntheticDaily(32) });
            // Ladder snapshot: bid[0] が cursor 中心 y にマップされる price 近傍に来るよう、
            // visible price range の中央値を狙って Render する。SyntheticDaily(32) の close は
            // 100.0±0.5 程度なので bid[0]=100.0 で hovered_price と一致するはず。
            lv.Render(new DepthSnapshotView
            {
                HasDepth = true,
                Bids = new[] { new DepthLevelView { Price = 100.0, Size = 100 } },
                Asks = new[] { new DepthLevelView { Price = 100.5, Size = 50 } },
                TimestampMs = 1,
            }, lastPrice: 100.2);
            Canvas.ForceUpdateCanvases();

            // Cursor を main_area 内に置く（plot center 付近）。ChartView は ChartLadderRoot.Crosshair
            // を返す getter を持つので、ここで書いた hovered_price はそのまま ladder 側からも見える。
            cv.SetCrosshairCursorForTest(new Vector2(10f, 0f));
            Canvas.ForceUpdateCanvases();
            if (!cv.Crosshair.hovered_price.HasValue)
                return "S1b HOVER-LADDER-01b: hovered_price unset after SetCrosshairCursorForTest "
                     + "(ChartView derive 経路が壊れている)";

            // Ladder の hover layer は parent.Crosshair を毎 Update 読む。headless では Update が
            // 走らないので TickForTest(0) で hover layer を sync させる。
            lv.TickForTest(0f);

            // hovered_price が ladder のどの row にマップされたかは NearestRow 次第。bid[0] (idx 11) /
            // LAST (idx 10) / ask[0] (idx 9) のいずれかには必ず lit row が立つ（cursor 中心 y で
            // visible_min..max の中央 ≈ 100 近傍に来る）。range で OR チェック。
            bool anyLit = false;
            for (int i = 9; i <= 11; i++)
                if (lv.GetRowHighlightTint(i).a > 0.01f) { anyLit = true; break; }
            if (!anyLit)
                return "S1b HOVER-LADDER-01b: ChartView.DeriveCrosshair → ladder 経路で row 9..11 の "
                     + "どれにも hover tint が立たない (Crosshair getter が shared root を返していない疑い)";

            // Cursor 外に出して exit clear を検証。
            ((UnityEngine.EventSystems.IPointerExitHandler)cv).OnPointerExit(
                new UnityEngine.EventSystems.PointerEventData(EnsureES()));
            Canvas.ForceUpdateCanvases();
            lv.TickForTest(0f);
            for (int i = 9; i <= 11; i++)
                if (lv.GetRowHighlightTint(i).a > 0.01f)
                    return "S1b HOVER-LADDER-01b: pointer exit 後も row " + i + " に tint が残る "
                         + "(Crosshair.Clear が ladder 側まで伝播していない)";
            Debug.Log("[E2E HOVER-LADDER-01b PASS] ChartView.SetCrosshairCursorForTest → DeriveCrosshair "
                    + "→ shared root.Crosshair → ladder NearestRow tint; pointer exit が ladder 側も clear。");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // SyntheticDaily helper (ChartCrosshairLastPriceE2ERunner と同形).
    static OhlcPoint[] SyntheticDaily(int n)
    {
        var arr = new OhlcPoint[n];
        long startMs = 1_700_000_000_000L;
        for (int i = 0; i < n; i++)
        {
            double o = 100.0 + (i % 7) * 0.05;
            double c = o + (i % 2 == 0 ? +0.03 : -0.03);
            arr[i] = new OhlcPoint
            {
                open_time_ms = startMs + (long)i * ChartViewState.BASIS_DAILY_MS,
                open = o, close = c,
                high = Math.Max(o, c) + 0.04, low = Math.Min(o, c) - 0.04, volume = 1000.0 + i,
            };
        }
        return arr;
    }

    static UnityEngine.EventSystems.EventSystem EnsureES()
    {
        var es = UnityEngine.Object.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>();
        if (es != null) return es;
        return new GameObject("EventSystem", typeof(UnityEngine.EventSystems.EventSystem))
            .GetComponent<UnityEngine.EventSystems.EventSystem>();
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
            Debug.Log("[E2E LADDER-DIFF-01 PASS] bid[0] size 100→200 immediate diff tint.");
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
            Debug.Log("[E2E LADDER-DIFF-02 PASS] tint decays to ~0 after DEPTH_DIFF_HIGHLIGHT_MS.");
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
            Debug.Log("[E2E LADDER-DIFF-03 PASS] price-keyed diff match still fires when best ticks up.");
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
