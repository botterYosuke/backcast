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

---

## S4 設計＋実装着地（2026-06-18・injected cell-facing API）

`/grill-with-docs`（#76 S4・Q1–Q2）で確定。S4 は **marimo cell が host の per-bar 契約へ adapt して発注できるようにする** スライス。thin_drain は引き続き dormant（実 `KernelRunner` 配線は S6）。実装は `engine/strategy_runtime/cell_api.py`（adapter・marimo-free）＋ thin_drain の `inject=` seed 機構。

### 確定した設計（grill）
- **Q1 values=injected driver State / actions=injected callable**: reachability は **object identity**（`runtime.py:_find_cells_for_state` の `globals[ref] is state`）で張られる＝graph-def 辺ではない。よって **値**（bar/tick/cash/position/portfolio）は host が compile 前に globals へ seed する **driver mo.state**（cell が getter を読む→identity で root 化→hot list 入り＋reactivity 辺＋no-look-ahead）。**アクション**（submit_market/log）は **injected plain callable**（fire-and-forget の副作用、reachability は読んでいる値/bar 経由）。findings 旧表の「injected global(portfolio)」と D5 の「portfolio=driver」は**排他でなく両立**＝portfolio は *injected driver State*。値の seed は既存 `_compile`（`glbls[name]` を読み `_find_cells_for_state` で identity 照合）が**無改修で**受ける。S4 の新規コードは **callable injection の seed 機構**のみ。
- **Q2 submit_market 署名 = signed quantity**: cell-facing `submit_market(qty, *, instrument_id=None)`。qty の符号が side（>0 BUY / <0 SELL）、|qty| が size、**delta order**（この bar で取引する数量＝命令型 on_bar と同契約・target position ではない）。adapter が kernel 契約（`ctx.submit_market(*, strategy_id, instrument_id, side: OrderSide, quantity>0)`）へ変換し strategy_id を host-bind・instrument_id を primary default。reactive idiom（`qty = signal * size`）の出力型そのもので分岐ゼロの sink。
- **adapter = host-owned validation の砦**（kernel に positivity guard が無いため全 host-owned 検証をここに集約）: `0`/`-0.0` → no-op（発注なし）、`NaN`/`inf` → fail-closed（`ValueError`・絶対に submit しない）。**lot 丸めはしない**（abs(qty) を float のまま渡す。J-Quants 売買単位＝venue 別レイヤ）。

### AC → 恒久 gate（behavior-to-e2e）
| 挙動（不変条件）| gate | 種別 |
|---|---|---|
| **adapter 変換**: signed qty → (side, abs(qty)) を kernel 契約へ・strategy_id host-bind・instrument_id primary default / 明示上書き | `tests/test_cell_api.py`（fake ctx・marimo-free・fast）| unit |
| **adapter 検証**: 0/-0.0 → no-op・NaN/±inf → `ValueError`（submit しない）| `..::test_cell_api`（同上）| unit |
| **injection 機構**: `open_runtime(.., inject={"submit_market": fn})` が cell globals へ seed され cell が呼べる（cold run＋hot drain 両方で resolve）| `test_strategy_runtime_thin_drain::test_injected_callable_is_callable_from_cell` | inject |
| **発注 parity**: signed-qty marimo cell-DAG（bar→signal→qty→submit_market）が命令型 on_bar twin（`submit_market(side, abs)`）と **同一の order 列**を出す | `..::test_injected_submit_market_order_parity_with_imperative_twin` | parity |
| **lazy-import 規律**: `engine.strategy_runtime.cell_api` は marimo-free（runtime seam が import 可） | `test_strategy_runtime_offline`（_CHILD import 集合へ追加）| invariant |

> 既存 `_dag_app`/`_dag_twin` parity gate は pure-compute 蓄積器で submit_market を呼ばない＝発注 parity を証明しない。S4 は signed→(side,abs) 変換を被験対象とする専用 order-parity gate を新設する。

### 実装着地（実コードで判明した2点・gate が surface）
- **cold compile run は injected action の副作用を発火させてはいけない**: `_compile` の cold `runner.run(all)` は全 cell を1度実行して globals を populate する。injected `submit_market` を素のまま seed すると、cold run が **bar 到来前に spurious order を発火**する（pure-compute cell では無害だが action cell では実害）。解決: cold run の間は injected 名に **inert no-op stub**（`_inert_action`）を seed し、cold run 後に**実 callable を arm**（同一 glbls を hot drain が使う）。これは S6 の実 KernelRunner 配線でも効く本番上の不変条件。
- **injected 名と cell-defined 名の衝突は fail-closed**（code-review CONFIRMED・tdd RED-first で修正）: cell が injected と同名を def すると arm 時に **silent clobber**（著者の値を action で上書き／spurious 発火）。`_compile` が `set(inject) & ∪cell.defs` を検出して `ValueError`。
- gate（全 GREEN・plain `uv run`）: `test_cell_api` 8（adapter 変換/検証）＋ thin_drain に inject 機構/order-parity(三-way・BUY/SELL/flat 自己 guard)/collision の3本追加。offline gate の seam import 集合に `cell_api` を追加（marimo-free 確認）。

### この slice で**やっていない**こと
実 `KernelRunner`/`_Context` への配線（S6）。S4 の adapter は fake StrategyContext で駆動・検証する standalone（dormant）。value-State の host preamble（canonical getter 名の所有）は author ergonomics 次第で S6/UI slice 判断（S4 は inject 機構が両 authoring 様式を支えることだけ確定）。

> 🤖 `/grill-with-docs` redesign 追補＋S3 セッション記録（Claude Code）。spike は throwaway。ADR-0012 accepted（#76 §6 の「spike 後に起案」を充足）。

---

## S6 設計の木（2026-06-18・`/grill-with-docs`）— dispatch 配線（dormant 解除）

`/grill-with-docs`（#76 S6）で確定。S3/S4 が前提（marimo prod dep・lazy-import 規律・injected order API）を揃えたので、S6 で **thin_drain を runtime seam へ配線し dormant を解除**する＝**初めてアプリで marimo 戦略が動く**スライス。

