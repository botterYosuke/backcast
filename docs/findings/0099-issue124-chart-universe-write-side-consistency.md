# findings 0099 — Issue #124: layout 復元が universe-sync をバイパスする矛盾 doc を write-side で作らない

> **採番経緯**: 本ファイルは当初 `0097-` で採番したが、rebase で sibling `fix(#58)`（`0097-issue58-no-trade-day-ohlc-zero-crash.md`）と
> 衝突したため push 前に `0099-` へ振り直した（#123 の `0095-` 衝突と同型の事故。push 前に検出・解消済み）。

#123（findings 0095 — `chart-window-universe-sync-on-restore` / `CHARTSYNC`。**注意**: `0095-` は #123 で 2 本に衝突採番されており、
本 issue が継ぐのは `ChartUniverseSyncE2ERunner`/`CHARTSYNC` 側）は **復元側の self-healing**（`ReseedFromEditor` 末尾で孤児
`chart:<iid>` を despawn）で症状を塞いだ。
本 issue はその **書き込み側の予防＝defense-in-depth**: 「universe に居ない `chart:<iid>` を持つ矛盾した document を
そもそも disk に書かない」ことを保証する。#123 は最後の砦として残す。`/grill-with-docs /behavior-to-e2e` 2026-06-24。

## 挙動（1 文の不変条件 / on-disk consistency）

> 保存される `<strategy>.json` の `layout.floatingWindows` に `chart:<iid>` があるなら、その document を**再オープン時に
> 解決される universe**（`scenario` sidecar キーがあればそれ、無ければ inline `.py` SCENARIO）が `<iid>` を含む。
> **ただし sidecar unreadable / inline unparseable のときは fail-open**（フィルタせず #123 を砦に残す）。

> ⚠️ **issue 本文の不変条件文言（「`scenario.instruments`（sidecar キー）が `<iid>` を含む」）は狭すぎ**。grill のコード裏取りで
> 訂正: 再オープン時に universe を解決するのは `SeedScenarioFromEditor`（`BackcastWorkspaceRoot.cs:308-314`）の
> `PopulateFrom(sidecar ?? inlineFallback)` であって、sidecar キー単体ではない。sidecar キーだけでフィルタすると inline `.py`
> SCENARIO 在住 instrument の chart を誤って落とし、reseed が default grid 位置で再 spawn → **位置永続リグレッション**になる。

## 根本原因（調査済み・#123 の続き）

`scenario` universe（`UniverseWriteback.Flush`）と `layout.floatingWindows`（`TryWriteLayout`）は **別経路・非原子・非対称
ゲート**で書かれる。書き込みゲートが非対称:

| キー | 書く経路 | 発火条件 |
|---|---|---|
| **layout.floatingWindows** | `TryWriteLayout`（File→Save / Save As / 終了 autosave） | `_currentLayoutPath` があれば必ず書く。**universe の状態を問わない** |
| **scenario universe** | `UniverseWriteback.Flush`（`UniverseWriteback.cs:47`）/ Run-commit | Replay **かつ** `Editable` **かつ** 完全 sidecar 既存 **かつ** content 変化（mutate-existing-only #67 / findings 0042） |

layout は「保存さえすれば必ず」書かれるのに scenario は 4 ゲートを全通過しないと書かれない —— この非対称が、`chart:<iid>`
だけ layout に焼かれ universe は空のまま、という矛盾 doc を生む。到達経路の最有力は ①「Replay でサイドバー追加 →
未 Commit → File→Save」（完全 sidecar が無く `Flush` が `SetInstruments==null` でスキップ、`TryWriteLayout` が chart だけ焼く）。

### 各書き込み経路の検証（grill でコード確証・2026-06-24）

| 経路 | universe を永続するか | 結論 |
|---|---|---|
| Save As | する（`OnFileSaveAs` の `_scenario.Commit`→`SetStartupParamsAndInstruments` 完全 sidecar・`:1968`） | chart は全部 universe 在→フィルタ不発・位置保持 |
| 通常 Save | しない（`OnFileSave` は `Flush` event 駆動＋mutate-existing-only のみ・`:1944`） | 未 commit 追加の chart が孤児化しうる |
| 終了 autosave | しない＋**reseed も無し**（`AutosaveCurrentDocument` は `TryWriteLayout` のみ・`:2201`） | **reseed では塞げない＝capture 時フィルタのみが効く** |

