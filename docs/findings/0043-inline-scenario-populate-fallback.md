# findings 0043 — 本線 Populate に inline-.py SCENARIO fallback を配線（sidecar 不在で universe 空 → LiveAuto 不可を封鎖）

> **後続更新（#78 / findings 0044, 2026-06-17）**: この inline-fallback 機構（`Populate: ReadScenario ?? fallback`）は
> 不変だが、**seed の path 源が env 既定 `_strategyFile` から「ロード済み editor の `.py`」へ再ホーム**された。
> seed は `ResolvePaths`（Awake）ではなく `ApplyLayout` 末尾の `SeedScenarioFromEditor` で走る。fresh install は
> editor 未バインド＝universe 空＝Run 封鎖（#78「未ロード→走らない」）で、#66 の元動機（fresh-install で空にしない）は
> #78 に吸収される。詳細は [findings 0044](0044-wysiwyr-run-reads-editor.md)。

issue #66（bug）。元は findings 0027 §3 (d)（cutover slice 2 の follow-up・silent-drop 禁止項）。
`grill-with-docs`（2026-06-17）で設計の木を導出。backcast に FLOWS.md は無いため、本 findings が
RED→実装→GREEN→HITL の正本。方針参照: **ADR-0005（scenario sidecar の merge 規律）/ #29（scenario panel）/
#24（golden 2-leg ドクトリン）/ findings 0027（silent-drop 禁止の観測性 2 層）**。

## 0. 症状とコード実態（裏取り済）

- `BackcastWorkspaceRoot.ResolvePaths()`（`:206-211`）が `_scenario.Populate(_strategyFile, DateTime.Now)` を
  **fallback 無し**で呼ぶ。`ScenarioStartupController.Populate(path, today, fallback=null)`（`:46-70`）は
  `ReadScenario(path) ?? fallback` で、sidecar 不在 → `snap=null` → else 枝 → `Universe.ReplaceAll(empty)` ＝ **universe 空**。
- universe 空だと footer LiveAuto ▶ が `BlockedNoInstrument` で no-op。戦略 `.py` の inline `SCENARIO.instruments` は
  mainline Populate では読まれない。
- **既定 fixture `python/spike/fixtures/strategies/kernel_spike_buy_sell.py` の隣に sidecar `.json` は無い**
  → production 既定パスは**現時点で既にこのバグを踏む**（新規 checkout で universe 空）。inline SCENARIO は
  ちょうど 5+1 キー（`schema_version` + `instruments`/`start`/`end`/`granularity`/`initial_cash`）。

## 1. 確定事項（grill 2026-06-17・Q1〜Q4）

### (D1) 読取り方式 = 純 C# inline パーサ（Python-free）。pythonnet は不成立（Q1）
新設 `ScenarioInlineReader.Read(pyPath) → ScenarioSnapshot` で inline `.py` の `SCENARIO` を C# だけで抽出し、
`Populate` の `fallback` に渡す。pythonnet `load_scenario` 案は **3 つの単独 blocker** で却下:
1. **タイミング**: `ResolvePaths`/`Populate` は `Awake` の `:210` で、`InitializePython` の `:194` **より前**。
   populate 時点で pythonnet は存在しない。
2. **所有権**: `InitializePython` は `if (_isOwner)`（`:192`）の中だけ。非所有 root は Python を**永久に立てない**が、
   UI build（startup tile の populate）は所有権非依存で常時走る（`:186-187`）。pythonnet 案は非所有パスを構造的に救えない。
3. **probe-testability**: `ScenarioStartupController`/`ScenarioSidecarStore` は「Python-free・AFK probe が fakes で
   headless 駆動」を設計核に置く。pythonnet 注入は既存 probe 群を壊す。`ScenarioSidecarStore` を Python-free に保つ
   規律（同 store docstring `:47-51`）と同一 seam に乗せる。

### (D2) 抽出範囲 = panel 所有の 5 キーのみ・literal subset（Q1 risk 封じ）
`start`/`end`/`granularity`/`initial_cash`/`instruments` のみ抽出（`ScenarioSnapshot` と同形）。任意ネストの
`strategy_init_kwargs` 等は読まない（パーサ表面積を最小化）。扱う literal subset（fixtures 実態で裏取り）:
- str（**double quote と single quote 両方**）、int（**Python アンダースコア区切り `10_000_000` を含む**）、
  str の list、`True`/`False`/`None`、**trailing comma**。
- `extract()`（`scenario.py:56-104`）が保証する不変条件に乗る: `SCENARIO` は literal `Dict` 確定
  （`DictComp`/非リテラル/多重定義は Python 側が reject 済）。C# は literal subset だけ正しく扱えばよい。

