---
status: accepted
---

# per-cell RUN を strategy 実行エントリーとし notebook = backtest に一本化する（ADR-0012 の「単一再生ボタン」facet を supersede）

`/grill-with-docs`（2026-06-20 owner セッション・3 案検討）＋ 続行（2026-06-20 #95 Phase 1・Q1/Q2 owner HITL）で導出。設計正本: 本 ADR ＋ [findings 0070](../findings/0070-notebook-equals-backtest-grill.md)。

> **実装状態**: 本 ADR は #95 Phase 1 で **accepted**（docs + glossary のみ）。実 cell-facing API `bt` ／ `KernelRunner` の state-machine 化／速度レジスタ／per-cell RUN ボタン配線は #95 Phase 2–6 の named slice で順番に landing する。`status: accepted` は **decision が確定**したことを指す（ADR-0012 と同じく「accepted だが実装は段階的」）。

## Context

- **ADR-0012** は marimo cell-DAG を target authored strategy モデルとして固定したが、「アプリ全体で 1 つの再生ボタン」（#64 R1 のビジョン文言）を将来表面として温存していた。`KernelRunner` の per-bar loop は host 所有で（`engine/kernel/runner.py:230` の `for bar in bars:`）、ユーザー cell は `get_bar()` を読み `submit_market(qty)` を呼ぶ reactive な反応役にすぎず、**「再生ループ自体は host が裏で回す」隠れ機構**だった。
- owner 問い: 「reactive 反応役にとどまる cell モデルでは、replay ループや engine 接続がユーザーから不可視。**ユーザーが notebook に書くコードで backtest 経路へ明示接続できないか**？」 → 2026-06-20 grill で 3 案検討（**B1**: 接続のみ明示・loop はホスト reactive にポンプ ／ **B2**: cell に `for bar in bt.replay(): ...` ループを書く ／ **B3**: cell に `bar = bt.step()` を書く）。
- **B1 は却下** (#96 close)：「reactive cell 複数が暗黙 DAG で繋がる」は marimo らしさを保つが、依然として **per-bar 駆動はホスト所有**で「ユーザーが backtest を駆動する」感が得られない。reactive と「明示接続」の概念衝突も悪い。
- **B2 と B3 は採用・併存**: B2（命令型ループ）= 早期 `break` / bar 跨ぎ任意 Python 状態 / 視覚 playback。B3（手動 step）= bar-by-bar デバッグ・per-cell RUN と自然に噛み合う。
- 「dry-run preview vs 実 backtest」のような **別系統を作らずに 1 本化**したい（D7 telos = notebook = backtest）。
- 既存実装の状況（2026-06-19 時点）:
  - `KernelRunner` per-bar 順序: `push_bar → on_bar → fill@close → push_order/push_portfolio → on_equity`（runner.py:230–336）
  - host pacing: `bar_interval_sec=_REPLAY_BAR_INTERVAL_SEC=0.01`（`_backend_impl.py:189` の hardcoded constant、#76 S6b-β で footer transport (#30) と共に固定）
  - `_replay_stop_event`（`DataEngineCore.core.py:43`、property `core.py:272`）= 唯一残る run-control event
  - running snapshot 経路: #65 `ReplayKernelObserver` → `engine.last_portfolio` → poll lane → Hakoniwa
  - 命令型 `.py` の UI 到達点: `#80` Strategy picker 退役で File→Open のみ。findings 0054 の **1-cell wrap** で非 marimo `.py` も Open 可能（migration / editing affordance）

## Decision

### D1 — notebook = backtest 一本化（dry-run 廃止）

戦略 cell が `bt.replay()` / `bt.step()` を**呼ぶ**とき、それは**実 backtest を駆動**する（`submit_market → ctx.submit_market → OrderEngine → ReplayBroker`、bar close で実約定・portfolio 更新・Hakoniwa に実数値）。**dry-run preview** や **`submit_market` を no-op にした別系統**は作らない（旧 #95 設計で言及されていたこの path は本 ADR で**却下**）。

「dry-run のように軽く動かしたい」ニーズは、`bt.replay()` / `bt.step()` を**呼ばない** cell（engine 非接続の純粋計算「土台」層）で代替する。**駆動と参照の区別**は厳密に保つ:

- **駆動 operation** = `bt.replay()` / `bt.step()` の**呼び出し**。これだけが KernelRunner の bar pointer を進める。
- **active bt state の参照** = `bt.bar()` / `bt.portfolio()`。**現在の bt 状態を読むだけ**で runner を進めない。`bt.replay()` / `bt.step()` の per-bar context 内で意味を持ち（呼び出し前は initial・終端以降は最後の bar での値）、独立に呼んでも副作用なし。
- **`bt.submit_market(qty)`** = `bt.replay()` / `bt.step()` の per-bar context 内**でのみ有効**（KernelRunner の `ctx.submit_market → ctx.pending → fill@close` 経路を踏むため）。context 外の呼び出しは Phase 3 で fail-closed（runner 外発注は意味を持たない）。

帰結: 駆動 operation を**書かない** cell は実 backtest を一切起こさず、`bt` ハンドルを単に free ref として import するだけでは何も走らない。実装者は「`bt` を参照したから run」ではなく「`bt.replay()` / `bt.step()` を呼んだから run」を境界とする。境界条件は cell の駆動 operation 呼び出しの有無で構造的に決まり、別 flag や別 mode を持たない。

### D2 — per-cell RUN を user-visible execution entry point とする

すべての cell 窓（adopted `region_001` ＋ spawned `region_002+`）に **RUN ボタン**を持たせる（ADR-0013 が立てた `StrategyEditorWindowFrame` の idempotent find-or-create、X ボタンと同型）。クリックすると押した cell＋reactive 下流が DAG 順で再実行される:

- `bt` 非使用の cell → **marimo native reactive 再計算**（`thin_drain.HeadlessKernel` を流用する interactive 経路。per-bar 用 frozen-list は使わない）。
- `bt` 使用の cell → 実 backtest を駆動（`bt.replay()` で全 bar / `bt.step()` で 1 bar）。

**専用の batch RUN ボタン（#76/#81 merge U1 の global ▶ Run）・title-bar Run（findings 0046 で言及されていた将来表面）・global transport（#30 既退役）は本 ADR で formal に supersede**。「アプリ全体で 1 つの再生ボタン」（ADR-0012 が将来 facet として温存していたもの）は**本 ADR で却下**する。

### D3 — `bt` ハンドルの lifecycle・状態共有

`bt` は **commit された startup-panel config 単位**にスコープされる。host は active config に対して**ちょうど 1 個の `bt` ハンドル**を free ref として cell globals に注入し（既存 `get_bar` 注入と同型 seam）、同じ startup-panel config が再 commit されたら**破棄して作り直す**。

`bt.replay()` と `bt.step()` は **同一 `bt` / 同一 KernelRunner 状態機械 / 同一 bar pointer** を共有する:

- `bt.replay(speed=N)` は呼ばれるたび **pointer を 0 に reset** して end まで走り、完走時に pointer = end を残す。
- `bt.step()` は現在 pointer から **1 bar 進める**。終端で `None`。
- `bt.step()` cell の再実行は**意図的に stateful**: 各実行で 1 bar 進む。marimo の reactive 再実行が「同じ cell の再評価＝idempotent」を前提にする規律と意識的に違う（B3 の正体は「ステッパー」で、cell を「ボタン」のように使う）。
- 完走後の `bt.step()` は `None`、次の `bt.replay()` はまた 0 から。
- replay 実行中に同じ `bt` へ別の RUN が入った場合は **running guard でブロック**（実装は Phase 4/6）。

ユーザーモデルは 3 行で説明できる: ① **config commit が `bt` を新規作成** ② **replay は常に 0 から** ③ **step は現セッションの pointer を進める**。

### D4 — 命令型 `.py` 実行 UI 経路の formal sunset

ADR-0012 §1 の「命令型 `Strategy.on_bar` / `strategy_loader` は移行期 only・退役は将来の named slice」を**本 ADR で履行**する。global ▶ Run / title-bar Run / footer transport を退役した結果、UI から命令型 `.py` を batch 実行する手段は無くなる。

**File→Open の 1-cell wrap**（findings 0054）は migration / editing affordance として残す: 命令型 `.py` を開いて編集し Save すると `generate_filecontents` 経由で marimo canonical 形に書き換わる＝**一方向マイグレーション補助**。だが**実行入口は per-cell RUN だけ**。

命令型 `Strategy` クラス・`strategy_loader.load`・`MarimoStrategy` への switch 経路（`_backend_impl._select_replay_strategy` の AST detect-first ordering）・`KernelRunner` boundary は **pytest / #24 golden gate / programmatic 互換 oracle 用に存続**するが、**UI fallback Run ボタンは残さない**。

### D5 — kernel per-bar 契約（不変の adaptation 境界）を再確認

`bt` の内部は **`KernelRunner` を再実装せず同一順序を wrap** する（ADR-0012 §2「不変の adaptation 境界」を継承）:

- `bt.replay()` は `KernelRunner` の per-bar シーケンス（`push_bar → on_bar[ユーザー本体] → fill@close → push_order/push_portfolio → on_equity`）を generator／state machine 化したもの。
- `bt.step()` は同じ state machine を **1 bar 進めて中断**する operation。
- B2 と B3 は **同じ state machine の 2 つの呼び出しスタイル**（D10）。

帰結: **#24 golden は byte-identical で保持**（ADR-0006 不変）。命令型経路は `KernelRunner` 無改修のため自動で保たれ、marimo 経路は wrap によって per-bar 順序が同一になる。

### D6 — config = startup panel / results = Hakoniwa（cell に書かない）

`universe / start / end / cash / granularity` は **`ScenarioStartupTile`** が所有し、**cell に書かない**（findings 0046 の「scenario 編集専用」化方針と一致）。`orders / positions / buying_power / run_result` は **Hakoniwa** が所有し、**表示 cell は書かない**。cell に残るのは **strategy ロジックだけ**:

```python
# B2: ループを自分で回す（走行中 Hakoniwa 逐次更新・速度はコード指定）
for bar in bt.replay(speed=2):
    pf = bt.portfolio()
    if bar.close > pf.avg_price and pf.position == 0:
        bt.submit_market(100)

# B3: 押すたびに 1 bar 進む
bar = bt.step()
if bar is not None and bar.close > bt.portfolio().avg_price:
    bt.submit_market(100)
```

### D7 — 走行中 running snapshot 経路（#65 流用・新 sink を作らない）

`bt.replay()` の per-bar 駆動は **既存の #65 sink 経路をそのまま push** する: 各 bar で `ReplayKernelObserver.push_portfolio` / `push_order` / `on_equity` が走り、`engine.last_portfolio` を atomic swap で更新。poll lane (`LiveRpcLanes.get_state_json`) が Hakoniwa を bar-by-bar に追従させる。**新 sink を作らない**。

`bt.step()` も同一の sink 経路を踏むので、push 後に 1 bar 分の状態が反映される。

### D8 — 速度レジスタ（撤去機能の意図的復活）

`_REPLAY_BAR_INTERVAL_SEC = 0.01`（`_backend_impl.py:189`、hardcoded constant）を、**host 所有・thread-safe・live-mutable な速度レジスタ**へ置き換える。配置は **`DataEngineCore` 上、`_replay_stop_event`（`core.py:43`）の隣**。loop は **毎 bar 読む**（`sleep(base / speed)`）。

ユーザー API は **`bt.replay(speed=N)` の引数のみ**。`speed` は call 時にレジスタへ書き込まれ、loop が次の bar から尊重する。**UI コントロール（slider / pacing button）は作らない**。`bt.step()` には速度は無意味で、`speed` 引数を持たない（ユーザー自身が pace する＝ボタンを押すごとに 1 bar）。

> **過去の文脈との関係**: #30/#68 で `▶/⏸/⏭/⏹ ＋ [1,2,5,10,50]` transport が削除された (`core.py:42` の在席コメント / `backend_service.py:387` / `WorkspaceFooterView.cs:5` / findings 0046) のは「reactive drain は瞬時に終わる前提」だった。B2 の visual playback ニーズが前提を変えたので、**pacing だけを `bt.replay(speed=N)` という形で意図的に復活**させる。UI ボタンは復活させない。**silent な復活でなく、findings 0070 に復活理由を明示記録**。

### D9 — run は worker thread・GIL 解放

`bt.replay()` の長走り loop は **worker thread** で実行（`BackcastWorkspaceRoot.OnRun → TryStartRun` と同型・Unity main thread を塞がない）。各 bar で **GIL 解放**（既存 `_REPLAY_BAR_INTERVAL_SEC` 期間の `time.sleep` 自体が解放点・速度レジスタ化後も維持）。

teardown: `bt.replay()` の終端 or `_replay_stop_event.set()` のいずれかで loop を抜け、ctxmgr の `__exit__` が `MarimoStrategy.close()` (= `open_runtime.__exit__`) を呼ぶ。`StrategyRuntime` の context leak 防止不変条件（findings 0046 S6-2）を保持。

### D10 — B2/B3 は同じ土台に乗せる

`bt.step()` を **「1 bar 進める state machine」**として実装し、`bt.replay()` は**それを内部で end まで回すだけ**にする。両者が同一 implementation を共有することで:

- `KernelRunner` 再実装ゼロ → golden byte-identical（D5 / #24 / ADR-0006）
- 1 つの bar pointer / 1 つの teardown 経路 / 1 つの sink push 列
- B2 と B3 の挙動差は「ユーザーがループを書く／書かない」だけ

### D11 — 段階実装（Phase 1–6）

本 ADR の decision は確定。実装は #95 issue 本文の **Phase 1–6** で段階的に landing する:

| Phase | 内容 | 出荷物 |
|---|---|---|
| 1 | 本 ADR + grill 設計 record + CONTEXT glossary | docs のみ（本セッション） |
| 2 | 土台（全 cell 窓の RUN ボタン + 純粋計算 per-cell run） | Python seam + C# RUN button + per-cell text repr |
| 3 | `bt` ハンドル + `KernelRunner` state machine 化 | `bt` 公開 API |
| 4 | B2 `bt.replay()`（#97 を吸収） | running snapshot + 速度レジスタ + stop |
| 5 | B3 `bt.step()`（#98 を吸収） | reset / idempotency の pin |
| 6 | 実行状態 UI + block popup + rich output | per-cell idle/running/stale + `mo.md` / table / chart 出力 |

## Considered Options

- **再生ループの所有権**: 採用＝**B2 ＋ B3 併存**（命令型 for ループ ＋ 手動 step を同一 `bt` で）。**却下**＝B1（接続だけ明示・loop はホスト reactive、#96）—— reactive cell モデルは依然「ユーザーから loop が見えない」隠れ機構で、明示接続の telos を満たさない。
- **dry-run の扱い**: 採用＝**廃止・一本化**。cell が `bt.replay()` / `bt.step()` を呼べば常に実 backtest 駆動。**却下**＝「dry-run preview = `submit_market` no-op + 結果別表示」の別系統 —— 2 つの実行モードを永続維持することになり、Hakoniwa が「実数値」と「preview 数値」のどちらを示すかが文脈依存になる UX 災害。「軽く動かす」は `bt.replay()` / `bt.step()` を呼ばない（純粋計算）cell が代替する。
- **命令型 `.py` UI 実行**: 採用＝**formal sunset**（programmatic / golden 用に存続）。**却下**＝(a) global ▶ Run を imperative-only fallback として温存（非対称 UX が永続化）／(c) 命令型 `.py` を自動 1-cell marimo wrap して per-cell RUN で走らせる（`Strategy.on_bar` → marimo per-cell の自動変換 adapter が必要で工数大、`_select_replay_strategy` の detect-first ordering と二重化）。owner telos「notebook = backtest 一本化」と整合する選択肢のみ採る。**File→Open の 1-cell wrap で命令型 `.py` を Open すること自体は可能**（findings 0051/0054・migration / editing affordance として保持）が、**UI batch 実行経路（global ▶ Run）は本 ADR で sunset 済み**＝Open と実行を seam として分け、実行入口だけを per-cell RUN に一本化する。
- **`bt` ハンドル lifecycle**: 採用＝**config 単位・単一 bar pointer 共有**（replay は 0 reset、step は pointer 進行）。**却下**＝(b) replay と step が独立 instance（「いまの backtest 状態」が複数になり running snapshot / Hakoniwa の意味が混乱）／(c) ノートセッション単位永続（replay の意味が「中断再開」になり B2 の「全期間 visual playback」直感と齟齬）。
- **速度レジスタの user-facing API**: 採用＝**`bt.replay(speed=N)` 引数のみ**（host 内部は thread-safe live-mutable・将来 HITL は additive ADR で解禁可能）。**却下**＝UI コントロール復活（#30/#68 footer transport を再実装することになり退役判断を再 open）／reactive `mo.state` ベース（cell 配線複雑化・現スコープでは過剰）。
- **走行中 Hakoniwa 追従の経路**: 採用＝**#65 既存 sink 経路の流用**（`ReplayKernelObserver` → `engine.last_portfolio` → poll lane）。**却下**＝新規 push 経路追加（poll lane と 2 経路化、ADR-0007 Replay portfolio projection の権威衝突）。
- **ADR-0012 の supersede 範囲**: 採用＝**facet-scoped supersede**（「単一再生ボタン」facet のみ supersede、Decision §1 §2 §3 §4 は踏襲、§1 の命令型 sunset は本 ADR D4 で履行）。**却下**＝全面 supersede（target authored model や per-bar contract や marimo dep 昇格を再 open する必要が無く射程過大）／編集（ADR-0012 自己保護条項に反する）。**ADR-0011 / ADR-0012 の facet-scoped supersede 文型と同型**。

## ADR-0012 / ADR-0013 / ADR-0006 との関係

- **ADR-0012 §1 部分採用＋部分履行**: target authored = marimo cell-DAG は**踏襲**。「命令型 sunset は将来の named slice」を本 ADR で**履行**（D4 = UI sunset）。
- **ADR-0012 §2 完全踏襲**: kernel per-bar 契約（`on_start`/`on_bar`/`on_stop` ＋ `ctx.submit_market`/`ctx.pending`/`ctx.denials`）は本 ADR でも不変の adaptation 境界（D5）。`bt` は KernelRunner を wrap して per-bar 順序を同一に保つ。
- **ADR-0012 §3 完全踏襲**: marimo `[project.dependencies]` 範囲 pin は本 ADR でも前提（cell に書かれた `bt` 使用 cell は marimo 経路を引く）。
- **ADR-0012 §4 完全踏襲**: lazy-import 規律は本 ADR の `bt` 実装でも維持（`bt` 公開 module も `test_strategy_runtime_offline` の clean interpreter で marimo 漏れを assert される）。
- **ADR-0012 が将来 facet として温存していた「アプリ全体で 1 つの再生ボタン」**: **本 ADR で却下・supersede**（D2 = per-cell RUN）。ADR-0012 本文は無改変（自己保護条項）。
- **ADR-0013 完全踏襲**: 1 cell = 1 floating window / 1 notebook = 1 .py / cell 集約 = `MarimoNotebookDocument`。本 ADR の RUN ボタンは ADR-0013 が立てた `StrategyEditorWindowFrame` の idempotent find-or-create で全窓に append される。
- **ADR-0006 完全踏襲**: #24 golden は byte-identical（D5・D10 が `KernelRunner` 再実装ゼロを保証）。

## Consequences

- **新規 cell-facing API `bt`** が strategy_runtime に landing する（Phase 3）。`bt` の実 module 名は Phase 3 で命名（候補: `engine/strategy_runtime/backtester.py`）。**`bt` 自身は marimo-free** であること（lazy-import 規律＝seam が marimo を引かず、`bt.replay()` 実行時にのみ marimo 経路へ降りる）。
- **既存の global ▶ Run / title-bar Run UI は撤去**される（Phase 6）。`RunReadinessViewModel`（#76/#81 merge U1）は退役、または「strategy 供給可能性」judgment だけを保ったまま「user-visible Run trigger」役割を失う（実装は Phase 6 で決定）。
- **`_REPLAY_BAR_INTERVAL_SEC` 定数撤去**: Phase 4 で速度レジスタへ昇格。constant 参照（`_backend_impl.py:189`/`922`）は thread-safe getter 経由へ rewire。
- **#97 / #98 issue を吸収**: 両 issue は本 ADR の Phase 4 / Phase 5 として実装される。本 ADR landing 後、両 issue 本文は本 ADR を「方針: ADR-0016」として参照する短い pointer に書き換えて良い（B2/B3 詳細実装記録は別途 findings）。
- **#96 issue は close**（B1 却下を本 ADR が固定）。
- **走行中の per-bar GIL 解放点**は既存と同じ（loop sleep）が、頻度が速度レジスタで可変になる。poll thread（`LiveRpcLanes` 30s default）と Hakoniwa の bar-by-bar 追従の整合は #65 の既存契約のまま。
- **golden gate**: `bt.replay()` の per-bar 駆動が `KernelRunner.run` と byte-identical な order/fill/equity を出すことを Phase 4 で新 parity gate で pin（既存 `test_dag_byte_identical_to_imperative_twin` は author-defined parity・production binding ではないので別建てが必要）。
- **下位の実装事実**（`bt` 実装の正確な class／KernelRunner state-machine 化の seam／速度レジスタの正確な field 名と setter API／running guard の構造／per-cell RUN button の C# 配線詳細）は本 ADR に書き戻さず **Phase 2–6 の `docs/findings/` に記録**し、本 ADR を「方針: ADR-0016」として参照する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。ADR-0012 / ADR-0013 / ADR-0006 / ADR-0007 は別 decision の固定 oracle として（本 ADR が明示 supersede した「単一再生ボタン」facet を除き）踏襲し、編集しない。下位の実装事実は各 Phase の `docs/findings/` に記録し、本 ADR を「方針: ADR-0016」として参照する。
