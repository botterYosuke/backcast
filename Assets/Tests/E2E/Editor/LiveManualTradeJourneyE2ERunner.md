# LiveManualTradeJourneyE2ERunner — 台本（Journey E2E 仕様 / 観測点 / 合格条件）

`LiveManualTradeJourneyE2ERunner.cs`（第二波で実装）が自動検証する **Journey E2E** の台本。実装者は `.cs` と本 `.md`
をセットで読む。これは調査メモではなく、**横断ストーリーの仕様・観測点・合格条件を定義する正本**。Action ID 採番・
カバー状態の語彙・責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は
[ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Journey E2E*。venue 接続→secret→LiveManual 切替→発注→約定→建玉表示→resting
> 取消という **手動実取引フローの横断縫い目**を観測する。**大半が実 venue / 実暗証 / 実約定に依存し HITL**だが、
> mock venue で自動化できる縫い目（接続 ACK・モードゲート・発注 RPC 受理・mock fill・secret roundtrip・Positions
> 復号・取消受付・logout 収束）を厳密に切り分ける。venue メニュー単体の挙動は [MenuBarE2ERunner](./MenuBarE2ERunner.md)
> MENU-11〜14 を参照。

## 対象ストーリー（10 ステップ）

1. アプリ起動（Unity）＋ `InitializePython(<venue>)` で live-configured server を build
2. Venue→Connect（接続 ACK→`venue_state` が `CONNECTED` へ・badge 更新）
3. footer mode を LiveManual へ切替える（precondition: 接続中のみ受理）
4. Order ticket が表示・操作可能になる（LiveManual ∧ ServerReady ∧ Connected）
5. 発注する（BUY/SELL・MARKET/LIMIT・qty/price）→ order-write lane へ
6. 第二暗証要求（tachibana second secret）→ secret modal 入力→ urgent-secret lane で submit
7. 約定する（FILLED）
8. Positions タイルが更新される（建玉表示）
9. 正常終了時に resting（未約定）注文を取り消す（取消受付→poll で取消確定）
10. logout（`venue_state` が `DISCONNECTED` へ収束・venue_id クリア）

## アーキテクチャ前提

- **C#↔Python は pythonnet（同一プロセス直接呼び出し）**。発注は `WorkspaceEngineHost.Lanes`（`LiveRpcLanes`・
  3 物理レーン）経由で、**main は GIL-FREE**（`BeginAllowThreads`）のままレーンが `place_order`/`submit_secret`/
  `get_state_json` を別スレッドで回す（`VenueLoginSecretProbe` と同型）。
- **ExecutionMode**（CONTEXT.md「ExecutionMode」）: 口語「Live」＝`LiveManual`（実発注・手動）。footer が mode picker を
  所有し（File 操作の副作用ではない）、`SetExecutionMode` は venue **未接続だと `EXECUTION_MODE_PRECONDITION` で
  拒否**される（`ModeManager` が `CONNECTED`/`SUBSCRIBED` 以外を拒否）。
- **venue 接続状態**（CONTEXT.md「venue 接続状態」）: `VenueLoggedIn/Out` push event は無い。唯一の継続 canonical は
  `get_state_json` の `venue_state`（`DISCONNECTED`/`AUTHENTICATING`/`CONNECTED`/…）＋`venue_id`。badge は poll から導出。
- **secret flow**（findings 0012 / `VenueLoginSecretProbe`）: 発注 write レーンが `place_order` 内でブロック→
  `SecretRequired` が main（GIL-free）にドレイン→`SecretModalController` に char[] で打鍵→**別の urgent-secret
  レーン**で `submit_secret`→place が FILLED 返却。plaintext は char[] のみ・submit 後 zeroize（no-leak 不変条件）。
- **取消受付 / 取消確定**（CONTEXT.md「取消受付 / 取消確定」）: ack-then-poll venue（kabu）では `cancel_order` の成立は
  **取消受付**（`PENDING_CANCEL`・注文 open のまま）で、終端 `CANCELED` は `get /orders` poll が後追いで運ぶ。mock venue は
  受付＝確定で即 `CANCELED`。
- **Positions タイル**: fill は `_host.Panel`（poll が `push_portfolio` 等を running snapshot へ）→ `FormatPositions`
  → `_positionsView`（`LivePanelTileView`）で建玉表示。**Order ticket ではなく Positions タイル**に建玉が出る
  （MEMORY「Live venue HITL recipe」）。
- mock 自動化は `VenueLoginSecretProbe` の手法を踏襲: `PythonEngine.Initialize`→`build_secret_mock_server`（throwaway
  `SecretMockAdapter`・本番 `MockVenueAdapter` は `SecretRequired` を出さない）→`LiveRpcLanes` を起こし phase 駆動。

## 操作一覧表（網羅台帳）

| Action ID | ステップ/行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 関連Surface台本 |
|---|---|---|---|---|---|---|
| JOURNEY-LIVE-01 | 起動＋server build | `BackcastWorkspaceRoot.cs:239` `_host.InitializePython(_venue)` | live-configured server build・`ServerReady` | mock server を build し ServerReady を assert | 自動(Probe有・要昇格) | [MenuBar](./MenuBarE2ERunner.md) MENU-11 |
| JOURNEY-LIVE-02 | Venue→Connect（接続 ACK） | `BackcastWorkspaceRoot.cs:473` `OnVenueConnect` / `_server.venue_login` | `venue_login` ACK success・`get_state_json.venue_state`==`CONNECTED`・badge CONNECTED | mock venue_login→state poll が CONNECTED を assert | 自動(Probe有・要昇格) | [MenuBar](./MenuBarE2ERunner.md) MENU-11 |
| JOURNEY-LIVE-03 | footer mode→LiveManual | `BackcastWorkspaceRoot.cs:1512` `_host.SetExecutionMode(req.Target)` | 接続中のみ `LiveManual` 受理（未接続は `EXECUTION_MODE_PRECONDITION` 拒否） | 接続後 set_execution_mode("LiveManual") success を assert・未接続は拒否 | 自動(Probe有・要昇格) | [FooterMode](./FooterModeE2ERunner.md) |
| JOURNEY-LIVE-04 | Order ticket 表示・操作可 | `BackcastWorkspaceRoot.cs:1139-1142` `_orderTicket.SetInteractable` | LiveManual で ticket 表示・`ServerReady ∧ Conn.IsConnected ∧ !TeardownComplete` で interactable | mode/接続を満たし ticket interactable を assert | 要新規自動化 | [OrderTicket](./OrderTicketE2ERunner.md) |
| JOURNEY-LIVE-05 | 発注対象 instrument 解決 | `BackcastWorkspaceRoot.cs:1166` `ManualInstrument()` | sidebar focus or universe[0]、空なら refuse（任意銘柄へは送らない） | universe seed 後 ManualInstrument 非空を assert・空で refuse | 要新規自動化 | [UniverseSidebar](./UniverseSidebarE2ERunner.md), [ReplayToHakoniwa](./ReplayToHakoniwaE2ERunner.md) steps 2-3 |
| JOURNEY-LIVE-06 | 発注（Place） | `BackcastWorkspaceRoot.cs:1171` `_host.Lanes.SubmitPlaceOrder` | order-write lane が place_order を受理（ServerReady ∧ Connected ∧ Lanes!=null gate 通過） | mock で SubmitPlaceOrder→callback 到達を assert | 自動(Probe有・要昇格) | [OrderTicket](./OrderTicketE2ERunner.md) |
| JOURNEY-LIVE-07 | 第二暗証要求ドレイン | `VenueLoginSecretProbe.cs:199`（`SecretRequired` drain） | write が place 内でブロック→`SecretRequired`(second_secret) が main にドレイン・logout 禁止 | mock(SecretMockAdapter) で SecretRequired を assert | 自動(Probe有・要昇格) | [SecretModal](./SecretModalE2ERunner.md) |
| JOURNEY-LIVE-08 | secret 入力→submit | `SecretModalController.cs:49` `Open`/`Submit` / `_lanes.SubmitSecret` | urgent-secret lane で submit→buffer zeroize・wire に plaintext 漏れ無し | 打鍵→Submit→BufferIsZeroed＋no-leak を assert | 自動(Probe有・要昇格) | [SecretModal](./SecretModalE2ERunner.md) |
| JOURNEY-LIVE-09 | 約定（fill） | `BackcastWorkspaceRoot.cs:1173`（`res.Success`/`res.Status`） | mock の同期 `OrderResult` が `FILLED`・`_manualOrderId` 確定 | place callback が FILLED を assert（mock fill） | 自動(Probe有・要昇格) | — |
| JOURNEY-LIVE-10 | Positions タイル更新（建玉） | `BackcastWorkspaceRoot.cs:345` `_positionsView` / `FormatPositions` | fill 後 `_host.Panel` の portfolio→Positions タイルに建玉が出る（ticket ではない） | state poll 後 FormatPositions が建玉行を含むことを assert | 要新規自動化 | — |
| JOURNEY-LIVE-11 | resting 注文の取消（受付） | `BackcastWorkspaceRoot.cs:1188` `_host.Lanes.SubmitCancelOrder` | cancel_order 受理→受付（mock は即 `CANCELED`、kabu は `PENDING_CANCEL`） | mock で SubmitCancelOrder→callback status を assert | 自動(Probe有・要昇格) | — |
| JOURNEY-LIVE-12 | 取消確定（poll 後追い） | CONTEXT「取消受付 / 取消確定」 | ack-then-poll venue で `PENDING_CANCEL`→poll→`CANCELED`（mock は受付＝確定） | mock の即 CANCELED を assert（実 kabu の poll 確定は HITL） | 自動(Probe有・要昇格) | — |
| JOURNEY-LIVE-13 | logout→DISCONNECTED 収束 | `BackcastWorkspaceRoot.cs:1721` `_host.VenueLogout` / `venue_logout` | logout 後 `venue_state`→`DISCONNECTED`・`venue_id` クリア・badge 収束 | venue_logout→state poll が DISCONNECTED を assert | 自動(Probe有・要昇格) | [MenuBar](./MenuBarE2ERunner.md) MENU-14 |
| JOURNEY-LIVE-14 | 実 venue で手動取引（実約定） | `BackcastWorkspaceRoot.cs:1171`（実 kabu/立花 経路） | 実 kabu/立花 へログイン・実第二暗証・実 fill・実建玉・実 depth | — | HITL専用（実 venue 接続・外部認証/秘密情報依存・実約定） | [MenuBar](./MenuBarE2ERunner.md) MENU-13 |
| JOURNEY-LIVE-15 | write 中 logout gate / serialization | `VenueLoginSecretProbe.cs:225,251` | 1 write lane の直列化（#2 は #1 解決まで prompt されない）・in-flight write 中 logout は defer→drain で promote | mock で serialization＋logout-gate を assert | 自動(Probe有・要昇格) | — |

## 自動検証する範囲（この Runner がゲートする・mock venue）

- **step 1-2（接続 ACK）**: mock server build→`venue_login` ACK→`get_state_json.venue_state` が `CONNECTED` へ収束
  （badge canonical）。
- **step 3（モードゲート）**: 接続中のみ `SetExecutionMode("LiveManual")` 受理、**未接続は `EXECUTION_MODE_PRECONDITION`
  拒否**（precondition 不変条件）。
- **steps 5-9（発注 RPC→secret→mock fill・縫い目）**: `SubmitPlaceOrder` が write lane に乗り→`SecretRequired` が
  main にドレイン→`SecretModalController` 経由 urgent-secret lane で `submit_secret`→place が `FILLED`。**plaintext
  no-leak / char[] zeroize / write lane 直列化 / in-flight write 中の logout defer** も検証（`VenueLoginSecretProbe`
  の 3 phase を Journey の文脈で縫う）。
- **step 10（Positions 復号・縫い目）**: fill 後 `get_state_json` の portfolio が `_host.Panel`→`FormatPositions`→
  Positions タイルに建玉として届く（**要確認**: mock fill が portfolio/positions を state JSON に載せるか。載らない
  場合は step 10 を HITL へ降格し理由併記）。
- **steps 11-13（取消＋logout）**: `SubmitCancelOrder` 受理→mock 即 `CANCELED`（受付＝確定）、`venue_logout`→
  `DISCONNECTED` 収束・`venue_id` クリア。

## 自動検証しない範囲（HITL に残す範囲・理由併記）

- **実 venue 接続（kabu / 立花）**（JOURNEY-LIVE-14）: 実ログイン・仮想 URL・セッション当日有効/生存・実 fill・実 depth。
  → **HITL専用**（実 venue 接続・外部認証/秘密情報依存・実約定）。MEMORY「Live venue HITL recipe」（`LIVE_VENUE` env・
  場中・price band）と立花スキルの起動設定を参照。
- **実第二暗証の入力**（実 kabu/立花 の second password）。本 Runner は throwaway `SecretMockAdapter` の `SecretRequired`
  を mock 値で満たす。実暗証は → **HITL専用**（外部認証・秘密情報依存）。
- **ack-then-poll venue の非同期取消確定**（実 kabu の `PENDING_CANCEL`→poll→`CANCELED` 配線）。**#25 が実装したのは
  broker 側の honoring のみ**で、実 kabu の返し分け＋poll 配線は **#23**（CONTEXT.md「取消受付 / 取消確定」）。mock は
  受付＝確定なので step 12 の**非同期確定**は → **HITL専用**（実 venue の poll 後追い依存）。
- **Order ticket / Positions タイルの実ピクセル描画**（建玉行のレイアウト・色）。本 Runner はデータ層（`FormatPositions`
  文字列・`SetInteractable` フラグ）で観測する。実描画は → **HITL専用**（GPU・実ウィンドウ前提）。
- **secret modal の実 uGUI 打鍵 / 25s 実タイマー**（`Input.inputString` ドレイン・絶対 timeout の実時間）。本 Runner は
  `SecretModalController`（time 注入の pure logic）を駆動する。実打鍵 UI は → **HITL専用**（実 EventSystem・実時間依存）。

## 観測点（step ごと）

| step | 観測 | 合否の意味 |
|---|---|---|
| 1 | mock server build 後 `ServerReady`（lanes 起動・main GIL-free） | server 構築済 |
| 2 | `venue_login` ACK success ∧ poll の `venue_state`==`CONNECTED` ∧ `venue_id` 載る | 接続成立（badge canonical） |
| 3 | 接続中 `set_execution_mode("LiveManual")` success ∧ 未接続では precondition 拒否 | モードゲート成立 |
| 4 | LiveManual で ticket 表示 ∧ `ServerReady ∧ Connected ∧ !TeardownComplete` で interactable | 発注 UI が開く |
| 5 | `ManualInstrument()` 非空（seed 済 universe）∧ 空なら place を refuse | 任意銘柄へ送らない安全弁 |
| 6 | `SubmitPlaceOrder` callback 到達（write lane 受理） | 発注 RPC 受理 |
| 7 | `SecretRequired`(second_secret) が main にドレイン ∧ in-flight 中 logout 禁止 | secret 要求の縫い目 |
| 8 | Submit 後 `BufferIsZeroed()` ∧ 全 wire に plaintext marker 不在 | no-leak / zeroize |
| 9 | place callback の `Status`==`FILLED` ∧ `Success` | mock 約定 |
| 10 | fill 後 `FormatPositions(snap)` が建玉行を含む（Positions タイル・ticket ではない） | 建玉が Positions へ届く |
| 11-12 | `SubmitCancelOrder` callback の status==`CANCELED`（mock 受付＝確定） | 取消受付（mock 即確定） |
| 13 | `venue_logout` 後 poll の `venue_state`==`DISCONNECTED` ∧ `venue_id`==null | 切断収束 |
| 15 | place #2 は #1 解決まで未 prompt ∧ in-flight write 中 `RequestLogout` defer→drain で promote | 直列化 / logout-gate |

> **delete-the-production-logic litmus**: `SubmitPlaceOrder` の write-lane 直列化を消すと step 15 が落ち、secret の
> urgent-lane 分離を消すと step 8（zeroize/no-leak）か直列化が落ちる。mode precondition ガードを消すと step 3 の
> 未接続拒否が落ちる（未接続でも LiveManual を受理してしまう）。

## 合格条件

- ログに `[E2E LIVE MANUAL TRADE PASS] mock venue connected → LiveManual gated on connection → order placed on the write lane → second-secret submitted on the urgent lane → mock FILLED → position surfaced in the Positions tile → resting order cancelled → logout converged to DISCONNECTED (3 lanes, main GIL-free, no plaintext leak).`
- プロセス exit code 0（`-quit` 併用、self-failing gate）。`error CS\d+` が 0 件。
- いずれかの観測点を落としたら `[E2E LIVE MANUAL TRADE FAIL] <msg>` で exit 1。

## 実行コマンド

```text
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod LiveManualTradeJourneyE2ERunner.Run -logFile <log>
```

このマシンの Unity: `C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe`。
compile だけ先に通すゲート: `-executeMethod` を外して同コマンド（`error CS\d+` 0 件＋`return code 0`）。
Unity ログは UTF-8 なので **ripgrep で grep**（PowerShell `Select-String` は取りこぼす）。

## 失敗時に確認するログ・代表的な原因

- **`venue_state never reached CONNECTED` / step 2 落ち**: mock `venue_login` 失敗、または poll が回っていない
  （lanes 未起動・main が GIL を握ったまま）。`VenueLoginSecretProbe` の `BuildServer`／`_lanes.Start` と突合。
- **`LiveManual accepted while disconnected` / step 3 落ち**: precondition ガード漏れ。`ModeManager` の
  `EXECUTION_MODE_PRECONDITION`（CONNECTED/SUBSCRIBED 以外を拒否）を確認。
- **`SecretRequired never drained` / step 7 落ち**: throwaway `SecretMockAdapter` が注入されていない（本番
  `MockVenueAdapter` は `SecretRequired` を出さない）。`build_secret_mock_server` を使っているか確認。
- **`plaintext secret leaked` / `buffer not zeroized` / step 8 落ち**: `SecretModalController.Submit` の `Array.Clear`
  漏れ、または wire に secret marker が乗った（urgent-secret lane が独立しているか・`VenueLoginSecretProbe` の no-leak
  ロジックと突合）。
- **`mock did not FILL` / step 9 落ち**: mock の `submit_order` 既定 outcome（`VenueLoginSecretProbe` は既定 FILLED）。
  arm が要る venue mock を使っていないか確認。
- **`position not in Positions tile` / step 10 落ち**: fill が portfolio/positions を `get_state_json` に載せていない、
  または `FormatPositions` の経路。→ **要確認**（mock fill が建玉を state に反映するか。反映しないなら step 10 を HITL へ
  降格し台本の自動判定を「ticket callback の FILLED まで」に絞る）。
- **`venue_state not DISCONNECTED after logout` / step 13 落ち**: `venue_logout` 後の poll 収束待ち不足、または
  `venue_id` が stale。`VenueLoginSecretProbe.PhaseAuditAndTeardown` の収束 assert と突合。
- **segfault / GIL stall / crash dump**（`%LOCALAPPDATA%\CrashDumps\Unity.exe.*.dmp`）: lanes の join 規律
  （`StopAndJoin`）と `EndAllowThreads`/`Shutdown` の順序（`VenueLoginSecretProbe` の finally と同型）を確認。

## 将来の `LiveManualTradeJourneyE2ERunner.cs` 実装方針

- **mock 経路は `VenueLoginSecretProbe` を昇格元**にする（同型: `PythonEngine.Initialize`→`build_secret_mock_server`
  →`BeginAllowThreads`→`LiveRpcLanes.Start`→phase 駆動→`StopAndJoin`→`venue_logout`→`get_state_json` 収束→`Shutdown`）。
  本 Journey はそれを「接続→LiveManual ゲート→place→secret→fill→Positions→cancel→logout」の**一本のストーリー**に
  並べ替え、各遷移を 1 観測点として `Execute()`（null=PASS）で連ねる。
- **root 経由の縫い目**（mode ゲート・ManualInstrument・Order ticket interactable・Positions 描画）は、可能なら実
  `BackcastWorkspaceRoot` を反射合成し（`ReplayToHakoniwaE2ERunner` 同型）`host.InitializePython(<venue>)` 直呼び＋
  `OnFooterMode`/`OnManualPlace`/`OnManualCancel`/`OnVenueDisconnect` を反射 invoke して観測する。**要確認**: root 合成と
  lanes 直駆動のどちらで Positions/ticket を観測するか（root 経由なら `Update()` を pump して poll を回す必要がある）。
- secret は `SecretMockAdapter`（throwaway・`SecretRequired` を出す）を注入。実 venue / 実暗証 / 非同期取消確定は
  本 Runner では**触れない**（HITL に残す・上記「自動検証しない範囲」）。
- teardown は lanes `StopAndJoin`→`venue_logout`→`PythonEngine.Shutdown`（or `host.Stop()`）。crash dump 回避のため
  in-flight write を残さない（全 lane idle を確認してから join）。
- 実行コマンドは上記。compile-only ゲートは `-executeMethod` を外した同コマンド。
