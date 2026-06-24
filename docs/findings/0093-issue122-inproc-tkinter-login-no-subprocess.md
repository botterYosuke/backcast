# findings 0093 — venue ログインを subprocess 廃止し in-process tkinter へ単純化（#122）

方針: ADR は無改変。本書は #122 スライスの下位確定事実を記録し、**findings 0012 の D4
（login UI 所有 = Python `login_dialog_runner` tkinter **subprocess**）を supersede** する。
0012 は findings（不変 ADR ではない）ため新 ADR は不要・既存 ADR は無改変。

## 発端（root cause）

通常起動の Unity app で Venue → Connect kabuStation (Verify) が
`login failed: VENUE_LOGIN_FAILED` で失敗していた。真因は **ホストプロセスの asyncio
ループ構成**であり、kabu API・ネットワーク・トークン・API パスワード・本体ログイン状態は
**すべて無関係**だった:

1. 埋め込み marimo server が Windows で
   `asyncio.set_event_loop_policy(WindowsSelectorEventLoopPolicy())` を **グローバル**に実行する
   （`marimo/_server/utils.py`）。
2. その後 `_ensure_live_loop`（`live_orchestrator.py`）が `asyncio.new_event_loop()` で作る live
   ループは、ポリシーを継承して **SelectorEventLoop** になる。
3. SelectorEventLoop は Windows で **subprocess 非対応**（`_make_subprocess_transport` が
   `NotImplementedError`）。
4. ログインダイアログは `create_subprocess_exec` で `login_dialog_runner` を起動していたため、
   この live ループ上で `NotImplementedError` を投げ、orchestrator が握りつぶして総称コード
   `VENUE_LOGIN_FAILED` を表示していた（症状の隠蔽）。
5. login の subprocess 経路は kabu / 立花で共通なので、両 venue とも壊れていた。

### 暫定ホットフィックス（保持＝defense-in-depth）

`_ensure_live_loop` で Windows 時に明示的に `ProactorEventLoop` を生成するホットフィックスを
先行適用した（Windows 既定値の復元・無害）。**#122 で subprocess login を撤去した後は login が
このループ種別に依存しなくなる**が、ホットフィックスは**残す**: ProactorEventLoop は Python の
Windows 既定であり、marimo が壊した既定を復元する。grep で確認したとおり **login は live ループ上の
唯一の subprocess 利用者**（`create_subprocess_exec` は `_handle_prompt_login` のみ）だったので、
保持の根拠は「将来 live ループに async subprocess が載っても安全」という防御的不変条件である。
回帰テスト `test_ensure_live_loop_is_proactor_under_marimo_selector_policy` がこれを pin する。

## 設計判断（grill 確定・案B 採用）

- **対象 OS は Windows 専用**で合意 → macOS の Tk main-thread（Cocoa）制約が消え、in-process
  tkinter が成立する（`Tk()` 非メインスレッド生成は Windows で動作する）。
- **案B を採用**: tkinter フォーム（`*_login_flow.py`）は残し、その外側の subprocess ラッパ・
  NDJSON・cred-path・python resolver を撤去する。案C（Unity ネイティブ modal）は C#↔Python の
  パスワード受け渡し＋keyboard-drain という新しい複雑性を足すため不採用。
- **セキュリティ維持**: tkinter は Python 側で動くため、API パスワードは埋め込み Python の
  メモリに閉じ、C# 管理メモリ（GC 残留する immutable string）には乗らない。
- **唯一のトレードオフ**: subprocess のクラッシュ分離を失う。`Tk()`/`mainloop` を try/except で
  囲み、失敗は `error_code` に降格してホストプロセスを落とさないことで緩和する
  （`_handle_prompt_login` の `run_in_executor` を `except Exception → VENUE_LOGIN_FAILED` で包む）。
- **専用スレッド**: dialog は最大で login timeout 分、人間の入力でブロックする。共有 executor
  プールは長時間ブロックで枯渇するため不可 → **per-login の単一 use `ThreadPoolExecutor`
  （max_workers=1）** を建てて `run_dialog` を走らせ、`finally` で shutdown する。tkinter の
  `Tk()`/`mainloop`/Tk-probe（`_try_create_tk`）はすべてこの 1 スレッド上で完結する。

