# findings 0070 — notebook = backtest 一本化（#95 Phase 1 grill 設計の木）

方針: [ADR-0016](../adr/0016-notebook-equals-backtest-per-cell-run.md)（per-cell RUN を strategy 実行エントリーとし notebook = backtest に一本化・ADR-0012 の「単一再生ボタン」facet を supersede）。本 findings は **#95 Phase 1 で起案**した同 ADR の grill 設計の木と、立て付けの根拠（B1 却下理由・速度復活理由・命令型 .py UI sunset の踏み込み判断・`bt` lifecycle の owner HITL 訂正・コードでの裏取り F1–F6・GIL spike）を**会話で消えないように固定する**。

backcast に `FLOWS.md` は無いため、本 findings ＋ ADR-0016 ＋ #95 Phase 2–6 各 slice の `docs/findings/` が設計＋検証の正本となる。ADR-0012 / ADR-0013 / ADR-0006 / ADR-0007 は immutable（書き戻さない）。

> **統合履歴（2026-06-20）**: 本 findings は当初 2 ファイル（`0070-notebook-equals-backtest-grill.md`＝立て付け／HITL と `0070-issue95-marimo-frontend-grill-design-tree.md`＝コード裏取り F1–F6／GIL spike）に分かれ **番号 0070 が重複**していた。同一 Phase 1 grill 設計の木の前半・後半だったため**本ファイルへ統合**し、後者は削除した。issue #95 コメントが旧 `0070-issue95-…` を「設計の木の正本」と名指していたが、**正本は本ファイル（および ADR-0016）**。

---

## Grill source（2026-06-20 owner セッション）

「**marimo notebook で kernel/engine を制御する**」本体の設計分岐の grill。`KernelRunner.run` の per-bar ループが host 所有（`engine/kernel/runner.py:230`）で、ユーザー cell は `get_bar()` を読み `submit_market(qty)` を呼ぶ reactive 反応役にすぎない既存モデルに対し、owner が「**ユーザーが marimo に書くコードで backtest 経路へ明示接続できないか**」を問うた。

3 案を検討（互いに排他・1 案採用）:

| 案 | ユーザーが書く strategy cell | loop 所有 | marimo らしさ | 状態 |
|----|--------------------------|----------|-------------|------|
| **B1**（#96） | reactive cell を**複数**（`bt.bar()` を読む） | ホスト | ✅ per-cell output / DAG が残る | **却下** |
| **B2**（#97） | `for bar in bt.replay(): ...` ループ **1 cell** | ユーザー | ❌ 1 スクリプトに縮む | **採用** |
| **B3**（#98） | `bar = bt.step()` cell（押すたびに 1 bar） | ユーザー | △ 状態持ち・marimo idempotent 前提と衝突 | **採用** |

**結論**: B1 却下・**B2 ＋ B3 併存**（同一 `bt` ハンドルで replay/step 両方を提供）。共通前提として `config` は **`ScenarioStartupTile`** が所有・`結果` は **Hakoniwa** が所有・cell に書くのは strategy だけ、を全 3 案で共有。

### B1 却下の理由（決定的）

owner が grill 中盤に B1 を「再 reactive 系」として温存しようとしたが、以下で却下:

- **「明示接続」の telos に届かない**: B1 は接続を panel で明示するが、per-bar 駆動は依然 host 所有。ユーザーから見て「loop はどこかで誰かが回している」隠れ機構が残る。owner が問うた「**ユーザーが notebook に書くコードで backtest 経路へ明示接続**」の telos は満たさない（ユーザーが `bt.replay()` / `bt.step()` を**書く**から「明示接続」になる）。
- **「reactive」と「明示接続」が概念衝突**: B1 は cell が reactive に再計算される（host の per-bar push がトリガー）。「ユーザーが engine を駆動」と「engine が cell を駆動」が同居し、どちらが authoritative かが曖昧。ユーザー側の mental model が割れる。
- **マーケットでの代替**: marimo らしさを保ちたい局面は「**土台層**＝engine 非接続の純粋計算 per-cell」が担う。B2/B3 cell は backtest 駆動という別意味論を持たせるので、reactive と命令的を意味の階層で分離する方が clean。
- **B2/B3 を 1 つにできない理由**: B2 は「視覚 playback / 早期 break / bar 跨ぎ任意状態」、B3 は「bar-by-bar デバッグ・per-cell RUN と自然に噛み合う」。両者は user task が違う（書きながら見たい vs 一発で全期間流したい）ので、API を 2 つ持つことに合理性がある。**同じ `bt` の 2 つの呼び方**として併存させると state 複雑化を避けられる（D10）。