### スコープ決定（owner binding）— epic は full bullet、実装は順序付きスライス
epic S6 bullet（`KernelRunner` 載せ替え＋単一再生ボタン撤去＋footer transport #30 撤去＋strategy-editor #15/#16）を **done-definition** に据えるが、**1 スライスに束ねない**。コードが順序を物理的に固定する: marimo cell を走らせる Python 経路（dispatch 配線）が無いのに UI の実行手段を消すと壊れる＝dispatch 配線が UI 撤去の厳密な前提。さらに Python 配線だけで hard 制約が3本（lazy-import / #24 golden byte-identical / teardown 保証）立ち、pytest で独立に green にできる自己完結スライス。UI と結合すると runtime の clean な変更が Unity AFK/HITL の green まで人質になる（[HITL surfaces bugs AFK gates miss]）。

- **S6a（本スライス）= Python runtime dispatch 配線**: marimo 判定分岐＋`MarimoStrategy` adapter（`open_runtime` 寿命所有・teardown 保証）＋発注 parity gate＋offline gate に新 detector module 追加。ここで dormant 解除。
- **S6b 以降 = UI 表面**: strategy-editor #15/#16 → 単一再生ボタン撤去 → footer transport #30 撤去。S6a で「実際に動く」ことを示してから表面を切替。
- **Live 配線は bullet 外**＝別 epic 据え置き（adapter は mode 非依存に作るので将来 Live が再利用可だが S6 では配線しない）。

### S6a 確定設計（owner binding・コードで裏取り済み）

| # | 決定 | 機構・根拠 |
|---|---|---|
| **S6-1 adapter 形** | `MarimoStrategy(Strategy)` を kernel 契約に被せる。**`KernelRunner` 本体は無改変**（ADR-0012 D2「不変の adaptation 境界」を字義通り守り #24 golden を byte-identical 保持） | `register(ctx)` で `_Context` を捕獲 → `on_start()` で `open_runtime.__enter__`（cold compile・inject 構築）→ `on_bar(bar)` で `rt.drain(...)` → adapter `close()` で `__exit__`。発注は injected `submit_market`→`ctx.submit_market`→`ctx.pending` で **fill ループは命令型と同一**（runner.py:280–292 無改修） |
| **S6-2 teardown 保証** | dispatch 側（`_backend_impl`）が `try: KernelRunner(...).run() finally: strategy.close()` で寿命所有 | `KernelRunner.run` は loop を try/finally で包まない（runner.py:236–336）＝bar 途中 raise で `on_stop` も `__exit__` も呼ばれず thread-local marimo context が leak（次 run で "RuntimeContext already initialized" 連鎖死）。`open_runtime` 自体は `@contextmanager`＝compile-raise は自己 teardown 済み。残る穴（compile 成功後の bar 途中 raise）を dispatch finally が塞ぐ。「1 process = 1 kernel session」不変条件も run ごと完全 teardown で保つ |
| **S6-3 marimo 判定** | dispatch seam で **AST scan（module-level `marimo.App()`）**。marimo-free（offline gate を守る・ユーザ module を exec しない） | 署名 = module-level `import marimo[as X]` **かつ** module-level の `X.App`/`App` 呼び出し（import alias 解決）。`App()` 構築が authoritative（`import marimo` 単独は弱い）。`__generated_with` は必須にしない（手書き marimo 可）。`"import marimo" in text` で安価 pre-filter → AST で `App()` 確認。marimo 自身も textual guard で構造識別（codegen.py:613） |
| **S6-4 detect-first 順序** | AST scan を先（無 exec・marimo-free）。App あり → marimo adapter 分岐（thin_drain を lazy import）／App 無し → 命令型分岐 → `strategy_loader.load(base_cls=Strategy)` | これで `load()` の `StrategyLoadError`（no/multiple subclass・import 失敗の3理由・strategy_loader.py:91/117/120）は**本物の命令型エラーのまま**＝壊れた命令型を marimo へ誤ルートしない（exception-as-control-flow 回避）。`SyntaxError`（parse 不能）は kind を推測せず load エラーとして表面化 |
| **S6-5 hybrid（App と Strategy 両方）** | `App` を authoritative とし **marimo 優先**（document） | `App` = notebook の決定的シグナル |
| **S6-6 driver 契約（host-seed・S4 先送りを確定）** | **host が canonical driver state を seed**（host が名前もオブジェクトも所有）。author は `bar = get_bar()` を builtin 同然に読む | author-defined（gate 式 `get_bar, set_bar = mo.state(...)`）でも host は drain で `{"get_bar": …}` を書き `drivers=["get_bar"]` を宣言＝**name-ownership は結局 host 側**で、author にマッチを強い失敗モードが「compile 落ち（no-reader/KeyError）」。host-seed はマッチ seam ゼロ＝durable author API として厳密に上。`editor #15/#16` 未着の今が author base ゼロ＝移行コスト最小の確定タイミング（後で author-defined→host-seed は名前衝突＝移行になる）。#64「Strategy Editor = cell・最小 ceremony」とも整合 |
| **S6-7 S6a の SET = bar のみ** | host-seed `get_bar` → **Bar オブジェクト全体**（`bar.close` 等）＋ injected `submit_market` | portfolio/position/cash の cell 露出は**直後の追加スライス**（同一 host-seed 機構の純追加・S4 Q1 の precision = prev-bar snapshot で no-look-ahead / 単一 `get_portfolio()` snapshot で granularity 最小 / fail-closed「読まれた driver だけ declare」を持ち込む）。delta 発注の tracer bullet として bar-only で成立 |
| **S6-8 on_order/denial の cell 可視性** | S6a では cell から不可視（adapter `on_order` = no-op）。`KernelRunner` は fill/denial を通常処理（`ctx.pending`→fill→portfolio→sink） | cell は reactive＝event callback を持たない。fill/denial の反映は **portfolio-driver スライス**で next-bar snapshot として入る（S6-7 と同時に解禁） |
| **S6-9 scenario / 起動メタ** | marimo 分岐は `_load_strategy` を通らない（Strategy サブクラス無し）ので **`load_scenario(path)` を直接呼ぶ**（sidecar JSON 優先・命令型と共有）。instruments/granularity/start/end/cash を取得 | strategy_id は host-bind、`default_instrument_id` = `instruments[0]`（S4 Q2）。file テキストは #78 provider の canonical .py path から読む（cwd/identity は ADR-0011） |

