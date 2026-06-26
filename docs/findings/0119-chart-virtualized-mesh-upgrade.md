# findings 0119 — Chart アップグレード（仮想化 Mesh 化 + 全期間 + ゴージャス要素）の設計の木

2026-06-26。owner 依頼「全期間 / オブジェクト再利用 / スクロール可能 / 仮想スクロール / 軽量でゴージャス」＋ all-in 指示（実装コスト度外視・理想形・手を抜くな）を `grill-with-docs` で設計ロックした下位事実集。**方針は [[ADR-0034]]**、本 finding はその下位決定・実装スコープ・E2E migration・実装順序を持つ。

## 起点（owner 文言の分解）

| 要望 | 設計反映 |
|---|---|
| 全期間チャートに起こしたとしてもデフォルトで見える範囲を考え直して軽くする | ChartViewState + 視窓仮想化 + 右端 anchor 初期化（D-2 / D-4） |
| オブジェクトの再利用などUnityの軽量化テクニックを使う | 単一 CanvasRenderer + Mesh で全 candle を UIVertex バッチに統合（D-1）。「再利用」は Image GO の再利用ではなく **頂点配列の in-place 更新** に翻訳 |
| チャートをスクロール可能にする | drag pan + wheel zoom + double-click reset（D-3）。ScrollRect は不採用（ADR-0034 §3） |
| 仮想スクロールで軽くする | visible window 内の bar だけ vertex を生成（D-2）。視窓外は計算スキップ |
| 軽量でゴージャス | grid / 軸ラベル / crosshair / 出来高サブペイン / last-price line を v1 で全部入れる（D-6） |
| 実装コスト度外視・理想形・手を抜くな | 最小侵襲案 (A) Image rect プール + 仮想化 を**棄却**。Mesh ベース置換に倒す（ADR-0034 §1） |

## 下位決定（grill 本セッションで固定）

### D-1 描画方式: 単一 CanvasRenderer + Mesh + UIVertex バッチ

- ChartView を **`MaskableGraphic`** サブクラスへ書き直す（`Image` 継承ではなく `Graphic` 系）。
- `OnPopulateMesh(VertexHelper vh)` で visible window の **全 candle (wick quad + body quad) + grid line + volume bar** を 1 つのバッチに統合。
- `SetVerticesDirty()` で再生成 trigger。Trigger 源は (a) ChartViewState の変化（pan/zoom/reset）(b) `TotalBarCount` の変化（新 bar）。**毎フレーム再生成ではない**（uGUI batching の dirty コストを抑える）。
- `Mesh` インスタンスは `CanvasRenderer.SetMesh()` に渡し、頂点配列 (`List<UIVertex>`) を **保持して in-place 更新**（capacity 増のときだけ拡張・findings 0023 v1 の Image GO 再利用と同思想を vertex 層で実現）。

### D-2 ChartViewState（per-window 独立な pure data）

- フィールド:
  - `translation_ms: long` — visible window の起点 timestamp（左端の bar.open_time_ms）
  - `cell_width_px: float` — 1 bar が横方向に占める画面 px（`MIN=1.0` / `DEFAULT=6.0` / `MAX=64.0` で clamp）
  - `auto_scale: bool` — pan/zoom 開始で false、ResetView で true
  - `basis_ms: long?` — granularity 由来の派生値（`Daily→86_400_000` / `Minute→60_000` / 未知→null=維持）
  - `cell_height_norm: float` — `(plot_height_px - volume_height_px) * tick_size / price_range`（autoscale 時のみ書き換え）
- 派生関数:
  - `visible_time_range() → (start_ms, end_ms)` = `(translation_ms, translation_ms + plot_width_px / cell_width_px * basis_ms)`
  - `visible_price_range() → (min, max)` = visible bar の low/high から fit（autoscale 時のみ）
  - `price_to_y(price) → px` / `y_to_price(y) → price`
  - `time_to_x(ms) → px` / `x_to_time(x) → ms`
