# findings 0072 — #95 Phase 3 設計の木（`bt` ハンドル ＋ `KernelRunner` state-machine 化）

> **採番訂正（2026-06-20）**: 本 finding は当初 0071 として起案・commit `443ca72` で push されたが、別ブランチ `feat/#95` 上で **Phase 2 土台**の commit `ebeac4b` が同じ 0071 を `0071-notebook-foundation-per-cell-run.md` で先に占有していた。merge で 2 ファイル共存になったため、**Phase 2 が先**を尊重し本 finding を **0072 へリネーム**（findings 0070 の「番号重複は前半/後半なら統合・別設計の木なら後発をずらす」規律。Phase 2 / Phase 3 は別設計の木）。

方針: [ADR-0016](../adr/0016-notebook-equals-backtest-per-cell-run.md)（per-cell RUN を strategy 実行エントリーとし notebook = backtest に一本化）。Phase 1 設計の木: [findings 0070](./0070-notebook-equals-backtest-grill.md)。**Phase 2 土台**: [findings 0071](./0071-notebook-foundation-per-cell-run.md)。

本 findings は **#95 Phase 3**（`bt` ハンドル ＋ `KernelRunner` state-machine 化）の **`/grill-with-docs` セッション 2026-06-20** で確定した下位決定（Q1–Q7 の lock）を、会話で消えないように固定する。ADR-0016 / ADR-0012 / ADR-0013 / ADR-0006 は immutable（書き戻さない＝自己保護条項）。実装事実は本 findings に固定し ADR を「方針」として参照する。

別ブランチ `feat/#95-phase3`（origin/main 起点 = `4999e8f`）で Phase 2 と並行に起案したが、grill 開始時点で Phase 2 は未着手の状態だった。Phase 2 は本 grill セッション中に並走で landed（`ebeac4b`）。

---

## 確定した下位決定（Q1–Q7 lock）

### Q1 — `KernelRunner` state-machine 化の refactor 形（**B: `KernelStepper` extract**）

| | 検討案 | 採否 |
|---|---|---|
| A | same class・new method（`KernelRunner._advance_one_bar()`） | **却下**: bt が `KernelRunner` の private を「friend access」で覗く形になる。`KernelRunner` が「一気走 runner」と「対話 step session」を兼ねて lifecycle が濁る |
| **B** | **extract `KernelStepper`**（state machine を独立 class） | **採用**: bt が stepper を **公開 API として消費**（private 結合無し）。lifecycle 明示。pytest が stepper 単体で書ける |
| C | generator-based（`KernelRunner.run_iter() -> Iterator`） | **却下**: `bt.replay()` の generator 表現は adapter 層で必要だが、**core state machine の根を generator にする**と `GeneratorExit` / early break / stop / 最終 bar fill / summary 確定が generator cleanup に寄りすぎる |

`KernelStepper` は `python/engine/kernel/stepper.py` 新設。`KernelRunner.run()` は `KernelStepper` を作って回すだけの薄い wrapper になる。既存 caller（`engine.kernel.runner.KernelRunner`）は無変更。

**Phase 3 は「wrap」より重い**: golden を作る `equity_curve` / `fills` / `last_prices` / `baseline_equity` / `bars` / `stopped_reason` は現状 `runner.py:202-325` のメソッドローカル。stepper extract は**これらを instance state へ hoist する**＝golden 生成コードを触る refactor（findings 0070 F4 補足通り）。

### Q2 — bar lifecycle contract（**γ: auto-close-on-next-step ＋ explicit `_close_open_bar()` hook の両立**）

bar 内シーケンスを 3 段に分割（現 `runner.py:230-303`）:

| 段 | 行 | 内容 |
|---|---|---|
| open | 237 / 241-243 | `push_bar(bar)` ＋ `reference_prices` overlay ＋ `ts_event_ns` ＋ `strategy.on_bar(bar)` |
| close | 246-303 | denial → `on_order`・accepted fill@close → `push_order` / `push_portfolio` / `apply_fill` ・`last_prices` 更新 ・`on_equity` ・rails 後判定 |
| finalize（post-loop） | 305-315 | `on_stop`・summary 計算・`push_run_complete` |

