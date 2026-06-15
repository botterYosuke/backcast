# findings 0023 — Replay transport footer（play / pause / step / speed / stop）

issue #30。親: #3（Step 1: Replay parity）/ #5（Step 3: カットオーバー）。
方針: **ADR-0005（1:1 表面 parity・固定 decision）/ ADR-0001（pythonnet 埋め込み・C# adapter が run lifecycle 所有）/
ADR-0006（DuckDB→kernel Replay）**。`grill-with-docs`（2026-06-15）で導出。
移植 oracle = TTWR `src/ui/footer.rs` + `src/replay/orchestration.rs` + `src/protocol.rs`（`TransportCommand`）。
ADR-0005/0001/0006 は自己保護条項を持つため本 findings に実装事実を記録し、ADR は参照のみ（書き戻さない）。
CONTEXT.md の **[[footer（screen-fixed chrome / 実行トランスポート）]]** 用語を本スライスで確定。

---

## 1. 移植 oracle の実態（TTWR を直接読んだ確定事実）と backcast との seam 差

TTWR footer は `TransportCommand`（`Pause`/`Resume`/`StepForward`/`ForceStop`/`SetSpeed(u32)`…）を UI→backend に
**mpsc channel** で送り、backend dispatcher が Python の `pause_backtest`/`step_backtest`/`set_replay_speed` 等を呼ぶ。
**全て実配線済み**（step も speed も stub ではない）。speed options = `[1, 2, 5, 10, 50]`、default 1x。

**backcast の transport seam は enum + mpsc ではない**（移植しない）。in-proc pythonnet の**直接メソッド呼び出し**で、
host がバー間（per-bar sleep が GIL を解放する隙）に GIL を取り [[Backcast Execution Kernel（kernel）]] の制御プリミティブを
叩く。`TransportCommand` enum/mpsc を持ち込むのは backcast のアーキ（in-proc・単一プロセス）に逆らうため却下。

## 2. 一次コード読みで確定した現状（issue #30 本文の前提を訂正）

| issue #30 の前提 | 実態（裏取り） |
|---|---|
| 「`ReplayRunLifecycle` は status-only で sticky-terminal」 | **`ReplayRunLifecycle.cs` は #50 で削除済み**（findings 0022 D2: #68 nautilus scaffold と共に retire）。status は throwaway harness の `_statusText` 直書きのみ。→ §6 で terminal authority を再定義し小型 lifecycle を再建。 |
| 「footer から play」 | run 起動（`start_engine`）は **#29 の "Run Replay" ボタンが所有**。footer ▶ は文脈依存（resume/pause/re-arm）に再定義（§5 C1）。 |
| 「engine が pause/step を駆動できるか要確認」 | pause/resume/stop は **kernel 経路で配線済み**（`core.py` `pause_replay`/`resume_replay`/`stop_replay`/`force_stop_replay` が `run_event`/`replay_stop_event` を駆動、`_start_engine_duckdb` が `KernelRunner` に注入）。**step と speed は欠落**（§3）。 |

**kernel 経路では `_replay_state` が IDLE/LOADED/RUNNING/PAUSED を正しく遷移する**（`core.py:140/255/265/285`）。
findings 0003 §1 の「poll の replay_state は IDLE 固定」は**削除済み nautilus 経路**（`start_nautilus_replay` が `_replay_state` を
触らなかった）の話で、kernel 経路には当てはまらない。→ footer の Idle/Loaded/Running/Paused は poll の `replay_state` から取れる。

## 3. engine 側の 3 欠落（本スライスの縦切りスコープ）

1. **step プリミティブが無い**。`step_replay()`（`core.py:393`）は存在するが `_advance_one_locked()` 経由で **legacy provider**
   （`_replay_provider`/`_replay_providers`）を進める。kernel 経路では provider=None（`core.py:202` 「`_replay_provider` stays None」・
   バーは `KernelRunner` の run が `apply_replay_event` で外部ストリーム）なので、kernel 経路で `step_replay` を呼ぶと空 provider →
   `core.py:388-391` の **random-price フォールバック**に落ちる＝実質死。`KernelRunner` のループ（`runner.py:212-281`）にも step seam は
   無い（あるのは `run_event.wait()` の pause 二値と `stop_event` の中断のみ）。
