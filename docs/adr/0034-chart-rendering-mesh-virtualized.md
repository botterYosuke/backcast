---
status: proposed
---

# Chart 描画方式: 単一 CanvasRenderer + Mesh + 視窓仮想化 + ChartViewState（drag pan / wheel zoom）

owner 依頼（2026-06-26）「チャートをアップグレード: 全期間チャートでもデフォルトで見える範囲を考え直して軽くする／オブジェクトの再利用などUnityの軽量化テクニックを使う／チャートをスクロール可能にする／仮想スクロールで軽くする……軽量でゴージャスなチャートに」＋「実装コストは度外視して**理想的な完成形**を目指せ。手を抜くな」を `grill-with-docs`（2026-06-26・flowsurface skill 参照）で設計ロックした決定。

flowsurface（Bevy + iced 製の参照実装）の **ChartViewState (translation / cell_width / visible_*_range) + ShapePainter 即時描画 + 視窓仮想化**を Unity uGUI に直訳する。直訳の妥当解は **単一 CanvasRenderer + Mesh で全 candle を 1 drawcall**＋**ChartViewState + pan/zoom observer**。

これは [[ADR-0008]] 系（hakoniwa / floating window 描画）と並ぶ Unity フロントエンド描画方針の load-bearing 決定で、ChartView v1（issue #53 / findings 0023 で抽出した uGUI Image rect ベース）を **方式ごと置換**する。

関連: findings 0023（ChartView v1 抽出・本 ADR で方式置換）／findings 0119（本 ADR の下位事実・実装スコープ・E2E migration）／findings 0111（KabuLiveChartRender E2E ゲート・本 ADR で probe seam が変わる）／findings 0104（ChartSpawnPreview cold seed・本 ADR で Replay 全期間 cold load に拡張）／flowsurface skill 対応表（`F:src/chart/kline.rs` / `F:src/chart/scale/linear.rs` / `F:src/chart/scale/timeseries.rs`）。

## Context

ChartView v1（`Assets/Scripts/Chart/ChartView.cs`）は **uGUI Image rect ベース**で書かれている:

- `Render(frame)` が呼ばれるたび `_candleRoot` 配下の **全 GameObject を Destroy**、bar 数 `n` に対し **2n 個の `Image` GameObject を `new`**（wick 1 + body 1）。
- 表示範囲は plot 領域へ全 bar を等分する固定 layout（pan / zoom / scroll なし）。
- データ側のキャップは Python reducer の `max_history_len=1000`。1 bar 増えるたび 2,000 GO の Destroy/Create が走る。
- `_chartRendered` で `bar_count` 変化時のみ rebuild する dedup はあるが、bar が 1 本増えれば毎度 full rebuild。

これでは以下 4 要件を同時に満たせない:

1. **全期間チャート**（scenario.start..end の全 bar・Daily で数千、Minute で数万）の表示。
2. **デフォルトで「見える範囲」だけ軽量に描画**。
3. **スクロール可能**（任意の時刻範囲へ移動）。
4. **仮想スクロール**（視窓外の bar を描画コストから外す）。

加えて「ゴージャス」要素（grid / 軸ラベル / crosshair / 出来高サブペイン）を v1 へ載せると Image GameObject 数がさらに増え、uGUI batching の dirty コストが乗る。

flowsurface（Bevy 製・production-grade）は **ChartViewState（translation / scale / cell_width / cell_height / basis）** を pure data として保持し、**ShapePainter で毎フレーム即時描画**、視窓仮想化は `visible_time_range` / `visible_price_range` で gate する。Unity uGUI で immediate-mode ShapePainter を直訳する妥当解は **単一 CanvasRenderer + custom MaskableGraphic（`OnPopulateMesh` で全 candle を UIVertex バッチに 1 drawcall 統合）**＋**ChartViewState を MonoBehaviour 上に Plain Object として持つ**＋**EventSystem の `IPointerDragHandler` / `IScrollHandler` / `IPointerClickHandler` で pan/zoom/reset**。

owner の "all-in" 指示（「実装コストは度外視」「理想的な完成形」「手を抜くな」）を受け、最小侵襲案（Image rect プール + 仮想化）は **棄却**。Mesh ベースへの方式置換を採る。

## Decision

**8 点**を固定する。後段の細部は findings 0119 に記録し、本 ADR は方針だけ持つ。