#96 は本 findings landing と同時に close。

### 速度（撤去機能）を意図的に「復活」させる理由

`#30/#68` で footer transport（`▶/⏸/⏭/⏹ ＋ [1,2,5,10,50]`）は `core.py:42` の在席コメントが残る形で削除された（`backend_service.py:387` / `WorkspaceFooterView.cs:5` / findings 0046）。退役理由は **「reactive drain は一瞬で終わる」**（cold compile + 1-shot drain が想定された時点での真）。

**B2 が前提を変えた**: `bt.replay()` は「目で追う visual playback」を想定し、ユーザーが**走っている様子を見ながら判断する**。固定値 pacing は遅すぎる（50k bar = 8.3 分）か速すぎる（描画追従 fail）かのどちらかになる。**pacing を user-controllable にしないと B2 のユーザー体験が崩れる**。

**復活の形**: footer transport の UI ボタン群は復活させない（#30/#68 の UI 退役判断は維持）。**「コードで `bt.replay(bars_per_second=N)` と指定」だけが復活させた seam**。速度は **`replay()` 開始時にキャプチャする call-time 値**で、**live-mutable レジスタにはしない**（走行中に変える手段＝UI ボタンが無く・RUN は順番待ちなので不要）。`bars_per_second` 指定が無ければ **sleep ゼロで全力**（GIL ハンドオフは CPython auto-switch に任せる＝下記 F6）。**cross-thread レジスタは stop（`_replay_stop_event`）だけ**。本 ADR の scope では cell-facing pacing API は **call-time の `bars_per_second=N` 引数のみ**（単位は名前に込める＝「1 秒あたり N bar・大きいほど速い」・per-bar sleep ≈ `1 / N` 秒。bare な `speed=` 倍率は public に出さない）。

silent な復活でないこと（findings に理由を残すこと）を本 finding が保証する。

---

## 裏取りで崩れた／補強した前提（F1–F6・2026-06-20 owner セッション）

issue #95 本文（同日 grill で全面再設計済み・B1 ボツ #96 / B2 #97・B3 #98 採用 / dry-run 廃止で notebook=backtest 一本化）を**コードで裏取り**したところ、計画が黙っていた／前提が崩れていた点が複数出た。本節がその下位決定の正本（ADR-0016 D2 / D8 / D9 はこの F1 / F6 を反映する）。

### F1 — 「土台」は `HeadlessKernel` の reuse では届かない（計画の最重要の甘さ）
issue D3 は「`thin_drain.HeadlessKernel` が既に headless marimo Kernel を立てている＝marimo native reactive で押した cell＋下流を実行」と書いていたが、実際は:
- `HeadlessKernel`（`thin_drain.py:152`）は **Kernel を組むだけの配線**。reactive single-cell 実行はしない。
- thin_drain は **frozen cell list の whole-notebook drain 専用**で、marimo の stale-set を意図的に消費しない（`thin_drain.py:449`「does not consume marimo's stale-set, which is what keeps it orchestration-free」）。
- per-cell 出力の捕捉 seam が無い（`execute_cell` 戻り値は捨てられ、return しない値は失われる）。

**ただし good news**: marimo の reactive runner 本体 `marimo._runtime.runner.cell_runner.Runner` は **既に in-proc で import 済み**（`thin_drain.py:68`）。今は cold compile の cell 一覧計算（`compute_cells_to_run`, `thin_drain.py:431`）にしか使っていないだけ。
→ **Phase 2 の正体**: 「HeadlessKernel reuse」ではなく **既 import の marimo `Runner` を、押した cell を root に in-proc で回し、出力を捕捉して窓に出す net-new 配線**。HTTP サーバーも自前 DAG エンジンも書かない。Phase 2 の AC は「thin_drain reuse」から書き直す。**marimo private API を新たに触るので、`Runner` の interactive 経路＋出力捕捉が使う private シンボルを `test_strategy_runtime_thin_drain.py` の drift gate 射程に足す**（ADR-0012 §3）。

### F2 — サーバーは立たない（owner 確認）
marimo は **Unity プロセス内・pythonnet 全埋め込みで in-process 実行**（ADR-0001）。thin_drain は `marimo._server` を**意図的に import しない**（`thin_drain.py:98-99` 明示コメント）。production に uvicorn/asgi/start_server/webbrowser は無い（grep ヒットは uv.lock の transitive と spike のみ）。

