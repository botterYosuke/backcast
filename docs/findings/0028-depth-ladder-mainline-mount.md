# findings 0028 — depth ladder を本線 chart tile に per-instrument mount（#57）

issue #57「orderbook: DepthLadderView を本線 scene に載せ替え」。親: #5（Step 3 カットオーバー）/ #14（hakoniwa
tile data binding）/ #54（`DepthLadderView` 抽出・本体）。方針: **ADR-0005（1:1 表面 parity・固定）/ ADR-0001
（Unity+pythonnet frontend・in-proc）**。スキル: `/tachibana` `/kabusapi`（depth 供給の落とし穴）。
移植元 oracle = TTWR `src/ui/chart/overlays_ladder.rs`（`chart_ladder_mode_sync_system` / `ladder_render_system`）。
`grill-with-docs`（2026-06-16）で導出。ADR-0005/0001 は自己保護条項を持つため本 findings に実装事実を記録し、ADR は
参照のみ（書き戻さない）。backcast に FLOWS.md は無いため本 findings ＋ AFK probe ＋ owner HITL leg が behavior gate。

## 0. このスライスの正味（grill で確定したコード実態）

cutover の slice-3 近傍（#23/#39/#57）で #23 re-home（commit `381d58c`）が発注/建玉/Auto パネルと
`ProductionLiveShell` 退役を吸収済み（findings 0014 §再 home / findings 0027 追記）。残る OPEN は **#57 = depth
ladder の本線 mount**。grill で裏取りした実態:

- **`DepthLadderView`（21 行 DOM ladder・LAST 中央・per-side alpha・theme 追従）は #54 追補で完成済み**。`Build(parent)`
  / `Render(DepthSnapshotView, double? lastPrice=null)` / 公開 seam `BestBid()/BestAsk()/LastRow()/Background`。
- **`DepthDecoder.Decode(stateJson, id)`（#26）も完成・durable**。`per_instrument[id].depth` を `PerInstrumentJsonLocator`
  （#60 抽出の共有 scanner）で構造的に切り出す。Replay は `depth:null`→`HasDepth=false`。
- **本線 chart tile host は #60 で完成**: `BackcastWorkspaceRoot.SpawnChartTile(id)` が universe instrument ごとに
  `chart:<id>` タイルを動的生成し、各タイルの `body` に実 `ChartView` を mount、poll loop（`Update` :534）で
  `per_instrument[id].ohlc_points` を per-id 描画。