2. **`set_replay_speed` は no-op スタブ**（`core.py:314-319`：`multiplier>0` を検証して `True` を返すだけ）。しかも
   `effective_interval` は**ループ前に 1 回**計算（`runner.py:196-198`）され `_ANIM_BUDGET_SEC=2.0`（`runner.py:40`）の総量キャップ付き
   → **走行中の速度変更が反映されない**。
3. **`InprocLiveServer` が transport RPC を公開していない**（`inproc_server.py:212-214`：#50 で #68 forwarder を「production caller が
   無い」として撤去）。**#30 がその caller になる**。

## 4. engine 制御ループの設計（runner.py の pause/step/speed ゲート）

**選択肢: primitive 注入を拡張**（run_event/stop_event/bar_interval_sec/sink と同型の注入規約の自然な延長。kernel は host 型を
import せず stdlib 型のみ。golden は全 default-off で byte-identical）。

### (A) 待ち機構 = step_event 加算＋短 poll（run_event/stop_event は無改変）

`run_event`/`_replay_stop_event` は `pause_replay`/`resume_replay`/`stop_replay`/`force_stop_replay` の 4 箇所で mutation され、
public property として `KernelRunner` に注入され、test 2 本（`test_kernel_runner_production_seam.py:87` の run_event gate /
`test_replay_review_fixes.py:120` の stop_event break）で挙動固定。→ **触らない**。`step_event:threading.Event` を**純加算**。

ゲート（`runner.py` の `for bar in bars` 先頭）:

```python
for bar in bars:
    if self._stop_event is not None and self._stop_event.is_set():
        stopped_reason = "stopped"; break
    if self._run_event is not None:
        while not self._run_event.is_set():            # paused
            if self._stop_event is not None and self._stop_event.is_set():
                break                                  # 下の再チェックで for を break
            if self._step_event is not None and self._step_event.wait(0.05):
                self._step_event.clear()               # 1 パルス消費
                break                                  # この 1 バーだけ通す
        if self._stop_event is not None and self._stop_event.is_set():
            stopped_reason = "stopped"; break
    self._sink.push_bar(bar)
    ...
```

- **1 バー正確**: `step_event` を `clear()` してから while を抜け、1 バー処理後にループ先頭へ → `run_event` は依然 clear なので
  再ブロック＝構造保証。
- **stop 即応**: paused 中も poll 各周回で `stop_event` を拾い、抜けた後に再チェックして break（現状の「paused 中 stop で 1 バー
  余分に進む」より綺麗）。run_event/stop_event の mutation 側は無改変。
- 代償は paused 中 50ms 毎の微 poll（体感・CPU とも無視可）。Condition 一本化は run_event API + 4 mutation + test 2 本の refactor＝
  blast radius が大きく、買うのは「体感ゼロの latency 解消」のみで割に合わないため却下。

### (C) step は PAUSED 限定（paused-start machinery は作らない）

TTWR の `LoadAndStep`（step-from-idle/loaded）は inproc dispatcher で **deprecated/stub**（"no longer supported; use RunStrategy"）。
AC も「pause 中 step で 1 バー」しか要求しない。→ kernel 経路の step は **RUNNING→pause 後の PAUSED からのみ**。`start_engine` は
無改変（paused-start 変種を作らない＝最小スコープ）。

**step_event 衛生（stale pulse 防止）**:
- `step_replay()` のガードを kernel 経路で **PAUSED-only** にタイトン（LOADED は legacy/test seam に分離、kernel 経路は
  `False, "StepReplay requires a paused run"` を返す）。理由: LOADED で `step_event.set` しても runner ループは未起動 → pulse が
  残り、後の `start_engine`（RUNNING, run_event=set）はゲート while に入らず未消費 → ユーザが pause した瞬間に stale pulse を
  勝手に消費して 1 バー進むバグになる。
- `start_engine`（fresh run）と `resume_replay`（再開時）で `step_event.clear()`（`core.py:256` が `_replay_stop_event.clear()` を
  start で呼ぶのと同じ衛生パターン）。
- kernel 経路 step は `_advance_one_locked()` を**呼ばない**（`step_event.set()` のみ。実バー生成は `KernelRunner` ループが
  `push_bar→sink→apply_replay_event` で行う＝live data path をそのまま使う）。

