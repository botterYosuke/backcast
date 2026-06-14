# findings 0013 — Live safety & graceful shutdown（#22・検証/クローズ・スライス）

方針: **ADR-0001 decision 3**（Unity死=Python死 / orphan 不在）と **decision 6**（正常終了時 broker
残注文 best-effort 取消）。ADR-0001 は自己保護のため編集せず「方針: ADR-0001」として参照する。
本書は #22 の下位確定事実（どの AC を何で証明したか・PASS 文字列・既知事項）を記録する。

先行: #7 S2-spike（findings 0005・AC(b) shutdown 順序を自己完結 seam で実証）/ #24 kernel tracer
（findings 0008）/ #25 Kernel Live Foundation（findings 0011・rails/gates/watchdog/cancel→detach を
kernel live 経路へ配線）。

## スコープ（grill-with-docs 2026-06-14 確定）

#22 は **検証/クローズ・スライス**であり、新規 safety 配線は無い。safety_rails / pre/post gate /
health_watchdog / graceful-stop→cancel→teardown→finalize 順序は #24/#25 で配線済み。本スライスは
それらが **production kernel-live 経路で実際に発火する**ことを証明し、mock の同期 FILLED が隠していた
**resting-order cancel** を実演し、**orphan 不在の構造不変条件**を assert し、**join-timeout fail-safe**
を入れる。

**production C# `OnApplicationQuit` owner は作らない**（実 Unity Live アプリの lifecycle owner は下流 UI
スライス #21/#23 の責務）。live 経路を駆動する C# は全て harness/probe（KernelLiveProbe / S2-spike /
LiveAdapterTracer）。#22 の AC は全て検証文言（「発火する test」「確認」「再現」）で UI 構築の語は無い。
安全機構の後付け禁止（ADR-0001）は **UI 完成前に安全機構を証明する**ことで満たす。

## 確定事実（4 ギャップ）

### Gap1 — resting-order best-effort 取消（AC3・CPython + Mono）

- **問題**: mock は同期 FILLED が既定（`mock_adapter.submit_order`）なので、shutdown 時に resting order が
  0 件で `cancel_inflight_orders` が空振りしていた。AC3「resting order が best-effort 取消され」を実演する
  テストが皆無だった。
- **production 停止順序はコードに既存**（grill で確認）: `stop_live_strategy` → `_control_run(stop_run)` →
  `strategy_host.stop_run`: `transition_to(STOPPING)`（graceful-stop）→ `_teardown`:
  `controller.cancel_inflight_orders`（timeout 6s・`driver.cancel_inflight`）→ `controller.detach`
  （`driver.stop` timeout 10s）→ `transition_to(STOPPED)`。その後 `stop_live_loop`（loop teardown）→
  C# `PythonEngine.Shutdown`（finalize）。これが AC3 の `graceful-stop → cancel → teardown → finalize`
  順序そのもの。#22 は **resting order を実在させて cancel フェーズを実走**させるだけ。
- **CPython**: `tests/test_kernel_live_shutdown.py::test_graceful_stop_cancels_resting_order_on_live_path`。
  mock を `set_next_order_outcome(status="ACCEPTED", filled_qty=0.0)` で resting させ、
  `cancel_inflight_orders` で order が `ACCEPTED → CANCELED`、`cancel_order_call_count==1`、open order==0 を assert。
- **Mono**: `run_all()`（後述）が ACCEPTED-未約定の注文を残し、`stop_live_strategy` の graceful 経路で
  `CANCELED`・`cancel_calls==1` を assert。新 fixture `spike/fixtures/strategies/kernel_buy_and_rest.py`
  （buy 1 lot・never sell）を使い golden twin（`kernel_spike_buy_sell.py`）は不変に保つ。

### Gap2 — health_watchdog の live-path 発火（AC1・CPython only）

- 機構は配線済み（`live_orchestrator.py:262` で `VenueHealthWatchdog` 構築、login 後 start、teardown で
  stop、`on_venue_logout=_publish_venue_logout` → `VenueLogoutDetected` backend event）。発火テストが皆無だった。