### thin_drain への小拡張（S6a で本物化）
1. **seed パスを型で分岐**: driver State は **real State を seed**（cold run でも読む＝cell が `get_bar()` を free-ref で resolve・identity で root 化）、アクションは現 `_inert_action` のまま arm。現 `_compile`（thin_drain.py:283/304）の inject= は**アクション専用**（docstring:273「host が bar 間に書く state は inject しない」）＝S4 が S6 へ送った当の論点。host-seed driver State は別経路で cold-run 前 seed。
2. **`_compile` docstring（line 273）更新**: interim 記述「host-written states are NOT injected」→「driver State は host-seed 可（D5 の roots = seed＋declare した subset ちょうど）」へ完成。
3. **fail-closed 再利用**: 全 host 名を seed、driver 宣言は cell.refs に現れた value-State subset だけ（`submit_market` は never declare）。S6a は単一 driver（`get_bar`）なので既存 no-reader 拒否で足り、subset ロジックの本番化は portfolio スライス。

### AC → 恒久 gate（behavior-to-e2e: backcast に FLOWS.md 無し＝gate がテスト正本）
| 挙動（不変条件）| gate | 種別 |
|---|---|---|
| **marimo 判定**: `marimo.App()` を持つ .py は marimo・持たない .py は imperative（無 exec・marimo-free）| 新 detector module の unit（AST 各分岐＋hybrid＋SyntaxError）| unit |
| **production-binding 発注 parity**: `_backend_impl` の dispatch を**実際に通した** host-seed bar の marimo 戦略が命令型 twin と同一 order 列／fill／equity | dispatch を駆動する parity gate（現 author-defined `test_injected_submit_market_order_parity` は機構単体テストとして残置・production 契約ではないと明記）| parity |
| **teardown 保証**: bar 途中 raise でも marimo context が leak しない（次 run が "already initialized" にならない）| 連続 run + 途中 raise の regression | invariant |
| **lazy-import 規律**: 新 detector module は marimo-free・seam は marimo 戦略実行時だけ lazy import | `test_strategy_runtime_offline`（_CHILD import 集合へ detector を追加）| invariant |
| **#24 golden 不変**: 命令型経路は byte-identical（`KernelRunner` 無改変）| 既存 #24 golden gate（不変で GREEN）| golden |

> 🤖 `/grill-with-docs`（#76 S6）セッション記録（Claude Code）。実装は tdd RED-first（gate を先に赤く）＋ code-review(simplify)。ADR-0012 は「方針」として参照（自己保護条項＝編集せず本 findings に下位事実を記録）。

### S6a 実装着地（2026-06-18・dormant 解除）

`/grill-with-docs`（設計の木）→ `/behavior-to-e2e`（AC→pytest gate）→ `/tdd`（RED-first 縦スライス）で実装。thin_drain を **runtime seam（`_backend_impl` の Replay dispatch）へ初配線**＝**dormant 解除**。アプリで marimo `.py` を Replay 実走できるようになった（初めてアプリ挙動が変わる #76 スライス）。

成果物（4 縦スライス・各 RED→GREEN）:
- `engine/strategy_runtime/strategy_kind.py`（新）: marimo-free AST detector（`is_marimo_app_source/_file`）。`tests/test_strategy_kind.py`（7）。offline gate の `_CHILD` import 集合へ追加。
- `engine/strategy_runtime/thin_drain.py`: `open_runtime`/`_compile` に **`driver_seeds={name: initial}`**（host-owned driver State を in-context `mo.state()` で生成→getter を cold-run 前 seed→free-ref read を root 化）。`drivers` は author-defined 経路として残置（既存 gate）。`tests/test_strategy_runtime_thin_drain.py` に host-seed 2 本追加（計 15）。
- `engine/strategy_runtime/marimo_strategy.py`（新）: `MarimoStrategy(Strategy)` adapter。`register`→ctx 捕獲 / `on_start`→`load_app`＋ExitStack で `open_runtime`（neutral Bar seed・`submit_market` inject）/ `on_bar`→drain / `close`→teardown。
- `engine/_backend_impl.py`: `_select_replay_strategy(strategy_file)`（detect-first 分岐・marimo は lazy import）＋ `_start_engine_duckdb` に配線＋ **run を `try/finally: strategy.close()` で包む**（teardown 保証）。`tests/test_marimo_strategy_adapter.py`（4: production-binding parity / teardown / dispatch 2）。

実コードで surface した非自明点（gate が束縛）:
- **`push_order` が受けるのは `OrderFilled`**（`.last_qty`/`.last_px`・`.quantity` ではない）。parity sink はこの形で記録。
- **teardown の必要性が cascade で実証**: parity test の sink バグ（`.quantity`）が run を mid-bar で raise → `on_stop`/`close` が呼ばれず marimo context leak → 次 run が "RuntimeContext already initialized"。KernelRunner.run に try/finally が無い事実を実機で確認＝S6-2 の dispatch finally が load-bearing。
- **neutral Bar seed**: cold compile は bar 到来前に全 cell を1度実行するので、driver は `close=0.0` の neutral Bar で seed（結果は hot drain が上書き）。author の div-by-zero は compile 時に surface（fail-fast）。
- **`load_app` は context を残さない**・手書き marimo（`__generated_with` 無し）も読める（実機確認）。

gate 状況: S6a 34 本＋golden/imperative 11 本＋全 suite 326 passed（既知の `test_login_subprocess_env` macOS path-separator quirk のみ fail＝本スライス無関係・`_login_subprocess_env` 未変更）。#24 golden byte-identical（KernelRunner 無改変）。offline/import-purity GREEN（seam は marimo を module-load で引かない）。

