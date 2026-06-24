# findings 0012 — Venue login and secret flow（#21・grill 確定）

方針: **ADR-0001 decision 8**（C#↔Python は単一 adapter 層・C# 製 sink を engine の sink 口へ差す）。
live セッション所有は **ADR-0004 案 C** の pure-Python `KernelLiveEngineController`（#25・findings 0011）。
本書は #21 スライスの下位確定事実を記録し、ADR は「方針」として参照する（ADR は自己保護のため編集しない）。

#20 **Live adapter tracer**（findings 0011-live-adapter-tracer・mock venue で C# が live loop を所有し
backend_events を GIL-free drain）の上に、**venue 接続 UI・login・第二暗証番号 secret フロー**を載せる。
TTWR(Bevy) の venue メニュー相当を Unity で再構築する縦スライス。

## ゴール（issue #21 + 方針更新コメント ADR-0004 案 C 反映）

- kabu Verify（検証）/ tachibana demo への接続・切断メニューを Unity から実行できる。
- tachibana 第二暗証番号を **発注 RPC の内側**で `SecretRequired` push → `submit_secret` →
  `SecondSecretResolver`（wait 30s）で都度収集。order write timeout（40s）は read/account（5s）と分離。
  PLACE 中の secret 入力で orphan order を生まない。
- secret は env / log / 平文ファイルに残さない（SecretVault 経路に一本化）。
- 接続状態が UI と engine state で整合（`get_state_json` が継続 canonical）。

## 前提（grill で裏取りした既存資産）

Python の login/secret/timeout 基盤は **TTWR(Rust frontend)向けに実装済み**で本スライスでは原則無変更:

- RPC surface: `InprocLiveServer.venue_login / venue_logout / submit_secret / place_order(second_secret=...)`
  （`inproc_server.py`）。
- secret 経路: `TachibanaAdapter.set_execution_hooks(secret_resolver=...)` →
  `_get_second_password` → `resolve()` → `SecondSecretResolver`（`secret_provider.py`）→
  `SecretVault`（`secret_vault.py`・TTL 60s・平文は `_store` のみ・`__repr__` 非開示）→ `submit_secret`。
- timeout 分離: `live_orchestrator.py` `_live_timeout_s=5.0`（read/account/subscribe/get_orders）/
  `_order_timeout_s=40.0`（place/cancel/modify）/ secret wait 30s（resolver 既定）/ login は別
  `_live_login_timeout_s()`。
- log 非保持: `live/logging.py` が `second_secret`/`token` を mask。
- kabu は第二暗証番号を使わない（`kabusapi_execution.py` が `secret_resolver` を受理して無視・R3）。

## 確定事実（grill 2026-06-14）

### D1. スライス scope = C# frontend/control + verification（production Python 原則無変更）

- 「UI-only」ではなく **C# frontend/control**: 画面に加え RPC worker / event drain / state poll / timeout /
  lifecycle 所有を含む。net-new は全て C#（venue メニュー・secret modal・接続 badge・3-lane RPC 制御）＋
  AFK probe / HITL harness / throwaway Python helper。
- production Python 変更は**実欠落が AFK で実証された場合のみ**足し、本書に追記する（既定は無変更）。
- 権威経路は `place_order(second_secret=...)` 引数を使わない。Unity は常に `second_secret=null` で発注し、
  `SecretRequired→submit_secret→SecondSecretResolver` だけを使う（`second_secret` 引数は facade で意図的に終端）。

### D2. 並行モデル = 物理 3 lane。単一 worker + 非同期キューは不可

- `place_order` RPC は内部で secret 30s を待つため最大 40s+α ブロックする。`future.result(timeout)` の wait 中は
  `concurrent.futures.Future.result()` が GIL を解放するので、別 C# スレッドが GIL を取り `submit_secret` を呼べる。
- **lane（物理スレッド分離）**:
  1. **Order-write lane** — place/cancel/modify。最大 40s+α。同時 write は直列化。
  2. **Urgent secret lane** — `submit_secret` 専用。write lane と必ず別スレッド。poll/logout/通常 control の
     後ろに並ばせない。
  3. **Poll lane** — `get_state_json`。#20 と同じ専用 poll worker。secret lane と分離。
