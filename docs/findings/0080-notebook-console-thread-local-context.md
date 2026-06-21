# 0080 — Strategy Editor の `print()`/console が実機で出ない真因: marimo RuntimeContext の thread-local 取りこぼし

方針: **findings 0079（#102 console + dynamic layout）の実機回帰の真因 fix**。ADR 無改変。
前提: 0076（console 設計）, 0079（C# layout / audit gaps）。

## 症状（owner HITL 2026-06-21）

実アプリで cell に `print('a')` を入力し ▶ を押すと、フッターに `Run cell: ContextNotInitializedError:` が出て
**console どころか rich output も含め何も表示されない**（run 全体が失敗）。pytest（`test_notebook_console.py` 7本）と
AFK E2E（`StrategyEditorNotebookE2ERunner` S21/S22）は両方 GREEN。

## 根本原因（実 Editor.log のトレースバック + 純 Python 再現で確定）

```
_backend_impl.py:991       run_pressed(...)                         # except で {"ok":False,"error":"ContextNotInitializedError: "}
notebook_session.py:578    _await(runner.run_all())
notebook_session.py:52     asyncio.run(maybe_coro)
marimo cell_runner.py:721  should_broadcast_data=_should_broadcast_data()
marimo cell_runner.py:71   ctx = get_context()
marimo context/types.py:248  raise ContextNotInitializedError
```

- marimo 0.20.4 の `cell_runner.run_all()` は冒頭で `_should_broadcast_data()` → **`get_context()` を fallback 無しで
  無条件呼び出し**（`cell_runner.py:71,721`）。
- marimo の RuntimeContext は `_ThreadLocalContext(threading.local)`（`context/types.py`）＝**OS スレッドローカル**。
  新しいスレッドが触れると `__init__` が `runtime_context=None` にリセット。`initialize_kernel_context` の docstring も
  **「Must be called exactly once for each client thread」**。
- `IncrementalNotebookSession._ensure_host` で context をセットした**スレッド**と、kernel を**駆動する**
  スレッドが、埋め込み pythonnet+`asyncio.run` レーンで食い違うと、駆動スレッドの thread-local が空 → 例外 →
  press 全体が失敗。**`run_all` だけでなく `_restage`（セルの再登録/コンパイル）も context を要する**: marimo は
  登録時に context へ publish するため、**コードを編集した後の press（変更セルの再登録）** が `_restage` で落ちる。
  この経路の例外は `run_pressed` が内部 try で捕捉して `{"ok":False,"error":...}` を返すので、`_backend_impl` の
  `except`（`logging.exception("run_cell failed")`）に**到達せず＝ Unity ログに traceback が出ない**（フッターに
  `Run cell: ContextNotInitializedError:` だけ）。

### なぜ全テスト緑なのに実機だけ落ちたか（テストギャップ）

- E2E（S21/S22）は **Python-FREE**（`_ConsoleExecutor` フェイク）→ marimo を一切踏まない。
- pytest（7本）は **C#-FREE で純 CPython**＝build と run が確実に同一スレッド → context 常在で必ず緑。
- **build スレッド ≠ run スレッドを踏む end-to-end ゲートが不在**。配線・JSON キー(`console`/`stream`/`text`)・C# paint は全て正しい。

### 決定論的再現（pythonnet 不要・スレッド非依存）

`s = IncrementalNotebookSession()` で **`s._ensure_host()`（context をこのスレッドに設定）→ `teardown_context()`
で「run スレッドに context 無し」を再現 → press 2 回**：①`print('a')` ②`print('CHANGED')`（編集）。
fix 前は ①が通っても ②が `_restage` で `ContextNotInitializedError`（実機の「run1 OK / 編集後 run2 失敗」と一致）。
> ⚠️ ゲートは**単一スレッド**で書く（context を建てたスレッドで `teardown_context`/`close` するので marimo の
> per-thread RuntimeContext を他テストへ漏らさない）。別スレッドで建てて main で close する版は、ephemeral
> スレッドに context が残り、スレッドスケジューリング非決定性 ＋ 後続 marimo テストの間欠失敗を招く
> （memory `behavior-to-e2e`「複数 backend で RuntimeContext already initialized」）。

## fix

marimo の RuntimeContext を **kernel を駆動する現在のスレッドに再アサート**する。**登録(_restage)と実行(_run)を
1 つの install スコープで包む**のが肝（_run だけでは編集後 press が `_restage` で落ちる）。

- `thin_drain.HeadlessKernel.__post_init__`: `initialize_kernel_context(...)` の返り値 `KernelRuntimeContext` を
  `self.runtime_context` に保持。
- `notebook_session._kernel_context()`（新ヘルパ）: `self._ensure_host().runtime_context.install()` を返す。
  `RuntimeContext.install()` は marimo 自身（AppKernelRunner）が使う再入可能ガード＝現在のスレッドに this
  kernel の context を pin し退出時に旧値へ復帰（save/restore）。build==run スレッドの従来パスは「同値を
  save→restore」で実質無改変、食い違い時のみ実効。手書き initialize/teardown より堅牢（None・同一・別 context
  の全ケースを restore で処理）かつ marimo 公認パターン＝bandaid ではない。
- `run_pressed` / `restage`: `with self._kernel_context():` で **`_restage` も `_run` も**包む。`_run` の例外は
  `with` を抜けて伝播させ `_backend_impl` のログ（`run_cell failed` + traceback）を温存。

## ゲート（RED→GREEN）

- **Python e2e**（`python/tests/test_notebook_console.py` に追加・正本）:
  `test_run_succeeds_when_kernel_was_built_on_another_thread` — thread A で `_ensure_host` → **単一 worker L** で
  press 2 回（fresh `print('a')` → **編集後** `print('CHANGED')`）。両方とも例外なく console を捕捉。
  - **RED**（fix 前 / narrow fix=_run のみ包む版でも）: press 2 が `_restage` で `ContextNotInitializedError`。
  - **GREEN**（fix 後）: console = `a\n` then `CHANGED\n`。
  - delete-the-production-logic litmus: `run_pressed` の `with self._kernel_context()` を外すと即 RED。
- **C# 側**: 本 fix は Python のみ。既存 S21/S22（Python-FREE）と C# 配線は無改変＝回帰なし
  （compile-only ゲートで `error CS` 0 を確認）。

## 影響ファイル

- `python/engine/strategy_runtime/thin_drain.py` — `HeadlessKernel.runtime_context` 保持。
- `python/engine/strategy_runtime/notebook_session.py` — `_run` の context 再アサート（+ top import）。
- `python/tests/test_notebook_console.py` — cross-thread 回帰テスト追加。
