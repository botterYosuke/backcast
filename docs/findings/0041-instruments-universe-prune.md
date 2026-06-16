# Instruments Universe Prune Findings: live universe 確定時の universe 外銘柄 prune（#253 stale-snapshot 回帰防止）

- Issue: #41 (instruments universe prune)・親 #5 (Step 3 カットオーバー) / #4 (Step 2 Live/Auto parity)
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0005 — カットオーバー = TTWR `src/ui` との 1:1 表面 parity](../adr/0005-cutover-scope-1to1-surface-parity-with-ttwr-ui.md)（accepted, 自己保護節あり）, [ADR-0001 — Unity + pythonnet 全埋め込み](../adr/0001-unity-pythonnet-embedded-frontend.md)
- 配置の根拠: ADR-0005 自己保護節（個別 surface の実装事実は ADR に書き戻さず findings に記録し ADR を「方針: ADR-0005」として参照）。**新規 ADR は起こさない**（universe prune gate は CONTEXT.md に確定語彙を追記済・非可逆コミットは ADR-0001/0005 に既存）。
- 先行（直接依存）: **#31（instrument picker / universe sidebar・CLOSED）** = `InstrumentRegistry`（universe SoT）＋ `IAvailableInstrumentsProvider`（status 形・mock）。#31 findings 0024 §8 は `instruments_universe_prune` を本 slice へ defer 済。
- 関連 CONTEXT: 「universe prune gate（破壊的 prune の live-source 判定・#41）vs picker status / badge band」「universe registry（instruments SoT）vs scenario panel」「venue 接続状態」「chart tile family / base tile」
- 設計確定: `grill-with-docs`（2026-06-17、owner インタビュー・4 問 + 持ち越し確定）

> **状態: 設計ロック済み。** 実装結果・ゲートログは §9 に追記。

---

## 0. 移植元の正確な実態（ADR-0005 規律: AC を額面で受けず実ソースを読む）

移植元 = `The-Trader-Was-Replaced/src/ui/instruments_universe_prune.rs`（薄い caller）＋ **真の正本 `src/ui/universe.rs`（`UniverseManager` / `RegistryValidator`・issue #145）**。実読の結論:

- prune の実ロジックは `instruments_universe_prune.rs` ではなく `universe.rs` にある。`prune_instruments_outside_universe_system` は `inputs_changed()` gate ＋ 空 registry skip を見るだけの**薄い caller**で、allowlist 解決は `UniverseManager::current_universe()` が行う。
- **R2 asymmetry（universe.rs:9-17・"must not be merged"）**: picker と prune は同じ gate を使わない。
  - picker = `universe_status()` → Live では `tickers.status` **だけ**で分岐（`Loading`/`Error` を見せられる）。
  - prune = `current_universe()` → Live では `live_prune_gate_ok()` の**三重 gate**（`source ∈ {LiveVenue, LocalVenueSnapshot}` ∧ `is_venue_live(venue.state)`）＋ status `Loaded` ∧ **non-empty**（空は `None`）。
- `is_venue_live`（`src/run/state.rs:93`）= **`{Connected, Subscribed}` のみ**。`Reconnecting` は live ではない（test `prune_skips_when_venue_state_reconnecting`）。
- `prune_runs_even_when_editable_is_false`（line 262）= **editable=false でも prune は走る**。`editable` は**ユーザー編集**だけを gate し、system prune は gate しない。
- `prune_skips_when_live_tickers_list_is_empty`（HIGH-1）= live で list が空でも registry を wipe しない。
- TTWR は Live/Replay **両方**で prune（Replay は `AvailableInstruments` を `scenario.end` で引いた allowlist・三重 gate 無し）。

### AC との差分（正すのは AC の文言・ADR ではない）