`code-review(simplify)`（high・2 ラウンド）で解消した指摘:
- **HIGH（crash）**: production は `strategy_file` を **str** で渡す（`backend_service` の `cfg.get`）が `_select_replay_strategy` の marimo 分岐が `load_scenario(str)` を呼び `_sidecar_path(...).with_name` で `AttributeError`＝**全 marimo run が STRATEGY_LOAD_ERROR**。dispatch test が Path を渡して masking していた。→ `Path(strategy_file)` 強制＋**dispatch test を str 入力へ**（regression guard）。
- **MEDIUM（error taxonomy）**: marimo の notebook load を **dispatch で eager に**（`load_app` を factory 前で実行・None→`StrategyLoadError`）。これで「ファイルが壊れている」は命令型と対称に **STRATEGY_LOAD_ERROR**、cold compile（on_start の analog）失敗だけ RUN_FAILED に揃う。adapter は `app_path=` → **`app=`（loaded App）** へ変更。
- **LOW/cleanup**: `driver_seeds` の clobber guard（cell が seed 名を def→fail-closed・inject と対称）／import 収集を module-level（`tree.body`）に制限／`_compile` の冗長 local `import asyncio` 撤去／no-op `register` override 撤去。
- 2 ラウンド目: 新規 Medium+ ゼロ（ctx 捕獲・同一スレッド・`tree.body` 検出・clobber 偽陽性なしを実機確認）。

未着手（S6a 範囲外）: portfolio/position の cell 露出（直後スライス）／UI 表面 S6b（#15/#16→単一ボタン撤去→footer #30）／Live 配線（別 epic）／D3 構造的 hot-list guard（任意）。

> 🤖 `/grill-with-docs`＋`/behavior-to-e2e`＋`/tdd`＋`/code-review(simplify)` セッション記録（Claude Code）。

---

## portfolio-driver スライス（2026-06-18・`/grill-with-docs`）— `get_portfolio()` cell 露出

`/grill-with-docs`（#76）で確定。S6a が `get_bar` 単一 driver で delta 発注の tracer bullet を通したのに続き、**marimo cell が `get_portfolio()` で建玉/現金/口座評価を読めるようにする**スライス（S6-7 が「直後の追加スライス」と名指したもの・同一 host-seed 機構の純追加）。これで **target-position 戦略**（`delta = target - get_portfolio().position`）が書ける。`KernelRunner` は無改変＝#24 golden byte-identical。Live 配線は別 epic（adapter は mode 非依存）。

### 確定した設計の木（owner binding・コードで裏取り済み）

| # | 決定 | 機構・根拠 |
|---|---|---|
| **P1 snapshot 形＝full glossary** | `get_portfolio()` は frozen `PortfolioSnapshot` を返す。フィールド = **cash / equity(MTM) / realized_pnl / position(primary 銘柄の signed net qty) / positions(全建玉の signed mapping)** ＋ `net_qty(instrument_id)`。CONTEXT.md glossary「口座評価額(equity)/現金(cash)/買付余力(buying_power)」の 3 値モデルをそのまま cell へ露出（equity は MTM・cash は realized 現金） | granularity 最小＝**単一 driver state**（`get_portfolio`）。値は複数だが driver は 1 本。reactive idiom は `get_portfolio().position` / `.cash` / `.net_qty("XXXX.T")` |
| **P2 読み seam＝ctx.portfolio_snapshot()** | `StrategyContext` Protocol を **additive 拡張**（`submit_market`/`buying_power`/`log` に並ぶ読み seam）。snapshot 型＋会計変換は `engine.kernel.portfolio`（`Portfolio.snapshot(prices, primary_instrument_id)`・marimo-free・layering 上 kernel 所有）、価格 view との合成入口を `_Context.portfolio_snapshot(instrument_id=None)` に置く（`mark_to_market_equity(self.reference_prices)`＋`cash`＋`realized_pnl`＋`net_signed_qty`）。Live `_Ctx` にも同名を実装（Protocol 整合・buying_power と対称の kernel-mirror。venue-余力 統合は将来 epic） | equity の正しさは `_Context` だけが同時に持つ 2 つ（`Portfolio` 会計＋`reference_prices`）に依存。adapter が `_portfolio`/`reference_prices` を内側読みして MTM/符号/realized 規約を再実装するのは S6a adapter の役割（「marimo を kernel 契約に adapt」）に重すぎる＝kernel が会計と pricing を所有 |
| **P3 no-look-ahead＝prev-bar snapshot** | adapter は `on_bar(N)` 入り口（drain 前）で `ctx.portfolio_snapshot(self.instrument_id)` を取り `get_portfolio` driver へ write。`KernelRunner` は `on_bar(N)` を **bar N の fill より前**（runner.py:269 → fill は 288）に呼ぶので、snapshot の cash/position は **pre-this-bar-fill＝end-of-(N-1)**。equity は `reference_prices`（bar N close overlay 済み・fill 価格と同一・runner.py:267/285）で MTM＝決定時点の最新値（look-ahead ではない＝bar は closed） | target-position 戦略の parity がそのまま no-look-ahead gate になる: cell が post-fill position を見たら BUY 直後に position 反映→delta=0 で命令型 twin（pre-fill 読み）と乖離する |
| **P4 subset declare＋empty-roots 拒否** | host は `{get_bar, get_portfolio}` を **speculative seed**（cell がどれを読むか事前に不明）。`_compile` は cold graph 解析後、**reader cell があった subset だけ** setter 構築・root 化・毎 bar write。読まれない facet は globals に seed されたまま（free-ref 解決用）だが宣言しない。最後に **roots が完全に空なら `ValueError`**（dead strategy＝毎 bar no-op を fail-closed） | thin_drain 拡張 #3「読まれた driver だけ declare」の本番化。bar-only も portfolio-only も有効。`driver_seeds` の no-reader は「skip」へ緩和（S6a の単一 driver no-reader 拒否は empty-roots 拒否へ役割移管）。**author-defined `drivers`（明示命名）の no-reader 拒否は不変**（明示宣言した driver が読まれない＝著者バグ） |
| **P5 active_drivers＝adapter の write 契約** | `StrategyRuntime.active_drivers`（= `frozenset(setters)`）を公開。adapter は open 後に subset を取得し、**active な driver だけ** drain へ渡す（`get_portfolio` が active な時だけ snapshot を組む＝unread portfolio の組立コストも消える）。`drain` は strict 維持（未知名 setter 欠落で KeyError＝adapter バグ検出） | subset で「seed したが未宣言」の facet を adapter が誤って write すると drain が KeyError。active_drivers で adapter が write 対象を正確に knowする |

