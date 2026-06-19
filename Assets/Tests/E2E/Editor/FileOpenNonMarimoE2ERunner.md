# FileOpenNonMarimoE2ERunner — 台本（Surface E2E slice / #86 release-gate）

`FileOpenNonMarimoE2ERunner.cs` が自動検証する **File→Open の非 marimo `.py` 1-cell wrap 経路（#86）** の台本。
共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)、設計の正本は
[findings 0054](../../../../docs/findings/0054-file-open-non-marimo-py-wraps-as-1-cell.md)、
集約の実装は [`MarimoNotebookDocument.Open`](../../../../Assets/Scripts/StrategyEditor/MarimoNotebookDocument.cs)。

> **位置づけ**: #86 は「非 marimo `.py`（imperative `Strategy` サブクラス）を File→Open で開いて Run まで通せる」
> ことを保証する release-gate slice。本台本は **その 1 つの不変条件**だけを正本的に gate する細い surface 台本で、
> Strategy Editor サーフェス全体の網羅は [StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) が
> 受け持つ（STRATEGY-16 の generic Open 経路に対して、本台本は #86 の wrap policy をピンポイントで固定する）。

## 対象サーフェス

`MarimoNotebookDocument`（ノート集約 = `IStrategyFileProvider`）の **File→Open 経路**。production の
`PythonnetMarimoSynthesizer.Decompose` は marimo `load_app` ベースで、`app = marimo.App()` を持たない `.py`
（v19_morning.py のような imperative `Strategy` サブクラス）に対して `None` を返す。集約はその null を **fail-soft
abort せず**、ファイル本文を 1 anonymous cell として wrap して bound する（findings 0054 D1）。fail-soft な
LastError 経路は **path/IO エラー専用**に縮退（findings 0054 D4）。

## 対象ユーザー行動

`python/strategies/v19/v19_morning.py`（実在する imperative 戦略の正本）を File→Open で開く →
中央に 1 cell window が出現し本文がそのまま見える → そのままの notebook が **Run-gate を通す**
（`RegistryStrategyFileProvider.TryGetStrategyFile` が当該 path を返す）。Save 後の on-disk は marimo 形式に
書き換えられる（owner 公認の一方向マイグレーション・findings 0054 D2/D3）。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| OPEN-NM-01 | 実 v19_morning.py を File→Open（非 marimo `.py`） | [`MarimoNotebookDocument.cs:149`](../../../../Assets/Scripts/StrategyEditor/MarimoNotebookDocument.cs)（`?? new List<Cell> { NewCell(content, "_", "{}") }`） | `Open` true・`CellCount==1`・`Cells[0].Body == 生本文`・`Name=="_"`・`IsBound && !IsDirty`・`LastError==null` | 実 v19_morning.py を直接読み、wrap 後の body と byte-for-byte 一致を assert（`class V19MorningStrategy` を含むこと＝非 vacuous） | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S10) — 合成 fixture で同等を確認 |
| OPEN-NM-02 | Open 後そのまま Run（Run-gate 解禁） | [`RegistryStrategyFileProvider.cs:30`](../../../../Assets/Scripts/StrategyEditor/RegistryStrategyFileProvider.cs)・[`BackcastWorkspaceRoot.cs:409`](../../../../Assets/Scripts/Live/BackcastWorkspaceRoot.cs)（`Register(NOTEBOOK_ID, _notebook)`） | wrap 直後に `TryGetStrategyFile(out path)` true・`path == v19_morning.py 絶対パス`。cell body 編集後 false（dirty）、SaveAs 後 true | NOTEBOOK_ID で `StrategyProviderRegistry` 登録 → registry-resolved provider が path を返す・dirty/clean 反転を assert | 自動(E2E済) | — |
| OPEN-NM-03 | Save で marimo 形式に一方向マイグレーション | [`MarimoNotebookDocument.cs:87`](../../../../Assets/Scripts/StrategyEditor/MarimoNotebookDocument.cs)（`Save`/`SaveAs`） | wrap 後の `SaveAs(temp)` → 新 path に合成出力・rebind・clean。fresh `MarimoNotebookDocument(synth)`（`FailDecompose=false`）で再 Open → 1 cell・body 一致・name `_` | 一方向マイグレーションが loss-less（body verbatim 保存・再 Open で同一 cell） | 自動(E2E済) | `StrategyEditorNotebookE2ERunner`(S10) round-trip |
| OPEN-NM-04 | path/IO エラーは fail-soft（wrap 化しない） | [`MarimoNotebookDocument.cs:127`](../../../../Assets/Scripts/StrategyEditor/MarimoNotebookDocument.cs)（`Fail("no path"/"not a .py"/"file missing"/...)` ） | `Open("")` / `Open("foo.txt")` / `Open(<不存在 .py>)` で false・`LastError` 設定・buffer 非破壊（CellCount/IsBound 不変） | findings 0054 D4 の縮退範囲を assert（広げ過ぎ防止＝IO エラーを wrap で隠さない） | 自動(E2E済) | — |

