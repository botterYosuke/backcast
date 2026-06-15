# Instrument Picker / Universe Sidebar Findings: 銘柄の追加・削除・選択を Unity で行う screen-fixed sidebar ＋ rich picker

- Issue: #31 (instrument picker / universe sidebar)・親 #5 (Step 3 カットオーバー)
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0005 — カットオーバー = TTWR `src/ui` との 1:1 表面 parity](../adr/0005-cutover-scope-1to1-surface-parity-with-ttwr-ui.md)（accepted, 自己保護節あり）, [ADR-0001 — Unity + pythonnet 全埋め込み](../adr/0001-unity-pythonnet-embedded-frontend.md)（proposed）
- 配置の根拠: ADR-0005 自己保護節（個別 surface の実装事実＝採用ウィジェット・seam 等は ADR に書き戻さず各スライスの `docs/findings/` に記録し、本 ADR を「方針: ADR-0005」として参照）。**新規 ADR は起こさない**（universe registry seam は CONTEXT.md に既出の確定語彙であり、本 slice は「UI 表面 + selection seam の追加」で非可逆コミットは ADR-0001/0005 に既存）。
- 先行（直接依存）: **#29（Replay 実行設定パネル）** = `InstrumentRegistry`（universe SoT）＋ `ScenarioSidecarStore.SetInstruments`（#31 用に温存した registry-only writeback seam）＋ `ScenarioStartupController.Populate`（restore = `Universe.ReplaceAll(snap.Instruments)`）／**#16**（`IStrategyFileProvider` = sidecar path 解決）／**#42**（`MenuBarViewModel` + IMGUI chrome の brain/view 分担）
- 関連 CONTEXT: 「universe registry（instruments SoT）vs scenario panel」「active strategy 選択」「板 / depth」（CONTEXT.md L350-379, L82-93）
- 別 issue（#31 範囲外・defer）: 実供給源（DuckDB `listed_info` / venue `fetch_instruments`）／ kabu 候補リスト外部 fixture 供給 ／ `instruments_universe_prune`（live universe 確定時の外銘柄 prune）／ venue 接続・`SubscribeMarketData` 実配線
- 設計確定: `grill-with-docs`（2026-06-15、owner インタビュー・5 問 + 持ち越し確定）

> **状態: 設計ロック済み・実装前（このドキュメントは grill 成果）。** 実装結果・ゲートログは §9 に後追記。

---

## 0. 移植元の正確な実態（ADR-0005 規律: AC を額面で受けず実ソースを読む）

移植元 = `The-Trader-Was-Replaced/src/ui/instrument_picker.rs` + `sidebar.rs`。実読の結論:

- **universe は screen-fixed 左 sidebar の Instruments セクションにのみ存在**。startup tile（`populate_startup_tile`）は start/end/granularity/initial_cash の 4 field だけで universe selector を持たない。
- sidebar Instruments 行 = `[ラベルクリック button → SelectedSymbol]` + `[価格列 70px]` + `[× remove button]`。末尾に `[+ Add]` ボタンがあり、直下に picker dropdown（検索 query 表示 + 候補行リスト）を spawn する。
- **TTWR は #266 で backend-authoritative 化**: `InstrumentRegistry` は projection-only mirror で、picker は `TransportCommand::AddInstrument` / `RemoveInstrument` を backend に送り `CuratedSetSnapshot` 経由で registry が更新される round-trip。
- picker の候補供給は `UniverseManager` が Replay（`AvailableInstruments` を end_date で DuckDB 引き）と Live（venue `Tickers`）を吸収し、`UniverseStatus`（`ReplayEndUnset` / `ReplayLoading` / `ReplayError` / `LiveVenueNotConnected` / `LiveLoading` / `LiveError` / `Ready{ids}` / 空）を返す。picker はこの status → 行 or placeholder を描画。
- writeback（Replay 経路）= `writeback_scenario_instruments_system`: gate は `editable` ＋ `Replay` mode ＋ `revision != flushed_revision`、target は事前解決済み `ScenarioWritebackPaths.cache_sidecar`（None なら return）。revision 駆動で coalesce flush。
- 行クリック（`instrument_row_click_system`）: Replay → `SelectedSymbol` 更新のみ／Live → 加えて `SubscribeMarketData` 送出。

