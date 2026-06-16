# findings 0026 — footer LiveAuto 起動 + mode 切替（#39 Slice 3 / UI）＋ #59 workspace root 統合

> **採番**: 当初 0025 で起票したが、origin/main が `0025-backcast-workspace-root.md`（#59）を先に main へ
> 載せたため番号衝突。本書を **0026** へリネームして解消（2026-06-16）。本文中の旧「0025」自己参照は本書
> （=0026）を指す。

issue #39。親: #4（Step 2: Live/Auto parity）/ #5（Step 3: カットオーバー）/ #59（本線 workspace root）。
方針: **ADR-0005（1:1 表面 parity・固定 decision）/ ADR-0001（in-proc 埋め込み・orphan 不在）**。
`grill-with-docs`（2026-06-15）で導出。移植 oracle = TTWR `src/ui/footer.rs`（mode segment /
`execution_mode_toggle_system` / `footer_pause_resume_system` の LiveAuto 枝 / 可視性 system 群）。
ADR-0005/0001 は自己保護条項を持つため本 findings に実装事実を記録し、ADR は参照のみ（書き戻さない）。
CONTEXT.md の **[[footer（screen-fixed chrome / 実行トランスポート）]]** 用語（#30 で確定）を本スライスで拡張。

---

## 0. #39 のスライス構成（engine 先行・本スライスは UI）

#39 は engine 側がすでに着手済みの multi-slice issue。本タスクは **Slice 3（footer UI）**:

- **Slice 1（engine・実装済み）**: Replay 切替時の `live_last_error` 抑止（`live_orchestrator.py:881`・
  抑止フラグ。runner/bridge の `_last_error` 自体は消さない＝stale-CONNECTED の healthy 誤判定を防ぐ）。
- **Slice 2（engine・実装済み）**: Replay 中の account fetch/emit 抑止（`account_sync.py:62/93`・`_tick` 入口の
  mode_provider gate）。
- **Slice 3（本スライス・UI）**: footer の mode セグメント（Replay/LiveManual/LiveAuto）＋ LiveAuto ▶ 起動。

## 1. 移植 oracle の実態（TTWR `footer.rs` を直接読んだ確定事実）

- mode セグメント3つを spawn（`spawn_mode_segment`）。**Manual/Auto は venue が live のときだけ表示**
  （`apply_venue_live_button_visibility_system`）、Replay は常時表示。
- mode クリック → `SetExecutionMode` RPC（`execution_mode_toggle_system`）。**楽観的ローカル状態を持たず、
  poll 差分（`BackendStatusUpdate::ExecutionModeChanged`）でしか `ExecutionModeRes` を更新しない**＝UI/backend
  desync を構造的に防ぐ。Live 遷移は venue Disconnected/Error なら client 側 precondition で送らず warn のみ。
- venue 非 live 転落で Live→Replay 自動復帰（`auto_replay_on_venue_disconnect_system`）。
- LiveAuto ▶（`footer_pause_resume_system` の LiveAuto 枝）= ①二重押しガード（`command_in_flight` or Running×run_id=None）
  → ②run_id ありなら Running→Pause / Paused→Resume トグル → ③停止中なら pre-flight 通過後に `StartLiveAuto` 送出。
- 可視性（`apply_execution_mode_visibility_system`）: LiveAuto では ▶ のみ表示、step/stop/speed は隠す。
  LiveManual では ▶ すら隠す（手動発注は別 ticket・footer の責務外）。
- **TTWR footer は live run を止めない**。`StopLiveStrategy` は protocol/inproc handler/内部状態機械にしか現れず
  **UI から一度も送出されない**。唯一の live teardown 経路は `venue_logout`→`_teardown_live_components`。
  TTWR のモデル = **live run の寿命 = venue セッション、mode とは別物**（Replay に切替えても auto は走り続ける）。

## 2. backcast seam の実態（一次コード読みで確定）

- engine seam は #38/#25 で**実配線済み**。`set_execution_mode` / `register_live_strategy` /
  `start_live_strategy` が `inproc_server.py`→`backend_service.py`→`_backend_impl.py`→`live_orchestrator.py` で公開。
  `start_live_strategy` は本物の auto ループを起こす（`_strategy_host.start_run`→controller.attach→on_start→
  live loop→post-trade 評価。Noop ではない）。
