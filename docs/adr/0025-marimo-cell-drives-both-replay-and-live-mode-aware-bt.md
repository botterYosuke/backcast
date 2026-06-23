---
status: accepted
---

# marimo 戦略 cell を Replay と Auto(LiveAuto) の両方で駆動する（mode-aware `bt` façade・per-cell RUN 一本化）

`/grill-with-docs`（2026-06-23・#112・owner HITL Q1–Q6）で導出。設計正本: 本 ADR ＋
[findings 0092](../findings/0092-issue112-marimo-cell-live-mode-aware-bt.md)。ADR-0016 / ADR-0012 /
ADR-0021 / ADR-0006 / ADR-0007 は別 decision の固定 oracle として（本 ADR が明示拡張する範囲を除き）踏襲し編集しない。

> **実装状態**: 本 ADR は #112 で **accepted**（decision 確定）。実 cell-facing 配線（mode-aware `bt`・
> live rendezvous bridge・per-cell RUN の mode 分岐・granularity 配線・footer ▶ 退役）は #112 の named
> スライスで landing する。`status: accepted` は ADR-0016 / ADR-0012 と同じく「decision が確定・実装は段階的」。

## Context

backcast のコンセプトは「**1 つの marimo 戦略 cell が Replay と Auto(LiveAuto) をシームレスに行き来できる**」こと
（issue #112）。しかし現状、戦略実行モデルが 2 系統に分裂している:

| | Cell モデル（Replay） | Strategy-subclass モデル（Live） |
|---|---|---|
| 形 | marimo `@app.cell` + `for bar in bt.replay():` | `class X(Strategy): def on_bar(...)` |
| 駆動 | per-cell RUN（`bt` 注入・ADR-0016） | `register_live_strategy` → `controller.attach` → `on_bar` push |
| 対応モード | **Replay のみ** | **Live のみ** |

`v19_morning_cell.py`（cell・`bt.replay()`）は **Replay 専用**で、Auto に切替えて per-cell RUN すると **live 登録段階で必ず失敗**する（`strategy_loader.load` が marimo cell を `engine.kernel.strategy.Strategy` サブクラス 0 個で reject・`strategy_loader.py:116`）。ADR-0016 は「notebook = backtest」を一本化したが **LiveAuto へは拡張していない**。Replay を pull する cell ループ（`bt.replay()` は generator）と、venue が `on_bar` を push する live loop（`driver.py:289-302`・確定 bar のみ）の **pull / push 非対称**、および **per-cell RUN（Replay）と footer ▶（Live）が完全別経路**であることが壁。

裏取りで判明した事実（issue 本文の誤認訂正）:
- issue 本文「kernel-native `v19_morning.py` / `v19_morning.json` は意図的に削除済み・Auto で動かす手段ゼロ」は **現 main（c6d9006）では false**。両ファイル＋`test_v19_auto_live_afk.py` は HEAD に存在し、命令型 `V19MorningStrategy(Strategy)` が Auto で動く。→ これは **parity oracle**（案の合否＝「cell を Auto で回した発注/約定列が `v19_morning.py` を Auto で回したのと同じか」を機械判定）。
- nautilus DNA（本プロジェクトの engine の祖）: 「backtest / live で実行意味論が同一・戦略はモード分岐しない・engine が data/exec を供給」（`system/kernel.py` / `common/actor.pyx` の register 注入）。本 ADR の `bt` façade はこの portability を踏襲する。
- 既存 live seam は再利用可能: `controller.attach` は scenario の **universe 全体を subscribe**（`controller.py:146,216`）、`_buying_power` は venue 余力 provider 権威（`driver.py:152-162`）、`_drain` は再入安全（`driver.py:340-354`）、`apply_venue_async_event` が非同期 fill を live loop 上で portfolio に反映（`driver.py:228-273`）。

## Decision

### D1 — 同一 cell を両モードで駆動する（mode-aware `bt` façade・案A）

cell は**一文字も変えず**、`bt` が **(BarSource, ExecutionSeam) のペアを隠す façade** としてモード対応する:

- **Replay**: BarSource = historical iterator、ExecutionSeam = `KernelStepper`（bar-close 約定の sim・現状）。
- **Auto**: BarSource = venue 確定 bar を流す thread-safe キュー、ExecutionSeam = 既存 `KernelLiveDriver.ctx`（intent queue → SafetyRails → broker → venue・余力 provider・portfolio）。