3 案の中で **γ（両立・idempotent）を採用**:

- `bt.step()` は呼ばれるたび **「前 bar が open なら close → 次 bar を open して返す」**（auto-close-previous）
- Phase 2 の per-cell RUN integration は cell 終了 `finally` で `bt._close_open_bar()` を呼ぶ（**idempotent**）

理由:
1. **Phase 3 単体で pytest が走る**（hook なしで `bt.step(); bt.submit_market(...); bt.step()` を直接駆動できる＝α の挙動）。Phase 2 と並行可能の前提（owner 要件）を構造で満たす
2. **Phase 2 が wire したら最も自然**: per-cell RUN finally で hook を叩けば、user が次 cell を実行する前に push が走り Hakoniwa が更新される
3. **α だけ**だと UI 反映が次 step まで遅れる
4. **β（hook-only）だけ**だと Phase 3 が Phase 2 hook に依存し、state machine 単体の確信が弱い

#### `KernelStepper` の公開 contract（3 primitive）

```python
handle = stepper.open_next_bar()   # auto-closes previous if needed
# caller runs body: strategy.on_bar(bar) or bt cell body
stepper.close_current_bar()        # idempotent, may be called by Phase 2 finally
result = stepper.finalize()        # idempotent, only after END/STOPPED
```

`KernelRunner.run()`・`bt.replay()`・`bt.step()` が **同じ primitive を順番違いで叩くだけ**で、golden byte-identical の根拠が code structurally clear。

#### `StepEvent` enum / `StepHandle` dataclass

```python
class StepEvent(Enum):
    BAR_OPEN = "bar_open"   # push_bar 済み・on_bar 相当を呼ぶ番・submit valid
    END = "end"             # 全 bar 終了・finalize 済み・以後 idempotent
    STOPPED = "stopped"     # stop_event で中断・finalize 済み（stopped_reason 有り）

@dataclass(frozen=True)
class StepHandle:
    event: StepEvent
    bar: Bar | None         # BAR_OPEN のときだけ非 None
    reason: str = ""        # STOPPED 時の理由（rails halt の violation.kind 等）
```

**rails halt は `STOPPED` に畳む**（既存 `runner.py:288` の `stopped_reason = violation.kind` と同じ意味）。enum を `RAILS_HALTED` まで増やすと control flow と domain reason が混ざる。理由は `handle.reason` / `RunResult.stopped_reason` で区別。

#### `_close_open_bar()` 名前と placement

`bt._close_open_bar()` — under-the-line public（命名で「Phase 2 wiring 専用」を示唆）。`bt.finalize_cell()` は bt 側が marimo cell lifecycle を知っているように見えて責務が広すぎる。bt が知るべきなのは「**今開いている bar を閉じる**」だけ。idempotent。

### Q3 — multi-instrument universe での implicit-instrument 契約（**a: implicit current bar's instrument**）

ADR-0016 D3 / CONTEXT line 400 が lock している cell-facing API は引数なしの `bt.bar()` / `bt.portfolio()` / `bt.submit_market(qty)`。一方 universe は複数銘柄もありえる（`runner.py:144` `instrument_ids: Optional[list[str]]`・`load_universe_bars` が時間順 merge）。

**採用 a（implicit current bar's instrument）**:

- `bt.bar()` — current / last bar（`bar.instrument_id` 単一）
- `bt.submit_market(qty)` — **open 中の `bar.instrument_id` 宛て**に signed delta 発注
- `bt.portfolio()` — **primary instrument = current/last bar.instrument_id の snapshot**

理由:
1. ADR-0016 D3 の例コード（`pf.avg_price` / `pf.position` の単一値参照）と整合
2. 既存 `cell_api.make_submit_market(ctx, strategy_id, instrument_id)` の延長（make 時固定 → per-bar 動的への自然な拡張）
3. multi-instrument scenario でも「bar-following 戦略」（bar が来た銘柄を売買）が同じ cell で書ける

#### `bt.bar()` / `bt.portfolio()` lifecycle rule

