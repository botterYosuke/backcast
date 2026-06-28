# 0131 — Venue ログイン uGUI モーダル化 / Python headless 認証（#181 実装の設計の木）

**Issue**: #181 / **Status**: 設計凍結（grill-with-docs 2026-06-28 owner HITL）→ 実装中
**方針**: [ADR-0040](../adr/0040-venue-login-ugui-modal-tkinter-abolished.md)（venue login orchestration 反転・tkinter 廃止）
**前提**: findings 0130（macOS off-main Tk crash 真因）・findings 0093（#122 in-proc tkinter＝本 finding で置換）・findings 0107（#133-135 Tcl_AsyncDelete＝crash クラス消滅で回帰廃止）・ADR-0023/0027/0033（認証本体・prod-gate・prefill は不変で再利用）

> **番号注記**: issue #181 本文・findings 0130 は新 finding 番号を名指していない。`ls docs/findings/ | sort` の次空き番号 0131 で採番した（0126 は account-summary-bar と macos-shutdown-segfault の 2 ファイルが既に消費・0130 が venue-login 真因）。

## 1. 何を作るか（オーケストレーションの反転）

旧: C# トリガ → **Python が tkinter GUI を開く**（`_handle_prompt_login` → `run_dialog` → `tkinter.Tk()`）。
新: **C# が uGUI モーダルを持ち入力を集める** → **Python は headless 認証だけ**。認証本体（`*_auth.py` /
`*_login_form_state.py` / `tachibana_login_messages` / `tachibana_file_store`）は無改変で再利用。

## 2. Python RPC surface（D1 の正本）

`venue_login` の `"prompt"` 分岐・`_handle_prompt_login` / `_try_create_tk` を撤去。モーダルが駆動する RPC 3 本を
`LiveLoopManager`（→ `DataEngineBackend` で wrap → `InprocLiveServer.InvokeMethod`）に新設する。

### 2.1 `venue_login_form_init(venue_id, mode) -> dict`
`build_form_init(env_hint=mode)` を呼んで prefill を返す。モーダル open 時とモード切替（demo↔prod / verify↔prod）で呼ぶ。
- Tachibana: `{"initial_mode", "auth_id_prefill", "key_path_prefill"}`（ADR-0033 = debug demo のみ非空・prod/release は空）
- kabu: `{"initial_mode", "station_port", "api_password_prefill"}`（debug verify のみ非空）
- 返り値はすべて plain（パスワード prefill は debug-only の dev 利便・release は `""`）。

### 2.2 `venue_login_probe_station(venue_id, mode) -> dict`
- kabu: `{"running": probe_station(port=<mode別>), "port": <18080|18081>}`
- kabu 以外: `{"running": True, "port": 0}`（OK 有効判定を統一・Tachibana に本体概念なし）
- モーダルの「再確認」ボタン・open 時・モード切替で呼ぶ。

### 2.3 `submit_venue_login(venue_id, mode, fields_json, secret) -> dict`
1 送信で **検証 → headless 認証 →（成功時のみ）後処理配線**。**開いている間に何度でも呼べる**（失敗は閉じず再試行）。
- `fields_json`: 非秘密フィールドの JSON。Tachibana=`{"auth_id","key_path"}`、kabu=`{}`。
- `secret`: kabu API パスワード（char[] から作る使い捨て string）。Tachibana=`""`。
- 処理:
  1. `validate_submission(...)`（空欄 → `EMPTY_FIELDS`）。kabu は `probe_station` も（未起動 → `KABU_STATION_NOT_RUNNING`）。
  2. `_start_live_components` → venue_sm `AUTHENTICATING`（旧 `_attempt("prompt")` 前半と同じ）。
  3. **headless 認証**（live loop 上で `run_coroutine_threadsafe`）:
     - Tachibana: `load_private_key_from_file(key_path)` → `tachibana_auth.login(auth_id, key, is_demo=(mode=="demo"), p_no_counter=PNoCounter())` → `tachibana_file_store.save_session({...})`（旧 `_run_auth`+`_on_auth_done` の中身）。
     - kabu: `kabusapi_auth.fetch_token(secret, env=mode)` → token（in-memory）。
  4. 失敗: 例外 → 既存 `_map_exception`（各 flow にあったものを headless helper へ移設）→ error_code → `auth_failure_view(ec)`（kabu）/ `raise_for_login_error`（Tachibana）で `status_text`(日本語) と `allow_retry`。**venue_sm を ERROR/reset に戻して** `AUTHENTICATING` を残さない（再送信できる）。`{success:False, error_code, status_text, allow_retry}`。
  5. 成功: `_finalize_login(adapter_creds)` を呼ぶ。`adapter_creds` は Tachibana=`VenueCredentials(credentials_source="session_cache", environment_hint=mode)`、kabu=`VenueCredentials(credentials_source="prompt_result", environment_hint=mode, token=token)`。`{success:True, error_code:"", status_text:"", allow_retry:False}`。

