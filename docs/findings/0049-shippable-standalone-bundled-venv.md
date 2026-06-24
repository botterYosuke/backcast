# findings 0049 — Shippable standalone build packaging（bundled venv → 実 exe 起動）

- Issue: #33（spinoff: #82 login subprocess hygiene）
- 関連 ADR: [ADR-0001](../adr/0001-unity-pythonnet-embedded-frontend.md)（d3 executor orphan-absence）/ [ADR-0002](../adr/0002-embedded-python-runtime-placement-and-resolution.md)（runtime 配置・本 issue で build 分岐が初実装）/ ADR-0004 案 C（kernel）/ ADR-0006（DuckDB 直読み）/ ADR-0012（marimo prod 昇格）
- 配置の根拠: ADR-0002 自己保護条項（slice 内で確定する下位事実は本 ADR に書き戻さず `docs/findings/` に記録し本 ADR を「方針: ADR-0002」として参照）。本ファイルは #33 で確定した下位事実の記録であり、ADR の方針を変更するものではない。
- grill-with-docs: 2026-06-18

---

## 1. なぜ #33 を書き直したか（scope re-anchor）

issue #33 当初本文の AC は `nautilus 1.226.0 pin` / `PRECISION_BYTES=8 standard と整合` を要求していたが、以下の事後 ADR で前提が完全に消滅していた:

- **ADR-0004 案 C**: nautilus Rust core 排除・pure-Python `Backcast Execution Kernel` に置換。`pyproject.toml` の `[project.dependencies]` から `nautilus-trader` は削除。
- **ADR-0006**: nautilus catalog 排除・J-Quants DuckDB 直読み。`PRECISION_BYTES` 概念自体が runtime に存在しない。
- **ADR-0012**: marimo を `[project.dependencies]` へ昇格（spike-only から prod へ）。

→ AC を **現実の依存 / runtime** に整合させ、Issue 本文を rewrite。`PRECISION_BYTES` 整合 AC は削除、dep pin は `CPython 3.13.11 win_amd64 + uv.lock の resolved version` へ置換。

## 2. 確定設計

### 2.1 Asset bundle layout（`<exe>_Data/StreamingAssets/PythonRuntime/`）

```
PythonRuntime/
├── cpython/                              ← uv CPython root の verbatim copy
│   ├── python.exe                        ← TTWR_PYTHON_BIN / subprocess resolver step 1
│   ├── python313.dll                     ← Locator が _libPython にバインド
│   ├── vcruntime140.dll / msvcp140.dll   ← uv 同梱 / AddDllDirectory で可視化
│   └── DLLs/, Lib/, ...
├── python/
│   ├── engine/                           ← import root (`import engine`)
│   └── .venv/Lib/site-packages/          ← duckdb / marimo / pyarrow / scikit-learn /
│       │                                   pandas / numpy / pydantic / httpx / websockets /
│       │                                   joblib / orjson + transitive
│       └── (pyvenv.cfg は post-process で DELETE)
└── runtime-manifest.json                 ← {schema, issue, target, cpython_version, built_at, paths}
```

### 2.2 Bundle 中身の選択（grill）

| カテゴリ | 含める？ | 理由 |
|---|---|---|
| `python/engine/` | ✅ | kernel/adapter/sink/strategy_runtime/scenario — Replay 必須 |
| `python/.venv/Lib/site-packages/` | ✅ | runtime deps（marimo/duckdb 等）|
| uv CPython root | ✅ | embed interpreter |
| `python/strategies/` | ❌ | strategy は Strategy Editor で user 作成 |
| `python/spike/` | ❌ | throwaway |
| `python/tests/` | ❌ | CI 側 |
| DuckDB データ root | ❌ | per-machine 巨大データ・env `BACKCAST_JQUANTS_DUCKDB_ROOT` で外部解決（ADR-0006 規約踏襲）|
| VC++ Redistributable installer | ❌ | uv 同梱 DLL + `AddDllDirectory` で代替（admin 権限要件回避）|

### 2.3 配置の選択（StreamingAssets 基準）

ADR-0002 が「基準は StreamingAssets、sidecar は保険」と既決。本 issue は ADR-0002 既定どおり StreamingAssets を採用。Sidecar（exe 隣 dir）は本 issue で導入しない:

- desktop standalone の StreamingAssets は `<exe>_Data/StreamingAssets/` に実ファイル展開され、size / 起動コピー / hot-swap 容易性のいずれも sidecar との差は無い（ADR-0002 §Consequences 既述）。
- Hot-swap が要件として明確化されていない（power user は `<exe>_Data/StreamingAssets/PythonRuntime/` を直接差し替え可）。
- Sidecar 採用は ADR-0002 自己保護条項により新規 ADR を要し、driver 無しでは筋が悪い。

### 2.4 Locator 改修（`Assets/Scripts/S1Spike/PythonRuntimeLocator.cs`）

