# HakoniwaE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`HakoniwaE2ERunner.cs`（第二波で実装済み・findings 0060）が自動検証する **Hakoniwa split-grid サーフェス**の台本。実装者は `.cs` と
本 `.md` をセットで読む。これは調査メモではなく、**このサーフェスでユーザーができる行動すべての網羅台帳と、
E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（1サーフェスでユーザーができる操作を網羅する回帰ゲート）。
> 複数サーフェスをまたぐ実ユーザーストーリー（universe seed → run → 箱庭更新）は *Journey E2E*
> （`ReplayToHakoniwaE2ERunner`）が担う。本 Surface 台本は「ヘッダ drag / mode 切替 / universe 同期が
> 正しい slot 順・tile 集合・box サイズの状態遷移を起こすか」までを観測する。

## 対象サーフェス

infinite canvas の Content 上に乗る単一の **split-grid サーフェス**（`HakoniwaController` ＋ 入力境界
`HakoniwaTileHeaderInput`）。chart + status 系 tile を **locked `ceil(√n)` グリッド**（`HakoniwaGridMath`）に並べる。
**slot（tile 順）が状態の正本**で、rect は n+slot から派生する snapshot（findings 0007 §3）。TTWR の Hakoniwa
（`src/ui/hakoniwa.rs`・ADR 0011/0014）の capability parity（ADR-0003・形式非互換）。tile の集合所有は分離する＝
**base tile = ExecutionMode**（`HakoniwaBaseTiles`・ADR 0013）／**chart tile = universe（`InstrumentRegistry`）**
（#60/#169）。membership 駆動（spawn/despawn・box-grow・base retile）の orchestrator は `BackcastWorkspaceRoot`で、
本コントローラは grid actuation を担う。

## 対象ユーザー行動

