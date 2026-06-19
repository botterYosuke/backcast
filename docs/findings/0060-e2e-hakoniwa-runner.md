# 0060 — E2E 第二波: HakoniwaE2ERunner 昇格（7本目）

**日付**: 2026-06-19 / **ブランチ**: `e2e/hakoniwa-runner`（`main` から分岐）
**関連**: [ADR-0015](../adr/0015-e2e-runner-layout-and-script-convention.md) / [台本](../../Assets/Tests/E2E/Editor/HakoniwaE2ERunner.md) /
[findings 0054](0054-e2e-scenario-startup-runner.md)（昇格の型・1本目） / [findings 0059](0059-e2e-depth-ladder-runner.md)（直前6本目） /
[findings 0007](0007-hakoniwa-slot-source-of-truth.md)（slot=正本・grid 派生 rect の不変条件）

## 文脈

第二波の「安い昇格」枠（既存 Probe あり）の **7本目**。Hakoniwa split-grid サーフェスを、`Assets/Editor` 系の
**4 つの throwaway/standing AFK probe**——`HakoniwaProbe`（grid 幾何・swap・back-compat）／`HakoniwaChartTileProbe`
（box-grow・dynamic tile round-trip）／`HakoniwaBaseModeProbe`（base retile・chart identity・profile honor）／
`HakoniwaProfileProbe`（per-mode validity 行列）——から **1 本の `HakoniwaE2ERunner.cs`（16 section）へ昇格・統合**。
型は findings 0054〜0059 で確立した「throwaway probe → `<Surface>E2ERunner`（PASS/FAIL タグ統一・`EditorApplication.Exit`
無条件化・`Execute()`-形 section chain）＋(B) 自然な検証単位＋`Covers:`＋AFK RED→GREEN litmus」。

DepthLadder（0059・1 probe→1 runner の git mv）と違い、本件は **4 probe → 1 runner の集約**で、probe ごとに
EPS 規約・root 合成方式が異なる（下記「設計の急所」）。assert は 1 行も削らず移送した。

## 昇格モデル（section と probe の対応）

`Run()` は 16 section を `??` で連結し最初の失敗文字列を返す（null=PASS・self-failing gate）。Action ID は
[台本](../../Assets/Tests/E2E/Editor/HakoniwaE2ERunner.md) の HAKONIWA-01..10 に対応。

| Section | Covers | 出自 probe | 観測 |
|---|---|---|---|
| S1 GridArithmetic | HAKONIWA-01,02 | HakoniwaProbe | `GridDims`/`CellRects`（5→3x2・等分・cover・非 overlap・空 6 番セル） |
| S2 SlotHitTest | HAKONIWA-02 | HakoniwaProbe | `SlotAt` hit-test（cell 中心・空 6 番・枠外 -1） |
| S3 ControllerReorder | HAKONIWA-01 | HakoniwaProbe | 実 RectTransform ツリー上で order→cell anchor 配線＋`Capture`/`Apply` 境界 |
| S4 DiskRoundTripNonVacuous | HAKONIWA-04 | HakoniwaProbe | slot 順 save→disk TEXT 証明→fresh load `Apply`（vacuous-green kill） |
| S5 InvalidOpAndTolerance | HAKONIWA-03,10 | HakoniwaProbe | invalid-op no-op＋重複/範囲外 slot tolerance |
| S6 BackCompatAndFallback | HAKONIWA-10 | HakoniwaProbe | missing/unknown id＋corrupt/missing → default fallback |
| S7 BoxGrowArithmetic | HAKONIWA-06 | HakoniwaChartTileProbe | `ComputeBoxSize == compute_hakoniwa_box_size`（n 依存・min-tile floor・非永続） |
| S8 DynamicTilesRoundTrip | HAKONIWA-05 | HakoniwaChartTileProbe | `AddTile`/`RemoveTile`＋非デフォルト chart 順の slot 非崩壊 round-trip |
| S9 ModeTileKinds | HAKONIWA-07 | HakoniwaBaseModeProbe | `HakoniwaBaseTiles.Kinds == TTWR hakoniwa_tile_kinds` |
| S10 BaseOnlyRetilePreservesChartIdentity | HAKONIWA-07 | HakoniwaBaseModeProbe | Replay↔Live で base のみ retile・chart の **RectTransform identity 保持**・box の n_total 再導出 |
| S11 LiveManualAutoNoOp | HAKONIWA-08 | HakoniwaBaseModeProbe | 同一 Live shape 2 度駆動で count/identity 不変（no-op） |
| S12 RestoreAppliesPerModeProfile | HAKONIWA-09 | HakoniwaBaseModeProbe | 実 root の `ApplyProfileOrder`（collision/legacy→canonical・valid→honor・visible 復活） |
| S13 ProfileValidityHonor | HAKONIWA-09 | HakoniwaProfileProbe | valid profile が saved(user-swapped) base 順を honor・Replay≠Live |
| S14 LegacyAndCollisionFallToCanonical | HAKONIWA-09 | HakoniwaProfileProbe | legacy/collision/set-mismatch→invalid→canonical `Kinds(mode)` |
| S15 ChartOrderExcludedAndPreserved | HAKONIWA-09 | HakoniwaProfileProbe | chart id は validity に無関与・base prefix から除外・chart 順保持 |
| S16 SeedAndDiskRoundTrip | HAKONIWA-09 | HakoniwaProfileProbe | forward-compat seed（AC2）＋`LayoutStore` disk round-trip |