- 単一 worker + 非同期キューは不可: 同一 consumer が `place_order` 内にいる間 `submit_secret` を dequeue できず
  デッドロックする。
- `venue_logout` は **lane ではなく lifecycle coordination 対象**（D7）。urgent secret / poll lane に混載しない。
- main thread は全工程 GIL 非取得（#20 D4 / S2-spike 規律を継承）。teardown 前に 3 lane worker を全て join。

### D3. ゲート = AFK 権威（実装品質）＋ 2 HITL（実 venue 受け入れ）の 3 層

1. **AFK headless 権威ゲート**（`VenueLoginSecretProbe.cs`・Mac/CI 再現可・実資格情報なし）。
   UI/RPC seam・3 lane・secret 往復・接続状態遷移・timeout 分離・平文非漏洩を自動検証。
   **production `MockVenueAdapter` は `secret_resolver` を no-op 受理し `SecretRequired` を出さない**ため、
   throwaway の **Tachibana-like adapter/helper**（`python/spike/`）を注入して secret 往復を発生させる:
   ```
   write lane: place_order(null secret) → adapter.submit 内で resolver.resolve() → SecretRequired push
   main:       GIL-free drain → modal state
   secret lane: submit_secret(request_id, secret) → write lane 復帰 → adapter が secret 受領 → FILLED
   ```
   assert: SecretRequired 全 field が C# decoder/view-model に到達 / submit_secret が write 完了前に別 thread から
   成功 / order RPC が SECRET_TIMEOUT・PLACE_TIMEOUT にならない / adapter は期待 secret を受領するが log・state・
   probe 出力・C# `char[]` バッファに平文がない（ゼロ化済み）/ main 全工程 GIL-free / teardown 前に 3 worker join。
   production の実 venue 通信成功までは証明しない。
2. **Tachibana demo HITL**（cross-platform・owner 手動・default-disabled harness）。実資格情報で
   connect → order → SecretRequired → modal → submit → fill → logout。実施結果を日時・OS・Unity/Python 版・
   結果とともに本書に記録。未実施なら AC①②は「自動 seam GREEN・実 demo 未確認」と明記。
3. **kabu Verify HITL**（Windows + kabuステーション本体起動が前提・owner 手動）。connect → order → fill → logout。
   **kabu では `SecretRequired` が一度も発生しないことも確認**。Mac は「skip」ではなく
   **platform-inapplicable**、Windows leg 未実施 と記録。
- **issue クローズには原則 Tachibana demo + Windows kabu Verify 両 HITL 結果が必要**。AFK GREEN だけで実接続 AC を
  完了扱いにしない。

### D4. login UI 所有 = Python `login_dialog_runner` tkinter subprocess（両 venue prompt）

> ⚠️ **SUPERSEDED by #122 / [findings 0093](0093-issue122-inproc-tkinter-login-no-subprocess.md)**:
> subprocess（`login_dialog_runner`・NDJSON・cred-path）は撤去し、tkinter ダイアログを
> **in-process（専用スレッド）**で実行する形に単純化した。以下の D4 本文は当時の確定事実として保存
> （0012 は point-in-time findings ＝本文は書き換えない）。

- Unity は credential form を作らない。connect ボタンは **tachibana・kabu とも `venue_login(credentials_source=
  "prompt")`** を投げるだけ。実資格入力は共通 `login_dialog_runner` subprocess の venue 別 tkinter dialog が所有。
- 成功後 orchestrator 内部で tachibana→`session_cache`、kabu→`prompt_result`(token) に変換する。
  kabu adapter の `"prompt"` 未対応は「adapter 内で prompt UI を直接起動しない」境界に過ぎず、通常経路は
  `login_dialog_runner` → `kabusapi_login_flow.run_dialog()`（tkinter・`fetch_token`・cred-path 書き込み・
  token は stdout 非出力）で成立する。**kabu skill の「prompt フロー未実装」記述は現コードに対し stale →
  post-impl で訂正する**。
