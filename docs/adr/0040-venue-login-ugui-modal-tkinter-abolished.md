---
status: accepted
---

# Venue ログインを Unity ネイティブ uGUI モーダル化し tkinter を廃止 — オーケストレーションの反転

`grill-with-docs`（2026-06-28, owner HITL）で導出。issue #181 / findings 0130。owner の依頼:
「macOS で Settings ▸『Connect Tachibana (Demo)』を押すと Unity が落ちる。venue ログインを Unity
ネイティブの uGUI モーダルにして tkinter を廃止せよ。実装コストは度外視して理想的な完成形を目指せ」。

## 背景

[[venue 接続状態]] の prompt 経路は「**C# がトリガ → Python が tkinter GUI を開く**」だった（#122 / findings
0093: subprocess を廃し in-process の専用スレッドで `tkinter.Tk()` を起こす）。findings 0130 で、この設計が
macOS で構造的に壊れることが確定した: 非メインスレッドで `Tk()` を起こすと Cocoa の `NSWindow` main-thread
違反で `libc++abi` abort（SIGABRT / exit 134）し、Obj-C の abort なので Python の `except` で捕捉できず
ホストプロセス（Unity）が即死する。Windows では Tk を secondary thread で動かせるため #122 検証をすり抜けた。

findings 0130 §5 は止血案として (a) main-thread dispatch・(b) off-main 拒否 degrade を挙げたが、owner は
本 ADR の **(c) GUI ライブラリを Python 側で一切起こさない** を本命採用した（findings 0130 §5.1）。

## Decision

**venue ログインダイアログ（Tachibana / kabu 共通）を Unity ネイティブの uGUI モーダルとして実装し、Python の
tkinter ダイアログ経路を完全に廃止する。** 設計の要は**オーケストレーションの反転**:

- 旧: C# がトリガ → **Python が GUI を開き、入力を集め、認証し、結果を返す**。
- 新: **C# が GUI（uGUI モーダル）を持ち、入力を集める** → **Python は受け取ったフォーム値で headless 認証だけ実行**する。

認証ロジックは既に GUI 非依存に分離済み（`*_login_form_state.py` の `build_form_init` / `validate_submission` /
`probe_station` / `auth_failure_view` ＋ `*_auth.py` の `login` / `fetch_token`）なので、tkinter view を uGUI
view に差し替えても**認証本体は無改変**で再利用する。

### D1: C#→Python の seam は専用 headless RPC 3 本（`venue_login` の prompt 分岐は撤去）

`venue_login(...,"prompt",...)` の分岐・`_handle_prompt_login` / `_try_create_tk` を撤去し、モーダルが駆動する
**専用 RPC 3 本**を新設する（下位の正本は findings 0131）:

1. **`venue_login_form_init(venue_id, mode)`** — モーダル初期化／モード切替時の prefill 再導出（`build_form_init`
   をそのまま Unity から呼ぶ）。Tachibana=認証ID・秘密鍵パスの prefill（ADR-0033・debug demo のみ）、kabu=本体
   ポート＋API パスワード prefill（debug verify のみ）。
2. **`venue_login_probe_station(venue_id, mode)`** — kabu 本体の起動確認（`probe_station` を Unity から呼ぶ）。
   `{running, port}` を返す。kabu 以外は `running=True` 固定（OK 有効判定を統一）。
3. **`submit_venue_login(venue_id, mode, fields_json, secret)`** — 1 回の送信で「検証 → headless 認証 →（成功時
   のみ）後処理配線」を行う。**モーダルが開いている間に何度でも呼べる**（失敗は閉じずに再試行）。後処理（adapter.login
   ／venue_sm を CONNECTED へ／account sync・health watchdog・instruments scheduler 起動）は**成功確定後にだけ**走る。
   返り値 `{success, error_code, status_text, allow_retry}`（`status_text` は既存 `raise_for_login_error` /
   `auth_failure_view` の日本語）。

`venue_login` 自体は `env`/`session_cache`（MOCK・起動時 session_cache 等）経路として存続する。

### D2: シークレット規律は char[] 無バッファ（既存 [[secret modal]] と一致）

kabu の API パスワードは C# 側で**managed string を一切作らず** char[] バッファに直接ためる（`SecretModalOverlay`
の `onTextInput`/`SecretModalController` 方式に倣う）。pythonnet 境界で使い捨て transient string を 1 本だけ作って
`submit_venue_login` の `secret` 引数に渡し、直後に char[] を zeroize する。Tachibana の認証ID・秘密鍵パスは
秘密ではない（ID とファイルパス）ので通常の入力欄でよい。

### D3: 秘密鍵 PEM 選択はネイティブ file picker

