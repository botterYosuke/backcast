# findings 0074 — #95 Phase 5 設計の木（B3 `bt.step()`・reset/idempotency・NoScenarioBacktester）

方針: [ADR-0016](../adr/0016-notebook-equals-backtest-per-cell-run.md)（per-cell RUN を strategy 実行エントリーとし notebook = backtest に一本化）。
Phase 1 設計の木: [findings 0070](./0070-notebook-equals-backtest-grill.md)。
Phase 2 土台: [findings 0071](./0071-notebook-foundation-per-cell-run.md)。
Phase 3 `bt` ハンドル: [findings 0072](./0072-issue95-phase3-bt-handle-kernelstepper.md)。
Phase 4 B2 `bt.replay()`: [findings 0073](./0073-issue95-phase4-b2-replay.md)。

本 findings は **#95 Phase 5（#98 を吸収）** = **B3 `bt.step()`** の `/grill-with-docs` ＋ `behavior-to-e2e` セッション 2026-06-20 で確定した下位決定を、会話で消えないように固定する。ADR-0016 / ADR-0012 / ADR-0013 / ADR-0006 / ADR-0007 は immutable（書き戻さない＝自己保護条項）。実装事実は本 findings に固定し ADR を「方針」として参照する。

base ブランチ: `feat/#95-phase5`（Phase 3 完了点 `c352b53` 起点、Phase 4 を `origin/main` 経由で `b56a87a` まで取り込み済み）。

---

## 0. Phase 5 が出荷するもの（Phase 4 finding §5「Phase 5 委譲」の履行）

Phase 4 は `bt.replay()` = 「**毎 press fresh bt**」（各 replay = 0→end atomic transaction）で B2 を出荷した。step は forward-only single-use の Phase 3 stepper を流用したまま、**B3 の reset/idempotency 規律は Phase 5 委譲**とした（Phase 4 finding §5）。Phase 5 はその委譲を履行する:

1. **`bt.step()` の press 跨ぎ persistence**: `bar = bt.step()` cell を 2 回押すと pointer が 0→1→2 と進む（cell 著者の意図）。
2. **scenario commit reset**: 別の scenario が commit されたら cache を invalidate し新 bt を構築（pointer は 0 に戻る）。
3. **terminal idempotency**: step が `None` を返した（END / STOPPED）後、finalize → cache 破棄。次の press は新 bt を build（pointer 再 0）。
4. **NoScenarioBacktester 失敗を guidance で**: scenario 未 commit で `bt.step()` を呼ぶと `NameError` ではなく `RuntimeError("commit the startup panel first...")` が cell output に出る。
5. **C# 側 running guard 不活性化**: step だけが使われる source では ▶→■ トグル / 第二 press reject は発火しない（step は instant ＋ 意図的に stateful）。
6. **allowed footgun の構造的保証**: 上流純粋計算 cell を press → reactive 下流の `bt.step()` cell も走り pointer が進む（findings 0070 F3 owner 決定「carve-out しない」を AFK と pytest で proof）。

---

## 1. 確定した下位決定（P5-1 〜 P5-6 lock）

### P5-1 — step bt 持続キャッシュ（DataEngineBackend 所有）

**問題**: Phase 4 `run_cell` は `bt.replay`/`bt.step` のいずれかが source にあれば `_build_notebook_bt` で **毎 press 新 bt** を作る。`bt.replay` ならそれが望ましい（各 replay = 0 reset）。**`bt.step` には致命的**: cell 著者は「`bar = bt.step()` を 5 回押して 5 bar 進めたい」のに毎 press pointer が 0 にリセットされる。

**採用**: `DataEngineBackend` が **step 専用の bt キャッシュ**を持つ:

```python
self._step_bt: Backtester | None
self._step_bt_run_buffer
self._step_bt_scenario
self._step_bt_key: str | None    # cache key = scenario_json
```

`run_cell` の分岐:

| source | scenario_json | path |
|---|---|---|
| pure compute（`bt.*` なし） | * | Phase 2 経路（bt 注入なし）。**step cache は teardown**（notebook が bt を使わなくなった） |
| `bt.step` のみ | 非空 | **`_acquire_step_bt`**: same key → cache hit reuse／diff key → 旧 bt teardown→新 build→cache |
| `bt.replay` あり（混在含む） | 非空 | Phase 4 経路（fresh bt per press）。**step cache は teardown** |
| `bt.*` あり | 空/null | **NoScenarioBacktester** placeholder を inject（guidance error） |