- #41 本文は「`ReplayCatalogFallback` / `LocalVenueSnapshot` を fallback として prune しない」と書くが、TTWR main の `live_prune_gate_ok` は `{LiveVenue, LocalVenueSnapshot}` を許可する（universe.rs:149-154）。**ただし `LocalVenueSnapshot` は firing path 無しの reserved 値**（protocol.rs:87「Phase 8.7 has no firing path」）。→ backcast は **本文優先で `LiveVenue` のみ許可**（D2）。divergence は内部 gate 定数であり ADR-0005 の「1:1 表面 parity」対象外（component/systems/traits は対象外と ADR-0005 Consequences が明記）。
- backcast は in-proc（ADR-0001）で TTWR の `Tickers` 永続 resource / Bevy ECS `Update` tick / `UnsubscribeMarketData` transport を持たない。→ 都度再解決・poll-loop 駆動・unsubscribe defer（D3/D4）。

## 1. backcast 現状（移植先の地面）

- `InstrumentRegistry`（`Assets/Scripts/ScenarioStartup/`）= universe SoT。`Add/Remove/ReplaceAll` は**全部 `Editable` gate**。`Changed` イベントが `BackcastWorkspaceRoot.SyncChartTilesToUniverse` を同期駆動（chart tile family）。prune ロジックは未実装。
- `AvailableInstruments`（#31）= `UniverseSourceMode {Replay, Live}` ＋ `UniverseStatusKind` ＋ `IAvailableInstrumentsProvider`（mock）。**source discriminator（LiveVenue/fallback）は無い**。実 live universe fetch（venue `fetch_instruments`）も mock。
- `VenueConnectionViewModel`（#21）= badge band `IsConnected = {CONNECTED, SUBSCRIBED, RECONNECTING}`（badge flap 防止で RECONNECTING を含む）。
- `BackcastWorkspaceRoot.Update()` = poll-loop。`DriveFooter`/`DriveDepthLadders` 等が change-detection で走る。

## 2. 設計決定（grill 2026-06-17・owner 確定）

### D1. prune 専用 resolver を分離（R2 asymmetry の capability parity）
picker の `IAvailableInstrumentsProvider.Query()`（status-facing）を prune の allowlist に**流用しない**。prune 専用に `UniversePruneGate.CurrentUniverse(inputs)` を新設し、`HashSet<string>` か `null` を返す。`null` = 未確定 → prune 禁止（fail-closed）。TTWR の "must not be merged" を守る。

### D2. Live prune gate = `LiveVenue` のみ（#41 本文優先・fail-closed）
Live で allowlist を返すのは次を**全部**満たす時だけ:
- `source == LiveVenue`（`ReplayCatalogFallback` / `LocalVenueSnapshot` / `Unknown` は除外）
- venue が **`{CONNECTED, SUBSCRIBED}` のみ**（`is_venue_live` parity・**Reconnecting 除外**）。badge の `IsConnected`（RECONNECTING 込み）を**再利用しない** — gate が自前の strict 述語を持つ。
- status が `Ready`（Loaded 相当・Loading/Error/NotConnected/Empty は除外）
- ids が **non-empty**（空 list で registry を wipe しない・TTWR HIGH-1。callee `PruneRetain` でも二重防御）

`LocalVenueSnapshot` の TTWR 許可は firing path 無しの reserved 値なので本文優先で除外（§0 参照）。

### D3. Replay prune も含む（TTWR parity）/ sibling 2 system は defer
- **Replay prune**: `scenario.end` 確定 ∧ catalog source が `Ready` ∧ non-empty の時だけ allowlist（三重 gate 無し）。`end` 未設定・Loading/Error・empty・未解決は全部 `null`。実 catalog（DuckDB `listed_info`）はまだ mock なので **contract を stub で固定**（#31 と同型）。
- **`unsubscribe_removed_instruments_system` は #41 対象外・明示 defer**: backcast に `UnsubscribeMarketData` transport / per-instrument subscription 管理が無い。空概念を先に作ると実装時に二重管理。→ **deferred sibling: live subscription 管理導入時に prune diff から unsubscribe を発火する**（silent-drop 防止のため本 §に明記）。
- **`invalidate_tickers_on_venue_disconnect_system` は不要**: TTWR は永続 `Tickers` を持つから stale source リセットが要るが、backcast は **都度再解決（no cached live universe・D4）**で stale source を保持しない。disconnect すれば次評価で venue-live=false → `null` → no-op（fail-closed で自動成立）。永続 source を将来導入する issue では invalidate-on-disconnect を**必須ペア**にすること。