### neutral seed（cold compile）
adapter は `on_start` で `driver_seeds={get_bar: neutral_bar(close=0), get_portfolio: ctx.portfolio_snapshot(self.instrument_id)}`。`on_start` 時 `reference_prices={}`・portfolio flat＝snapshot は cash=initial_cash・equity=initial_cash・position=0（`mark_to_market_equity({})`＝flat→cash）。cold run は全 cell を1度実行（submit_market は inert）。著者の `target/position` 等の div0 は compile 時 surface（fail-fast・neutral bar close=0 と同型）。

### AC → 恒久 gate（behavior-to-e2e: backcast は findings＋pytest が正本）
| 挙動（不変条件）| gate | 種別 |
|---|---|---|
| **snapshot 会計**: `Portfolio.snapshot` が cash/equity(MTM at prices)/realized_pnl/position/positions/net_qty を正しく出す・frozen | `test_portfolio_snapshot.py`（pure・fast）| unit |
| **ctx 合成**: `_Context.portfolio_snapshot(iid)` が `reference_prices`＋live portfolio を合成し pre-fill position を返す | `test_kernel_buying_power_seam` に追加（実 runner・monkeypatch bars）| unit |
| **subset declare**: bar-only→`{get_bar}` active / portfolio-only→`{get_portfolio}` active / 両読み→両 active / 無読み→empty-roots `ValueError` | `test_strategy_runtime_thin_drain`（host-seed 群に追加）| subset |
| **active_drivers**: `rt.active_drivers` == reader subset ちょうど | 同上 | invariant |
| **production-binding target-position parity（no-look-ahead）**: `delta = target - get_portfolio().position` の marimo 戦略が命令型 twin（同じ pre-fill position 読み）と order/fill/equity 一致 | `test_marimo_strategy_adapter`（実 KernelRunner）| parity |
| **lazy-import 規律**: `kernel.portfolio`/`ctx.portfolio_snapshot` は marimo-free（seam は引かない）| `test_strategy_runtime_offline`（不変で GREEN）| invariant |
| **#24 golden 不変**: 命令型経路は byte-identical（KernelRunner 無改変・Protocol 拡張は命令型が呼ばない）| 既存 golden gate | golden |

方針: [ADR-0012](../adr/0012-marimo-embed-reactive-strategy-execution-model.md)（自己保護・編集せず本 findings に下位事実を記録）。`StrategyContext` の `portfolio_snapshot` 追加は ADR-0012 D2「不変の adaptation 境界」の **additive 拡張**（既存 `buying_power`/`log` と同列の読み seam・命令型経路は呼ばず golden 不変）。

### 実装着地（2026-06-18・本 slice）
- `kernel/portfolio.py`: `PortfolioSnapshot`（frozen・cash/equity(MTM)/realized_pnl/position/positions＋`net_qty`）＋ `Portfolio.snapshot(prices, primary_instrument_id)`。
- `kernel/strategy.py`: `StrategyContext` Protocol に `portfolio_snapshot` 追加＋ `Strategy.portfolio_snapshot()` helper（`buying_power` と対称・register 前は fail-loud）。
- `kernel/runner.py`: `_Context.portfolio_snapshot(iid)`＝`portfolio.snapshot(self.reference_prices, iid)`（on_bar 入り口の pre-fill book）。`kernel/live/driver.py`: `_Ctx.portfolio_snapshot`（kernel-mirror・Live venue 統合は将来 epic）。
- `strategy_runtime/thin_drain.py`: driver loop を subset 化（host-seed no-reader=skip・author `drivers` no-reader=従来通り reject・最後に empty-roots reject）＋ `StrategyRuntime.active_drivers`。
- `strategy_runtime/marimo_strategy.py`: `get_portfolio` driver を speculative seed（neutral=on_start の flat snapshot）＋ on_bar は `active_drivers` の subset だけ write（snapshot は get_portfolio が active な時だけ組む）。
- gate: 全 suite **355 passed**（新規: snapshot 単体 6／subset・active_drivers／target-position parity=no-look-ahead）。#24 golden byte-identical・offline/import-purity GREEN（kernel.portfolio/ctx.portfolio_snapshot は marimo-free）。
- `code-review(simplify)`（high・8 finder angle）: **CONFIRMED 1**＝`PortfolioSnapshot` が `frozen=True`＋`MappingProxyType` で生成 `__hash__` が unhashable→`hash()` TypeError（「値」契約違反）→ `positions = field(hash=False)`（eq には残す）で修正＋hashable 回帰 gate。他候補（unconditional neutral_pf seed／`is not None` vs `""`／Live equity-cash）は production ctx が Protocol 実装済み・kernel の空 instrument→0.0 と整合・Live は本 slice 範囲外で REFUTED。cleanup 3 件は Low（Medium+ 残ゼロ）。

### 残務（順序・不変）
**S6b UI**（#15/#16→単一ボタン撤去→footer #30）→ Live 配線（別 epic）→ D3 構造的 hot-list guard（任意）。

> 🤖 `/grill-with-docs` セッション記録（Claude Code）。実装は tdd RED-first＋`code-review(simplify)`。

---

## S6b 設計の木（2026-06-18・`/grill-with-docs`）— UI パラダイム移行＋v19 marimo 移植

`/grill-with-docs`（#76 S6b）で確定。S6a＋portfolio スライスで **marimo `.py` は既に production の再生ボタンから実走する**（C# `BackcastWorkspaceRoot.OnRun → start_engine{strategy_file} → _backend_impl._start_engine_duckdb → _select_replay_strategy`、コードで裏取り済み）。S6b に **Python 実走の残務は無い**。残るのは UI パラダイム移行（命令型＋transport replay → reactive cell-DAG＋単一 Run）と、それを実証する **v19 の marimo 移植＋parity**。

