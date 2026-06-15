# findings 0023 — candlestick 描画を本番コンポーネント `ChartView` に抽出（#53）

方針: ADR-0005（1:1 surface parity・chart は明示 in-scope サーフェス）/ ADR-0001（Unity+pythonnet frontend）。
移植元 parity 参照: TTWR `src/ui/chart/render_main.rs`・`title_bar.rs`・`axes_labels.rs`。
本ファイルが #53 スライスの正本。ADR-0005 は自己保護条項により**編集せず**、本 findings から「方針: ADR-0005」で参照する。

> **マージ後の注記（origin/main #50 nautilus 全撤去との合流時）**: #53 抽出後に #50（findings
> 0022-nautilus-full-removal）がマージされ、nautilus ベースの throwaway harness `ReplayChartHarness` /
> `ReplayPanelsHarness` は**削除**された（kernel-native の `ScenarioStartupHitlHarness`（#29）が後継）。
> よって `ChartView` の現実の consumer は **`ScenarioStartupHitlHarness` + #44 montage（`ThemeHitlHarness`）**
> の 2 つに収束。本抽出により両者が同一部品を共有し AC②③ を満たす構図は不変。本 findings 内で
> 「3 harness」と記す箇所は抽出時点（#50 マージ前）の記録として読むこと。

## 結論（grill-with-docs 2026-06-15・owner 確定）

candle/軸/タイトルの描画を、3 harness に三重コピペされていた状態から、単一の本番コンポーネント
`Assets/Scripts/Chart/ChartView.cs`（MonoBehaviour）へ集約した。3 harness（ReplayChartHarness /
ReplayPanelsHarness / ScenarioStartupHitlHarness）と #44 montage（ThemeHitlHarness）が**同一部品**を
使い、`ThemeService.Changed` を部品側で購読して theme 切替に追従する。検証（AFK ThemeProbe）は
**偽 Swatch ではなく本番 ChartView の candle Image** を sample する。

## ハード証拠（実機照合）

- **三重コピペの実態**: `RenderCandles` + `AddRect` + chart UI 構築が 3 harness にピクセル単位で重複。
  candle 算術（autoscale=visible min/max・wick=high/low・body=open/close・`close>=open?long:short`）は
  完全同一で、違いは「親 rect の作り方」だけ（全面 inset / 0.62 パネル+`_plotArea` 子 inset / Hakoniwa tile）。
- **#44 の検証ギャップ**: montage の candle は本物ではなく偽 `Swatch` 矩形（`candle_up`/`candle_down`）で、
  `ThemeProbe`（findings 0020 Q9 §4c）もその偽矩形を assert していた。本番 candle 部品の色切替は**誰も
  検証していなかった**。
- **theme build-once バグ（findings 0018 L1）**: 3 harness とも色を
  `static readonly Color UP_COLOR = ThemeService.Current.status.@long;` で型初期化時に 1 回だけ捕捉し、
  `Changed` を購読していないため切替に追従しなかった。
- **TTWR には「ChartView 部品」が無い**: TTWR の chart は immediate-mode の Bevy system
  （`chart_main_render_system` が毎フレーム `ShapePainter` で read-only `ChartViewState` から全 candle を
  描き直す）＋ `ChartViewState`。retain される部品ではなく「状態 + 毎フレーム関数」。

## parity の切り分け（重要）

| 側面 | parity 関係 |
|---|---|
| candle 色ロール（up=status.long / down=status.short） | TTWR `render_main.rs` 同値（`theme.status.long/short`） |
| candle 幾何（wick=high/low・body=open/close・doji 最低 1.5px相当の min 1px） | TTWR 同型 |
| autoscale（visible min/max） | TTWR `ChartViewState` 同趣旨（実装は別） |
| title bar 価格/変化率（`{:.2}` price・符号付き `{:.2}%`・long/short 着色・base 0 で「—」） | TTWR `title_bar.rs`（`format_chart_title_price` / `format_change_pct` / `chart_title_price_system`）忠実 |
| **retained uGUI 構造（Image を保持・bar 増加時のみ rebuild）** | **backcast 独自**。TTWR は immediate-mode で「部品」概念が無く、Unity に「建て済み UI の毎フレーム自動再描画」が無いため。findings 0020 の「切替伝播は backcast 独自」と同種の framework 差による強制逸脱 |
| **`ThemeService.Changed` 購読 → `ApplyTheme()`** | **backcast 独自**（findings 0020 と同根） |

