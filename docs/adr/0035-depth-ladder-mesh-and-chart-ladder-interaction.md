---
status: proposed
---

# DepthLadderView を Mesh ベースに置換し、chart↔ladder インタラクション（hover 価格 highlight・板差分 highlight 減衰）を載せる

owner 依頼（2026-06-26）「live 用の ladder 付きチャートも対象にして（理想形・手を抜くな）」を `to-issues` の slice 検討中に設計ロックした決定。

これは [[ADR-0034]]（ChartView を単一 CanvasRenderer + Mesh + 視窓仮想化へ置換）の **下位/兄弟決定**で、ChartView と同じ "Image rect destroy/recreate" の根本問題を抱える **DepthLadderView** にも同方針を波及させ、Live モードの **chart+ladder 複合 widget**（`BuildChartContent` で 1 chart window 内に sibling 配置）を理想形に揃える。

加えて flowsurface 流の **chart↔ladder インタラクション**（chart 上で hover した bar の close 価格を ladder の対応行に highlight・板の size 差分を Timer で減衰させる highlight）を本 ADR スコープに含める。

関連: [[ADR-0034]]（ChartView Mesh 化・本 ADR は同方針を ladder に波及）／findings 0119（ChartView 下位事実）／findings 0120（本 ADR の下位事実・実装スコープ・E2E migration）／findings 0024（DepthLadderView v1 抽出・本 ADR で方式置換）／findings 0094（AddChartLadderJourney・本 ADR で probe seam 移行）／findings 0111（KabuLiveChartRender E2E ゲート）／flowsurface skill 対応表（`F:data/src/panel/ladder.rs` / `F:src/widget/chart/heatmap/` 板差分 highlight caveat #4）。

## Context

[[ADR-0034]] が ChartView を Mesh ベースへ置換する一方、Live モードの板表示を担う **DepthLadderView**（`Assets/Scripts/Live/DepthLadderView.cs`・findings 0024 / #54 で抽出）は v1 のまま:

- `Render(snapshot, lastPrice)` が呼ばれるたび `_rowsRoot.GetChild` を**全 destroy**、21 行（10 ask + LAST + 10 bid）の **`Image` (bg) + `Text` (label) を `new GameObject`**（合計 ~42 GO/render）。
- Bid/Ask の色は `ThemeService.Current.colors.hakoniwa_up` / `hakoniwa_down`（findings 0054 P1）を独自に参照。**ChartView の BULLISH/BEARISH と同 source ではない**（flowsurface 翻訳の single-source 原則違反）。
- chart 上で hover した bar の価格を ladder で highlight する経路は **無い**（chart と ladder は読み取り側で独立）。
- 板の差分（size 変化）を視覚化する一時 highlight も **無い**。

Live モードでは `BuildChartContent`（`BackcastWorkspaceRoot.cs:722`）が 1 chart window 内に **ChartView (chart area)** + **DepthLadderView (ladder area・右側 inset)** を sibling 配置する。`_lastLadderLive` フラグで Live = active+inset / Replay = hidden+full-width にトグル。owner の "理想形" 指示を受けると、ChartView だけ Mesh ベース化して隣の DepthLadderView を Image rect のまま残すのは「手を抜く」ことになり整合しない。

加えて flowsurface の `chart_ladder_pane.rs`（[[ADR-0034]] §1 で参照した skill 対応表 Phase F）は chart と ladder を 1 つの ChartViewState と CrosshairState で共有し、hover 中の `hovered_price` を ladder 表示と連動させる。板差分 highlight は同 skill caveat #4 が指摘する Bevy 流パターン（`Timer` Component で減衰 despawn）。これらを Unity に直訳する。

## Decision

**5 点**を固定する。後段の細部は findings 0120 に記録し、本 ADR は方針だけ持つ。

1. **DepthLadderView Mesh 化**: DepthLadderView を **単一 `CanvasRenderer` + custom `MaskableGraphic` サブクラス** へ書き直す。`OnPopulateMesh(VertexHelper vh)` で **21 行の per-side alpha 背景 quad + LAST 行 bg + 差分 highlight tint quad** を **1 つの UIVertex バッチへ統合**し **GPU draw call=1**。`Image` (bg) の per-row 生成は撤廃。**`Text` (label) は uGUI Text のまま retain**（TMP/SDF への移行は本 ADR スコープ外。Text glyph batching は Canvas が別 sub-mesh で扱うので Mesh 統合の利得は bg quad だけ）。21 行は固定なので [[ADR-0034]] の視窓仮想化は **適用しない**（全行常時可視）。`AddRow` の despawn+respawn は **`Text` 配列を保持して in-place 更新**へ書き換える（findings 0024 v1 の Image GO 再利用と同思想を Text 層で実現）。

2. **Bid/Ask 色を ChartView と single-source 化**: DepthLadderView の Bid 色 = `ChartView.BULLISH_CANDLE_COLOR` / Ask 色 = `ChartView.BEARISH_CANDLE_COLOR`。両者は同じ const か `Resource<ChartPalette>` を参照する。`ThemeService.Current.colors.hakoniwa_up` / `hakoniwa_down` を ladder と chart が **独立に参照**するのを退役（flowsurface skill 翻訳の single-source 原則）。findings 0054 P1 の cream-legible 規約はそのまま継承。