### AC との差分（正すのは AC の文言・ADR ではない）

issue AC「選択銘柄が chart / depth / 実行設定の対象に反映される」は 2 seam の圧縮:
- **実行設定の対象 = universe 集合**（`scenario.instruments`）→ #29 sidecar writeback で既に反映。
- **フォーカス銘柄 = SelectedSymbol（単一）**→ chart/depth が読む。backcast 未実装で #31 が新設。

## 1. backcast への移植方針（in-proc 直結・ADR-0001）

TTWR の **backend-authoritative round-trip（#266）は移植しない**。backcast は in-proc 埋め込みで C# `InstrumentRegistry` が直接 SoT（#29 確定）。移植するのは sidebar の **UI レイアウト・行構造・picker dropdown** と **UniverseStatus の status 形** と **revision 駆動 writeback の gate**。`AddInstrument`/`RemoveInstrument` の transport は不要 — picker/× は registry を直接 mutate → `SetInstruments` で writeback。

brain/view 分担は #29(uGUI tile)/#42(`MenuBarViewModel` + IMGUI chrome) に揃える: **plain C# controller（probe 検証可能な brain）＋ 薄い view（screen-fixed chrome 系なので IMGUI 寄り・view 技術は HITL 専用で可逆）**。AFK GREEN は brain にかける。

## 2. 設計決定（grill 2026-06-15・owner 確定）

### D1. sidebar 配置 = 左 screen-fixed chrome を新設（#29 入力は温存・並存）
TTWR 1:1 parity。MENU_BAR と FOOTER の間の左側に固定 sidebar を新設し Instruments セクションを載せる。**#29 の startup タイル内テキストリスト入力は同一 SoT（`InstrumentRegistry`）を編集し続けるため温存（並存）** — CONTEXT.md「#29 の薄い入力を剥がして置換するリワークは出さない」に忠実。一本化は cutover shell の責務であって #31 ではない。

### D2. 選択配線 = SelectedSymbol seam を新設し depth を実消費に・chart は seam だけ defer
`SelectedSymbol`（フォーカス銘柄）を plain C# の SoT/observable として新設し、sidebar 行クリックが駆動。**実消費は depth に絞る**（`DepthDecoder.Decode(json, iid)` は明示 iid を取る唯一の現実消費者で、入力 id を SelectedSymbol 由来に差し替えるのが最小改修）。AFK probe で「行クリック → SelectedSymbol 更新 → depth target 追従 → `per_instrument[id]` から正しい板」を実証。chart は run universe 由来の暗黙描画で、フォーカス化は cutover shell の責務 = seam だけ残して defer。

### D3. 候補供給 seam = UniverseStatus 相当の status 形を移植 ＋ mock provider は Ready/Empty のみ
`IAvailableInstrumentsProvider`（status を返す）を新設し、`UniverseStatus` 相当（`Ready{ids}` / `Empty` / `Loading` / `Error` / `NotConnected` / `EndUnset`）の形を移植。picker UI は **全 placeholder を今回描く**（surface parity 完全）。#31 の mock provider は `Ready`/`Empty` を返す実装、残り status は probe 用 stub を合成注入して placeholder 描画を AFK 固定。各 status を「いつ返すか」の意味論は実供給 issue。

### D4. writeback タイミング = revision 駆動の即時 writeback（TTWR parity）
registry 変更で revision++ → flush が `ScenarioSidecarStore.SetInstruments` で registry → sidecar を即時永続化（#29 温存 seam を再利用）。gate = `editable`（`instruments_ref` ロック時は書かない）＋ **Replay mode**（Live は no-op）＋ revision 差分。sidecar path は #29 と同じ `IStrategyFileProvider` で解決し、**supplyable でなければ skip**（registry は in-memory のまま・次の Run Commit で永続化）。復元は #29 `Populate` が既に実装。flush 機構は coalesce 寄り（dirty/revision フラグ ＋ end-of-frame flush）で連打の冗長 write を避ける。