### 2.4 `_finalize_login(adapter_creds)` helper（共有抽出）
旧 `venue_login._attempt` の末尾（現 live_orchestrator.py:817-841）を method へ抽出: `adapter.login(creds)` →
venue_sm `AUTHENTICATING→CONNECTED` → `_start_account_sync_after_login` / `_start_health_watchdog_after_login` /
`_start_instruments_scheduler_after_login` → `_suppressed_error_baseline`。`submit_venue_login` が呼ぶ。
（`venue_login` の env/session_cache 経路も同 helper へ寄せられるなら寄せる＝/simplify altitude。最低限 submit が使う。）

## 3. C# uGUI モーダル（D2/D3）

`VenueLoginModalOverlay`（新・`SecretModalOverlay`/`ModifyModalOverlay` の chrome を踏襲・z-order は secret(1000) 未満/
settings(900) 以上＝設定の上に重なる）。`OnVenueConnect(venue, env)`（BackcastWorkspaceRoot.cs:2969）を「即 VenueLogin」から
「**モーダルを開く**」へ配線変更。

- Tachibana: 認証ID 入力（通常 InputField）/ 秘密鍵 PEM パス（InputField＋「参照…」= `Win32FileDialog`/`MacFileDialog` の `.pem` ピッカー）/ demo・prod ラジオ（切替で `venue_login_form_init` 再呼び・prefill 再導出）。
- kabu: API パスワード（**char[] 無バッファ**= `onTextInput` で char[] へ・masked dot 表示・managed string を作らない）/ verify・prod ラジオ / 本体ポート表示 / 「再確認」（`venue_login_probe_station`）/ 本体未起動なら OK 無効。
- 送信: 「Authenticating…」busy 表示 → worker thread で `submit_venue_login`（`Py.GIL()`・kabu の `secret` は char[] → 使い捨て `PyString` → 直後に char[] zeroize）。失敗は `status_text` を赤字表示し閉じずに再試行。成功で `set_execution_mode("LiveManual")`（現 `VenueLogin` と同じ）→ モーダル閉じ → 既存の poll 駆動 badge / `_settingsAutoClose` が CONNECTED を拾う。

`Win32FileDialog` / `MacFileDialog` に汎用 `OpenFile(title, filterDesc, filterPattern, initialDir)`（または `.pem` 専用）を追加。

## 4. 削除（D4・dead code を残さない）

- 削除: `tachibana_login_flow.py` / `kabusapi_login_flow.py` / `_login_dialog.py` / `live_orchestrator._handle_prompt_login` / `live_orchestrator._try_create_tk` / `venue_login` の prompt 分岐 / `_attempt` の prompt 専用前半。
- `VenueCredentials.credentials_source` の `"prompt"` リテラルは削除（`Literal["session_cache","env","prompt_result"]`）。`prompt_result`＋token は kabu 成功時の adapter 受け渡しで存続。
- テスト移管/廃止:
  - `test_inproc_prompt_login.py` → 新 `test_venue_login_headless.py`（submit/probe/form_init の純検証・dispatcher 検証分岐・PRODGATE-01 を「prod が env フラグ無しで headless 認証へ到達」へ移管・kabu missing-token → `LOGIN_INVALID_RESPONSE` を submit 版で）。
  - `test_login_dialog_tk_teardown.py`（TKTEARDOWN-01/02/03）→ **廃止**。Tcl_AsyncDelete crash クラスは tkinter を起こさなくなる時点で構造的に消滅（移送対象ではない）。findings 0107 に stale-marker を付す。
  - `test_venue_login_prompt_macos_main_thread.py`（findings 0130 RED gate）→ 「prompt 経路（= venue_login モジュール）が tkinter を import/Tk しない」へ更新し **xfail-strict 撤去 → enforcing**。
  - `test_login_prefill.py`（ADR-0033）/ `test_prod_gate_abolished.py`（PRODGATE）/ `test_kabu_login_auth_rejected.py` の **ダイアログ依存部分**を新 RPC 経由へ書き換え（form_state/auth の純検証は不変で残す）。

## 5. Gate（behavior-to-e2e）