- **test-only plumbing**: `MockVenueAdapter` に `set_health(bool)`／`_healthy` を追加（`check_health` は従来
  ハードコード `return True`。`set_next_order_outcome` と同流儀）。
- **CPython**: `tests/test_health_watchdog_live.py`。`check_health→False` → `_tick` → `on_venue_logout` →
  `_publish_venue_logout` → `VenueLogoutDetected` が **ちょうど 1 回**（debounce）emit、復旧（True）で re-arm、
  再 False で 2 回目 emit、を assert。
- **スコープは emit まで**。VenueLogoutDetected を受けて run を止める owner は下流 UI（#21/#23）。watchdog が
  乗る backend-event marshal 境界は #20/#25 の既存 Mono event テストでゲート済みのため **Mono レグ不要**。

### Gap3 — orphan 不在の構造不変条件（AC2・CPython + Mono）

- 「Unity を kill して両方死ぬのを観測する」非決定的 kill テスト**ではない**（両方死ぬので観測不能）。
  ADR-0001 decision 3 のとおり同一プロセス埋め込みで自動成立する事実を **構造不変条件の assert** で示す
  （CONTEXT.md `orphan-absence invariant`）:
  1. **同一 PID**: live loop 上で実行した coro が返す `os.getpid()` が host と一致。Mono では C#
     `Process.GetCurrentProcess().Id` と一致を assert。
  2. **daemon thread**: live loop thread（`phase8-live-loop`・`live_orchestrator.py:210`）が `daemon==True`。
  3. **out-of-process order pump 不在**: `multiprocessing.active_children()` の増分が 0。
- **CPython**: `test_kernel_live_shutdown.py::test_orphan_absence_structural_invariants`。
- **Mono**: `run_all()` が `python_pid`／`loop_daemon`／`child_count` を返し、`KernelLiveProbe` が
  `python_pid == Process.GetCurrentProcess().Id`・`loop_daemon`・`child_count==0` を assert。

### Gap4 — join-timeout fail-safe（AC4・CPython・唯一の production コード変更）

- **問題**: `stop_live_loop`（`live_orchestrator.py`）は `thread.join(timeout)` 後 `is_alive()` を見ずに
  無条件で `_live_loop/_live_thread = None` にしていた。生存 worker（GIL 保持中かもしれない）の上で C#
  `PythonEngine.Shutdown` を行うとデッドロックする（S2-spike AC(b) の逆順 negative と同根）。
- **変更**: `stop_live_loop` を **bool 返却**化。`join(timeout)` 後も `is_alive()` なら `False` を返し
  **handle を null しない**（未停止の危険状態を観測可能に残す）＋ `"unsafe to finalize"` を error ログ。
  正常 join／未起動は従来どおり handle をクリアして `True`。この bool が C# finalize gate
  （`KernelLiveProbe` の `workerStopped` 相当）が消費する「**finalize しない**」契約。
- **CPython**: `test_stop_live_loop_fails_closed_when_worker_hangs`（loop.stop を無視する stub worker →
  `False`＋handle 保持）／`test_stop_live_loop_returns_true_on_clean_join`（happy path）。
- **C# skip-branch は GREEN 経路で未踏**: `KernelLiveProbe.cs` の「worker did not stop; skipping Python
  shutdown」分岐は GREEN 走では worker が必ず止まるため一度も実行されない（述語が書かれているだけ）。Mono で
  worker を実際にハングさせる試験は **GIL デッドロック誘発で CI 停止リスク > 価値**のため行わない（grill 確定）。
  Mono レグは happy path（`loop_stopped_clean==True`）のみ assert。

## 変更インベントリ

**production コード（1 箇所のみ）**:
- `engine/live/live_orchestrator.py` — `stop_live_loop` を bool 返却化＋hung-worker fail-closed（Gap4）。