## 設計の急所（grill 由来・section ごとに厳密保存）

1. **EPS の section 別保存（grill 急所2）**: HakoniwaProbe 由来の格子幾何 6 section（S1–S6）は元 probe どおり
   **`EPS_GRID=1e-4f`**、ChartTile/BaseMode/Profile 由来（S7–S16）は **`EPS=1e-3f`**。集約時に片方へ丸めると
   元 probe の許容誤差を緩める/締めるので、定数 2 本を分けて保存。
2. **BaseMode 由来 section の独立 scene build（grill 急所3）**: S9–S12 は元 `HakoniwaBaseModeProbe` どおり
   **section 内で独立に `OpenScene`+`BuildWorkspace`** する（共有 root へ畳まない）。実 `BackcastWorkspaceRoot` を
   反射合成して `SyncBaseTilesToMode`/`ApplyProfileOrder`/`Universe.ReplaceAll` を駆動するため、section 間で
   root 状態を共有すると base 集合・chart identity の検証が汚染される。verbatim 保存。
3. **色 section を新設しない（Theme 重複回避）**: Hakoniwa runner には色ロールの assert を入れない。テーマは
   `ThemeProbe`（findings 0054 で `hakoniwa_*` ロールへ移行済）が正本で、surface runner 側に重複させない。

## 据え置きの仕分け（owner 判断: ChartTile S2 / BaseMode S5 を Hakoniwa 以外カテゴリへ）

owner の「Hakoniwa 以外のカテゴリへ移す」判断を、**2 つの orphan section を Hakoniwa runner に入れず・捨てず・
元 probe に trimmed standing probe として据え置く**機構で満たした（将来 Chart/Panel カテゴリ runner へ移送予定）:

- **`HakoniwaChartTileProbe` を S2 のみへ trim**: `InstrumentOhlcDecoder` のチャート数値読取り（decoy/null/absent/
  malformed の per-id ohlc decode）。Hakoniwa grid の関心事ではない＝将来の **Chart カテゴリ runner** へ。
- **`HakoniwaBaseModeProbe` を S5 のみへ trim**: #65 の口座/RunResult panel empty-state（honest Replay 空状態＋
  real-data render＋RunResult running→full 切替）。これも grid でなく panel の関心事＝将来の **Panel カテゴリ runner** へ。
- **`HakoniwaProbe` と `HakoniwaProfileProbe` は `git rm`**（assert を wholesale で runner へ移送済み・残す section 無し）。

commit: `4298a94`（runner 16 section 追加・compile-clean）→ `7fa3a60`（Chart/BaseMode を orphan section へ trim・
wholesale 移送済み 2 probe を rm）。

## behavior-to-e2e 判断

