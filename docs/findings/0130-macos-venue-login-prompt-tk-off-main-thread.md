# 0130 — macOS: Settings ▸「Connect Tachibana (Demo)」で Unity が落ちる — tkinter を非メインスレッドで起こす（root cause 確定 / RED gate land）

**Issue**: #181（owner 端末 macOS で再現報告 2026-06-28） / **Status**: 原因確定・**RED gate land 済み**・fix は #181（着手可能）
**Gate**: `python/tests/test_venue_login_prompt_macos_main_thread.py`（`@pytest.mark.xfail(strict=True)`・darwin 限定）
**Repro probe**: `python/spike/` 不要——in-process pytest で決定論再現（下記）
**関連**: #122 in-proc dialog（findings 0093）・[[macos-shutdown-segfault-duckdb-threadstate]]（別件の macOS-only teardown crash）

## 1. 依頼と結論サマリ

「この端末（macOS）で setting dialog の `Tachibana(Demo)` をクリックすると Unity アプリが落ちる。原因を調査せよ」。

- **真因**: クリック → venue prompt-login が **tkinter `Tk()` を専用 executor スレッド（非メインスレッド）で生成**する。macOS の Cocoa は `NSWindow` を**メインスレッド限定**で要求するため、非メインでの `Tk()` が `NSInternalInconsistencyException`（"NSWindow drag regions should only be invalidated on the Main Thread!"）→ `libc++abi` abort（**SIGABRT / exit 134**）。これは Obj-C の abort で **Python 例外ではない**ので、`_try_create_tk` / `_handle_prompt_login` の `except Exception` では捕捉できず**ホストプロセス（Unity）が即死**する。
- #122 の in-process dialog 設計（findings 0093）は **Windows で検証**された（Windows は Tk を secondary thread で動かせる）。macOS Cocoa は不可——これが死角。
- 既存 prompt-login テストは**全て** `monkeypatch.setattr(live_orchestrator, "_try_create_tk", lambda: True)` で実 Tk probe を潰しており、誰も「実 `_try_create_tk` を非メインで走らせる」経路を踏まない＝この death-angle を全員すり抜ける。
- abort は **Cocoa main-thread UB のため非決定論**（同一コードで exit 134 と exit 0 の両方を観測）。よって gate は flaky な「落ちる」症状ではなく**決定論の真因不変条件**（「macOS で prompt-login が `Tk()` を非メインスレッドで生成しない」）を assert する。fix 方式は非依存（main-thread dispatch でも off-main 拒否でも両立）。

## 2. 呼び出し連鎖（クリック → crash）

```
Settings ▸ "Connect Tachibana (Demo)" ボタン
  SettingsVenueSectionView (Assets/Scripts/Live/SettingsVenueSectionView.cs:46,63)
  ▸ BackcastWorkspaceRoot.OnVenueConnect("TACHIBANA","demo")   (BackcastWorkspaceRoot.cs:2969)
  ▸ WorkspaceEngineHost.VenueLogin(...)                         (WorkspaceEngineHost.cs:650)
      new Thread("WorkspaceVenueLogin") + Py.GIL()              ← 非メイン worker
  ▸ venue_login("TACHIBANA","prompt","demo")                    (live_orchestrator.py:658)
  ▸ _attempt("prompt")
  ▸ asyncio.run_coroutine_threadsafe(_handle_prompt_login, live_loop)   (live_orchestrator.py:786)  ← live loop スレッド
  ▸ _handle_prompt_login                                        (live_orchestrator.py:566)
  ▸ loop.run_in_executor(executor, _run)   executor="venue-login-dialog" (live_orchestrator.py:622-627)  ← 非メイン worker
  ▸ _run ▸ _try_create_tk()                                     (live_orchestrator.py:80,601)
  ▸ tkinter.Tk()  →  TkpInit ▸ TkMacOSXMakeRealWindowExist ▸ -[NSWindow init...]  ← 非メインで Cocoa → ABORT
```

実クラッシュ署名（本調査で再現）:

```
*** Terminating app due to uncaught exception 'NSInternalInconsistencyException',
    reason: 'NSWindow drag regions should only be invalidated on the Main Thread!'
  -[NSWindow _initContent:styleMask:backing:defer:contentView:]
  TkMacOSXMakeRealWindowExist   (libtcl9tk9.0)
  TkpInit
  _tkinter_create               (_tkinter.cpython-313-darwin.so)
  Tkapp_New
  thread_run                    ← venue-login-dialog worker thread（非メイン）
libc++abi: terminating due to uncaught exception of type NSException   → exit 134
```