teardown のタイミング（idempotent）:

- scenario commit が変わった（`_acquire_step_bt` の cache miss）
- source が `bt.step` を使わなくなった（pure compute 化 / 削除）
- step bt が terminal に到達した（`bt.result is not None`）→ finalize → cache 破棄

teardown 内容: `engine.force_stop_replay()`（idempotent）＋ cache fields を None に。RunBuffer の明示的 abort は **しない**（atexit hook が aborted 状態に固定）— Phase 6 で polished teardown lifecycle に置換可。

### P5-2 — terminal handling（forward-only step + finalize on terminal）

`bt.step()` が `None` を返す＝stepper が END / STOPPED に到達＝`bt.result is not None`。`run_cell` の finally で:

```python
elif bt is not None and is_step_bt and bt.result is not None:
    result["run_summary"] = self._finalize_run(run_buffer, scenario)
    self._teardown_step_bt()
```

つまり「step が terminal に到達した press」では Phase 4 replay と同じ finalize 経路を踏む。**次の press は cache miss → 新 bt build → 同じ scenario でも pointer 再 0**。これは ADR-0016 D3「完走後の `bt.step()` は `None`、次の `bt.replay()` はまた 0 から」の **step 解釈**:

- 同 bt の単独 `bt.step()` 連打は forward-only（terminal 後は `None` 固定）。
- terminal 到達後、host が cache をクリアするので、次の press は scenario unchanged でも新 bt → pointer 0。

これは "**replay() は呼ぶたび 0 reset**（Q3 Y2）" を `bt.replay` press = 毎 fresh bt（Phase 4）として履行しつつ、`bt.step` の "**完走後の bt.step() は None**" を terminal-press-finalize で履行する Phase 4/5 分担。owner Q3 = Y2 の意図（ADR D3 文言を実装）と整合（実装は Backtester に factory を持たせるのではなく、DataEngineBackend に cache を持たせる Phase 4 アーキを延長）。

#### owner blessing（2026-06-21）— ADR-0016 D3 の「2 契約」分解で固定

実装レビューで「ADR D3『完走後の `bt.step()` は `None`』の literal 解釈 vs 実装の『次 press で 0 から再走』」を owner に surface し、**後者を blessing**。新 ADR は不要（D3 が許す余地＝「config 単位 bt」「step は stateful」「Phase 5 で reset/idempotency を pin」の中に収まる）。本 §P5-2 の下位決定として固定する。owner が明示した契約分解:

- **object-local / terminal press の契約**: 同一 `bt` インスタンス内では D3 文言どおり terminal 後の `bt.step()` は **`None` 固定**（forward-only）。
- **host lifecycle / cache teardown 後の契約**: notebook の **次 press** は host が terminal finalize 後に cache を破棄済みなので、同じ scenario でも **新しい `bt` を構築し pointer 0 から再走**する。

→ 実装者向け正本（one-liner）: **`terminal press returns None, finalizes, tears down; next press rebuilds (pointer 0)`**。ADR-0016 は無改変（自己保護条項）— 本 finding が「方針: ADR-0016」を参照する下位決定として保持する。

### P5-3 — NoScenarioBacktester（pre-commit fail-closed・Q5）

ADR-0016 D1 の fail-closed 規律を pre-commit に拡張。`engine/strategy_runtime/backtester.py` に新規 class:

```python
class NoScenarioBacktester:
    @staticmethod
    def _raise(method: str) -> None:
        raise RuntimeError(f"bt.{method}(): {_NO_SCENARIO_GUIDANCE}")
    def step(self): self._raise("step")
    def replay(self, *, bars_per_second=None): self._raise("replay")
    def bar(self): self._raise("bar")
    def portfolio(self): self._raise("portfolio")
    def submit_market(self, qty): self._raise("submit_market")
    def _close_open_bar(self): pass        # Phase 2 finally compatibility
    def arm(self): pass                     # NotebookSession._apply_inject uniformity
    def disarm(self): pass
```

guidance: `"no active scenario; commit the startup panel first, then press RUN again"`。

`replay()` は **generator ではなく regular function**（`yield` を含めない）— call time に raise させ、`for bar in bt.replay():` が iteration loop body に入る前に error が出る（観測されない undefined engine state を作らない）。