1. **描画**: ChartView を **単一 `CanvasRenderer` + custom `MaskableGraphic` サブクラス**へ書き直す。`OnPopulateMesh(VertexHelper vh)` で **visible window 内の全 candle**（wick quad + body quad）を **1 つの UIVertex バッチへ統合**し **GPU draw call = 1**。Image / GameObject の per-candle 生成は撤廃。

2. **視窓仮想化**: ChartView は **`ChartViewState`** を持つ。フィールドは `translation_ms`（視窓の起点 timestamp）/ `cell_width_px`（1 bar が画面上で占める横 px・min/max クランプ）/ `cell_height_norm`（価格スケール・autoscale or 手動）/ `auto_scale: bool` / `basis_ms`（granularity 由来の派生・findings 0023 / chart_viewstate.rs の Unity 翻訳）。`visible_time_range = [translation_ms, translation_ms + plot_width_px / cell_width_px * basis_ms]` を計算し **その範囲の bar だけ vertex を生成**。範囲外は計算スキップ。

3. **UX = drag pan + wheel zoom + double-click reset**（ScrollRect は使わない・横スクロールバーも出さない）:
   - chart 上を **左ドラッグ** → `translation_ms` を cursor 移動量分シフト・`auto_scale=false` 化。右/中ボタンは PanCam 相当に流す（floating window のドラッグと干渉しないよう `propagate(false)` 相当の handler 分離）。
   - chart 上で **マウスホイール** → cursor 位置を中心に `cell_width_px` を ×1.1 / ÷1.1（`Mathf.Pow(1.1f, scroll)`）・`auto_scale` のクランプは `MIN_CELL_WIDTH_PX = 1.0f` ～ `MAX_CELL_WIDTH_PX = 64.0f`。Ctrl 同時押下は PanCam 全体ズームへ譲る（chart は無反応）。
   - **ダブルクリック** → `ChartViewState.ResetView()`（translation を最新 bar に合わせ右端 anchor・`cell_width_px = DEFAULT_CELL_WIDTH_PX = 6.0f`・`auto_scale = true`）。

4. **デフォルト visible 範囲**: 起動時 / `ResetView` 時は **右端 anchor + `cell_width_px = DEFAULT_CELL_WIDTH_PX`**（plot 幅 540px なら ≈90 本表示）。Python が `max_history_len=1000` 本投げてきても**描画 vertex 数は ~180**（90 bar × 2 quad）に圧縮。Replay で scenario が数千本でも初期表示は ~90 本固定。

5. **「全期間」データ supply**:
   - **Replay**: scenario の `start..end` 全 bar を engine 側で cold load し `per_id_ohlc_points` に乗せる（findings 0104 ChartSpawnPreview の preview seed 経路を「scenario 全期間」に拡張）。`max_history_len` は Replay 中は **撤廃** または scenario 期間 bar 数まで自動拡張。
   - **Live**: `max_history_len=1000` を**維持**。kabu historical backfill での「Live でも全期間」は本 ADR スコープ外（別 issue）。Live の「全期間」は当日 + reducer リングバッファに留める。

6. **ゴージャス要素**（v1 スコープ・flowsurface 1.0 同等）を全て本 ADR で同時実装:
   - **price 軸ラベル**: 右 gutter に Text 子・`calc_optimal_price_ticks` 純関数（flowsurface `scale/linear.rs` 直訳）・`Changed<ChartViewState>` 相当で despawn+respawn。`main_area` のみ（volume area には引かない）。
   - **time 軸ラベル**: 下 gutter に Text 子・`calc_optimal_time_step` 純関数（flowsurface `scale/timeseries.rs` 直訳）。
   - **グリッドライン**: candle と同じ Mesh バッチへ低 alpha で統合（追加 drawcall ゼロ）。
   - **クロスヘア**: hover で十字線 + 価格/時刻 readout badge（gutter に重畳）。`CrosshairState` を ChartView に持たせる。
   - **出来高サブペイン**: plot 領域を上 80%（main_area）/ 下 20%（volume_area）に分割し、volume bar を candle と同じ Mesh バッチに統合（追加 drawcall ゼロ）。
   - **last-price dashed line**: 最新 close 価格に水平点線（gutter まで延びる）。
   - 範囲外: indicator overlay / DOM heatmap / footprint cluster（別 slice）。

