# Mode-conditional base tiles Findings: base パネルのタイル化＋モード別 base（TTWR `hakoniwa_tile_kinds`/`reconcile_hakoniwa_tiles` parity）

- 受け皿 issue: **#61**（chart/panels: base パネル(BP/Orders/Positions/RunResult)タイル化＋モード別 base）。親 #1 (Epic) / #5 (Step3 cutover)。**4-stage 計画の stage②（B+M）**。
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0005 — 1:1 surface parity](../adr/0005-cutover-scope-1to1-surface-parity-with-ttwr-ui.md)（accepted・自己保護節）, [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)（accepted・自己保護節）。下位事実は ADR に書き戻さず本 findings に記録し ADR を「方針: ADR-0005/0003」として参照。
- 先行: **stage① #60（findings 0027・chart tile family／box-grow／base=[startup]）**, #59（workspace root / findings 0025・§12 universe 共有 SoT）, #39（footer mode segment / findings 0026・`FooterModeViewModel`）, #14（Hakoniwa split-grid / findings 0007）, #10（panel decoder / ReplayPanelDecoder）。
- 設計確定: `grill-with-docs`（2026-06-16・owner インタビュー）。AC を TTWR 実ソース（`src/ui/hakoniwa.rs` の `hakoniwa_tile_kinds`/`detect_hakoniwa_mode_change_system`/`reconcile_hakoniwa_tiles`・ADR 0013(#169 amendment)）と照合し、**AC は実態の正確な言い換え**であることを確認（#42 のような乖離なし）。

> **状態: 設計確定。** grill で全分岐を lock。実装着手時に §10 へ証跡を追記する。

---

## 0. スコープ（owner 確定 2026-06-16）

stage② = 2 つの capability:

- **B（パネルのタイル化）**: 現状 `ProductionLiveShell.OnGUI`（live HITL シェル）にしか無い BuyingPower/Orders/Positions/RunResult を **Hakoniwa tile** にする（chart tile と同じ chrome／header drag swap／persist 対象）。本線 `BackcastWorkspaceRoot` には**まだ出ていない**ので、新規に tile として載せる。
- **M（モード別 base）**: base tile の集合（種類）を **mode が決める**。mode 切替で **base のみ** retile（chart tile は identity 保持）。

### 採用 / 不採用

- **採用**: base tile を `[startup]` 固定（#60）から **mode 別 tile kinds** へ拡張／`hakoniwa_tile_kinds(mode)` port／base 集合変化時のみ base-only retile／chart identity 保持・新 grid 後半へ再配置／order 不変条件 `[base…, chart…]`／grid = n_base+n_chart（既存 `_hako.Count` 経由で box-grow 済み）／4 base tile の中身は **既存データのみ配線**（下記 §3）／AFK 権威ゲート + owner HITL。
- **不採用（= 後続 stage の additive）**:
  - **per-mode profile**（Replay/Live で別レイアウト保存）→ stage③ #62。**#61 は単一共有レイアウト**のまま（startup の出入りは既存 `DeriveOrder`/`NormalizeOrder` tolerance が吸収）。
  - **box 位置/サイズ永続化＋drag-handle 移動/リサイズ・divider resize** → stage④ ~~#63~~ **ボツ**（#63 close 2026-06-24・[ADR-0017](../adr/0017-hakoniwa-dockable-floating-windows.md) が split-grid を退役し floating window の自由配置＋geometry 永続化＝[ADR-0024](../adr/0024-puzzle-feel-drag-magnetic-snap-swap-translate-detach-merge.md)で代替）。#61 の box は #60 の derived-grow（n_total から導出）を維持。
  - **Replay パネルの実データ表示** → **別 follow-up issue**（§3・owner 指示「沈黙の欠落にしない」）。#61 は honest empty state（"(no data)"）。

## 1. モデル（owner 確定 2026-06-16）

```
order = [base tiles…, chart tiles…]            # base が前・chart が後（#169 所有権分離）
base(Replay) = [startup, buying_power, orders, positions, run_result]   # startup index 0
base(Live)   = [buying_power, orders, positions, run_result]            # startup 無し
chart        = [chart:<id1>, chart:<id2>, …]   # universe メンバー（#60・不変）
grid         = ceil(√n_total) 等分（既存 HakoniwaGridMath.CellRects）
```

- **base 集合の正本 = mode**（TTWR `ExecutionMode`）。backcast には enum/Resource が無いので **mode の正本は footer `FooterModeViewModel.DisplayMode`**（poll が overwrite・findings 0026）。base 集合判定は `DisplayMode → {Replay, Live}` の **2 値**に畳む（`Replay`→Replay base、`LiveManual`/`LiveAuto`→Live base）。LiveManual⇄LiveAuto は base 集合が同一なので retile しない（TTWR `detect_hakoniwa_mode_change_system` の集合比較 parity）。
- **chart 集合の正本 = universe**（#60・不変）。base retile は chart に触れない（所有権分離）。
- base/chart の判別は **tile id の prefix `chart:`**（TTWR は `PanelKind::Chart` component で判別。backcast の等価物が id prefix）。header swap は base↔chart の cross-swap も許す（TTWR ADR 0014 parity）が、retile は prefix で確実に chart を識別するので invariant `[base…, chart…]` は retile のたびに復元される。

## 2. モード変化トリガ（owner 確定 2026-06-16）— footer poll を監視

- TTWR は `ExecutionModeRes.is_changed()` で 1 フレーム検出 → 集合比較 → `HakoniwaModeChanged` emit → `reconcile_hakoniwa_tiles`。
- backcast は `BackcastWorkspaceRoot.DriveFooter()` が**毎フレーム** `_footerMode.ApplyPoll(state)` で `DisplayMode` を更新している。ここに **base-shape 変化検出**を足す: 前回の base-shape（Replay/Live の 2 値）を保持し、変化したフレームにのみ `SyncBaseTilesToMode()` を呼ぶ（chart の `SyncChartTilesToUniverse` と対の membership orchestration）。`DisplayMode` は engine poll 由来なので mode の SoT と整合（footer click → SetExecutionMode → poll → DisplayMode → base retile の一方向）。
- **race 安全**: retile は main-thread の Update 内のみ（chart sync と同じ）。Locked 中（Live 遷移待ち）は `DisplayMode` がまだ前 mode なので retile せず、poll が target に追いついた瞬間に 1 回だけ retile（optimistic flip しない Live の構造と整合）。

## 3. base tile の中身（owner 確定 2026-06-16・Option 1 = 構造＋既存データのみ）

- **B のレンダリングは新規 uGUI View**（`ProductionLiveShell` の IMGUI `OnGUI`/`GUILayout` は Hakoniwa の uGUI/RectTransform tile に**再利用不可**）。データ供給 VM `LivePanelViewModel`（`_host.Panel`）は**そのまま流用**（live-only だった出自どおり）。
- **Live**: `_host.Panel`（`LatestAccount.BuyingPower`/`Positions`/`Cash`・`LatestOrder`・`FilledOrderCount`・`LatestLifecycle`・`LatestTelemetry`）。`DrainLiveEvents()` は既に Update で呼ばれている。
- **Replay**: replay state poll（`TradingState`）は **`buying_power`/`positions`/`orders`/`run_result` を持たない**（`core.py:_build_trading_state_locked` で確認。`ReplayPanelDecoder` は名称に反し live `gui_bridge_actor` ペイロードを decode し、run summary は別系統 `nautilus_backtest_runner`）。→ Replay の 4 パネルは **honest empty state "(no data)"**。捏造仮データでも後で剥がす stub でもない＝完成形。
- **follow-up issue（#61 外・owner 指示で起票）**: Replay 時の BuyingPower/Positions/Orders/RunResult を実数値表示する。`TradingState` poll への additive 出力 or backtest summary surface 経路の設計（engine/Python 変更を伴う）。まず「summary が深い engine 改造なしに取り出せるか」を grill で前裁定。silent truncation 禁止（owner 方針）。
- `ReplayPanelDecoder` の名称・責務整理（live ペイロードを decode するのに "Replay" 名）は follow-up で扱う（#61 ではコメントで明示するに留め、behavior 変更しない）。

## 4. durable 改造（owner 確定 2026-06-16）— base retile 経路

- **唯一の本質的変更は `BackcastWorkspaceRoot` への base membership orchestration 追加**（chart の `SyncChartTilesToUniverse` と対称）。`HakoniwaController` は #60 で `AddTile`/`RemoveTile` を既に持つので **Controller 改修は最小**。
- 必要な Controller 追加: order を `[base…, chart…]` に保つ **insert 経路**。現 `AddTile` は末尾 append（chart 用に正しい）だが、base retile では base を **chart の前**に入れる必要がある。→ `AddTile(id, rt, atEnd:false)` 相当 or orchestrator が retile 後に order を `[base…, chart…]` へ再構築する `controller`-tolerant な reorder。**採用**: Controller に「base 区画（chart: prefix でない id）を前、chart を後ろに安定ソートする」responsibility は持たせず、**orchestrator が desired order を計算し Controller の既存 swap/rebuild 機構で適用**（slot SoT は Controller・membership 判断は orchestrator の #60 原則を踏襲）。具体機構は実装時 §10 に記録。
- `SyncBaseTilesToMode(shape)`: 現 base tile（非 `chart:` id）を desired kinds と diff → 不要を `RemoveTile`＋GameObject destroy、不足を spawn（chart と同じ `BuildTileChrome`＋View）→ order を `[desired base…, 現 chart…]` へ整え → `ApplyBoxGrow()`（n_total 由来・既存）。teardown で View/VM 購読解除（orphan なし・#60/§12 規律）。

## 5. 永続化（owner 確定 2026-06-16）— スキーマ追加 0（#61 は単一共有）

- #60 と同じ：正本は slot 順だけ。base tile id（`startup`/`buying_power`/`orders`/`positions`/`run_result`）も `PanelLayout{id,slot,visible,rect}` にそのまま乗る（既存 `Capture`/`Apply`）。
- **mode 別 base の単一共有レイアウトでの整合**: Live で `startup` が居ないとき doc に `startup` slot があっても `Apply` の `DeriveOrder` が「doc にあって live に無い id は skip」で吸収。Replay へ戻ると `startup` tile が再 spawn され `NormalizeOrder`/append で order に復帰。**per-mode で別レイアウトを覚えるのは stage③ #62**（#61 は単一共有なので mode 跨ぎで slot 順は最後の配置を共有）。

## 6. 射程外（#61 に含めない）

- per-mode profile（③ #62）／box 位置・サイズ永続化＋drag-handle・divider resize（④ ~~#63~~ **ボツ**・ADR-0017 退役で代替）／Replay パネルの実データ（follow-up）／`ReplayPanelDecoder` リネーム（follow-up）。

## 7. 検証サーフェス（owner 確定 2026-06-16・AFK probe が正本ゲート）

### AFK probe（headless・batchmode 可・新 `HakoniwaBaseModeProbe` ＋ `BackcastWorkspaceProbe` 拡張）

1. **mode 別 tile kinds**: `HakoniwaTileKinds(Replay)` = `[startup, buying_power, orders, positions, run_result]`、`HakoniwaTileKinds(Live)` = `[buying_power, orders, positions, run_result]`（pure fn・TTWR 一致）。
2. **base-only retile（非トートロジー）**: 実 root を headless 合成し universe に 2 銘柄 → chart:<id> ×2。`DisplayMode` を Replay→Live→Replay と動かし、base が 5→4→5 に retile される一方 **chart tile の RectTransform identity（同一 instance）が保持**され、order が常に `[base…, chart…]`、grid = n_base+n_chart。
3. **LiveManual⇄LiveAuto は no-op**: 同一 Live base なので base tile が despawn/respawn されない（identity 保持）。
4. **single-shared persist round-trip**: Replay で swap → Capture → save → 新 instance load → slot 生存。Live で startup 欠落 doc が skip され crash しない（forward-compat）。
5. **#14/#59/#60 回帰**: `HakoniwaProbe`/`HakoniwaChartTileProbe`/`BackcastWorkspaceProbe`/`ReplayLayoutProbe` ほか GREEN 継続。

### owner-run HITL

- footer mode segment で Replay⇄LiveManual⇄LiveAuto 切替 → base が retile（Replay で startup 出現／Live で消失）・chart tile が消えない・grid 再レイアウト・box grow。Live 接続中に各 base tile に live account/order/telemetry が出る。zoom 下の base↔chart header swap。Save→再 Play で slot 復元。

## 8. AC 達成方針

- **AC1（4 パネルが tile・swap/persist 対象）**: AFK = §7 S1/S4。HITL = header swap・Save 復元。
- **AC2（mode 切替で base retile・Replay startup 込み/Live 無し）**: AFK = §7 S2/S3。HITL = segment 切替で startup 出入り。
- **AC3（mode 切替で chart identity 保持・後半スロット再配置）**: AFK = §7 S2（RectTransform instance 同一）。
- **AC4（AFK probe＋HITL＋回帰）**: §7 全項目。

## 9. 関連・正本

- 移植元: TTWR `src/ui/hakoniwa.rs`（`hakoniwa_tile_kinds`/`populate_tile_by_kind`/`detect_hakoniwa_mode_change_system`/`reconcile_hakoniwa_tiles`）, ADR 0013(#169 amendment), `buying_power.rs`/`orders.rs`/`positions.rs`/`run_result_panel.rs`/`panel_specs.rs`。
- backcast: CONTEXT.md「mode-conditional base tile / base retile」（本 slice で追加）／「chart tile family / base tile」／「tile / slot / tile swap」。findings 0027（chart tile family・前段）/ 0026（footer mode）/ 0025（workspace root）/ 0007（split-grid）。

## 10. 実装証跡（#61・2026-06-16・AFK GREEN）

実装ファイル:
- 新規: `Assets/Scripts/Hakoniwa/HakoniwaBaseTiles.cs`（pure: base tile id 定数＋`Kinds(live)`＝TTWR `hakoniwa_tile_kinds` port・`IsLiveShape`/`IsChartId`）、
  `Assets/Scripts/Live/HakoniwaBasePanelView.cs`（uGUI base パネル View・`LivePanelViewModel` から live 描画・Replay は honest "(no data)"）、
  `Assets/Editor/HakoniwaBaseModeProbe.cs`（新 AFK 必須ゲート）。
- 変更: `HakoniwaController`（`Reorder` 追加＝`[base…, chart…]` invariant 復元・box-size-free 維持。`DEFAULT_ORDER` は #14 HakoniwaProbe 用にコメント補足のみで**不変**＝revert 済み）、
  `BackcastWorkspaceRoot`（base orchestration: `SpawnBasePanel`×4・`SyncBaseTilesToMode`・`RefreshBasePanels`・`DriveFooter` に base-shape 検出＋panel refresh・build で Replay shape 初期化）、
  `BackcastWorkspaceProbe.Section10`（base が #60 の `[startup]` → #61 の Replay 5 tile になったので期待 tile 数 3→7・base panel 存在 assert を追加）。

AFK GREEN（Unity 6000.4.11f1 `-batchmode -nographics`）:
- **`HakoniwaBaseModeProbe`**（新・必須）: ① mode tile kinds（Replay=[startup,buying_power,orders,positions,run_result]・Live=startup 落とす・`IsLiveShape` の Replay/LiveManual/LiveAuto/unknown 畳み込み・`IsChartId`）
  ② base-only retile（実 root を headless 合成＋2 chart・shape Replay→Live→Replay で base 5→4→5・**chart tile の RectTransform instance 同一性を保持**・order 常に `[base…, chart…]`・box-grow=`ComputeBoxSize(n_total)`・startup は index 0 復帰）
  ③ LiveManual⇄LiveAuto は no-op（同一 Live shape・identity 保持・count 不変）→ `[HAKONIWA BASE MODE PASS]`。
- **回帰 GREEN**: `HakoniwaChartTileProbe`（#60・DEFAULT_ORDER revert＋Reorder 追加後も PASS）／`HakoniwaProbe`（#14・DEFAULT_ORDER 不変で PASS）／
  `BackcastWorkspaceProbe`（Section10 を 7 tile に更新・all sections green）／`ReplayLayoutProbe`（#12 schema 不変）全 PASS。`DepthDecodeProbe`/`UniverseSidebarProbe` 等は #61 が触れないため非対象。
  - 補足: 4 probe を直列実行する際 Unity instance の lock 解放が間に合わず "another Unity instance" 衝突が出たが、プロセス完全終了を待つ直列化で全 GREEN（env アーティファクト・実バグでない）。

**code-review（simplify・high・2026-06-16）で発見＋修正した HIGH バグ**:
- **restore 時の base id 衝突**（2 reviewer 独立 CONFIRMED）: `LayoutDocument.Default()`（fresh/missing/corrupt fallback）と pre-#61 / **#60 世代の sidecar**（owner が前日 HITL で生成済み）は `orders`/`positions`/`run_result` を旧 slot で持ち、`startup`/`buying_power` を欠く。`_hako.Apply(doc)` の `DeriveOrder` がこの slot で並べ替えるため、base 領域がスクランブルされ（startup が slot 0 から外れる）、または #60 世代 doc では base 4 パネルが chart の**後ろに沈む**。orchestrator は shape flip 時しか canonical 順を再適用しないので起動時（Replay・flip 無し）に修復されない＝**起動ごとに base 順が壊れる**。加えて stale `visible=false` が衝突 id に乗ると base パネルが恒久的に hide。
  - **修正**: `ReassertBaseAfterRestore()` を `ApplyLayout` の `_hako.Apply(doc)` 直後に追加。現 shape の `Kinds(_baseLive)` で base を canonical 順に `Reorder`（chart は復元相対順を保持）＋ base tile（非 closeable）を強制 active。**#61 は single-shared なので restore 時 base 順は canonical**（per-mode 順永続は #62）。`HakoniwaBaseModeProbe.Section4` で機械ロック（Default 衝突／#60 世代 doc／visible=false の 3 ケース）。
- **Medium 品質**: ① `DriveFooter` の `panelStamp = AppliedCount + (live?1L<<62:0)` ビットトリックを撤去 → shape flip は `SyncBaseTilesToMode` 内で `RefreshBasePanels` 即時再描画・content は素の `AppliedCount` gate。② `SpawnBasePanel`/`SpawnChartTile` のスキャフォルド重複を `BuildTileShell(id, out body)` に抽出。
- **非修正（Medium 未満の妥当判断）**: `HakoniwaBasePanelView` と retired `ProductionLiveShell.DrawPanels`（IMGUI dev/HITL シェル）の format 重複＝別サーフェスで dev シェルは将来削除候補・Low。`SyncBaseTilesToMode` の startup 特化（general set-diff でない）＝ADR 0013 が「startup が唯一の mode-conditional base tile」と固定済みで YAGNI。

修正後 AFK 再 GREEN: `HakoniwaBaseModeProbe`（Section1-4・restore 衝突回帰込み）PASS／`BackcastWorkspaceProbe`（BuildTileShell リファクタ回帰）PASS。

**owner-run HITL（pending）**: footer mode segment で Replay⇄LiveManual⇄LiveAuto → base retile（Replay で startup 出現／Live で消失・BP/O/P/RR は常駐）・chart tile が despawn しない・grid 再レイアウト・box grow。Live 接続中に各 base tile へ live account/order/telemetry 表示（Replay は "(no data)"）。zoom 下の base↔chart header swap。**Save→再 Play で base は canonical・chart は slot 復元**（owner の #60 世代 sidecar でも base が壊れないことを確認）。**owner 実機 Play で確認待ち。** Replay パネル実データは follow-up #65。

## 11. origin/main マージでの #23 統合（2026-06-16・owner 確定）

#61 WIP は #60（`e795b13`）から分岐しており、並行して **#23 re-home**（origin/main `872739d`・`23-rehome-live-surfaces-to-workspace`）が **Orders/Positions/RunResult を HITL PASS 済みの静的タイルとして** `BackcastWorkspaceRoot` に着地していた。両者は同じ live-panel 領域を別実装で再構築していたため `BackcastWorkspaceRoot.cs` がマージ衝突。

**owner 決定（reconciliation）= #23 のテスト済み配線を正本にし、#61 はその上に mode-conditional 層だけを載せる**:
- **Orders/Positions/RunResult**: #23 の **scene-authored タイル + `LivePanelTileView`（`FormatOrders`/`FormatPositions`/`FormatRunResult`）+ `RefreshLiveTiles`（`AppliedCount` gate）** をそのまま採用（HITL PASS 済みを温存）。
- **BuyingPower**: #23 には無い（scene タイルも無い）ため、**`SpawnBuyingPowerTile()` で動的生成**し、#23 と同じ `LivePanelTileView` + 新 `FormatBuyingPower` で配線。
- **`HakoniwaBasePanelView.cs` は削除**（§10 の独自 uGUI View は `LivePanelTileView` に置換され重複・不要に）。§10 の「非修正 Low（format 重複）」は本統合で解消。
- **mode-conditional 層（#61 の本体）は不変で存続**: `HakoniwaBaseTiles.Kinds`／`_baseTiles`（#23 の scene タイル + BuyingPower を追跡）／`SyncBaseTilesToMode`／`ReassertBaseAfterRestore`／`DriveFooter` の shape-flip フック。shape flip 時の即時再描画は `RefreshBasePanels` → **`ForceRefreshLiveTiles()`（gate 無視で 4 panel を `Refresh`）** に置換。冗長だった `_lastPanelStamp` poll は #23 の `_lastPanelApplied` gate に一本化。
- honest empty state は **mode-aware で維持**: `LivePanelViewModel`（`_host.Panel`）は monotonic（clear されない）ため、Live→Replay flip 後に live formatter をそのまま回すと**直前の live 口座/建玉/約定が Replay 画面に残り誤表示**になる（code-review high が検出）。→ `LivePanelTileView.ShowReplayEmpty()` を追加し、`PushLiveTiles` が `_baseLive==false` のとき 4 panel に "(no data — Replay)" を描画（live のときのみ formatter）。#61 の honest-empty 意図（§0/§3）を #23 wiring 上で復元。Replay 実データは follow-up #65 のまま。

**AFK 再 GREEN（マージ後・Unity 6000.4.11f1 `-batchmode -nographics`）**: `HakoniwaBaseModeProbe`（Section1-4・`_basePanels` 参照を `_baseTiles` ベース検証に更新）→ `[HAKONIWA BASE MODE PASS]`。`BackcastWorkspaceProbe` / `WorkspaceLiveSeamProbe`（#23 回帰）も確認。**owner 実機 HITL（§10 末）は引き続き pending。**

**独立再検証（2026-06-16・別セッション・HEAD `45ce071`）**: マージが #61 を退行させていないか裏取りするため 4 probe を fresh プロセスで直列再実行し全 PASS を確認（CS コンパイルエラー無し）:
- `[HAKONIWA BASE MODE PASS]` mode tile kinds + base-only retile + chart identity + live-shape no-op（#61 ゲート）
- `[BACKCAST WORKSPACE PASS]` all sections green（#23/#61 統合）
- `[HAKONIWA CHART TILE PASS]` box-grow + per-id ohlc decode + dynamic tile round-trip（#60 回帰）
- `[WORKSPACE LIVE SEAM PASS]` connect→badge / place→FILLED→panel / cancel-lane GIL-safe / teardown clean, maxStall=29ms（#23 回帰）

これで残るゲートは **owner 実機 HITL のみ**（§10 末のチェックリスト）。

### HITL の AFK 巻き取り（2026-06-16・残 HITL 最小化）

owner の「手順が多い・自動化できる分は巻き取れ」を受け、HITL チェックリストのうち**挙動として検証可能な項目を AFK probe に移管**した。

- **`HakoniwaBaseModeProbe.Section5`（新規・honest empty-state 回帰）**: `_host.Panel.Apply("{\"AccountEvent\":{...buying_power:12345...}}")` で**実 live イベントを注入** → `_baseLive=true` + `PushLiveTiles` で BuyingPower が "12345" を描画することを確認 → `_baseLive=false` + `PushLiveTiles` で **4 panel 全てが `LivePanelTileView.ReplayEmpty` に戻り stale live 値を残さない**ことを assert。これは cdc09d4（monotonic VM の Live→Replay 誤表示）を**実描画テキストで機械ロック**したもので、HITL チェックリスト#4 の中核を owner の手から外した。→ `[HAKONIWA BASE MODE PASS]`（Section1-5・CS エラー無し）。
- **AFK で担保済み（owner 不要）**: base retile（#3, Section2）／LiveManual⇄LiveAuto no-op（#3, Section3）／chart identity（#5, Section2）／restore 衝突・canonical 復元・visibility（#6, Section4）／honest empty-state（#4, Section5）。
- **owner 実機 Play に残す最小スモーク（AFK 不能＝実レンダリング/実 Python/実ジェスチャ依存）**: ① Play で 5 base タイル＋4 パネルが画面に正しく描画される目視。② menu bar `Venue→Connect MOCK (dev)` が実際に接続でき footer に Manual/Auto セグメントが出る。③（任意）実 LiveAuto/Manual セッションで実 wire イベントが panel に流れる。④（任意）zoom 下の header-drag swap の操作感。base retile / 順序 / empty-state のロジックは AFK 済みなので、③④で違和感が無ければ受入可。

### owner-run HITL: **PASS（2026-06-16）**

owner が実機 Play で残スモーク（①描画目視 ②MOCK 接続→footer Manual/Auto 出現）を確認し **PASS**。AFK 巻き取り済み項目（base retile / Manual⇄Auto no-op / chart identity / restore 衝突 / honest empty-state）と合わせ、**#61（stage② mode-conditional base tiles）受入完了**。次は stage③ #62（per-mode profile）。Replay パネル実データは follow-up #65。