- **欠けているのは「載せ先」だけ**（#57 issue の記述どおり）: `DepthLadderView` の本線 consumer が無く、`per_instrument[id]
  .depth` / `.price` は poll JSON に在るが mainline では未 decode（OHLC のみ decode 済）。現 consumer は throwaway HITL
  （`DepthLadderHitlHarness`・venue=MOCK ハードコード）と #44 montage のみ。

→ **#57 = 各 chart tile に `DepthLadderView` を per-instrument mount し、Live で chart tile 右帯に板を出して Replay で
隠し、本番 poll→`DepthDecoder.Decode`→`Render` を production で配線する**。部品は完成済。

## 1. 確定事項（grill 2026-06-16）

### (D1) mount geometry = TTWR 忠実（chart 縮小＋右帯 ladder・owner 確定）
TTWR `overlays_ladder.rs:152-226`（`chart_ladder_mode_sync_system`）は Live 中、各 chart tile の右端 `LADDER_WIDTH` 帯に
ladder pane を tile の child として spawn し、chart draw child を左へ縮める（chunk H）。Replay では despawn して chart を
全幅へ戻す（depth は Live 専用・TTWR Phase 8 §0.5.1）。**owner 選択 = option A（TTWR 忠実）**。backcast 実現:

- `SpawnChartTile` で tile `body` を **2 つの sub-rect に分割**: `chartArea`（左・mode で右 inset 可変）＋ `ladderArea`
  （右端 `LADDER_WIDTH` 帯・full height）。`ChartView.Build(chartArea,…)` / `DepthLadderView`（`ladderArea` に AddComponent）
  `.Build(ladderArea)`。
- **Live**: `chartArea.offsetMax.x = -LADDER_WIDTH`（chart が左へ縮む・candle を覆わない）＋ `ladderArea` active。
- **Replay**: `chartArea.offsetMax.x = 0`（全幅復帰）＋ `ladderArea` SetActive(false)（板を隠す）。
- `LADDER_WIDTH = 120f`（TTWR `viewstate::LADDER_WIDTH` 同値）。

> option B（overlay・chart 全幅のまま右に重ねる）/ C（別 tile）は却下: B は Live 中 candle の右端が隠れ Replay と見えが
> 変わる（TTWR 逸脱）、C は AC「chart tile 右に ladder」と per-instrument×tile 対応から逸脱。

### (D2) per-instrument 厳守 = chart tile ごとに専用 ladder（single-global 退行禁止）
TTWR `chart_ladder_mode_sync_system` は **全 chart tile** に各自の ladder を出し、`ladder_render_system` は各 pane が自分の
`ChartInstrument.instrument_id` で depth を引く（`overlays_ladder.rs:17` Caveat: single-global `depth` 退行を避ける）。
backcast も **`_depthLadders: Dictionary<string, DepthLadderView>`**（id→ladder）で chart tile と 1:1、各 ladder は
`DepthDecoder.Decode(state, id)` で**自分の id の板だけ**を描く。selected-only ではなく全 chart tile（AC「per-instrument 厳守」）。

### (D3) mode-sync = retained-uGUI の per-frame SetActive（framework 差による正当逸脱）
TTWR は immediate-mode（`exec_mode.is_changed() || Added<ChartInstrument>` で spawn/despawn）。Unity retained-uGUI には
「毎フレーム自動 despawn」が無いため、**ladder を `SpawnChartTile` 時に常設で mount し、mode で SetActive/inset を切替える**
（findings 0024/0023 と同根の retained 逸脱）。`Added<ChartInstrument>`（Live 中に新タイル）相当は、新タイル spawn 時に
現 mode を即適用することで満たす（per-frame の mode 適用が新タイルも拾う）。mode 権威 = `FooterModeViewModel.DisplayMode`
（`!= Replay` で Live）。`ApplyDepthLadderMode(isLive)` は `_lastLadderLive` で**変化時のみ** rect を触る（毎フレーム thrash 回避）。

### (D4) LAST 行 = `per_instrument[id].price`（TTWR `LastPrices` 相当）
TTWR は LAST 行に per-instrument の `LastPrices`（last trade）を出す（`overlays_ladder.rs:262/304`）。backcast の
`get_state_json` は `per_instrument[id].price` を持つ（#60 probe §2 が裏取り済）。**新規 durable decoder
`InstrumentPriceDecoder.Decode(stateJson, id) -> double?`** を `DepthDecoder`/`InstrumentOhlcDecoder` と同じ discipline
（共有 `PerInstrumentJsonLocator` 経由・absent/null→null・malformed→FormatException）で追加し、`Render(snap, last)` に渡す。
price が number でない（string/object）場合は null（"LAST ---"）に倒す。locator に scalar 終端 scan `ScanScalarEnd` を 1 つ公開
（"extract, don't mirror"＝scanner 権威を locator に一本化）。

### (D5) render 配線 = poll payload 変化時・per-id signature early-out
本線 `Update` の poll block は OHLC を per-id decode 済（`_chartRendered` で series 長 dedup）。depth も同型に:
**`DriveDepthLadders()` を `DriveFooter()` の後**（`_footerMode.ApplyPoll` 後＝mode が最新）に呼び、Live のときのみ
`_host.LatestStateJson` を per-id decode→`Render`。payload 不変なら早期 return（`_lastDepthPayload`）、payload 変化内でも
**自分の板（depth+last の signature）が不変なタイルは 21 行 rebuild を skip**（`_depthRendered`・TTWR `depth_signature`
early-out 相当）。Replay では decode を完全に skip（ladder は SetActive(false) で hidden）。malformed は try/catch で last 保持。

## 2. 検証（RED 先行・正本は本 findings）

**新規 AFK probe `WorkspaceDepthLadderProbe`**（headless・Python-FREE・`BackcastWorkspaceProbe` と同じ root-drive 反射
harness）。判定は `error CS` 0 ＋ `[WORKSPACE DEPTH LADDER PASS]`（`grep -c "error CS"` の 0-match exit-1 落とし穴に注意）:

1. **`InstrumentPriceDecoder`（新 decoder unit）**: per-id price を decoy 文字列に惑わされず取得 / absent・null→null /
   price-as-string→null（throw しない）/ malformed→FormatException。
2. **mount per tile**: root を headless drive（ResolvePaths+BuildWorkspace）し universe=[X,Y]→各 chart tile に
   `DepthLadderView` が `ladderArea`（body の右帯 child）として存在、`chartArea` に ChartView が同居。
3. **mode-sync（D1/D3）**: `ApplyDepthLadderMode(true)`→`ladderArea` active＋`chartArea.offsetMax.x == -LADDER_WIDTH`、
   `ApplyDepthLadderMode(false)`→`ladderArea` inactive＋`chartArea.offsetMax.x == 0`。
4. **per-instrument render（D2/D4）**: state JSON（X=depth+price / Y=depth 無し+price）で `RenderDepthLadders(state)`→
   X ladder は `BestAsk()/BestBid()/LastRow()` 非 null＋`LastRow().text=="LAST {Xprice}"`、Y ladder は placeholder
   （`BestAsk()==null && LastRow()==null`）＝**X の板を Y が共有しない**（single-global 退行 kill）。

**owner HITL leg（AC④）**: 立花 demo venue で実 depth が本線 chart tile 右帯 ladder に流れ、depth 更新で板が live 更新、
Replay へ抜けると ladder が消えることを実機目視。AFK は mock/decoder/構造で決定的、実 venue depth は HITL で確認。
（**訂正**: 当初「JST 場中依存」と書いたが、立花 **demo**（`demo-kabuka.e-shiten.jp`）は**時間外でも板を返す**＝AC④ HITL は閉局帯でも実走可能。owner 指摘 2026-06-16。）

## 2.1 検証実績（Unity 6000.4.11f1 実機 batchmode・2026-06-16・当 dev 環境で自走）

[[unity-batchmode-afk-gate-runnable]] のとおり当 dev に Unity 6000.4.11f1 実在。AFK gate を自走。判定は `UNITY_EXIT=0`
＋ `[... PASS]` ＋ `error CS` 0（`grep -c` の 0-match exit-1 落とし穴に注意）:

- **`WorkspaceDepthLadderProbe.Run` → `[WORKSPACE DEPTH LADDER PASS]`**（exit 0・`error CS` 0）。§2 の §1〜§4 全 GREEN:
  price decode（decoy/null/absent/string→null/malformed throw）/ per-tile mount（X,Y 各 ladder＋chartArea＋ChartView 同居）/
  mode-sync（Replay=hidden＋offsetMax.x 0・Live=shown＋offsetMax.x −120・再 Replay で復帰）/ per-instrument render
  （X=21 行 board＋`LAST 105.00`、Y=placeholder＝X の板を共有しない single-global 退行 kill）。probe は厳密値
  （offsetMax.x ±120 / 完全一致テキスト / best 行 null 判定）で非空虚。impl 前は seam 不在（compile-RED）。
- **回帰 `BackcastWorkspaceProbe.Run` → `[BACKCAST WORKSPACE PASS]`**（`SpawnChartTile` を chartArea/ladderArea へ
  再構成したため #59/#60 root を再検証・全 section GREEN・`error CS` 0）。回帰 `HakoniwaChartTileProbe.Run` →
  `[HAKONIWA CHART TILE PASS]`（共有 locator `ScanValue`→`ScanScalarEnd` 委譲・OHLC decode 不変）。
- **セッション中に `#61`（Hakoniwa base tiles＋`buying_power`＋`HakoniwaBaseTiles`・mode-conditional base）が main へ
  merge され base が 5タイルへ変化**（HEAD `872739d`→`c6dd753`）。本スライスを `c6dd753` へ rebase し **#61 の 5-base 上で
  両 gate を再走 GREEN**（`WorkspaceDepthLadderProbe`＝chart tile のみ ladder＝`Count==2` 不変／`BackcastWorkspaceProbe`
  #61 版＝`HakoniwaBaseTiles.PanelOrder` で base 検証）。depth ladder は chart tile に載るので base 拡張と直交。