→ 終了 autosave 経路があるため、grill 候補の **(C) reseed 並べ替えだけでは不十分**。**(B) co-write は mutate-existing-only /
inline-shadow / D5 に抵触**。よって **(A) capture 時フィルタ**が、3 経路を一様に・既存不変条件を一切壊さず（**読むだけで
scenario を書かない**）満たす唯一案。owner HITL（2026-06-24）で (A)＋oracle=sidecar??inline＋fail-open を確定。

## 採用設計（grill で確定）

`TryWriteLayout` が write 直前に `PruneOrphanChartWindowsForPersistence(doc, path)` を呼ぶ（`BackcastWorkspaceRoot.cs`）:

```csharp
bool TryWriteLayout(string path)
{
    LayoutDocument doc = CaptureLayout();
    PruneOrphanChartWindowsForPersistence(doc, path);   // #124
    LayoutSidecarStore.WriteLayout(path, doc);
    return true;
}
```

- **oracle = `TryResolvePersistedUniverse`** ＝ `ScenarioSidecarStore.TryReadScenario(path)`（sidecar キー）`?? ScenarioInlineReader.Read(path)`
  （inline `.py` SCENARIO）。`SeedScenarioFromEditor` と同一解決。
- **fail-open**: `TryReadScenario` が false（unreadable: 破損 JSON / 構造不正値 / I/O ロック）または inline が
  `ScenarioReadStatus.Unparseable` のとき `false` を返し、呼び出し側はフィルタせず全 chart を残す（#123 が砦）。`Absent`
  （sidecar キー無し＋inline node 無し）は**確信のある空 universe**＝全 chart を prune（到達経路①の核）。
