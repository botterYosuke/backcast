# findings 0046 — marimo-embed thin-drain runtime: 設計の木 (#76 redesign / S1)

方針: **将来 ADR（embed 実行モデル）** — 起案は実装 epic（ADR-0005 supersede ＋ marimo を prod deps へ昇格）と
同時に行い、本 slice では ADR に着手しない（#76 §6 の ADR 先送りを尊重）。設計正本: [Discussion #64](https://github.com/botterYosuke/backcast/discussions/64)。
gate spike の実測は [`docs/spike/marimo-embed-result.md`](../spike/marimo-embed-result.md)。本 doc は #76 redesign grill
（`/grill-with-docs`, 2026-06-17, Q1–Q5）で owner が確定した **pre-implementation 設計の木**を固定する（会話で消えないように）。

---

## 背景 — 「再設計で速くする」の正体

#76 gate spike は文書化 API（`AppKernelRunner.run`）経由の per-bar reactive recompute を **≈4.4ms/bar（235s/50k）= AC1 RED**
と測った。redesign grill で内訳を実測した結果、その遅さは **100% が marimo `Kernel.run` のオーケストレーション**
（entry-point 走査 ＋ graph mutation ＋ lint ＋ topo-sort ＋ execution-context install）で、**静的 cell-DAG なら per-bar に一切不要**だった。

- 静的グラフ前提で「executor ＋ 実行する cell 列を cold で一度だけ計算 → per-bar は `executor.execute_cell(cell, glbls)`
  （＝`exec(body); eval(last_expr)`）を直接呼ぶ」**host-owned 痩せ drain** で **~0.6–3µs/bar（~0.04–0.18s/50k）= native 同等**を実測。
- ＝ **reactive モデル自体はゼロコスト**。Replay も native 速度で reactive 実行でき、当初 AC4 の「Replay/Live 分離 or 却下」は不要。

---

## 決定（owner 確定・binding）

### D1 — bar 内＝変数 dataflow / bar またぎ＝mo.state（Q1）
- **bar 内の中間値**（bar→indicator→signal→order→risk）は **cell の return 変数**で渡す（def→ref で静的辺が張られる acyclic forward）。
- **bar をまたぐ feedback**（position/portfolio/cash・前 bar 値の持ち越し）と **host 入力**（bar/tick）は **mo.state**。
- 根拠（コードで裏取り）: marimo の静的辺は変数名の def→ref のみ。`mo.state` 値渡し（writer `set_x()`／reader `get_x()`）は
  共有変数名を def/ref しないので **writer→reader 辺が張られない**（両者は state 定義 cell の兄弟）。実測: `transitive_closure(bar_root, children)`
  は変数 dataflow の `_order` を捕捉するが、state 連鎖の `_order` は取りこぼす。
- 効能: closure を一度 precompute すれば topo 順で全 forward-pass を捕捉、**fixpoint 不要**。state 定義 cell は root でも descendant
  でもないので hot list から自動除外（再生成事故も無し）。

### D2 — precompute は marimo の関数を **call**（mirror しない）（Q2）
- roots = `∪ kernel._find_cells_for_state(state, "__external__")`（host-driven state ごと。runtime.py:1492、refs が state と
  identity 一致する cell を返す）。
- hot list = `Runner.compute_cells_to_run(graph, roots, excluded, "autorun")`（cell_runner.py:180、**@staticmethod**＝kernel 不要で
  cold 呼び出し可。`roots ∪ stale-ancestors → transitive_closure(children, import_block_relatives) → topological_sort`）。
- 自前で closure/topo を組まない（option 2 却下）: `compute_cells_to_run` の stale-ancestor union / import-block relatives / overrides
  prune を取りこぼす＝「approximate するな」違反。record-replay も却下（option 3）: 実 drain 1 回の録画は値依存の実行列で、
  条件付き分岐（bar 500 で初発火する枝）を取りこぼし不健全。`compute_cells_to_run` は静的＝全 bar で健全。
- precompute は初回 full run 直後（stale 無し）に呼ぶ／overrides=None（embedded-prune 枝を no-op に）。

### D3 — fail-closed は **構造的**（new cid 発火＝拒否、出力一致ではない）
- cold で実 marimo drain を 1 回走らせ `execute_cell` を instrument。**hot list に無い cid が発火したら拒否**（= state 媒介の
  2nd-round＝bar 内 state 連鎖）。
- ⚠️ **出力一致（full-marimo-drain との byte-identical）で判定してはいけない**: 正当な跨ぎ feedback は cold drain で既存 cid を
  in-drain 再走させ（look-ahead）値が変わるが、**痩せ drain の「前 bar 値を読む」＝look-ahead 無しこそ正しい backtest 意味論**。
  検証は構造（new cid）に限定し、既存 cid 再走や値差で誤拒否しない。