### 現在地（grill 冒頭の逆向きチェック・git/code）
- 先行スライス・常時不変条件に **blocked されていない**：#15/#16/#30 の UI 表面は **すべて `BackcastWorkspaceRoot`（#59 本線合体）に production-wired**（gh の「未マージ branch」は stale）。portfolio スライスは着地済（作業ツリー未コミット）。
- 現状の run 入口は **2つ**：`ScenarioStartupTile` の Run ＋ `ReplayFooterView` の ▶（両方 `OnRun()` に収束）。footer は transport だけでなく **mode segments（Replay/LiveManual/LiveAuto）と run 入口 ▶** を持つ。
- 現存戦略は `v19_morning.py` ただ1つ＝**命令型**（marimo 実例ゼロ）。`File→New` は空エディタ（テンプレ無し）。

### 確定（owner binding）

| # | 決定 | 機構・根拠 |
|---|---|---|
| **B1 footer transport 撤去** | transport（play/pause/step/speed/stop）を UI から撤去。**footer は存続**＝mode segments＋**アプリ全体で単一の Run 入口**。`ScenarioStartupTile` の Run は撤去し設定タイルは scenario 編集専用へ。**完成形・仮状態なし** | reactive drain は native 速度（0.3s/50k）で run→即完了＝scrub の affordance が古い。#30 transport は **ADR-0012（reactive 実行モデル）が supersede**（self-protection＝ADR 編集せず本 findings に記録）。footer は mode chrome＋global run として役割更新（`ReplayFooterView`→`WorkspaceFooterView` 的にリネーム候補）。参照消滅した `ReplayTransportViewModel`/`ReplayLifecycle`/#30 専用 enablement は削除、Python の pause/step/speed/stop RPC は production surface から退役（internal force-stop/teardown は host lifecycle 用に名前/所有者を整理して残す） |
| **B2 editor=cell-DAG 既定化（widget 無改修）** | エディタ widget は無改修（marimo .py = plain Python＝既に編集可）。`File→New` が marimo App skeleton を seed・canonical/picker 上の既定戦略を marimo 版へ。命令型 v19 は legacy oracle/parity fixture として残置（**実走は sunset まで残る**が著者導線の正面に置かない） | #64「Strategy Editor = cell・最小 ceremony」＝marimo の plain-.py 形式で `@app.cell` 関数が cell。cell-aware editor（cell 境界描画/per-cell run）は不要（別途・任意）。命令型 sunset は後続 named スライス＝S6b 範囲外 |
| **B3 v19 を marimo 移植（multi-instrument 含む）** | S6b の完成 gate は「小さい toy parity」ではなく **marimo-v19 vs 命令型-v19 で同一 bars・model stub・cash から order/fill/equity 一致**（mount 非依存の deterministic parity を必須・実データ gate は mount 依存 skip 可）| owner 方針「完成形・仮状態を残すな」＝最小例で済ませて v19 を後続に逃がさない。v19 はマルチ instrument のクロスセクショナル ML ランカー（universe `_snapshots` 蓄積→10:00 JST ranking→複数銘柄発注） |
| **B4 driver=Option1（bar driver 不追加）** | **新しい host-owned multi-instrument bar driver は作らない**。マルチ instrument 性は ① 発注=`submit_market(qty, instrument_id=iid)`（S4 Q2・済）② 履歴=strategy 所有の `mo.state` feedback dict `snaps[iid]→[closes]`（D4 self-cycle・per-`get_bar()` 蓄積）③ 決定時クロスセクション=蓄積済 feedback dict 読み（朝場で全銘柄 stream 済＝live read 不要）④ 退出 position=`get_portfolio().positions`。唯一の真の host gap は **`buying_power` の cell 露出**（cash と同型の純追加・ctx には既存 seam） | **spike で実証**（下記）。no-look-ahead が自然一致：v19 `_enter` は `minute<entry` の bar しか append せず entry bar を含めない＝**決定は prev-bar 蓄積を読む**＝marimo の bar-crossing feedback（前 bar 値読み）と完全一致 |

### driver-shape spike（throwaway・`python/spike/marimo_embed/v19_shape.py`・`[V19-SHAPE PASS]`）
owner が「完成形 API を spike 無しで凍結しない」と要求。3-instrument の minimal marimo app（feedback dict 蓄積→entry top-2 rank→multi-iid submit→exit flatten）を **実 `KernelRunner`＋`MarimoStrategy` adapter** で命令型 twin と突合し全一致。潰した3リスク:
1. **feedback dict 持続/更新**：朝場 A/B/C close が drain をまたいで蓄積（hot path で安定）
2. **entry 時 snapshot 完備＋entry bar 非 append**：top-2=A,B が prev-bar 蓄積から ranking。fill 価格 `B@902`（B の entry-minute bar は A の entry drain より flat-list 後＝prev close でフィル）が命令型と byte 一致＝no-look-ahead 正しい
3. **multi-iid production parity**：order/fill/equity 完全一致（adapter の `instrument_id=` ルーティングが実 fill まで命令型と同一）

⚠️ spike は fixed qty（cash-aware 未行使）。`buying_power` 経路（v19 `_cash_aware_picks`/`_alloc_a0_equal_nominal_e1`）は **B4 の純追加 gap**として v19-port スライスで露出＋検証する（snapshot に `buying_power` 追加 or injected callable・cash と同型で低リスク）。

### 実装スライス（順序・各 committable で coherent）
コードが順序を固定する。**完成形・仮状態なし**は「中間に half-built な footer を出さない」意＝各中間も自己 coherent（footer transport は α/β の間そのまま動く）:
1. **S6b-α（Python）**: `buying_power` cell 露出（snapshot 追加 or inject）＋ v19 を marimo cell-DAG へ移植（`python/strategies/v19/v19_morning_cell.py` 等）＋ **mount 非依存 deterministic v19 parity gate**（marimo-v19 ↔ 命令型-v19）。命令型 v19 は oracle/fixture として残置。
2. **S6b-β（C#/Unity）**: `File→New` の marimo template seed＋canonical/picker 既定を marimo へ。run 入口単一化（`ScenarioStartupTile` の Run 撤去・footer ▶ へ統合）。HITL 要。
3. **S6b-γ（C#/Unity＋Python cleanup）**: footer transport（pause/step/speed/stop）撤去＋`ReplayTransportViewModel`/`ReplayLifecycle` 等の参照消滅削除＋`ReplayFooterView` リネーム＋Python transport RPC 退役（internal teardown は残す）。HITL 要（旧 transport 機能喪失の owner 確認）。