**test-only plumbing**:
- `engine/live/mock_adapter.py` — `set_health(bool)`／`_healthy`（Gap2）・`cancel_order_call_count`（Gap1 観測点）。

**新規 fixture / spike**:
- `spike/fixtures/strategies/kernel_buy_and_rest.py` — buy 1 lot・never sell（resting order を作る）。
- `spike/kernel_live/run_mock_live.py` — `run()` は **不変**（#25 purity gate）。`run_shutdown_cancel()`
  （resting cancel + orphan + Gap4 happy path）・`run_all()`（twin roundtrip と統合・単一エントリ）を追加し
  `main()` を拡張。

**テスト**:
- `tests/test_kernel_live_shutdown.py`（Gap1 CPython / Gap3 CPython / Gap4 CPython×2）。
- `tests/test_health_watchdog_live.py`（Gap2 CPython）。

**Mono probe**:
- `Assets/Editor/KernelLiveProbe.cs` — `run()` → `run_all()` に切替、resting-cancel／orphan／clean-join を assert。

**記録**:
- `CONTEXT.md` に `orphan-absence invariant` term 追加。新 ADR 不要（decision 3 & 6 が既に所有）。FLOWS.md
  はこの repo に無い（正本は本 findings）。

## ゲート

```
# layer2 CPython 権威ゲート
uv run pytest -q
# import-purity + #22 full chain（fresh subprocess）
uv run python -m spike.kernel_live.run_mock_live   # → [KERNEL LIVE PURITY PASS] ... / exit 0
# layer3 Unity-Mono full Live（KernelLiveProbe は単一チェーン run_shutdown_cancel を駆動）
<UnityEditor> -batchmode -nographics -quit -projectPath . -executeMethod KernelLiveProbe.Run
```

注: Mono probe は #25 の二連チェーン（run_all）ではなく **単一チェーン `run_shutdown_cancel`** を駆動する
（resting-cancel + orphan + clean-join を被覆。#25 の fill-roundtrip は CPython 権威ゲートで毎回再証明される）。

## 実装結果（2026-06-14）

- **新規 CPython テスト GREEN**: `test_kernel_live_shutdown.py`（4）+ `test_health_watchdog_live.py`（1）＝5 passed。
- **#22 full chain PASS**（`python -m spike.kernel_live.run_mock_live`）:
  ```
  [KERNEL LIVE PURITY PASS] full-chain fills=2 final_net=0.0 realized=200.0
    resting=ACCEPTED->CANCELED cancel_calls=1 loop_daemon=True child_count=0 loop_clean=True nautilus_leaked=0
  ```
  → twin roundtrip（#25 不変）＋ resting cancel（Gap1）＋ orphan 不変条件（Gap3）＋ clean-join（Gap4 happy）＋
  Rust core 非ロードを 1 プロセスで通す。`test_kernel_live_purity.py` は本 PASS 文字列を grep するため引き続き GREEN。
- `compileall` PASS。

### code-review 反映（#22 code-review・2026-06-14・GREEN 30 passed）

high-effort code-review（7 angle finder → verify）＋ 2nd pass で High 1・Medium 5 を修正:

- **Gap4 の bool がどこにも consume されていなかった（High）** — `LiveLoopManager.stop_live_loop` が返す
  fail-closed bool を `_backend_impl.stop_live_loop` と `backend_service.stop_live_loop` が握り潰し（`-> None`）、
  C# `KernelLiveProbe` も `PythonEngine.Shutdown` を `workerStopped` だけで gate していて、Gap4 の信号が
  finalize 判断に一切届いていなかった。**両 facade で bool を return**（backend_service は例外時 False で fail-closed）・
  **probe は `workerStopped && loopClean` で Shutdown を gate** するよう修正。
- **`stop_live_loop` 異常路が `_live_loop` を残し `_ensure_live_loop` が dead loop を再利用（Medium）** — handle を
  両経路でクリアし、un-joinable thread は `_hung_loop_thread` に sticky 保持して **False を冪等**化（GIL 保持中の
  再呼出が True に fail-open しない）。test: 冪等 False ＋ thread 死後 True を assert。
