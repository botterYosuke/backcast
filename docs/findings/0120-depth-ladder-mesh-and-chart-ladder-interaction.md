# findings 0120 — DepthLadderView Mesh 化と chart↔ladder インタラクションの下位決定

2026-06-26。[[ADR-0035]] を方針として、live モードの chart+ladder 複合 widget を [[ADR-0034]] と同基準（単一 CanvasRenderer + Mesh + theme single-source）へ揃える slice の下位事実集。[[findings 0119]]（ChartView 側）と対になる。

## 起点

owner: 「live 用の ladder 付きチャートも対象にして」（2026-06-26・[[ADR-0034]] と同 all-in 指示「実装コスト度外視・理想形・手を抜くな」を継承）。

ChartView だけ Mesh 化して隣の DepthLadderView を Image rect のまま残すと:
- 1 chart window 内の左右 sibling で描画方式が混在
- bid/ask 色が chart の BULLISH/BEARISH と独立参照 → theme 切替で食い違いが出るリスク
- chart の hover 価格を ladder 側に視覚連動する seam が無いため flowsurface 流 UX が出ない
- 板差分の視覚化（Bookmap/Hyperliquid 系）が無い

→ DepthLadderView も Mesh 化、色を single-source 化、chart↔ladder インタラクションを v1 に同梱、を [[ADR-0035]] で確定。

## 下位決定（grill 本セッションで固定）

### D-9 DepthLadderView Mesh 化

- DepthLadderView を **`MaskableGraphic`** サブクラスへ書き直し（`Image` ではなく `Graphic` 系）。
- `OnPopulateMesh(VertexHelper vh)` で **21 行の per-side alpha 背景 quad + LAST 行 bg + 差分 highlight tint quad + hover highlight tint quad** を 1 つのバッチに統合。drawcall=1。
- **`Text` (label) は uGUI Text のまま retain**（TMP/SDF 移行は別 slice）。Text は Canvas が別 sub-mesh で扱うので Mesh 統合の利得は bg quad だけ。
- 21 行は固定なので [[視窓仮想化（virtualized chart）]] は適用しない（全行常時可視）。
- `Text` 配列を `_rowTexts[21]` 等で **保持して in-place 更新**（despawn+respawn しない）。findings 0024 v1 の Image GO 再利用と同思想を Text 層で実現。
- `_lastSnapshot` を 1 frame 分保持して D-11 の diff 計算に渡す。
- `SetVerticesDirty()` の trigger 源は (a) Render(snapshot) 呼び出し時 (b) hover state 変化 (c) Timer 進行（差分 highlight 減衰）。毎フレーム dirty は避ける。

### D-10 Bid/Ask 色の single-source 化

- 共通色定義の置き場: ChartView が `public static class ChartPalette` を新設し `BULLISH_CANDLE_COLOR` / `BEARISH_CANDLE_COLOR` を `Color` const として公開。
- 既定値は ThemeService 連動の getter（`ChartPalette.Bullish() => ThemeService.Current.colors.hakoniwa_up`）。
- DepthLadderView は Bid = `ChartPalette.Bullish()` / Ask = `ChartPalette.Bearish()` を参照。`ThemeService.Current.colors.hakoniwa_up` への直接参照は禁止。
- `findings 0054 P1`（cream-legible 規約・Bid=hakoniwa_up / Ask=hakoniwa_down）はそのまま継承。色の意味（Bid=上=Bullish）は変えない。

### D-11 chart↔ladder インタラクション seam = CrosshairState 共有 SoT