- **2段直列の backcast 形**: `register_live_strategy(strategy_file, original_path)`→`strategy_id` /
  `start_live_strategy(strategy_id, instrument_id, venue, safety_limits_dict=None)`→`run_id`。
  precondition: `start_live_strategy` は `ExecutionMode==LiveAuto` 必須（`live_orchestrator.py:1623`）。
  **TTWR の `StartLiveAuto` wire が運ぶ `allowed_instruments` を backcast seam は取らない。`safety_limits` も optional**。
- **`set_execution_mode` は TTWR とバイト同一**（`live_orchestrator.py:856-884` ≡ TTWR `:805-833`）。両方とも
  Replay 切替で run を止めず抑止フラグを立てるだけ。→「mode と run は分離」が両者共通のセマンティクス。
- 既に `ProductionLiveShell.RegisterAndStartAuto()`/`StopAuto()` が IMGUI placeholder として register→start /
  stop を叩いて動作（`ProductionLiveShell.cs:331-377`・コメントに「これは footer #39 surface」）。本スライスで
  production footer に役目を移し placeholder を退役させる。
- LiveAuto run 状態の権威 = `LivePanelViewModel.LatestLifecycle`（`LiveLifecycleEvent` の RunId/Status、
  C# `LiveBackendEventSink`→`Apply` 経由）。AC3 の terminal 返し分け／re-arm はここから導出（#30 の
  `ReplayLifecycle` は Replay 専用なので Live は別系統）。
- 選択中銘柄 = `SelectedSymbol`（`UniverseSidebarController` 保持）。戦略 = `IStrategyFileProvider`
  （#16・supplyable 5条件）。venue = `VenueConnectionViewModel` の poll venue_id（fallback 構成 venue）。
- **穴は C# 側（Python は塞がっている）**: poll は **execution_mode を最初から権威値で運んでいる**——
  `core.py:494` が `get_current_state()` で `execution_mode=mode_manager.current_mode` を populate する
  （model 構築時に入るので `_backend_impl.get_state_json` の update dict には現れない＝TTWR `_backend_impl` でも
  同理由で見えなかった）。`models.py:70` の `TradingState.execution_mode` も実値で出る。**真の穴は C#**:
  `VenueConnectionViewModel.StateDto`（`cs:44-48`）は「自分が要る2スカラ（venue_state/venue_id）だけ parse」する
  明示方針（`cs:52-53`）で execution_mode を読まない（shell は last-sent の `_execMode` mirror を使用）。これが
  findings 0017 §9(d) の穴の実体。→ engine 変更は不要。**`FooterModeViewModel` が同じ poll 文字列から自前の最小 DTO
  （`{ string execution_mode; }`）で parse して `DisplayMode` を上書き**（D1）。`VenueConnectionViewModel` を拡張せず
  各 VM が自分の関心だけ読む＝tested VM 無傷（`ReplayTransportViewModel` を触らないのと同原則）。
  Python 契約ガードとして「`set_execution_mode(LiveAuto)` 後 poll が `execution_mode=="LiveAuto"`」のテスト2本を
  追加済み（2 passed・将来 `core.py` が execution_mode を落としたら footer が無言で壊れるのを防ぐ低コスト回帰）。

## 3. 決定（grill 2026-06-15）

### (D1) mode 真実源 = 楽観＋poll 答え合わせ（poll 値で常に上書き）

- **Replay / 速度 / step など engine が断り得ない切替は poll を待たず即時反映**。
- **Live（Manual/Auto）への切替は押下→即ロック＋スピナー→poll で通れば切替／断られたら解除**（実質 TTWR parity）。
- poll の値が常に正準で、楽観表示を上書きする。

**唯一の TTWR poll 正準からの逸脱**: 「engine が断り得ない切替（Replay/速度/step）は poll を待たず即時反映」。
根拠 (1) これらは engine が拒否し得ず即時反映しても poll が同値を返すだけで構造的に desync しない、
(2) 反応速度を優先する owner 要望。Live 遷移だけは拒否され得るのでロックして engine の答えを待つ＝ここは parity。
→ CONTEXT.md の footer 用語に1行追記（§下）。

### (D2) LiveAuto の停止 = C# が明示的に run をたたむ（表面 parity・配線は backcast 固有の追加）