`bt.replay()` の `next()` が venue 確定 bar を待ってブロックし、`bt.submit_market`/`bt.portfolio`/`bt.buying_power` は live ctx へ routing。`KernelLiveDriver` の**発注・rails・余力・portfolio・teardown 機構はまるごと再利用**し、差し替えるのは「`on_bar(bar)` を直接呼ぶ」→「bar をキューへ渡し worker の完了を待つ」の一点だけ。`controller.attach` の配線（broker/portfolio/buying_power_provider/emit_order/rails）は無傷。

**却下＝案B（reactive `get_bar()` 経路 = `MarimoStrategy`/thin_drain の再利用）**: その経路は出荷戦略で誰も使っていない**休眠コード**で、`_select_replay_strategy:338` から Replay footer 経由でしか繋がらず、その footer 自体 ADR-0016 で per-cell RUN に置換された退役予定枝。案B は (1) 休眠枝の蘇生、(2) cell を reactive 書法へ**書き直す**（v19 の `placed`/`pending_buys`/`snaps` という「ループ本体が毎 bar 同じ Python local を見る」前提が壊れ `mo.state` 復活＝#95/findings 0046 がわざわざ捨てた書法へ退行）、(3) AC#1「`bt.replay()` で書かれた cell をファイルを差し替えずに」・AC#2「同一 cell・同一エディタ状態」を字義どおり破る。→ 案B は「既存シームの再利用」に見えて休眠枝蘇生＋退行＋AC 違反。

### D2 — pull/push の橋渡しは「1 bar ずつのランデブー（lock-step）」（案A-1）

cell ループは **notebook-session worker thread**（per-cell RUN と同じ thread）で回す。marimo `RuntimeContext` が **OS スレッドローカル**（#102 の実機落ち真因）なので、cell 本体を venue の asyncio live-loop thread とは別スレッドで回さざるを得ない。両スレッドを **1 bar ずつのランデブー**で受け渡す:

- live consumer（live-loop）: 確定 bar → `await bridge.drive_bar(bar)`（①bar を thread-safe `queue.Queue` へ put、②cell が「この bar 終わった」と signal するまで `await`）→ 解けたら既存 `await self._drain()`（この bar で積まれた intent を venue へ）→ 次の venue event を待つ。
- cell ループ（worker）: `bt.replay()` の `next()` が `queue.get()` でブロックし bar を受領。本体実行 → 次の `next()` 呼び出しで「bar 完了」を signal。

この lock-step は **アルゴ実行系（nautilus / Lean / Backtrader / zipline）の業界標準「単一スレッド event-driven で 1 イベントずつ順番に処理（data → handler → 発注を積む → 次の event 前に処理しきる）」を、marimo に強制されたスレッド分割をまたいで再構築したもの**。`on_bar → drain` の順序を保ち、parity oracle を構造的に満たす。**却下＝案A-2（fire-and-forget キュー）**: consumer が bar を置いて即次へ進む形は、(1) bar i の submit が処理される前に bar i+1 が届き得て**発注順序が venue タイミング依存で非決定的**＝backtest/live parity 崩壊（nautilus が意図的に避ける形・本プロジェクトは parity が存在理由）、(2) portfolio read が drain と競合しロックが要る＝結局 A-1 より作業増。

#### D2-R1 — submit は bar 境界で一括 1 ホップ

`bt.submit_market(qty)` は **worker ローカルの list に貯める**だけ（クロススレッドしない）。cell が次の `next()` を呼んだ瞬間に「この bar の intent 束＋完了 signal」を **1 回の `call_soon_threadsafe`** で live loop へ渡す。consumer は resume 後に `_enqueue` 一括 → `await _drain()`。→ `on_bar → drain` の順序が `call_soon_threadsafe` の FIFO タイミング頼みではなく**構造で保証**され、replay の意味論（bar 本体で submit → bar-close で settle）と完全一致。`_intents` deque は従来どおり **live-loop thread しか触らない**不変条件を保つ。

#### D2-R2 — portfolio / buying_power も live loop に marshal