tile ヘッダ drag による slot swap、swap のキャンセル/no-op、slot 順の永続化、universe 銘柄 add/remove に追従する
chart tile の spawn/despawn、銘柄数 n からの box derived-grow、mode 切替（Replay↔Live）での base retile、
LiveManual⇄LiveAuto の no-op、mode 別 layout profile の honor/canonical 復元、欠損/未知/重複/破損 doc の tolerance。
divider resize と box 移動/リサイズは **未実装（将来 slice）**なので行動行に理由付きで載せる。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 昇進元 / 既存 Probe |
|---|---|---|---|---|---|---|
| HAKONIWA-01 | tile ヘッダを drag して 2 tile の slot を入れ替え | `HakoniwaTileHeaderInput.cs:56`→`HakoniwaController.cs:70`（`Swap`） | `_order` の 2 要素が入れ替わり、両 tile の anchorMin/Max が新 cell に再配置（offsets 0）、`SlotOf` が追従 | 反射で `Swap(from,to)` を駆動し `_order`/anchors を assert | 自動(E2E済) | §1,§3（旧 `HakoniwaProbe`） |
| HAKONIWA-02 | drag drop 位置から着地 slot を決定（hit-test） | `HakoniwaTileHeaderInput.cs:49,55`→`HakoniwaController.cs:124`（`SlotAtNormalized`） | drop 点 → root-local → 0..1 正規化 → セル hit-test（cell 中心は自 slot、空セル/枠外は -1） | 正規化点で `SlotAtNormalized`/`HakoniwaGridMath.SlotAt` を assert（screen→正規化変換と EventSystem 経路は HITL） | 自動(E2E済) | §1,§2（旧 `HakoniwaProbe`） |
| HAKONIWA-03 | 同 slot へ drop / 枠外へ drop（swap キャンセル） | `HakoniwaController.cs:71-73`（`Swap` guard） | `from==to`・範囲外・負値・枠外 hit（-1）は **no-op（false）**で `_order` 不変 | 反射で no-op 系 Swap を駆動し順序不変を assert | 自動(E2E済) | §5（旧 `HakoniwaProbe`） |
| HAKONIWA-04 | swap 後のレイアウトが保存・復元される | `HakoniwaController.cs:129`（`Capture`）／`:147`（`Apply`） | slot 順が `Capture`→disk→`Load`→`Apply` で round-trip（rect は grid から再生成・on-disk text に `"slot"` が出る） | 一時パスへ save→fresh controller で `Apply`、swap 順と cell 配置を assert（vacuous-green kill 込み） | 自動(E2E済) | §4（旧 `HakoniwaProbe`） |
| HAKONIWA-05 | universe に銘柄 add/remove → chart tile が即 spawn/despawn | `BackcastWorkspaceRoot.cs:618`（`SyncChartTilesToUniverse`）→`HakoniwaController.cs:86,97`（`AddTile`/`RemoveTile`） | `chart:<id>` tile が universe と常に同期（add=末尾 slot へ append・remove=order から除去）、`[base…, chart…]` 不変、識別は id prefix `chart:` | `Universe.ReplaceAll` 後の `_chartTiles`/`hako.Count`/order を assert、非デフォルト chart 順の disk round-trip | 自動(E2E済) | §8（旧 `HakoniwaChartTileProbe`） |
| HAKONIWA-06 | 銘柄数で箱（box）が決定的に grow（位置/サイズは非永続） | `BackcastWorkspaceRoot.cs:779`→`HakoniwaGridMath.ComputeBoxSize` | box サイズ = `compute_hakoniwa_box_size` の port（min-tile floor・default floor・n から derive・**persist しない**） | `ComputeBoxSize(n,…)` を TTWR 期待値（n=6→840x450 等）と突き合わせ | 自動(E2E済) | §7（旧 `HakoniwaChartTileProbe`） |
| HAKONIWA-07 | mode 切替（Replay↔Live）で base 集合が変わったとき base retile | `BackcastWorkspaceRoot.cs:717`（`SyncBaseTilesToMode`） | Replay=`[startup,buying_power,orders,positions,run_result]`／Live=startup を despawn。**base 集合が変わったときだけ** retile、chart tile は identity 保持で後半 slot へ再配置、box は n_total から再導出 | 実 root を合成し `SyncBaseTilesToMode(true/false)` を反射駆動、`SlotOf("startup")`・chart の RectTransform 同一性・box を assert | 自動(E2E済) | §9,§10（旧 `HakoniwaBaseModeProbe`） |
| HAKONIWA-08 | LiveManual ⇄ LiveAuto（同一 Live shape）= no-op | `BackcastWorkspaceRoot.cs:717`（`SyncBaseTilesToMode(true)` 二度） | 2 度目の Live retile は despawn/respawn 無し（tile count・chart identity・startup 不在 不変） | 同一 shape を 2 度駆動し count/identity 不変を assert | 自動(E2E済) | §11（旧 `HakoniwaBaseModeProbe`） |
| HAKONIWA-09 | mode 別 layout profile の復元（honor / canonical 落とし） | `BackcastWorkspaceRoot.ApplyProfileOrder`（`_profiles` seed 経由） | 保存 profile の base id 集合が `Kinds(mode)` と一致＝user の base 並びを honor、不一致/欠損/衝突 doc は canonical へ（#61 衝突安全）。chart 順は常に honor・membership は universe 再導出 | `HakoniwaLayoutProfiles` の validity 行列＋実 root の `ApplyProfileOrder` を assert | 自動(E2E済) | §12–§16（旧 `HakoniwaProfileProbe`／`HakoniwaBaseModeProbe`） |
| HAKONIWA-10 | 欠損/未知/重複/破損 doc の tolerance（back-compat） | `HakoniwaController.cs:166`（`DeriveOrder`）／`:186`（`NormalizeOrder`） | doc 未知 id は無視・既知欠落 id は末尾・重複/範囲外 slot は contiguous へ collapse・破損 JSON は default 順 | tolerance doc を `Apply` し count 不変＋期待順を assert | 自動(E2E済) | §5,§6（旧 `HakoniwaProbe`） |
| HAKONIWA-11a | 盤面 point の入力ルーティング決定（header→swap / body→pan / 盤外→pan の経路分離・math-pick） | `HakoniwaGridMath.RouteBoardPoint`（board-normalized point→(slot,inHeader)）←`HakoniwaStageMath.UnprojectToSlot` | header band（cell 上端 `headerFrac`）=(slot=S, inHeader=true)＝swap、body=(slot=S, inHeader=false)＝pan、盤外/隙間=(slot=-1)＝pan。production==gate（findings 0068 §12） | `HakoniwaPerspectiveStageProbe` §4（`Section4_BoardPointRouting`）が (a)/(b)/(c) を assert・RED→GREEN 実証（findings 0068 §14） | 自動(Probe有・要昇格)（issue #93 入力スライス gate。E2ERunner 昇格は #93 実配管時に `HakoniwaInputRoutingE2ERunner` へ） | `HakoniwaPerspectiveStageProbe` §4 |
| HAKONIWA-11b | ヘッダ drag の実感（実 screen-press → 掴めて入れ替わる視覚・取りこぼし無し） | `HakoniwaTileHeaderInput.cs:41`（`OnEndDrag`）／#93 後は RawImage への screen-press→RT pixel→`RouteBoardPoint` dispatch | 実ポインタ drag で tile が掴めて入れ替わる視覚、ヘッダのみ swap・body は pan へ fall-through、取りこぼし無し（math-pick routing 決定は HAKONIWA-11a が AFK で固定済み） | — | HITL専用（実ピクセル＋実マウス＋実ウィンドウ＋GPU 前提） | `HakoniwaHitlMenu` |
| HAKONIWA-12 | divider drag で列幅/行高の比率変更（resize） | — | — | — | 対象外（未実装・将来 slice。`HakoniwaGridMath.cs:12`「#14 does NOT do divider resize」・等分グリッド固定） | — |
| HAKONIWA-13 | 箱（box）を drag で移動／drag-handle でリサイズ・位置/サイズを永続化 | — | — | — | 対象外（未実装・将来 slice。box 位置/サイズは derived・`tile/slot/tile swap`/`chart tile family` エントリで #14 外と明記） | — |
| HAKONIWA-14 | hakoniwa が斜め俯瞰ジオラマ（perspective stage）で描画される（盤面が WorldSpace Stage canvas に載り perspective camera が透過 RT へ撮影 → `_content` の RawImage で合成） | `BackcastWorkspaceSceneBuilder.Build`（Stage canvas/perspective camera/RT/RawImage 配線・HakoniwaRoot reparent）／`BackcastWorkspaceRoot._hakoniwaRawImage` | Stage canvas=`WorldSpace`+専用 layer／perspective camera `targetTexture`=RT・`cullingMask`=当該 layer のみ・fov/位置が `StageParams.Default` 由来／Main camera が当該 layer 除外／board rotation=`Euler(pitch,yaw,0)`・寸法 Default 由来／HakoniwaRoot が `_content`→Stage canvas へ転出・RawImage が旧位置 | `BackcastWorkspaceProbe.Section16_HakoniwaStageWiring` ＋ Section2 反転（HakoniwaRoot 非 Content）＋ Section1 `_hakoniwaRawImage` ref（RED→GREEN staged・findings 0068 §15） | 自動(Probe有) RED→GREEN | `BackcastWorkspaceProbe` §16 / findings 0068 §15 |