### owner 実機 HITL PASS（AC④・2026-06-16・Windows・Unity 6000.4.11f1 Editor Play・立花 demo）
本線 `BackcastWorkspace.unity` を Play（`BackcastWorkspaceRoot` = 単一 Python owner・`[WorkspaceEngineHost] live-configured
server built; ... lanes polling.`）。`LIVE_VENUE=TACHIBANA`（.env）で server を立花 demo 構築 → `Tools > Backcast >
Live Demo Roundtrip (Tachibana demo)` harness の **Connect TACHIBANA** → `Connected: TACHIBANA`。footer **LiveManual** →
**`8918.TSE` chart タイルの右帯に depth ladder が出現・chart が左に縮み・実 demo の bid/ask＋LAST が表示/更新**、footer
**Replay** → **ladder 消滅・chart 全幅復帰**。**PASS**（owner 報告）。Console error 0。
- **学び**: 立花 **demo は時間外でも depth を返す**ため、AC④ HITL は閉局帯（実走時 ~01:24 JST）でも実走できた（上記訂正）。
- gotcha: venue は **one-per-server で Awake 確定**。`LIVE_VENUE` が `.env.example` 既定でコメント状態だと server が MOCK で
  建ち、harness が `Connect MOCK`＋`VENUE_MISMATCH` warn を出す（#23 RH5 の安全 warn が正しく作動）。`.env` の `# LIVE_VENUE`
  をコメント解除して**再 Play**で TACHIBANA に切替。

