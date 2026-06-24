# 0095 — issue #123: Instruments 空でも `chart:<iid>` フローティング窓が残る（layout 復元が universe-sync をバイパス）

- **状態**: 実装＋AFK ゲート GREEN（owner 実機 HITL は任意 — 純 C# / Python-FREE の決定論ゲートで挙動が閉じる）
- **gate**: `Assets/Tests/E2E/Editor/ChartOrphanSyncJourneyE2ERunner.{cs,md}`（CHART-ORPHAN-01..04）
- **fix**: `Assets/Scripts/Live/BackcastWorkspaceRoot.cs` `ReseedFromEditor()` 末尾

## 症状

Instruments が空（サイドバー「No instruments」）なのに、per-instrument の `chart:<iid>` フローティング窓が
spawn したまま残る。base dock は5枚（startup/buying_power/orders/positions/run_result）で `chart` を含まないため、
画面に出る Chart は必ず per-instrument 窓＝孤児。

## 根本原因（コードで裏取り）

chart 窓は universe（Instruments SoT）と membership-sync される設計で、`SyncChartWindowsToUniverse`
（`:963`）が「chart 唯一の spawn/close 経路」のはず。だが `RestoreFloating`（`:2219`）が layout sidecar の
`floatingWindows` を **universe チェック無しで** id 順に `ctrl.Spawn(KIND_CHART, "chart:<iid>", …)` する
（factory `BuildChartContent:746` 経由で `_chartViews[iid]` も populate される）。これが **第二の spawn 経路**。

ブート順序の穴:

1. `BuildWorkspace`（`:393-394`）で `SyncChartWindowsToUniverse()` を1回（この時点 universe 空 → 何もしない）。
   以後の照合は `Universe.Changed` 購読頼み。
2. `ApplyLayout → RestoreFloating`（`DoFileOpen:1900` / `ResumeLastDocumentOrDefault:2077`）が保存済み
   `chart:<iid>` を universe チェック無しで spawn。
3. `ReseedFromEditor`（`:330`）→ `SeedScenarioFromEditor` → `Universe.ReplaceAll`。だが `ReplaceAll`
   （`InstrumentRegistry.cs:83`）は **集合が変わらなければ `Changed` を発火しない**:
   - 空→空（`SequenceEqual([],[])==true`、`:98`）
   - `Editable=false`（instruments_ref ロックで早期 `return false`、`:85`）

結果、復元された孤児 `chart:<iid>` 窓を誰も despawn せず残る（`Changed` 購読の Sync が走らない）。

## 修正（採用案）

`ReseedFromEditor()` の末尾で `SyncChartWindowsToUniverse()` を **`Changed` 非依存に1回**呼ぶ。

- `ReseedFromEditor` は **ApplyLayout の post-RestoreEditors seed / File→Open(`DoFileOpen`) / File→Save /
  Save As / boot resume(`ResumeLastDocumentOrDefault`) が共有する canonical tail**（`:324-329` のコメント契約）
  なので、ここに置けば **点 fix にならず全 File→Open/Resume 入口に効く**（issue「点 fix にしない」を構造的に充足）。
- **idempotent + geometry-preserving**: RestoreFloating の `Spawn(KIND_CHART)` が factory 経由で `_chartViews`
  を既に populate しているため（`:746`）、universe に居る iid は `SyncChartWindowsToUniverse` の `ContainsKey`
  分岐で **no-op（respawn しない）＝復元ジオメトリ x/y/w/h を保持**。孤児だけ `DespawnChartWindow` で閉じる。
- Save 系（ApplyLayout を通らない）でも無害（孤児が無ければ no-op）。

## 採否した代替案

- **RestoreFloating で chart-family を spawn しない**（universe にある時だけ復元）→ **不採用**。ユーザが動かした
  chart 位置の永続が失われる（復元でジオメトリを置き、sync は孤児 despawn / 欠落 spawn のみ行うので位置が保たれる、が
  正しい所有権分離）。
- **ReplaceAll を常に Changed 発火に変える**（SequenceEqual 短絡を撤去）→ **不採用**。writeback churn 回避という
  ReplaceAll の設計意図（`:30-34`）を壊し、startup tile / sidebar writeback の no-op 不変条件に波及する。孤児問題は
  reseed tail の1点で閉じるべきで、SoT mutator の契約を変えるのは過剰。

## 入力マトリクス（`Changed` が飛ばない入力）と E2E カバレッジ

| `Changed` 不発火の原因 | Action ID | litmus 感度 |
|---|---|---|
| 空→空 `ReplaceAll`（File→Open） | CHART-ORPHAN-01 | ◎ tail Sync を消すと RED |
| universe=[X]（Changed 発火・no-regression / geometry 保持） | CHART-ORPHAN-02 | ガード（飛ぶので両方 GREEN） |
| 空→空 `ReplaceAll`（boot resume 入口） | CHART-ORPHAN-03 | ◎ tail Sync を消すと RED |
| `Editable=false` no-op ＋ 孤児＋残存の混在 | CHART-ORPHAN-04 | ◎ tail Sync を消すと RED |

- 各 section は **実 `OnFileOpen` / `ResumeLastDocumentOrDefault` を反射駆動**（ユニット直叩きでなく end-to-end）。
- vacuity-kill: 復元ジオメトリは grid anchor(-600,-332) から離れた distinct 座標を使い、stray respawn なら検出。

### §matrix — CHART-ORPHAN-04（Editable=false）は forward-defense characterization

`Universe.Editable=false` を立てる **production 経路は現状コードに存在しない**（`grep '.Editable ='` は default
`= true` 以外 0 件。`instruments_ref` 外部参照ロックは `InstrumentRegistry.cs:10` のコメント上の概念で未配線）。
よって本セルは latent guard（`ReplaceAll:85` の `if(!Editable) return false`）への **forward-defense**：precondition
（locked registry）は reflection で注入し、logic under test（`ReseedFromEditor`→tail Sync の実 File→Open 経路）は
production。将来 instruments_ref ロックが配線されても孤児が出ないことを先に固定する。

## RED→GREEN

- **RED**（fix 前 / `SyncChartWindowsToUniverse();` を ReseedFromEditor 末尾から削除）: CHART-ORPHAN-01 / 03 / 04 が
  FAIL（no-Changed セルで孤児 chart が live のまま）。
- **GREEN**（fix 後）: 4 section とも PASS、`[E2E CHART-ORPHAN-01..04 PASS]` ＋ `[E2E CHART ORPHAN SYNC JOURNEY PASS]`。

## 再走手順

```
pwsh scripts/run-live-e2e.ps1 -Method ChartOrphanSyncJourneyE2ERunner.Run
# or: <Unity> -batchmode -nographics -quit -projectPath D:\Documents\backcast \
#             -executeMethod ChartOrphanSyncJourneyE2ERunner.Run -logFile <abs>
# 確認: Bash `grep -a "E2E CHART-ORPHAN"`（per-id 4 PASS）/ exit=0 / error CS 0 件
```

## リグレッション非破壊

- 既存 `ChartPlacementJourneyE2ERunner`（CP-S1 saved-honor / CP-S2 cascade-kill / CP-S4 empty）は不変。tail Sync は
  「孤児 despawn・欠落 spawn・在籍 no-op」のみで grid 配置ロジックに触れない。
- `AddChartLadderJourneyE2ERunner`（Live +Add → ladder 可視 spawn）も不変（universe 成長は Changed 経路のまま）。