- **pytest（headless 認証半分）**: `submit_venue_login` 成功で CONNECTED 到達（mock adapter）・失敗の error_code/status_text/allow_retry の出し分け（kabu 本体未起動/API無効/PW不正・Tachibana AUTH_FAILED/SERVICE_OUT_OF_HOURS）・`form_init` の prefill・`probe_station` RPC・「venue_login モジュールが tkinter を import しない」import-purity gate。Action-ID: `VLOGIN-HEADLESS-0x` ＋ 移管した `PRODGATE-01`。
- **AFK probe（モーダル C# 半分・Python-FREE fake executor）**: 入力→送信→結果表示を fake submit executor（成功/失敗/再試行）で駆動。kabu の char[] バッファが managed string を作らず zeroize される・OK 無効/再確認・PEM ピッカー seam・モード切替 prefill。Action-ID: `VLOGIN-MODAL-0x`。`scripts/run-all-tests.ps1` の rollup に載せる。

## 6. HITL（owner 専用・自動化外）

実クリック → 実モーダル表示 → 実 venue（kabu verify / tachibana demo）ログイン → 実約定は display-bound（findings 0093 §HITL と同様）。
macOS 実機の crash 消滅確認も owner HITL。本スライスの自動 gate は「C# 入力→送信→結果」と「headless 認証」を Python-FREE / pytest で固定する。

## 7. 実装着地（2026-06-28）

### Python（665 passed, 1 skipped）
- 新 RPC: `LiveLoopManager.venue_login_form_init / venue_login_probe_station / submit_venue_login` ＋ `_finalize_login`
  （旧 `_attempt` 末尾を抽出・env/session_cache と共有）＋ `_login_preamble`（submit 用の venue guard）。4 層に配線
  （`LiveLoopManager` → `DataEngineBackend` → `BackendService` → `InprocLiveServer`）。
- 新 module `engine/exchanges/venue_login_headless.py`（`authenticate_tachibana` = login+save_session / `authenticate_kabu`
  = fetch_token / `LoginSubmitFailure(error_code, status_text, allow_retry)` / 旧 flow の `_map_exception` を移設）。
- 削除: `tachibana_login_flow.py` / `kabusapi_login_flow.py` / `_login_dialog.py` / `_handle_prompt_login` /
  `_try_create_tk` / `venue_login` の prompt 分岐。`VenueCredentials.credentials_source` の Literal から `"prompt"` 撤去・
  `_KNOWN_CRED_SOURCES` から撤去（live_orchestrator ＋ _backend_impl 両所）。`venue_login` の空 source は INVALID_CREDENTIALS_SOURCE。
- テスト: `test_inproc_prompt_login.py` → `test_venue_login_headless.py`（submit 成功で CONNECTED〔mock〕・EMPTY_FIELDS・
  KABU_STATION_NOT_RUNNING・LoginSubmitFailure の status_text/allow_retry・form_init・probe・PRODGATE-01・import-purity）。
  `test_login_dialog_tk_teardown.py`（TKTEARDOWN-01/02/03）= **廃止**（Tcl_AsyncDelete crash クラス消滅）。
  `test_venue_login_prompt_macos_main_thread.py` = xfail-strict 撤去 → enforcing（login 経路が tkinter を import/Tk しない）。
  `test_kabu_login_auth_rejected.py` / `test_prod_gate_abolished.py`(PRODGATE-08) を headless module へ repoint。

### C#
- `VenueLoginModalController`（plain C#・char[] 無バッファ secret・CanSubmit gate・BuildFieldsJson・mode 再導出・probe 連動・
  ApplyResult〔成功 close＋zeroize / 失敗 retain＋allow_retry〕）＋ `VenueLoginModalOverlay`（uGUI・ScreenSpaceOverlay
  sort 1000・kabu onTextInput char[] / tachibana InputField＋PEM 参照・mode radio・port/再確認・赤字 status）。
- `WorkspaceEngineHost.VenueLoginFormInit / VenueLoginProbeStation / SubmitVenueLogin`（worker thread＋Py.GIL・submit は
  char[]→transient PyString→zeroize＝CallSubmitSecret と同規律）。`Win32FileDialog`/`MacFileDialog`/`IFileDialog`/
  `StubFileDialog` に `OpenPrivateKey`（.pem）追加。
- `BackcastWorkspaceRoot`: overlay 生成＋イベント配線・`OnVenueConnect`（MOCK=env login / TACHIBANA・KABU=モーダル）・
  `OpenVenueLoginModal` / `DriveVenueLoginModal`（worker→main 受け渡し＋view 同期・kabu は Open 後に probe）・
  `OnVenueLogin{Char,Backspace,Mode,Browse,Recheck,Submit,Cancel}`・`ConnectConfigured`（#23 HITL 経路）も非 MOCK はモーダルへ。