- **read-only**: scenario を一切書かない → mutate-existing-only(#67) / inline-shadow ban / D5（Live universe 非永続）を不変に保つ。
- **chart family only**: `DockShape.IsChartId` のみ対象。base dock 5 窓 / Order ticket / editor shell は無条件で floatingWindows に乗る。
- **live 窓は despawn しない**: captured DTO のみ濾す（Save は `ReseedFromEditor` 末尾で live 収束・終了 autosave は単に終わる）。

### accepted behavior（リグレッションではない）

未 commit 追加 instrument / Live venue-driven instrument の chart が落ちるのは整合（その instrument 自体が永続されていない）。
位置永続が守られるのは「解決済み universe に居る instrument」だけ（CHARTWRITE-02/03/07 が x/y/w/h 保持を pin）。

## 著したゲート（RED→GREEN）

**`Assets/Tests/E2E/Editor/ChartUniverseWriteConsistencyE2ERunner.{cs,md}`**（Issue release-gate slice・Python-FREE・AFK）。
実 `BackcastWorkspaceRoot` を反射合成し、実 `OnFileSave` / `AutosaveCurrentDocument` / `TryWriteLayout`（実 `CaptureLayout` 込み）を
駆動 → 書かれた `.json` を読み戻し assert。

| Action ID | 入口 | cell | assert |
|---|---|---|---|
| CHARTWRITE-01 | `OnFileOpen`→`Universe.Add`→`OnFileSave` | path①（universe 未永続・未 commit 追加） | 保存 `.json` の layout に孤児 `chart:7203` 無し・scenario も空 |
| CHARTWRITE-02 | `TryWriteLayout` | inline `.py` SCENARIO=[7203]（sidecar キー無し） | `chart:7203` 残り＋ジオメトリ(812,-456,520,360)保持（oracle=sidecar??inline） |
| CHARTWRITE-03 | `TryWriteLayout` | sidecar=[7203]、layout に chart:7203+9984 | 9984 落ち・7203 残り＋ジオメトリ保持 |
| CHARTWRITE-04 | `AutosaveCurrentDocument` | 終了 autosave（reseed 不在経路） | autosave 後 `.json` に孤児 `chart:7203` 無し |
| CHARTWRITE-05 | `TryWriteLayout` | sidecar 構造破損（`start` が object→`TryReadScenario` false） | `chart:7203` 残る（fail-open・geometry nuke しない） |
| CHARTWRITE-06 | `TryWriteLayout` | sidecar キー無し＋inline 不均衡 dict（Unparseable） | `chart:7203` 残る（fail-open・inline 枝） |
| CHARTWRITE-07 | `TryWriteLayout`→fresh `ApplyLayout` | group 化 chart:7203+9984、universe=[7203] | 9984 落ち・再オープンで 7203 が復元ジオメトリ＋singleton（group dissolve）・9984 phantom 復元せず |
| CHARTWRITE-08 | `TryWriteLayout` | sidecar キー**存在**＋instruments:[]（present-but-empty） | 孤児 `chart:7203` 落ち（`sidecar!=null` confident-empty 枝・01/04 の inline-absent 枝とは別経路） |

CHARTWRITE-09（実ブートで「サイドバー追加→未 Save→保存→再起動」しても孤児 Chart 窓が出ない目視）は HITL専用。

**RED→GREEN litmus**:
- `TryWriteLayout` の `PruneOrphanChartWindowsForPersistence(doc, path);` を削除 → **CHARTWRITE-01 / 03 / 04 / 07 / 08 RED**（孤児 chart が disk に焼ける）。
- oracle を sidecar キー単体へ狭める（inline fallback を外す） → **CHARTWRITE-02 RED**。
- fail-open を fail-closed（読めない universe を空とみなす）へ変える → **CHARTWRITE-05 / 06 RED**。
- group dissolve を壊す（survivor の dangling group を残す） → **CHARTWRITE-07 RED**。

非 vacuity: 各 cell はフィルタ対象 chart を `ApplyLayout→RestoreFloating`（#123 の第二 spawn 経路）または実 `Universe.Add` で
**実際に live 化**してから prune する（positive-control）。「最初から spawn しない」no-op が pass に化けない。

## 実走（AFK）

```
& $UNITY_EDITOR_PATH -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast `
   -executeMethod ChartUniverseWriteConsistencyE2ERunner.Run -logFile <abs log>
# expect: [E2E CHART UNIVERSE WRITE PASS] + [E2E CHARTWRITE-01..08 PASS] / verdict はタグ
# ランチャ: scripts/run-live-e2e.ps1 -Method ChartUniverseWriteConsistencyE2ERunner.Run
```

### 実走結果（Unity 6000.4.11f1 batchmode・2026-06-24・`error CS` 0 件）

- **GREEN（fix あり）**: `[E2E CHART UNIVERSE WRITE PASS]` ＋ `[E2E CHARTWRITE-01..08 PASS]` 全 8 セル PASS（rollup `8 PASS / 0 FAIL`）。
  ※ CHARTWRITE-08（present-but-empty sidecar の `sidecar!=null` confident-empty 枝）と S2 の confident-resolve precondition は
  並列マルチエージェント・レビュー（#124 review・2026-06-24）の指摘 M1/M2 で追加。
- **RED（fix 削除＝`PruneOrphanChartWindowsForPersistence(doc, path);` をコメントアウト）**:
  `[E2E CHART UNIVERSE WRITE FAIL] S1 CHARTWRITE-01: orphan chart:7203.TSE was BAKED into the persisted layout`
  （`??` 短絡で S1 で停止＝prune 無しで孤児が disk に焼けることを実証・非 vacuity 確認）。
- **#123 二重防御の非破壊**: 同コミット tree で `ChartUniverseSyncE2ERunner`（CHARTSYNC-01..04）も `[E2E CHART UNIVERSE SYNC PASS]` GREEN。

## 関連

- 修正: `Assets/Scripts/Live/BackcastWorkspaceRoot.cs`（`TryWriteLayout` ＋ `PruneOrphanChartWindowsForPersistence` /
  `TryResolvePersistedUniverse`）。
- 復元側の砦（残す）: findings 0095（#123）/ `ChartUniverseSyncE2ERunner`（CHARTSYNC-01..04）。
- 解決の正本: `SeedScenarioFromEditor`（`:304`）= `sidecar ?? inline`。`ScenarioSidecarStore.TryReadScenario`（tolerant・findings 0051 D3）/
  `ScenarioInlineReader.Read`（#66 / findings 0043）。
- 書込ゲートの非対称: findings 0042（#67 mutate-existing-only writeback）。