> tile rect は slot から派生する snapshot なので「rect を動かす行動」は無い（自由配置・overlap は不変条件違反）。
> universe の編集自体は Universe サイドバー サーフェスの行動で、本台本は HAKONIWA-05 として **Hakoniwa 側の反映**を観測する。

## 観測点（詳細）

- **HAKONIWA-01/02/03（swap）**: 本 runner §1（grid 算術＝`GridDims`/`CellRects`・等分/cover/非 overlap/空 6 番セル）、
  §2（`SlotAt` hit-test・cell 中心/空 6 番/枠外）、§3（実 RectTransform ツリー上で order→cell anchor・`Capture`/`Apply` 境界）、
  §5（no-op/重複/範囲外 tolerance）。旧 `HakoniwaProbe` S2–S6 を assert を 1 行も削らず移送。実ポインタ→drop の screen 変換と
  EventSystem 経路（header が上に乗るので canvas が gesture を見ない）の実体感は HITL（HAKONIWA-11b）、#93 perspective stage の math-pick routing 決定（header/body/盤外）は AFK（HAKONIWA-11a / `HakoniwaPerspectiveStageProbe` §4）。
- **HAKONIWA-04（永続化）**: 本 runner §4 が on-disk TEXT 証明（`"id":"run_result","slot":0` 等）＋fresh load で
  vacuous-green を kill。File→Save 連動（layout sidecar への書き込み）は MenuBar / Journey 側の責務。
- **HAKONIWA-05/06（chart 同期・box-grow）**: 本 runner §8（`AddTile`/`RemoveTile`＋非デフォルト order の disk round-trip）と
  §7（`ComputeBoxSize`）。旧 `HakoniwaChartTileProbe` の box-grow/dynamic tile を移送。membership 所有は
  `BackcastWorkspaceRoot`（universe `InstrumentRegistry.Changed`）だが、grid への反映を本サーフェスで観測する。