## 確定事実（実装・2026-06-24）

### D1. in-process 化（両 venue）

- `_handle_prompt_login` の `create_subprocess_exec` を廃止。venue/env/prod-allow 検証と
  `try_create_tk`/`NO_DISPLAY_AVAILABLE` 判定を **in-proc dispatcher（`LiveLoopManager` モジュール）**
  へ移設（`_VALID_LOGIN_VENUES`/`_ENV_PER_VENUE`/`_try_create_tk`）。
- `kabusapi_login_flow.run_dialog(env_hint)` は token を **戻り値で直接返す**
  （`{"success","error_code","token"}`・cred-path ファイル書き込みを廃止）。立花は
  `tachibana_login_flow.run_dialog(env_hint)` が session_cache をディスクに書く現行のまま
  （token は返さない）。
- `NO_DISPLAY_AVAILABLE` の env フォールバック retry（debug ビルド限定・`venue_login._attempt`）は
  不変。`DEV_KABU_API_PASSWORD` 等の env フォールバックも維持。

### D1-timeout. login timeout = inner `asyncio.wait_for` + Tk-safe cancel（#122 統合レビュー M1）

subprocess 版は inner timeout で proc を `kill()` できた。in-proc 版は Python スレッドを外部から殺せない
が、同等の打ち切りを **2 段**で実装する: (1) `_handle_prompt_login` が
`asyncio.wait_for(run_in_executor(...), timeout=_live_login_timeout_s())` で内側打ち切りし、`LOGIN_TIMEOUT`
を返す（generic な `VENUE_LOGIN_FAILED` ではなく粒度を保つ）。(2) タイムアウト時に `threading.Event`
（cancel_event）を set し、dialog 側は `root.after(200ms)` の `_poll_cancel` でそれを観測して **自スレッドで
`root.destroy()`**（Tk-thread-safe）＝窓を閉じる。これで「打ち切り後も窓が残り再 Connect で積層」を解消する。
- `executor.shutdown(wait=False)` は意図的（hung dialog を待って live loop をブロックしない）。
- auth daemon thread（kabu `fetch_token` / 立花 pubkey login）の `root.after` は destroyed-root 対策で
  try/except ガード済み（cancel/タイムアウトで窓が消えても daemon が TclError で死なない・token は GC され
  ログ/ディスクに残らない）。
- 残る理論的 edge: cancel_event 観測前（≤200ms）に thread が走り続ける点のみ＝Python スレッドを殺せない
  言語制約で、「subprocess クラッシュ分離を失う」合意済みトレードオフの最小残差。回帰テスト
  `test_prompt_login_hang_dialog_times_out_to_login_timeout` が hang→`LOGIN_TIMEOUT`（< 3s 復帰）を pin。
- **M1（統合レビュー）**: `cancel_event.set()` は **`finally`** に置く（except 節だけだと
  live-loop teardown 由来の `asyncio.CancelledError`＝`BaseException` 直系が `except Exception` に
  捕まらず、cancel_event が立たないまま non-daemon executor ワーカーが `mainloop()` に居残り
  → インタプリタ終了の atexit join で hang する）。成功経路では dialog が既に return 済で no-op・冪等。
- **M4（統合レビュー）**: cancel-close の *判定*（cancel 観測 ⇒ `LOGIN_TIMEOUT` 記録＋token クリア）を
  両 venue 共有の `engine/exchanges/_login_dialog.apply_cancel_timeout` に抽出し、display 非依存で単体化
  （`test_apply_cancel_timeout_*`）。実 `root.destroy()` 配線は display-bound＝owner HITL がカバー。

### D2. dead-code 撤去

- `engine/live/login_dialog_runner.py` 削除（subprocess エントリポイント・NDJSON プロトコル）。
- `_backend_impl.py` の `_resolve_python_executable` / `_login_subprocess_env`
  / `_sweep_stale_cred_files`（cred-path tempfile の Windows ハンドルリース掃除）を撤去
  ＝唯一の呼び出し元（login subprocess）が消えたため。`import tempfile` と
  `from .paths import PYTHON_SRC_ROOT` も併せて撤去（login 専用だった）。