- `CrosshairState` Component（[[ADR-0034]] の D-6 で導入）の **所有を chart+ladder 複合 widget の親 Component に外出し**。
- 親 Component = `ChartLadderRoot`（仮称・`BuildChartContent` が chart window root に張る）。chartArea と ladderArea の sibling 親。
- ChartView は親の `CrosshairState` を書き、DepthLadderView は同じ親の `CrosshairState` を読む。両者は同じ root の子なので `GetComponentInParent<ChartLadderRoot>()` で取得。
- **chart → ladder の単方向**。ladder hover で chart に逆 highlight は v1 スコープ外（別 slice）。
- 同一 instrument を別 chart window で開いた場合、各 window が独立した `ChartLadderRoot` を持つので独立した CrosshairState。
- DepthLadderView は `hovered_price` を受け取ったら、ask 行 / bid 行 / LAST 行のうち **その price に最近接の 1 行**を highlight（Mesh バッチに hover tint quad を加える）。複数行ヒットは出さない。LAST 行に近ければ LAST を highlight。

### D-12 板差分 highlight（size 変化の減衰 tint）

- DepthLadderView は `_lastSnapshot` を保持し、`Render(newSnapshot)` で **price key で同一視**して level ごとに `size` 差分を計算。
- price key 同一視: `Mathf.Abs(price_a - price_b) < PRICE_KEY_EPSILON`（既定 0.01 倍 size の tick）。flowsurface skill caveat #5 / #7（価格 binning の精度・Decimal or i64 で扱う）に従う。
- 差分が `MIN_SIZE_DIFF_TO_HIGHLIGHT` 以上（既定 1 株）の level に **`DEPTH_DIFF_HIGHLIGHT_MS = 300ms` の Timer** で alpha が落ちる tint quad を Mesh バッチへ加える。linear 減衰（`alpha = remaining_ms / DEPTH_DIFF_HIGHLIGHT_MS`）。
- size 増加 = Bid/Ask 色を高 alpha でブースト（基底 row bg `alpha=0.22` に追加で `+0.5*decay`）。
- size 減少 = grey tint（`Color(0.5f, 0.5f, 0.5f, 0.5*decay)`）。
- Timer は MonoBehaviour `Update` で per-frame に `remaining_ms -= Time.deltaTime * 1000`。`remaining_ms <= 0` で要素を `Dictionary<price_key, Timer>` から削除。
- Snapshot ごとに best が ticked up/down するケース（price が変わって size 比較対象が無い）でも、price key で同一視できなければ「**新規 level = 増加扱い**」「**消えた level = 減少扱い**」で highlight。
- Timer は per-level・per-side（bid と ask は別 dictionary）。最大 20 level 同時 highlight だが Mesh バッチに統合されるので drawcall は変わらない。

### D-13 公開 API（E2E / Theme probe seam）

```csharp
public Color BestBidColor { get; }                       // 旧 BestBid()→Text の Color seam 置換
public Color BestAskColor { get; }                       // 旧 BestAsk()→Text の Color seam 置換
public Color LastRowColor { get; }                       // 旧 LastRow()→Text の Color seam 置換
public int RowCount { get; }                             // Mesh で描いた可視行数（常時 21、placeholder 時は 1）
public Color GetRowHighlightTint(int rowIndex)           // 板差分 + hover highlight の現在 tint Color（消えていれば Color.clear）
public DepthSnapshotView CurrentSnapshot { get; }        // 直近 Render に渡した snapshot（diff 計算の test 介入点）
```

旧 `Image Background` / `Text BestBid()` / `Text BestAsk()` / `Text LastRow()` は **撤廃**。

### D-14 E2E migration

#### 既存 E2E の書き換え（findings 0024 / 0094 / 0111 系）

| Action ID | 旧 assertion | 新 assertion |
|---|---|---|
| DEPTH-01（DepthLadderE2ERunner） | `_rowsRoot.childCount == 21`（行 Image GO 数）| `lv.RowCount == 21` |
| DEPTH-02 | `BestBid().color == hakoniwa_up` | `lv.BestBidColor == ChartPalette.Bullish()` |
| ADDLADDER-03 | `_depthLadders[7203] != null`（既存配線確認）| 変更なし |
| ADDLADDER-04/05 | `_depthLadders[7203].gameObject.activeSelf` | 変更なし（`_lastLadderLive` 配線は無改変） |
| THEME-LADDER-BID/ASK/BG（ThemeProbe） | `BestBid().color == hakoniwa_up` | `lv.BestBidColor == ChartPalette.Bullish()` |