`bt.portfolio()` / `bt.buying_power()` も live loop へ round-trip（`run_coroutine_threadsafe(...).result()`・μs オーダー）し、**snapshot 構築を live-loop スレッド上で**行う。理由: ランデブーが排他するのは bar-drain だけで、`apply_venue_async_event`（poll/EC 由来の非同期 fill・`driver.py:228-273`）は別経路で live loop 上を走り portfolio を mutate する。worker が `bt.portfolio()` を読む最中に live loop が `apply_fill` する＝positions dict への 2 スレッド同時アクセス（立花 demo 場中 = AC#3 HITL でモロに踏む）。marshal で portfolio も `_intents` と同じ「live-loop スレッドしか触らない」不変条件に揃う。

#### D2-R3 — submit ルーティングは「現在 drive 中の bar の instrument」

`bt.submit_market(qty)` は replay と**同一シグネチャ**（instrument 引数なし）を保ち、live では bridge が rendezvous で保持する「現在 drive 中の bar の instrument」へ発注する（replay の "open bar's instrument" 契約 = `backtester.py:189` の live 版）。v19 の multi-instrument entry（`pending_buys.pop(bar.instrument_id)`）が live で正しく効くのはこの一点に依存。attach の primary `instrument_id` は **nominal**（`strategy.id` / seed snapshot 用）で submit ルーティングとは別物。

### D3 — 実行入口は per-cell RUN ただ一つ（mode-aware launcher・footer ▶ 退役）

per-cell RUN ボタンが現在の ExecutionMode を見て分岐する: **Replay → 現状の replay bt**／**LiveAuto＋venue 接続済み → live bt（A-1 bridge）を構築し live run を register→start**。ユーザー体験は「mode セグメントを Auto にして、同じ cell の同じ RUN を押す」＝AC#2 完全一致。走行中は **▶→■ トグル**（findings 0073 と同型）で stop=teardown。**従来別物だった per-cell RUN と footer ▶ が、mode-aware `run_cell` の一点で 1 本に合流**する。

**footer ▶（LiveAuto 起動・findings 0026 D3）を退役**（廃止）する。live run は「marimo の cell を run する」のと同一で、**システムは live run へ誘導しない**（ユーザーがどの cell を run すれば live run になるか理解している）。footer の **mode セグメント（Replay/Manual/Auto 切替）は残す**（mode はユーザーが明示的に持つ）。ADR-0016 D2「per-cell RUN を user-visible execution entry にする」の telos に live を合流させる。

### D4 — このアプリは marimo editor＝run/materialize は marimo 強制（非marimo = error）

editor で開くファイルは marimo であり、live materialize は「開いている notebook を marimo App として build → `LiveCellBridge` を作る」**だけ**。`is_marimo_app_file` の marimo↔imperative 分岐は持たない。`load_app(...) is None` 判定（replay `_select_replay_strategy` の `_backend_impl.py:348-350` と同じ）を **分岐の片側ではなく唯一の道**＝build できなければ即エラー `StrategyLoadError("not a marimo notebook: <path>")`（broken syntax は `SyntaxError` 素通し）。専用 `error_code = NOT_A_MARIMO_NOTEBOOK` を足し UI 文言を「marimo notebook ではありません」に明確化する。命令型 `strategy_loader.load(base_cls=Strategy)` は **editor live 経路から外れる**（コードは pytest / #24 golden / parity oracle の**直接** caller 用に存続＝oracle は既に `load_strategy(_V19_PY, base_cls=KernelStrategy)` を直接叩き editor loader を通らない）。これは ADR-0016 D4「命令型 UI 実行を sunset・programmatic/golden 用に存続」を「黙って dispatch せず明示的に弾く」へ一段強めたもの。

**スコープ**: #112 は run/materialize 契約のみ marimo 強制。Open 時の 1-cell wrap（findings 0054）は**据え置き**（非marimo を開けなくする (ii) は editor Open 層の別決定）。

### D5 — 終了は ■/切断のみ・場引け後は idle（live run の寿命 = venue セッション）

live cell run の寿命 = **■ stop（per-cell ▶→■）または venue 切断**まで。それ以外で自動終了しない。**live の `bt.replay()` は「キュー空 ≠ StopIteration」**（これが live 版の定義的差分）: `next()` は thread-safe キューを blocking get し、**番兵を引いた時だけ StopIteration**、空キューでは絶対に終わらず黙ってブロック＝idle。場引け（bar が止まる）はこれで自然に表現される（v19 は既に 14:55 で flatten 済み）。end 日付は live では無意味（`bt.replay()` は live で end を見ない）。findings 0026「live run の寿命 = venue セッション、mode とは別物」と一致。