### (D) speed = interval=base/multiplier（毎バー holder 読み）＋総量キャップ撤廃

`effective_interval` を**毎バー** speed holder から読み、`interval = _REPLAY_BAR_INTERVAL_SEC(0.01) / multiplier`。
`set_replay_speed(n)` が holder（`DataEngine._replay_speed_multiplier`、default 1、`_lock` 下）を更新し、`_start_engine_duckdb` は
`speed_provider` callable（または holder）を `KernelRunner` に注入。golden は base=0 → interval 0 で byte-identical。
speed options=`[1,2,5,10,50]`・default 1x で TTWR parity。

**`_ANIM_BUDGET_SEC=2.0` 総量キャップ（`runner.py:40,198`）は transport 制御下で撤廃する。**
- 理由: cap は #49-review-#3 で **transport が無い前提の #29 auto-follow ハック**（長 Minute run が `N*0.01` 秒寝るのを防ぐ）だった。
  長 run では `2.0/n_bars` が既に ~0.00002s まで潰れ、それを multiplier で割っても**体感不変**＝「長 run をバー単位で観察する」という
  speed/step の主目的そのものを cap が殺す。#30 が stop/speed の **rate 所有権をユーザに渡す**以上、自動即時化はユーザの選択を奪うだけ。
- **逆転コスト（明示）**: `test_replay_review_fixes.py:180-216`（budget-cap テスト・`slept["total"] <= 3.0` を N=1000 で assert）を
  更新/削除する。これは #49-review-#3 の決定を意図的に覆すもの。長 run の高速完走は **50x または stop** で代替。footer default は
  TTWR 同様 1x（長 Minute run の 1x が遅いのは parity 上正しい挙動）。golden は base=0 で従来通り無影響。

## 5. C# 側の設計

### (C1) footer ▶ = 文脈依存（TTWR `footer_pause_resume_system` と同型）

`▶`: **PAUSED→resume / RUNNING→pause / Idle・Loaded・Done・Failed→run 起動（re-arm）**。re-arm は **#29 と同一の `OnRun` 経路**
（`TryStartRun`→launcher: `load_replay_data`→`start_engine`）を再利用（ロジック重複なし）。`force_stop_replay` が providers/duckdb_root を
クリア（`core.py:294-301`）した後でも launcher が reload 込みで正しく re-arm する。#29 "Run Replay" ボタンは config panel に残し、
両者とも同じ `OnRun` を叩く。stop ボタン = `force_stop_replay`（TTWR `ForceStop` 相当・findings 0017 §9(b) が #30 に委譲）。

### (C2) 小型 lifecycle 型を再建（terminal authority = launcher 結果）

poll は Idle/Loaded/Running/Paused までしか出せず、**完走後 `core.py:850` の `force_stop`→`replay_state=IDLE` のため Done/Failed を
terminal として保持できない**。→ `(poll replay_state, launcher BacktestRunResult)` を融合する**小型 durable lifecycle 型**を 1 個作り、
Idle/Loaded/Running/Paused/Done/Failed を導出（AFK test 可能）。transport VM はそれを消費して**ボタン enablement のみ**決める（単一責任）。
削除された `ReplayRunLifecycle` を丸ごと復活させる必要はなく、融合ロジックだけの最小型でよい（sticky-terminal は launcher 結果＝
terminal authority で再定義）。

### (C3) transport RPC は `InprocLiveServer` forwarder で公開

`pause/resume/step/stop/set_replay_speed` を `InprocLiveServer` に薄い forwarder（`DataEngine` の `(bool, str|None)` tuple を
`start_engine` と同形の `{success, error_code, error_message}` dict にマップ）として durable 公開。C# は run も transport も **server 1
オブジェクト経由**で一貫（run lifecycle 制御=server / data prep=engine の関心分離）。#50 が空けた穴を意図通り埋める。

### durable 成果物 / throwaway / 委譲（選択肢: 既存パターン踏襲）

- **durable（C#）**: pure-logic transport VM（`MenuBarViewModel`/`ScenarioStartupController` 同型・AFK 駆動・GIL 非依存で intent emit）
  ＋ uGUI footer view builder（`ScenarioStartupTile` 同型・ThemeService テーマ）＋ 小型 lifecycle 型（C2）。