3. **chart↔ladder インタラクション seam = `CrosshairState`（共有 SoT）**: ChartView の `CrosshairState`（[[ADR-0034]] §6 で導入予定の hover 状態 pure data）を、chart+ladder 複合 widget の **共有 SoT**として持つ。**所有は複合 widget の親**（`BuildChartContent` が生成する chart window root に張る Component）。ChartView は hover 中 `hovered_price` を書き、DepthLadderView は同じ Component を読んで **対応行の highlight tint quad を Mesh バッチに加える**。hover 抜けで tint quad を消す。これは双方向ではなく **chart → ladder の単方向**（ladder hover で chart に逆 highlight は v1 スコープ外）。

4. **板差分 highlight（size 変化の減衰 tint）**: DepthLadderView は受信した DepthSnapshot を `_lastSnapshot` で 1 frame 分保持し、次フレームで level ごとに `size` 差分を計算。**差分が `MIN_SIZE_DIFF_TO_HIGHLIGHT` 以上**（既定 1 株）の level に **300ms の Timer**（`DEPTH_DIFF_HIGHLIGHT_MS`・linear 減衰）で alpha が落ちる tint quad を Mesh バッチへ加える。size 増加 = Bid/Ask 色を高 alpha でブースト、size 減少 = grey tint（findings 0024 の grey scale を流用）。Timer は MonoBehaviour の `Update` で per-frame 進める（Bevy `Timer` Component の Unity 翻訳）。Snapshot ごとに level price が変わるケース（best が ticked up/down）でも、price key で同一視（level 配列 index ではない・skill caveat #5）。

5. **DepthLadderView 公開 API（E2E / Theme probe seam migration）**: `Image Background` / `Text BestBid()` / `Text BestAsk()` / `Text LastRow()` を撤廃し、以下を新設:
   - `Color BestBidColor { get; }` / `Color BestAskColor { get; }` / `Color LastRowColor { get; }` — ThemeProbe の Color seam 置換。
   - `int RowCount { get; }` — Mesh で描いた可視行数（常時 21、placeholder 表示時は 1）。
   - `Color GetRowHighlightTint(int rowIndex)` — 板差分 / hover highlight の現在 tint Color（消えていれば `Color.clear`）。E2E が assert する。
   - `DepthSnapshotView CurrentSnapshot { get; }` — 直近 Render に渡した snapshot（diff 計算の test 介入点）。
   - 配線（`BuildChartContent` の `_lastLadderLive` トグル / `ladderAreaGo.SetActive` / `chartArea.offsetMax.x=-LADDER_WIDTH`）は**無改変**。既存 E2E（ADDLADDER-01..05 / DEPTH-01/02）は assertion を新 API へ書き換える。

## Consequences

- **Pros**:
  - Live モードの chart+ladder 複合 widget が ChartView と同じ「1 GameObject + 1 drawcall + Mesh in-place 更新」基準に揃う。
  - Bid/Ask 色の single-source 化で「ChartView と DepthLadderView の色が theme 切替で食い違う」事故が構造的に消える。
  - chart hover 価格の ladder 連動（flowsurface UX）と板差分 highlight（Bookmap / Hyperliquid 風）が同時に手に入る。
  - 既存配線（`BuildChartContent` / `_lastLadderLive` / `ApplyDepthLadderMode`）を触らず seam だけ移行。

- **Cons / Risks**:
  - DepthLadderView v1（findings 0024 / #54）の Image rect ベースを破壊する。
  - ADDLADDER-01..05（findings 0094）/ DEPTH-01/02（findings 0024）/ THEME（ladder_bg / ladder_bid / ladder_ask）の既存 E2E が **assertion 書き換え必須**。
  - `CrosshairState` 共有 SoT は chart+ladder 複合 widget の親 Component に置くため、chart window root の Component 設計が 1 段増える（v1 では ChartView が自分で持てば済むが、共有 SoT 化で外出し）。

- **Alternatives considered**:
  - **(A) DepthLadderView を Image rect プールにとどめる**: 21 行固定なのでプールも仮想化も既に不要に近い。Mesh 化の利得は 21 quad 程度。
    - 棄却理由: ChartView と方式が分かれ「色の single-source 化」と「Mesh バッチ統合 highlight」が中途半端になる。owner の「理想形」「手を抜くな」指示と矛盾。
  - **(B) chart↔ladder インタラクションを ChartView と DepthLadderView の bidirectional Component 通信で書く**: ChartView が DepthLadderView の参照を直接持つ。
    - 棄却理由: 複数 chart window を同 instrument で開いたとき、どの ladder と紐づくか曖昧。`CrosshairState` を**複合 widget の親 Component** に置けば「同じ親」=「同じ複合 widget」で 1 対 1 に決定する（skill caveat: per-window 独立）。

- **方針: 本 ADR は decision を**固定**する。** `MIN_SIZE_DIFF_TO_HIGHLIGHT` / `DEPTH_DIFF_HIGHLIGHT_MS` / size 増減の tint alpha 係数・hover highlight の alpha・price key 同一視のヒステリシス（near-equal の epsilon）などの下位事実は本 ADR に書き戻さず、`docs/findings/0120` に記録し本 ADR を「方針: ADR-0035」として参照する。

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす（先例: [[ADR-0024]] / [[ADR-0029]] / [[ADR-0032]] / [[ADR-0034]]）。
