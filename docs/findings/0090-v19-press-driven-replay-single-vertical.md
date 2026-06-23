# findings 0090 — v19 Replay の「実ボタン press 単一縦串」ゲート（2 ゲート分割の継ぎ目を 1 本で塞ぐ）

> 出荷前確認（owner HITL・2026-06-23）の続き。owner 要求「**バイパスしたテストではなく、ユーザーが操作するのと
> 同じ状況を再現するテスト**」＝「既存データで owner 手動 HITL と同じテストを実行」。`behavior-to-e2e` の縦串として記録。
> 方針参照: ADR-0016（notebook=backtest per-cell RUN）/ findings [0089](./0089-v19-marimo-cell-file-anchor.md) / [0079](./0079-notebook-console-and-thread-context.md)（#102 継ぎ目）。

## 動機 — 既存 2 ゲートの「継ぎ目」

#95 Phase 4 の behavior-to-e2e は per-cell RUN を **2 ゲートに分割**して gate していた:

| ゲート | カバー | **バイパスしているもの** |
|---|---|---|
| `V19ReplayLiveE2ERunner`（findings 0089） | 実 Python・実 NAS・実約定（DATA 縦串） | C# の press→controller→lane 配線（`InvokeRunCell` を **直叩き**） |
| `NotebookToHakoniwaJourneyE2ERunner` | 実 root の press→controller→lane 配線（C# 縫い目） | 実 Python（**fake executor**・`startWorker:false` 同期レーン） |

両ゲートとも GREEN でも、**どちらも跨がない seam** が残る（findings 0079/#102 が一般論として警告した「両ゲート緑でも
実機だけ落ちる継ぎ目」）。v19 で具体的に継ぎ目に当たるのは 3 つ:

1. **decompose→synthesize の往復忠実度**: owner が `v19_morning_cell.py` を *開く*と、実 marimo（`PythonnetMarimoSynthesizer`
   → `decompose_json`）が cell へ分解し、press 時に `SynthesizeLiveSource`（`generate_filecontents`）が source を *組み直す*。
   `V19ReplayLiveE2ERunner` は **raw ファイルを press** するのでこの往復を一切通らない＝往復で source が壊れても気づけない。
2. **実 worker thread + 実 Python の同一スレッド契約**（findings 0079/#102）: `InvokeRunCell` は「marimo の `RuntimeContext`
   は thread-local なので session の build+run を 1 スレッドで」と契約。`NotebookRunLane(startWorker:true)` の worker がこれを
   保証するが、Journey は `startWorker:false` の同期 fake レーンなので **実 worker thread 上の実 Python を一度も踏まない**。
3. **`__file__` strategyPath leg の非空虚な実解決**（findings 0089）: Journey は unbound editor harness なので実 provider が
   null を返し **sentinel 文字列**で leg を gate していた（「配線は通る」止まり）。実ファイルを開いて `BuildNotebookStrategyPath`
   が**実 v19 path を返す**ところは通っていない。

owner の「バイパスではなく手動 HITL と同じ」要求は、この継ぎ目を **実ボタン press から実約定まで 1 本**で通すことを求めている。

## このゲート — `V19ReplayPressE2ERunner`

`v19_morning_cell.py` を **owner が手で開いて ▶ を押す**のと同じ経路を、実 NAS データ上で end-to-end に自動駆動する:

```
OpenScene(BackcastWorkspace) → root._host.InitializePython("MOCK")（実 server / 実 marimo / 実 executor が ready）
  → ResolvePaths + BuildWorkspace（production が PythonnetMarimoSynthesizer・HostNotebookCellExecutor・
     NotebookRunLane(startWorker:true)・NotebookRunController を配線。fake へ差し替えない）
  → _coordinator.Open(v19_morning_cell.py)（実 decompose_json で N cell へ分解）
  → ReseedFromEditor（SeedScenarioFromEditor が v19 sidecar の scenario を _scenario へ populate）
  → bt.replay セルの実 RUN ボタン .onClick.Invoke()（production gate `_isOwner && _host.ServerReady` 通過 → RunCell）
  → RunCell が SynthesizeLiveSource で実 source を組み直し、worker lane へ Submit
  → worker thread: HostNotebookCellExecutor → InvokeRunCell(source, idx, scenarioJson, strategyPath=実 v19 path)
     → 実 marimo cold-run が __file__ を anchor → _artifacts が cell 隣接 artifacts を自己ロード
     → bt.replay() が実 NAS minute bars を stream → top-k 10:00 entry / 14:55 flatten が実約定
  → main は DrainAndRoute を pump して worker の完了を待つ → run_summary が engine.last_run_summary へ
  → host.RunSummaryJson（LiveRpcLanes の get_run_summary_json poll cache）で fills_count > 0 を assert
```