### D5. Live モード = Replay 中心・Live は seam のみ defer
- 行クリック: Replay/Live 共通で SelectedSymbol（フォーカス）は更新。**Live の `SubscribeMarketData` 送出は未配線 seam として明示しつつ defer**。
- writeback: Replay gate で Live は no-op（D4）。
- 供給: Live 源は mock で `NotConnected` placeholder を返せる形だけ用意（実際にいつ返すかは実供給 issue）。

### D6. `ProductionLiveShell._iidText` = 併存（SelectedSymbol 既定／手動 override）
SelectedSymbol を depth の既定ソースにしつつ、`_iidText` 手入力は manual override として残す（Live demo の手入力動線を壊さない）。優先順位は last-writer-wins で双方向結合はしない（独立 override・AFK probe を太らせない）。一本化は cutover shell へ defer。

## 3. スコープ

### 採用（#31）
- 左 screen-fixed sidebar（Instruments セクション: universe 行リスト + × remove + 行クリック select + 価格列 + `[+ Add]`）
- instrument picker dropdown（検索 query + 候補行リスト + クリックで universe 追加 + 100ms 同一 id debounce）
- `SelectedSymbol` SoT（新設）＋ depth への実配線
- `IAvailableInstrumentsProvider` seam（status 形）＋ mock provider（Ready/Empty）
- registry → sidecar の revision 駆動 writeback（`SetInstruments` 再利用・Replay gate）
- plain C# controller（brain・AFK probe）＋ 薄い view ＋ HITL harness

### 不採用（別 issue / cutover shell）
- 実供給源（DuckDB `listed_info` / venue `fetch_instruments`）・kabu 候補 fixture
- venue 接続・Live `SubscribeMarketData` 実配線
- `instruments_universe_prune`
- chart のフォーカス銘柄切替実配線・`_iidText` 一本化（cutover shell）
- multi-active picker（CONTEXT「リッチ multi-active picker は follow-up」）

## 4. 検証
backcast 規律（FLOWS.md は無い）: **AFK probe（headless・brain 駆動）＋ HITL harness（owner 目視）**。
- AFK: picker open → status placeholder（stub 注入で全 status）→ 候補クリックで registry 追加 → revision writeback で `<strategy>.json` の `scenario.instruments` 永続化 → Populate restore → 行 × remove → 行クリックで SelectedSymbol 更新 → depth target 追従。Replay gate（Live で writeback no-op）。`editable=false`（`instruments_ref`）で picker force-close + writeback skip。
- HITL: sidebar 描画・picker dropdown 操作の目視。

## 9. 実装結果（2026-06-15）

durable（`Assets/Scripts/Universe/`）:
- `SelectedSymbol.cs` — フォーカス銘柄 SoT（observable・Changed イベント・D2）
- `AvailableInstruments.cs` — `UniverseSourceMode` / `UniverseStatusKind` / `AvailableInstrumentsResult` / `IAvailableInstrumentsProvider` / `MockAvailableInstrumentsProvider`（D3）
- `UniverseWriteback.cs` — content-diff coalesce flush・editable＋Replay gate・path 未解決 skip・`Prime`（D4）
- `InstrumentPickerController.cs` — picker brain（toggle/query/100ms debounce/status→rows・D3/D5）
- `UniverseSidebarController.cs` — sidebar brain（registry 参照共有・行クリック→SelectedSymbol・×削除・`LiveSubscribeHook` defer seam・D1/D2/D5）
- `UniverseSidebarHitlHarness.cs` — IMGUI HITL view（owner 目視・Python-free mock）

throwaway / gate:
- `Assets/Editor/UniverseSidebarProbe.cs` — AFK regression gate（8 section）
- `Assets/Editor/UniverseSidebarHitlMenu.cs` — Tools > Backcast > Universe Sidebar HITL

#29 は無改変（`InstrumentRegistry` の `SetInstruments` 温存 seam をそのまま消費・registry 参照を host が共有）。

ゲートログ: `[UNIVERSE SIDEBAR PASS] picker + status + select + writeback + depth-follow verified`（exit=0・Unity 6000.4.11f1 batchmode・コンパイル警告/エラー 0）。