`_close_open_bar` / `arm` / `disarm` は no-op = Phase 2 finally と `NotebookSession._apply_inject` の uniform call が無事故。

`run_cell` で inject:
```python
elif uses_bt and not scenario_json:
    from engine.strategy_runtime.backtester import NoScenarioBacktester
    inject = {"bt": NoScenarioBacktester()}
```

### P5-4 — C# step は running guard / ▶→■ 対象外（P4-3/4 の subset 排除）

`NotebookRunController.RunCell` の Phase 4 実装は `bt.replay || bt.step` で running guard を発火させていた:

```csharp
bool drivesBacktest = source.Contains("bt.replay") || source.Contains("bt.step");
...
if (drivesBacktest && !string.IsNullOrEmpty(scenarioJson))
{
    _btRunActive = true;
    _onRunningChanged?.Invoke(regionId, true);   // ▶ → ■
}
```

これは **step に対して致命的**:
- step は **instant** な single-bar op で「in-flight」状態が事実上存在しない（次 press までの drain ですぐ終わる）。
- step は **意図的に stateful**（cell を「ボタン」のように使う・ADR-0016 D3）— 「次の press」を guard で reject すると cell 著者の意図と直接矛盾。

採用変更:
```csharp
bool drivesReplay = source.Contains("bt.replay");
bool drivesStep = source.Contains("bt.step");
bool drivesBacktest = drivesReplay || drivesStep;
...
if (drivesReplay && !string.IsNullOrEmpty(scenarioJson))   // Phase 5: step を除外
{
    _btRunActive = true;
    _onRunningChanged?.Invoke(regionId, true);
}
```

scenario JSON の取得は両方で行う（step も scenario が必要）。running guard / glyph トグルだけが `drivesReplay` 条件に細分化される。

### P5-5 — AFK probe set（Section15 / STRATEGY-24/25/26）

Phase 4 が Section14 / STRATEGY-21/22/23（bt.replay control）を取った後、Phase 5 は Section15 / STRATEGY-24/25/26 を起こす:

| probe | pin する挙動 | RED→GREEN litmus |
|---|---|---|
| STRATEGY-24 | step press → running guard / ▶→■ を活性化させない | `drivesReplay` 判定を `drivesBacktest` に戻すと guard が立ち RED（runningEvents 非空、glyph=="■"） |
| STRATEGY-25 | step 連打が executor まで届き counter が累加（press 跨ぎ持続の C# 側可観測） | guard が誤って活性化すると 2nd press が reject されて `exec.Calls==1` で RED |
| STRATEGY-26 | scenario 未 commit + bt.step press → guidance text が cell output に出る | provider が `""` の時に backend が NoScenarioBacktester を inject する分岐を消すと cell output が空 / NameError で RED |

実装は **Python-FREE stub executor**（`_StepExecutor`）を `NotebookRunLane` に inject。stub は scenario JSON が空なら guidance を、非空ならステップカウンタを返す。**real bt persistence / cache logic は Python pytest が正本**（`test_notebook_step_afk.py`）。

### P5-6 — Python pytest gates

| 物 | 内容 | gate ファイル |
|---|---|---|
| NoScenarioBacktester fail-closed | step/replay/bar/portfolio/submit_market が全て guidance RuntimeError を raise。`_close_open_bar` / `arm` / `disarm` は no-op | `test_backtester_phase5.py` |
| step bt 持続 cache | 同 scenario の 2 連打で `backend._step_bt` が同一インスタンス（cache hit）、chart に 2 bar が積まれる | `test_notebook_step_afk.py` |
| scenario commit reset | scenario_json が変わる press で `backend._step_bt` が別インスタンスへ（cache miss → rebuild） | `test_notebook_step_afk.py` |
| terminal finalize + rebuild | N_BARS+1 press で `run_summary` が出て `_step_bt is None`・engine state == "IDLE"・次の press で再 build される | `test_notebook_step_afk.py` |
| scenario unset で guidance | scenario=None の press で cell output に `RuntimeError: ...commit the startup panel...` が出る・engine 不変 | `test_notebook_step_afk.py` |
| source pivot teardown | `bt.step` を含む source の後で pure compute source を press すると cache が破棄される | `test_notebook_step_afk.py` |

---

## 2. owner HITL（2026-06-20 grill）— Q1〜Q5 の lock 経緯

