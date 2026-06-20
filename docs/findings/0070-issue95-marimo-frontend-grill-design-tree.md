# findings 0070 — #95 grill 設計の木: Strategy Editor marimo frontend 化（土台 reactive / 本体 bt 駆動 / GIL）

`/grill-with-docs`（2026-06-20・owner セッション）で #95 の計画を既存ドメインモデルと照合し、甘いところを
潰した記録。issue 本文（同日 grill で全面再設計済み・B1 ボツ #96 / B2 #97・B3 #98 採用 / dry-run 廃止で
notebook=backtest 一本化）を**コードで裏取り**したところ、計画が黙っていた／前提が崩れていた点が複数出た。
本 findings がその下位決定の正本。**ADR-0012 は参照のみ（自己保護条項・書き戻さない）**。D12 の新 ADR
（"アプリ全体で 1 つの再生ボタン" ファセットの facet-scoped supersede）は **owner HITL** で別途起こす。

方針: ADR-0012（target = marimo cell-DAG）/ ADR-0013（1 notebook = 1 .py）/ findings 0046（thin-drain）/
0050（cell = floating window）/ ADR-0001（pythonnet 全埋め込み）/ ADR-0006（#24 golden immutable）。

## 裏取りで崩れた／補強した前提

### F1 — 「土台」は `HeadlessKernel` の reuse では届かない（計画の最重要の甘さ）
D3 は「`thin_drain.HeadlessKernel` が既に headless marimo Kernel を立てている＝marimo native reactive で
押した cell＋下流を実行」と書いていたが、実際は:
- `HeadlessKernel`（`thin_drain.py:152`）は **Kernel を組むだけの配線**。reactive single-cell 実行はしない。
- thin_drain は **frozen cell list の whole-notebook drain 専用**で、marimo の stale-set を意図的に消費しない
  （`thin_drain.py:449`「does not consume marimo's stale-set, which is what keeps it orchestration-free」）。
- per-cell 出力の捕捉 seam が無い（`execute_cell` 戻り値は捨てられ、return しない値は失われる）。

**ただし good news**: marimo の reactive runner 本体 `marimo._runtime.runner.cell_runner.Runner` は
**既に in-proc で import 済み**（`thin_drain.py:68`）。今は cold compile の cell 一覧計算
（`compute_cells_to_run`, `thin_drain.py:431`）にしか使っていないだけ。
→ **Phase 2 の正体**: 「HeadlessKernel reuse」ではなく **既 import の marimo `Runner` を、押した cell を
root に in-proc で回し、出力を捕捉して窓に出す net-new 配線**。HTTP サーバーも自前 DAG エンジンも書かない。
Phase 2 の AC は「thin_drain reuse」から書き直す。**marimo private API を新たに触るので、`Runner` の
interactive 経路＋出力捕捉が使う private シンボルを `test_strategy_runtime_thin_drain.py` の drift gate
射程に足す**（ADR-0012 §3）。

### F2 — サーバーは立たない（owner 確認）
marimo は **Unity プロセス内・pythonnet 全埋め込みで in-process 実行**（ADR-0001）。thin_drain は
`marimo._server` を**意図的に import しない**（`thin_drain.py:98-99` 明示コメント）。production に
uvicorn/asgi/start_server/webbrowser は無い（grep ヒットは uv.lock の transitive と spike のみ）。

### F3 — reactive（冪等）と `bt` 駆動（破壊的・状態前進）の衝突 → **「下流も走る・特別扱いなし」確定**
marimo の reactive は「cell をいつ再実行しても同じ」が前提。`bt.step()`/`bt.replay()` は RUN のたびに
エンジン状態を前進させる破壊的操作。具体 footgun: 上流の純計算 cell（例 `threshold=1.05`）で RUN を押すと、
それを参照する下流の `bt.step()` cell が reactive 再実行され**意図せず 1 bar 進む／backtest 再始動**する。

**owner 決定（binding）**: **下流も普通に走ってよい。プログラム側に「bt cell を伝播から除外」等の余計な
仕組みを作らない。仕組みはシンプルに保ち、ユーザーが注意する。** → 「bt cell を carve-out する機構」は
実装しない（むしろ実装が軽くなる）。marimo 本来の reactive 挙動と一致。

### F4 — golden（#24）確定タイミング → **(A) 終端まで step したときだけ byte-identical**
`bt.step()` は途中でやめると最後まで行かない。一方 #24 golden は「最後まで走った equity_curve から
byte-identical」が AC。**owner 決定 (A)**: **summary（sharpe / max_drawdown 等）は `bt.step()` が `None`
（終端）を返したときだけ確定・出力する。途中放棄は「未完」でサマリ無し。** replay(B2) は最後まで回るので
常に確定。不変条件: **「step を終端まで押し切ったら replay と同じ最終 equity_curve = byte-identical」**。
Phase 5 AFK probe で「step 終端→summary が replay と一致」を pin。

付随する設計制約（質問不要・固定）:
- **B2 generator の fill 順序**: `for bar in bt.replay():` は bar N を yield → ユーザー本体（=on_bar）→
  次の `next()` で bar N の fill@close→push_order→push_portfolio→equity 点→bar N+1 を yield、で回す。
  **最終 bar の fill は generator 終了処理（StopIteration 経路）で実行**。golden 順序
  `on_bar(N)→fill@close(N)→push_portfolio(N)`（`runner.py:230-304` / findings 0008 §2）を保つ。
- **Phase 3 は「wrap」より重い**: golden を作る `equity_curve`/`fills`/`last_prices` はループ内**ローカル**
  （`runner.py`）。step 化はこれらを instance へ hoist する＝golden 生成コードを触る。D10「再実装せず
  wrap」は「ローカルを instance へ上げる factor」を含むと正しく読む。push_run_complete（post-loop 1 回）の
  発火は F4 (A) のとおり終端でのみ。

