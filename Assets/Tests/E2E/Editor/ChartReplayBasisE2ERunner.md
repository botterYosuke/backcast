# ChartReplayBasisE2ERunner — Replay 分足の basis 正本化（同一 X collapse＋Mesh 65000 頂点超過の回帰ゲート）

`ChartReplayBasisE2ERunner.Run` / 期待タグ `[E2E CHART REPLAY BASIS PASS]` / exit 0。正本: `docs/findings/0133`。

## 背景

Replay を **Minute** モードで回すと、ローソク足が同じ X 位置に重なって描かれ、X 軸が日付目盛りになり、
`ArgumentException: Mesh can not have more than 65000 vertices` で描画が落ちる（owner 実機 2026-06-30）。

真因は単一: **中身は分足なのに `ChartViewState.basis_ms` が DAILY に化けている**。host（`BackcastWorkspaceRoot`）は
cold preview（findings 0129・カタログ全期間）を最初のフレームとして描くので、granularity を明示配線せず
`ChartView` の「最初のフレームの bar 間隔から basis を推定して固定」（`Render` の `if (!basis_ms.HasValue)`）に
委ねると、preview が日足っぽい間隔だと basis が DAILY に固定される。すると後続の分足ストリームは
(a) 隣接が `60000/86_400_000*cell ≈ 0.04px` に collapse し (b) `fit_all` の窓が ~8 日を覆って全分足を描画 →
`RenderedBarCount*12 > 65000` で例外。

修正は **host が毎ポール scenario granularity を `ChartHostWiring.Apply`→`ChartView.SetGranularity` で配線**し、
basis の正本を granularity に置く（推定は basis 未指定時のフォールバックに降格）。65000 頂点の保険クランプ自体は
別 issue #183（本ゲートのスコープ外）。

## 操作一覧表

| Action ID | 行動 | 入口(file:line) | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|---|
| CHARTBASIS-01 | scenario=Minute・日足プレビュー frame→分足ストリームを host 配線で Render | `ChartHostWiring.Apply` / `ChartView.Render` | `ViewState.basis_ms`・隣接分足の `TimeToX` 差 | あり | 自動(E2E済・ChartReplayBasisE2ERunner) |
| CHARTBASIS-02 | 同上で描画本数が頂点予算内か | `ChartView.OnPopulateMesh` | `RenderedBarCount` | あり | 自動(E2E済・ChartReplayBasisE2ERunner) |
| （実 SDF 描画・実 Replay 再生で重なりが無いか） | 実画面でローソクが横に進むか | — | 目視 | — | HITL専用（headless は count/座標値のみ・実描画は HITL） |

## 合格条件

- **CHARTBASIS-01**: scenario=Minute で `ChartHostWiring.Apply(cv, fitAll:true, Minute)` を配線すると、先に日足
  プレビュー frame を Render しても最終的に `basis_ms==BASIS_MINUTE_MS`、かつ隣接分足の `TimeToX` 差 ≥ 0.5px
  （同一列に collapse しない）。
- **CHARTBASIS-02**: 同条件で `RenderedBarCount > 0` かつ `RenderedBarCount*12 < 65000`（fit_all=Minute が cell を
  MIN へクランプ＋直近窓へ右寄せするので、合成 8640 分足でも窓内は数百本）。

## LITMUS（delete-the-production-logic）

- `ChartHostWiring.Apply` の `cv.SetGranularity(granularity)` を削除 → 先頭の日足プレビュー frame が basis を
  DAILY に固定 → 分足が 0.04px に collapse（CHARTBASIS-01 RED）＋ ~8 日窓が全 8640 分足を覆い
  `RenderedBarCount*12 ≈ 103680 ≥ 65000`（CHARTBASIS-02 RED・実機の ArgumentException 経路）。
  2026-06-30 実走で RED→GREEN を実証（findings 0133 §gate）。

## 備考

- Python-FREE。合成 `OhlcPoint` を `ChartView.Render()` に渡し `Canvas.ForceUpdateCanvases()` で OnPopulateMesh を
  発火（`ChartVirtualizationE2ERunner` と同型）。配線は production の `ChartHostWiring.Apply`（host が Update で
  呼ぶのと同一メソッド）を通すので re-implementation の tautology を避ける。
- 真因再現の肝＝**先に日足っぽい frame を Render して inference に DAILY を掴ませる**こと。分足だけ Render すると
  `InferBasisMs` が正しく MINUTE を返し fix 無しでも GREEN になる（バグが出ない）。
