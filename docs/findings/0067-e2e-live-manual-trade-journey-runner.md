# findings 0067 — E2E LiveManualTradeJourney runner（第二波・Journey・全行新規）

`Assets/Tests/E2E/Editor/LiveManualTradeJourneyE2ERunner.cs`（台本: 同ディレクトリ `.md`）。第二波 14 本目・
横断ストーリーの Journey E2E。**手動実取引フローの縦縫い**（venue 接続 → LiveManual モードゲート → Order ticket
操作可 → 発注 → 第二暗証 → mock fill → Positions 建玉 → resting 取消 → logout 収束）を観測する。実 venue / 実暗証 /
実約定（JOURNEY-LIVE-14）は HITL のまま。`VenueLoginSecretProbe`（#2 据え置きの pythonnet 上流）は移送せず、
secret/lane recipe の手本として参照する。

## 実行コマンド

```text
# compile-only ゲート（error CS\d+ が 0 件・return code 0）
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "C:\Users\sasai\Documents\backcast" -logFile "<log>"

# AFK GREEN
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "C:\Users\sasai\Documents\backcast" \
  -executeMethod LiveManualTradeJourneyE2ERunner.Run -logFile "<log>"
# expect: [E2E LIVE MANUAL TRADE PASS] ... / exit=0
# 確認は Bash `grep -a "E2E LIVE MANUAL TRADE" <log>`（PASS 行は → を含むため ripgrep/Select-String は不可）
# Python-FULL（lane/secret/fill）なので sentinel（Found no leaked weakptrs / Package Manager shutdown）まで待つ
```

## section ↔ Action ID

| section / phase | Covers | 基層 | 観測 |
|---|---|---|---|
| `SectionA_RootTicketGate` | JOURNEY-LIVE-01a / 04 / 05 | 接続済み MOCK root | Order window は LiveManual のみ可視・unconnected で SetInteractable(false)→connected で true・ManualInstrument 解決（空→hint / Universe[0] / footer 優先）・空 universe で refuse（lane 未到達） |
| `PhaseConnectAndModeGate` | JOURNEY-LIVE-01b / 02 / 03 | secret-mock lanes | 未接続 `set_execution_mode("LiveManual")`→success=False（precondition 拒否）→`venue_login`→poll が CONNECTED＋venue_id→接続後 LiveManual 受理 |
| `PhasePlaceSecretFill` | JOURNEY-LIVE-06 / 07 / 08 / 09 | secret-mock lanes | `SubmitPlaceOrder`→write lane→`SecretRequired`(second_secret) が main(GIL-free)へドレイン・in-flight 中 `!CanUserLogout`→打鍵→urgent-secret lane で submit→`BufferIsZeroed`＋wire no-leak→place が FILLED |
| `PhasePositionsAndCancel` | JOURNEY-LIVE-10 / 11 / 12 | secret-mock lanes | arm 前 `FormatPositions`=flat→`arm_account_position`＋`force_account_snapshot`→AccountEvent→production `FormatPositions` 反射に建玉行／**11-neg**: FILLED 注文の `SubmitCancelOrder`→`ORDER_NOT_CANCELABLE`（終端は取消不可）→`arm_order("ACCEPTED")`＋write lane place（full secret）で **resting 注文**を作り ACCEPTED を実証→`SubmitCancelOrder`→CANCELED（mock 受付=確定） |
| `PhaseSerializationLogoutGateAndLogout` | JOURNEY-LIVE-15 / 13 | secret-mock lanes | 直列化（2 write・#2 は #1 解決まで未 prompt）／logout-gate（1 write in-flight で `RequestLogout` defer→drain で `ConsumePendingLogout` promote）→`StopAndJoin`→`venue_logout`→`get_state_json` が DISCONNECTED＋venue_id null |

JOURNEY-LIVE-14（実 venue 実約定）は HITL専用＝section 無し・台本のまま。

## 設計判断

- **二基層に分ける（root と lanes 直駆動）**: production root の `host.InitializePython` は `InprocLiveServer(de, venue)`
  ＝`MockVenueAdapter` を built するが、本番 `MockVenueAdapter` は `SecretRequired` を出さない（注入 seam が無い）。
  よって secret 縫い目は `build_secret_mock_server`（throwaway `SecretMockAdapter` 注入・findings 0012）で lanes を
  直駆動する別基層に置く（`VenueLoginSecretProbe` 同型）。Order ticket 表示/操作可〔04〕と ManualInstrument〔05〕は
  root logic（`DriveOrderTicket`/`OnManualPlace`）なので接続済み MOCK root の別 section で観測する（OrderTicket
  SectionD 同型）。各 surface の単体挙動の正本は既存 runner（OrderTicket / SecretModal / FooterMode / MenuBar）＝
  移送せず横断縫い目だけを観測する。