### F5 — スレッド構成 → **1 エンジン・1 worker スレッド・RUN は順番待ち**
marimo Kernel/RuntimeContext は**スレッドローカル**（`thin_drain.py:152`「same thread that will later
compile and drain」）。「計算 cell は Unity main・bt cell は worker」にすると 1 Kernel を 2 スレッドから
触り壊れる。**owner 決定**: **計算 cell も bt cell も同じ 1 個の marimo エンジンを通す。エンジンは Unity
本体とは別の worker スレッドに 1 個だけ置く。replay 走行中に別 cell の RUN を押しても順番待ち**（エンジン
=1=1 スレッド）。D8 の「worker thread で replay・Unity main を塞がない」もこれで一括達成。

### F6 — 速度（D9）と走行中 Hakoniwa 更新（D8）の衝突 → **GIL 床は入れない・auto-switch 依存**（spike PASS）
現状 GIL の唯一の解放点は per-bar の `time.sleep(_bar_interval)`（`runner.py:302`）。速度を上げて
interval→0 にすると `if > 0` で sleep が消え GIL 解放点も消え、poll lane が飢えて Hakoniwa が走行中に
更新されず最後に一気に飛ぶ。

当初案「毎 bar 必ず小さく sleep する GIL 床（floor）」は **owner が却下** — それは「Hakoniwa の都合で
エンジン速度を縛る」最悪パターンそのもの（100 万 bar × 0.5ms = 500s の純損）。**owner の binding 優先順位:
最悪なのは Hakoniwa の更新処理が marimo/engine の実行速度を拘束すること。Hakoniwa が見た目上追いつかなく
ても構わない。**

**確定構成**:
- **エンジン worker は全力で回す。明示 sleep を入れない**（速度指定が無いとき）。
- Hakoniwa 更新は別スレッド（エンジン=worker / poll lane=別 / 描画=Unity main）。読み手は
  `engine.last_portfolio` の atomic-swap スナップショット（`replay_kernel_observer.py:144`）を取れるときに
  読むだけ。**書き手からの back-pressure ゼロ**。
- GIL 受け渡しは **CPython 自動切替（`sys.setswitchinterval` 既定 ~5ms）**に任せる。エンジンは sleep で
  時間を捨てないので速度は拘束されない。Hakoniwa は間に合わなければ見た目が遅れるだけ（owner 許容）。
- **D9 の速度を純化**: 速度（`bt.replay(speed=N)`）は「目で追いたいときだけ意図的にエンジンを遅くする
  視覚機能」。指定が無ければ sleep ゼロで全力。**速度は live-mutable レジスタにしない**（走行中に変える
  手段＝ボタン無し＋RUN 順番待ち、なので不要）— replay() 開始時にキャプチャする値。**stop だけ cross-thread
  レジスタ**（`_replay_stop_event`・force-stop 割り込みに必要）。

#### spike 実証（`python/spike/gil_handoff_spike.py`・2026-06-20・PASS）
worker が GIL を握り tight loop（sleep 無し）で回す間、別スレッドの poll が定期的に GIL を取れるか実測:

| 構成 | engine bars/s | poll 更新数/2s | 時間遅れ | poll cadence |
|---|---|---|---|---|
| CURRENT（sleep 10ms/bar） | ~94 | 40 | 0 bar | 50ms |
| **PROPOSED（sleep 無し・switch 5ms）** | **~2.37M** | **31** | **~18ms** | 66ms |
| PROPOSED（sleep 無し・switch 1ms） | ~2.40M | 34 | ~21ms | 63ms |

→ **sleep 撤去でエンジンは ~25,000× 高速（back-pressure ゼロ）。reader は飢えず（31 live read・終端の
~18ms 後ろ）追従**。reader の cadence は 50→66ms に ~30% 伸びる（GIL 再取得レイテンシ）が、これは owner
許容の「視覚的に少し遅れる」。switch 1ms ノブは誤差程度の改善＝既定 5ms で十分。

**残存リスク（finding に明記）**:
1. **faithfulness**: spike の reader は Python `threading.Thread`。実物は C# poll lane の `Py.GIL()`
   （`PyGILState_Ensure`・foreign thread）。CPython eval-loop の `gil_drop_request` は native/foreign を
   問わず同じ drop 経路でハンドオフするので強い proxy だが、**完全忠実版は Unity AFK pythonnet probe**。
   Phase 4 で auto-switch を実機 reconfirm してから「明示 sleep 撤去」を最終 lock（RED なら
   `sys.setswitchinterval` を少し下げる等へ pivot）。
2. **per-bar が長い C 拡張（nautilus/marimo C 区間）で GIL を握り続ける**と auto-switch が効かず、その区間
   だけ Hakoniwa が凍る。ただし**凍るのは reader であってエンジンではない**ので owner 優先順位は不変。

## 次アクション（Phase 1 = ADR + 設計 record）
- D12 の新 ADR を **owner HITL** で起こす（"1 つの再生ボタン" facet-scoped supersede・dry-run 廃止＝
  notebook=backtest 一本化・B2/B3 併存・config=panel・結果=Hakoniwa・速度=視覚機能・速度復活理由）。
- CONTEXT.md に glossary（`bt` ハンドル / replay / step / 土台 / 本体 / 速度=視覚機能 / 1 エンジン 1 worker /
  notebook=backtest）。
- 本 findings を「方針: ADR-0012 / 新 ADR」として参照。spike は `python/spike/gil_handoff_spike.py` に保存。