Phase 4 が landed する前の grill セッションで Q1-Q5 を確定したが、Phase 4 が並行で大半を実装したため Q1/Q2 の seam 形は Phase 4 アーキ（scenario JSON を run_cell に同梱）に吸収された。残った Phase 5 固有の意味論決定は Q3/Q4/Q5:

| Q | owner 決定 | 実装着地 |
|---|---|---|
| Q3 | **Y2**: `bt.replay()` は呼ばれるたび 0 reset 再走（ADR D3 文言通り） | Phase 4「毎 press fresh bt」で履行（同観察等価）|
| Q4 | **(A)** `StrategyEditorNotebookE2ERunner` Section に追加（既存 Surface 台本の延長） | Section15 を追加 |
| Q5 | **(b)** NoScenarioBacktester を常時 inject、未 commit は fail-closed `RuntimeError` with guidance | `NoScenarioBacktester` 追加・`run_cell` で inject |

Phase 5 が Phase 4 完了後に着手したため、Q1/Q2 で議論した「`set_active_scenario_json` 明示 commit seam」は Phase 4 の「`scenario_json` を run_cell に同梱」設計を尊重して採用しなかった（実装簡素化）。step persistence は backend 側 cache で実現＝設計の core は Q3 lock に集約。

---

## 3. allowed footgun の明示記録（findings 0070 F3 履行）

ADR-0016 D3 / findings 0070 F3 owner 決定:

> **下流も普通に走ってよい。プログラム側に「bt cell を伝播から除外」等の余計な仕組みを作らない。仕組みはシンプルに保ち、ユーザーが注意する。**

これは **B3 step cell の意図的 stateful 性**（cell を「ボタン」のように使う）と直接対立する footgun 源泉:

- 上流 `threshold = 1.05` cell の RUN → reactive 下流の `bar = bt.step()` cell が再評価 → **意図せず 1 bar 進む**

Phase 5 はこれを **AFK では捉えず**（reactive cascade の正しさは marimo の領分）、**Python pytest 側で再評価が走ることを構造的に proof する**: 既存 Phase 2 `test_notebook_interactive_run.py` の `test_downstream_recomputes_and_independent_does_not` が autorun カスケードを assert している＝同じ機構が bt.step() cell でも働くことは保証されている（特別な carve-out が無いことを構造で証明）。

ユーザー教育的注意:
- step cell は cell-as-button モデルで、上流 cell の編集 / RUN によって意図せず進むことがある
- step を「冪等な reactive evaluation の世界」に置きたい場合は、cell に push button や別の trigger 機構を author が組む（Phase 6 で affordance を検討）

---

## 4. Phase 5 done-gate

1. **既存 #24 / golden 系 green**（Phase 4 / Phase 3 不退行＝主 gate）
2. **Phase 4 gate green**（`test_backtester_phase4` / `test_notebook_replay_afk` / `test_notebook_interactive_run`）
3. **NoScenarioBacktester fail-closed**（`test_backtester_phase5` 7 件）
4. **step bt 持続 cache + scenario reset + terminal finalize + pre-commit guidance + source pivot teardown**（`test_notebook_step_afk` 5 件）
5. **offline import-purity 不変**（`test_strategy_runtime_offline` — backtester に NoScenarioBacktester を追加しても marimo-free）
6. **C# compile 0 error**（NotebookRunController の `drivesReplay` 細分化）
7. **AFK STRATEGY-24/25/26 GREEN**（`StrategyEditorNotebookE2ERunner` Section15）
8. **E2E-INDEX 更新**（STRATEGY-01..26 / 行数 26）
9. **CONTEXT.md `bt` glossary 加筆**（Phase 5 step lifecycle / NoScenarioBacktester / scenario commit reset）
10. **findings 0074 landed**（本 finding）
11. **`code-review(simplify)` Medium+ 0**（CLAUDE.md 必須・`/pair-relay` で潰す）＋ `post-impl-skill-update`
12. **issue #98 を pointer comment で close**

---

## 5. Phase 5 範囲外（Phase 6 / 将来 ADR に委譲）