- **GIL 規律（host を流用しないので probe と異なる）**: SectionA の `host.InitializePython` が `PythonEngine.Initialize`
  ＋`BeginAllowThreads` を済ませ（main GIL-free・interpreter alive）、`host.Stop()` 後も interpreter は生かす
  （`WorkspaceEngineHost.Stop` は決して `Shutdown` しない）。SectionB-E は GIL-free な main を引き継ぐので、
  `BuildSecretMockServer` と全 `_server` 直呼び（`set_execution_mode`/`venue_login`/`force_account_snapshot`/
  teardown）を `using(Py.GIL())` で包む（host の各メソッドと同型）。`BeginAllowThreads`/`EndAllowThreads`/`Shutdown`
  は呼ばない（process は `EditorApplication.Exit` で終了）。lanes は内部で各自 `Py.GIL()` を取り main の状態に依存しない。
- **step 10 は `get_portfolio_json` でなく `force_account_snapshot`→AccountEvent→`FormatPositions`**（台本の当初
  「要確認」を解決）: `get_portfolio_json` は `BackendService` で `engine.last_portfolio is None` のとき `""` を返す＝
  **Replay 専用チャネル**（#65・`LiveRpcLanes` の poll も `execution_mode=="Replay"` のときだけ呼ぶ）で、live manual
  では常に空。live の Positions ソースは AccountEvent push（`AccountSync` の force/interval fetch）→ sink →
  `LivePanelViewModel.LatestAccount.Positions` → production `FormatPositions`。`MockVenueAdapter` は fill から建玉を
  導出しない（`submit_order` は `OrderResult` を返すだけ・`fetch_account` は `set_account_snapshot` で armed した
  snapshot を返す）ので、throwaway helper `arm_account_position`（`secret_mock.py`）で建玉を仕込み
  `force_account_snapshot()`（`AccountSync.force_resync`・dedup 貫通）で AccountEvent を push、production
  `FormatPositions(_vm)` を反射 invoke して建玉行を assert する。**causal な fill→建玉 bookkeeping は実 venue 責務
  ＝HITL**（JOURNEY-LIVE-14）。本 section が observe するのは AccountEvent→Positions タイルの decode/push 縫い目。

## 非空虚化（vacuity litmus）

- **03 モードゲートは positive を gate に含めて negative を非空虚化**: 未接続拒否（success=False）だけだと機構が死んでいても
  通る。接続後の LiveManual 受理（success=True）を同一サーバで実証してから未接続拒否を assert する。
- **04 interactable は false→true の弧**: unconnected で SetInteractable(false) を先に実証してから、login→CONNECTED 後の
  true を assert（接続が effect を持つことの証明）。05 refuse は接続済み host でのみ非空虚（未接続だと connect ゲートが
  先に弾く・OrderTicket §設計判断と同じ）＝refuse 後に `_manualOrderId` が空のままで lane 未到達を確認。
- **10 Positions は arm 前 flat を先に実証**: `force_account_snapshot` の前に `FormatPositions`=`(flat / no account
  snapshot)` を assert してから、arm＋force 後に建玉行が出ることを assert（AccountEvent push 経路が動いている証明）。
- **15 は直列化と logout-gate を分離**: 合体すると #2 の `BeginWrite` が #1 drain 直後の `ConsumePendingLogout` と
  race する（write count が 0 を経由しない）。直列化は 2 write で #2 未 prompt を、logout-gate は単一 write の
  defer→drain→promote を別々に観測する。drain は secret 提出（fast）で行い、`VenueLoginSecretProbe` の SECRET_TIMEOUT
  phase は本 Journey に持ち込まない（AFK 短縮・flush race 低減・SECRET_TIMEOUT は probe が据え置きで正本）。
- **no-leak**: 既知マーカー `9753-secret` を全ドレイン wire 文字列で検査し（`_leaked`）、modal `BufferIsZeroed` を submit
  毎に assert（`VenueLoginSecretProbe` の厳格 assert を踏襲・緩めない）。

## AFK RED→GREEN（11/12 resting 注文 cancel）

初版は step 9（resting 取消）を **FILLED 注文の id**（`PhasePlaceSecretFill` が残す `_placedOid`）で cancel
していたため、AFK で `[E2E LIVE MANUAL TRADE FAIL] JOURNEY-LIVE-11: cancel not Success: ORDER_NOT_CANCELABLE
| JOURNEY-LIVE-12: cancel not CANCELED` が出た（02/03/06-09 は通過）。

- **原因**: `ManualOrderFacade.cancel`（`python/engine/live/order_facade.py`）は `is_terminal(prior_state.status)`
  なら adapter に届く前に `OrderFacadeError("ORDER_NOT_CANCELABLE")` を raise する。`_TERMINAL_STATUSES`
  （`order_types.py`）に `FILLED` が含まれるため約定済み注文は構造的に取消不可。台本 step 9 は **resting
  （未約定＝非終端）注文**の取消であり、FILLED 注文を cancel するのは台本違反（そもそも成立しない）。