- **build 分岐に OS split を追加**: Windows = `cpython/python313.dll` + `python/.venv/Lib/site-packages` / Mac = `cpython/lib/libpython3.13.dylib` + `python/.venv/lib/python3.13/site-packages`。verification gate は Windows のみ（cutover #5 が「本番 = Windows」）。
- ⚠️ **OBSOLETE（#122・2026-06-24）**: 旧「`TTWR_PYTHON_BIN` env を `ConfigureBeforeInitialize()` で set」項。login subprocess と `_resolve_python_executable()` は #122/findings 0093 で in-process tkinter 化に伴い撤去され、`TTWR_PYTHON_BIN` の Python 読者は消滅した（C# producer も `PythonRuntimeLocator.cs` から撤去）。本項は historical。
- **Windows loader hygiene（Editor + Player）**: uv 同梱 `vcruntime140.dll` / `msvcp140.dll` が transitive `.pyd` load から見える。VC++ Redistributable 事前 install を**前提から外せる**（新規 Win10 LTSC / 領域制限環境）。**Editor と Player で API を分割**:
  - **Player**: `SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS)` + `AddDllDirectory(_pythonHome)`（modern・process-wide で PATH-based DLL search を disable=shipped self-contained app では secure default）。
  - **Editor**: `SetDllDirectory(_pythonHome)`（legacy・cpython/ を System32 の前に挿入するが **PATH search は preserve**）。Editor は third-party plugin（analytics SDK / profiler / VCS integration 等）が lazy LoadLibrary で PATH 依存している可能性があり、modern API の process-wide PATH disable が silent breakage を起こすリスクを避ける。Single-dir 制限は問題なし（cpython/ のみ追加すれば足りる）。

  この API split は code-review(simplify) round-2 の MEDIUM finding（"SetDefaultDllDirectories は process-wide で Editor の future plugin に影響"）への対応。trade-off は明示し、editor 側の DLL load 失敗が出た場合は当該 plugin の DLL dir に対し追加で `AddDllDirectory` を呼ぶ等の override 余地を残す。
- **`runtime-manifest.json` sanity assert**: 欠損 path を hard fail に。silent ImportError を防ぐ。

### 2.5 Build post-process（`Assets/Editor/BackcastShippableBuild.cs`）

`IPostprocessBuildWithReport` で:

1. `python/engine/` → `runtime/python/engine/` を copy（`__pycache__` 除外）
2. `python/.venv/Lib/site-packages/` → `runtime/python/.venv/Lib/site-packages/` を copy（`__pycache__` 除外）
3. `python/.venv/pyvenv.cfg` の `home=` を読んで uv CPython root を解決 → `runtime/cpython/` へ copy
4. **copy 先の `pyvenv.cfg` を DELETE**（Locator が単一 SoT）
5. `python -m compileall --invalidation-mode unchecked-hash -o 0 -o 1 -j 0 -q` を両 tree に実行
6. `runtime-manifest.json` を書き出し

#### 2.5.1 `unchecked-hash` の必須性

`compileall` の default は timestamp validation。post-process のファイル copy は `.py` の mtime を rewrite する → ship した `.pyc` が起動時に **stale 扱い**され毎回 cold compile → pre-warm が無効化。`--invalidation-mode unchecked-hash` は hash で valid 判定し immutable deploy 前提と整合する。

#### 2.5.2 `-O 2` を使わない理由

`-O 2` は `__doc__` を strip し、`inspect`/`pydoc` 系を見るライブラリ（特に nautilus 系 oracle テストや一部 marimo 内省処理）で壊れるリスク。`-o 0 -o 1` の両 level のみ ship（CPython 起動 flag に依存しないため両方持つ）。

### 2.6 Executor orphan-absence assert（build leg）

`ExecutorOrphanAbsenceAssert.AssertInProcParity()` を `WorkspaceEngineHost.InitializePython()` の `PythonEngine.BeginAllowThreads()` 直後・`!Application.isEditor` のときだけ実行:

- `os.getpid() == HOST_PID` を GIL 配下で取得し比較
- `multiprocessing.active_children() == []` を assert

**scope**: executor in-proc parity の build leg literal-text 実証（ADR-0001 d3 が守る範囲）。**non-executor の login subprocess（findings 0016 / `engine.live.login_dialog_runner`）は #82 で別 ADR-0013 起案を伴う category 拡張として処理**——ADR-0001 d3 は executor を Job Object 無しに済ませる設計声明で、login subprocess は host crash 時の hygiene 問題ではあっても safety invariant 違反ではない（短命・発注権限無し・"実弾を出し続ける" failure mode 不該当）。

## 3. 却下した代案（grill）

### 3.1 `pyvenv.cfg` を相対 `home=` に rewrite

CPython 3.13 の `getpath.c` は `pyvenv.cfg` の `home=` を venv ディレクトリからの**相対 join をしない**（PEP 405 の慣行は絶対パス）。相対 `home=` に書き換えると `<venv>/Scripts/python.exe` 直起動経路が implementation-defined になり 3.14 で壊れ得る。Locator しか `pyvenv.cfg` を読まないなら**ファイル経由する indirection 自体が冗長** → delete が正解。

