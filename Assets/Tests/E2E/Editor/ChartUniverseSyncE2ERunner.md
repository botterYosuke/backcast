# ChartUniverseSyncE2ERunner — 台本（Issue #123 release-gate slice / 操作網羅台帳）

`ChartUniverseSyncE2ERunner.cs` が自動検証する issue #123 の release gate。実装者は `.cs` と本 `.md` をセットで読む。
下位事実: [findings 0095](../../../../docs/findings/0095-issue123-chart-window-universe-sync-on-restore.md)。採番・カバー語彙・
責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)、配置は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。

> **位置づけ**: *Issue release-gate slice runner*（特定 issue #123 の不変条件「chart windows == universe (SoT)」が
> *layout 復元を跨いで* 成立することを細く正本化する）。Surface/Journey 台帳とは別の表（E2E-INDEX「Issue release-gate
> slice runners」）に置く。

## 直す不具合

Instruments が空（サイドバー「No instruments」）なのに per-instrument の `chart:<iid>` フローティング窓が残る矛盾。

**根本原因**: chart 窓の spawn/close は universe（Instruments SoT）と membership-sync される設計で、`SyncChartWindowsToUniverse`
が「chart 唯一の spawn/close 経路」のはずだが、`RestoreFloating` が layout sidecar の `floatingWindows` を id で無条件
spawn するため **第二の spawn 経路**になっている。ブート/Open 順序は次の穴を持つ:

1. `ApplyLayout → RestoreFloating` が保存済み `chart:<iid>` を **universe チェック無しで spawn**（`_chartViews[iid]` を populate）。
2. その後の `ReseedFromEditor → SeedScenarioFromEditor → Universe.ReplaceAll` で universe を seed。だが `ReplaceAll` は
   **集合が変わらなければ `Changed` を発火しない**（空→空の `SequenceEqual`／`Editable=false`（`instruments_ref` ロック）で no-op、
   `InstrumentRegistry.cs:85`/`:98`）。
3. 結果、`Universe.Changed` 購読の `SyncChartWindowsToUniverse` が走らず、universe に居ない孤児 `chart:<iid>` 窓が残る。

base dock は3枚（buying_power/orders/positions）で `chart` を含まないため（startup は ADR-0026・run_result は
ADR-0037 で退役）、画面に出る Chart は必ず per-instrument 窓＝孤児。

## 直し方（採用）と不採用案

- **採用**: `ReseedFromEditor()` 末尾で `Changed` 非依存に `SyncChartWindowsToUniverse()` を1回呼ぶ
  （`BackcastWorkspaceRoot.cs`）。`ReseedFromEditor` は **復元→reseed の唯一の共有 tail**（Resume / File→Open / Save / New が
  すべて通る）なので、点 fix にせず全 sibling 入口を1点で塞ぐ。seed の **後**に呼ぶのが必須——seed 前（例 `ApplyLayout` 直後）に
  置くと、復元したばかりの chart を「universe に居ない」と誤判定して despawn→default grid 位置で再 spawn し、復元ジオメトリを
  破壊する（＝下記 CHARTSYNC-02/04 が RED）。
- **不採用**: `RestoreFloating` で chart-family を spawn しない案。ユーザが動かした chart 位置の永続が失われる（復元でジオメトリを
  置き、sync は孤児 despawn / 欠落 spawn のみ行うので位置は保たれる）。

## 最重要の不変条件（litmus）

- **テスト自身は `InitializePython` を呼ばない（Python-FREE）。** layout 復元＋reseed に kernel は不要（接続なしの File-op
  モード副作用は no-op、notebook synthesiser は `FakeMarimoSynthesizer`）。
- **孤児 despawn は `Changed` の到着でなく、各 restore→reseed が seed を終えた決定論的な末尾点で起きる**。`BuildWorkspace` も
  `ResumeLastDocumentOrDefault` も同じ `Awake()` の同期コールスタック内で走るので、孤児は1フレームも描画されない。
