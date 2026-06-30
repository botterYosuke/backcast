// ChartReplayBasisE2ERunner.cs — findings 0133 regression gate: Replay Minute モードで分足が同一 X に
// 重なり、可視窓が過大化して "Mesh can not have more than 65000 vertices" で落ちる不具合を回帰ゲート化する
// Surface E2E runner。台本: same-dir ChartReplayBasisE2ERunner.md。
//
//   <Unity> -batchmode -nographics -quit -projectPath <abs> \
//           -executeMethod ChartReplayBasisE2ERunner.Run -logFile <abs>
//   # expect: [E2E CHART REPLAY BASIS PASS] ... / exit=0
//   # compile-only: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
//
// WHAT THIS GATES — basis_ms（バー間隔＝X 軸スケール）の正本が scenario granularity であること（findings 0133）。
// 真因の再現: host は cold preview（findings 0129・カタログ全期間）を FIRST フレームとして描くので、もし
// granularity を明示配線せず ChartView の「最初のフレームから basis を推定して固定」に委ねると、preview が
// 日足っぽい間隔だと basis が DAILY に固定され、後続の分足ストリームが (a) 同一 X に collapse し
// (b) 可視窓が全 bar を覆って RenderedBarCount が 65000 頂点予算を超える。
//
//   CHARTBASIS-01: scenario=Minute で host が ChartHostWiring.Apply を配線すると、日足プレビュー frame が
//     先に来ても basis_ms==MINUTE のまま・分足が別々の X に並ぶ（隣接分足の TimeToX 差 ≥ 0.5px）。
//     delete-the-logic litmus: ChartHostWiring.Apply の `cv.SetGranularity(granularity)` を抜く →
//     先頭の日足 frame が basis を DAILY に固定 → 分足が 0.04px に collapse（差 < 0.5px）→ RED。
//
//   CHARTBASIS-02: 同条件で RenderedBarCount * 12 < 65000（頂点予算内）。fit_all=Minute が cell を MIN に
//     クランプして直近の小窓に右寄せするので密な分足でも数百本しか出ない。
//     delete-the-logic litmus: 同じく SetGranularity を抜く → basis=DAILY で 8 日窓が全分足を覆い
//     RenderedBarCount が TOTAL_MINUTE(8640) に張り付く → *12 > 65000 → ArgumentException 経路 → RED。
//
// Python-FREE: 合成 OhlcPoint を ChartView.Render() に直接渡し、Canvas.ForceUpdateCanvases() で
// OnPopulateMesh を発火、RenderedBarCount / basis_ms / TimeToX を post-emit で読む。配線は production の
// ChartHostWiring.Apply（host が Update で呼ぶのと同一）を通すので re-implementation の tautology を避ける。

using System;
using UnityEditor;
using UnityEngine;

public static class ChartReplayBasisE2ERunner
{
    const float PLOT_WIDTH_PX = 540f;
    const float CHART_HEIGHT_PX = 400f;
    const float GUTTER_HORIZ = 80f;     // GutterLeft(60) + GutterRight(20) — mirror of ChartView gutters
    const float HOST_WIDTH_PX = PLOT_WIDTH_PX + GUTTER_HORIZ;   // = 620

    const int PREVIEW_DAILY_BARS = 20;       // cold preview（findings 0129 全期間）を模す日足 frame
    const int TOTAL_MINUTE_BARS = 8640;      // 6 日分の分足（DAILY basis なら ~8.4 日窓が全数を覆い 8640*12 > 65000）

    // 65000 頂点上限 / 1 bar = 12 頂点（wick+body+volume の 3 quad）→ 安全予算は 5416 本。
    const int VERTEX_BUDGET_BARS = 65000 / 12;   // = 5416

    // 隣接分足の X 差の floor。MINUTE basis (cell≥MIN 1.0) なら ≥1.0px、DAILY basis (cell≤MAX 64) なら
    // 60000/86_400_000*64 ≈ 0.044px に collapse する。0.5 が両者を明確に分ける。
    const float DISTINCT_X_FLOOR_PX = 0.5f;