- `live_orchestrator.py` の `import os` / `import subprocess` / `import tempfile` / `import json`
  / `import re`（login subprocess 専用または既に未使用だった dead import）を撤去。
- `tests/test_login_subprocess_env.py` を撤去し、`tests/test_inproc_prompt_login.py` で置換。

### D3. 回帰ゲート（behavior-to-e2e 正本）— RED→GREEN

死角: 既存 login テスト（`KabuLiveE2ERunner`/`TachibanaLiveE2ERunner`/`VenueLoginSecretProbe`）は
すべて `credentials_source="env"` でダイアログ経路を**通っていない**。元バグは
「marimo selector policy × prompt 経路（subprocess）× Windows」の交点が**どのテストでも
実行されていなかった**ため漏れた。新規 `tests/test_inproc_prompt_login.py` がこの交点を pin:

- `test_kabu_prompt_login_returns_token_in_memory_on_selector_loop` — `SelectorEventLoop` 上で
  prompt 経路が token を in-memory で返す（headless ダイアログスタブ）。
- `test_tachibana_prompt_login_succeeds_on_selector_loop` — 立花は token=None・session_cache 経路。
- `test_prompt_login_dialog_crash_degrades_to_error_code` — `Tk()`/`mainloop` 例外が
  `error_code` 降格でホストを巻き込まない（トレードオフ緩和の証明）。
- `test_prompt_login_no_display_degrades_to_error_code` — headless host で
  `NO_DISPLAY_AVAILABLE`（dispatcher 所有の判定）。
- `test_ensure_live_loop_is_proactor_under_marimo_selector_policy`（win32 限定）— marimo の
  `WindowsSelectorEventLoopPolicy` を global 設定した状態で `_ensure_live_loop` が
  `ProactorEventLoop` を返す（hotfix の defense-in-depth pin）。
- `test_prompt_login_hang_dialog_times_out_to_login_timeout`（統合レビュー M1）— hung ダイアログが
  inner `asyncio.wait_for` で `LOGIN_TIMEOUT` に打ち切られ live loop を hang させない（< 3s 復帰）。
- `test_venue_login_prompt_reaches_connected`（統合レビュー M2）— end-to-end で `venue_login("KABU","prompt",…)`
  が実 live loop（`run_coroutine_threadsafe`）→ `_handle_prompt_login` → token 注入 → `venue_sm` が
  `CONNECTED` へ収束。既存 login テストが全て `credentials_source="env"` で迂回していた死角を本当に通す。

**RED**（pre-#122 subprocess code・Windows・2026-06-24）: 1 本目が
`_make_subprocess_transport → NotImplementedError`（`_WindowsSelectorEventLoop`）で fail
＝元バグの厳密再現（test 内 `run_dialog` monkeypatch が subprocess 境界を越えられず、実ダイアログが
headless で走る／selector ループで subprocess 起動が落ちる）。
**GREEN**（in-proc 化＋統合レビュー M1/M2 反映後・2026-06-24）: 7 passed。フルスイート
`uv run pytest tests/` = **537 passed**。

再走: `cd python && uv run pytest tests/test_inproc_prompt_login.py -q`。

## 実 venue 受け入れ（HITL・Windows 実機・owner 手動）

AFK/pytest GREEN だけでは実接続 AC を完了扱いにしない（findings 0012 D3 と同方針）。下記 2 leg を
Windows 実機で実施し、日時・OS・Unity/Python 版・結果とともに本節へ追記すること。

- **kabu Verify HITL**: ☐ 未実施。通常起動 Unity app から Venue → Connect kabuStation (Verify)。
  in-process tkinter ダイアログが開き、API パスワード入力 → `fetch_token` → token を in-memory で
  受領 → `CONNECTED`。**subprocess を一切起動しない**こと・**`SecretRequired` が出ない**ことを確認。
- **立花 demo HITL**: ☐ 未実施。同一経路で login round-trip（session_cache 書き込みは現行維持）。

## behavior gate

backcast に FLOWS.md は無く、本 findings ＋ `tests/test_inproc_prompt_login.py` の RED→GREEN
＋ owner HITL leg が等価物。