- **実 production 入口を駆動**: CHARTSYNC-02 は `ResumeLastDocumentOrDefault`（boot resume・PlayerPrefs ポインタ）、
  CHARTSYNC-01/03/04 は `OnFileOpen → DoFileOpen`（File→Open）。ユニット直叩きでなく end-to-end。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| CHARTSYNC-01 | layout に `chart:7203` を持つ doc を File→Open（scenario universe=空＝空→空 `SequenceEqual` で `Changed` 不発火） | `OnFileOpen`→`DoFileOpen:1900`→`ApplyLayout`→`ReseedFromEditor:330` | reseed 後 `_chartViews` に 7203 無し／`_dockWindows.RectOf("chart:7203.TSE")=null`／universe 空 | 反射で孤児 despawn＋universe 空を assert | 自動(E2E済) | — |
| CHARTSYNC-02 | layout に `chart:7203`、universe=[7203] の doc を boot resume → 窓は残り復元ジオメトリ保持 | `ResumeLastDocumentOrDefault:2068`→`ApplyLayout`→`_coordinator.Open`→`ReseedFromEditor` | `_chartViews` に 7203 有り／`CaptureLayout` の `chart:7203` 矩形が書込値(812,-456,520,360)と一致 | 反射で survive＋`CaptureLayout` 矩形一致を assert | 自動(E2E済) | — |
| CHARTSYNC-03 | layout に `chart:7203`、registry ロック（`Editable=false`=`instruments_ref`）で File→Open → `ReplaceAll` no-op | `OnFileOpen`→`DoFileOpen:1900`→`ReseedFromEditor`（`ReplaceAll` が `:85` で false） | universe 空のまま／`_chartViews` に 7203 無し／窓 null | 反射で孤児 despawn を assert（ロック cell） | 自動(E2E済) | — |
| CHARTSYNC-04 | layout に `chart:7203`＋`chart:9984`、universe=[7203] を File→Open → 9984 despawn・7203 survive＋ジオメトリ保持 | `OnFileOpen`→`DoFileOpen:1900`→`ReseedFromEditor` | `_chartViews` に 9984 無し／7203 有り／`CaptureLayout` の `chart:7203` 矩形が書込値一致（despawn-orphan-only） | 反射で混在（孤児＋残存）を assert | 自動(E2E済) | — |
| CHARTSYNC-05 | 実ブートで sidebar「No instruments」と画面に Chart 窓ゼロを目視整合 | 実 `Awake()`→`ResumeLastDocumentOrDefault` | 実 GUI で chart 窓が出ない／sidebar 空 | 実ウィンドウ・実ピクセル | HITL専用（実ブート・目視） | — |

## litmus（delete-the-production-logic）

- `ReseedFromEditor()` 末尾の `SyncChartWindowsToUniverse();` を削除 → **CHARTSYNC-01 / CHARTSYNC-03 RED**（孤児 chart が残る）。
- 同 sync を seed の **前**（例 `ApplyLayout` 直後）へ移す → **CHARTSYNC-02 / CHARTSYNC-04 RED**（復元 chart を default grid 位置へ
  再 spawn しジオメトリ破壊）。
- `RestoreFloating` の chart 復元（`ctrl.Spawn(KIND_CHART,…)`）を残しつつ universe seed を消す（=矛盾を作る）と CHARTSYNC-02 の
  universe seed assert が RED ＝harness の非 vacuity 確認。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
        -executeMethod ChartUniverseSyncE2ERunner.Run -logFile <abs log>
# expect: [E2E CHART UNIVERSE SYNC PASS] / exit=0  （確認は Bash `grep -a "CHART UNIVERSE SYNC"`）
# per-Action-ID タグ: [E2E CHARTSYNC-01 PASS] … [E2E CHARTSYNC-04 PASS]（rollup は単一トークン id で集計）
# compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
# ランチャ経由: pwsh scripts/run-live-e2e.ps1 -Method ChartUniverseSyncE2ERunner.Run
```