- **durable（Python）**: §3 の 3 欠落（step seam / dynamic speed / InprocLiveServer forwarder）。
- **throwaway glue**: 既存 `ScenarioStartupHitlHarness` に footer を足して HITL 実挙動を確認。transport 呼び出しはクリック時に GIL を
  短時間取得して server forwarder を叩く（O(1)・per-bar sleep で GIL 頻繁開放＝T1。main GIL-free 規律は durable VM 側で担保、glue は
  throwaway なので過剰配線しない）。run 起動は #29 "Run Replay" が単一 Play-owner。
- **委譲**: durable composition-root shell（PythonEngine lifecycle を 1 度だけ所有し chart/panels/footer を attachable view で差す・
  ADR-0001 d8 / findings 0003 §8 trajectory）への統合は **#5/#7/#39**。本スライスは spike precedent（S0→#10→#11→#29）に従う。

## 6. 検証（RED 先行・正本は findings＝backcast に FLOWS.md 無し）

- **Python pytest（RED→GREEN）**: ① step seam が PAUSED から**正確に 1 バー**進め再ブロックする / ② dynamic speed で interval が
  走行中に変わる（holder 更新が次バーに反映）/ ③ paused 中 stop が即 break / ④ step_event 衛生（LOADED で set しても fresh
  `start_engine`/`resume` 後に stale 消費しない）/ ⑤ `set_replay_speed` 実装（multiplier=0 拒否は維持）。budget-cap テスト
  （`test_replay_review_fixes.py:180-216`）は §4(D) の逆転に伴い更新/削除。
- **C# AFK probe**（headless 決定的・`ScenarioStartupProbe`/`MenuBarVerify` 同型）: lifecycle 6 状態の導出（poll+launcher 融合）、
  transport VM の button enablement（terminal で transport 無効化・▶ 文脈分岐・re-arm）、intent emit。
- **HITL harness**（owner 目視）: `ScenarioStartupHitlHarness` に footer を足し、play/pause/step/speed/stop でバーが
  前進/停止/1 バー前進/レート変化、terminal 後 ▶ で re-arm を実機確認。

### 検証実績（2026-06-15・Unity 6000.4.11f1 実機・Windows）

- **Python AFK**: 全 260 GREEN（新規 `test_replay_transport_seam.py`〔step seam / dynamic speed / stop-while-paused / golden byte-identical〕・
  `test_data_engine_transport.py`〔set_replay_speed 実装・kernel 経路 step PAUSED-only・step_event 衛生・1x reset・非 int 拒否〕・
  `test_inproc_transport_forwarders.py`〔forwarder の tuple→dict〕＋更新した `test_replay_review_fixes.py`〔budget-cap 撤廃の逆転〕）。
- **C# AFK**: `Assets/Editor/ReplayTransportVerify.cs` を `Unity -batchmode -executeMethod ReplayTransportVerify.Run` で **21/21 PASS**・
  `error CS` 0 件（lifecycle 6 状態 fusion / ▶ 文脈分岐 / step PAUSED-only / stop・speed enablement / terminal 無効化 / speed options）。
- **実機 HITL PASS（owner 目視, 2026-06-15）**: `ScenarioStartupHitlHarness` を Play、`8918.TSE` Daily を UI 設定。owner が footer で
  **▶/⏸ pause・resume**（バー停止↔再開）、**⏭ step**（1 クリック＝1 本）、**速度変更 1/2/5/10/50x**（再生レート反映）、
  **⏹ stop→再 run で re-arm** を確認。terminal は **`Done`**（クリーン完走）。AC①〜④ 全達成。
- **HITL で表面化した別件（#30 外・follow-up 起票）**: `8918.TSE` Daily に **2020-10-01（東証システム障害＝終日売買停止）** の
  OHLCV 全 0 行があり、その bar が `OhlcPoint(price>0)` 検証で落ち run が `RUN_FAILED` になる（市場休止日の 0-bar をクラッシュさせる
  reducer/loader の堅牢性バグ。#30 は bar 値・検証に触れないため pre-existing＝main でも 2020-10-01 を含む期間で再現）。HITL は
  当日を避けた範囲（Start 2021-01-01）で `Done` を確認。**修正は #30 外**（reducer が無取引日を skip / carry-forward する別スライス）。
