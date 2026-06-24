# findings 0095 — Issue #123: layout 復元が universe-sync をバイパスし孤児 `chart:<iid>` 窓が残る

Instruments が空（サイドバー「No instruments」）なのに per-instrument の `chart:<iid>` フローティング窓が残る矛盾を、
backcast の AFK 回帰ゲートに落として塞いだ記録。`/grill-with-docs /behavior-to-e2e` 2026-06-24。

## 挙動（1 文の不変条件）

**chart 窓の集合は universe（Instruments SoT）と常に一致する** — `Universe.Changed` が飛ぶ通常経路だけでなく、
**layout 復元（`ApplyLayout → RestoreFloating`）を跨いでも**成立しなければならない。universe に居ない `chart:<iid>` は
復元後も despawn され、universe に居る `chart:<iid>` は復元ジオメトリ（x/y/w/h）を保ったまま残る。

## 根本原因

chart 窓の spawn/close は universe と membership-sync される設計で、`SyncChartWindowsToUniverse` が「chart 唯一の
spawn/close 経路」のはず。だが `RestoreFloating` が layout sidecar の `floatingWindows` を id で無条件 spawn するため
**第二の spawn 経路**になっている。実コードの順序（`BackcastWorkspaceRoot.cs`）:

1. `ApplyLayout(doc) → RestoreFloating` が保存済み `chart:<iid>` を **universe チェック無しで spawn**。frame factory
   `BuildDockWindowFrame → BuildChartContent` を通り `_chartViews[iid]` を populate（`:746`）。
2. その後の `ReseedFromEditor → SeedScenarioFromEditor → Universe.ReplaceAll` で universe を seed。だが `ReplaceAll` は
   **集合が変わらなければ `Changed` を発火しない**:
   - 空→空（`_ids.SequenceEqual(next)` で false）— `InstrumentRegistry.cs:98`
   - `!Editable`（`instruments_ref` ロック）で即 false — `InstrumentRegistry.cs:85`
3. 結果、`Universe.Changed += SyncChartWindowsToUniverse`（`:394`）が走らず、universe に居ない孤児 `chart:<iid>` 窓が残る。

base dock は5枚（startup/buying_power/orders/positions/run_result）で `chart` を含まないため、画面に出る Chart は必ず
per-instrument 窓＝孤児。`scenario` sidecar（universe）と `layout` sidecar（floatingWindows）は別経路・別タイミングで
書かれ原子性が無いため、過去に instrument があった状態の chart ジオメトリが、universe が空/未設定に戻った後も layout 側に
生き残ると desync する。

## タイミング（owner HITL で確定）

`BuildWorkspace()` も `ResumeLastDocumentOrDefault()` も両方 **同じ `Awake()` 内（`:247`）で同期実行**。孤児の検知＝despawn は
`ReseedFromEditor()` 末尾、seed が完了した直後の **同一 `Awake()` 同期コールスタック内**で、最初の `Update()`／描画フレームより
**前**に起きる。よって孤児は「一瞬出て消える」フラッシュすら見えない。E2E も「`Awake` 相当の同期復元の直後に chart 窓ゼロを
assert する（フレーム待ち不要）」形で書ける。

## 直し方（採用）と不採用案

**採用**: `ReseedFromEditor()` 末尾で `Changed` 非依存に `SyncChartWindowsToUniverse()` を1回呼ぶ。

```csharp
void ReseedFromEditor()
{
    SeedScenarioFromEditor();
    _tile?.SyncFieldsFromController();
    _sidebarCtrl?.PrimeWritebackFromCurrent();
    SyncChartWindowsToUniverse();   // #123: Changed 非依存で孤児 despawn / 欠落 spawn
}
```

`ReseedFromEditor` は **復元→reseed の唯一の共有 tail** で、全 sibling 入口が通る（点 fix にしない・AC「File→Open 系すべてに
効く」を構造で保証）:

| 呼び出し元 | restore? | 末尾 sync の効果 |
|---|---|---|
| `DoFileOpen:1903`（File→Open） | ✅ `ApplyLayout` | 孤児 despawn（本命） |
| `ResumeLastDocumentOrDefault:2083`（boot resume） | ✅ `ApplyLayout` | 孤児 despawn（boot 本命） |
| `OpenFileNewDefault:2104`（File→New 既定） | ✅ `ApplyLayout(Default)` | Default に chart 無 → 整合維持 |
| `OnFileSave:1930` | ❌ | universe 不変・窓一致 → 冪等 no-op |
| `OnFileSaveAs:1960` | ❌ | 同上 no-op |

順序制約（**seed の後**でなければならない）: sync が孤児判定するには ① 復元 chart が `_chartViews` に載り（`ApplyLayout` 後）
② universe が seed 済み（`ReseedFromEditor → ReplaceAll` 後）の両方が要る。seed 前（例 `ApplyLayout` 直後）に置くと、復元した
ばかりの `chart:X` を「universe に居ない」と誤判定→despawn→`Changed` で default grid 位置に再 spawn し **復元ジオメトリを破壊**
する。これは下記 CHARTSYNC-02/04 が RED で検出する。`DoFileNew` だけは `ReseedFromEditor` を通らず `_scenario.Clear()` 直叩き
だが、New は floating 窓を一切復元しない＝孤児の発生源にならないので穴は無い（孤児の SOURCE は全て restore→reseed 経路）。