### D4. 発火点 = poll-loop 内 change-gated `DrivePrune()`・都度再解決・再入防止
- `BackcastWorkspaceRoot.Update()` に `DrivePrune()` を追加（`DriveFooter`/`DriveDepthLadders` と同じ列・DriveFooter の後で DisplayMode を fresh に）。
- **都度再解決**: live universe をキャッシュせず、評価の瞬間に source snapshot ＋ venue state ＋ scenario.end を読む。永続 live source は #41 では**導入しない**。
- **change-gate**（`inputs_changed()` parity）: 入力 fingerprint（mode / source / venueState / status / ids / scenario.end）が前回から変わった時だけ評価。毎フレーム retain しない。
- **再入防止**: prune は poll 駆動で走り `Universe.Changed` を**購読しない**。`PruneRetain` が縮めた結果 `Changed` が同期発火 → 既存 `SyncChartTilesToUniverse` が chart tile を追従（＝下流反映であって prune 再評価トリガーではない）。
- brain = `UniversePruneDriver`（plain C#・probe 検証可能）。root は inputs を組んで `Tick` を呼ぶだけ。

### D5. `InstrumentRegistry.PruneRetain(ISet<string> allowed)` 新設（editable bypass）
TTWR parity 上 `editable=false`（`instruments_ref` / locked）は「ユーザー編集禁止」であって「system prune 禁止」ではない。既存 `Add/Remove/ReplaceAll` は全部 `Editable` gate を持つので prune には使えない。→ editable を bypass する prune 専用 mutator を足す:
- `allowed == null` → no-op（false）
- `_ids.Count == 0`（空 registry）→ no-op
- `allowed.Count == 0` → **callee 側でも no-op**（#253 二重防御・全消し防止）
- `RemoveAll(id => !allowed.Contains(id))`・実際に縮んだ時だけ `Changed` 1 回発火・bool 返し
- ログは `before > after` の時だけ（`mode`/`source` を残し将来 HITL 調査に効かせる）

## 3. スコープ

### 採用（#41）
- `InstrumentRegistry.PruneRetain`（editable bypass・二重防御）
- `UniversePruneGate.CurrentUniverse(inputs)`（Live 三重 gate / Replay end-gate・`null`=未確定）＋ `UniversePruneSourceKind` 列挙 ＋ `UniversePruneInputs` struct
- `UniversePruneDriver`（change-gated tick・都度再解決・brain）
- `IUniversePruneSource` seam ＋ null 実装（実 live/catalog source 来訪まで dormant）
- `BackcastWorkspaceRoot` に `DrivePrune()` 配線（dormant だが wired）
- AFK probe（matrix 固定）

### 不採用（別 issue / defer）
- `unsubscribe_removed_instruments_system`（live subscription 管理導入時・§D3）
- `invalidate_tickers_on_venue_disconnect_system`（永続 source 導入時・§D3）
- 実供給源（DuckDB `listed_info` / venue `fetch_instruments`）= #31 と同じく別 issue。本 slice は contract + stub。