方針: [ADR-0012](../adr/0012-marimo-embed-reactive-strategy-execution-model.md)（自己保護・編集せず本 findings に下位事実を記録）。B1 の「#30 transport 撤去」は ADR-0012 の reactive 実行モデルが strategy-authoring 表面を supersede したことの UI 帰結＝additive な新 ADR は不要（reactive モデルが target ＝transport は旧 affordance）。

### S6b-α 実装（着地中）
- **step1 buying_power 露出（着地・commit `5a07232`）**: `PortfolioSnapshot` に `buying_power` field 追加（cell は `get_portfolio().buying_power` で cash-aware sizing）。`Portfolio.snapshot(.., *, buying_power=None)` は純会計のまま default=cash、ctx seam（`_Context`/`_Ctx`）が `self.buying_power()` を渡す（Replay=cash・Live=venue provider・pre-fill＝no-look-ahead）。gate=snapshot 単体＋**biting** production parity（small cash で block・vacuous でない）。全 suite 357 passed。
- **step2 v19 marimo 移植＋deterministic parity（残）**: 下記 model-seam 決定で設計確定。

### v19-port model seam 決定（2026-06-18・grill・owner binding）
**A'＝host-owned scorer SERVICE 注入**（モデルオブジェクトでも cell 内 load でもない）:
- marimo-v19 cell は **features→ranking→cash-aware sizing→multi-iid submit→exit** の戦略ロジックに集中。ML 採点は host が注入する **`score(features)` callable**（service）。
- production は host が canonical `.py` path/sidecar/artifact から **lazy scorer** を組む（初回呼び出しで `joblib.load()`→`predict`）。cell に I/O・sklearn 依存・遅延ロードを漏らさない（#76 の「cell＝strategy logic」と整合）。
- deterministic parity gate は marimo-v19 と命令型-v19 twin の**両方に同一 stub scorer を注入**＝mount 非依存が構造的に保証。
- ⚠️ **actions と services の分離（owner 指摘・完成形要件）**: 現 `inject=` は cold run 中に callable を **inert stub** 化する（`submit_market` 等 action の spurious 発火を防ぐ S4 設計）。**scorer は値を返す service**なので inert 化すると footgun（cold run で None 返し）。→ thin_drain に **`services=`（cold run でも live な値返し callable）** を `inject=`（action）と別経路で追加する。v19 の neutral cold seed は minute<entry で score path 未到達だが、完成形として services を構造的に分ける。
- **parity 精度の港戦略**: v19 の pure helper（`_compute_features`/`_cash_aware_picks`/`_alloc_a0_equal_nominal_e1`）を **共有 module へ抽出**し marimo-v19 と命令型 v19 が同一関数を import＝float 演算が構造的に一致（再導出しない＝byte parity リスク最小化）。marimo-v19 file は薄い cell-DAG（orchestration）に保つ。

### S6b-α step2 設計の木（2026-06-18・`/grill-with-docs`・owner binding）
step2 の未解決下位分岐を grill で確定（Q1–Q9）。コードで裏取り済み（thin_drain の inject/driver_seeds 経路・既存 v19 gate の `_StubModel` 注入規約・`MarimoStrategy` の strategy-agnostic な on_start）。