### D4 — feedback 契約: supported は3形（Q4）
跨ぎ feedback consumer は hot list に到達可能でなければならない。実測で確定した3形:

| 形 | 例 | 判定 | 機構 |
|---|---|---|---|
| **A self-cycle**（同一 cell が read+write）| EMA/counter/running-stat を1 cell | ✅ PASS | self-loop skip（resolve_state_updates 条件3）で new cid 出ず・bar-root で hot 在 |
| **B forward 接続**（別 cell が feedback ＋ bar/forward を読む）| position→sizer, drawdown→risk | ✅ PASS | 既存 cid（closure 内）＝new cid 出ず |
| **C host 昇格**（純 delay-line を host-driven state に）| 現 bar 非依存の遅延伝播 | ✅ PASS | host が bar 間で書く→reader が root（_find_cells_for_state）|

- **拒否**: forward 非接続の feedback-only consumer（feedback だけ読む別 cell）＝ bar 内連鎖と構造的に区別不能 → new cid で拒否。
  remedy は A/B に書き換える or C（host 昇格）。
- 曖昧性の解決は **authoring モデル**（A/B/C）に押し出し、fail-closed は「new cid＝拒否」のまま単純維持。

### D5 — driver-state は host が **明示宣言**（Q5）
- StrategyRuntime は「どの State が host-driven（root の起点）か」を **host の明示宣言**で知る。**host-owned State オブジェクトを
  渡す**のを推奨（bar/tick/position/portfolio/cash は全て host 側の概念＝host が作って inject するので object を直接保持。名前文字列
  `driver_getters=['get_bar',...]` は driver state を cell が作る著者規約時の fallback。`globals[name] is state` で解決可を実証）。
- **auto-detect（全 mo.state を driver 扱い）は不可**: 実測で cell-written feedback state（get_ema）を root 扱いすると D4 で拒否すると
  決めた forward 非接続 consumer が黙って hot list に入り **D3/D4 を破る**。命名規約も brittle で却下。
- **不変条件**: `roots = host が bar 間で書く state ちょうど`。これを超えると cell-written/host-written の区別が消えて設計が崩れる。
- host states = `{bar, tick} ∪ {host が bar 間で書く feedback states: position/portfolio/cash ＋ 昇格された delay-line}`。

---

## per-bar cell 契約（hot path の正しさ境界・redesign grill 前段で確定）
痩せ drain は per-cell の execution-context（`_install_execution_context`）を張らない。実測した境界:

| per-bar cell が触るもの | hot path | 
|---|---|
| bar/state 読み + compute + injected global（submit_market/portfolio）+ mo.state read/write | ✅ OK（native）|
| `mo.output` / `mo.md` | ⚠️ silent no-op（crash せず描画なし＝footgun）→ **fail-closed guard で明示エラー化** |
| `mo.ui.*` 要素 | ❌ hard error（`MarimoRuntimeException`）|

per-bar cell = **pure compute のみ**。mo UI/output/virtual-file は cold path（初期起動・live-edit・低速 step デバッグ）専用。

---

## UX への影響（owner 懸念への結論）
計算と描画の分離（今の kernel↔Unity 分担と同じ）なので体験はほぼ犠牲にならない: ライブ編集→reactive 更新は cold path で
full marimo のまま無傷／チャート・パネルは host 描画（cell の責務外）／スライダー等 param は cold で作り hot で値を読む。
唯一 per-bar cell 内 `mo.output` が silent no-op になる footgun のみ＝guard で明示エラーに倒し、debug は低速 step（full path）で代替。

---

## coupling の所在
`compute_cells_to_run` / `_find_cells_for_state` は marimo private API だが **cold path（compile/edit ごとに1回）でのみ**呼ぶ。
per-bar hot path は素の `exec` で marimo 内部に触れない＝private-API 依存は cold に封じ込め、pin/保守も cold だけ。

---

## 実装 epic へ持ち越す未解決分岐
1. **cold↔hot 遷移トリガー**: live-edit でグラフ変化 → full `Kernel.run` 再コンパイル → precompute 再実行 → hot 再開。視覚的 hitch を出さない遷移設計。
2. **fail-closed guard 実装**（D3 の構造判定 ＋ per-bar cell の mo.output/UI 明示エラー化）。
3. **痩せ drain の host API 化**（StrategyRuntime / CompiledStrategy 抽出: cold で compiled body＋topo＋driver states を frozen 構造体に）。
4. **新規 ADR（embed 実行モデル）起案 ＋ ADR-0005 supersede ＋ marimo を prod deps 昇格**（S3、本 slice 範囲外）。
5. entry_points 未キャッシュは marimo perf bug（cold path のみ顕在・upstream PR 候補）。