- **resting order の ACCEPTED 観測が `submit_order_call_count` と race（Medium）** — counter は ACCEPTED 適用前に
  増えるため、`_await_resting_order`（ACCEPTED status を poll）に置換（spike＋test 両方）。
- **`_orders` 辞書の並行 iteration で "changed size during iteration" の恐れ（Medium）** — snapshot（`list(...)`）化。
- **`_FakeLoop` に `stop` 属性が無く `call_soon_threadsafe(loop.stop)` が AttributeError で握り潰され、test が
  主張する挙動を検証していなかった（Medium）** — `stop()` を追加し signaled を assert。
- **cleanup**: `run()`/`run_shutdown_cancel` の LiveLoopManager 構築を `_build_live_manager` に集約・`run_all` の
  `leaked` 三重計算を解消・`KernelLiveProbe` ヘッダの run_all→run_shutdown_cancel 齟齬を訂正。
- **Unity-Mono full Live gate（D5 layer 3）— 実行試行したが当該環境では PASS 不可・環境起因（#22 非起因）**:
  `KernelLiveProbe.Run` を Unity 6000.4.11f1 batchmode で計 4 回実行したが、いずれも worker（埋め込み Python の
  live チェーン）実行中に **非決定的なネイティブ Mono crash／異常終了**で停止し PASS マーカー未達。**#22 起因ではない**
  ことを次で切り分け済み:
  - **proven-GREEN な #25 `run()` シナリオ（throwaway `_DiagBaselineProbe`）も同環境で crash**（#22 の cancel 経路を
    一切通らない twin fill-roundtrip だけでも落ちる）。
  - **非決定的**: 同一コードで worker heartbeat が run 毎に 27／4／4／10 とばらつく（決定的ロジックバグなら一定）。
    1 回は native `Crash!!!`（mono runtime「UNKNOWN while executing native code / domain required for stack walk」・
    exit 139）、他は crash handler を経ず exit 127 で「Shut down」。
  - Temp/lockfile クリア・stray Unity kill・`__pycache__` クリアでも再現。
  - 環境差分: 本セッションで **`.venv` が uv により再生成**された（同根で catalog pytest fixture も欠落＝下記 8 件 RED）。
    2026-06-14 に同 kernel-live Mono gate が GREEN だった（findings 0011 §Unity-Mono full Live gate）こと自体が、
    **コード経路は Mono で健全**で、回帰は本セッションの環境（埋め込み Python ランタイム/native wheel 状態）にあることを示す。
  - **owner 対応**: 2026-06-14 GREEN 時の既知良好環境（`.venv`/native ランタイム＋catalog data）を復元し再走する。
    CPython 権威ゲート（layer2）と import-purity full-chain（layer3 と同一 Python 経路を fresh subprocess で完走）は
    GREEN のため、#22 のロジックは検証済み。残るは Mono ホスト上の環境健全性確認のみ。

#### 深掘り調査結果（2026-06-14・crash 箇所の localization）