- **HAKONIWA-07/08（base retile）**: 本 runner §9（`HakoniwaBaseTiles.Kinds == TTWR hakoniwa_tile_kinds`）・§10（非自明な核心＝
  Replay→Live→Replay で base のみ retile・chart の **RectTransform identity 保持**・box の n_total 再導出を実 root で assert）・
  §11（Live shape no-op）。各 section は元 `HakoniwaBaseModeProbe` どおり section 内で独立に `OpenScene`+`BuildWorkspace` する。
- **HAKONIWA-09（per-mode profile）**: 本 runner §13–§16 が pure `HakoniwaLayoutProfiles` の validity 行列＋forward-compat seed＋
  disk round-trip、§12 が実 root の `ApplyProfileOrder`（collision/legacy→canonical／valid→honor／visible 復活）を assert。
  旧 `HakoniwaProfileProbe` / `HakoniwaBaseModeProbe` S4 を移送。

## 自動判定（合格条件）

- ログに `[E2E HAKONIWA PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+` が 0 件。
- 各 `自動(*)` 行の観測点を 1 つでも落としたら `[E2E HAKONIWA FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `HakoniwaController.Swap` の入れ替え本体・`SyncBaseTilesToMode` の startup add/remove・
  `ComputeBoxSize` の n 依存項を消すと、対応する assert が必ず落ちること。
- section ごとに EPS を厳密保存: 格子幾何 6 section（§1–§6）は `EPS_GRID=1e-4f`、ChartTile/BaseMode/Profile 由来
  （§7–§16）は `EPS=1e-3f`（元 probe の許容誤差を集約時に緩めない）。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `HakoniwaProbe` | batchmode・pure＋実 RectTransform | **昇格済み**（`git rm`）。S2–S6 → 本 runner §1–§6。HAKONIWA-01/02/03/04/10 の正本 |
| `HakoniwaChartTileProbe` | batchmode・pure＋実 RectTransform | box-grow（S1）→ §7、dynamic tile round-trip（S3）→ §8 を移送（HAKONIWA-05/06）。**S2（ohlc decode）は Hakoniwa 外の関心事として trim 据え置き**——将来の Chart カテゴリ runner へ移送予定 |
| `HakoniwaBaseModeProbe` | batchmode・実 root 合成 | base retile/chart identity/profile（S1–S4）→ §9–§12 を移送（HAKONIWA-07/08/09）。**S5（#65 panel empty-state）は Hakoniwa 外の関心事として trim 据え置き**——将来の Panel カテゴリ runner へ移送予定 |
| `HakoniwaProfileProbe` | batchmode・pure logic | **昇格済み**（`git rm`）。per-mode validity 行列／disk round-trip → 本 runner §13–§16（HAKONIWA-09） |
| `HakoniwaHitlMenu` | HITL ハーネス | HAKONIWA-11b の視覚・実 screen-press 確認用に**探索 Probe として残す**（routing 決定 11a は AFK） |

> 据え置き 2 section（ChartTile S2 / BaseMode S5）は Hakoniwa grid の不変条件ではない（チャート数値 decode / 口座 panel の
> empty-state）ため本 runner には入れず、元 probe を **trimmed standing probe** として残し回帰を維持する（findings 0060）。

## 実装メモ（findings 0060）

- HAKONIWA-07/08/09 は元 `HakoniwaBaseModeProbe` 同型に **実 `BackcastWorkspaceRoot` を反射合成**（`OpenScene` →
  `_font` セット → `ResolvePaths` → `BuildWorkspace`）し、`SyncBaseTilesToMode`/`ApplyProfileOrder`/`Universe.ReplaceAll`
  を反射駆動。各 section が独立に scene build する（共有 root へ畳まない）。Python-FREE（base 集合・chart 同期・slot は kernel 不要）。
- HAKONIWA-01〜04/10 は元 `HakoniwaProbe`/`HakoniwaChartTileProbe` 同型に **headless RectTransform ツリー**を直接組み、
  `HakoniwaController` を pure に駆動（root 合成不要の軽量セクション）。disk round-trip は production sidecar を汚さない一時パスへ。
- セクション構成は操作一覧表の `自動(*)` 行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを返す `Execute()`（null=PASS）
  パターン。teardown は spawned GameObject の `DestroyImmediate` ＋ 一時 dir 削除。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod HakoniwaE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep / Bash `grep -a` で grep**（PowerShell `Select-String` は取りこぼす）。serial 実行必須（Unity プロジェクトロック）。