| # | 決定 | 機構・根拠 |
|---|---|---|
| **T1 scorer seam＝`score(rows)→scores`** | 注入 service は **cross-sectional z-norm + `model.predict`** を所有。cell は shared helper で per-iid 特徴 dict を作り `score_v19_rows(rows)` を呼ぶだけ（cell に pandas/z-norm/sklearn/I-O を漏らさない・findings の「`score(features)`」字義通り） | z-norm は cell の戦略分岐ではなく model 前処理＝service 側が正。`predict(X)`-only 案（B）は z-norm/DataFrame 組立を cell へ押し出し v19 移植として重い |
| **T2 z-norm＝共有・順序は契約** | z-norm+predict を共有 `score_universe(rows, model)` に抽出＝命令型 `_score_instruments` と host scorer factory の**両方が同一関数を call**。stub model を両 path へ同一注入で byte-parity 構造保証。`rows` は **universe 挿入順**で組む（top-k tie / stub の deterministic parity 保護） | service 内で z-norm 再実装すると float drift。順序差（universe 順 / rs_ref skip / None drop）も parity を揺らすので `build_rows` も共有（T8） |
| **T3 config 切り分け＝B** | **knobs（top_k/entry/exit/order_qty/cash_gate/safety_margin/alloc_policy/lot_size）= author cell 定数**（strategy authoring・#64「editor=cell」）。**universe（順付き）+ rs_ref = host 注入**（artifact 由来・run identity）。**cold/static config で per-bar driver にしない**（mo.state にしない） | gate は命令型 twin ctor params と cell 定数を同値固定（fixture が束縛・全 host 注入は不要） |
| **T4 thin_drain API＝services= ＋ constants= 分離** | 新 param 2 本。**`services=`**＝値返し live callable（`score_v19_rows`）。**`constants=`**＝immutable static data（`universe: tuple`/`rs_ref: str`・大 dict は `MappingProxyType`）。両者とも **cold run 前に real value を seed**（inject の inert→arm は services/constants には**不適用**＝services は cold で live＝owner の anti-footgun 要件）・**clobber guard は inject と対称**・setter/root なし（reachability は読む bar/portfolio 経由） | 同じ「live static free-ref」でも callable と data は意味/失敗モードが別。constants を immutable に寄せ cell の universe mutation で順序契約が壊れる道を塞ぐ |
| **T5 twin＝本物の `V19MorningStrategy`** | gate の oracle は **production v19 を shared helper 呼び出しへ behavior-preserving refactor**し `._model = _StubModel()`（既存規約 `test_v19_replay_core.py:91`）を注入。marimo 移植が**実際に traded される v19** と一致することを証明 | 別 test twin（B）は production v19 と drift しても gate 緑＝slice の意味が弱る。refactor は score 計算の置き場共有のみ（発注/timing 不変）＝RED-first 特性化（既存 v19 gate）で挙動不変を固定してから進む |
| **T6 prod wiring＝gate-first** | step2 = thin_drain `services=`/`constants=` ＋ `MarimoStrategy(.., services=, constants=)` ctor passthrough ＋ **real-v19 deterministic parity gate（stub 直接注入）**。**runtime API は完成形**。v19-specific production scorer resolver（sidecar spec→lazy joblib scorer discovery・universe source・picker default）は **named follow-up** | B3「実データ gate は mount 依存 skip 可・mount 非依存 deterministic が必須」＋ B2「marimo 既定化＝β」。α は parity 証明に集中＝committable。仮状態ではない（API は whole・v19 discovery だけ後送り） |
| **T7 fixture＝複数日（daily reset exercise）** | synthetic bars は **≥2 JST 日**（各日 09:5x snaps→10:00 entry→14:55 exit ＋ 日境界 reset）。marimo の day-tracking feedback（`get_day` 等）＋ snaps/placed/exited reset が v19 `_reset_day` と byte 一致することを gate が証明。`_jst_day_minute` を共有（境界整合） | v19 の本質＝「一日一プロセス」。既存 `test_v19_timing_logic_daily_roundtrips`（2 営業日）と整合。観測点: 日付変更 reset・2 日目も entry・各日 entry/exit 1 回・entry bar 非 snapshot |
| **T8 共有 module＝`v19_core.py`** | `python/strategies/v19/v19_core.py`。抽出: `_jst_day_minute` / `compute_features` / `current_price` / **`build_rows`**（universe 順 assembly・順序契約を構造化）/ `score_universe(rows, model)`（pandas lazy・**sklearn 非依存**＝model duck-typed）/ `cash_aware_picks` / `alloc_a0_equal_nominal_e1`。先頭 `_` を外し public-ish（移行 commit は compat wrapper 可）。**cell は `score_universe` を import しない**（host 注入 `score_v19_rows` 経由）が他 pure helper は import | 命令型 v19 と marimo-v19 が同一正本を共有＝再導出ゼロ |
| **T9 marimo file＝`v19_morning_cell.py`・α は sidecar なし** | `python/strategies/v19/v19_morning_cell.py`。**α は .py のみ ship**（gate reference）。**sidecar（scenario + scorer-spec）と dispatch 配線は production resolver follow-up で一緒に**（runnable になった時点で sidecar も出す＝「走れると偽る sidecar」を避ける）。gate は sidecar 不要で direct `MarimoStrategy(app=.., services=.., constants=..)` を駆動 | α の成果物は parity-proven reference cell strategy＝まだ picker/canonical production artifact ではない（β が既定化） |

### marimo-v19 cell-DAG の形（T1/T3/T7 から）
```
_config cell:    universe = get_universe()/free-ref constant; rs_ref; TOP_K=…; ENTRY/EXIT minute; ORDER_QTY; CASH_GATE; …(author 定数)
_state cell:     get_day/set_day, get_snaps/set_snaps, get_placed/set_placed, get_exited/set_exited (mo.state feedback)
_accumulate cell: bar=get_bar(); day,minute=_jst_day_minute(bar.ts); 日変化なら snaps/placed/exited reset; minute<ENTRY なら snaps[iid] へ OHLCV 蓄積
_decide cell:    bar/pf=get_portfolio(); entry時=build_rows(snaps,universe,rs_ref,…)→score_v19_rows(rows)→rank top_k→cash_aware_picks(top,snaps,pf.buying_power,…)→submit_market(qty,instrument_id=iid); exit時=pf.positions を flatten
```
no-look-ahead は spike で実証済（entry bar 非 append＝prev-bar 蓄積を読む＝marimo bar-crossing feedback と一致）。

### 実装順（各 RED-first・committable で coherent）
1. **thin_drain `services=` ＋ `constants=`**（RED: services が cold で live に値返し / constants が immutable seed / 両者 clobber guard / offline gate の seam import 集合は不変）。
2. **`v19_core.py` 抽出＋命令型 v19 refactor**（RED: 既存 v19 gate を characterization として先に走らせ・抽出後も byte 不変＝挙動保存）。
3. **`MarimoStrategy(.., services=, constants=)` ctor passthrough**（`on_start` で `open_runtime(.., services=, constants=)` へ）。
4. **`v19_morning_cell.py`** cell-DAG 作成。
5. **deterministic v19 parity gate**（複数日 synthetic・real `V19MorningStrategy`＋stub model oracle・marimo は同一 stub を `services=` 経由・assert fills/equities/(fills,final_cash,realized_pnl)・fixture guard で multi-iid 発注＋cash gate bite を非 vacuous 化）。
6. 全 suite＋#24 golden byte-identical＋offline/import-purity GREEN。

### 残務（順序・不変）
**S6b-α step2**（設計＝上記 T1–T9 ＋実装順で確定。実装着手）→ **v19 production scorer resolver**（T6 follow-up: sidecar scorer-spec→lazy joblib scorer discovery ＋ `v19_morning_cell.json` sidecar ＋ dispatch 配線・universe source 確定）→ **S6b-β**（template/canonical＋run 単一化・HITL）→ **S6b-γ**（footer transport 撤去＋cleanup・HITL）→ Live 配線（別 epic）→ D3 構造的 hot-list guard（任意）。

> 🤖 `/grill-with-docs`（#76 S6b）セッション記録（Claude Code）。driver-shape は throwaway spike で実証・step2 設計の木は T1–T9 で確定。ADR-0012 は「方針」として参照（自己保護条項＝編集せず本 findings に下位事実を記録）。
