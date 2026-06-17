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

> ⚠️ **このセクションは [redesign 追補 (2026-06-18)](#redesign-追補-2026-06-18--per-bar-cell-契約を撤回context-均一化案a) で撤回された。**
> 「per-bar cell = pure compute のみ／mo.output 黙殺→guard」は **hot/cold の挙動差をユーザーに露出させる**ため owner 判断で却下。
> 下記の境界（context を張らないと mo.output が黙殺・mo.ui が hard error）は**実測の事実として正しい**が、解決策が
> 「guard で塞ぐ」から「context を張って全 cell を marimo と同じ挙動に均一化する」へ変わった。以下は歴史的記録として残す。

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
| 挙動（保証したい不変条件）| gate（pytest）| 種別 |
|---|---|---|
| **AC1 perf**: 50k bar を native 速度で完走（orchestrated 235s に対し 桁違い）| `test_strategy_runtime_thin_drain::test_thin_drain_native_speed_and_hot_state_write`（5s/50k budget・hot-path mo.state write も検証）| perf |
| **control（旧契約・降格）**: context-less bare execute では mo.output silent / mo.ui hard error（＝なぜ D6 が要るか。`executor.execute_cell` 直叩きで pin。プリミティブ経由ではない）| `..::test_hot_path_contract_mo_output_silent` / `..::test_hot_path_contract_mo_ui_hard_error` | control |
| **D6 production-output**: production primitive `_execute_hot_cell(ctx,...)`（step が毎 bar 呼ぶもの）で mo.output が kernel stream へ publish される（黙殺されない）| `..::test_production_context_publishes_mo_output` | contract(prod) |
| **D6 production-ui**: 同 primitive で mo.ui が hard error にならない | `..::test_production_context_mo_ui_no_hard_error` | contract(prod) |
| **D2 precompute**: hot list は driver-rooted・topo 順・state 定義 cell は自動除外 | `..::test_precompute_hot_list_is_driver_rooted_topo_ordered` | precompute |
| **S2 parity**: 多 cell DAG（bar→signal→order→portfolio, self-cycle）が直 on_bar twin と byte-identical | `..::test_dag_byte_identical_to_imperative_twin` | parity |
| **未配線不変条件**: runtime seam は marimo を import しない（S1 dormant）| `test_strategy_runtime_offline::test_runtime_seam_does_not_import_marimo`（clean subprocess・常時走る）| invariant |

実測（production drain＝set_driver＋step）: median ≈5.9µs/bar・50k≈0.31s（native baseline 0.060s の ~5x、
orchestrated 235s の ~750x 速）＝budget 内・native-class。
走らせ方: `uv run python -m pytest tests/test_strategy_runtime_thin_drain.py`（S3/ADR-0012 で marimo が prod 依存になり `--group spike` 不要）。

### この slice で**やっていない**こと（後続）
S3 fail-closed guard（D3 構造判定の runtime 実装）/ S4 injected globals（`submit_market`/`portfolio` の cell 注入）/
S5 cold↔hot 遷移 / S6 `KernelRunner` 載せ替え ＋ ADR-0005 supersede ＋ marimo prod 昇格。

---

## redesign 追補 (2026-06-18) — per-bar cell 契約を撤回・context 均一化（案A）

`/grill-with-docs` 続行セッション。owner が **「hot cell / cold cell をユーザーに見せるモデルはボツ（わかりにくい）。
速度は確保したまま代替案を」** と判断。S3 の「per-bar cell = pure compute 契約 ＋ mo.output/UI fail-closed guard」を**撤回**し、
代わりに **痩せ drain の per-cell 実行を marimo の execution-context で包んで全 cell を均一化する（案A）** に確定。

### 何が問題だったか
S1 痩せ drain は per-cell の execution-context を張らない（`executor.execute_cell` 直叩き）。その副作用として
**bar を読む cell（hot）と読まない cell（cold）で挙動が割れる**: hot 経路だけ `mo.output` が silent no-op（footgun）・
`mo.ui` が hard error。これは「ユーザーが hot/cold を意識し、どこで何が使えるかを学ぶ」必要を生む＝**漏れた抽象**。
遅さの真因（4.5ms/bar）は entry-point 走査＋graph mutation＋lint＋topo であって **execution-context ではない**
（`with_cell_id` = ExecutionContext 1個生成＋属性差し替え＝サブµs）。つまり「速さ」と「hot/cold 露出」は**本来無関係**で分離できる。

### 確定（owner binding・実測裏取り済み）
- **D6 — per-bar cell も execution-context を張る（`with_cell_id`）**。痩せ drain は precompute した cell 列を毎 bar 実行する
  S1 のまま。違いは各 `execute_cell(cell, glbls, graph)` を `get_context().with_cell_id(cid)` で包むこと。
- これで **全 cell が普通の marimo cell として振る舞う**: `mo.output` は動く（`_output.py:42` の `execution_context is None` 早期 return を
  通らなくなる）・`mo.ui` も hard error にならない。**hot/cold の挙動差は消滅**。precompute 列は「内部の最適化」でユーザーには不可視。
- **pure-compute 契約・mo.output/UI guard は不要**（S3 の guard 部分を削除）。**`mo.output` を毎 bar cell に書くのは事故ではなく
  普通の marimo 挙動**。その出力を host がパネル（Hakoniwa）へ流すか捨てるかは**配線の選択**（#64 R4 と整合）であってユーザー規約ではない。

### 実測（spike `python/spike/marimo_embed/context_uniform.py`・`CTX-UNIFORM PASS`）
| 経路 | per-bar median | 50k 換算 |
|---|---|---|
| 痩せ drain（context 無し＝S1）| 3.03µs | 0.157s |
| **痩せ drain ＋ context（D6・案A）** | **6.26µs** | **0.345s**（native 級・orchestrated 235s の ~680x 速・overhead 2.07x は絶対値誤差）|

| 境界 | context 無し（control）| **context あり（D6）** |
|---|---|---|
| `mo.output` | 黙殺（output 空）| **動く（output 充填）＝footgun 消滅** |
| `mo.ui` | hard error（`MarimoRuntimeException`）| **ran-no-error ＝普通の marimo cell と同じ** |

### この追補で**変わらない**こと
D1（bar 内＝変数 dataflow / bar またぎ＝mo.state）・D2（precompute は marimo 関数を call）・**D3（構造的 fail-closed＝hot list 外 cid 発火を拒否。
parity/no-look-ahead の正しさ境界であって UI 契約とは別物）**・D4（feedback 3形）・D5（driver-state は host 明示宣言）は**全て不変**。
撤回したのは「per-bar cell 契約（pure compute のみ・mo.output guard）」のみ。

### 実装 epic への影響
- **S1 module の小改修**: `StrategyRuntime.step` の `executor.execute_cell` 呼び出しを `with_cell_id` で包む（D6）。
- **S3 の再定義**: 「fail-closed guard（mo.output/UI footgun 封じ）」は消滅。S3 に残るのは **ADR 起案 ＋ ADR-0005 supersede ＋ marimo prod 昇格** と、
  必要なら **D3 構造的 hot-list 完全性 guard**（mo.output とは無関係の parity 保証）。
- contract gate（`test_hot_path_contract_mo_output_silent` / `..._mo_ui_hard_error`）は「context 無しの素の挙動」を pin したもの＝**control として有効**だが、
  production runtime（context あり）の挙動 gate を別途足す（mo.output が動く・mo.ui が動く）。

### D6 gate-ownership 決定（`/grill-with-docs` 続行 2026-06-18・owner binding）
D6 の唯一の新しい不変条件＝「production が毎 bar **context を張り続ける**」。footgun（mo.output 黙殺）は context を張らずに cell を叩くだけで自明に再混入するので、D6 gate の唯一の仕事は production が wrap を保持しているかを構造的に縛ること。S1 が `_execute_hot_cell` を導入して潰した drift（gate が production からずれる）をここでも潰す。確定した形:

- **context をプリミティブ `_execute_hot_cell` 自身に内包**（別 `*_in_context` helper は作らない）。`with_cell_id` の wrap は `_execute_hot_cell` docstring が言う "how a hot cell is invoked" の変更そのもの＝プリミティブの憲章上ここに入る。シグネチャは `_execute_hot_cell(ctx, executor, cell, glbls, graph)`。D6 後の production は二度と context-less で走らないので、プリミティブは常に production を映す。
- **`step`**: `ctx = get_context()` を step ごと1回 bind（既存「once per step」構造を維持）→ ループ内で `_execute_hot_cell(ctx, ...)`。`drain` は `step` 経由なので別途改修不要。
- **新 production gate**: 同じ `_execute_hot_cell(ctx, ...)` を単一 cell に当て（cold-run は mo.output/ui app で globals を populate しないので `_boundary` 同型で単一 cell 駆動）、**ブロック退出後に `host.stream` に出力 CellOp が届いたこと**で mo.output が動くと判定（`execution_context.output` の内部属性直読みより end-to-end に正しい証拠＝出力が実際に kernel へ publish された user-visible 効果を観測）。mo.ui は no-exception で判定。production が wrap を外せば gate が RED。
- **既存 context-less 2 gate を降格**: `_execute_hot_cell` 経由をやめ `executor.execute_cell` 直叩きに変更。D6 後 production は context-less で走らない＝この2 gate は「production と drift-bound な contract」ではなく「なぜ D6 が要るかの control／歴史」に役割が変わる。プリミティブは pre-D6=bare／post-D6=wrap と常に production を映す。
- 実測（grill 中に裏取り）: `host.stream.messages`（`_SilentStream`）は bare-execute＋`with_cell_id` でも出力 CellOp を1件 capture し退出後も残る。mo.ui+ctx = `ran-no-error`。context 付き 50k≈0.42s（budget 5s・overhead 2.09x）＝既存 perf gate を維持。

### D6 実装着地（2026-06-18・本 slice）
- `thin_drain.py`: 単一プリミティブ `_execute_hot_cell(ctx, executor, cell, glbls, graph)` が `with ctx.with_cell_id(cell.cell_id)` で各 cell を包む。`step` が `ctx = get_context()` を step ごと1回 bind しループ内で呼ぶ（`drain` は `step` 経由＝無改修）。import は `marimo._runtime.context.types.get_context` 追加のみ。
- gate（`tests/test_strategy_runtime_thin_drain.py`・8 GREEN）: 新 `test_production_context_publishes_mo_output`（kernel stream に出力 CellOp が届く）/ `test_production_context_mo_ui_no_hard_error`（`ran-no-error`）。既存 `test_hot_path_contract_*` 2本は `executor.execute_cell` 直叩きの **control** へ降格（プリミティブ非経由）。perf/precompute/parity は不変で GREEN、offline/import-purity も GREEN＝marimo は runtime seam に漏れず spike-only 据え置き。
- 残務（不変）: D3 構造的 hot-list guard / S3 ADR 起案＋ADR-0005 supersede＋marimo prod 昇格 / S6 `KernelRunner` 載せ替え。本 module は dormant のまま。

---

## S3 実装着地（2026-06-18・ADR-0012 起案＋marimo prod 昇格＋offline gate intent 昇格）

`/grill-with-docs`（#76 S3・Q1–Q4）で設計の木を固め、**[ADR-0012](../adr/0012-marimo-embed-reactive-strategy-execution-model.md)**（marimo embed を reactive strategy 実行モデルとする）を accepted で起案。S3 が landing させたのは「docs + manifest + gate-intent」の3点で、**per-bar 配線（S6）／order 注入（S4）は未着手・thin_drain は引き続き dormant**。

- **Q1 supersede 範囲**: ADR-0012 は ADR-0005-cutover を **strategy-authoring 表面（Strategy Editor＝cell-DAG ＋ 単一 run ボタン→reactive）のみ**部分 supersede。他5表面の 1:1 TTWR parity は踏襲。ADR-0011 の facet-scoped supersede 文型を踏襲し、ADR-0005 本体は無改変（scoping は ADR-0012 側）。＝findings 冒頭/末尾の「ADR-0005 supersede」は**部分 supersede**と確定（全面ではない）。
- **Q2 dep の形**: `[project.dependencies]` に **`marimo>=0.20.4`**（PyPI 範囲 pin）。editable local fork は破棄（embed は `marimo._server` を引かず fork 不要）。`uv.lock` は **0.20.4 に解決**（検証版・破壊的 upgrade なし）。範囲 pin の安全網＝lock 固定＋thin_drain gate が private-API drift 検出。
- **Q3 offline gate**: `test_strategy_runtime_offline` は機構据え置き・intent を「dormancy」→「**lazy-import 規律**（seam は module-load で marimo を引かない／marimo 戦略実行時だけ lazy import）」へ昇格。gate が `engine.kernel.runner` を import するので **S6 が module-top に marimo/thin_drain を置けば即 RED**＝lazy import を構造的に強制。
- **Q4 execution-model 立場**: marimo cell-DAG が **唯一の target authored モデル**、命令型 `Strategy.on_bar`/`strategy_loader` は**移行期 only**（新機能を足さない frozen surface）。kernel per-bar 契約（`on_start`/`on_bar`/`on_stop`＋`ctx.submit_market`）は**不変の adaptation 境界**で marimo は App→compile→drain で adapt（S4 injected globals＋S6 dispatch）。#24 golden は命令型経路で byte-identical 保持。

gate 結果（S3）: thin_drain 8 / offline+import-purity 2 すべて GREEN（plain `uv run`・marimo prod 化で default run でも走る）。lazy-import 規律 GREEN＝seam は marimo installed でも module-load で引かない。

### S3 後の残務（順序）
**S4** cell へ `submit_market`/`portfolio` を inject（marimo 戦略が発注できるように）→ **S6** `KernelRunner` adapter/dispatch 配線（lazy import・dormant 解除）→ 命令型 sunset（さらに後の named スライス）。D3 構造的 hot-list guard は独立の任意 guard。

> 🤖 `/grill-with-docs` redesign 追補＋S3 セッション記録（Claude Code）。spike は throwaway。ADR-0012 accepted（#76 §6 の「spike 後に起案」を充足）。