| state | `bt.bar()` | `bt.portfolio()` | `bt.submit_market(qty)` |
|---|---|---|---|
| 未開始（一度も step/replay してない） | `None` | `instrument_id=None` で aggregate snapshot（position == 0・positions == {}・cash/equity/buying_power は初期値） | `ValueError`（fail-closed） |
| BAR_OPEN 中 | current bar | primary = `current_bar.instrument_id` | OK（`ctx.submit_market` 経由） |
| close 後 / 次 step 前 | last bar | primary = `last_bar.instrument_id` | `ValueError` |
| END / STOPPED 後 | 最後の bar | primary = `last_bar.instrument_id` | `ValueError` |

`bt.portfolio()` snapshot は `positions` mapping（全建玉）も同居させ、**観測**は multi-instrument に開いておく（primary は single instrument の便宜値）。

#### cross-instrument 発注

**Phase 3 範囲外**。将来必要なら `bt.submit_market(qty, instrument_id="...")` または `instrument="..."` を additive に足す（ADR/CONTEXT を supersede しない・キーワード引数の additive 拡張）。Phase 3 では足さない＝最小 contract「open bar の銘柄を読む・その銘柄に発注」で固定。

### Q4 — `KernelStepper` の Strategy 引数・`Backtester` 構築 factory・Phase 3 parity gate

#### (a) `KernelStepper(strategy: Strategy | None = None)`

採用案: **既存 `Strategy` ABC（`register/on_start/on_bar/on_stop/on_order`）の語彙を保ったまま、stepper が `None` を受け入れる最小変更**。

却下案: bt 側 `_NoopStrategy` 偽実装（「bt に Strategy 概念はない」設計を曇らせる） / callbacks 化（汎用化過剰）。

**重要な実装上の規律**: `strategy=None` は **user hook（register/on_start/on_bar/on_stop/on_order）を skip するだけ**で、**fills / denials / sink push（push_bar / push_order / push_portfolio / on_equity / push_run_complete）/ portfolio apply は必ず stepper が処理する**。「user hook が無い」≠「注文処理が無い」。

#### (b) `Backtester` 構築 factory（2 入口）

```python
# python/engine/strategy_runtime/backtester.py（marimo-free）
class Backtester:
    def __init__(self, stepper: KernelStepper) -> None: ...      # unit test / direct
    @classmethod
    def from_scenario(
        cls,
        scenario: dict,                                          # validated scenario dict
        *,
        data_root: str | Path,
        push_target=None,
        sink=None,
        rails=None,
        stop_event=None,
    ) -> "Backtester": ...                                       # production wire
    
    # ADR-locked 5 API
    def replay(self, *, bars_per_second: float | None = None) -> Iterator[Bar]: ...
    def step(self) -> Bar | None: ...
    def bar(self) -> Bar | None: ...
    def portfolio(self) -> PortfolioSnapshot: ...
    def submit_market(self, qty: float) -> None: ...
    
    # Phase 2 hook（idempotent・under-the-line public）
    def _close_open_bar(self) -> None: ...
```

`scenario` は **normalized/validated `dict`**（既存 `engine.strategy_runtime.scenario.load_scenario()` が返す形）。ここで `ScenarioConfig` dataclass を新設しない（Phase 3 主目的から横に広がる）。中で `instrument_ids = scenario["instruments"]`、`start/end/granularity/initial_cash` を読む。

#### (c) Phase 3 parity gate（3 系統）

| # | gate | 何を proof |
|---|---|---|
| (α) | **既存 #24 golden green**（refactor 安全網） | `KernelRunner.run()` が `KernelStepper` wrapper 化しても既存 golden / replay tests が byte-identical で通る = stepper 内部 parity |
| (β) | **新規 replay parity** `test_backtester_replay_byte_identical_to_kernel_runner`（仮称） | 同一 trading logic を `Strategy.on_bar` と `for bar in bt.replay()` body に二重実装し `push_target` の JSON list を完全一致 assert = bt façade の新しい価値を直接証明 |
| (γ) | **新規 step-to-end parity** `test_backtester_step_to_end_byte_identical_to_kernel_runner`（仮称） | `while (bar := bt.step()) is not None: ...` で (β) と同じ buffer 一致を assert = findings 0070 F4(A)「step を終端まで押し切れば replay と byte-identical」を構造で pin |