- **per-cell idle/running/stale 表示**: ⏹ トグルは Phase 4 で前倒し済み、stale 表示と block popup は Phase 6（D11 / 0073 §2 申し送り）
- **rich output**（`mo.md` / 表 / 図 / matplotlib）: Phase 6
- **title-bar Run / global ▶ Run の formal 撤去**: Phase 6 sunset（ADR-0016 D2/D4）
- **cross-instrument 発注**（`bt.submit_market(qty, instrument="...")`）: 将来 additive ADR
- **step cell の "ボタンモード" 専用 affordance**（reactive cascade からの除外）: 将来 ADR（owner が footgun を「allow」した決定との衝突）

これらは ADR-0016 の方針下で各 Phase の findings に固定する（ADR は書き戻さない＝自己保護条項）。

---

## 6. 実装着地（`feat/#95-phase5`）

P5-1〜P5-6 の設計どおり実装。

### landed したコード

| 物 | 場所 | 内容 |
|---|---|---|
| `NoScenarioBacktester` | `engine/strategy_runtime/backtester.py` 末尾に追加 | step/replay/bar/portfolio/submit_market が guidance RuntimeError、`_close_open_bar` / `arm` / `disarm` は no-op |
| step bt cache | `engine/_backend_impl.py` `DataEngineBackend.__init__` ＋ `run_cell` ＋ `_acquire_step_bt` / `_teardown_step_bt` | `_step_bt` / `_step_bt_run_buffer` / `_step_bt_scenario` / `_step_bt_key`。step-only source で cache hit/miss/teardown を制御 |
| pre-commit inject | `_backend_impl.run_cell` の `elif uses_bt and not scenario_json` 分岐 | NoScenarioBacktester placeholder を inject |
| running guard 細分化 | `Assets/Scripts/StrategyEditor/NotebookRunController.cs` `RunCell` | `drivesReplay` だけが `_btRunActive=true` ＆ `onRunningChanged` をトリガ。`drivesStep` は scenario JSON 取得のみ |
| AFK Section15 | `Assets/Tests/E2E/Editor/StrategyEditorNotebookE2ERunner.cs` ＋ `.md` | STRATEGY-24/25/26 を `_StepExecutor` stub で gate |
| E2E-INDEX 更新 | `Assets/Tests/E2E/Editor/E2E-INDEX.md` | STRATEGY-01..26 / 行数 26 / 自動(E2E済) 22 |
| pytest | `python/tests/test_backtester_phase5.py`（7 件）／ `python/tests/test_notebook_step_afk.py`（5 件） | NoScenarioBacktester + step e2e |
| CONTEXT 加筆 | `CONTEXT.md` `bt` glossary entry | Phase 5 注記（step lifecycle・terminal idempotency・scenario commit reset・NoScenarioBacktester） |

### done-gate 結果