stop ボタンは**出さない**（TTWR 表面 parity）。だが backcast は findings 0017 §4 で orphan（blank/Replay 表示
なのに live 執行継続）を **ADR-0001 由来の不変条件として禁止**し、「走行中 live の停止は #39 の責務」と先送り済み。
TTWR footer は teardown を持たない（venue 寿命に紐付け）が、**backcast はその不変条件のぶん厳しいので、TTWR footer
に無い teardown を追加する**——これは正当な逸脱（justification: backcast の orphan 禁止不変条件。「TTWR engine に
合わせる」ではない）。

**動作詳細（C案・確定）**: footer が「LiveAuto から抜ける」操作（Replay **または** LiveManual セグメント押下）を受けたとき:
- run_id あり（Running/Paused）: 先に `stop_live_strategy(run_id)` → 成功時のみ `set_execution_mode(target)` 送出。
  この間はロック＋スピナー（graceful teardown は時間がかかるので即時にしない＝D1 の Live 遷移と同じ扱い）。
- stop 失敗: mode を切替えず **LiveAuto に留め**、エラー表示。→「Replay 表示なのに run 生存」を構造的に作らない。
- run_id 無し: Replay 切替は即時（engine が断れず desync しない）。
- LiveManual に抜けるときも auto loop が裏で発注し続けたら同じ orphan なので同様に stop してから切替える。

**venue-drop auto-replay も同じ stop-then-switch を通す（grill round で確証・誤前提を訂正）**: 外部 venue drop は
走行中 LiveAuto run を**止めない**——`live_orchestrator._publish_venue_logout`（:1061-1069）は `VenueLogoutDetected` を
emit するだけで `_teardown_live_components` も `stop_live_strategy` も呼ばない（kabu watchdog も Tachibana SS hook も
同コールバック :264/:273）。よって poll 由来の auto-replay（`FooterModeViewModel.ShouldAutoReplay`）で host が素の
`SetExecutionMode(Replay)` だけ送ると、run は zombie として生存し Replay 表示＝D2 が防ぐ orphan と同型になり、venue
再接続で ▶ が Pause/Resume 判定になって**捨てたつもりの run が蘇り実発注を再開し得る**。→ **auto-replay も active run が
あれば user-leave と対称に `stop_live_strategy(ActiveRunId)`→成功で `SetExecutionMode(Replay)`**（venue 不在なので
graceful cancel は best-effort・ローカル teardown は成立）。「user-leave は stop / venue-drop-leave は stop しない」の
非対称は不変条件違反＝禁止。

### (D3) LiveAuto ▶ の pre-flight 材料（backcast seam へのマッピング）

| TTWR `StartLiveAuto` | backcast | 出どころ |
|---|---|---|
| `strategy_file` | `register_live_strategy(strategy_file=...)` | `IStrategyFileProvider`（supplyable・呼出時再問い合わせ） |
| `original_path` | `register_live_strategy(original_path=...)` | 同 provider のパス（backcast は cache/original 分割を持たない・provider が保存済み canonical を返す） |
| instrument | `start_live_strategy(instrument_id=...)` | scenario universe ∩ `SelectedSymbol`（無ければ先頭）＝TTWR `check_live_auto_venue_and_instrument` と同型 |
| venue | `start_live_strategy(venue=...)` | `VenueConnectionViewModel` poll venue_id（fallback 構成 venue） |
| `safety_limits` | `start_live_strategy(safety_limits_dict=None)` | **None**（backcast scenario に safety_limits 無し・scenario 由来 rail は follow-up） |
| `allowed_instruments` | — | **backcast seam は取らない**（map 先なし） |

▶ の文脈分岐（TTWR `footer_pause_resume_system` LiveAuto 枝と同型）:
- 二重押しガード（起動 in-flight or Running×run_id 未確定）→ block。
- run_id あり: Running→`pause_live_strategy` / Paused→`resume_live_strategy`（即時・graceful 不要）。
- 停止中（Idle/terminal）: pre-flight 通過時のみ register→start（起動はロック＋スピナー）。terminal 後の ▶ は re-arm。

## 4. 実装の置き場所（parity-first）

- #30 の `ReplayFooterView` + `ReplayTransportViewModel`（production uGUI・Replay専用）を**拡張**し、mode セグメント
  （Replay/Manual/Auto）と LiveAuto ▶ を載せる。TTWR が `footer.rs` 1枚で全モードを捌くのに合わせ footer は1つに保つ。