**不要**。本件は pure promotion（既存 probe assert の集約・移送）で、新しい Action ID も挙動変更も不変条件追加も無い。
production ロジックは一切触らず、検証ゲートのみを再配置した。FLOWS.md 相当（backcast では `*E2ERunner.md` 台本が
正本）は本 runner の .md と E2E-INDEX で整合済み。

## RED→GREEN（実走実証）

memory `e2e-wave2-runner-promotion` の「昇格元が既稼働 GREEN＋移送が assert を 1 行も削らないなら RED は GREEN 実走
＋delete-the-logic litmus 計画で代替可」に該当。新規不変条件を一切足していない（4 probe の assert を verbatim 移送）
ため physical RED は集約せず、**16 section を統合 runner で GREEN 実走**＋台本の delete-the-production-logic litmus
（`HakoniwaController.Swap` の入れ替え本体・`SyncBaseTilesToMode` の startup add/remove・`ComputeBoxSize` の n 依存項を
消すと対応 assert が落ちる）で非空虚性を anchoring。trim 後の 2 orphan probe も独立 GREEN を確認（移送漏れ/二重削除の
検出）。

### AFK 実走結果（serial 実行・全 GREEN・verbatim）

```
HakoniwaE2ERunner.Run        exit=0  [E2E HAKONIWA PASS] grid arithmetic + slot hit-test + controller reorder + non-vacuous slot disk round-trip + invalid-op/tolerance + back-compat/fallback + box-grow + dynamic chart tile round-trip + mode tile kinds + base-only retile (chart identity) + live-shape no-op + per-mode profile honor/canonical + profile validity matrix + chart exclusion + forward-compat seed + per-mode disk round-trip verified.   sentinel: Found no leaked weakptrs.
HakoniwaChartTileProbe.Run   [HAKONIWA CHART TILE PASS] per-id ohlc decode (decoy/null/absent/malformed) verified.   sentinel OK
HakoniwaBaseModeProbe.Run    exit=0  [HAKONIWA BASE MODE PASS] honest Replay empty-state + #65 real-data render + RunResult running→full switch verified.   sentinel OK
```

→ 統合 runner も、trim 後に据え置いた 2 orphan probe も全て GREEN。実装は確定。

## 再走手順

```pwsh
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
  -batchmode -nographics -quit -projectPath "C:\Users\sasai\Documents\backcast" `
  -executeMethod HakoniwaE2ERunner.Run -logFile "$env:TEMP\hako.log"
# 期待: [E2E HAKONIWA PASS] ... / exit 0
```

- compile-only ゲート: `-executeMethod` を外した同コマンドで `error CS\d+` 0 件。
- 確認は **Bash `grep -a "E2E HAKONIWA" <log>`**（`→` 等を含む行を ripgrep/`Select-String` は取りこぼす）。
- **serial 必須**（同一 compile 単位＋Unity プロジェクトロック）。**lock-abort で再走した経緯**: 統合 runner と
  orphan probe を並行起動したところ 1 本目が Unity プロジェクトロックを掴んだまま 2 本目が lock 競合で abort した。
  `Get-Process Unity` が空になるのを確認してから次を起動する直列運用で全 GREEN を取得（memory `unity-afk-probe-run`
  の lock-abort 罠）。

## 改名の波及（現行化）

- 台本 `HakoniwaE2ERunner.md`（HAKONIWA-01..10 を `自動(E2E済)` へ・実装方針 section を実装済みへ）と
  `E2E-INDEX.md`（Hakoniwa 行を `13|10|0|0|1|2`＋✅・rollup に 7本目追記・残り未昇格から Hakoniwa を除外）を整合。
- stale narrative コメント現行化: `HakoniwaHitlHarness.cs` と `HakoniwaController.cs` の「authoritative gate は
  HakoniwaProbe」言及を現 `HakoniwaE2ERunner` へ（コメントのみ・ロジック不変）。
- 統合 runner `.cs` 冒頭の provenance コメント（HakoniwaProbe 等 4 probe からの昇格元説明）は **意図的な由来記録**
  なので falsify しない（report のみ）。historical findings（0007 等）の probe 言及も narrative＝残置。