- `env`（`DEV_TACHIBANA_*` / `DEV_KABU_API_PASSWORD`）は headless/debug fallback・明示的開発運用に限定。
  API password を環境変数に置く運用を UI 標準にしない。

### D5. secret modal = Unity ネイティブ・keyboard-drain・25s absolute・C# が平文生存期間を完全所有

- `SecretRequired` は backend event として sink を流れる（`event_wire._secret_required_wire` =
  `request_id/venue/kind/purpose`）。`LiveBackendEventDecoder` に typed decode を追加し、`LivePanelViewModel` の
  unknown-tag 落ちを直して Unity main が modal を出す。入力後 **urgent secret lane** から `submit_secret`。
- **入力方式**: `char[]` だけでは不十分。Unity `InputField` / 表示 `Text` / IME / クリップボードに平文を**複製しない**。
  移植元同様 **keyboard drain で `char[]` へ直接入力し、表示は文字数分のマスクのみ**。`TMP_InputField` の内部 string に
  生 secret を載せない（不変 string は GC まで残留するため）。
- **生存期間は C# が能動所有**: submit 成功・キャンセル・modal クローズで即 `char[]` を明示上書きゼロ化（GC 任せにしない）。
  Python `SecretVault` TTL は「submit 済み平文を resolver/order が短時間再利用する寿命＝送信後」を管理し、
  **送信前の入力中平文は管轄外**なので C# 所有が唯一解。
- **25s absolute timeout**（移植元継承）でモーダルを閉じバッファをゼロ化。入力ごとに延長する idle timeout は backend
  30s を超過し得るため**不採用**。包含順を不変条件として固定: **25s(modal absolute) < 30s(secret wait) <
  40s(order write)**。
- `submit_secret` 送信値は引数で渡し、フィールド・ログ・view-model に保持しない。AFK probe は modal 経路通過後に
  C# state/probe 出力へ平文が出ないこと・`char[]` ゼロ化済みを assert（D3）。

### D6. 接続状態の権威 = `get_state_json` 単一 canonical（CONTEXT.md「venue 接続状態」項）

- `venue_login` ACK = login 直後の即時結果。**`get_state_json` の `venue_state`/`venue_id` = 唯一の継続 canonical**。
  UI badge は poll から導出（接続中＝`CONNECTED/SUBSCRIBED/RECONNECTING` のみ venue_id を載せ stale を防ぐ既存規律）。
- `VenueLogoutDetected` は health watchdog 由来の外部切断を知らせ**再ログインを促す通知**であって、badge を直接
  `DISCONNECTED` へ変える権威ではない。通知後も badge は poll の収束を待つ。
- `VenueLoggedIn`/`VenueLoggedOut` という push event は新設しない（存在しない）。

### D7. logout coordination = 二重防壁（UI disable + 制御層 teardown シーケンス）

- **第一防壁（UI）**: order-write in-flight、または secret modal open（=place が secret 待ち）中は
  disconnect/logout を disable。
- **第二防壁（制御層 durable シーケンス）**: UI 抑止をすり抜けても teardown が write と重ならないよう、`venue_logout` を
  以下の順で実行する:
  1. 新規 write 受付停止
  2. urgent secret 処理を停止
  3. order-write 完了 または PLACE_TIMEOUT(40s) を待つ
  4. poll lane 停止・join
  5. `venue_logout`（teardown: `runner.aclose()` + `adapter.logout()` + venue SM reset）
  6. 全 worker join 後に Python runtime teardown
- hung write は #21 では強制 cancel しない（3 で timeout 待ち）。force-disconnect / 正常終了時 best-effort cancel は
  親 **#4** に委譲（#21 では in-flight cancel を実装しない）。
- logout 戻り後に `get_state_json` が `DISCONNECTED` / `venue_id=None` へ収束することを確認。
- AFK probe: write in-flight 中の logout 要求が teardown を即時実行しない（遅延/直列化）/ lane 解放後に teardown が走り
  `DISCONNECTED`・`venue_id=None` に収束 / logout 経路が secret・poll lane と独立、を検証。

### D8. 平文非保持の監査境界

