# ChartNoDataE2ERunner — 空チャートの「データなし」表示・1970エポック軸抑制（#182 副次 AC）

`ChartNoDataE2ERunner.Run` / 期待タグ `[E2E CHART NO DATA PASS]` / exit 0。

## 背景

確定窓に当該銘柄のバーが無く、ストリームも 0 本のとき `per_instrument` がその銘柄を持たない →
`InstrumentOhlcDecoder` の `HasSeries=false` → `BackcastWorkspaceRoot` の per-id chart loop が
`continue` で `ChartView.Render` を呼ばない → チャートは描画されないまま既定の時間軸が残る。
`ChartScale.FormatTimeLabel(0)` は `translation_ms=0` を **1970-01-01** とフォーマットするため、
空チャートに 1970 年台の謎の時間軸が出る（#182 副次 AC の症状）。修正は **`ChartView` が
real frame を持たないとき "NO DATA" マーカーを出し、時間軸ラベルを抑制する**こと。

## 操作一覧表

| Action ID | 行動 | 入口(file:line) | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|---|
| CHART-NODATA-01 | 新規 ChartView を Build（Render 無し） | `ChartView.Build` | `NoDataShown`・`ActiveTimeLabelCount` | あり | 自動(E2E済・ChartNoDataE2ERunner) |
| CHART-NODATA-02 | 系列 Render → 空 Render | `ChartView.Render` 空分岐 | `NoDataShown`・`ActiveTimeLabelCount` の遷移 | あり | 自動(E2E済・ChartNoDataE2ERunner) |
| （実グリフ点滅・実スクショ） | 実 SDF/legacy フォントで "NO DATA" が読めるか | — | 目視 | — | HITL専用（headless はテキスト値のみ・グリフ描画は HITL） |

## 合格条件

- **CHART-NODATA-01**: Build 直後（一度も Render していない）チャートは `NoDataShown==true` かつ
  **active 時間ラベル 0 本**（既定 1970 軸が出ない）。
- **CHART-NODATA-02**: 系列を Render すると `NoDataShown==false` ＋ active 時間ラベル > 0（非空虚の床）。
  その後 **空フレームを Render** すると `NoDataShown==true` に戻り、**active 時間ラベルが 0 本にクリア**
  される（stale な軸が残らない）。再度系列を Render するとマーカーが消え軸が復活する（litmus 尾）。

## LITMUS（delete-the-production-logic）

- `_noDataLabel` / `UpdateNoData` を撤去 → `NoDataShown=false` → CHART-NODATA-01/02 RED。
- `Render` 空分岐の `_lastTimeTicks.Clear()` ＋ `_axisLabelsDirty=true` を削除 → 空遷移後も stale ラベルが
  残り `ActiveTimeLabelCount>0` → CHART-NODATA-02 RED。

## 備考

- headless は `Canvas.ForceUpdateCanvases()` ＋ `RebuildAxisLabels()` で OnPopulateMesh／ラベル生成を駆動
  （`ChartAxisGridE2ERunner` と同型）。`LastTimeLabelCount` はプール件数で単調増加するため、可視判定は
  **`ActiveTimeLabelCount`**（active な子のみ計数）で行う。
- マーカー文字列は ASCII "NO DATA"（LegacyRuntime.ttf でグリフ欠落 □ を避ける／CJK フォント配線後に
  日本語化可能・memory `unity-mojibake-is-missing-cjk-glyph` 参照）。