### 3.2 post-process で venv を `uv pip install --target` で再構築

dev 機状態依存を 0 にする魅力はあるが、artifact 品質は上がらない（uv の wheel cache は決定的）。一方で post-process が **network 要求** を抱えオフライン build 不能・local build が毎回数分遅延。dev-machine 独立性は **CI の pre-build job** で venv 化する方が筋（issue #33 スコープ外）。

### 3.3 `#33` 内で Job Object を入れる

Owner が grill 中に walk-back。ADR-0001 d3 を literal に読むと protect 対象は **executor lifetime**（"実弾を出し続けるプロセス" の orphan 不在）であり、login subprocess は外側のカテゴリ。`#33` 内に Job Object を捻じ込むと ADR-0001 自己保護条項に抵触し**新 ADR を本 issue で起こす責任が発生**して scope 暴発。→ #82 にスピンオフし ADR-0013 起案を伴う category 拡張として扱う。

### 3.4 Sidecar dir（exe 隣 `PythonRuntime/`）

ADR-0002 が StreamingAssets を default にしており、hot-swap も `<exe>_Data/StreamingAssets/` 直差しで成立。Sidecar 採用は ADR-0002 supersede（新 ADR）を要し、driver 無しでは手続き不適切。

### 3.5 VC++ Redistributable を installer 同梱

uv 同梱 `vcruntime140.dll` + `AddDllDirectory` で代替成立。Redist install は admin 権限要件・冪等冗長・portable install と非整合。

## 4. AC 達成状況（実装直後・HITL 未走）

- ✅ **Code 側完全**: post-process + Locator + 起動 assert + manifest sanity・Editor 機能の劣化無し（`UnityPlayer` 内 P/Invoke は `WindowsPlayer` 限定 guard 付き）。
- ⏳ **HITL Windows 検証**: `Tools > Backcast > Build Shippable (Windows64)` → `<build>/windows64/backcast.exe` 起動 → `<exe>_Data/StreamingAssets/PythonRuntime/` layout 確認 → Replay 完走 → executor probe assert pass の owner 視認待ち。

## 5. 再現手順

### Windows standalone build

```pwsh
# Repo root（python/.venv が存在する dev 機）
& "C:\Path\To\Unity.exe" `
    -batchmode -nographics -quit `
    -projectPath "C:\Users\sasai\Documents\backcast" `
    -buildTarget StandaloneWindows64 `
    -executeMethod BackcastShippableBuild.BuildWindows64 `
    -logFile "build\windows64-build.log"
```

または Unity Editor の `Tools > Backcast > Build Shippable (Windows64)` をクリック。
`build/windows64/backcast.exe` が生成され、`backcast_Data/StreamingAssets/PythonRuntime/` 配下に bundle が展開される。

### exe 単独実行

```pwsh
# DuckDB データ root を env で渡す（ADR-0006）
$env:BACKCAST_JQUANTS_DUCKDB_ROOT = "D:\StockData\jp"
& "build\windows64\backcast.exe"
```

起動 log で:
- `[PythonRuntimeLocator]` 系の error が無いこと
- `[ExecutorOrphanAbsenceAssert]` の throw が無いこと
- Replay run が完走し status/positions/orders/chart が更新されること

## 6. 未消化・射程外

- **Mac standalone の実 build 検証** — code は両 OS 用に書いたが、verification gate は Windows のみ（cutover #5 が「本番 = Windows」と確定済み）。
- **Login subprocess hygiene** — #82 + ADR-0013 起案で別 category として扱う。
- **DuckDB データ配布** — per-machine の外付け / Synology 配置を前提とし、env で外部解決。配布手順は cutover #5 の運用 doc 側。
- **CI 自動化** — Windows runner で `BuildWindows64` を回す GitHub Actions は本 issue では立ち上げない（手元 dev build → HITL → owner 視認のループで初回 cutover ゲートを通す方針）。
- **オフライン install 検証** — 本 build が真に self-contained か（system Python / Anaconda / Redist 完全不在の clean Win10 で動くか）の最終確認は HITL release-gate で実施。

## 7. ADR 関係

- **ADR-0002 は不変**（自己保護条項）。本 ADR の方針「基準は StreamingAssets / runtime 解決は 1 箇所」を踏襲し、下位の実装事実（bundle layout / `pyvenv.cfg` delete / `compileall` flag / `AddDllDirectory` / manifest）を本 findings へ記録。
- **ADR-0001 d3 は不変**。本 issue は executor in-proc parity を build leg literal-text で実証する probe を追加するのみで、d3 の文言・範囲は変えない。Non-executor subprocess の category 拡張は #82 / ADR-0013 で別途。
- **CONTEXT.md L369（orphan-absence の `_Avoid_: Job Object 不要`）は不変**。本 issue では編集しない（narrow 化は #82 のスコープ）。
