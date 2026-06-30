// ChartHostWiring.cs — the single per-chart wiring the host (BackcastWorkspaceRoot.Update) applies to
// every ChartView on each state poll, BEFORE Render. Kept as a MonoBehaviour-free plain helper so the
// AFK gate (ChartReplayBasisE2ERunner) drives the SAME production wiring instead of re-implementing it.
//
// 方針: findings 0133 — basis_ms（バー間隔＝X 軸スケール）の正本は scenario の granularity であって、
// 「最初に届いた bar フレームの間隔から推定して固定」ではない。host が毎ポール scenario granularity を
// SetGranularity で配線することで、cold preview（findings 0129・カタログ全期間）が日足っぽい間隔でも
// 分足の Replay が DAILY basis に化けない（＝同一 X への collapse と 65000 頂点超過を根絶）。
//
// SetFitAllOnAutoScale / SetGranularity はどちらも idempotent（basis / flag が変わるときだけ再アンカー）
// なので毎ポール呼んでも pan/zoom を奪わない。

public static class ChartHostWiring
{
    // Apply the per-chart, per-poll wiring. `fitAll` = Replay 全期間 fit（Live は false）。`granularity`
    // = scenario の足種（basis の SoT）。順序は host の従来挙動を保つ（fit-all → basis）。
    public static void Apply(ChartView cv, bool fitAll, GranularityChoice granularity)
    {
        if (cv == null) return;
        cv.SetFitAllOnAutoScale(fitAll);
        cv.SetGranularity(granularity);   // findings 0133 fix: basis = scenario granularity（抜くと RED）
    }
}
