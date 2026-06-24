# findings 0016 — login subprocess の Python resolver が uv install root を見ない（実バグ修正）

> ⚠️ **HISTORICAL（#122 で前提コード撤去・2026-06-24）**: 本 findings が扱う login subprocess・`login_dialog_runner`・`_resolve_python_executable()` は #122/findings 0093 で in-process tkinter 化に伴い撤去済み。以下は当時のバグ修正の記録で、現行コードに対応物は無い。

## 症状（ハード証拠・確認済み）

ProductionLiveShell Connect（kabu Verify）が `login failed: LOGIN_SUBPROCESS_CRASHED`。Unity Editor.log の
`COMMAND LINE ARGUMENTS:` が login subprocess を **Unity ホスト exe で起動**していたことを示した:

```
D:\UnityHub\Editor\6000.4.11f1\Editor\Unity.exe
-m
engine.live.login_dialog_runner
--venue kabu
```

→ subprocess は `Unity.exe -m engine.live.login_dialog_runner …` となり Python を実行せず（使い捨て Unity が起動）
NDJSON を出さない → orchestrator（`live_orchestrator.py`）が `LOGIN_SUBPROCESS_CRASHED`。stderr 空も符合。

## root cause

`engine/_backend_impl.py` の `_resolve_python_executable()`。pythonnet / PyO3 in-proc では `sys.executable` は
host exe（Unity.exe / backcast.exe）で Python ではない。リゾルバは in-proc を検知して `sys.base_prefix` 配下の
**`Scripts/` と `bin/` だけ**を探索していたが、**uv が install する CPython は `python.exe` を install ROOT 直下に
置き `Scripts/` は空**。よって候補が見つからず host exe にフォールバックしていた。venue 非依存（同一 machine
構成なら tachibana も同症状）。

## 不変条件（behavior gate）

`_resolve_python_executable()` は「`sys.executable` が非 Python・`Scripts/`/`bin/` に python 不在・install ROOT
（`base_prefix` / `prefix`）直下に `python.exe` 在」のとき **root の python.exe を返す**（host exe にフォールバック
しない）。実 Python の `sys.executable`（venv-activated / tachibana out-of-proc）はそのまま返す（step 1/2 不変）。

## 修正（純加算）

step 3（Scripts/bin 探索）の後・exe フォールバックの前に、`base_prefix` と `prefix` 直下の
`python.exe`/`python3.exe`/`python` を探す step 4 を追加。a1ef5a6 の embedded-Windows path resolution と同型で、
step 1/2 は不変なので実 Python 経路・tachibana は退行しない。

## gate（backcast に FLOWS.md は無い。findings + test が正本）

- `python/tests/test_login_subprocess_env.py`
  - `test_resolve_python_uses_install_root_when_scripts_empty` — RED→GREEN（root python.exe を返す）
  - `test_resolve_python_keeps_real_sys_executable` — step 2 退行ガード（実 Python はそのまま）

## 任意（より堅牢な seam・未着手）

C# 側 `PythonRuntimeLocator.ConfigureBeforeInitialize` が `TTWR_PYTHON_BIN` を resolved venv python に明示セット
すれば step 1 で確定し、Python 側 resolve に依存しなくなる。今回は Python 側 resolve hardening（AFK 検証可）を本線とし、
C# 側は提案として残す。
