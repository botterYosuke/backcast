# UniverseBridgeE2ERunner — 台本（ADR-0031 S1 / #141）

方針: [ADR-0031](../../../../docs/adr/0031-cell-driven-dynamic-universe-bt-universe-api.md) ＋
[findings 0115 §S1](../../../../docs/findings/0115-issue141-145-cell-driven-dynamic-universe.md)。

## 何を gate するか

戦略 cell の `bt.universe.add/remove/clear/list` が C# `InstrumentRegistry` SoT を「プログラムによるユーザー編集」として
動かす背骨（ADR-0031 D1/D2）。Python 半（enqueue/read-back/validation）は pytest `test_bt_universe_bridge.py`（BTUNIV-01..08）、
C#/Unity 半（drain→registry→chart 反映・read-back JSON 契約）は本 runner（BTUNIV-09..12）が gate する＝2 ゲート分割。

本 runner は **Python-FREE**：engine の `drain_universe_edits()` が出すのと同じ JSON 形
（`[{"op":"add","id":"7203.TSE"}]`）を `UniverseBridge` に食わせ、実 `BackcastWorkspaceRoot` 合成の **実 registry ＋ 実 chart
cascade**（`InstrumentRegistry.Changed → SyncChartWindowsToUniverse`）を駆動する。edit の *source*（実 Python cell run）だけが fake。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath <abs> -executeMethod UniverseBridgeE2ERunner.Run -logFile <abs>
# expect: [E2E UNIVERSE BRIDGE PASS] / exit=0（確認は Bash `grep -a "UNIVERSE BRIDGE"`）
pwsh scripts/run-live-e2e.ps1 -Method UniverseBridgeE2ERunner.Run
```

## 操作一覧表

| Action ID | 行動 | 入口 | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|---|
| `BTUNIV-09` | `bt.universe.add("7203.TSE")`（drain JSON）を適用 | `UniverseBridge.ApplyJson` → `_scenario.Universe` | registry に X＋`chart:X` 窓 spawn（`_chartViews`／back-plane） | `changed==1` ∧ `Ids.Contains(X)` ∧ chart 窓存在 | 自動(E2E済) |
| `BTUNIV-10` | add 2 件→`remove("7203.TSE")` | 同上 | 当該 chart despawn・survivor 残 | removed の chart 不在 ∧ survivor 存在 | 自動(E2E済) |
| `BTUNIV-11` | add 2 件→`clear()` | 同上（clear→`ReplaceAll(empty)`） | 全 chart despawn・universe 空 | `Ids.Count==0` ∧ chart 全消滅 | 自動(E2E済) |
| `BTUNIV-12` | read-back seam JSON 契約 | `UniverseBridge.ParseEdits` / `BackcastWorkspaceRoot.UniverseIdsToJson`（reflect） | engine edit JSON→ops／registry Ids→`push_universe_ids` 配列 | 双方向の string 契約一致 | 自動(E2E済) |
| `BTUNIV-16` | read-channel dirty-latch（§Review2 / §Review F2/F4/F5）：registry edit→dirty、**not-ready push は dirty を維持**（seed を落とさない） | `DriveUniverseBridge`（reflect・server 未 ready） | `_universeMirrorDirty` の遷移／`PushUniverseIds`＝false・`DrainUniverseEdits`＝`""` | edit 後 dirty=true ∧ tick 後も dirty=true | 自動(E2E済) |
| `BTUNIV-01..08` | Python 半（enqueue/round-trip/D2 no-own-SoT/validation/golden parity） | pytest `test_bt_universe_bridge.py` | — | `@pytest.mark.scenario` | 自動(E2E済・pytest) |
| `BTUNIV-14,15` | LiveAuto bridge 配線（14=`LiveCellBridge` ctor／15=loader→factory→bridge の真の bug 部位） | pytest `test_bt_universe_bridge.py` | — | `@pytest.mark.scenario` | 自動(E2E済・pytest) |
| `BTUNIV-13` | 実機目視：cell で `bt.universe.add` → Hakoniwa に chart 窓が出る | owner 手元 | 実描画 | — | HITL専用（実 Python run・実 chart 描画） |

## RED→GREEN litmus（findings 0115 §S1 / §Review2）

- `UniverseBridge.Apply` の `add`/`remove`/`clear` case を消す → BTUNIV-09/10/11 が RED。
- `UniverseIdsToJson` / `ParseEdits` を壊す → BTUNIV-12 RED。
- `DriveUniverseBridge` が push 結果に依らず `_universeMirrorDirty` を clear する（または `PushUniverseIds` が
  `!_serverReady` で true を返す）→ not-ready 窓の seed が失われ BTUNIV-16 RED（§Review F2/F4/F5 の回帰）。