1. **既存 #24 / golden 系 green**: `test_kernel_golden_cpython` PASS（Phase 5 が触らない golden path の不退行）。
2. **Phase 4 gate green**: `test_backtester_phase4`（10）/ `test_notebook_replay_afk`（5）/ `test_notebook_interactive_run`（11）全件 PASS。
3. **NoScenarioBacktester fail-closed**: `test_backtester_phase5` 7 件 PASS（step/replay/bar/portfolio/submit_market が guidance RuntimeError・iteration ではなく call time に raise・`_close_open_bar`/`arm`/`disarm` no-op）。
4. **step bt cache + scenario reset + terminal + pre-commit + source pivot**: `test_notebook_step_afk` 5 件 PASS（cache 持続 / scenario 変更 rebuild / terminal finalize→rebuild / scenario unset guidance / pure-compute pivot teardown）。
5. **offline import-purity 不変**: `test_strategy_runtime_offline` PASS（`NoScenarioBacktester` を追加しても backtester module は marimo-free）。
6. **C# compile 0 error**: AFK 実走で `error CS\d+` 0 件確認。
7. **AFK STRATEGY-24/25/26 GREEN**: 実走ログに `[E2E STRATEGY NOTEBOOK PASS] ... + #95 Phase 5 bt.step() persistence (STRATEGY-24 step press does NOT activate ▶→■ / running guard; STRATEGY-25 same-scenario re-press reuses the cached executor signal (pointer persists); STRATEGY-26 scenario-unset bt.step cell surfaces the guidance error in cell output) — Unity-owned, ADR-0003/0013 capability parity, under Unity Mono` ＋ `Found no leaked weakptrs.` 確認（recompile-skip 罠なしの 1st run で PASS）。
8. **E2E-INDEX 更新**: STRATEGY-01..26 / 行数 26 / 自動(E2E済) 22 へ。`#95 Phase 4: STRATEGY-21/22/23 ... #95 Phase 5: STRATEGY-24/25/26 ...` 注記追加。
9. **CONTEXT.md `bt` glossary 加筆**: 「実装着地（#95 Phase 5 #98・findings 0074）」段落を Phase 4 段落の次に追加。step lifecycle persistence / NoScenarioBacktester / scenario commit reset / terminal finalize / running guard 細分化を明文化。
10. **findings 0074 landed**: 本 finding（Phase 5 設計の木＋実装着地）。
11. **`code-review(simplify)` Medium+ 0**: high-effort review 5 angles で 5 件の Medium 検出→直接修正済み:
    - (a) **engine LOADED-leak on step bt build failure**: `_acquire_step_bt` の except path で `_teardown_step_bt()` が `_step_bt is None` で early-return し `force_stop_replay()` を呼ばないため engine が LOADED で stuck → run_cell の except に `self.engine.force_stop_replay()` を明示追加（replay path との parity 復元）。
    - (b) **engine LOADED-leak on substring false-positive**: `"bt.step" in source` がコメント / 文字列リテラル match → cache built + engine LOADED → cell never drives → finally で teardown されず engine stuck。`_acquire_step_bt` に `cache_miss` フラグを追加し、finally で「step path AND cache_miss AND not was_driven」→ `_teardown_step_bt()` を追加（cache HIT で undriven は user の conditional step なので preserve、cache MISS で undriven のみ teardown）。
    - (c) **open-bar leak on cell raise**: cell が `bt.step()` で bar を開いた後に raise すると、submit_market された order が次 press の bar close に bleeding（findings 0072 §Q2 contract 違反）→ run_cell の finally 冒頭で unconditional に `bt._close_open_bar()` を呼ぶ（idempotent、NoScenarioBacktester も対応）。
    - (d) **`NoScenarioBacktester._raise` not typed `-> NoReturn`**: 全 method に `return None` / `raise AssertionError("unreachable")` の dead trailer がコピペで増殖していた → `_raise` を `NoReturn` annotation に変更し全 trailer を削除（noise -5 行）。
    - (e) **CONTEXT.md `bt` glossary line 400 contradiction**: 「same config を再 commit すると破棄＋作り直し」が Phase 5 cache-hit-reuse と矛盾 → 「**異なる scenario** の re-commit で破棄＋作り直し（同 scenario は cache hit reuse＝step pointer は維持）」に更新。

    回帰 gate として `test_notebook_step_afk.py` に 3 件追加: `test_substring_false_positive_does_not_strand_engine_in_loaded` / `test_open_bar_is_closed_in_finally_when_cell_raises_after_step` / `test_step_bt_build_failure_resets_engine_to_idle`。3 件とも修正前 RED → 修正後 GREEN を実証（fix 適用後の pytest 8/8 PASS）。

12. **issue #98 close**: 本 finding + 実装 commit + ADR-0016 を pointer に close（commit 後）。

pytest summary: 55 passed（offline 1 + kernel golden 1 + Phase 3 12 + Phase 4 10 + Phase 5 7 + notebook 11 + notebook_replay_afk 5 + notebook_step_afk 8 = 55）。AFK GREEN（2 回実走）。

### 範囲外として記録（finding に保留）

- **mixed source `bt.replay` + `bt.step` で step persistence が失われる**: source 全体に対する substring 判定なので、cell 0 が `bt.replay` で cell 1 が `bt.step` の notebook を press すると `uses_replay=True` で `_acquire_step_bt` を bypass する。fix には pressed cell 単位の detection（C# が pressed cell body を別途送る or Python 側で marimo source を per-cell parse）が必要で Phase 5 scope を超える。**回避策**: notebook を別ファイルに分割して step と replay を共存させない。Phase 6 で pressed-cell-aware detection を検討。
- **C# `drivesReplay` substring match on comment**: 同様に `bt.replay` をコメントに含む step cell で running guard が誤発火。Phase 5 の C# 側は Phase 4 のパターンを保持しており fix には cross-language の detection 共有が必要。Phase 6 sunset 時に整理。
- **scenario_json cache key as raw string**: JSON key ordering で同 scenario が異 key として cache miss する可能性。C# 側の Newtonsoft JObject serialize は安定なので production では発生確率低、Phase 6 で parse-then-compare に格上げ可能。