(β)/(γ) は **γ lock の auto-close-on-next-step** を使って Phase 2 不要で直接 pytest できる形にする。

### Q5 — Phase 3 ↔ Phase 4 の scope 境界

| 軸 | 採用 | Phase 3 で書く | Phase 4 で書く |
|---|---|---|---|
| (A) `bars_per_second` pacing | **A1** | API signature だけ固定（`bt.replay(*, bars_per_second: float \| None = None)` 引数を受理）。値は store するが sleep は **入れない**（no-op）。`_REPLAY_BAR_INTERVAL_SEC` も触らない | per-bar `sleep(1/N)` 挿入・`_REPLAY_BAR_INTERVAL_SEC` 撤去・GIL spike 実機 reconfirm（findings 0070 F6 ladder） |
| (B) `stop_event` | **B1** | `KernelStepper(stop_event=...)` 引数で受け取り `open_next_bar()` 冒頭で check（set 済みなら `STOPPED` / `reason="stopped"` を返す）。`Backtester.from_scenario(stop_event=...)` で受け渡し可能。**新 stop UI は作らない**。pytest で「pre-set stop_event なら bar を流さず STOPPED」を pin | host から実 set する経路（C# worker thread / footer 起動）、AFK probe |
| (C) running guard | **C1** | guard 不在。`Backtester` は single-threaded 前提で実装。pytest も同一 thread の順次呼び出しだけ。コメントで「multi-thread guard is Phase 4」を 1 行 pin | thread-safe な busy flag・UI block popup（D11 Phase 6） |
| (D) worker thread driver | **D1** | `Backtester` / `KernelStepper` は thread-agnostic な pure Python object。呼び出した thread で同期的に進む | host worker thread 経路・Unity main thread off-loading・AFK probe |

**Phase 3 境界の最終形**:

- **入れる**: `bars_per_second` 引数受理 / `stop_event` seam preserve / stepper-backtester parity / single-thread direct pytest
- **入れない**: pacing sleep / `_REPLAY_BAR_INTERVAL_SEC` 撤去 / GIL/Hakoniwa AFK / running guard / worker thread wiring

### Q6 — bt teardown ＋ module/class 名 ＋ 既存型の移設

#### (a) bt teardown（**α: no explicit close**）

`Backtester` は marimo Kernel も DuckDB connection も pythonnet resource も所有しない pure Python façade。`KernelStepper` が抱えるのも `bars: list[Bar]`（eager 全 load・file handle なし）と pure Python objects。**`close()` を公開する意味が薄い**。

破棄時の host 手順: **`stop_event.set()` → bt 参照 drop → new bt 構築**。GC で確実に解放（ファイナライザ依存なし）。

却下: `__del__` 経由の cleanup（GC timing 不確定で副作用 trigger に使うべきでない＝Python anti-pattern）。

**注意**: `KernelStepper.finalize()` は **run result 確定**の primitive として残す（resource cleanup API ではない）。

#### (b) module placement

| 型 | 移設先 |
|---|---|
| `KernelStepper` / `StepEvent` / `StepHandle` | `python/engine/kernel/stepper.py`（新規） |
| `_Context` | `runner.py:51-133` → `stepper.py` に移設（stepper の private 内部状態として cohere） |
| `RunResult` | `runner.py:40-48` → `stepper.py` に移設（`stepper.finalize() -> RunResult`） |
| `KernelRunner` | `runner.py` のまま（薄い wrapper 化）。stepper を import |
| `Backtester` | `python/engine/strategy_runtime/backtester.py`（新規・ADR-0016 候補名） |

`runner.py` は back-compat の保険として `from engine.kernel.stepper import RunResult, _Context  # noqa: F401  re-export` を持つ。

#### (c) class 名（lock）

**`Backtester` / `KernelStepper` / `StepEvent` / `StepHandle` / `RunResult`**

- `RunResult` はリネームしない（既存 imperative path の `KernelRunner.run() -> RunResult` 語彙が定着・`StepperResult` にすると無駄な意味差が生まれる）
- `RunResult` ＝ 完走または停止した run の結果 / `StepHandle` ＝ 1 step/open の返り値、で役割が分かれる

