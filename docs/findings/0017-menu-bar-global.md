# findings 0017 — menu bar（全体メニュー）: File(Layout) + 実行モード副作用 + venue 統合

issue #42。方針: **ADR-0005（1:1 表面 parity・固定 decision）/ ADR-0001 / ADR-0003**。
`grill-with-docs`（2026-06-15）で導出。CONTEXT.md の **[[menu bar（全体メニュー / screen-fixed chrome）]]** 用語を本スライスで確定。
ADR-0005 は自己保護条項を持つため本 findings に実装事実を記録し、ADR は参照のみ（書き戻さない）。

## 1. 移植 oracle の実態（TTWR `src/ui/menu_bar.rs` を直接読んだ確定事実）

issue #42 本文の AC は TTWR menu_bar の **不正確な言い換え**だった。oracle の実態:

| menu | items | 実挙動 |
|---|---|---|
| **File** | New / Open / Save / Save As | **Layout 文書**操作（`LoadLayout`/`SaveLayout`/`SaveLayoutAs`）。strategy `.py` は layout sidecar が参照し `apply_layout_system → StrategyFileLoadRequested` で**間接**ロード。menu 自体に strategy opener は無い。 |
| **Edit** | Undo / Redo | `UndoMenuRequested`/`RedoMenuRequested` |
| **Venue** | Connect Tachibana(Demo/Prod), Connect kabu(Verify/Prod), Disconnect | `VenueLogin`/`VenueLogout` |
| **Help** | Settings | settings modal spawn |

**実行モード切替は独立 picker ではなく File 操作の副作用**（Live 限定）:
- `File→New` → `ForceStop` ＋ `SetExecutionMode(LiveManual)`（loaded strategy/fragments/instrument registry を unload。venue 未接続なら gate で no-op）。
- `File→Open` を **Live 中**に行うと → `SetExecutionMode(LiveAuto)`。
- Replay item も 3-way picker も menu_bar には**無い**。

→ AC①「strategy の Open/Save」= File=Layout の誤記（strategy 編集は #16 が所有）。AC②「Replay/LiveManual/LiveAuto を切替」= menu_bar に無い surface（明示 picker は footer #39/#30 の責務）。**正すのは AC の文言であって ADR ではない**。

## 2. 確定した canonical model（Model A: TTWR 忠実 parity）

- **File = Layout**: menu bar の File から Layout の New/Open/Save をする。strategy `.py` 編集は **#16（CLOSED）** が所有し layout sidecar 経由で参照復元。menu bar は strategy opener を再実装しない（AC③）。
- **mode = 副作用**: 明示 picker を menu bar に持たない。`File→New`→clear＋`SetExecutionMode(LiveManual)`、Live 中 `File→Open`→`SetExecutionMode(LiveAuto)`。明示 Replay/LiveManual/LiveAuto 切替と run 操作（▶/pause/step/speed）は **footer #39（OPEN）/#30（OPEN）** が所有。
- **#38（CLOSED）**で LiveAuto 実配線済 → `SetExecutionMode(LiveAuto)` 副作用は **#42 で完全配線可能**（「LiveAuto 項目は #38 後」の前提は満たされている）。
- **venue 統合**: Connect×venue / Disconnect を menu bar に統合。既存 **`VenueMenuViewModel`（#21）を再利用合成**（ロジック再実装禁止）。`ProductionLiveShell.DrawChrome()` のインライン venue Connect/Disconnect ボタン＋badge は撤去し menu bar に委譲。
- **所有権**: `ProductionLiveShell` は composition root（live loop / GIL 規律 / 全 durable 型の所有者）のまま。menu bar は Shell がホストする pure-logic ViewModel（AFK 駆動）＋描画 View。VM は `VenueConnectRequest` と同型の request を emit、RPC/local op は Shell/`LiveRpcLanes` が実行。

## 3. backcast が TTWR と食い違う 2 gap と解消

- **gap① ForceStop 不在**: backcast の transport に走行中 replay 停止（`force_stop_replay` 等価）が無い（`pause/resume/step_backtest` のみ、停止は `close()` 全 teardown だけ）。→ `File→New` の parity-critical は **clear ＋ LiveManual** に限定。**走行中 replay/live の停止は #39(live)/#30(replay) の責務**。#42 は実行中なら **refuse**（下記）で代替し teardown に手を伸ばさない。replay-stop transport は #30 へ寄せる follow-up。
- **gap② precondition が raise vs no-op**: `mode_manager.set_execution_mode` は venue 未接続で `LiveManual`/`LiveAuto` を `ValueError(EXECUTION_MODE_PRECONDITION)` で**拒否**。TTWR は gate で**黙って no-op**。→ menu bar VM 側で **venue が `CONNECTED`/`SUBSCRIBED` のときだけ** `SetExecutionMode` を送るガードを置き、TTWR の **observable no-op** を再現（例外回避）。clear 自体は無条件。

