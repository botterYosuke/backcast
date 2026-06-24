# ChartOrphanSyncJourneyE2ERunner — 台本（Journey / 操作網羅台帳）

`ChartOrphanSyncJourneyE2ERunner.cs` が自動検証する issue **#123「Instruments 空でも `chart:<iid>`
フローティング窓が残る」** の release gate。実装者は `.cs` と本 `.md` をセットで読む。
下位事実: [findings 0100](../../../../docs/findings/0100-issue123-chart-orphan-on-empty-universe.md)（元 `0095-`・#124 レビューで採番衝突を解消しリネーム）。
採番・カバー語彙・責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)、配置は
[ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。

> **位置づけ**: *Journey E2E*（layout 復元 → scenario reseed → chart membership-sync を跨ぐ実ブートストーリー）。
> 実 `OnFileOpen` / `ResumeLastDocumentOrDefault` を反射駆動し、Python-FREE（`FakeMarimoSynthesizer`）で回す。

## 埋める死角

chart 窓は universe（Instruments SoT）と membership-sync される設計で、`SyncChartWindowsToUniverse` が
「chart 唯一の spawn/close 経路」のはず。だが `RestoreFloating`（`BackcastWorkspaceRoot.cs:2219`）が layout
sidecar の `floatingWindows` を **universe チェック無しで** id 順に spawn するため、**第二の spawn 経路**になる。
ブート順序の穴:

1. `BuildWorkspace` で `SyncChartWindowsToUniverse()` を1回（universe 空 → 何もしない）＋ `Universe.Changed` 購読。
2. `ApplyLayout → RestoreFloating` が保存済み `chart:<iid>` を universe チェック無しで spawn（factory 経由で
   `_chartViews[iid]` も populate＝`:746`）。
3. `ReseedFromEditor → SeedScenarioFromEditor → Universe.ReplaceAll`。**集合が変わらなければ `Changed` 不発火**
   （空→空の `SequenceEqual`／`Editable=false` の no-op）→ Changed 購読の Sync が走らず孤児が残る。

既存 `ChartPlacementJourneyE2ERunner` の P7（empty universe）は **「sidecar に chart 無し」** で 0 件を確認する
だけで、**「sidecar に `chart:X` あり＋空 universe → 孤児」** という #123 の真のバグ経路を通らない。本 runner が
その死角を RED→GREEN で塞ぐ。

## 最重要の不変条件（litmus）

- **修正は `ReseedFromEditor` の末尾で `SyncChartWindowsToUniverse()` を `Changed` 非依存に1回呼ぶこと。**
  `ReseedFromEditor` は ApplyLayout/Resume/File→Open/Save が共有する canonical tail（`:324-329` 契約）なので
  点 fix にならず全入口に効く。idempotent + geometry-preserving（universe に居る iid は `_chartViews` 在籍で no-op
  ＝復元ジオメトリ保持、孤児だけ despawn）。
- **delete-the-production-logic**: 追加した `SyncChartWindowsToUniverse();`（ReseedFromEditor 末尾）を消すと
  **CHART-ORPHAN-01 / 03 / 04 が RED**（no-Changed セルで孤児が残る）。CHART-ORPHAN-02 は universe が空→[X] で
  Changed が飛ぶ no-regression / geometry ガード。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| CHART-ORPHAN-01 | `chart:X` を含む layout ＋ 空 universe で File→Open | `BackcastWorkspaceRoot.OnFileOpen:1854`→`ApplyLayout`→`ReseedFromEditor:330` | universe 0 件・`_dockWindows.Has("chart:7203.TSE")=false`（孤児 despawn） | 実 OnFileOpen 駆動、universe.Count==0 ＋ chart 窓不在を assert | 自動(E2E済) | — |
| CHART-ORPHAN-02 | `chart:X` ＋ universe=[X] で File→Open（位置永続） | 同上 | chart:X 残存 ＋ 復元ジオメトリ（x/y）保持＝tail Sync が no-op | `RectOf("chart:7203.TSE")≠null` ＋ anchoredPosition==saved | 自動(E2E済) | `ChartPlacementJourneyE2ERunner`（CP-S1-01 saved-honor 同型） |
| CHART-ORPHAN-03 | 同条件を実 ResumeLastDocumentOrDefault（boot resume）で | `BackcastWorkspaceRoot.ResumeLastDocumentOrDefault:2068`→`ApplyLayout`→`ReseedFromEditor` | universe 0 件・孤児 chart 不在（resume 入口でも fix が効く） | PlayerPrefs resume pointer 設定→実メソッド反射駆動、孤児不在を assert | 自動(E2E済) | — |
| CHART-ORPHAN-04 | `Editable=false`（instruments_ref ロック）で ReplaceAll no-op・孤児＋残存の混在 | `InstrumentRegistry.ReplaceAll:85`（Editable gate）→`ReseedFromEditor` tail Sync | locked universe=[7203] 不変・孤児 `chart:6758` despawn・残存 `chart:7203` は geometry 保持 | locked 状態を注入→実 OnFileOpen、孤児不在＋残存 geometry を assert | 自動(E2E済・forward-defense) | — |

> **CHART-ORPHAN-04 注**: `Universe.Editable=false` を立てる **production 経路は現状コードに無い**（`instruments_ref`
> 外部参照ロックは `InstrumentRegistry.cs` のコメント上の概念で未配線・`grep .Editable =` は default 以外 0 件）。本セルは
> latent guard（`ReplaceAll:85`）への forward-defense characterization。precondition（locked registry）は注入、logic
> under test（ReseedFromEditor→tail Sync の実 File→Open 経路）は production。findings 0095 §matrix 参照。

## 入力マトリクス（`Changed` が飛ばない入力）の対応

| `Changed` 不発火の原因 | カバー Action ID |
|---|---|
| 空→空 `ReplaceAll`（`SequenceEqual` 短絡） | CHART-ORPHAN-01 / 03 |
| `Editable=false`（instruments_ref ロックで no-op） | CHART-ORPHAN-04 |
| universe が復元 chart の部分集合（孤児＋残存の混在） | CHART-ORPHAN-04（survivor 7203 ＋ orphan 6758） |
| universe=[X]（Changed 発火・no-regression / geometry） | CHART-ORPHAN-02 |

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath D:\Documents\backcast \
        -executeMethod ChartOrphanSyncJourneyE2ERunner.Run -logFile <abs log>
# expect: [E2E CHART ORPHAN SYNC JOURNEY PASS] + per-id [E2E CHART-ORPHAN-0N PASS] / exit=0
#         （確認は Bash `grep -a "E2E CHART-ORPHAN"`）
# compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
# ランチャ経由: pwsh scripts/run-live-e2e.ps1 -Method ChartOrphanSyncJourneyE2ERunner.Run
```