**不採用**: `RestoreFloating` で chart-family を spawn しない案。ユーザが動かした chart 位置の永続が失われる（復元でジオメトリを
置き、sync は孤児 despawn / 欠落 spawn のみ行うので位置は保たれる）。

## 著したゲート（RED→GREEN）

**`Assets/Tests/E2E/Editor/ChartUniverseSyncE2ERunner.{cs,md}`**（Issue release-gate slice・Python-FREE・AFK）。実
`BackcastWorkspaceRoot` を反射合成し、実 restore→reseed 入口（boot resume＝`ResumeLastDocumentOrDefault`、File→Open＝
`OnFileOpen → DoFileOpen`）を駆動。layout sidecar は `LayoutSidecarStore.WriteLayout` で `chart:<iid>` を明示配置し、
scenario sidecar は universe を seed する。

| Action ID | cell | 入口 | assert |
|---|---|---|---|
| CHARTSYNC-01 | 空→空 `SequenceEqual`（no Changed） | File→Open | 孤児 `chart:7203` despawn・universe 空 |
| CHARTSYNC-02 | 残存＋ジオメトリ保持 | boot resume | `chart:7203` survive・`CaptureLayout` 矩形=書込値(812,-456,520,360) |
| CHARTSYNC-03 | `Editable=false`（`instruments_ref` ロック・`ReplaceAll` no-op） | File→Open | 孤児 despawn・universe 空のまま |
| CHARTSYNC-04 | subset（孤児＋残存の混在） | File→Open | `chart:9984` despawn・`chart:7203` survive＋ジオメトリ保持 |

**入力マトリクス（bug-class-sweep）**: `Changed` が飛ばない2入力（空→空 `SequenceEqual` ＝CHARTSYNC-01／`Editable=false` ロック
＝CHARTSYNC-03）と subset（CHARTSYNC-04）の全セルを掃いた。注: production は現状 `instruments_ref → Editable=false` を配線して
いない（grep で production セッターなし）ので CHARTSYNC-03 は `Universe.Editable=false` を直接立てて simulate（既存
`UniverseSidebarE2ERunner`/`ScenarioStartupE2ERunner` の locked-registry simulation と同型）。

**RED→GREEN litmus**:
- `ReseedFromEditor()` 末尾の `SyncChartWindowsToUniverse();` を削除 → **CHARTSYNC-01 / 03 RED**（孤児 chart 残存）。
- 同 sync を seed の **前**（`ApplyLayout` 直後）へ移す → **CHARTSYNC-02 / 04 RED**（復元 chart を default grid へ再 spawn し
  ジオメトリ破壊）。

非 vacuity: 復元 chart は `RestoreFloating` で実際に spawn される（`_chartViews` に載る）ので「最初から spawn しない」no-op が
pass に化けない。CHARTSYNC-02/04 はジオメトリ一致まで assert するので despawn+respawn を「残った」と誤認しない。

## 実走（AFK）

```
& $UNITY_EDITOR_PATH -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast `
   -executeMethod ChartUniverseSyncE2ERunner.Run -logFile <abs log>
# expect: [E2E CHART UNIVERSE SYNC PASS] + [E2E CHARTSYNC-01..04 PASS] / verdict はタグ
# ランチャ: scripts/run-live-e2e.ps1 -Method ChartUniverseSyncE2ERunner.Run
```

CHARTSYNC-05（実ブートで sidebar「No instruments」と Chart 窓ゼロを目視整合）は実ウィンドウ・実ピクセルが要るため
HITL専用。**2026-06-24 owner HITL で PASS 実走確認済み**: `Tools ▸ Backcast ▸ Issue123 ChartSync - Generate + arm
boot-resume fixture`（`Assets/Editor/ChartUniverseSyncHitlFixture.cs`）が layout=`chart:7203`／universe=空の mismatch
文書を実 store で書き出し resume ポインタに arm → Play 起動で sidebar「No instruments」＋ Chart 窓ゼロを目視確認。

## 関連

- 修正: `Assets/Scripts/Live/BackcastWorkspaceRoot.cs`（`ReseedFromEditor` 末尾の1行）。
- chart spawn/sync 本体: `SyncChartWindowsToUniverse`（`:963`）/ `RestoreFloating`（`:2219`）/ `BuildChartContent`（`:722`）。
- `Changed` 不発火の正本: `InstrumentRegistry.cs`（`ReplaceAll` `:83`、`Editable` ゲート `:85`、`SequenceEqual` `:98`）。
- 設計上の親類: findings 0091（#114 chart grid placement / cascade kill）・findings 0094（AddChartLadder Journey）。