## 4. 検証
backcast 規律（FLOWS.md は無い・正本は本 findings ＋ AFK probe）: **AFK probe（headless・Python-free・brain 駆動）＋（必要なら）HITL**。
- **gate matrix**（characterization・RED→GREEN）:
  - `Live + LiveVenue + {CONNECTED|SUBSCRIBED} + Ready + non-empty` → prune（外銘柄を retain 除外）
  - `Live + ReplayCatalogFallback` → no prune
  - `Live + LocalVenueSnapshot` → no prune（#41 本文優先）
  - `Live + LiveVenue + empty ids` → no prune（HIGH-1）
  - `Live + LiveVenue + {Loading|Error|NotConnected}` → no prune
  - `Live + LiveVenue + Ready + venue Reconnecting` → no prune（Reconnecting 除外）
  - `Live + LiveVenue + Ready + venue Disconnected` → no prune
  - `Replay + end set + Ready + non-empty` → prune
  - `Replay + end unset` → no prune
  - `Replay + {Loading|Error|Empty}` → no prune
- **PruneRetain**: editable=false でも prune・空 allowlist で全消ししない・空 registry skip・縮んだ時だけ `Changed` 1 回。
- **change-gate**: 同一 inputs を 2 回 tick → 2 回目は再評価しない。
- **integration**: gate → PruneRetain → `Changed` 発火（`SyncChartTilesToUniverse` が chart tile を追従するのは root 既存配線で composition 成立）。

## 9. 実装結果（2026-06-17）

durable:
- `Assets/Scripts/ScenarioStartup/InstrumentRegistry.cs` — `PruneRetain(ISet<string>)` 追加（editable bypass・null/empty allowlist 二重防御・空 registry skip・縮んだ時だけ `Changed` 1 回・D5）。
- `Assets/Scripts/Universe/UniversePruneGate.cs` — `UniversePruneSourceKind`（LiveVenue/fallback 列挙）／`UniversePruneInputs`／`UniversePruneGate.CurrentUniverse`（Live 三重 gate ＝ LiveVenue ∧ `{CONNECTED,SUBSCRIBED}` ∧ Ready ∧ non-empty／Replay end-gate・`null`=未確定／`IsVenueLiveForPrune` 述語・D1/D2/D3）／`IUniversePruneSource` ＋ `NullUniversePruneSource`（dormant）／`UniversePruneDriver`（change-gated tick・都度再解決・D4）。
- `Assets/Scripts/Live/BackcastWorkspaceRoot.cs` — `DrivePrune()` を `Update()` の `DriveFooter` 後・`DriveDepthLadders` 前に配線（fresh DisplayMode／pruned chart tile を depth より先に伝播）。`_pruneSource`（null source）＋ `_pruneDriver`（`_scenario.Universe` 上）を構築。VenueState は raw poll を渡し gate が strict 述語を適用（`Conn.IsConnected` を流用しない）。

throwaway / gate:
- `Assets/Editor/UniversePruneProbe.cs` — AFK characterization gate（5 section・40 check）。Live/Replay gate matrix・PruneRetain 契約（editable bypass・全消し防止）・change-gate・gate→PruneRetain→Changed integration・disconnect 自動 fail-closed。

#31（picker）・#29（registry SoT 既存 mutator）は無改変。`IAvailableInstrumentsProvider.Query()` は prune に流用せず分離（R2 asymmetry 保持）。

ゲートログ: `[UNIVERSE PRUNE PASS] gate matrix + PruneRetain + change-gate + integration verified`（exit=0・Unity 6000.4.11f1 batchmode・コンパイルエラー 0）。

### 既知の defer（silent-drop 防止・再掲）
- **unsubscribe-on-prune**: live subscription 管理（`UnsubscribeMarketData` transport / per-instrument subscribe）導入時に、prune diff から unsubscribe を発火する。
- **invalidate-on-disconnect**: 永続 live universe source を将来導入する issue で**必須ペア**として入れる（#41 は都度再解決なので不要）。
- **実供給源**: venue `fetch_instruments`（LiveVenue source の実 producer）／ DuckDB `listed_info`（Replay catalog）が来るまで prune は production で dormant。`NullUniversePruneSource` を実 source に差し替えるだけで起動する。