- **AFK gate**: C# 入力バッファ（`char[]` ゼロ化）・ログ・view-model/state・probe 出力・（あれば）一時ファイルを監査し、
  既存 Python characterization test（#19）を再実行する。
- **実 HITL**: 秘密値そのものを証跡へ書かない。「検索対象ログ・state に既知のテスト用 marker が存在しない」形式で判定する。

## 実装インベントリ（durable / throwaway）

**durable（`Assets/Scripts/Live/`）**:
- `LiveBackendEventDecoder` に `SecretRequired` typed decode 追加 + `LivePanelViewModel` の unknown-tag 落ち修正。
- venue 接続状態 view-model（`get_state_json` poll から `venue_state`/`venue_id`/badge 値を保持・D6）。
- secret modal の C# state（keyboard-drain `char[]` 所有・ゼロ化・25s absolute timeout・D5）。
- **3-lane RPC 制御層**（order-write / urgent secret / poll worker + D7 の logout teardown シーケンス）。
- venue connect/disconnect メニュー UI。

**throwaway（`Assets/Editor/` + `python/spike/`）**:
- `VenueLoginSecretProbe.cs` — AFK 権威ゲート（`[VENUE LOGIN SECRET PASS]` / `... FAIL]`・self-failing・exit 0/1）。
- playmode HITL harness（tachibana demo / kabu Verify・default-disabled・owner 手動の実描画 + 実接続 leg）。
- throwaway Tachibana-like adapter/helper（`python/spike/`・`SecretRequired` を実際に発生させる注入経路。
  生産 `InprocLiveServer` API は無改修・#20 D2 と同方針）。

**production Python**: 原則無変更（D1）。

## 実装インベントリ（実ファイル・2026-06-14）

**durable（`Assets/Scripts/Live/`）**:
- `LiveBackendEventDecoder.cs`（`DecodeSecretRequired` / `DecodeVenueLogout` 追加）
- `LivePanelViewModel.cs`（`SecretRequired` / `VenueLogoutDetected` タグ処理・unknown-tag 落ち解消）
- `VenueConnectionViewModel.cs`（poll-canonical badge・login ACK・logout NOTICE 非権威）
- `SecretModalController.cs`（keyboard-drain `char[]`・マスク・25s absolute・ゼロ化）
- `LiveLogoutCoordinator.cs`（D7 二重防壁の decision core）
- `LiveRpcLanes.cs`（order-write / urgent-secret / poll の 3 物理 lane + StopAndJoin teardown）
- `VenueMenuViewModel.cs`（両 venue prompt dispatch・badge text・write 中 disconnect 無効）

**throwaway（`Assets/Editor/` + `python/spike/`）**:
- `VenueLoginSecretProbe.cs`（M4 権威 AFK ゲート）/ `VenueLoginSecretM1Probe.cs`・`SecretModalM2Probe.cs`・
  `LogoutCoordinatorM2Probe.cs`・`VenueMenuM3Probe.cs`（M1–M3 focused gate）
- `VenueLoginSecretHitlHarness.cs` + `VenueLoginSecretHitlMenu.cs`（owner 手動 playmode・default-disabled）
- `python/spike/venue_login_secret/secret_mock.py`（throwaway Tachibana-like adapter・`build_secret_mock_server`）
  + `run_secret_smoke.py`（CPython 疎通）

**production Python / C#（既存）**: 無変更（D1 どおり）。

## スコープ境界（#21 が含まないもの・owner 合意 2026-06-14 案 A）

#21 は **durable control 層（view-model/lane/coordinator/modal）＋ AFK 権威 seam ＋ owner 手動 HITL harness**
を提供する。**通常起動の Unity app から触れる production Live panel（venue メニュー／secret modal／badge の
本番 UI）は #21 のスコープ外**。理由: backcast にまだ production Live panel が存在せず、その配置（venue メニューを
infinite-canvas / hakoniwa / floating-window のどこに置くか）は独立した設計判断であり、別スライスで grill すべき。
AC① の「Unity から接続・切断」は #21 では **HITL harness を受け入れ vehicle** として満たし（実 demo GREEN）、
production UI 結線は **#23（Live demo roundtrip done-gate）に統合**する（当初 #28 に切り出したが
#23 の AC「通常起動 Unity から発注→約定→建玉反映」と重複するため #28 は close・#23 に畳んだ）。
durable 型 `VenueConnectionViewModel`/`SecretModalController`/`LiveRpcLanes`/`VenueMenuViewModel` を
そのまま再利用する前提。