#### D5-R — teardown のスレッド順序契約（self-deadlock 回避）

nautilus skill が明示警告する罠（live-loop self-deadlock）を契約化する:

- ■/切断 → `controller.detach`（caller/host スレッド・live loop ではない）→ `run_coroutine_threadsafe(driver.stop(), loop).result()`。
- `driver.stop`（live loop 上）: `_stopping` 立て → consumer cancel → **番兵を rendezvous queue に put**（worker の `next()` を解く）→ `bridge.on_stop`。**ここで worker を join してはならない**（live loop が worker を待つと、worker が D2-R2 の portfolio 往復で同じ live loop に `.result()` 待ちなら相互デッドロック）。
- `driver.stop` の `.result()` が返った後、**caller スレッド側で worker を join**（cell ループは番兵で既に脱出済み・caller は live loop でないので worker の最後の R2 往復を live loop が捌けて安全）。
- **不変条件**: live loop は決して worker を block-join しない／ live loop 往復を跨いでロックを保持しない。

### D6 — granularity を配線する（Replay↔Auto を任意粒度で parity）

`scenario.granularity → normalize_granularity → interval_ns`（単一 source of truth ＝ `engine.kernel.duckdb_bars._GRANULARITIES` 由来の kernel-side helper・nautilus-free）。live bar 間隔を `live_orchestrator.py:233` の **session-global ハードコード 60s から、run の granularity 由来 interval へ**置き換える。`LiveRunner` は既に `intervals_ns`（複数間隔）対応（`live_runner.py:46-52,77`）。

**なぜ ADR か**: 現状 granularity は live 経路で一度も参照されず（`python/engine/live/` に "granularity" の語が無い）、`v19` が `"Minute"` だから 60s と**偶然一致して動くだけ**＝parity が wired ではなく accidental。`"Daily"` cell を Auto にすると黙って 1 分足で駆動＝**silently wrong**（#112 が殺すべきバグの同類）。本 ADR で「granularity は両モードで同一 source of truth から駆動」を不変条件に格上げする。**却下＝最小（60s 固定・granularity != Minute なら明示エラー）**: v19 出荷には足りるが issue の telos「どの cell もシームレス」に反する（owner「理想的な完成形」）。

下位機構（per-run aggregator 供給 vs multi-interval 集約＋`KlineUpdate` に `interval_ns` タグ追加して driver が filter）は findings 0092 で pin する。

## Considered Options

- **実行モデル統一**: 採用＝**案A（mode-aware `bt` façade・同一 cell 両モード）**。却下＝案B（reactive `MarimoStrategy`/thin_drain 再利用＝休眠枝蘇生＋cell 書き直し退行＋AC 違反・D1）。
- **pull/push 橋渡し**: 採用＝**A-1 ランデブー（lock-step）＋R1 一括 submit＋R2 portfolio marshal**（業界標準の単一スレッド順序保証をスレッド分割越しに再構築・parity 構造保証）。却下＝A-2 free-run（発注順序が venue タイミング依存＝parity 崩壊・nautilus が意図的に避ける形）。
- **実行入口**: 採用＝**per-cell RUN を mode-aware 単一 launcher・footer ▶ 退役**（AC#2 同一ジェスチャ・ADR-0016 telos）。却下＝footer ▶ を live 起動に残す（mode 切替後に別ボタン＝AC#2 の精神に後退・二重化）。
- **marimo 検出**: 採用＝**marimo 強制 guard（非marimo = error・唯一の道）**。却下＝marimo↔imperative detect-first 分岐（このアプリは marimo editor で imperative を editor から走らせる経路を持たない・分岐は不要な複雑化）。
- **granularity**: 採用＝**配線（任意粒度で parity・理想形）**。却下＝60s 固定＋非 Minute エラー（最小・telos に反する）。
- **終了条件**: 採用＝**■/切断のみ・idle 容認**（findings 0026 と一致）。却下＝自動 end-of-day 終了（live に end 概念は無く、寿命 = venue セッション）。

## ADR-0016 / ADR-0012 / ADR-0021 / ADR-0006 との関係

