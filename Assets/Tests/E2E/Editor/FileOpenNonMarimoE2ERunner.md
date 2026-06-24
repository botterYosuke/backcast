# FileOpenNonMarimoE2ERunner — 台本（Surface E2E slice / #113 release-gate）

`FileOpenNonMarimoE2ERunner.cs` が自動検証する **File→Open の「marimo or error」契約（#113）** の台本。
共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)、設計の正本は
[findings 0098](../../../../docs/findings/0098-issue113-open-layer-marimo-only.md)、
集約の実装は [`MarimoNotebookDocument.Open`](../../../../Assets/Scripts/StrategyEditor/MarimoNotebookDocument.cs)。

> **位置づけ**: #113 は findings 0054 §D1 の「非 marimo `.py` を 1-cell 自動 wrap して開く」決定を **反転**し、
> editor を marimo notebook 専用にする slice。本台本は **その入口（Open 層）の不変条件**を正本的に gate する
> 細い surface 台本で、Strategy Editor サーフェス全体の網羅は
> [StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md)（S10）が受け持つ。run/materialize 側の
> marimo 強制（`NOT_A_MARIMO_NOTEBOOK`）は #112（ADR-0025 D4）の `test_marimo_live_guard.py` が正本で、本台本は
> その契約を **Open 層へ前倒し**したことを固定する（「marimo or error」が open〜run で一貫）。

## 対象サーフェス

`MarimoNotebookDocument`（ノート集約 = `IStrategyFileProvider`）の **File→Open 経路**。production の
`PythonnetMarimoSynthesizer.Decompose` は marimo `load_app` ベースで、`app = marimo.App()` を持たない `.py`
（v19_morning.py のような imperative `Strategy` サブクラス）に対しては `decompose_json` が `None` を返し、broken
syntax には `SyntaxError` を伝播させる（findings 0098 / cell_synthesis `raise_syntax_error=True`）。集約はこれを
**1-cell wrap せず明示エラー**にする（非 marimo → `"not a marimo notebook"` / broken → `"syntax error: …"`）。
path/IO エラーは従来どおり fail-soft（それぞれ固有の `LastError`）。

## 対象ユーザー行動

`python/strategies/v19/v19_morning.py`（実在する imperative 戦略）を File→Open で開く → **「marimo notebook では
ありません」と明示エラー**が出て、編集中バッファは一切変わらない。broken syntax の marimo ファイルを開くと
**SyntaxError 由来の別エラー**になる。正常な marimo notebook はそのまま開けて Run-gate を通す。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| OPEN-NM-01 | 実 v19_morning.py を File→Open（非 marimo `.py`） | [`MarimoNotebookDocument.cs`](../../../../Assets/Scripts/StrategyEditor/MarimoNotebookDocument.cs)（`Decompose==null → Fail("not a marimo notebook")`） | `Open` false・`LastError=="not a marimo notebook"`・`!IsBound`・`CurrentPath==null`・buffer 非破壊 | 実 v19_morning.py（`class V19MorningStrategy` 包含で非 vacuous）を開いて拒否されることを assert | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S10) — 合成 fixture で同等 |
| OPEN-NM-02 | broken-syntax の marimo `.py` を File→Open | 同上（`Decompose` が SyntaxError を運ぶ → `Fail("syntax error: …")`） | `Open` false・`LastError` が `"syntax error"` で始まる・非 marimo とは **別文言**・`!IsBound`・buffer 非破壊 | broken-syntax が distinct error になり not-a-marimo と混同されないことを assert（AC#2） | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S10) |
| OPEN-NM-03 | 正常な marimo notebook を File→Open（無回帰）→ そのまま Run | [`RegistryStrategyFileProvider.cs:30`](../../../../Assets/Scripts/StrategyEditor/RegistryStrategyFileProvider.cs)・[`BackcastWorkspaceRoot.cs:409`](../../../../Assets/Scripts/Live/BackcastWorkspaceRoot.cs)（`Register(NOTEBOOK_ID, _notebook)`） | `Open` true・cell/name 復元・`TryGetStrategyFile` true・body 編集→false / Save→true の 5 条件反転 | valid marimo は無回帰で開けて NOTEBOOK_ID で provider が path を返す（AC#4） | 自動(E2E済) | — |
| OPEN-NM-04 | path/IO エラーは fail-soft（marimo-or-error と別文言） | [`MarimoNotebookDocument.cs`](../../../../Assets/Scripts/StrategyEditor/MarimoNotebookDocument.cs)（`Fail("no path"/"not a .py"/"file missing")`） | `Open("")` / `Open("foo.txt")` / `Open(<不存在 .py>)` で false・**固有の** `LastError`・buffer 非破壊 | path/IO エラーが marimo 判定に飲み込まれず固有の理由を保つことを assert | 自動(E2E済) | — |