### Q7 — branch logistics ＋ Phase 3 done-gate

#### (a) branch

- **base**: `origin/main`（= `4999e8f`・Phase 1 docs landed 済み）
- **branch**: `feat/#95-phase3`
- **PR target**: main へ直接（Phase 2 と独立）。先入れ側が後入れ側を rebase

#### (b) Phase 3 done-gate（11 項目・順序整理済み）

| # | gate | 内容 |
|---|---|---|
| 1 | **既存 #24 / golden 系 green** | `KernelStepper` extract 後も `test_dag_byte_identical_to_imperative_twin` 等が byte-identical で通る = Phase 3 の **主 gate**（refactor 安全網） |
| 2 | **`Backtester` replay parity** | (β) `test_backtester_replay_byte_identical_to_kernel_runner` |
| 3 | **`Backtester` step-to-end parity** | (γ) `test_backtester_step_to_end_byte_identical_to_kernel_runner` |
| 4 | **`stop_event` seam preserve** | pre-set stop_event で `open_next_bar` が `STOPPED` を返し bar を流さない |
| 5 | **lazy-import / offline purity** | `test_strategy_runtime_offline.py` が new `backtester` module を import しても marimo を引かない（marimo-free・findings 0046 S6 既定） |
| 6 | **`bars_per_second` no-op smoke** | `bt.replay(bars_per_second=10)` 引数受理・sleep は入らない（Phase 3 は high-speed only） |
| 7 | **`bt.submit_market` context-out fail-closed** | bar open 前 / close 直後に呼ぶと `ValueError`（D1 lock の constructive proof） |
| 8 | **`bt.bar()` / `bt.portfolio()` lifecycle rule** | Q3-b の 4 状態（未開始 / open 中 / close 直後 / END 後）の戻り値を pytest で pin |
| 9 | **findings 0072 landed** | 本 finding（Q1–Q7 の lock 記録・0071→0072 リネーム済み） |
| 10 | **CONTEXT 加筆（必要最小限）** | 既存「notebook = backtest 一本化 / `bt` ハンドル」エントリの**実装確定**注記。新規 term は最小（`KernelStepper` を glossary に立てるかは owner 判断） |
| 11 | **`code-review(simplify)` Medium+ 0 件** | CLAUDE.md 必須アクション。今回の変更は「単なる wrap より重い」refactor なので Medium+ 0 を done 条件に入れる価値あり |

#### (c) ADR-0016 は不編集

自己保護条項。本 findings 0072 から「方針: ADR-0016」として参照するだけ。

---

## 本 finding がやっていないこと（Phase 3 範囲外・Phase 4–6 に委譲）

- pacing sleep の実挿入（`bt.replay(bars_per_second=N)` の per-bar `sleep(1/N)`） → Phase 4
- `_REPLAY_BAR_INTERVAL_SEC = 0.01`（`_backend_impl.py:189`）の撤去 → Phase 4
- GIL handoff の Unity AFK 実機 reconfirm → Phase 4
- running guard の正確な配置・thread-safe な busy flag → Phase 4 / 6
- UI block popup → Phase 6
- host worker thread driver（`BackcastWorkspaceRoot.OnRun → TryStartRun` 同型経路） → Phase 4
- per-cell RUN ボタンと `bt._close_open_bar()` の wire → Phase 2 / 6
- cross-instrument 発注 API（`bt.submit_market(qty, instrument="...")` 等） → 将来 additive ADR

これらは ADR-0016 の方針下で各 Phase の findings に固定する（ADR は書き戻さない＝自己保護条項）。

---

## 実装着地（2026-06-20・`feat/#95-phase3`）

Q1–Q7 の設計の木どおりに実装し、done-gate 1–11 を GREEN 化した。

### landed したコード