これは V19Replay と NotebookToHakoniwa の **両半分を実 root 上で縫い合わせた**唯一の単一縦串で、上記 3 つの継ぎ目を
すべて実機で踏む。

## 自動検証する範囲

| Action ID | 行動 | 観測点 | 合否の意味 |
|---|---|---|---|
| `V19PRESS-01` | 実 marimo で v19 を開く（decompose） | `coordinator.Open(cellPy)` が true・cell 数 ≥ 1 | 往復の入口（分解）が通った |
| `V19PRESS-02` | 開いた doc から scenario panel が seed | `_scenario.Universe.Count > 0`（v19 sidecar の universe） | sidecar→scenario panel の手渡しが通った |
| `V19PRESS-03` | strategyPath leg が実 v19 path を返す | `BuildNotebookStrategyPath() == cellPy`（非 sentinel・非 null） | findings 0089 の `__file__` leg が実ファイルで非空虚に解決 |
| `V19PRESS-04` | bt.replay セルの実ボタン press → RUNNING | press 後 `IsBacktestRunning==true`・glyph ▶→■ | 実 onClick gate→controller→guard が通った |
| `V19PRESS-05` | worker lane の実 Python が実 source を実バーで駆動 | timeout 内に run 完了（`IsBacktestRunning==false` へ）・hung でない | 実 worker thread 上の実 marimo session（build+run 1 スレッド契約）が通った |
| `V19PRESS-06` | 実約定（top-k 10:00 / 14:55） | `host.RunSummaryJson` の `fills_count`（or `trade_count`）`> 0` | 往復した source が artifacts を解決し実エンジンを駆動して実約定した |

## 自動検証しない範囲（carve-out）

- **実 GPU での base tile / chart のピクセル・色・レイアウト** → owner HITL（`-nographics` は GPU 無し）。
- **エンジン faithfulness の数値 parity**（byte-identical fills 等） → `test_v19_replay_core.py` / `test_v19_marimo_parity.py`。
- **走行中スナップショットの逐次 tile 更新の C# 縫い目** → `NotebookToHakoniwaJourneyE2ERunner`（本縦串は最終 run_summary の fills を gate）。

## RED→GREEN ゲート（delete-the-production-logic litmus）

本縦串は GREEN が「実機で owner 操作が通る」十分条件。production を壊すと落ちる（vacuity 無し）:

- **findings 0089 の strategyPath inject**（`DataEngineBackend.run_cell` の `__file__` 注入）を外す → V19PRESS-03 は実 path を
  返すが、worker の実 cold-run で `_artifacts` が `Path(__file__).parent/artifacts` を解決できず FileNotFound → 約定ゼロ →
  V19PRESS-06 が RED（`no run_summary` / `fills=0`）。
- **decompose→synthesize 往復**が壊れて bt.replay セルが消える/壊れる → synthesized source が bt を駆動せず約定ゼロ → V19PRESS-05/06 RED。
- **worker lane の thread 契約**（`startWorker:true` の build+run 同一スレッド）が崩れる（main で build / worker で run 等）→
  `RuntimeContext`（`ContextNotInitializedError`）→ run_cell が ok=false → V19PRESS-06 RED。
- **production onClick gate**（`_isOwner && _host.ServerReady`）や RunCell の running guard を壊す → V19PRESS-04 が RED。

## 実行（罠: 実 NAS / sandbox / exit 139）

```
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod V19ReplayPressE2ERunner.Run -logFile <abs>
# expect: [E2E V19-PRESS PASS] ... fills_count=N / verdict はタグで判定
# ⚠️ 実 J-Quants minute mount（/Volumes/StockData/jp）を継承するため Bash サンドボックス無効で起動（memory bash-sandbox-masks-volumes-nas）
# mount 不在: [E2E V19-PRESS SKIP]（PASS 扱い・偽 GREEN を作らない）
# InitializePython("MOCK") 系は shutdown segfault で exit=139 だが PASS/FAIL タグで判定（#107 と同型）
# 確認は Bash `grep -a "E2E V19-PRESS"`（→/■ を含むため ripgrep/Select-String は取りこぼす）
```

カバー状態: `V19PRESS-01..06` = `自動(E2E済)`（実 mount 時）。mount 不在 CI では SKIP。