## 逸脱の記録（parity-first 原則）

1. **retained vs immediate-mode**: 上表のとおり framework 差による強制逸脱。parity は色ロール・幾何・
   autoscale・title 意味のレベルで取り、毎フレーム再描画方式は写さない。
2. **x 軸の統一**: ReplayChart/Panels は `x = (open_time_ms - minT)/timeRange`（時間正規化）、
   ScenarioStartup は `x = i/(n-1)`（index 等間隔）と**元々 2 方式が混在**していた。owner 確定で
   **時間正規化に統一**（ScenarioStartup が index→時間正規化へ変化）。連続 daily bar では両者ほぼ一致する
   ため視覚回帰は軽微。なお TTWR の `interval_to_x` は厳密には interval-index ベース（`ChartViewState`/
   `cell_width` 依存）で、どちらの harness 方式とも完全一致はしない。**厳密な interval-x マッピングは
   `ChartViewState` 移植を要するため follow-up（下記）に同梱**。
3. **ScenarioStartup tile に軸 gutter が付く**: 従来 gutter 無しで candle がタイル全面だったが、ChartView
   共通化で左 60 / 下 40 等の gutter inset が入る。小タイルでは占有が大きいが HITL harness として許容。
4. **scope=既存の集約のみ**: TTWR `render_main.rs` のグリッド線・境界線・last-price 破線、
   `axes_labels.rs` の軸目盛ラベルは backcast 未実装。今回の抽出には含めず follow-up に切り出し
   （みなしご防止）。title bar（価格/変化率）は owner 確定で v1 に含めた。

## ChartView API（確定）

- `Build(RectTransform parent, bool showTitleBar)` — parent 内に bg＋軸線2本＋（showTitleBar 時のみ
  上端 24px 帯：CHART ラベル/価格/変化率）＋candle root を組む。axis gutter は**部品内部**（親 rect は
  offset-zero のまま＝ReplayPanels の persisted-layout 不変条件を保つ）。
- `Render(ReplayBarFrame frame)` — 上記算術で candle を rebuild（bar 数が変わったときだけ呼ぶのは
  呼び出し側の既存ガード）。title 有効時は last close と `(last.close-first.open)/first.open` を更新。
- `ApplyTheme()` — bg/軸/既存 candle（up/down を内部 list で保持し再着色）/title を `ThemeService.Current`
  から塗り直す。`Build` 内で `ThemeService.Changed += ApplyTheme`、`OnDestroy` で解除（`StrategyEditorView`
  と同じ作法）。
- probe seam: `Background`（bg Image）・`FirstCandle(bool bullish)`（該当方向の最初の candle Image）。
  montage が 2-bar mock（bearish 1・bullish 1）を Render し、ThemeProbe が本番 candle 色を sample する。

## 検証（AC④）

- **AFK `ThemeProbe.Run`**（findings 0020 §4c を更新）: montage の `chart_bg`/`candle_up`/`candle_down` が
  **本番 ChartView の graphics** を指すようになり、dark→NonDefault 切替で本番 candle 色が追従することを
  非空虚に kill。`candle_up`/`candle_down` の非 null も assert。
- **decode 回帰**（`ReplayChartDecodeProbe`）は不変。
- **HITL 目視**: 3 harness（ReplayChart 全面 / ReplayPanels パネル / ScenarioStartup タイル）と
  ThemeHitlHarness montage で candle/title が描画され、montage の T トグルで本番 candle 色が切替わること。

## follow-up（みなしご防止・別 issue 起票）

1. **TTWR chart chrome の移植**（→ **#56** 起票済み）: グリッド線・境界線（`render_main.rs
   draw_grid_and_borders`）・軸目盛ラベル（`axes_labels.rs`）・last-price 破線（`dashed_segments`）・
   厳密な interval-x マッピング（`ChartViewState`/`cell_width`）。
2. **panel 配色の theme 追従**: ReplayPanels の右カラム（`PANEL_BG`/`TEXT_COLOR`）と ScenarioStartup の
   status text（`TEXT`）は #53 scope 外のため build-once（static 捕捉）のまま温存。#44 の AC②（全 UI 追従）
   完遂として別途。