Tachibana の秘密鍵 PEM 選択は `Win32FileDialog`（comdlg32）/ `MacFileDialog`（Editor `OpenFilePanel`）に `.pem`
ピッカーを足して使う。macOS は出荷外なので Editor 経路で十分（findings 0130 §HITL）。

### D4: tkinter 経路は完全削除（dead code を残さない）

`tachibana_login_flow.py` / `kabusapi_login_flow.py` / `_login_dialog.py`（`teardown_tk` / `apply_cancel_timeout`）
／ `_handle_prompt_login` / `_try_create_tk` ／ `venue_login` の prompt 分岐を削除する。`VenueCredentials` の
`credentials_source="prompt"` リテラル・`prompt_result` の token 受け渡しの扱いは findings 0131 で確定する。

依存テストは**削除でなく新 RPC のテストへ移管**する: `test_inproc_prompt_login.py`（prompt dispatcher）・
`test_login_dialog_tk_teardown.py`（TKTEARDOWN-01/02/03 = #133-135 の Tcl_AsyncDelete 回帰）・
`test_venue_login_prompt_macos_main_thread.py`（findings 0130 RED gate）・prefill/prod-gate/auth-rejected の
ダイアログ依存部分。**Tcl_AsyncDelete crash クラスは tkinter を起こさなくなる時点で構造的に消滅する**ので
TKTEARDOWN 回帰は移送対象でなく**廃止対象**（findings 0131 に明記）。

## Considered Options

- **採用 (c): uGUI モーダル化＋tkinter 廃止。** crash が構造的に消える（Python が GUI を起こさない）＋「別 OS
  ウィンドウで気づかない」UX 解決＋Windows でも #122/#133 の thread/teardown 綱渡りが不要。実装は大きいが owner が
  「理想的な完成形」を明示。
- **不採用 (a): main-thread dispatch。** live loop はメインでないため Unity 側のメインスレッド pump が要る大きめの
  配線変更で、tkinter 依存も別 OS ウィンドウ UX も残る。
- **不採用 (b): off-main 拒否で `NO_DISPLAY_AVAILABLE` degrade。** 即時の止血としては最小だが、macOS で実ダイアログを
  得られず（debug は env fallback / release は表示のみ）UX 課題が残る。crash の根本（GUI を Python で起こす）は不変。

## Consequences

- **不具合解消**: macOS の venue-login crash は構造的に発生し得なくなる（Python は tkinter を import すらしない）。
- **UX**: ログインがアプリ内モーダルで出る（別 OS ウィンドウが出ない）。失敗時はモーダル内に日本語エラーを赤字表示し
  閉じずに再試行。
- **Python**: 上記ファイル・関数を削除。`*_login_form_state.py` / `*_auth.py` / `tachibana_login_messages` /
  `tachibana_file_store` は headless 認証として存続・再利用。新 RPC 3 本を `LiveLoopManager` ＋ `DataEngineBackend`
  に追加。
- **C#**: `VenueLoginModalOverlay`（新）＋ `OnVenueConnect` をモーダル起動へ配線。`Win32FileDialog`/`MacFileDialog`
  に `.pem` ピッカー追加。新 RPC 用 lane。`VenueLoginSecretHitlHarness` も uGUI モーダルへ更新。
- **テスト**: tkinter 依存テストは新 RPC テストへ移管、TKTEARDOWN 回帰は廃止（crash クラス消滅）。findings 0130 の
  macOS gate は「prompt 経路が tkinter を import/Tk しない」へ更新し xfail-strict を撤去して enforcing 化。
- **CONTEXT**: [[venue 接続状態]] の login-UI 所有のバックリンク（L184「#122/findings 0093 が supersede」）を
  本 ADR / findings 0131 へ更新。
- **下位事実は findings に固定**: RPC の正確な signature・削除した個々の call site・移管したテストの assert・
  RED→GREEN・AFK 再走手順は `docs/findings/0131-*.md` に記録し、本 ADR を「方針: ADR-0040」として参照する
  （本 ADR には書き戻さない）。

## 自己保護

本 ADR の decision は固定。覆す（tkinter ダイアログを再導入する／Python に GUI 所有を戻す）場合はこのファイルを
編集せず、**本 ADR を supersede する新規 ADR** を起こす。下位事実（RPC signature・削除 call site・テスト移管の
assert・モーダルのフィールド配置の細部）は本 ADR に書き戻さず slice の findings に記録し本 ADR を参照する。
ADR-0023（v4r9 pubkey auth）/ ADR-0027（prod-gate 廃止）/ ADR-0033（prefill）は自己保護条項により編集しない——
本 ADR が「login UI の所有を C# へ反転する」旨は本 ADR 側にのみ記す（認証本体・prod-gate・prefill の方針は不変で
再利用する）。
