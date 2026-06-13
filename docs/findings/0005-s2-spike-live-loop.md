# S2-spike Findings: live asyncio-loop cross-thread marshal ゲート（Step 2 threading seam）

- Issue: #7 (S2-spike — live asyncio loop + sub-thread tick push を venue 実接続前に Unity Mono で検証)
- Epic: #1 ／ **Step 2(#4) の前提ゲート**
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0001 — Unity/pythonnet 全埋め込み](../adr/0001-unity-pythonnet-embedded-frontend.md)（proposed; decision 6 が本ゲートを前提とする）, [ADR-0002](../adr/0002-embedded-python-runtime-placement-and-resolution.md)（accepted）
- 配置の根拠: 本ゲートの **outcome は下位事実**（pass/fail・閾値・再走手順）であり ADR には書き戻さない。ADR-0001 decision 6 は既に「S2-spike(#7) の AC(a) 成立が前提」と明記済みなので、本 findings はその前提の **充足記録**として機能する（ADR を「方針: ADR-0001」として参照するのみ）。
- 先行: #2（S0 / threaded backtest gate / findings 0001）, #9（Replay tracer — CPython smoke + Mono probe の二段切り分け / findings 0001）
- 実行環境（先行 slice と同一）: Intel x86_64 / macOS 13.7.8 / Unity 6000.4.11f1（standalone=Mono）/ CPython 3.13.13 / nautilus 非依存

> **状態: AFK ゲート GREEN（Mac leg, 2026-06-13）。** 設計は `grill-with-docs` で確定、直接実装（Python seam model + C# Mono probe の 2 言語だが、grill で仕様完全ロック・単一契約・~4 ファイル）。`S2SpikeLiveLoopProbe.Run` が batchmode で `exit=0`、CS エラー 0、例外 0。CPython smoke も 3 連続 GREEN。実装結果は §8。
> **Windows headless leg も GREEN（2026-06-13、#18 / Step2 prereq）**: 本 win_amd64 マシン（CPython 3.13.11 / Unity 6000.4.11f1 Mono）で `S2SpikeLiveLoopProbe.Run` を batchmode 実行し PASS。`PythonRuntimeLocator` を OS 分岐させ Windows uv home を `pyvenv.cfg` の `home=` から導出（実測値は §8）。CPython smoke も Windows venv で PASS。
> **未了（owner 手元）**: ① **playmode render leg**（実 Unity フレーム cadence）は default-disabled harness を owner が手動再走（§6）。② **S0 Windows leg**（#18・別ゲート）— S2-spike とは別物で、threaded Nautilus backtest を Windows で通す。**⛔ RED（背景スレッド経路）**: nautilus import + pin は通るが **C# background worker thread 上の `BacktestEngine.run()` で segfault**。owner 指定 3 診断（A logging / B synthetic+no-strategy / C main-thread）で切り分けた結果、**main thread では GREEN**＝原因は **Windows-Mono + pythonnet で foreign background thread に GIL state を載せて Rust run-loop を回す**組合せ固有（ADR-0001 decision 4 の threading 経路）。Windows-Mono 埋め込み自体の非互換ではない。詳細は `docs/spike/s0-result.md §1.1`＋診断ラダー。**ADR-0001 は accepted 昇格不可（AC①＝background-worker GREEN 未達）、#18/#4 は本件が解けるまで前進不可**（owner 判断: pythonnet/Mono の thread attach・`PyGILState` 登録の調査）。

---

## 1. なぜ S0(#2) green では Step 2 を de-risk できないか — seam の別物性

S0 は **threaded backtest**（有界・1 回 run・C# worker が `Py.GIL()` 保持で `BacktestEngine.run()`）を検証した。Step 2 の live は threading seam が違う：

| | backtest（S0 / #2） | live（Step 2 / #7 が gate） |
|---|---|---|
| native | Rust core run loop | tokio + venue WS スレッド |
| 時間 | 秒・1 回 | 時間〜無限・`run_forever()` |
| host→engine | `Py.GIL()` 内で同期 run | **`run_coroutine_threadsafe(coro, loop).result(timeout)`** で loop に marshal |

S0 が触れていない**核の未知数 = cross-thread asyncio marshal の GIL 往復**：host worker が `.result()` で待つ間に内部で GIL を解放（`Condition.wait`→blocking acquire 周りの `Py_BEGIN_ALLOW_THREADS`）→ loop スレッドが GIL 取得して coro 実行 → worker 再取得。これが **Mono+pythonnet で健全か**を venue 実接続の前に単独検証する（S0 に混ぜず seam を明示分離）。

## 2. 設計ロック（grill-with-docs, owner 確定 2026-06-13）

- **粒度1 の自己完結 seam model** を CPython smoke と Mono probe の **両方**が駆動（option 3⊇1）。production `LiveOrchestrator` は直接駆動しない（venue stack / LiveRunner / Nautilus kernel を失敗原因に混ぜず診断力を保つ）。CPython smoke は事前切り分け、**Mono probe が本判定**。
- **production 対応はコメント/本 findings に固定**（コード重複の意図的受容、S1 と同型）:
  - loop 所有 → `python/engine/live/live_orchestrator.py:164`（`_ensure_live_loop`: `new_event_loop()`→`run_forever()` を daemon thread "phase8-live-loop" で）。
  - host-facing marshal → `python/engine/live/engine_controller.py:583`/`:614`（`run_coroutine_threadsafe(_cancel/_stop, loop).result(timeout=6/10)`）。
- **GREEN 判定 = prompt completion であって「no-hang」ではない**（owner が issue 本文で明示）。`.result(timeout)` は GIL starve でも無限ハングせず `TimeoutError` を投げるため、毎コール timeout する壊れた系も「ハングしない」を満たす。→ elapsed ≈ coro 固有コストを assert。

## 3. AC(a) — cross-thread GIL 解放 = no-deadlock（本ゲートの核）

owner 確定の assertion 設計・閾値（`python/spike/s2spike_live_loop.py` の定数と一致）:

- **coro**: `time.perf_counter()` 基準で **~10ms GIL 保持の実 Python 仕事**（busy-loop, work counter を返す）＋ `await asyncio.sleep(0.05)`。固有 ~60ms。`async def f(): return 1` は速すぎて GIL 往復を露出しないため不可。
- **`.result(timeout=5.0)`**：no-hang→`TimeoutError` の安全網であって PASS の拠り所ではない。
- **計測**：C# `Stopwatch` で `InvokeMethod` 全体を計測。**tick push 稼働中に 10 回 marshal**（1 回成功は GREEN にしない＝長時間 loop の継続的 GIL 往復が対象）。
- **同時 order call**：別 worker スレッドで `marshal_order` を並行発行（2 C# スレッドが GIL + 単一 loop を競合）。同じ band で個別検証。

**PASS 条件**（役割分担: timeout=安全網 / 全 call 上限=starvation 検出 / median=定常性 / 下限+work counter=no-op false-green 検出）:
- 全 call: 例外/`TimeoutError` なし。
- 全 call: `0.04s ≤ elapsed < 1.0s`（上限は 5s timeout 境界から十分離れ starvation/timeout-乗りを検出、下限は no-op 経路を検出）。
- `median < 0.25s`（定常 prompt 性。cold first call も含めて隠さない）。
- `tick count > 0`（sub-thread tick push が loop に到達＝loop が host の `.result` 待ちで starve していない）。
- order result `== ACK:*`（coro 実行確認）かつ同じ band。
- **main thread heartbeat が stall しない**（headless proxy for 安定描画）：main は GIL を一切取得せず、heartbeat 間隔の最大 gap `< 200ms`（frame-hitch baseline）。

## 4. AC(b) — shutdown 順序 = ADR-0001 decision 6（broker 残注文取消）の生死

- **正順 Mono ゲート**: worker が `graceful_stop()`（`run_coroutine_threadsafe(_cancel).result(6)` → `(_stop).result(10)`、prod と同じ marshal）。**main は `Join(10000)` だけ**（wait は worker 側＝Unity quit 中の ANR キル回避）。**Join 成功後だけ** loop stop/join → runtime finalize（逆順禁止）。assert: `cancel_ran==True` && `resting==0` && `stopped==True`、main GIL 取得なし。
- **runtime finalize まで実検証する**（codex review #1 で「skip では AC(b) の finalize 順序が未充足」と指摘・修正済 2026-06-13）: 全 worker join + loop teardown が済み **どのスレッドも GIL を保持しない** ことを確かめた後、main で `PythonEngine.EndAllowThreads(savedState)` → `PythonEngine.Shutdown()` を実行し、**deadlock せず返ること**で全順序（graceful_stop → join → loop teardown → runtime finalize）の健全性を assert する。逆順なら（worker が GIL 保持中の finalize）ここで deadlock する。Phase A の `w1/w2.Join` と teardown thread の `t.Join` も**戻り値を必須 assert**（hang を false-green にしない）。
- **Join timeout 時は FAIL し runtime を finalize しない**（生存 worker が Python 使用中の finalize は危険）。FAIL 経路では finalize を行わず `Exit(1)`（`engineStarted` フラグで「未 finalize」を明示ログ）。
- **逆順 negative micro-check（独立インスタンス）**: loop を先に `teardown_loop()`（stop/join/**close**）してから cancel coroutine を schedule → `run_coroutine_threadsafe` が即 `RuntimeError`（loop closed）で失敗（6s 待たない）→ `cancel_ran==False` / `resting>0`。生成済み coroutine は `close()` して "never awaited" warning を防ぐ。これは**因果の文書化**（decision 6 が逆順で空振りする根拠）であり、正順 Mono ゲートが**実際の可否判定**。

**(a) 不可だった場合の含意**（今回は green なので発生せず・記録のみ）: graceful cancel も同じ marshal を通るため、decision 6「正常終了 best-effort 取消」が**この経路で達成不能**（broker 指値が残る）→「live が遅い」ではなく **安全要件の finding**。ADR-0001「Nautilus が Mono を拒んだ場合」と同じ姿勢で decision 6 の gating（別機構 or 受容）を**新規 superseding ADR**に上げる（ADR-0001 を edit しない）。

## 5. 成果物（識別子: 裸の `S2` は使わない・人間向けは常に `S2-spike`）

- `python/spike/s2spike_live_loop.py` — 自己完結 seam model（`LiveLoopSeam`）+ CPython smoke `run_smoke()`/`main()` + `reverse_order_negative_check()`。
- `Assets/Editor/S2SpikeLiveLoopProbe.cs` — Mono headless 本ゲート（`-executeMethod S2SpikeLiveLoopProbe.Run`、self-failing、`exit 0/1`）。
- `Assets/Scripts/S2Spike/S2SpikeLiveLoopHarness.cs` — render leg（default-disabled playmode harness）。
- log tag: `[S2-SPIKE LIVE LOOP PASS]` / `... FAIL]`。

## 6. Play 所有権（排他的再走手順・衝突ではない）

二つの auto-bootstrap が両方 `PythonEngine.Initialize()` を呼ぶと race するため Play は単一所有。既定は `ReplayPanelsHarness` が owner。S2-spike render leg を実測する時だけ `ReplayPanelsHarness` の auto-bootstrap を OFF・`S2SpikeLiveLoopHarness.AutoBootstrapEnabled` を ON にして Play → `[S2-SPIKE LIVE LOOP PASS]` を読み、復元する。S0SpikeHarness と同じ温存方式。render leg の PASS 条件（codex review #2/#5 で強化 2026-06-13）: frames≥300 ＋ `runs/orders/ticks>0` ＋ **post-warmup の max-hitch `< MAX_HITCH_S(0.2s)` かつ hitch 回数 `≤ MAX_HITCHES(5)`**（全フレーム 150ms のような定常 hitch も FAIL になる）＋ **worker が `graceful_stop`＋`teardown_loop` を完走し post-condition（cancel_ran && resting==0 && stopped && teardownOk）が成立してから**（PASS を worker 停止前に出さない）。main は終始 GIL 非取得。

## 7. 既知の単純化（spike として意図的）

- runtime パスは `PythonRuntimeLocator`（#9 M3 の共有 resolver）。build branch（StreamingAssets）は #2 Windows leg まで未検証のまま（ADR-0002 slice split）。
- seam PyObject は worker 上で `Py.GIL()` 内生成・他 worker から GIL 下で参照・**Dispose しない**（process exit が回収）。⚠️ S0/S1 probe は `Shutdown()` を skip するが、**S2-spike probe は成功経路で `Shutdown()` まで実行する**（§4 = AC(b) finalize 順序の検証が本ゲートの一部のため）。全 worker join 後に main が GIL を再取得して finalize するので deadlock しない。FAIL 経路でのみ skip。
- coro は固定反復数ではなく `perf_counter` 基準で GIL 保持時間を確保（CI マシン差で iteration 数が変わっても ~10ms を維持）。
- W2（並行 order）は `_seam != null` ではなく **`_seamReady`（`start()` 完了後に publish）** を待つ（codex review #3）。W1 は `_seam=Invoke()`→`start()` 間で GIL を手放さないため現状 race は不可達だが、publish-then-start の latent footgun を構造的に除去。

## 8. 実装結果（Mac leg, 2026-06-13）

- **CPython smoke**（`.venv/bin/python spike/s2spike_live_loop.py`、3 連続）:
  `[S2-SPIKE LIVE LOOP PASS] calls=10 median≈0.0611s band≈[0.060,0.071]s ticks≈93 order=ACK:1 cancel_ran=True resting=0 reverse_order_guarded` / `exit=0`。
- **Mono headless probe**（batchmode `-executeMethod S2SpikeLiveLoopProbe.Run`、codex review 修正後の再走）: `unity_exit=0`、CS エラー 0、例外 0。
  `[S2-SPIKE LIVE LOOP PASS] calls=10 median=0.0611s band=[0.0604,0.1055]s order=ACK:1 orderElapsed=0.0837s ticks=92 maxStall=12ms cancel_ran=True resting=0 reverse_order_guarded runtime_finalized`
  ＋ `runtime finalized in order (graceful_stop -> join -> loop teardown -> EndAllowThreads + Shutdown) without deadlock`。
  → median ≪ 0.25s、全 call が band 内、**maxStall=12ms ≪ 200ms**（main は最後まで GIL-free）、tick=92（loop 非 starve）、decision-6 cancel が marshal を通って完走、**runtime finalize が全順序で deadlock せず完了**。

### 8.1 Windows leg（win_amd64, 2026-06-13・#18 / Step2 prereq）

- **環境**: Windows 11 / CPython **3.13.11** win_amd64（production pin）/ nautilus-trader 1.226.0 / Unity 6000.4.11f1（Mono）。
- **CPython smoke**（`python/.venv/Scripts/python.exe python/spike/s2spike_live_loop.py`）:
  `[S2-SPIKE LIVE LOOP PASS] calls=10 median=0.0644s band=[0.0602,0.0745]s ticks=106 order=ACK:1 cancel_ran=True resting=0 reverse_order_guarded` / `exit=0`。
- **Mono headless probe**（batchmode `-executeMethod S2SpikeLiveLoopProbe.Run`、`UNITY_EXIT=0`、CS エラー 0、例外 0）:
  `[S2-SPIKE LIVE LOOP PASS] calls=10 median=0.0621s band=[0.0602,0.0652]s order=ACK:1 orderElapsed=0.0687s ticks=103 maxStall=18ms cancel_ran=True resting=0 reverse_order_guarded runtime_finalized`
  ＋ `runtime finalized in order (graceful_stop -> join -> loop teardown -> EndAllowThreads + Shutdown) without deadlock`。
  → Mac leg と同水準（maxStall=18ms ≪ 200ms、median ≪ 0.25s、全順序 deadlock-free）。**deploy OS=Windows でも cross-thread asyncio marshal + shutdown 順序が健全**。
- **runtime 解決の差分**（slice-level fact・ADR-0002「方針」参照のみ・ADR 非編集）: `PythonRuntimeLocator` を `Application.platform == WindowsEditor` で分岐。Windows は uv CPython home を `python/.venv/pyvenv.cfg` の `home=`（= `…\cpython-3.13.11-windows-x86_64-none`）から導出、libpython=`python313.dll`、venv site=`.venv\Lib\site-packages`。Mac 分岐（documented uv consts）は不変。

## 9. 判定

- **AFK core seam GREEN（Mac leg）**: `run_coroutine_threadsafe(...).result()` の cross-thread GIL 往復 + shutdown 順序（runtime finalize 含む）は Unity Mono で健全。これは **headless AFK レグの GREEN** であって `#7 全体`の GREEN ではない。
- **`#7` fully GREEN は playmode PASS 後**（grill ロック済み判定 §3/§6）。したがって現時点で **#4（Live/Auto parity）は未 unblock**。#4 着手の前提として次を閉じる必要がある: ① **playmode render leg の owner 実測**（§6・`S2SpikeLiveLoopHarness` の hitch budget + shutdown post-condition PASS）② **Windows leg**（ADR-0001: deploy OS=Windows で最終確認・Mac-green は `win_amd64` を保証しない）。
- ADR-0001 decision 6 の前提 AC(a) は **headless で充足**（broker 残注文取消の marshal 経路は Mono で生存）。残 2 レグが閉じるまで #4 を着手可能とは判定しない。
