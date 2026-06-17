# marimo-embed Spike 結果: `App.embed()` を strategy 実行基盤に使えるか

- Issue: **#76**（gate spike のみ。実装 epic・ADR 起案・docs 反映は本 issue スコープ外）
- 設計正本: [Discussion #64](https://github.com/botterYosuke/backcast/discussions/64)（本文 + R7–R14）
- 関連 ADR: ADR-0001（orphan-free runtime, **proposed**・自己保護条項あり＝編集不可）、
  ADR-0005（supersede は spike PASS 後の新規 ADR で。本 spike では着手しない）
- 実行環境: Intel x86_64 / macOS 13.7.8 / Python 3.13.11（venv `python/.venv`, uv 構成）/
  Unity 6000.4.11f1（Mono leg, `-batchmode -nographics`）
- marimo: **0.20.4（local fork `/Users/sasac/marimo` を editable）**
- 既存 spike 規約に倣う: `python/spike/<name>/` の headless probe ＋ PASS マーカー、結果は本 doc。

---

## 0. grill で固めた設計ツリー（owner 確定・binding）

spike-0（#76 コメント, 2026-06-17）で owner が確定した木をそのまま継承。本セッションでの差分は **dep source のみ**。

| 論点 | 結論 |
|---|---|
| marimo の入れ方 | spike 専用 dep group に隔離（`[dependency-groups] spike`）。runtime/配布 deps には入れない（ADR-0001 orphan-free を ADR 受理まで汚さない）|
| **marimo の取得元** | **local fork を editable**（`[tool.uv.sources] marimo = {path="../../marimo"}`・pyproject 相対パスで portable）。⚠️ spike-0 は PyPI `marimo==0.20.4` を pin する決定だったが、本セッションで owner が移植元＝local fork に変更（spike 専用・容易に可逆・ADR 不要）。fork も == 0.20.4 でバージョン一致。注: `uv.lock` は editable path を絶対パスに解決して保存する（uv 仕様）＝lock 側の絶対パスは `uv sync` で再生成される spike-only の wart |
| AC1 baseline | `python/engine/kernel/runner.py:208` `KernelRunner.run()` の per-bar loop。nautilus streaming seam は #50 で削除済（現 HEAD でも `load_universe_bars` は `engine.kernel.duckdb_bars` 由来で nautilus 不在を確認）|
| AC1 bar ソース | synthetic in-memory（50k）を両 leg + AC2 で共有する単一生成器。**毎 bar `close` を変える**（marimo の change-elision を構造的に封じる）|
| AC3 の証明分担 | 本証明は CPython。Mono は S2 骨格を流用した最小・無条件 smoke 1 回だけ |
| 計測 | 定常 median を主指標、cold-compile 先頭 bar 別掲、tail(max per-bar) 併記。N×ゲートは実測を見て owner が確定 |
| 置き場所 | `python/spike/marimo_embed/` ＋ 本 result doc |

### F1 = GREEN（裁定の継承）
strict「`marimo._server` を sys.modules に入れない」は `kernel_golden/purity.py` の「nautilus を入れない」過剰近似
proxy。proxy が代理していた真の不変条件は 2 つ（①ADR-0001 orphan-free ②Mono crash-safety＝native teardown 不在）で、
両方 GREEN。marimo `_server.*` は pure-Python・never-run・teardown 面ゼロで nautilus_pyo3 の Mono-crash リスクを
**転移しない**。よって F1-GREEN は緩和でなく proxy の置換。詳細は #76 コメント正本。

---

## 1. 判定サマリ

| AC | 結果 | 一言 |
|---|---|---|
| **F1**（reachability）| ✅ GREEN | headless で `running_in_notebook()`→True、`App.embed()` が reactive 分岐、2 seed で再計算実証 |
| **AC1**（per-tick 性能）| ⚠️→✅ **再設計で GREEN（§8）** | 文書化 `AppKernelRunner.run` 経由は ≈4.4ms/bar（235s/50k）だが、その遅さは **marimo `Kernel.run` オーケストレーションの per-bar 重複**が原因。**host-owned 痩せ drain で ~0.04–0.18s/50k = native 速度を達成**（§8）。reactive モデル自体はゼロコスト |
| **AC2**（mo.state 反応性 / stale drain）| ✅ GREEN | `set_bar()`→`runner.run({cell})` で UI 操作なしに per-bar 再走を確立。**close を固定したまま K 回 drain して cell が K 回再走**＝change-elision を host 強制 drain が打ち破ることを実証。never-run も維持 |
| **AC3-CPython**（async 直列化）| ✅ GREEN | 単一 loop で bar/edit が submission 順に直列化（順序問題・データ競合なし）、50 concurrent drain が deadlock なく完走 |
| **AC3-Mono**（import graph load）| ✅ GREEN | loro(Rust 拡張) 含む marimo import graph が Mono+pythonnet で load+1 drain、crash/フレーム停止なし（maxStall 8ms）|

**AC1 が load-bearing。** 文書化された embed / `AppKernelRunner.run` drain API 経由の per-bar reactive recompute は、
cell body ではなく **marimo `Kernel.run()` の per-call 機構（hooks / context install-uninstall / op-broadcast /
dataflow セットアップ）が支配的コスト**で、Replay の数万 bar streaming には桁違いに重い。

---

## 2. F1 — reachability（`python/spike/marimo_embed/spike0_context_standup.py`）

`uv run --group spike python -m spike.marimo_embed.spike0_context_standup` → `SPIKE0 PASS`

```
running_in_notebook()         : True
reactive embed (seed->result) : {20: 41, 100: 201}      # 定数では最大 1 seed しか満たせない＝reactive 確定
orphan-free (import-time)     : {'active_children': 0, 'listening_sockets': []}
orphan-free (post-embed)      : {'active_children': 0, 'listening_sockets': []}
marimo._server.* resident     : 8 modules               # spike-0 と一致
```

- headless context は marimo 自身の `MockedKernel` recipe（tests/conftest.py）を `marimo._server.utils` import 抜きで再現
  （`context_standup.py:HeadlessKernel`）。spike が自分から `_server` を import しない。
- **8 module（記録・将来の marimo bump で再 check 用）**: `marimo._server`, `.ai`, `.ai.tools`, `.ai.tools.types`,
  `.app_defaults`, `.asgi`, `.models`, `.models.completion`。root cause は `marimo._ai._pydantic_ai_utils` の eager
  `from marimo._server.ai.tools.types import ToolDefinition`（`marimo._ast.app` から到達・回避不能）。pure-Python・never-run。
- orphan-free は import 時 **と** embed run 後の両方で assert（spike-0 residual (b)＝never-run を runtime-stable に格上げ）。

---

## 3. AC1 — per-tick 性能（`ac1_perf.py`, N=50,000）

`uv run --group spike python -m spike.marimo_embed.ac1_perf 50000`

```
HEADLINE  real KernelRunner.run(): 0.060s for 50,000 bars (1.20 us/bar)
MECHANISM (identical loop, swap only on_bar dispatch):
  direct on_bar: cold=2.2us     median=0.26us    mean=0.31us    tail=47.5us
  embed drain : cold=4474.6us   median=4370.99us mean=4697.51us tail=98295.3us
  embed drain completed 50,000 bars in 234.875s (steady+cold)
  SLOWDOWN vs PRODUCTION (embed wall / real KernelRunner wall): 3,907x  (234.9s vs 0.060s)
  isolated-dispatch tax (embed median / trivial direct median): 16,812x
```

- **HEADLINE** = 現 `KernelRunner.run()`（push_bar→reference_prices→denials→fills の実 loop）を synthetic 50k bar で完走。
  `load_universe_bars` を synthetic list 注入に差し替え I/O ゼロ。＝「現 kernel 直 `on_bar` 経路」。
- **MECHANISM** = surrounding loop を同形に保ち、swap するのは on_bar dispatch だけ。両 leg とも `await dispatch(bar)`
  でラップ（async 機構コストを定数化）し、差分＝reactive drain の純オーバーヘッド。strategy 計算は両 leg 同一
  （`2*close+1`）で trivial＝機構オーバーヘッドを isolate。
- **2 つの倍率を混同しないこと**: `isolated-dispatch tax`（embed median ÷ 孤立 trivial direct median ≈16,800x）は
  *分母が ~0.3µs* の **marimo 機構の純税**で、worst-case に大きく出る診断値。owner が体感する遅さは
  `vs PRODUCTION`（embed wall ÷ 実 `KernelRunner` wall = 235s ÷ 0.060s ≈ **3,900x**）の方。両者とも判定は同じ向き。
- **完走はする**（50k 完走・AC1 の完走ゲートは満たす）が、定常 median **4.4ms/bar**。Replay が数万〜数十万 bar を
  流す前提では非現実的。**N×ゲートの owner 確定が必要**だが、production 比 ~3,900x（4.4ms/bar）はどの妥当な許容倍率も
  大きく超える。
- コスト所在: cell body ではなく `AppKernelRunner.run`→`Kernel.run` の per-call 機構。tail 62ms は GC/初期化スパイク。

---

## 4. AC2 — mo.state 反応性 / per-bar stale drain（`ac2_reactivity.py`）

`uv run --group spike python -m spike.marimo_embed.ac2_reactivity` → `AC2 PASS`

```
closes fed      : [1000.0, 1010.0, 1005.5, 1000.0]
tracked results : [2001.0, 2021.0, 2012.0, 2001.0]
forced re-runs  : 4/4 drains at CONSTANT close=1000.0
```

- **per-bar drain 手順（確立）**: host が `set_bar(close)` mo.state setter を呼び、`await runner.run({strategy_cell})` で
  下流 cell を明示 drain。`AppKernelRunner.run` は lazy 強制で渡した cell だけ実行＝host 主導の drain（UI element 不在）。
- **change-elision の打破を分離して実証**: 「常に変わる co-input（tick）を足す」と *どんな* reactive 系でも再走するため
  証明にならない。代わりに **close を直前値に固定（reactive 入力ゼロ変化）** したまま K=4 回 drain し、cell が exec_log に
  K 回追記＝**host 強制 drain が staleness/elision を無視して必ず再実行する**ことを示した（入力変化に依存しない再走）。
- **never-run 維持**: drain loop 後も orphan-free（active_children=0・LISTEN socket 無し）、新規 `marimo._server.*` ゼロ。

---

## 5. AC3 — async × GIL marshaling

### 5.1 CPython leg（`ac3_async.py`）✅ GREEN

`uv run --group spike python -m spike.marimo_embed.ac3_async` → `AC3-CPYTHON PASS`

```
serialized events : [('bar',100),('bar',110),('edit',3.0),('bar',100),('edit',0.5),('bar',200)]
observed          : [('bar',201,2.0,100),('bar',221,2.0,110),('edit',331),('bar',301,3.0,100),('edit',51),('bar',101,0.5,200)]
concurrent drains : 50/50 completed in 0.255s (watchdog 30.0s)
```

- **直列化（順序問題に収束）**: 単一 asyncio loop / 単一 consumer が bar/edit 混在 queue を submission 順に適用。
  同一 close 100 が coeff 変更後は 201→301 と別結果＝torn state なし、適用順序が well-defined。
- **live 編集 同居**: `edit(coeff)` は以降の bar に反映（331=110×3+1, 51=100×0.5+1）。per-tick drain と autorun 相当が
  同一 loop で共存。
- **no-deadlock / prompt completion**: 50 drain を `asyncio.gather` で同時投入しても全完走・0.255s（watchdog 30s 比で
  十分小）＝GIL/loop starvation なし（S2 の GREEN 基準＝no-hang ではなく prompt completion に整合）。
- **証明スコープの限界（caveat）**: serialized leg は単一 consumer の逐次適用＝順序保証は構成上自明で、drain×live-edit の
  *真の* 競合は再現していない（marimo が単一 loop に直列化する設計前提の確認に留まる）。concurrent leg は完走数+時間を検証し
  結果値までは assert しない。また watchdog は被験ループと同一 loop 上にあるため、真の hard-block では clean RED でなく hang に
  なる（= GREEN は「速く完走」を意味し「deadlock 検出機構がある」ではない）。いずれも単一 loop test の本質的限界で、verdict は不変。

### 5.2 Mono leg（`mono_smoke.py` ＋ `Assets/Editor/MarimoEmbedAc3MonoProbe.cs`）⏳

genuine な新規リスク（spike-0 residual (a)）: **loro（Rust 拡張）/ msgspec / pydantic-core / starlette を含む marimo
全 import graph が Unity Mono + pythonnet で load し、1 embed drain が crash/フレーム停止なく走るか**（S2 が踏んでいない唯一の点）。

```
<Unity6000.4.11f1> -batchmode -nographics -projectPath /Users/sasac/backcast \
    -executeMethod MarimoEmbedAc3MonoProbe.Run -logFile /tmp/marimo_ac3_mono.log -quit
```

- main: Initialize→BeginAllowThreads（GIL-free 維持）→ GIL-free heartbeat（≤200ms stall）。
  W1: `Py.GIL()`→sys.path 挿入（marimo は editable の .pth が Mono PYTHONPATH で未処理のため fork パスを直接挿入）
  → `spike.marimo_embed.mono_smoke.run_one_drain(123.5)` を 1 回 → 期待 248.0。
- **結果 ✅ GREEN**（Unity 6000.4.11f1, exit 0）:

```
[MARIMO-EMBED AC3 MONO PASS] marimo import+drain under Mono OK: result=248 (expected 248),
  import=0.00s, drain=0.821s, main maxStall=8ms (<= 200ms)
```

  - marimo の重い import（**loro = Rust 拡張**, msgspec, pydantic-core 含む）は `run_one_drain` 内で遅延 import される
    ため、`drain=0.821s` にグラフ load+headless standup+1 drain が全て含まれる。**crash / segfault / フレーム停止なし**。
  - main の GIL-free heartbeat は最大 stall 8ms（≤200ms）＝worker が import+drain する間も main は滑らかに継続。
  - ＝ spike-0 residual (a)「marimo 全 import graph が Mono+pythonnet で clean に load」を **GREEN で解消**。
    native teardown 面（nautilus_pyo3 が S0/S2 で踏んだ Mono-crash）は marimo には無いことを実証。

---

## 6. AC4 — 決定ゲート（owner 判断材料）

- **F1 / AC2 / AC3-CPython / AC3-Mono は GREEN**: reactive 埋め込み・per-bar stale drain・単一 loop 直列化・
  Mono import graph load はいずれも到達可能で安全。構想の「reactive cell-DAG として動く」部分は技術的に成立する。
  唯一 S2 が踏んでいなかった「marimo 全 import graph（loro Rust 拡張含む）の Mono load」も clean。
- **AC1 が決定的（唯一の RED）**: 文書化された embed drain API 経由の per-bar reactive recompute は
  **≈4.4ms/bar**（50k=235s vs 現 `KernelRunner` 0.060s ＝ **production 比 ≈3,900x**）。Replay が数万 bar を流す前提では
  現実装のまま採用不可。owner 判断は実質次の二択:
  1. **却下 / 再設計**: 「アプリ全体で 1 つの再生ボタンを cell-DAG に置換」は維持しつつ、per-bar drain に
     `AppKernelRunner.run`（=`Kernel.run` フルパス）を使わない**痩せた drain 経路**を別途設計できるかが争点。
     現 API のままでは Replay 不適。Live（低頻度 tick・1 本/秒〜分）なら 4.4ms/bar は許容余地あり＝**Replay と Live で
     実行基盤を分ける**設計も選択肢。
  2. **続行**: Live 限定 or 痩せ drain 前提で新規 ADR 起案（＋ADR-0005 supersede）へ。Mono は GREEN なので
     技術的ブロッカーは AC1 性能のみ。
- **AC1 の N×ゲートは未確定**: owner が定常 median 4.4ms/bar を見て許容倍率を確定する（本 doc がその材料）。

> ⚠️ **§8（spike-2 再設計）が本節の結論を覆した。** AC1 の遅さは reactive モデルの本質ではなく
> marimo `Kernel.run` オーケストレーションの per-bar 重複実行が原因で、**痩せ drain で native 速度を達成**。
> 「却下 / Replay-Live 分離」はいずれも不要になり、AC1 は **GREEN（解決可能）** に転じた。最新の正本は §8。

---

## 8. AC1 再設計（spike-2）— host-owned 痩せ drain で **native 速度**（`ac1_thin_drain.py`）

`uv run --group spike python -m spike.marimo_embed.ac1_thin_drain` → `AC1-THIN PASS`

AC1 の 4.4ms/bar をプロファイルした結果、**93% が `importlib.metadata.entry_points()` のディスク走査**
（`cell_runner.Runner.__init__`→`get_executor()`→`registry.get_all()` が cell 実行ごとに全 dist-info を
未キャッシュで読み直す）。残りも graph mutation / lint(`check_for_multiple_definitions`) / topological_sort /
execution-context install を**静的グラフなのに毎 drain やり直す**分。**いずれも static cell-DAG なら per-bar に不要**。

「executor ＋ dirty cell を一度だけ計算 → per-bar は `executor.execute_cell(cell, glbls, graph)`
（＝`exec(cell.body, glbls); eval(cell.last_expr, glbls)`）を直接呼ぶ」host-owned 痩せ drain の実測:

| 経路 | per-bar median | 50k 換算 |
|---|---|---|
| 現 `AppKernelRunner.run` | 4537µs | 235s |
| entry_points キャッシュのみ | 161µs | 9.1s |
| **host-owned 痩せ drain** | **~0.6–3µs** | **~0.04–0.18s**（現 `KernelRunner` 0.060s と同等＝native）|

→ **reactive モデル自体は本質的にゼロコスト**。Replay を native 速度で reactive 実行でき、§6 AC4 の
「Replay と Live を分ける」想定は不要。

### hot-path 契約の境界（owner 実測・probe で regression 固定）
痩せ drain は per-cell の execution-context を張らないため、per-bar cell が使えるものに非対称な境界がある:

| per-bar cell が触るもの | 痩せ drain hot path |
|---|---|
| bar/state 読み + compute + injected global(`submit_market`/`portfolio`) | ✅ OK（native）|
| `mo.state` **read/write**（`set_xxx()`）| ✅ OK（thread-level context 在中＝**reactivity の本体は保たれる**）|
| `mo.output` / `mo.md` | ⚠️ **silent no-op**（crash せず描画もしない＝footgun）|
| `mo.ui.slider` 等 UI element | ❌ **hard error**（`MarimoRuntimeException`／`BaseException` 派生＝fail-closed）|

### 決定（grill / owner 確定）
- **per-bar cell = pure compute のみ**（bar/state 読み・injected global・`mo.state` read/write）。`mo` UI/output/virtual-file は
  **cold path（初期起動・live-edit）専用**。#64 構想の per-bar は元々 pure compute なので適合。
- 契約の自己強制が**非対称**（`mo.ui`=hard error で気づける／`mo.output`=silent で気づけない）。
  → **hot path で output/UI を fail-closed に倒す guard（または最低限の明文化）を契約に含める**。
- この「per-bar cell = pure compute」契約は strategy 著述モデルの核なので **ADR 候補**（#76 方針どおり起案は実装 epic で）。

### 実装 epic へ持ち越す未解決分岐（本 spike では設計のみ）
1. 痩せ drain の host API 化と marimo private 依存（`executor.execute_cell` / `CellImpl.body` / `last_expr`）の安定性。
2. **dirty-set + topo の正しい precompute**: prototype は 1 cell 直打ち。実装は marimo の dataflow で「bar-state 変化時に
   dirty な cell 集合」を一度計算し topo 順に exec する（静的グラフ＝固定）。
3. **cold↔hot path 遷移**: live-edit でグラフが変わったら full `Kernel.run` で再コンパイル → dirty-set/topo 再計算 →
   hot path 再開、の境界 trigger。
4. fail-closed guard（`mo.output`/`mo.ui` の hot-path footgun 封じ）。
5. entry_points 未キャッシュは marimo 本体の perf bug（cold path のみ影響＝低頻度なら放置可。upstream 候補）。

---

## 7. 成果物

- `python/spike/marimo_embed/__init__.py`
- `python/spike/marimo_embed/context_standup.py`（headless KernelRuntimeContext + orphan-free assert）
- `python/spike/marimo_embed/synthetic.py`（共有 bar 生成器 + 最小 strategy/sink）
- `python/spike/marimo_embed/spike0_context_standup.py`（F1）
- `python/spike/marimo_embed/ac1_perf.py`（AC1 当初計測）
- `python/spike/marimo_embed/ac1_thin_drain.py`（AC1 再設計 spike-2: 痩せ drain native 速度 + hot-path 契約境界）
- `python/spike/marimo_embed/ac2_reactivity.py`（AC2）
- `python/spike/marimo_embed/ac3_async.py`（AC3 CPython）
- `python/spike/marimo_embed/mono_smoke.py` ＋ `Assets/Editor/MarimoEmbedAc3MonoProbe.cs`（AC3 Mono）
- `python/pyproject.toml` `[dependency-groups] spike` ＋ `[tool.uv.sources] marimo`（editable fork）