    public static void Run()
    {
        string fail;
        try
        {
            fail = Section1_MinuteBasisNoCollapseAndBudget();
        }
        catch (Exception e) { fail = "driver: " + e; }

        if (fail == null)
        {
            Debug.Log("[E2E CHART REPLAY BASIS PASS] (CHARTBASIS-01) scenario=Minute + ChartHostWiring.Apply: "
                    + "日足プレビュー frame が先でも basis_ms=MINUTE / 分足が別々の X（隣接差≥" + DISTINCT_X_FLOOR_PX
                    + "px）; (CHARTBASIS-02) RenderedBarCount*12 < 65000（fit_all=Minute が MIN セルへクランプ）— "
                    + "findings 0133 の collapse＋Mesh 65000 頂点超過を根絶。");
            EditorApplication.Exit(0);
        }
        else
        {
            Debug.LogError("[E2E CHART REPLAY BASIS FAIL] " + fail);
            EditorApplication.Exit(1);
        }
    }

    static string Section1_MinuteBasisNoCollapseAndBudget()
    {
        var cv = BuildChart(out var canvasGo);
        try
        {
            // host の Update が毎ポール行う順序を忠実に再現:
            //   ① ChartHostWiring.Apply(cv, fitAll:true, Minute)  ← basis の正本配線（findings 0133 fix）
            //   ② Render(cold preview = 日足 frame)               ← findings 0129: 最初に全期間プレビュー
            //   ③ ChartHostWiring.Apply(...)（毎ポール・idempotent）
            //   ④ Render(分足ストリーム)
            // fix が無いと ② が basis を DAILY に固定し ④ が collapse＋頂点超過する。
            long previewStartMs = 1_700_000_000_000L;
            var previewDaily = SyntheticBars(PREVIEW_DAILY_BARS, previewStartMs, ChartViewState.BASIS_DAILY_MS, 3700.0);

            ChartHostWiring.Apply(cv, fitAll: true, granularity: GranularityChoice.Minute);
            cv.Render(new ReplayBarFrame { Ohlc = previewDaily });
            Canvas.ForceUpdateCanvases();

            // 分足ストリーム（preview の末尾の翌分から 6 日分・昇順）。
            long minuteStartMs = previewDaily[previewDaily.Length - 1].open_time_ms + ChartViewState.BASIS_MINUTE_MS;
            var minuteBars = SyntheticBars(TOTAL_MINUTE_BARS, minuteStartMs, ChartViewState.BASIS_MINUTE_MS, 3700.0);

            ChartHostWiring.Apply(cv, fitAll: true, granularity: GranularityChoice.Minute);
            cv.Render(new ReplayBarFrame { Ohlc = minuteBars });
            Canvas.ForceUpdateCanvases();

            if (cv.TotalBarCount != TOTAL_MINUTE_BARS)
                return "S1: TotalBarCount=" + cv.TotalBarCount + ", want " + TOTAL_MINUTE_BARS
                     + "（最終フレームは分足ストリーム）";

            // CHARTBASIS-01a: basis は scenario の Minute（日足プレビューに引きずられない）。
            if (cv.ViewState.basis_ms != ChartViewState.BASIS_MINUTE_MS)
                return "S1 CHARTBASIS-01: basis_ms=" + cv.ViewState.basis_ms + ", want "
                     + ChartViewState.BASIS_MINUTE_MS + "（MINUTE）。日足プレビュー frame が先に basis を DAILY へ "
                     + "固定し、host が scenario granularity を配線していない（ChartHostWiring.Apply の "
                     + "SetGranularity が抜けている）と DAILY に化ける — findings 0133 の真因。";

            // CHARTBASIS-01b: 隣接分足が別々の X に並ぶ（同一列に collapse しない）。
            var plot = cv.LastPlotRect;
            float x0 = cv.ViewState.TimeToX(minuteBars[0].open_time_ms, plot.xMin, plot.width);
            float x1 = cv.ViewState.TimeToX(minuteBars[1].open_time_ms, plot.xMin, plot.width);
            float dx = Mathf.Abs(x1 - x0);
            if (dx < DISTINCT_X_FLOOR_PX)
                return "S1 CHARTBASIS-01: 隣接分足の TimeToX 差=" + dx + "px < " + DISTINCT_X_FLOOR_PX
                     + "px → 同一 X に collapse（basis が DAILY のとき 60000/86_400_000*cell ≈ 0.04px）。";

            // CHARTBASIS-02: 可視窓内の描画本数が頂点予算内（65000 頂点で落ちない）。
            int rendered = cv.RenderedBarCount;
            if (rendered <= 0)
                return "S1 CHARTBASIS-02: RenderedBarCount=" + rendered + " — 可視窓に分足が 1 本も出ていない"
                     + "（right-anchor / fit-all clamp の回帰）。";
            if (rendered * 12 >= 65000)
                return "S1 CHARTBASIS-02: RenderedBarCount=" + rendered + " → 頂点 " + (rendered * 12)
                     + " ≥ 65000（Mesh can not have more than 65000 vertices）。basis=DAILY だと 8 日窓が全 "
                     + TOTAL_MINUTE_BARS + " 分足を覆い窓が過大化する — findings 0133。予算上限本数=" + VERTEX_BUDGET_BARS;

            Debug.Log("[E2E CHARTBASIS-01 PASS] basis_ms=MINUTE（日足プレビュー frame に化けず）/ 隣接分足 X 差="
                    + dx + "px ≥ " + DISTINCT_X_FLOOR_PX + "（collapse せず）。");
            Debug.Log("[E2E CHARTBASIS-02 PASS] RenderedBarCount=" + rendered + " → 頂点 " + (rendered * 12)
                    + " < 65000（fit_all=Minute が cell を MIN へクランプ＋直近窓へ右寄せ）。TOTAL_MINUTE="
                    + TOTAL_MINUTE_BARS + " 本でも窓内は予算内。");
            return null;
        }
        finally { UnityEngine.Object.DestroyImmediate(canvasGo); }
    }