7. **pan/zoom 状態の永続化**: chart window ごとに `ChartViewState`（`translation_ms` / `cell_width_px` / `auto_scale`）を **layout sidecar**（findings 0048 multi-document layout）の per-window slot に乗せる。reopen で復元。**version を 1 上げて旧 layout は migrate**（古い sidecar に viewState が無ければ `ResetView()` 相当を default）。同じ instrument を別 window で開けば各 window が独立した ChartViewState を持つ。

8. **E2E migration**: ChartView は破壊的変更となるため、既存 probe seam を Mesh ベース向けに **置換**する。`ChartView` に **新 public API**を追加し、E2E はそちらを assert する:
   - `int TotalBarCount { get; }` — 受け取ったデータの全 bar 数。
   - `int RenderedBarCount { get; }` — 直近 OnPopulateMesh で visible window 内に描いた bar 数（≤ TotalBarCount）。
   - `Color FirstCandleColor(bool bullish) { get; }` — 最初の bullish/bearish candle の vertex 色（ThemeProbe seam の Image 置換）。
   - `ChartViewState ViewState { get; }` — translation / cell_width のテスト介入点。
   - 旧 `FirstCandle(bool)→Image` / `Candles` GameObject child count / `c` 名 child は **撤廃**。
   - 影響 E2E: **CHARTRENDER-01..03**（findings 0111）/ **THEME**（ThemeProbe・ThemeHitlHarness）/ **AddChartLadderJourney** など。全 E2E の assertion を新 API に書き換え、新規 gate（VIRTUALIZE-01 / PAN-01 / ZOOM-01 / RESET-01 / PERSIST-01）を `behavior-to-e2e` で同時に landする。

## Consequences

- **Pros**:
  - 全期間表示しても visible window 内の vertex 数固定 → 描画コストは bar 数に依存しない。
  - GPU draw call = 1（candle + grid + volume すべて含む）。GameObject 数 = 1（per chart）。
  - flowsurface 流の pan/zoom UX が手に入り、TradingView / Bookmap と同じ操作感。
  - ゴージャス要素（grid / 軸ラベル / crosshair / 出来高）が低コストで載る（同 Mesh バッチ）。
  - 永続化で reopen 時の scroll 位置が保たれる。

- **Cons / Risks**:
  - ChartView v1 の Image rect ベースを破壊する。CHARTRENDER-01..03（findings 0111・前回 commit `a437523` で landした live regression gate）の `Candles 配下 2*n rect` assertion が確実に RED 化 → migration が前提。
  - ThemeProbe / ThemeHitlHarness の `FirstCandle(bool)→Image` seam が壊れる → `FirstCandleColor(bool)→Color` へ置換が前提。
  - Replay 全期間 cold load は engine 側に変更（findings 0104 経路の拡張）。Live の挙動には触らない。
  - `MaskableGraphic` サブクラスは uGUI batching の振る舞いを理解した実装が要る（`SetVerticesDirty` / `SetMaterialDirty` / `OnPopulateMesh` の責務分離）。

- **Alternatives considered**:
  - **(A) Image rect + オブジェクトプール + 視窓仮想化**: ChartView v1 を最小侵襲で拡張。`Stack<Image>` プール、visible 範囲だけ enable。
    - 棄却理由: GPU draw call は 1 にならない（uGUI batch 1 個 + Image GameObject ~200 個常駐）。owner の「理想形」「手を抜くな」指示と矛盾。flowsurface ShapePainter の Unity 直訳としても弱い。
  - **(C) 3D Mesh + LineRenderer**: シーン空間で描く。
    - 棄却理由: Hakoniwa の Sprite 系 UI と座標系が混ざる。uGUI の Canvas raycast / modal layer / theme system と統合しづらい。

- **方針: 本 ADR は decision を**固定**する。** R_SNAP / D_DETACH と同じく、`MIN/MAX/DEFAULT_CELL_WIDTH_PX` の最終値・`calc_optimal_price_ticks` の最適刻み係数・`auto_scale` の収束ヒステリシス・layout sidecar スキーマの version 番号・crosshair badge の z オーダー・gutter 余白・easing 関数などの下位事実は本 ADR に書き戻さず、`docs/findings/0119` に記録し本 ADR を「方針: ADR-0034」として参照する。

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす（先例: [[ADR-0024]] / [[ADR-0029]] / [[ADR-0032]]）。