### F3 — reactive（冪等）と `bt` 駆動（破壊的・状態前進）の衝突 → **「下流も走る・特別扱いなし」確定**
marimo の reactive は「cell をいつ再実行しても同じ」が前提。`bt.step()`/`bt.replay()` は RUN のたびにエンジン状態を前進させる破壊的操作。具体 footgun: 上流の純計算 cell（例 `threshold=1.05`）で RUN を押すと、それを参照する下流の `bt.step()` cell が reactive 再実行され**意図せず 1 bar 進む／backtest 再始動**する。

**owner 決定（binding）**: **下流も普通に走ってよい。プログラム側に「bt cell を伝播から除外」等の余計な仕組みを作らない。仕組みはシンプルに保ち、ユーザーが注意する。** → 「bt cell を carve-out する機構」は実装しない（むしろ実装が軽くなる）。marimo 本来の reactive 挙動と一致。

### F4 — golden（#24）確定タイミング → **(A) 終端まで step したときだけ byte-identical**
`bt.step()` は途中でやめると最後まで行かない。一方 #24 golden は「最後まで走った equity_curve から byte-identical」が AC。**owner 決定 (A)**: **summary（sharpe / max_drawdown 等）は `bt.step()` が `None`（終端）を返したときだけ確定・出力する。途中放棄は「未完」でサマリ無し。** replay(B2) は最後まで回るので常に確定。不変条件: **「step を終端まで押し切ったら replay と同じ最終 equity_curve = byte-identical」**。Phase 5 AFK probe で「step 終端→summary が replay と一致」を pin。

付随する設計制約（質問不要・固定）:
- **B2 generator の fill 順序**: `for bar in bt.replay():` は bar N を yield → ユーザー本体（=on_bar）→ 次の `next()` で bar N の fill@close→push_order→push_portfolio→equity 点→bar N+1 を yield、で回す。**最終 bar の fill は generator 終了処理（StopIteration 経路）で実行**。golden 順序 `on_bar(N)→fill@close(N)→push_portfolio(N)`（`runner.py:230-304` / findings 0008 §2）を保つ。
- **Phase 3 は「wrap」より重い**: golden を作る `equity_curve`/`fills`/`last_prices` はループ内**ローカル**（`runner.py`）。step 化はこれらを instance へ hoist する＝golden 生成コードを触る。D10「再実装せず wrap」は「ローカルを instance へ上げる factor」を含むと正しく読む。push_run_complete（post-loop 1 回）の発火は F4 (A) のとおり終端でのみ。

### F5 — スレッド構成 → **1 エンジン・1 worker スレッド・RUN は順番待ち**
marimo Kernel/RuntimeContext は**スレッドローカル**（`thin_drain.py:152`「same thread that will later compile and drain」）。「計算 cell は Unity main・bt cell は worker」にすると 1 Kernel を 2 スレッドから触り壊れる。**owner 決定**: **計算 cell も bt cell も同じ 1 個の marimo エンジンを通す。エンジンは Unity 本体とは別の worker スレッドに 1 個だけ置く。replay 走行中に別 cell の RUN を押しても順番待ち**（エンジン=1=1 スレッド）。D9 の「worker thread で replay・Unity main を塞がない」もこれで一括達成。

### F6 — 速度（D8）と走行中 Hakoniwa 更新（D7）の衝突 → **GIL 床は入れない・auto-switch 依存**（spike PASS）
現状 GIL の唯一の解放点は per-bar の `time.sleep(_bar_interval)`（`runner.py:302`）。速度を上げて interval→0 にすると `if > 0` で sleep が消え GIL 解放点も消え、poll lane が飢えて Hakoniwa が走行中に更新されず最後に一気に飛ぶ。

当初案「毎 bar 必ず小さく sleep する GIL 床（floor）」は **owner が却下** — それは「Hakoniwa の都合でエンジン速度を縛る」最悪パターンそのもの（100 万 bar × 0.5ms = 500s の純損）。**owner の binding 優先順位: 最悪なのは Hakoniwa の更新処理が marimo/engine の実行速度を拘束すること。Hakoniwa が見た目上追いつかなくても構わない。**