> Decompose が null/SyntaxError → **明示エラー**は集約の policy。Python seam（`cell_synthesis.decompose_json`）の
> 「非 marimo → None / broken → SyntaxError」契約は findings 0098 の正本（layer-3 pytest
> `test_marimo_cell_synthesis_golden.py::test_entry_point_decompose_non_marimo_returns_none`）。
> `FakeMarimoSynthesizer{FailDecompose=true}` は非 marimo leg を、`{SyntaxErrorDetail=…}` は broken-syntax leg を
> AFK で忠実に再現する seam double。

## 観測点（詳細）

- **OPEN-NM-01（非 vacuous reject）**: 実 `python/strategies/v19/v19_morning.py` をテストハーネスが独立に
  `File.ReadAllText` で読み、`class V19MorningStrategy` を含む（= 本当に非 marimo な imperative 戦略である）ことを
  確認したうえで、`Open` が **false** を返し `LastError=="not a marimo notebook"`、未 bound、buffer 非破壊である
  ことを assert。**delete-the-logic litmus**: `MarimoNotebookDocument.Open` の null 分岐に
  `?? new List<Cell> { new Cell(content, "_", "{}") }` の wrap leg を戻すと `Open()==true` になり OPEN-NM-01 が落ちる。
- **OPEN-NM-02（distinct SyntaxError）**: broken-syntax を `Open` → `LastError` が `"syntax error"` で始まり、
  `"not a marimo notebook"` とは異なることを assert（AC#2 = 無言 wrap で隠さない・非 marimo と混同しない）。
- **OPEN-NM-03（無回帰 + Run-gate）**: 合成した valid marimo（2 cell）を `Open` → 成功・name 復元・
  `RegistryStrategyFileProvider`（production と同一 NOTEBOOK_ID）が path を返す。body 編集→provider false、
  Save→true の 5 条件反転も固定。
- **OPEN-NM-04（fail-soft 縮退）**: empty / wrong ext / file missing が **それぞれ固有の** `LastError` を保ち、
  marimo-or-error の文言に飲み込まれないことを assert（拒否ロジックが IO エラーまで誤ラベル化しない guard）。

## 自動判定（合格条件）

- ログに `[E2E FILE OPEN NONMARIMO PASS] <要約>`、各 section 到達で per-Action-ID タグ
  `[E2E OPEN-NM-01 PASS]` … `[E2E OPEN-NM-04 PASS]`（単一トークン = rollup が拾う）、プロセス exit code 0、
  `error CS\d+` が 0 件。
- いずれかの section を落としたら `[E2E FILE OPEN NONMARIMO FAIL] <msg>` で exit 1（per-id タグはその手前で止まる）。
- **delete-the-production-logic litmus**: 上記 OPEN-NM-01 の wrap-leg 復活で確定的に RED。

## 実行コマンド

```text
<Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
        -executeMethod FileOpenNonMarimoE2ERunner.Run -logFile <log>
# expect: [E2E FILE OPEN NONMARIMO PASS] ... / exit=0
# compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
# Unity ログは UTF-8 なので ripgrep で grep（PowerShell Select-String は取りこぼす）。
```

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `StrategyEditorNotebookE2ERunner` S10 | pure aggregate (`MarimoNotebookDocument`) | 同 section が #113 の合成 fixture（非 marimo reject / broken-syntax distinct / valid 無回帰）を assert。本台本は**実 on-disk v19_morning.py**で OPEN-NM-01 を正本ゲートし、Run-gate まで延伸。 |
| `FileNavGuardE2ERunner` FILEGUARD-06 | 実 root を通した File→Open guard | 非 marimo の dirty File→Open が guard を挟みつつ拒否され buffer 保全（root レベルの #113 配線）。 |

## 射程外（別 slice）

- 実 pythonnet `PythonnetMarimoSynthesizer.decompose_json` 経路は **layer-3 pytest**
  （`test_marimo_cell_synthesis_golden.py`）が正本。本台本は seam double で C# 集約 policy を gate する。
- 実 `start_engine` 経由の Run-time エラー（imperative load の SyntaxError surface）は #112 / findings 0054 D3a の
  run 層契約（`test_marimo_strategy_adapter.py`）が担う。本台本は **Open 層**の拒否だけを固定する。
- 実 workspace での File→Open 目視（メニュー通知に「marimo notebook ではありません」相当が出る）は **HITL**。