- **ADR-0016 拡張（supersede ではない）**: ADR-0016 は「notebook = backtest（Replay）」を per-cell RUN で一本化した。本 ADR はその per-cell RUN を **mode-aware にして LiveAuto へ拡張**する（ADR-0016 が「LiveAuto へは拡張していない」と残した余地を埋める）。ADR-0016 の決定（`bt` façade・KernelRunner 無改変の per-bar 契約・byte-identical golden・▶→■ stop）は**全て踏襲**。`bt` の Replay 側意味論は無改変。ADR-0016 本文は無改変（自己保護条項）。
- **ADR-0012 踏襲**: kernel per-bar 契約（`on_start`/`on_bar`/`on_stop` ＋ `ctx.submit_market`/`ctx.pending`）は不変の adaptation 境界。live bridge は `KernelLiveDriver`（その契約の live 実装）を wrap し本体無改変＝命令型 oracle の byte-identity を保つ。marimo=target / 命令型=移行期 only（editor live 経路から外し programmatic/oracle 用に存続）を本 ADR D4 が一段履行。
- **ADR-0021 踏襲**: 単一 venue・実行時再バインド。live cell run は接続中 venue（`VenueConnectionViewModel` poll）で起動。本 ADR は venue 解決を変えない。
- **ADR-0006 踏襲**: #24 golden は byte-identical（live bridge は `KernelLiveDriver` 無改変・Replay 経路の `KernelStepper` も無改変）。
- **findings 0026（footer LiveAuto）**: 本 ADR D3 が footer ▶ 起動役を退役（mode セグメントは存続）。findings 0026 の orphan-teardown 不変条件（live run を mode-leave で stop）は per-cell ▶→■ stop ＋ venue-drop auto-replay で継続。

## Consequences

- **新規 live bridge** が landing する: `LiveCellBridge`（cell ループを worker thread で回し rendezvous で live driver に結線）＋ mode-aware `bt`（Replay/Auto で BarSource/ExecutionSeam を切替）。`Backtester` は thin/marimo-free を保つ（lazy-import 規律・ADR-0012 §4）。
- **`KernelLiveDriver._consume` に外科的フック**: strategy が bridge（async `drive_bar`）なら `await drive_bar(bar)` → `_drain()`。普通の Strategy は sync `on_bar` → drain で**無改変**＝命令型 parity oracle 無傷。
- **mode-aware `run_cell`**: Auto + 接続済みで live bt 注入＋engine の live driver を下に register→start→attach（bridge 構築）。per-cell RUN と footer ▶ が 1 本に合流。
- **footer ▶ 退役**: `LiveAutoTransportViewModel` の起動役を撤去（mode セグメントは存続）。findings 0026 の起動/pause/resume の責務は per-cell ▶→■（stop）へ移送、pause/resume の処遇は findings 0092 で決定。
- **granularity 配線**: `live_orchestrator.py:233` の 60s ハードコードを scenario 由来 interval へ。任意粒度 cell が Replay↔Auto で parity。
- **parity oracle 戦略**: `v19_morning.py`（命令型）＋`test_v19_auto_live_afk.py` を **退役させず**、cell live 経路が「`v19_morning.py` を Auto で回したのと同一の発注/約定列」を緑にしてから、最後に `v19_morning.py` の退役を判断する（issue の「もう消した」前提のまま進めると唯一の正解定義を失う）。
- **下位の実装事実**（bridge の正確な class／driver フックの seam／rendezvous queue・完了 future の機構／granularity→interval 供給機構／C# per-cell RUN mode 配線／footer ▶ 退役の cutover／error_code）は本 ADR に書き戻さず **findings 0092** に記録し本 ADR を「方針: ADR-0025」として参照する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。ADR-0016 / ADR-0012 / ADR-0021 / ADR-0006 / ADR-0007 / findings 0026 は別 decision の固定 oracle として踏襲し編集しない。下位の実装事実は findings 0092 に記録し本 ADR を「方針: ADR-0025」として参照する。（注: 本 ADR は #112 設計時に 0024 と採番されたが、#108-111 の puzzle-feel-drag ADR-0024 と番号衝突したため、実装着手時（#112）に **ADR-0025** へ採番し直した＝decision 内容は無改変・番号衝突の文書修正のみ。）
