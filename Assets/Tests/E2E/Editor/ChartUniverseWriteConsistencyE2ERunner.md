# ChartUniverseWriteConsistencyE2ERunner — 台本（Issue #124 release-gate slice / 操作網羅台帳）

`ChartUniverseWriteConsistencyE2ERunner.cs` が自動検証する issue #124 の release gate。実装者は `.cs` と本 `.md` を
セットで読む。下位事実: [findings 0099](../../../../docs/findings/0099-issue124-chart-universe-write-side-consistency.md)。
採番・カバー語彙・責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)、配置は
[ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。

> **位置づけ**: *Issue release-gate slice runner*。#123（[ChartUniverseSyncE2ERunner](./ChartUniverseSyncE2ERunner.md)）の
> **復元側** self-heal に対する **書き込み側** の補完＝defense-in-depth。#123 は最後の砦として残す（両ゲートとも GREEN を維持）。

## 守る不変条件（1 文・on-disk consistency）

> 保存される `<strategy>.json` の `layout.floatingWindows` に `chart:<iid>` があるなら、その document を**再オープン時に
> 解決される universe**（`scenario` sidecar キーがあればそれ、無ければ inline `.py` SCENARIO ―― `SeedScenarioFromEditor`
> と同一解決）が `<iid>` を含む。**ただし sidecar unreadable / inline unparseable のときは fail-open**（フィルタせず #123 を砦に残す）。

## 直す不具合

`scenario` universe（`UniverseWriteback.Flush`）と `layout.floatingWindows`（`TryWriteLayout`）は **別経路・非原子・
非対称ゲート**で書かれる:

| キー | 書く経路 | 発火条件 |
|---|---|---|
| **layout.floatingWindows** | `TryWriteLayout`（File→Save / Save As / 終了 autosave） | `_currentLayoutPath` があれば必ず書く。**universe の状態を問わない** |
| **scenario universe** | `UniverseWriteback.Flush` / Run-commit | Replay **かつ** Editable **かつ** 完全 sidecar 既存 **かつ** content 変化（mutate-existing-only #67） |

この非対称が、`chart:<iid>` だけ layout に焼かれ universe は空のまま、という矛盾 doc を生む（到達経路①: Replay でサイドバー
追加→未 Commit→File→Save。Flush が mutate-existing-only でスキップし、`TryWriteLayout` が chart だけ焼く）。

## 直し方（採用 = (A) capture 時フィルタ）と不採用案

- **採用 (A)**: `TryWriteLayout` が write 直前に `PruneOrphanChartWindowsForPersistence` を呼び、captured `LayoutDocument` から
  「その doc の**解決済み universe（sidecar??inline）**に居ない `chart:<iid>`」を落とす。**scenario は読むだけ**（一切書かない）
  なので mutate-existing-only(#67)/inline-shadow/D5（Live universe 非永続）を壊さない。3 経路（Save / Save As / 終了 autosave）が
  全て `TryWriteLayout` を通るので一様にカバー。
- **不採用 (B) 整合 co-write**: layout 書込時に universe も scenario へ書く。完全 sidecar を作ると inline `.py` SCENARIO を恒久
  shadow し mutate-existing-only / D5 に抵触。
- **不採用 (C) write-time reseed 並べ替え**: 保存直前に reseed して live を整合させてから capture。Save 系は塞げるが **終了
  autosave に reseed が無い**ため穴が残る（CHARTWRITE-04 がこの穴を pin）。(A) が (C) を内包。

### accepted behavior（リグレッションではない）

- **未 commit 追加 instrument / Live venue-driven instrument の chart が落ちる**のは整合であって位置永続リグレッションでは
  ない（その instrument 自体が永続されていない＝chart だけ残す方が矛盾）。位置永続が守られるのは「解決済み universe に
  居る instrument」だけ（CHARTWRITE-02/03/07 が x/y/w/h 保持を pin）。

## 最重要の litmus

- **テスト自身は `InitializePython` を呼ばない（Python-FREE）。** layout capture と scenario READ に kernel は不要。
- **実 production 入口を駆動**: CHARTWRITE-01 は `OnFileOpen→OnFileSave`、CHARTWRITE-04 は `AutosaveCurrentDocument`、
  02/03/05/06/07/08 は永続化チョークポイント `TryWriteLayout` を実 `CaptureLayout` 込みで反射駆動。
- 非 vacuity: フィルタ対象の chart は `ApplyLayout→RestoreFloating`（=#123 の第二 spawn 経路）または実 `Universe.Add` で
  **実際に live 化**してから prune する（positive-control）。「最初から spawn しない」no-op が pass に化けない。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（method） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| CHARTWRITE-01 | bare doc（universe 未永続）を Open → サイドバー相当の未 commit 追加 → File→Save | `OnFileOpen`→`Universe.Add`→`OnFileSave`→`TryWriteLayout` | 保存 `.json` の `layout` に `chart:7203` 無し／scenario universe も空のまま | 反射で disk 読戻し・孤児不在を assert | 自動(E2E済) | — |
| CHARTWRITE-02 | inline `.py` SCENARIO=[7203]（sidecar キー無し）の doc を保存 | `TryWriteLayout`（`CaptureLayout` 込み） | `chart:7203` が残り復元ジオメトリ(812,-456,520,360)保持 | 反射で keep＋矩形一致を assert（oracle=sidecar??inline） | 自動(E2E済) | — |
| CHARTWRITE-03 | sidecar universe=[7203]、layout に `chart:7203`+`chart:9984` を保存 | `TryWriteLayout` | `chart:9984` 落ち・`chart:7203` 残り＋ジオメトリ保持 | 反射で subset（孤児＋残存）を assert | 自動(E2E済) | — |
| CHARTWRITE-04 | bare doc を Open → 未 commit 追加 → **終了 autosave**（reseed 経由しない） | `OnFileOpen`→`Universe.Add`→`AutosaveCurrentDocument`→`TryWriteLayout` | autosave 後 `.json` の `layout` に `chart:7203` 無し | 反射で disk 読戻し・孤児不在を assert（reseed 不在経路） | 自動(E2E済) | — |
| CHARTWRITE-05 | scenario sidecar が構造破損（`start` が object→`TryReadScenario` false）の doc を保存 | `TryWriteLayout` | `chart:7203` が**残る**（fail-open・geometry を nuke しない） | 反射で fail-open keep を assert | 自動(E2E済) | — |
| CHARTWRITE-06 | sidecar キー無し＋inline SCENARIO が不均衡 dict（Unparseable）の doc を保存 | `TryWriteLayout` | `chart:7203` が**残る**（fail-open・inline 枝） | 反射で fail-open keep を assert | 自動(E2E済) | — |
| CHARTWRITE-07 | group 化した `chart:7203`+`chart:9984`（universe=[7203]）を保存→再オープン | `TryWriteLayout`→（fresh root）`ApplyLayout`→`RestoreFloating` | disk から 9984 落ち・再オープンで 7203 が復元ジオメトリ＋singleton（group dissolve）・9984 は phantom 復元されない | 反射で group 衛生を assert | 自動(E2E済) | — |
| CHARTWRITE-08 | sidecar scenario キーが**存在し instruments:[]（present-but-empty）**の doc を保存 | `TryWriteLayout` | `chart:7203` 落ち（`sidecar!=null` confident-empty 枝・01/04 の inline-absent 枝とは別） | 反射で disk 読戻し・孤児不在を assert | 自動(E2E済) | — |
| CHARTWRITE-09 | 実ブートで「サイドバー追加→未 Save→保存→再起動」しても孤児 Chart 窓が出ないことを目視 | 実 `Awake`→保存→再 boot | 実 GUI で chart 窓が出ない／sidebar と一致 | 実ウィンドウ・実ピクセル | HITL専用（実ブート・目視） | — |

## litmus（delete-the-production-logic）

- `TryWriteLayout` の `PruneOrphanChartWindowsForPersistence(doc, path);` を削除 → **CHARTWRITE-01 / 03 / 04 / 07 / 08 RED**（孤児 chart が disk に焼ける）。
- oracle を sidecar キー単体へ狭める（inline fallback を外す） → **CHARTWRITE-02 RED**（inline universe の chart を誤って prune）。
- fail-open を fail-closed（読めない universe を空とみなす）へ変える → **CHARTWRITE-05 / 06 RED**（transient read 失敗で復元ジオメトリを nuke）。
- group dissolve を壊す（survivor の dangling group を残す） → **CHARTWRITE-07 RED**。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
        -executeMethod ChartUniverseWriteConsistencyE2ERunner.Run -logFile <abs log>
# expect: [E2E CHART UNIVERSE WRITE PASS] / exit=0  （確認は Bash `grep -a "CHART UNIVERSE WRITE"`）
# per-Action-ID タグ: [E2E CHARTWRITE-01 PASS] … [E2E CHARTWRITE-08 PASS]（rollup は単一トークン id で集計）
# compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
# ランチャ経由: pwsh scripts/run-live-e2e.ps1 -Method ChartUniverseWriteConsistencyE2ERunner.Run
```