### (D3) 失敗時挙動 = 不在と読めないを区別・throw しない（Q2）
`Read` は **不在・読めない両方で null を返し、決して throw しない**（`ResolvePaths:210` は try/catch 無し＝
throw すると Awake/workspace build が壊れる）。状態は返り値チャネルで区別する:
- API: `ScenarioInlineReader.Read(string pyPath, out ScenarioReadStatus status) → ScenarioSnapshot`
  （`status ∈ {Found, Absent, Unparseable}`）。
- **Absent**（`.py` に `SCENARIO` 代入ノード無し）= 正当な blank/新規戦略 → 静かに null（`Populate` が `SeedDefaults`・
  universe 空）。誤報を出さない（info 止まり）。
- **Unparseable**（`SCENARIO` ノードは在るが C# subset が parse 失敗＝Python なら読める形を含む）= capability gap/
  潜在バグ → null（throw しない）＋ **loud**。ここを黙って null にすると #66 のサイレント no-op が当該戦略で再発し、
  findings 0027「silent drop にしない」に正面から反する。
- 観測性 2 層（findings 0027）: **floor** = `Read` 内で `Debug.LogWarning(path + reason)`（dev に必ず見える）。
  **user-facing** = `ResolvePaths` が `status==Unparseable` を受けて `_menuBarView?.ShowMessage(...)` で menu notice
  （`ShowMessage(msg) => _message = msg` は単なるフィールド代入で `_menuBarView` は scene serialized ref＝Awake 時点で
  存在・OnGUI が後から描画。stash 不要・BuildWorkspace 前呼び出しでも安全）。

### (D4) 配線（Q1）
`ResolvePaths`:
```csharp
var snap = ScenarioInlineReader.Read(_strategyFile, out var status);
_scenario.Populate(_strategyFile, DateTime.Now, snap);
if (status == ScenarioReadStatus.Unparseable)
    _menuBarView?.ShowMessage("strategy SCENARIO unreadable — save a scenario sidecar");
```
sidecar は依然 inline に勝つ（`Populate`: `ReadScenario(path) ?? fallback`）＝ Python `load_scenario` の fallback 順
（sidecar 優先→無ければ .py の inline）と一致。

## 2. 検証（RED 先行・#24 の golden 2-leg を scenario reader へ適用）（Q3/Q4）

repo の golden ドクトリン（`capture_golden.py` / `test_kernel_golden_cpython.py`）に正確に乗せる:
golden は **oracle（SoT）から記録**し consumer 自身の前提から計算しない／capture は explicit-run only・reviewed／
committed golden は frozen fixture／2-leg（staleness guard + reproduce leg）。SoT は run path と同一の Python
`load_scenario`/`extract`。

- **fixture**: literal subset を代表する戦略 `.py`（single/double quote・underscore int cash・list instruments・5 キー）。
  既存 spike fixtures を流用しつつ subset を網羅する代表を選ぶ/足す。
- **Leg A（Python capture + staleness guard・SoT 側）**: explicit capture script が `load_scenario(fixture)` の
  5 キー projection を `scenario_inline_golden.json` に書く（reviewed commit・C# は書かない）。pytest が
  `load_scenario(fixture) == committed golden` を assert（Python 側 drift を検出し golden を honest に保つ）。
- **Leg B（C# probe・cross-language pin・RED の本体）**: `ScenarioStartupProbe` に section 追加。同じ committed golden を
  読み、`ScenarioInlineReader.Read(fixture)` の 5 キー projection が golden と一致するか assert。
  - **RED**: reader 未実装/不一致 → `[... FAIL]`・`UNITY_EXIT=1`・`error CS` 0。
  - **GREEN**: `ScenarioInlineReader` 実装後 → PASS・`UNITY_EXIT=0`。
- **Unparseable-loud 安全網**: golden 集合外の exotic-but-Python-valid な形は golden では拾えない → (D3) の
  Unparseable 経路が黙殺せず loud にする（golden=既知形の一致保証、Unparseable-loud=未知形の安全網。補完関係）。
- **Unity batchmode 判定**: `UNITY_EXIT=0` + ログ `Exiting batchmode successfully` + `error CS` 0
  （`grep -c "error CS"` の 0-match exit-1 落とし穴に注意）。

## 2.1 検証実績（2026-06-17）

- **実装**: `Assets/Scripts/ScenarioStartup/ScenarioInlineReader.cs`（新規・Python literal subset の
  再帰下降パーサ + module-level locator + brace-match）。`BackcastWorkspaceRoot.ResolvePaths` 配線（D4）。