| 物 | 場所 | 内容 |
|---|---|---|
| `KernelStepper` / `StepEvent` / `StepHandle` / `RunResult` / `_Context` | `engine/kernel/stepper.py`（新規） | per-bar 状態機械（3 primitive `open_next_bar`/`close_current_bar`/`finalize`）。golden を作る `_equity_curve`/`_fills`/`_last_prices`/`_baseline_equity`/`_index`/`_stopped_reason` を instance state へ hoist（Q1 の「wrap より重い」refactor）。`strategy=None` は user hook だけ skip・fills/denials/sink push/portfolio apply は stepper 責務（Q4） |
| `KernelRunner` | `engine/kernel/runner.py`（薄化） | 「全 bar load（`load_universe_bars` は runner 名前空間に残置＝既存 8 本の monkeypatch test 無改変）→ stepper を end まで駆動 → finalize」の wrapper。`RunResult`/`_Context`/`StepEvent`/`StepHandle`/`KernelStepper` を re-export |
| `Backtester` | `engine/strategy_runtime/backtester.py`（新規・marimo-free） | `bt` ハンドル。5 ADR-API ＋ `_close_open_bar`（Phase 2 hook）＋ `from_scenario(scenario_dict)` |
| `signed_qty_to_side` | `engine/kernel/orders.py` | signed delta → `(side, size)`/`None` を `cell_api.make_submit_market`（refactor で共有）と `bt.submit_market`（Q3 延長）が共用。kernel→strategy_runtime の層逆転を避けるため orders に置いた |

### done-gate 結果

1. **既存 #24 / golden 系 green** — full suite **418 passed / 0 skipped**（owner DuckDB mount present）。`test_kernel_subprocess_matches_committed_golden` が byte-identical で通過＝extract の安全網を実データで実証（**主 gate**）
2–3. **replay parity / step-to-end parity** — `test_backtester_phase3.py` の (β)`test_backtester_replay_byte_identical_to_kernel_runner` / (γ)`test_backtester_step_to_end_byte_identical_to_kernel_runner` が `KernelRunner`（命令型 `Strategy` 双子）の sink buffer と完全一致。F4(A)「step を終端まで押し切れば replay と byte-identical」を構造で pin
4. **stop_event seam** — `test_pre_set_stop_event_streams_no_bars` / `_via_bt_handle`（pre-set で bar を流さず `STOPPED`・`bars==0`）
5. **offline purity** — `test_strategy_runtime_offline` に `stepper` / `backtester` を追加して marimo-free を pin（GREEN）
6. **`bars_per_second` no-op** — `test_bars_per_second_accepted_no_sleep`（引数 store・`stepper._bar_interval_sec==0`＝pacing 未配線）
7. **context-out fail-closed** — `test_submit_market_{before_first_step,after_close,after_end}_raises`
8. **lifecycle rule** — `test_lifecycle_states_bar_and_portfolio`（未開始/open中/close後/END後の 4 状態）
9. 本節 = findings 0072 実装着地記録
10. CONTEXT 加筆（`bt` ハンドル entry に「実装着地（#95 Phase 3）」注記）
11. `code-review(simplify)` Medium+ 0

### 実装中に確定した下位の下位決定

- **bar lifecycle の golden 同値性**: `open_next_bar()` は「前 bar を close（denials/fills/equity/rail）→ 次 bar を open（push_bar/ref_prices/on_bar）」の順で叩く。`while open_next_bar().event is BAR_OPEN: pass` が旧 `runner.run()` の per-bar 列を**演算順まで完全再現**する（stop は push_bar 前・rail は equity append 後の break-before-sleep も保存）。
- **CONTEXT 既述「replay は呼ばれるたび 0 に reset / 再走」との整合**: Phase 3 は **1 `bt` = 単一 forward-only run**（replay と step は同一 pointer を共有）。reset/再走は `bt` ライフサイクル＝**Phase 5**（最初の grill コメント ⑩）。Phase 3 で再走するなら `bt` を作り直す（Q6 α teardown）。CONTEXT の bt entry にこの境界を明記した（in-place 注記・ADR-0016 は無改変）。
- **`load_universe_bars` の名前空間**: stepper へ移さず runner に残置。既存 8 test の `monkeypatch.setattr(runner_mod, "load_universe_bars", ...)` を 1 つも壊さない（gate 1 のリスク最小化）。`Backtester.from_scenario` は backtester 名前空間で load。