## ゲート / 再走

### AFK 権威ゲート（Mac leg・2026-06-14・GREEN）

`<Unity> -batchmode -nographics -quit -projectPath . -executeMethod <Probe>.Run`（Unity 6000.4.11f1）:

- **M4 権威**: `VenueLoginSecretProbe.Run` → `[VENUE LOGIN SECRET PASS] secret roundtrip /
  SECRET_TIMEOUT / serialization / logout-gate / no-leak — 3 lanes, main GIL-free (maxStall=9ms)` exit 0。
  実 Mono+pythonnet で 3 lane・secret 往復・**SECRET_TIMEOUT（secret 未提出）**・**同一 write lane の直列化**・
  logout 調停・平文非漏洩を一気通貫検証。main maxStall=9ms（< 200ms＝GIL-free）。
- **M1–M3 focused**（pure-C#）: `VenueLoginSecretM1Probe` / `SecretModalM2Probe` /
  `LogoutCoordinatorM2Probe` / `VenueMenuM3Probe` いずれも exit 0・対応 `[... PASS]`。
- **CPython 疎通**: `cd python && .venv/bin/python -m spike.venue_login_secret.run_secret_smoke`
  → `[VENUE LOGIN SECRET CPYTHON PASS]`（SUCCESS / SECRET_TIMEOUT=6.0s orphan-free / SERIALIZED / NO_PLAINTEXT）。
- CS エラー 0・新規 crash dump 無し。

### 実 venue 受け入れゲート（HITL・owner 手動）

issue クローズには原則この 2 leg の実施結果が必要（AFK GREEN だけでは実接続 AC を完了扱いにしない・D3）。

- **Tachibana demo HITL**: ☑ **実施・secret seam GREEN**（2026-06-14・macOS 13.7.8 / Darwin 22.6.0・
  Unity 6000.4.11f1・Python 3.13・venue=TACHIBANA demo）。`Tools > Backcast > Venue Login Secret HITL
  (Tachibana demo)` で **connect(tkinter prompt) → Connected → Place → `SecretRequired` → Unity ネイティブ
  secret modal（char-drain・マスク表示）→ 第二暗証番号 → Submit → `submit_secret` → 発注 success
  status=ACCEPTED → Disconnect → badge Disconnected・ボタン無効化** を確認。
  - **FILL は未確認**（実施日が週末＝閉局帯。EVENT WS `p_errno=2` reconnect ループ・MARKET 注文は約定せず
    ACCEPTED 受理止まり）。**FILL 観測は平日 JST 取引時間に持ち越し**。
  - **非漏洩**: secret はモーダルでドットマスク表示のみ・status/last-order/Console に平文なし（秘密値は本欄にも不記載）。
  - 本実施で発見し修正した実バグ: ① mode を login 後設定（pre-login は EXECUTION_MODE_PRECONDITION）、
    ② Disconnect 後の poll 停止で badge が Connected 固着＋完了キューへの enqueue で crash
    （`LANES_STOPPED` graceful 化 + 最終 poll で badge 収束 + teardown 後ボタン無効化）。
- **kabu Verify HITL**: ☐ 未実施（**Mac は platform-inapplicable** — kabuステーション本体 Windows-only）。
  Windows leg で `Tools > Backcast > Venue Login Secret HITL (Kabu verify)`。connect → place → fill → logout。
  **kabu では `SecretRequired` が一度も出ないことも確認**。

### docs/skill 整合（post-impl）

- kabu skill の「prompt フロー未実装（残課題）」は現コードに対し **stale**（`kabusapi_login_flow.run_dialog`
  に tkinter form + `fetch_token` + cred-path 書き込みが実在）。post-impl で訂正する（D4）。

- behavior gate: backcast に FLOWS.md は無く、本 findings + AFK probe + owner HITL leg が等価物。