→ **#57 = AFK GREEN ＋ owner HITL PASS で完了**（残務なし）。#4（Step 2 done-gate）の depth-ladder leg もこれで充足。

### pre-existing 回帰ゲートバグの観測と顛末（bug-class sweep・記録のみ）
作業初期の HEAD `872739d` で `BackcastWorkspaceProbe.Section10`（#60 `e795b13`）が **merge-staleness で RED だった**
（私の変更前の clean HEAD でも同一 FAIL を再現確認＝#57 非起因）。原因: Section10 は base Hakoniwa を `[startup]` のみと
仮定し `hako.Count != 3` を assert していたが、後続 merge の #23 re-home が base を `[startup, orders, positions,
run_result]` へ拡張したため `ReplaceAll(AAA,BBB)` 後の実数が合わなかった（family で数えるべき）。
**顛末**: その後 **`#61`（`HakoniwaBaseTiles` で base tile を一般化し mode-conditional base＝5タイルへ）が main へ merge**
され、**Section10 は #61 が `HakoniwaBaseTiles.PanelOrder` ベースの robust assertion へ正式に書き換えた**（`c6dd753`）。
よって本スライスは `BackcastWorkspaceProbe.cs` を**変更しない**（#61 の修正が正本・私の暫定 family-count 修正は不要に
なり破棄）。current HEAD `c6dd753` で Section10 を含む全 section GREEN を再確認済。[[bug-class-sweep-and-matrix-coverage]]。

## 3. follow-up（silent drop にしない）
- pane bg の alpha 0.95 は #54 から継続未移植（不透明 `colors.background`・`ChartView` と同 scope 切り）。
- 色ロール bid/ask=`status.bid/ask`（#44 既定逸脱・#54 記録）。
- `MenuBarView` の uGUI 化（#42 follow-up）/ native file picker（findings 0017 §9a）は本スライス非対象。