## 3. 決定論再現（root-cause invariant）

実 production `_handle_prompt_login` を実行（`run_dialog` のみ no-op stub・`Tk` をスレッド記録 fake に差替＝Cocoa を触らず決定論化）:

```
result: (True, '', None)
tk constructed on threads: ['venue-login-dialog_0']   ← 非メイン executor で毎回 100% 生成
OFF-MAIN (bug present): ['venue-login-dialog_0']
```

abort 自体は非決定論（exit 134 / exit 0 を観測）だが、「非メインで `Tk()` を生成する」事実は**決定論で 100%**。これが gate の正本。

## 4. RED→GREEN

- **RED（現状）**: `test_prompt_login_never_creates_tk_off_main_thread` が `XFAIL`（darwin）。実 `_handle_prompt_login` を駆動 → `Tk()` が `venue-login-dialog` worker（非メイン）で生成 → `off_main` 非空 → assert 失敗 → xfail。suite は赤くならない。
- **GREEN（fix 後・両案で検証済み）**: off-main `Tk()` が消えると `off_main` 空 → assert 通過 → **XPASS-strict が hard-fail** → xfail marker 撤去を強制し enforcing gate 化。
  - 案(a) main-thread dispatch: `Tk()` がメインで生成 → `off_main` 空 → PASS。
  - 案(b) off-main 拒否で degrade: `_try_create_tk` が darwin×非メインで `Tk()` を呼ばず `False` → `result=(False,'NO_DISPLAY_AVAILABLE',None)` → PASS（スクラッチ実証済み）。
- delete-the-production-logic litmus: 将来 fix の main-thread guard を外せば再び off-main → RED。

非 darwin は `skipif`（Tk off-main は合法・本不変条件は macOS 固有）。

## 5. 修正方針（owner 設計判断待ち）

| 案 | 内容 | 評価 |
|---|---|---|
| (a) main-thread dispatch | dialog を Unity メインスレッドへマーシャルして実行 | 本筋だが live loop はメインでない＝Unity 側のメインスレッド pump が要る大きめの配線変更 |
| (b) off-main 拒否で degrade | `_try_create_tk` が darwin×非メインで `Tk()` を呼ばず `NO_DISPLAY_AVAILABLE` を返す | 低リスク・既存 `except` の明示意図（"never take down the host process"）と一致。debug build では `NO_DISPLAY_AVAILABLE` → **env 資格情報（`DEV_TACHIBANA_AUTH_ID_DEMO` 等）へ自動 fallback** するので Tachibana(Demo) が実際に接続できる副次利得。release build では crash せず `NO_DISPLAY_AVAILABLE` 表示 |

推奨は **(b) を即時の crash 止血**として入れ、(a) を別 issue で follow（macOS でも実ダイアログ表示を得る）。

### 5.1 採用方針（owner 判断 2026-06-28・#181 を更新）

owner 判断で **(c) venue ログインを Unity ネイティブ uGUI モーダル化し tkinter を廃止**を本命採用（#181 に格上げ・(a)/(b) は不採用）。理由: tkinter を Python 側で一切起こさなくなるため **crash が構造的に消える**＋「別 OS ウィンドウで気づかない」UX も解決＋Windows でも #122/#133 の thread/teardown 綱渡りが不要になる。

実現可能性調査の要点（2026-06-28）: 認証ロジックは既に GUI 非依存に分離済み（`*_login_form_state.py` の build_form_init/validate_submission/probe_station/auth_failure_view ＋ `*_auth.py` の login/fetch_token）なので tkinter view を uGUI view に差し替えるだけ。Unity 側部品も既存（`SettingsModalOverlay`/`ModifyModalOverlay` の modal chrome・`TMP_InputField(Password)`・`SecretModalOverlay` の masked 方式・`MacFileDialog`/`Win32FileDialog`・`venue_login` RPC の `prompt_result` 前例）。設計判断は #181 本文の 5 点（prompt 分岐の分解／C#→Python フォーム値 seam／PEM ピッカー／secret 規律／kabu probe_station RPC）。

本 gate（off-main Tk 構築を禁止する不変条件）は Unity 化後も整合する——prompt 経路が tkinter を起こさなくなれば「非メインで Tk()」自体が発生せず PASS（GREEN）になり、xfail-strict marker を撤去して enforcing 化できる。

## 6. HITL

実クリック → 実ダイアログ表示 → 実約定までは display-bound（findings 0093 §HITL と同様 owner 専用）。本 gate は crash の真因（off-main Tk 構築）だけを決定論で固定する。