- LiveAuto 起動／停止／pause/resume の orchestration（register→start・stop-on-leave・文脈分岐）は durable な
  pure-logic VM（`ReplayTransportViewModel` 同型・AFK 駆動）に置き、uGUI view は intent を上げるだけ。
- `ProductionLiveShell.RegisterAndStartAuto()/StopAuto()` の register→start / stop ロジックと2段ガードを
  production footer 側へ移し、IMGUI placeholder を退役（findings 0017 §6(c) の撤去境界）。

## 5. composition root / 検証 venue（確定）

- **(O1 確定) HITL host = `ProductionLiveShell`**。findings 0017 §2 が ProductionLiveShell を composition root
  （live loop / GIL 規律 / run_id / 全 durable 型の所有者）と明記。findings 0017 §9(c) が「Manual/Auto footer toggle の
  正式 footer 化は #39 が所有」と名指しで先送り済み＝本スライスがその回収。production footer を ProductionLiveShell に
  配線→IMGUI placeholder 退役→mode/▶/抜け時 stop をここで検証。同 footer view の Replay transport ボタンは可視性で
  隠れて同居するだけ（Replay run を ProductionLiveShell から駆動する完全統合シェルは別スコープ・findings 0023 §5）。
- **(O2 確定) AFK = MOCK / HITL = MOCK 全ロジック ＋ tachibana demo 実起動1レグ**。
  - MOCK が回帰ガードの正本。#39 新規挙動（orphan-teardown: Replay/Manual に抜ける→`stop_live_strategy(run_id)`→
    teardown / 二度押しガード / ▶ 文脈分岐）は MOCK で register→start→live loop→(mock fill)→抜けて teardown まで
    **決定的に・閉局でも**全部回せる。
  - demo leg = 既存 Tools > Backcast > Live Demo Roundtrip（Tachibana demo・findings 0014 が確立、2026-06-14 実証）。
    **demo leg の done-bar = 「tachibana demo で ▶→RUNNING 到達＋Replay/Manual に抜けて teardown を実機目視」**
    （閉局でも観測可能）。実 fill→建玉反映は market-hours-gated で #23 LiveDemoRoundtrip が既に所有する領域＝
    #39 固有の新規挙動ではない（平日場中ならボーナス確認）。kabu Verify は同 launcher 並記の代替（owner 任意）。

## 6. 検証（予定・RED 先行・正本は本 findings＝backcast に FLOWS.md 無し）

- **C# AFK probe**（headless 決定的・`ReplayTransportVerify` 同型・MOCK）: mode 遷移（楽観＋poll 上書き・Live ロック→
  poll 解除）、可視性（Manual/Auto は venue live 限定・LiveAuto で step/stop/speed 隠す・LiveManual で ▶ 隠す）、
  ▶ 文脈分岐（起動/pause/resume・二度押しガード）、LiveAuto 抜け時の stop-then-switch・stop 失敗時 LiveAuto 維持、
  terminal re-arm。register→start→RUNNING→抜けて teardown（run_id クリア・orphan 不在）まで MOCK で決定的に。
  **必須ゲート2本（grill round で追加）**: (G1) **venue-drop-while-LiveAuto-running → run が stop される**
  （`ShouldAutoReplay` 消費時に active run なら mode→Replay だけで終わらず `stop_live_strategy` を通す）。
  (G2) **start ok → 初手 lifecycle が terminal(ERROR) → ▶ が再 arm**（`_startInFlight` が同期 run_id 解除で stick しない）。
- **HITL（owner 目視）**: ①MOCK で venue 接続→Auto 切替→▶ で strategy 起動→panel 反映→Replay/Manual に抜けて
  teardown（orphan 不在）。②tachibana demo で ▶→RUNNING 到達＋抜けて teardown（done-bar・閉局可）。

### 検証実績（VM ロジック・2026-06-15）