ステップマーカー（`logging.warning("[SC] …")`）を `run_shutdown_cancel` に仕込んで Mono probe を再走し、
crash を **`mgr.venue_login("MOCK")` 内**（`[SC] login:begin` の直後・`set_mode` 到達前）に特定。`venue_login` は
daemon asyncio loop thread（`phase8-live-loop`）を起こし最初の cross-thread GIL marshal
（`run_coroutine_threadsafe(adapter.login).result()`）を回す箇所＝S2-spike(#7) が検証した seam そのもの。**crash 箇所
自体が flaky**（本 run は login、run 1 は run() 完走後の run_shutdown_cancel 深部＝27 beats）で、native stack は
unsymbolized（"UNKNOWN while executing native code / domain required for stack walk"）＝**埋め込み CPython × Mono ×
pythonnet の GIL/thread-state native fault**。

切り分けで除外した候補:
- numpy 2.4.6→2.4.4 / scikit-learn 1.9.0→1.8.0（TTWR 一致）に下げても再現 → native wheel version ではない。
- CPython pin 3.13.11（2026-06-14 GREEN と同一）→ pin regression ではない。
- pythonnet bridge は project の `Assets/Plugins/Python.Runtime.dll`（venv 非依存・未変更）→ bridge drift ではない。
- `venue_login` は #25 コード（#22 未変更）→ #22 起因ではない。

**追加切り分け（CPython build 仮説を棄却）**: backcast の uv CPython 3.13.11 build は本セッションの venv 再生成時
（`python313.dll` mtime 2026-06-14 12:05）に refresh されており、2026-06-14 GREEN 時の build と異なる可能性が
あったため、`pyvenv.cfg` の `home=` を TTWR の既知安定 build **cpython-3.13.13（2026-05-20 build）** へ一時切替して
Mono probe を再走（3.13.x は ABI 互換なので venv site-packages はそのまま使える）。→ **3.13.13 でも同様に crash**
（exit 127・8 beats・verdict 未達）。よって **CPython build/version は根本原因ではない**（3.13.11-fresh / 3.13.11-prior=GREEN /
3.13.13-stable のいずれでも flaky に crash）。pin は 3.13.11 へ復元済み。

**再起動後の再走（owner がマシン再起動）— 改善せず**: クリーン state で run 1=22 beats / run 2=4 beats といずれも
crash（exit 127・verdict 未達）。reboot は beat 数を改善する（22 vs degraded 4–10）が **pass はしない**。クリーンが
必要条件だが十分条件ではない。

**決定的 A/B（cancel-path 仮説を棄却）**: 「#22 の resting-cancel marshal（`cancel_inflight → broker.cancel →
await adapter.cancel_order` の cross-thread marshal）が Mono crash の trigger では」という仮説を、throwaway
`_DiagBaselineProbe` で **proven-GREEN な #25 `run()`（cancel marshal を一切通らない twin fill-roundtrip）** を
**クリーン再起動直後の初回ラン**として走らせて検証。→ **`run()` も同様に crash**（22 beats・exit 127・PASS 0）。
よって **cancel marshal は trigger ではない**＝#22 の resting-cancel 固有の問題ではなく、**#25 の path（2026-06-14 に
GREEN）すらクリーン state で crash する一般的不安定性**。

**最終結論**: 埋め込み Python × Unity-Mono × pythonnet の **GIL cross-thread marshal そのものの native 不安定性**で、
**#22・cancel path・CPython version(3.13.11/3.13.13)・CPython build・numpy/scikit・pythonnet bridge・reboot の
いずれでも解消しない**。2026-06-14 に同 Mono gate（#25 `run()`）が GREEN だったので、2026-06-14 以降にマシン/ランタイム
環境が回帰したことになる（venv 再生成では説明しきれない＝machine-level の何かが変化）。**恒久対策は (a) 2026-06-14
GREEN 当時のマシン/ランタイム差分の特定、または (b) GIL-state トレース付き Mono/pythonnet native デバッグ（symbol 必要）**
で、いずれも **#22 の成果物（CPython 権威ゲート GREEN）とは独立した infra タスク**。#22 のロジックは CPython で完全検証
済みのため、本 slice は CPython ゲート GREEN をもってロジック完了とし、Mono layer-3 は infra 回帰の解消後に再走する。

### 既知事項（#22 非起因）

- **catalog 依存テストが fresh `.venv` で RED**（`test_kernel_bars` / `test_kernel_golden_cpython` /
  `test_kernel_risk_gate` / `test_kernel_teardown_mono`・計 8 件）。`bars=0`／`bar_count: 68 != 0` で、Replay
  catalog parquet fixture が当該環境に不在なことが原因。#22 の変更を stash した baseline でも同一に失敗するため
  **pre-existing な環境/データ問題**であり #22 起因ではない（#22 は mock-venue kernel-live で catalog 非依存）。
  catalog fixture を整えれば解消する想定。