## 4. File→New の clear 範囲 — Full TTWR parity（in-memory reset 限定）＋ 実行中 refuse

TTWR `FileNewRequested` が reset する実体 = StrategyBuffer + fragments + InstrumentRegistry + ScenarioMetadata + ScenarioReadTarget + editor panel despawn。backcast には等価 primitive が揃っており**新規 architecture ではなく wiring**:

| TTWR reset | backcast 等価 primitive | 所在 |
|---|---|---|
| StrategyBuffer/fragments | `StrategyDocument.ResetUnboundEmpty()` ＋ editor panel despawn ＋ `StrategyProviderRegistry.Unregister(windowId)` | #16 |
| InstrumentRegistry clear | `InstrumentRegistry.ReplaceAll(empty)` | #29 `ScenarioStartupController.Universe` |
| ScenarioMetadata/ReadTarget | `ScenarioStartupController.Clear()`（Params 再初期化 + read target reset） | #29 |
| editor panel despawn | `FloatingWindowController.Apply(LayoutDocument.Default())` | Layout |

唯一の小追加: **#29 に `Clear()`**（Params 初期化 + `Universe.ReplaceAll(empty)` + Errors reset + scenario read target reset）の cohesive メソッドを足す（architecture 変更ではない）。

**実行中 refuse（安全境界・ADR-0001）**: `_autoRunId != ""`（live auto 走行中）または replay 走行中（engine `RUNNING`）なら `File→New` を **refuse**（「実行中の strategy/replay を停止してから New」を notice）。clear も mode 変更もしない。根拠: backcast は mode と run が分離（`set_execution_mode` は走行中 `_autoRunId` を止めない）ため、blank workspace ＋ live 執行継続は ADR-0001 の禁止状態。teardown は #39/#30 所有なので #42 は refuse に留める。

**#42 から明示除外**（過剰/TTWR もやらない）: scenario sidecar `.json` の削除（in-memory reset のみ）／live/replay teardown（#39/#30）／`LayoutStore.Save(Default())` auto-persist（別関心）。

## 5. File(Layout) 操作の深度 — 縦切り優先（Option 1）

backcast の layout は **単一固定パス auto-sidecar**（`LayoutPathResolver.DefaultPath()` = `persistentDataPath/layout.json`、`LayoutStore.Save/Load` は素朴 overwrite/fail-soft）であって multi-document ではない。native file picker は backcast に**一切存在しない**。

- **#42 範囲**: `File→Open` = resolver パスの `layout.json` を `LayoutStore.Load` → strategy/panel 復元（Live 中なら先に `SetExecutionMode(LiveAuto)`）。`File→Save` = 現 layout を capture → `LayoutStore.Save(resolver パス)`。`File→New` = §4。
- **Deferred（follow-up issue 起票で silent drop にしない）**: native file picker ＋ `File→Save As` ＋ 任意パス Open（= TTWR の multi-document layout surface）。third-party file picker 依存の導入 ＋ `LayoutStore` の multi-doc 化は mode-seam tracer と直交する別スライス（ADR-0001「複雑化は先回りで入れない」）。

## 6. Venue connect variants — 4 全出し＋prod は ALLOW_PROD grey-out（既存ガード流用）

prod 接続は**既に多層ガード済**: Python の `KABU_ALLOW_PROD`/`TACHIBANA_ALLOW_PROD == "1"` 環境変数 → 無いと login dialog で prod grey-out → backend `PROD_NOT_ALLOWED` / `require_prod_env` で二重拒否（`kabusapi_login_flow.py` / `tachibana_login_flow.py` / `kabusapi_url.py`）。

- **#42 範囲**: Connect Tachibana(Demo/Prod) / Connect kabu(Verify/Prod) / Disconnect の 4 variants を出す。prod は `VenueMenuViewModel` を拡張し、**login dialog と同じ作法**で `*_ALLOW_PROD` env が "1" のときだけ enable（無ければ grey-out）。Python が安全 authority（C# grey-out は UX parity）。新しい安全装置は作らない（既存流用＝AC③整合）。