**確定構成**:
- **エンジン worker は全力で回す。明示 sleep を入れない**（速度指定が無いとき）。
- Hakoniwa 更新は別スレッド（エンジン=worker / poll lane=別 / 描画=Unity main）。読み手は `engine.last_portfolio` の atomic-swap スナップショット（`replay_kernel_observer.py:144`）を取れるときに読むだけ。**書き手からの back-pressure ゼロ**。
- GIL 受け渡しは **CPython 自動切替（`sys.setswitchinterval` 既定 ~5ms）**に任せる。エンジンは sleep で時間を捨てないので速度は拘束されない。Hakoniwa は間に合わなければ見た目が遅れるだけ（owner 許容）。
- **D8 の速度を純化**: 速度（`bt.replay(bars_per_second=N)`）は「目で追いたいときだけ意図的にエンジンを遅くする視覚機能」。指定が無ければ sleep ゼロで全力。**速度は live-mutable レジスタにしない**（走行中に変える手段＝ボタン無し＋RUN 順番待ち、なので不要）— `replay()` 開始時にキャプチャする値。**stop だけ cross-thread レジスタ**（`_replay_stop_event`・force-stop 割り込みに必要）。

#### spike 実証（`python/spike/gil_handoff_spike.py`・2026-06-20・PASS）
worker が GIL を握り tight loop（sleep 無し）で回す間、別スレッドの poll が定期的に GIL を取れるか実測:

| 構成 | engine bars/s | poll 更新数/2s | 時間遅れ | poll cadence |
|---|---|---|---|---|
| CURRENT（sleep 10ms/bar） | ~94 | 40 | 0 bar | 50ms |
| **PROPOSED（sleep 無し・switch 5ms）** | **~2.37M** | **31** | **~18ms** | 66ms |
| PROPOSED（sleep 無し・switch 1ms） | ~2.40M | 34 | ~21ms | 63ms |

→ **sleep 撤去でエンジンは ~25,000× 高速（back-pressure ゼロ）。reader は飢えず（31 live read・終端の ~18ms 後ろ）追従**。reader の cadence は 50→66ms に ~30% 伸びる（GIL 再取得レイテンシ）が、これは owner 許容の「視覚的に少し遅れる」。switch 1ms ノブは誤差程度の改善＝既定 5ms で十分。

**残存リスク（finding に明記）**:
1. **faithfulness**: spike の reader は Python `threading.Thread`。実物は C# poll lane の `Py.GIL()`（`PyGILState_Ensure`・foreign thread）。CPython eval-loop の `gil_drop_request` は native/foreign を問わず同じ drop 経路でハンドオフするので強い proxy だが、**完全忠実版は Unity AFK pythonnet probe**。Phase 4 で auto-switch を実機 reconfirm してから「明示 sleep 撤去」を最終 lock（RED なら `sys.setswitchinterval` を少し下げる等へ pivot）。
2. **per-bar が長い C 拡張（nautilus/marimo C 区間）で GIL を握り続ける**と auto-switch が効かず、その区間だけ Hakoniwa が凍る。ただし**凍るのは reader であってエンジンではない**ので owner 優先順位は不変。

---

## #95 Phase 1 owner HITL（2026-06-20 続行セッション）

issue #95 本文 D1–D12 は 2026-06-20 grill の凍結出力。Phase 1 が新 ADR を起案するに当たって、残った load-bearing な下位決定を 2 問だけ HITL でアンカーした:

### Q1 — 命令型 `.py` の UI 経路の扱い（採用＝formal sunset）

issue D11 が「global ▶ Run / title-bar Run / footer transport は supersede」と言うため、命令型 `.py` の UI 実行手段が消える。この ADR で formal sunset するかを問うた。

**owner 判断**: **採用＝formal sunset**。ただし wording で 2 点訂正:

1. 命令型 `.py` を **「UI から開けない」と書かない**。現実は #80 picker 退役後でも **File→Open は使え、`load_app` が None の場合は findings 0054 の 1-cell wrap で開ける**。`.py` を開く migration / editing affordance としては存続。
2. ADR の主張は「**UI 実行経路の sunset**」であって「`.py` Open の sunset」ではない。実行入口を per-cell RUN だけに一本化し、命令型 `Strategy` クラスを UI で batch 実行する fallback は残さない、が正確。

帰結（ADR-0016 D4 へ反映）:

- global ▶ Run / title-bar Run / footer transport は **#95 D11 により supersede**
- UI からの実行は **per-cell RUN のみ**
- 命令型 `.py` は **File→Open の 1-cell wrap** で編集・移行用に開ける
- imperative `Strategy` class を UI で batch 実行する fallback は残さない
- 命令型 runtime / `strategy_loader` / `KernelRunner` boundary は pytest / golden / programmatic 用に存続

**却下した選択肢**:

- (b) 命令型 fallback UI を残す（global ▶ Run を imperative-only に降格） → 「notebook = backtest 一本化」(D7) の telos に逆行。永続的に非対称 UX を抱える。
- (c) 命令型 `.py` を自動 1-cell marimo wrap して per-cell RUN で走らせる → `Strategy.on_bar` → marimo per-cell の自動変換 adapter が要り工数大。`_select_replay_strategy` の AST detect-first ordering と二重化。設計上も notebook=backtest 一本化を濁す。

### Q2 — `bt` ハンドルの lifecycle・状態共有・reset trigger（採用＝config 単位・単一 bar pointer 共有）

issue D4 が「startup パネル config から `bt` を構築・1 個」とは言っていたが、**いつ reset されるか**・**`bt.replay()` と `bt.step()` の状態共有**は明示されていなかった。

**owner 判断**: **採用＝config 単位・単一 bar pointer 共有**。

- `bt` は **startup-panel config 単位**で 1 個生成
- `bt.replay()` と `bt.step()` は **同じ `bt` / 同じ KernelRunner state machine / 同じ bar pointer** を共有
- startup-panel config を commit し直したら **`bt` は破棄して作り直す**（kernel teardown + 新 kernel）
- `bt.replay(bars_per_second=N)` は呼ばれるたび **pointer を 0 に reset** して end まで走る
- `bt.step()` は現在 pointer から **1 bar 進める**（終端で `None`）
- `bt.step()` 同一 cell の再実行は **意図的に stateful**: 各実行で 1 bar 進む
- 完走後 pointer = end。以後 `bt.step()` は `None`、次の `bt.replay()` はまた 0 から再走
- replay 実行中に同じ `bt` へ別 RUN が入る場合は **Phase 4/6 の running guard でブロック**

ユーザーモデルは 3 行で説明できる: ① **config commit が `bt` を新規作成** ② **replay は常に 0 から** ③ **step は現セッションの pointer を進める**。

**却下した選択肢**:

- (b) replay と step が独立 instance（独自 pointer） → 「いまの backtest 状態」が複数になり Hakoniwa の running snapshot / stop / reset の説明が急に増える。D10「同じ土台に乗せる」と相性が悪い。
- (c) ノートセッション単位永続（startup commit で 1 回作って以後 reset なし） → `bt.replay()` が「中断再開」意味論になり B2 の「全期間 visual playback」の直感を壊す。`reset` ボタンが必要になり旧 transport を呼び戻す。

帰結（ADR-0016 D3 へ反映）。

---

## 実装段階（#95 Phase 1–6）

本 findings は **Phase 1 のみ**を着地させる。Phase 2–6 は順序付き別 slice で、各 Phase 着地時に該当 findings を起こして本 finding を参照する。

| Phase | 内容 | gate（issue #95 AC より） |
|---|---|---|
| 1 | 本 ADR + 本 findings + CONTEXT glossary（**本セッション着地**） | owner HITL（実施済み）|
| 2 | 土台（全 cell 窓 RUN ボタン + 純粋計算 per-cell run） | pytest（reactive 下流再計算 / 上流非依存 cell は再計算されない / import-purity 不変）＋ AFK probe（adopted+spawned 両方にボタン在・押下で出力が窓に出る）|
| 3 | `bt` ハンドル + `KernelRunner` state-machine 化 | pytest（step が 1 bar 進む / replay が全 bar / 両者が runner.py と同一順序 = golden 同値）|
| 4 | B2 `bt.replay()`（#97 を吸収） | AFK probe（replay cell RUN → Hakoniwa 逐次更新・速度変更が効く・stop で止まる）|
| 5 | B3 `bt.step()`（#98 を吸収） | AFK probe（step cell RUN → 1 bar 進む）+ reset/idempotency pin |
| 6 | 実行状態 UI + block popup + rich output | per-cell idle/running/stale + `mo.md` / table / chart 出力 |

## 本 finding が**やっていない**こと（Phase 1 範囲外）

- `bt` 実装の具体クラス・module 配置（Phase 3）
- `KernelRunner` を「1 bar 進めて中断/再開」できる形へ切り出す具体 seam（Phase 3）
- 速度（`bars_per_second`）の正確な capture timing 実装 / per-bar pacing sleep の挿入点（Phase 4。速度は開始時キャプチャで同期プリミティブ不要・cross-thread の同期が要るのは stop / running guard だけ）
- running guard の構造（Phase 4 / 6）
- 全 cell 窓の RUN ボタン C# 配線詳細（Phase 2 / 6）

これらは本 ADR の方針下で各 Phase の findings に固定する（ADR は書き戻さない＝自己保護条項）。