- **修正（C# のみ・Python production / mock とも無改変）**: mock の既存 throwaway helper
  `arm_order(server, status, filled_qty, avg_price)`（`secret_mock.py`・`set_next_order_outcome` ラッパ）で
  次の発注結果に非終端 `"ACCEPTED"`（`VALID_ORDER_STATUSES` にあり terminal でない）を仕込み、write lane で
  place（full second-secret flow）して **resting 注文**を作る。place 結果が `ACCEPTED`（open/cancelable）で
  あることを先に実証してから（vacuity guard）`SubmitCancelOrder`→`CANCELED` を assert する。さらに 11-neg
  として `_placedOid`（FILLED）の cancel が `ORDER_NOT_CANCELABLE` で refuse されることを assert し、終端＝取消不可
  と非終端＝取消可の対比を非空虚化する。cancel は `SecretMockAdapter` が override しない（submit_order のみ
  secret 必要）ので secret 不要で素通りし、mock 既定の `CANCELED`（受付＝確定）を返す。

## delete-the-production-logic litmus（RED 化の正確な編集箇所）

- `python/engine/mode_manager.py` `ModeManager.set_execution_mode` の precondition（`venue_state not in
  _LIVE_OK_VENUE_STATES` で `EXECUTION_MODE_PRECONDITION` を raise）を no-op 化 → **03 FAIL**（未接続でも LiveManual を受理）。
- `Assets/Scripts/Live/LiveRpcLanes.cs` の write lane 単一スレッド消費（`WriteLane`/`EnqueueWrite`）を並行化 → **15 直列化 FAIL**（#2 が #1 解決前に prompt）。
- `Assets/Scripts/Live/SecretModalController.cs` `Submit()` の buffer zeroize（`Array.Clear`）を削除 → **08 FAIL**（`BufferIsZeroed` false）。urgent-secret lane の独立を壊し wire に plaintext が乗れば `_leaked` で **08 FAIL**。
- `Assets/Scripts/Live/LiveLogoutCoordinator.cs` `RequestLogout` が in-flight write 中に true を返す → **15 logout-gate FAIL**（defer しない）。
- `force_account_snapshot`／`AccountSync` の push を no-op 化（または `arm_account_position` を外す）→ **10 FAIL**（建玉が Positions タイルに出ない）。

## HITL に残す範囲（JOURNEY-LIVE-14・理由併記）

- 実 venue 接続（kabu / 立花）・実第二暗証・実 fill・実 depth・実建玉（外部認証/秘密情報/実約定依存）。MEMORY
  「Live venue HITL recipe」（`LIVE_VENUE` env・場中・price band）と立花/kabusapi スキルの起動設定を参照。
- ack-then-poll venue の非同期取消確定（実 kabu の `PENDING_CANCEL`→poll→`CANCELED`）。mock は受付=確定なので
  JOURNEY-LIVE-12 の **非同期確定**は HITL（本 Runner は mock 即 CANCELED まで）。
- secret modal の実 uGUI 打鍵 / 25s 実タイマー・Order ticket / Positions タイルの実ピクセル描画は HITL
  （本 Runner はデータ層 = `FormatPositions` 文字列 / `SetInteractable` フラグ / pure-logic controller で観測）。

## 検証（2026-06-20 lead 実走・確定）

- compile-only: `error CS\d+` **0 件**・`Exiting batchmode successfully` / return code 0・新 `.meta` 生成。
- AFK GREEN: `-executeMethod LiveManualTradeJourneyE2ERunner.Run` で `[E2E LIVE MANUAL TRADE PASS] mock venue connected
  → LiveManual gated on connection → order placed on the write lane → second-secret submitted on the urgent lane →
  mock FILLED → position surfaced in the Positions tile → resting order cancelled → logout converged to DISCONNECTED
  (3 lanes, main GIL-free, no plaintext leak).` を bash `grep -a`（`→` 含むため Select-String/ripgrep 不可）で
  **1 件確認**・FAIL 0 件・中間 MARK（`secret-mock lanes started; main GIL-free` / `disconnected LiveManual reject
  code=EXECUTION_MODE_PRECONDITION`）あり・sentinel（`Found no leaked weakptrs` / Package Manager shutdown）あり＝
  executeMethod 実走（secret-mock lane/fill/cancel/logout 全縫い目成功）・exit 0。
- 初走 RED→GREEN（JOURNEY-LIVE-11/12）: FILLED 注文を cancel して `ORDER_NOT_CANCELABLE`（`ManualOrderFacade.cancel` が
  terminal status を adapter 到達前に raise）→ resting 注文（`arm_order("ACCEPTED")` で open）を別に作って cancel→`CANCELED`
  へ修正。11-neg（filled cancel refuse）を vacuity anchor として残置。production/mock 無改変・C# runner のみ修正。
- RED litmus は lead 任意（vacuity は各 phase の positive 先行実証＋11-neg anchor で担保）。