## 7. Edit / Help（構造 parity・中身は委譲/stub）

menu 構造 File/Edit/Venue/Help は出す。**Edit(Undo/Redo)** は active editor（#16 `EditHistory`）へ委譲（active editor 不在なら disabled）。**Help→Settings** は ADR-0005 が別 surface として列挙済のため item は出すが settings modal 本体は settings slice に deferred（stub）。

## 8. 検証

- **AFK probe**（headless 決定的）: MOCK 接続 → `File→Open`(Live 中) → `get_state_json` の `execution_mode == LiveAuto` を assert。`File→New` → `LiveManual`（接続中）/ 未接続なら mode 不変（no-op）を assert。実行中 `File→New` → refuse（clear/mode 変更が起きない）を assert。venue prod gate（`*_ALLOW_PROD` 未設定で prod `CanConnect==false`）。
- **HITL harness**（owner 目視）: menu 描画・File/Venue 操作・mode badge 反映。`ProductionLiveShell` のインライン venue chrome 撤去後の重複ゼロ確認。
- pure VM unit（AFK）: `CanConnect`/prod-gate/mode-guard/`File→New` refuse-when-running。

### 検証実績（2026-06-15・Unity 6000.4.11f1 実機）

- **コンパイル PASS**: `Unity -batchmode -quit -nographics` で exit 0・`error CS` 0 件（ILPP post-process まで到達）。
- **headless 判定ロジック 16/16 PASS**: `Unity -batchmode -executeMethod MenuBarVerify.Run`（Python 不要・`Assets/Editor/MenuBarVerify.cs`）。prod grey-out gate / `File→New` refuse・clear・guarded-LiveManual no-op / `File→Open` LiveAuto 副作用 / `ScenarioStartupController.Clear()` を実機実行で GREEN。
- **実機 HITL 14/14 PASS（AC④）**: `Tools > Backcast > Menu Bar HITL (#42)`（`MenuBarHitlHarness` + `MenuBarHitlMenu`）を Play で起動。MOCK engine 起動で **engine 往復まで GREEN**——C3 `File→Open-while-Live → get_state_json execution_mode==LiveAuto`、C5 `File→New → execution_mode==LiveManual`、C1 CONNECTED で `LiveModeAllowed`。Console 0 error / 0 warning。
- **運用 gotcha**: menu-driven HITL は単一 Play-owner 規律のため、当時の auto-bootstrap owner（`ReplayPanelsHarness.AutoBootstrapEnabled`）を一時的に `false` にして engine を解放 → HITL 後 `true` に復元（findings 0005 §6 の規律どおり）。`ReplayPanelsHarness` は別途 Mac 固定パス（`/Users/sasac/...` strategy/catalog）が Windows で未解決の既知問題あり（#42 外）。

## 9. 起票する follow-up（ADR-0005 surface を silent drop しないため）

- (a) native file picker ＋ `File→Save As` ＋ 任意パス layout Open（multi-document layout surface）。
- (b) replay-stop transport（gap① の `force_stop_replay` 等価）を **#30** に寄せる。
- (c) `ProductionLiveShell` の Manual/Auto footer toggle（現状 decorative）の正式 footer 化は **#39** が所有 — #42/#39 の撤去境界は実装順調整。
- (d) `execution_mode` を `VenueConnectionViewModel.ApplyStatePoll` の `StateDto` に bind し、`mode:` 表示と将来の現モード参照を **engine 正準の poll 値**へ寄せる（現状 `_execMode` は shell の last-sent 送信意図 mirror＝二重 source of truth。本スライスは shell が mode の唯一の writer なので一貫するが、engine 自律遷移で drift し得る。File→Open 副作用の **同期判定**は last-sent intent の方が fresh なので決定源は据え置き、display/sole-truth のみ canonical 化＝/simplify altitude Q4・LOW）。
- (e) live engine boot（`DataEngine` + `set_rust_event_sink` + `InprocLiveServer` + `sys.path` insert）が LiveSpike harness 群（`DepthLadderHitlHarness`/`LiveAdapterTracerHitlHarness`/`ProductionLiveShell`/`MenuBarHitlHarness`）で copy-paste されている。共有 `LiveEngineBootstrap` への抽出は #42 を超える project-wide cleanup（/simplify reuse 指摘・既存 helper 不在のため #42 では新規 duplication ではない）。