- **C# AFK PASS**: `Assets/Editor/FooterLiveAutoVerify.cs` を `Unity -batchmode -executeMethod FooterLiveAutoVerify.Run`
  で **29/29 PASS**・`error CS` 0 件（compile 成功）。カバレッジ: mode D1（poll 上書き・Live ロック→poll 解除・拒否解除・
  Replay 即時）／可視性（Manual/Auto は venue live 限定）／▶ 文脈分岐（Start/Pause/Resume/re-arm）＋pre-flight 4ゲート＋
  instrument 解決（selected∈universe 優先・無→先頭）＋二度押し（ack ok でも lifecycle 未追従なら block）／D2 stop-then-switch
  （Replay/LiveManual 両方向）／**G1**（venue-drop 中 run 生存＝host stop 必須）／**G2**（start ok→初手 ERROR→▶ 再 arm）。
  Unity 6000.4.11f1・Windows。

- **view（item4）/ 配線（item5）compile クリーン（2026-06-15）**: `ReplayFooterView` を後方互換拡張（modeVm/autoVm/onMode は
  optional・null で #30 Replay-only render を完全保持＝`ScenarioStartupHitlHarness` 無改変）。mode セグメント3つ＋mode-routed ▶
  （Replay/LiveAuto 共有・LiveManual は ▶ も隠す）＋step/stop/speed は Replay 限定可視。`ProductionLiveShell` に production footer を
  配線（Canvas/EventSystem/40px bottom bar・harness 同型）、IMGUI Manual/Auto placeholder（`Mode` toggle / `DrawAutoControls` /
  `RegisterAndStartAuto` / `StopAuto`）を退役、register→start / pause/resume / stop-then-switch を VM-routed handler へ移設。
  worker→VM 通知は volatile signal 経由で main 消費（pure VM を off-thread mutate しない）。**`FooterLiveAutoVerify.Run` を
  改変後アセンブリ全体で再 compile→29/29 PASS・`error CS` 0**（item4/5 の compile ゲート）。**残: HITL（item7・uGUI 実描画/クリック/
  mode 切替/LiveAuto 実起動/抜け teardown）— probe は compile+VM ロジックのみで uGUI runtime は未検証**。

### code-review（high/recall・2026-06-15）— Medium+ 5 件を修正

`/code-review`（7 angle・finder→verify）で item4/5 を grill。**CONFIRMED Medium+ 5 件**を RED 観点で確定し修正（CLAUDE.md
「Medium 以上が消えるまで」規約）。シグネチャは実型照合済み・**compile は owner 側 Unity 再検証が owed**（当 dev 環境で C# compile/run 不可）:

- **[High] mode 切替と auto-start が別 single-flight**（`_modeSending` vs `_autoActionSending`）→ `set_execution_mode(Replay)` が
  `start_live_strategy` と GIL 上で競合し Replay 下で run 起動＝orphan。start in-flight 中は `HasActiveRun=false` で D2 も回避される。
  **fix**: `LiveAutoTransportViewModel.IsStartInFlight` を公開し、`OnFooterMode` が `_autoActionSending || IsStartInFlight` の間は
  mode 切替を block（notice 表示）。StopRunThenSwitch が lock だけ立てて early-return する stuck も同時解消。
- **[Med] FooterAutoStart が canTrade 接続ゲート喪失**＋venueIdentity の `_venue` fallback（既定"MOCK"）が空にならず `BlockedNoVenue`
  dead。**fix**: `!_serverReady || !_conn.IsConnected || _teardownComplete` を register→start 前に再アサート（manual ticket と対称化）。
- **[Med] disconnect 後 footer stuck**: `Update` が `_finalStateJson` を `DriveFooter` より先に null 化 → `_footerMode` が最終状態を
  受け取れず Manual/Auto セグメント＋ticket 残存。**fix**: teardown ブロックで `_footerMode.ApplyPoll(_finalStateJson)` も給電。
- **[Med] `_autoRunId` は StopRunThenSwitch でしか clear されず**自然終了（ERROR/STOPPED/complete）で stale → `isLiveAutoRunning`
  stuck true → File→New 永久拒否。**fix**: `isLiveAutoRunning` を lifecycle 権威 `_footerAuto.HasActiveRun` 参照に変更。
- **[Med] `ShouldAutoReplay` level-trigger** → consume 後に StopRunThenSwitch が early-return で取りこぼし／lag 窓で二重 stop。
  **fix**: `DriveFooter` が `!_teardownComplete && !_autoActionSending && ShouldAutoReplay` のときのみ consume+act（teardown 後の
  RPC 発火も防止）。