## 実証（throwaway probe・spike dep group）
`python/spike/marimo_embed/`: F1/AC1/AC2/AC3 ＋ redesign grill の各実機検証（変数 dataflow vs state 連鎖の closure 差、
`compute_cells_to_run` cold 呼び出し、self-cycle PASS / shape-3 REJECT、auto-detect が D4 を破ること）を本 grill 中に実走して確認。

---

## S1 実装記録（host-owned 痩せ drain core ＋ 静的 precompute・本 slice で着地）
spike probe（単一 cell 直打ち）を **production module** `engine/strategy_runtime/thin_drain.py` に卒業。epic
推奨どおり **S1＋S2 を統合**（痩せ drain core ＋ D2 の静的 precompute）した最小縦スライス。

### 着地した形（owner 確定の設計フォーク）
- **standalone・dormant・未配線**: 新 module は `HeadlessKernel`（spike の `context_standup` を卒業）＋
  `CompiledStrategy`(cold frozen 構造体)＋`StrategyRuntime`(hot drain)＋`open_runtime()` ctx-mgr。**runtime seam
  （`runner.py`/`_backend_impl`）からは import しない**。`KernelRunner` への載せ替え＝S6、marimo prod 昇格＝S3 ADR の後。
- **marimo は spike-only 据え置き**（`[dependency-groups] spike`・local fork editable）。本 module は marimo を top-import
  するが、runtime import path に乗らない＝prod は marimo-free のまま（offline guard で固定）。
- **D2 は marimo 関数を call**: roots = `kernel._find_cells_for_state(state, "__external__")`（host-driven state ごと）、
  hot list = `Runner.compute_cells_to_run(graph, roots, set(), "autorun")`(staticmethod)。mirror しない（D2 遵守）。
- **D5 は host 明示宣言**: `drivers=[getter名]` で root state を宣言。getter（=State）→ `State._set_value` で setter を回収。
  auto-detect しない。

### 実装中に判明した境界（spike では踏まなかった点）
- **cold `runner.run(all)` は mo.output/mo.ui cell を含む app で globals を populate しない**（cell が落ち empty globals）。
  ＝contract gate は cold run せず **bare `executor.execute_cell` 直叩き**（spike `_boundary` と同型）で hot-path 契約を検証する。
  perf/parity/precompute gate は pure-compute app なので cold run は健全。
- **`open_runtime.__enter__` で `_compile` が raise すると `__exit__` が呼ばれず kernel context が leak**（Python の
  ctx-mgr 規約）→ `__enter__` で compile 失敗時に明示 teardown する（prod robustness fix・cross-session leak 封鎖）。

### AC → 恒久 gate（behavior-to-e2e: backcast に FLOWS.md 無し＝gate がテスト正本）
| 挙動（保証したい不変条件）| gate（pytest, spike-group）| 種別 |
|---|---|---|
| **AC1 perf**: 50k bar を native 速度で完走（orchestrated 235s に対し 桁違い）| `test_strategy_runtime_thin_drain::test_thin_drain_native_speed_and_hot_state_write`（5s/50k budget・hot-path mo.state write も検証）| perf |
| **契約**: per-bar cell = pure compute（mo.output silent / mo.ui hard error）| `..::test_hot_path_contract_mo_output_silent` / `..::test_hot_path_contract_mo_ui_hard_error` | contract |
| **D2 precompute**: hot list は driver-rooted・topo 順・state 定義 cell は自動除外 | `..::test_precompute_hot_list_is_driver_rooted_topo_ordered` | precompute |
| **S2 parity**: 多 cell DAG（bar→signal→order→portfolio, self-cycle）が直 on_bar twin と byte-identical | `..::test_dag_byte_identical_to_imperative_twin` | parity |
| **未配線不変条件**: runtime seam は marimo を import しない（S1 dormant）| `test_strategy_runtime_offline::test_runtime_seam_does_not_import_marimo`（clean subprocess・常時走る）| invariant |

実測（production drain＝set_driver＋step）: median ≈5.9µs/bar・50k≈0.31s（native baseline 0.060s の ~5x、
orchestrated 235s の ~750x 速）＝budget 内・native-class。
走らせ方: `uv run --group spike python -m pytest tests/test_strategy_runtime_thin_drain.py`。

### この slice で**やっていない**こと（後続）
S3 fail-closed guard（D3 構造判定の runtime 実装）/ S4 injected globals（`submit_market`/`portfolio` の cell 注入）/
S5 cold↔hot 遷移 / S6 `KernelRunner` 載せ替え ＋ ADR-0005 supersede ＋ marimo prod 昇格。