- **fixture**: `python/spike/fixtures/strategies/kernel_spike_buy_sell.py`（既存・double quote・underscore int・
  単一 instrument）+ `python/spike/fixtures/strategies/scenario_inline_subset.py`（新規・single quote・
  multi-instrument list・nested `strategy_init_kwargs`（True/False/None・comma-in-string）・trailing comma）。
- **Leg A（Python・この dev で実走 GREEN）**: `python/tests/capture_scenario_inline_golden.py`（capture・
  explicit-run only）→ `python/tests/golden/scenario_inline_golden.json`（committed）。
  `python/tests/test_scenario_inline_golden.py` で `load_scenario(fixture) == committed golden` を assert＝
  **3 passed**（`uv run pytest`）。collect-only 287 tests・import error 0。
- **Leg B（C#・Unity 6000.4.11f1 実機 batchmode・GREEN）**: `ScenarioStartupProbe.Section10_InlineReaderMatchesGolden`
  （同 golden ↔ `ScenarioInlineReader.Read` の 5 キー一致 + Absent/Unparseable/truncated/docstring/missing 境界）。
  `-executeMethod ScenarioStartupProbe.Run` → **`[SCENARIO STARTUP PASS]`・`UNITY_EXIT=0`・`error CS` 0**
  （probe は `EditorApplication.Exit(0)` で抜けるため `Exiting batchmode` ログ行は出ない＝UNITY_EXIT=0+PASS で判定）。
- **配線回帰ゲート（実機 batchmode・GREEN）**: `BackcastWorkspaceProbe.Section11_InlineScenarioSeedsUniverse`
  （hermetic・`BACKCAST_HITL_STRATEGY` env override で temp .py を指す）= ResolvePaths が inline から universe を seed・
  sidecar が inline に勝つ、を value-assert（#66 の本丸＝配線ゲート）。`-executeMethod BackcastWorkspaceProbe.Run` →
  **`[BACKCAST WORKSPACE PASS] all sections green.`・`UNITY_EXIT=0`・`error CS` 0**（既存 Section 1-10 も回帰なし）。
- **アルゴリズム事前確証**: 実機実走の前に C# パーサのロジックをそのまま Python へ移植した使い捨て検証で両 fixture が
  golden を byte 一致再現・全境界（truncated/docstring/bad-escape 含む）正を確認済（後に Unity 実機で一致確認）。

## 2.2 review 指摘の修正（code-review high・2026-06-17）

並行 finder（correctness/cross-file/python-leg）の Medium を解消:
- **[Med] 切り詰め/不均衡 dict が silent Absent**: locator を tri-state（`NotFound`/`Found`/`Malformed`）化。
  `{` は在るが brace 不均衡 → `Malformed` → `Unparseable`（loud）。#66 の silent no-op 再発を封鎖。
- **[Med] locator が docstring 盲目**: `IsInsideTripleString` を追加し、module docstring 内の
  `SCENARIO = {...}`（prose・ast はノード無し）を Found 扱いしない（→ Absent）。docstring 後の本物の
  SCENARIO は正しく Found。
- **[Med→Low] 未知エスケープ silent 維持**: `\x..`/`\u..` 等を silent 誤値にせず throw → `Unparseable`。
- **[Med] pytest の import 時 golden ロード**: parametrize ソースを `try/except` で遅延化し、golden 欠如時の
  opaque collection 落ちを回避（`test_golden_exists_and_nonempty` が actionable メッセージを担当）。
  併せて `test_fixtures_have_no_sidecar` を追加（sidecar 追加で 2-leg が別ソースに分裂するのを loud に検出）。
- **[Low] probe Section11 の root2/scenario2 null 未チェック**を leg(a) と対称化。
- 強化後の境界（truncated→Unparseable / docstring→Absent）を C# probe Section10 と使い捨て algorithm-port の
  両方で GREEN 確認。REFUTE: triple-quote 値 / `0x`-int / 暗黙連結 → `Unparseable` は D2 の loud 安全網（設計どおり）。

## 3. 非スコープ / follow-up（silent drop にしない）
- (e) sidebar の universe writeback が params 無しの不完全 sidecar を書く件は **#67 で mutate-existing-only 化済**
  （`ScenarioSidecarStore` の `allowCreate` 規律）。本 issue は read 側 fallback の配線のみ。
- pythonnet `load_scenario` を将来の正準にするなら timing/ownership 再設計が前提（本 issue では不要）。