    // ---- helpers ----

    static ChartView BuildChart(out GameObject canvasGo)
    {
        canvasGo = new GameObject("ChartReplayBasisCanvas", typeof(Canvas));
        var hostGo = new GameObject("ChartHost", typeof(RectTransform));
        var host = hostGo.GetComponent<RectTransform>();
        host.SetParent(canvasGo.transform, false);
        host.anchorMin = new Vector2(0.5f, 0.5f);
        host.anchorMax = new Vector2(0.5f, 0.5f);
        host.pivot = new Vector2(0.5f, 0.5f);
        host.sizeDelta = new Vector2(HOST_WIDTH_PX, CHART_HEIGHT_PX);
        var cv = hostGo.AddComponent<ChartView>();
        cv.Build(host, showTitleBar: false);
        return cv;
    }

    // 決定論的な OhlcPoint 列（spacingMs 間隔・±小幅ドリフト・RNG なし）。basis 推定は spacingMs から決まる。
    static OhlcPoint[] SyntheticBars(int n, long startMs, long spacingMs, double basePrice)
    {
        var arr = new OhlcPoint[n];
        for (int i = 0; i < n; i++)
        {
            double drift = ((i % 7) - 3) * 2.0;
            double o = basePrice + drift;
            double c = basePrice + drift + (i % 2 == 0 ? +3.0 : -3.0);
            double h = Math.Max(o, c) + 4.0;
            double l = Math.Min(o, c) - 4.0;
            arr[i] = new OhlcPoint
            {
                open_time_ms = startMs + (long)i * spacingMs,
                open = o,
                high = h,
                low = l,
                close = c,
                volume = 1000.0 + i,
            };
        }
        return arr;
    }
}