- gate: `VenueLoginModalE2ERunner`（VLOGIN-MODAL-01..09・Python-FREE fake executor）＋台本 `.md`＋E2E-INDEX 登録。

### RED→GREEN litmus
- macOS gate（test_venue_login_prompt_macos_main_thread）: 旧は xfail-strict（off-main Tk 生成を観測）。fix 後は login 経路に
  tkinter import が無い＝enforcing PASS。login 経路に `import tkinter` を戻すと RED（再び macOS crash 経路）。
- AFK（VLOGIN-MODAL）: `Close` の zeroize / `CanSubmit` の StationRunning 依存 / `ApplyResult` の allow_retry / submit の
  secret 受け渡しを壊すと対応 section が RED（各 section 末尾 litmus）。
- pytest（PRODGATE-01）: prod front-stop を再導入すると prod が headless 認証へ届かず RED。

### 残（HITL）
- VLOGIN-MODAL-10/11（実 venue 認証・実モーダル表示・macOS crash 消滅の目視）は owner HITL＝実アプリの Settings ▸ Connect
  から出る uGUI モーダルで記録する。

## 9. レビュー後クリーンアップ（2026-06-28・/zoom-out review）

実装直後にレビュー（dead code / simpler / 回帰 / behavior カバレッジ）で見つけた修正:

### correctness（4 件）
1. **`submit_venue_login` の stuck-AUTHENTICATING**: `_ensure_live_loop()` と `_finalize_login()` が reset ガードの外＝そこで
   raise すると venue_sm が AUTHENTICATING に残り、以後 `ALREADY_AUTHENTICATING` で**全ログイン恒久ロックアウト**。`_ensure_live_loop`
   を try 内へ移し、finalize 呼び出しも try で包んで raise→`_reset_after_login_failure()`。
2. **timeout future 未 cancel**: `futures.TimeoutError` で orphan コルーチンが走り続け、tachibana は timeout 通知後に
   `save_session` を書く恐れ→ `fut.cancel()`（best-effort）を追加。
3. **set_execution_mode 失敗後の再 submit で二重 build**: C# は login 成功後に `set_execution_mode(LiveManual)` を叩き、失敗時は
   モーダルを開いたまま再試行させる。submit に idempotent already-CONNECTED short-circuit が無く `_start_live_components` を
   live session 上で再実行→ venue_login の no-op を移植（CONNECTED/SUBSCRIBED かつ live_last_error なし→成功返し）。
4. **kabu probe staleness race（C#）**: モード切替で旧 port の probe が後着すると `SetStationProbe` が新モードの port/running を
   上書き→ `VenueLoginModalController.SetStationProbe` で `port != StationPort` の stale probe を破棄。

### dead code（クリーン化）
- `VenueLoginSecretHitlHarness.cs` ＋ `VenueLoginSecretHitlMenu.cs` を**退役**（自前 Connect() が retired `venue_login("prompt")`
  ＝INVALID_CREDENTIALS_SOURCE で実 venue に繋げず・tkinter 廃止で credential form を持てない＝実アプリのモーダルが上位互換）。
- `VenueMenuViewModel` の `CredentialsSourceFor`（"prompt" 返し）/ `BuildConnectRequest`×2 / `VenueConnectRequest` struct を削除
  （production の OnVenueConnect は MOCK=`VenueLogin("env")`・実 venue=モーダルで、これらを通らない）。`VenueMenuM3Probe.BothVenuesUsePrompt`
  も削除（vacuous gate ＝ retired "prompt" 契約を green-lock していた）。M3Probe の他 section（poll badge / 接続 gating / LIVE_VENUE filter）は不変。
- adapter の到達不能 `if source == "prompt"` 分岐（`tachibana.py` / `kabusapi.py`）を削除＝`VenueCredentials.credentials_source` は
  "prompt" を含まない Literal で構築不能。stale comment（削除済 `_handle_prompt_login` / `run_dialog` 参照・`backend_service` の
  `or "prompt"` default）を是正。

### behavior カバレッジ追補（test_venue_login_headless.py）
- timeout→`LOGIN_TIMEOUT`＋venue_sm reset / finalize raise→reset（stuck-AUTHENTICATING の RED gate）/ kabu 空 token→
  `LOGIN_INVALID_RESPONSE` / 既 CONNECTED 再 submit の idempotent 成功。

## 8. HITL（owner 専用・自動化外）

実クリック → 実モーダル表示 → 実 venue（kabu verify / tachibana demo）ログイン → 実約定は display-bound（findings 0093 §HITL と同様）。
macOS 実機の crash 消滅確認も owner HITL。本スライスの自動 gate は「C# 入力→送信→結果」と「headless 認証」を Python-FREE / pytest で固定する。