#### 新 gate

`behavior-to-e2e` で以下を land:

- **LADDER-RENDER-01**: Mesh で 21 行（10 ask + LAST + 10 bid）の bg quad が 1 つの UIVertex バッチに統合され drawcall=1。
- **LADDER-RENDER-02**: placeholder（!HasDepth）時に `RowCount == 1` で「(no board)」プレースホルダ。
- **HOVER-LADDER-01**: ChartView の `CrosshairState.hovered_price = 480.5` を立てる → 最近接 ladder 行（例: bid[0] @ 480.4）の `GetRowHighlightTint(rowIndex)` が hover tint Color。hover 抜けで `Color.clear`。
- **LADDER-DIFF-01**: snapshot1（bid[0] size=100）→ snapshot2（bid[0] size=200）→ 即時 `GetRowHighlightTint(bid[0])` が増加 tint（alpha ≈ 1.0 * decay）。
- **LADDER-DIFF-02**: 同 snapshot を 300ms 後にもう一度 Render → tint が `Color.clear` に減衰しきっている（Timer 減衰）。
- **LADDER-PALETTE-01**: Bid 色 == `ChartPalette.Bullish()`（ChartView と single-source、theme 切替で同時に変わる）。

## 実装スコープ（順序・[[findings 0119]] と並行）

| 順 | 内容 | gate |
|---|---|---|
| 1 | ADR 0035 + finding 0120 + CONTEXT.md glossary を land | docs only |
| 2 | behavior-to-e2e で LADDER-RENDER-01/02 / HOVER-LADDER-01 / LADDER-DIFF-01/02 / LADDER-PALETTE-01 を RED で land、DEPTH/ADDLADDER/THEME 系を新 assertion へ書き換え | E2E AFK RED |
| 3 | ChartPalette 抽出（D-10）。DepthLadderView 参照を ChartPalette へ移行 | LADDER-PALETTE-01 GREEN |
| 4 | DepthLadderView を Mesh ベース実装（D-9）。Text 配列の in-place 更新、bg quad の UIVertex バッチ統合 | LADDER-RENDER-01/02 GREEN |
| 5 | ChartLadderRoot Component 新設、CrosshairState 共有 SoT 化（D-11） | HOVER-LADDER-01 GREEN |
| 6 | 板差分 highlight 実装（D-12）。Timer 減衰 / price key 同一視 | LADDER-DIFF-01/02 GREEN |
| 7 | merged rollup で全 GREEN | rollup GREEN |
| 8 | code-review(simplify) + pair-relay で Medium+ ゼロ化 | review GREEN |

## RED→GREEN litmus（delete-the-production-logic）

- D-9 を壊す: `OnPopulateMesh` の bg quad を no-op → LADDER-RENDER-01 RED。
- D-10 を壊す: ChartPalette 経由でなく直接 `hakoniwa_up` を参照 → LADDER-PALETTE-01 RED（theme 切替で chart と ladder が分離）。
- D-11 を壊す: DepthLadderView が `CrosshairState` を読まない → HOVER-LADDER-01 RED。
- D-12 を壊す:
  - diff 計算を no-op → LADDER-DIFF-01 RED。
  - Timer を進めない（`remaining_ms` を減らさない） → LADDER-DIFF-02 RED（300ms 後も tint が残る）。

## 範囲外（明示）

- ladder hover で chart に逆 highlight する双方向 interaction（v2 で別 slice）。
- DepthLadderView の Text を TMP/SDF へ移行（別 issue・#119 系と整合）。
- 板差分 highlight の音響キュー（flowsurface `audio.rs` 翻訳・別 slice）。
- DOM heatmap / footprint cluster（ChartView 側 [[findings 0119]] と同じく別 slice）。