> Decompose が null → wrap は **集約の policy**。Python seam（`engine.strategy_runtime.cell_synthesis.decompose_json`）の
> 「非 marimo / broken → None」契約は不変（findings 0054 D5）。`FakeMarimoSynthesizer{FailDecompose=true}` は
> その production null leg を AFK で忠実に再現する seam double。

## 観測点（詳細）

- **OPEN-NM-01（非 vacuous wrap）**: 実 `python/strategies/v19/v19_morning.py` をテストハーネスが**独立に**
  `File.ReadAllText` で読み、その文字列を **`Cells[0].Body` と等値**に assert する。さらに body が
  `class V19MorningStrategy` という signature を**含む**ことも assert（synthesized 空 blob でない＝
  本物のファイルを開いている証跡）。**delete-the-logic litmus**: `MarimoNotebookDocument.Open` の
  `?? new List<Cell> { NewCell(content, "_", "{}") }` を消すと OPEN-NM-01 が `Open() == false` で落ちる
  （旧 fail-soft 動作）。
- **OPEN-NM-02（Run-gate）**: production の Run/Step/LiveAuto は `BackcastWorkspaceRoot.cs:272` の
  `RegistryStrategyFileProvider(_registry, NOTEBOOK_ID)` 越しに path を引く。本台本は同じ NOTEBOOK_ID 文字列を
  使って `StrategyProviderRegistry` を構築し、`MarimoNotebookDocument` を Register したうえで
  `RegistryStrategyFileProvider` から **同じ path** が引けることを assert する。`Cells[0].SetBody` で dirty 化
  → provider false、`SaveAs(temp)` で clean → provider true、の反転で **5 条件（findings 0010 §5）が
  正しく生きている**ことも一緒に固定する。
- **OPEN-NM-03（loss-less migration）**: `SaveAs` で temp パスへ synth 出力 → 別 `MarimoNotebookDocument`
  + `FakeMarimoSynthesizer{FailDecompose=false}` で再 Open → `Cells[0].Body == 原 v19_morning.py 本文`。
  fake synthesizer は marker 経由の reversible blob だが、production `generate_filecontents` の round-trip
  契約（findings 0050）と同じ shape の不変条件を gate する（layer-3 pytest golden が production seam を担う）。
- **OPEN-NM-04（fail-soft 縮退）**: findings 0054 D4 の表（`no path` / `bad path` / `not a .py` / `file missing` /
  `read failed`）のうち、AFK で deterministic に踏める 3 つ（empty / wrong ext / file missing）を assert。
  この縮退は #86 の wrap が「path/IO エラーまで飲み込む方向」に肥大化していないことの guard。

## 自動判定（合格条件）

- ログに `[E2E FILE OPEN NONMARIMO PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、
  `error CS\d+` が 0 件。
- 各 section を 1 つでも落としたら `[E2E FILE OPEN NONMARIMO FAIL] <msg>` で exit 1。
- **delete-the-production-logic litmus**（OPEN-NM-01 が真に non-vacuous であることの保証）:
  `MarimoNotebookDocument.Open:149-150` の `?? new List<Cell> { NewCell(content, "_", "{}") }` を消すと
  Open が false を返し OPEN-NM-01 が「Open returned false on real v19_morning.py」で落ちる。
  逆に `Cells[0].Body` を空文字に差し替えると `class V19MorningStrategy` 包含 assert が落ちる。

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
| `StrategyEditorNotebookE2ERunner` S10 | pure aggregate (`MarimoNotebookDocument`) | 同 section が #86 の合成 fixture を assert。本台本は**実 on-disk v19_morning.py**で同等＋Run-gate まで延伸する正本ゲート。S10 は併存（pure aggregate の細部）。 |
| `StrategyEditorNotebookE2ERunner` S4 | supplyable 5 条件 | OPEN-NM-02 の dirty/clean 反転で同 contract を再 assert（registry 越しに）。 |

## 射程外（別 slice）

- 実 pythonnet `PythonnetMarimoSynthesizer` の `decompose_json` 経路は **layer-3 pytest golden**
  （`python/tests/test_marimo_cell_synthesis_golden.py::test_entry_point_decompose_fail_soft_on_broken_py`）
  が正本。本台本は seam double（`FakeMarimoSynthesizer{FailDecompose=true}`）で C# 集約 policy を gate する。
- 実 `start_engine` 経由の v19_morning.py 実 Run は **HITL**（pythonnet・nautilus_trader engine が必要）。
  本台本の OPEN-NM-02 は production が実 Run に渡す **同じ path** が同じ registry seam から引ける、までを
  AFK で固定する（≒ Run-gate 解禁の release-gate）。