**LOW（未修正・記録のみ）**: lock 中 Replay セグメント無効化で abort 不可・lock タイムアウト無し・sig が ActiveRunId 欠落/_autoStatus
余分・選択セグメントの lock 中 dim・ResolveInstrument の silent 代替（TTWR parity）。**owner 側 TODO**: 改変後の compile + `FooterLiveAutoVerify`
再走（new gate の AFK 追補が望ましい）→ HITL。

---

## #39 → #59 統合（footer を本線 workspace root へ載せ替え・2026-06-16）

origin/main の **#59「Backcast workspace root」**（scene-authored 合成ルート・`BackcastWorkspaceRoot` + `ReplayEngineHost` +
findings 0025-backcast-workspace-root + ADR-0009）を merge したところ、#59 が footer を **Replay-only** で構築していた
（`ReplayFooterView` の #30 コンストラクタ）。owner 決定（2026-06-16）: **#59 の枠を正準とし、#39 の footer 機能をそこへ載せ替える**。

### 確定した方針（owner grill 2026-06-16）
- **decision 1**: `ReplayEngineHost`（Replay 専用・Run ごと server 構築）を **一般化** し、起動時に **live 構成の永続 server を1本**
  （`DataEngine` + `set_rust_event_sink` + `InprocLiveServer(de, venue)`）建てて Replay も Live も同じ server で回す。`InprocLiveServer` は
  replay/live 両 RPC を同一 façade に持つ（inproc_server.py）。
- **decision 2**: live seam（event sink / `LivePanelViewModel` / `VenueConnectionViewModel` / `SecretModalController` / `LiveRpcLanes` /
  venue login / live strategy lifecycle）は **host が所有**、`BackcastWorkspaceRoot` は View/VM を配線するだけ。
- **decision 3（退役順序）**: `ProductionLiveShell` は live seam 移植＋検証が GREEN になるまで削除しない。

### 実装ステップと検証（GREEN）
1. **Step 1（engine AFK）**: live 構成 server で Replay 経路が不変であることを pin（`test_live_configured_server_replay_intact.py`・GREEN）。
2. **Step 2（host）**: `ReplayEngineHost` → **`WorkspaceEngineHost`**。永続 live server・poll は `LiveRpcLanes` に一本化・live seam 所有・
   live RPC メソッド化（`VenueLogin`/`SetExecutionMode`/`RegisterAndStartLiveAuto`/`Pause|Resume|StopLiveStrategy`/`StopLiveThenSetMode`(D2)）・
   `_liveRpcInFlight` 単一フライト。Replay API は完全保持。**headless compile GREEN・CS0420 0**。
3. **Step 3/4（root 配線）**: `BackcastWorkspaceRoot` に mode-aware footer・menu Venue を host へ・`DrainLiveEvents`・`DriveFooter`
   （#39 review fix 込み移植）・mode-routed `OnFooterPlayPause`/`OnFooterMode`/`FooterAutoStart`。**headless compile GREEN**＋
   **engine AFK**（`test_live_auto_lifecycle_inproc_server.py`: venue_login MOCK→LiveAuto→register→start→stop→Replay・GREEN）。

### スコープ境界（#59 が元々分離）
- **prod venue submenu UI = #42** / **secret modal・manual order ticket・live panels = #23**。本統合（#39）の責務外で、workspace root
  にはまだ無い。AFK は `host.VenueLogin` を直接駆動するため #39 検証に venue UI は不要。
- **ProductionLiveShell の扱い**: #39 footer の重複に加え、**#23 の manual ticket / live panels / tachibana live-demo roundtrip HITL** を
  内包する（mainline root には未移植）。全面退役は #23 再 home が前提＝**owner 判断待ち**（findings 末尾「未決」）。
- **ADR-0009 昇格**: 統合 root の **owner HITL（workspace scene 実 Play）GREEN 後**に proposed→accepted。現状 proposed のまま。

### 未決（owner 判断・session 末）
- `ProductionLiveShell` の最終処遇（(a) #23 HITL harness として残置 /(b) 全面退役＋#23 喪失 /(c) #23 を root へ再 home）。
- `ProductionLiveShell.cs` の owner 未コミット WIP（MOCK dev connect ボタン）の commit/discard。
- owner HITL: workspace scene を実 Play し、Connect→Auto→▶→RUNNING→抜けて teardown を目視（MOCK・閉局可）。