- per-window で独立: 同じ instrument を別 window で開けば独立 ChartViewState。
- **v1 制約 (#155-163)**: production は `BackcastWorkspaceRoot._chartViews` Dictionary keyed by `chart:<iid>` で **1 chart-window per iid**。同 iid を別 window で開く v2 拡張は `chart:<iid>#<n>` 命名で別 slice。本 finding の sidecar schema は v2 拡張に対応済（FW entry ごとに独立 chart_view_state）。

### D-3 UX: drag pan / wheel zoom / double-click reset

- 入力 handler 実装:
  - `IPointerDownHandler` / `IDragHandler` / `IPointerUpHandler` で **左ボタンドラッグ** のみ受ける（右/中は parent floating window の drag に流す = `eventData.button != PointerEventData.InputButton.Left` で early-return）。
  - `IScrollHandler.OnScroll` でホイール。Ctrl 修飾は parent の PanCam 全体ズームへ譲る（chart は無反応）。`Mathf.Pow(1.1f, eventData.scrollDelta.y)` で `cell_width_px` を更新、cursor 位置を中心に補正（flowsurface `apply_cursor_zoom` の翻訳）。
  - `IPointerClickHandler` で **ダブルクリック検出**（`eventData.clickCount == 2`） → `ChartViewState.ResetView()`。drag 直後の click を除外するため `_dragged` フラグを `IPointerDownHandler` でクリア → drag 中に立てる → click handler で参照（Bevy `Pointer<Click>` の drag 後 click 罠と同じ・flowsurface `ChartClickState.dragged` の翻訳）。
- pan / zoom 開始で `auto_scale = false`、ResetView で `auto_scale = true`。

### D-4 デフォルト visible 範囲

- 初期 / `ResetView` 時:
  - `cell_width_px = DEFAULT_CELL_WIDTH_PX = 6.0f`
  - `translation_ms = latest_bar.open_time_ms - (plot_width_px / cell_width_px - 1) * basis_ms` → **右端 anchor**（最新 bar が右端に来る）
  - `auto_scale = true`
- plot 幅 540px / cell 6px なら 90 本表示 → Daily で約 4 ヶ月、Minute で約 1.5 時間相当。owner 要望「軽くする」を満たす。

### D-5 全期間データ supply（Replay / Live で別ポリシー）

- **Replay**:
  - engine `_backend_impl.py` / `core.py` の `load_replay_data` 経路で scenario `start..end` の全 bar を DuckDB cold load。
  - `per_id_ohlc_points` を Replay 中は `max_history_len` キャップを **撤廃** または scenario bar 数まで自動拡張（findings 0104 ChartSpawnPreview の preview seed 経路を「scenario 全期間」に拡張）。
  - `InstrumentOhlcDecoder` / state JSON 形は無改変（Decode → ChartView 経路は流用）。
- **Live**:
  - `max_history_len = 1000` を**維持**。kabu historical backfill での「Live 全期間」は本 finding スコープ外（必要なら別 issue）。
  - 当日 + reducer リングバッファに留める。
- これにより RPC payload は Replay でも初期 1 回 cold seed のみ膨れる（以後の poll は per_id 増分だけ）。

### D-6 ゴージャス要素のスコープ（v1 で全部入れる）

| 要素 | flowsurface 参考 | Unity 翻訳 |
|---|---|---|
| price 軸ラベル（右 gutter） | `F:src/chart/scale/linear.rs::calc_optimal_price_ticks` | 純関数を C# 移植・`Text` 子を despawn+respawn・`main_area` のみ |
| time 軸ラベル（下 gutter） | `F:src/chart/scale/timeseries.rs::calc_optimal_time_step` | 同上・basis_ms に応じて min/h/D 単位 |
| グリッドライン | （ShapePainter line） | candle と同じ Mesh バッチへ alpha=0.06 で統合（追加 drawcall 0）。**scope**: price grid は `main_area` 限定（volume_area に価格目盛りは無い）／time grid は `plot` 全体（main_area + volume_area 貫通、flowsurface "MultiSplit で各 pane 独立" 設計に倣い同じ時間軸を candle と volume bar の双方に視覚連結） |
| クロスヘア + readout | `F:src/chart/kline.rs` の crosshair | hover 中の `cursor_world` を持つ・`hovered_price` / `hovered_time_ms` を派生・gutter に badge（Text + 背景 quad） |
| 出来高サブペイン | `F:src/ui/chart.rs::chart_volume` (backcast TTWR oracle) | plot 領域を 80%/20% に分割・volume bar を同 Mesh バッチに統合・candle 色を alpha=0.6 で再利用 |
| last-price dashed line | （TTWR title_bar 関連） | 最新 close 価格に水平点線（gutter まで延びる）・Mesh バッチ統合 |

範囲外（別 slice）: indicator overlay / DOM heatmap / footprint cluster / comparison line。

### D-7 pan/zoom 状態の永続化（layout sidecar）

- 既存 layout sidecar スキーマ（findings 0048 multi-document layout）を拡張:
  ```json
  {
    "windows": [
      {
        "kind": "chart",
        "instrument_id": "9501.TSE",
        "rect": { ... },
        "chart_view_state": {
          "translation_ms": 1761900000000,
          "cell_width_px": 6.0,
          "auto_scale": false
        }
      }
    ],
    "version": 2
  }
  ```
- `version` を 1 上げる（LayoutDocument.CURRENT_VERSION=2 — Assets/Scripts/Layout/LayoutDocument.cs:147-207 の実 schema 参照）。旧 sidecar に `chart_view_state` 無し → `ResetView()` 相当を default で migrate・破棄しない。
- 同 instrument を別 window で開けば各 window が独立した sidecar slot を持つ。
- `cell_height_norm` は autoscale で再計算されるため **永続化しない**（cell_width + auto_scale + translation だけで復元できる）。
- **v1 制約**: 上記 schema は per-FW-entry に独立 chart_view_state を持てるが、production は v1 で 1 chart-window/iid のため per-iid 1 entry のみ書く。v2 で同 iid 二窓を許可するときは production 側で chart:<iid>#<n> spawn ロジックを足し、本 schema は無改変で受け入れる。

### D-8 E2E migration（破壊的変更を gate で守る）

#### 新 public API（ChartView）

```csharp
public int TotalBarCount { get; }           // 受け取った OhlcPoint 全件
public int RenderedBarCount { get; }        // 直近 OnPopulateMesh で visible window 内に描いた件数
public Color FirstCandleColor(bool bullish) // 旧 FirstCandle(bool)→Image の置換
public ChartViewState ViewState { get; }    // test 介入点（translation / cell_width / auto_scale）
```

#### 既存 E2E の書き換え

| Action ID | 旧 assertion | 新 assertion |
|---|---|---|
| CHARTRENDER-01 | `Candles 配下 2*n rect` GameObject child count == 28 | `cv.TotalBarCount == 14 && cv.RenderedBarCount > 0` |
| CHARTRENDER-02 | `Candles 配下 0 child` | `cv.TotalBarCount == 0 && cv.RenderedBarCount == 0` |
| CHARTRENDER-03 | `Candles 配下 0 child` | 同上 |
| THEME-CANDLE-UP/DOWN（ThemeProbe） | `FirstCandle(true).color == th.colors.hakoniwa_up` | `cv.FirstCandleColor(true) == th.colors.hakoniwa_up` |
| AddChartLadderJourney 系 | （chart の child 名で seam を見ていた箇所） | `ChartView` の public API を参照 |

#### 新 gate（VIRTUALIZE / PAN / ZOOM / RESET / PERSIST）

`behavior-to-e2e` で `ChartVirtualizationE2ERunner.cs`（仮）を立て、Action-ID:

- **VIRTUALIZE-01**: TotalBarCount=1000 / cell_width=6px / plot=540px → RenderedBarCount==90（±1）・他の bar は vertex に出ない。
- **PAN-01**: 初期 ViewState.translation_ms=T0 → drag(-100px) → translation_ms == T0 - 100/cell_width*basis_ms・auto_scale=false。
- **ZOOM-01**: 初期 cell_width=6 → wheel(+3 notch) → cell_width == 6 * 1.1^3（≈ 7.99）・cursor 中心保持。
- **RESET-01**: pan/zoom 後 double-click → ChartViewState が `ResetView()` の値（right-anchor / DEFAULT_CELL_WIDTH_PX / auto_scale=true）。
- **PERSIST-01**: chart window の sidecar 保存 → reopen → ChartViewState が往復で復元（version 上げ + migrate も合わせて検証 — LayoutDocument 実 schema との対応は LayoutDocument.cs:147 参照）。

## 実装スコープ（順序）

| 順 | 内容 | gate |
|---|---|---|
| 1 | ADR 0034 + finding 0119 + CONTEXT.md glossary を land | （docs only） |
| 2 | behavior-to-e2e で CHARTRENDER-01..03 migration + VIRTUALIZE / PAN / ZOOM / RESET / PERSIST 新 gate（RED） | E2E AFK RED |
| 3 | ChartView を Mesh ベース実装（D-1 / D-2 / D-3 / D-4）。ThemeProbe / ThemeHitlHarness を新 API へ移行 | THEME / CHARTRENDER GREEN |
| 4 | ゴージャス要素（D-6）を Mesh バッチに統合 | VIRTUALIZE GREEN |
| 5 | Replay 全期間 cold load を engine 側へ（D-5） | pytest GREEN |
| 6 | pan/zoom 永続化（D-7） | PERSIST GREEN |
| 7 | merged rollup `pwsh scripts/run-all-tests.ps1` で全 GREEN | rollup GREEN |
| 8 | code-review(simplify) + pair-relay で Medium+ ゼロ化 | review GREEN |
| 9 | post-impl-skill-update | skill updated |

## RED→GREEN litmus（delete-the-production-logic）

- D-1 を壊す: `OnPopulateMesh` を no-op → VIRTUALIZE-01 / CHARTRENDER-01 RED。
- D-2 を壊す: visible window を無視して全 bar を vertex 化 → VIRTUALIZE-01 RED（RenderedBarCount が TotalBarCount に張り付く）。
- D-3 を壊す:
  - drag handler を抜く → PAN-01 RED。
  - wheel handler を抜く → ZOOM-01 RED。
  - double-click handler を抜く → RESET-01 RED。
- D-7 を壊す: sidecar に `chart_view_state` を書かない → PERSIST-01 RED。
- D-5 を壊す: Replay cold load を no-op → 全期間 RPC payload にならず ChartSpawnPreview の延長 gate（新規）が RED。

## 検証

- `pwsh scripts/run-live-e2e.ps1 -Method ChartVirtualizationE2ERunner.Run` で `[E2E VIRTUALIZE-01 PASS]` / `[E2E PAN-01 PASS]` / `[E2E ZOOM-01 PASS]` / `[E2E RESET-01 PASS]` / `[E2E PERSIST-01 PASS]`。
- `pwsh scripts/run-live-e2e.ps1 -Method KabuLiveChartRenderE2ERunner.Run` で `[E2E CHARTRENDER-01..03 PASS]`（new-API assertion 版）。
- `cd python && uv run pytest tests/test_replay_chart_full_period_seed.py -v`（新規・D-5）。
- `pwsh scripts/run-all-tests.ps1` で merged rollup GREEN。

## 範囲外（明示）

- Live mode での kabu historical backfill（D-5）：別 issue。
- indicator overlay / DOM heatmap / footprint cluster / comparison line：別 slice。
- 3D Mesh + LineRenderer 路線（ADR-0034 alternatives §C）：棄却済。
