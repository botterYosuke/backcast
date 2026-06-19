# LayoutPersistenceJourneyE2ERunner — 台本（Journey E2E 仕様 / 観測点 / 合格条件）

`LayoutPersistenceJourneyE2ERunner.cs`（第二波で実装）が自動検証する **Journey E2E** の台本。実装者は `.cs` と
本 `.md` をセットで読む。これは調査メモではなく、**横断ストーリーの仕様・観測点・合格条件を定義する正本**。
Action ID 採番・カバー状態の語彙・責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は
[ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Journey E2E*（複数サーフェスをまたぐ実ユーザーストーリー）。各ステップの
> 単体挙動は対応 Surface 台本（[MenuBarE2ERunner](./MenuBarE2ERunner.md) の File 操作群）へ参照を張り、ここでは
> **配置データが「窓/タイル/canvas」→ `<strategy>.json` の `layout` キー → 開き直し→ live UI へ繋がって round-trip
> するか（横断の縫い目）**を観測する。レイアウト各次元の純データ round-trip は既存 Probe（`ReplayLayoutProbe`・
> `MultiDocLayoutProbe`）が **scene-free / Python-FREE** で証明済みだが、それらは「picker UI + root 配線は HITL」と
> 明記している。本 Journey はその HITL 部分＝**実 `BackcastWorkspaceRoot` を通した配置→Save→Open→復元**を埋める。

## 対象ストーリー（9 ステップ）

1. アプリ起動（Unity）＋ document（`.py`＋sidecar）を File→Open（baseline 配置）
2. 箱庭（Hakoniwa）タイルを並べ替える（base/chart の tile order を非既定へ）
3. Floating window（Order ticket）を移動／前面化する
4. Infinite canvas を pan / zoom する（非既定 view）
5. mode を Replay⇄Live で切替え、各 mode の tile 並びを別 profile として作る（per-mode profile）
6. File→Save（`<strategy>.json` の `layout` キーへ全 4＋1 次元を書き込む。`scenario` キーは保持）
7. File→New で workspace を別状態へ汚す（または別 document を開く）
8. File→Open で同じ document を開き直す
9. 配置が復元される（canvas pan/zoom・箱庭 tile 順・floating window rect/zOrder・notebook cell 位置・
   active mode の profile）

## アーキテクチャ前提

- **`layout` キーは Unity 所有の versioned schema**（`LayoutDocument`・ADR-0003 capability parity、byte-compat
  ではない）。同一 `<strategy>.json` に **engine 所有の `scenario` キー（v3）と共存**する（CONTEXT.md「scenario
  sidecar」・`LayoutSidecarStore` が Newtonsoft merge で相手キーを保持）。
- **配置の 5 次元**は別ディメンション（`LayoutDocument.cs` のコメント）: `hakoniwaProfiles`（per-mode tile 順、
  `panels` は active mode ミラー＆legacy seed）／`canvasView`（pan/zoom）／`floatingWindows`（非 cell window の
  canvas 論理 rect＋zOrder）／`strategyEditors`（#81 で退役・空）／`cellPositions`（cell 順並行の位置）。
- **per-mode profile**（CONTEXT.md「per-mode layout profile」）: Replay と Live が各々の Hakoniwa tile 並び順を
  覚える。`from_mode`: Replay→replay／LiveManual・LiveAuto→**同一 live profile**。canvas/window/editor は **mode
  横断で flat 共有**（per-mode 化しない）。mode 切替は「旧 profile に退避→切替→新 profile を検証 load」。
- 本 Runner は **Python-FREE が既定**（配置 round-trip は描画も kernel も要さない）。`OnFileSave`/`OnFileOpen` の
  mode 副作用（`SetExecutionMode`）は **venue 未接続なら no-op**（`SendModeSideEffect` が null mode をスキップ）
  なので、disconnected な AFK 経路では host を触らない。mode の per-mode profile 切替は host 非依存の
  `HakoniwaLayoutProfiles`／`_baseLive` フラグで駆動できる（**要確認**: footer mode を AFK で切替える反射経路）。

## 操作一覧表（網羅台帳）

| Action ID | ステップ/行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 関連Surface台本 |
|---|---|---|---|---|---|---|
| JOURNEY-LAYOUT-01 | 起動＋document を Open（baseline） | `BackcastWorkspaceRoot.cs:1557` `OnFileOpen` | open 後 `_currentLayoutPath`＝開いた `.py`、geometry は sidecar の `layout` から復元 | StubFileDialog で fixture `.py` を渡し open を反射 invoke | 自動(E2E済・Section1) | [MenuBar](./MenuBarE2ERunner.md) MENU-04 |
| JOURNEY-LAYOUT-02 | 箱庭 tile を並べ替え | `HakoniwaController.cs:129` `Capture` / `BackcastWorkspaceRoot.cs:1729` `StashActiveProfile` | tile order 変更が `CaptureLayout().hakoniwaProfiles`／active `panels` に載る | 並べ替え後 `_hako.Capture().panels` の slot 列を assert | 自動(E2E済・Section1) | [Hakoniwa](./HakoniwaE2ERunner.md) |
| JOURNEY-LAYOUT-03 | Floating window 移動／前面 | `BackcastWorkspaceRoot.cs:1743` `CaptureLayout`（非 cell window） | Order ticket の rect/zOrder が `floatingWindows` に載る（cell window は除外） | window 移動後 `CaptureLayout().floatingWindows` を assert | 自動(E2E済・Section1) | [FloatingWindow](./FloatingWindowE2ERunner.md) |
| JOURNEY-LAYOUT-04 | Infinite canvas pan/zoom | `InfiniteCanvasController.cs:33` `CaptureView` | pan/zoom が `CaptureLayout().canvasView`（panX/panY/zoom）に載る | view 変更後 `canvasView` を assert | 自動(E2E済・Section1) | [InfiniteCanvas](./InfiniteCanvasE2ERunner.md) |
| JOURNEY-LAYOUT-05 | notebook cell 配置 | `BackcastWorkspaceRoot.cs:1754` `ToCellPositions(_coordinator.CapturePositions())` | cell 位置が cell 順並行の `cellPositions` に載る | cell 移動後 `CaptureLayout().cellPositions` を assert | 自動(E2E済・Section1) | [StrategyEditorNotebook](./StrategyEditorNotebookE2ERunner.md) |
| JOURNEY-LAYOUT-06 | mode 切替で per-mode profile | CONTEXT「per-mode layout profile」/ `HakoniwaLayoutProfiles` | Replay と Live が別 tile 順 profile を保持、canvas/window は共有 | mode を Replay⇄Live し各 profile の独立を assert | 自動(E2E済・Section1) | [FooterMode](./FooterModeE2ERunner.md) |
| JOURNEY-LAYOUT-07 | File→Save（layout キー書込） | `BackcastWorkspaceRoot.cs:1598` `OnFileSave`→`TryWriteLayout`→`LayoutSidecarStore.WriteLayout` | `<strategy>.json` に `layout` キーが書かれ、`scenario` キーは保持 | save 後ファイルを再読し layout キー存在＋scenario 非破壊を assert | 自動(E2E済・Section1) | [MenuBar](./MenuBarE2ERunner.md) MENU-05 |
| JOURNEY-LAYOUT-08 | workspace を別状態へ汚す | `BackcastWorkspaceRoot.cs:1526` `OnFileNew` | New で notebook 1 空セル・universe clear・untitled（geometry はリセットしない） | New を invoke し _currentLayoutPath="" を assert（汚し工程） | 自動(E2E済・Section1) | [MenuBar](./MenuBarE2ERunner.md) MENU-02 |
| JOURNEY-LAYOUT-09 | File→Open で開き直し | `BackcastWorkspaceRoot.cs:1569` `LayoutSidecarStore.TryReadLayout`→`1584 ApplyLayout` | TryReadLayout が true（valid layout キー）→ ApplyLayout 駆動 | 同 `.py` を再 open し layoutOk=true を assert | 自動(E2E済・Section1) | [MenuBar](./MenuBarE2ERunner.md) MENU-04 |
| JOURNEY-LAYOUT-10 | canvas 復元観測 | `BackcastWorkspaceRoot.cs:1779` `_canvas.ApplyView` / `InfiniteCanvasController.cs:39` | open 後 canvas の pan/zoom が step 4 の値へ | 復元後 `_canvas.CaptureView()` ≈ 保存値を assert | 自動(E2E済・Section1) | [InfiniteCanvas](./InfiniteCanvasE2ERunner.md) |
| JOURNEY-LAYOUT-11 | 箱庭 tile 順 復元観測 | `BackcastWorkspaceRoot.cs:1784` `HakoniwaLayoutProfiles.FromDocument`→`1785 ApplyProfileOrder` | tile order が step 2 の並びへ（検証 honor：base 集合一致時） | 復元後 `_hako.Order` が保存順と一致を assert | 自動(E2E済・Section1) | [Hakoniwa](./HakoniwaE2ERunner.md) |
| JOURNEY-LAYOUT-12 | floating window 復元観測 | `BackcastWorkspaceRoot.cs:1795` `RestoreFloating` | 既存 window は in-place 再配置、追加 window は spawn、zOrder 昇順→前面 | 復元後 window rect/前後を assert | 自動(E2E済・Section1) | [FloatingWindow](./FloatingWindowE2ERunner.md) |
| JOURNEY-LAYOUT-13 | per-mode profile 復元 | `BackcastWorkspaceRoot.cs:1785` `ApplyProfileOrder(_baseLive)` | active mode の profile を検証 load（base 集合不一致は canonical `Kinds(mode)`） | replay/live 各 active で復元 profile を assert | 自動(E2E済・Section1) | [FooterMode](./FooterModeE2ERunner.md) |
| JOURNEY-LAYOUT-14 | corrupt/no-layout sidecar → bare open | `BackcastWorkspaceRoot.cs:1564-1584`（layoutOk=false 経路） | layout キー無/破損なら ApplyLayout を skip し現 geometry を保持（no-wipe・findings 0048 D4） | scenario-only/破損 sidecar を open し geometry 非破壊を assert | 自動(E2E済・Section2) | [MenuBar](./MenuBarE2ERunner.md) MENU-04 |
| JOURNEY-LAYOUT-15 | Save As で document ペアを fork | `BackcastWorkspaceRoot.cs:1615` `OnFileSaveAs` | `<newname>.py`＋`<newname>.json`（scenario＋layout）生成、旧ペアは独立、`_currentLayoutPath` 更新 | StubFileDialog で新パス→両ファイル生成＋旧非破壊を assert | 自動(E2E済・Section3) | [MenuBar](./MenuBarE2ERunner.md) MENU-06 |

> tile 順／window rect／canvas view／scenario⇄layout 共存の **純データ round-trip** は `ReplayLayoutProbe`
> （panels・Capture/Apply・fallback・Default 一致）と `MultiDocLayoutProbe`（layout-key round-trip・coexist・
> TryReadLayout 厳格性・SaveAs）が AFK 権威。本 Journey はそれを**実 root の配置→Save→Open→復元**へ縫い、
> 「Probe が HITL と切り出した root 配線」を埋める（重複ではなく縫い目を観測）。

## 自動検証する範囲（この Runner がゲートする）

- **steps 2-6（配置→doc）**: 実 `BackcastWorkspaceRoot.CaptureLayout()` が、並べ替えた箱庭 tile 順・移動した
  floating window・pan/zoom した canvas・notebook cell 位置・per-mode profile を **5 次元すべて**で `LayoutDocument`
  へ集約すること。
- **step 7（doc→disk・縫い目）**: `OnFileSave` → `LayoutSidecarStore.WriteLayout` が `<strategy>.json` の `layout`
  キーへ書き、**`scenario` キーを clobber しない**（coexist merge）。
- **steps 8-13（disk→live・縫い目）**: 別状態に汚した後 `OnFileOpen` の `TryReadLayout`→`ApplyLayout` が、live の
  canvas/Hakoniwa/floating/cell/per-mode profile を**保存値へ復元**すること（非デフォルト値が round-trip して live
  UI に届く＝vacuous-round-trip kill の Journey 版）。
- **step 14（no-wipe）**: layout キー無し／破損 sidecar の open は ApplyLayout を skip し、生きている geometry を
  破壊しない（findings 0048 D4）。
- **step 15（fork）**: Save As が 2 ファイルペアを正しく分岐し、旧ペアを独立に保つ。

## 自動検証しない範囲

- **実ピクセルの見た目**（tile/window の実描画位置・pan/zoom 後のフレーミングの美観）。`-nographics` は GPU が無く、
  本 Runner は配置を **データ層**（`Capture()`/`CaptureView()`/`_hako.Order` 等）で観測する。実ピクセルは owner HITL。
  → **HITL専用**（実ウィンドウ・GPU 前提）。
- **EventSystem 実 raycast による drag/click-to-front 操作**（title bar drag の screen→canvas 論理 delta、
  クリックで最前面）。本 Runner は controller API（`_windows.ApplyGeometry`/`BringToFront`・`_canvas.ApplyZoom`）を
  直接駆動する。実マウス drag は owner HITL。→ **HITL専用**（実 EventSystem・実ウィンドウ前提）。
- **OS ネイティブファイルダイアログ**（実 picker の選択操作）。`StubFileDialog` で差し替える。実ダイアログは
  → **HITL専用**（OS ネイティブダイアログ依存）。
- **mode を footer で実操作して per-mode profile を切替える UI 経路**（footer mode picker の実クリック→
  `SetExecutionMode` 受理）。本 Runner は per-mode profile ロジックを `HakoniwaLayoutProfiles`／`_baseLive` で
  駆動する（**要確認**: AFK で footer mode 切替を反射駆動できるか。できなければ profile 層のみを縫い、footer の
  実操作は LiveManualTrade Journey / footer Surface 台本へ寄せる）。

## 観測点（step ごと）

| step | 観測 | 合否の意味 |
|---|---|---|
| 1 | open 後 `_currentLayoutPath`＝fixture `.py`、`CaptureLayout()` が default と差のある baseline を返せる状態 | document が開き配置を持つ |
| 2-5 | `CaptureLayout()` の `hakoniwaProfiles`/active `panels`・`floatingWindows`・`canvasView`・`cellPositions` が**非デフォルト**へ変化 | 5 次元の配置が doc へ集約された |
| 6 | Replay profile と Live profile の tile 順が**独立**（一方の変更が他方に漏れない）、canvas/window は共有 | per-mode profile の分離が成立 |
| 7 | 保存後の `<strategy>.json` に `layout` キーが存在 ∧ 既存 `scenario` キーが読める（`ScenarioSidecarStore.ReadScenario` 非 null） | doc→disk・coexist 非 clobber |
| 8 | `OnFileNew` 後 `_currentLayoutPath==""`・notebook 1 空セル（汚し成立） | workspace が別状態 |
| 9 | 再 open の `TryReadLayout` が true（valid layout キー） | disk→読込が valid |
| 10 | 復元後 `_canvas.CaptureView()` ≈ step 4 の pan/zoom（EPS 一致） | canvas 復元 |
| 11 | 復元後 `_hako.Order` が step 2 の保存順と一致 | tile 順復元（検証 honor） |
| 12 | 復元後 Order ticket window の rect/zOrder が step 3 の保存値 | floating 復元 |
| 13 | active mode の profile が保存 profile（base 集合一致時 honor／不一致は canonical） | per-mode profile 復元 |
| 14 | scenario-only/破損 sidecar の open 後、生きている canvas/tile/window が**不変** | no-wipe 保証 |
| 15 | `<newname>.py`＋`<newname>.json` が生成され、旧 `.py`/`.json` が無改変 | Save As fork |

> **delete-the-production-logic litmus**: `OnFileSave` の `TryWriteLayout` 呼びを消すと step 7 が落ち、`OnFileOpen`
> の `ApplyLayout(doc)` 呼びを消すと steps 10-13 が落ちる。`CaptureLayout` の任意の 1 次元の収集を消すと、その次元の
> step 2-5/復元観測が落ちる（非デフォルト値が round-trip しなくなる）。

## 合格条件

- ログに `[E2E LAYOUT JOURNEY PASS] real workspace round-tripped 5 layout dimensions (canvas/tiles/windows/cells/per-mode profile) through the <strategy>.json layout key and restored them on reopen.`
- プロセス exit code 0（`-quit` 併用、self-failing gate）。`error CS\d+` が 0 件。
- いずれかの観測点を落としたら `[E2E LAYOUT JOURNEY FAIL] <msg>` で exit 1。

## 実行コマンド

```text
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod LayoutPersistenceJourneyE2ERunner.Run -logFile <log>
```

このマシンの Unity: `C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe`。
compile だけ先に通すゲート: `-executeMethod` を外して同コマンド（`error CS\d+` 0 件＋`return code 0`）。
Unity ログは UTF-8 なので **ripgrep で grep**（PowerShell `Select-String` は取りこぼす）。

## 失敗時に確認するログ・代表的な原因

- **`layout key missing after save` / step 7 落ち**: `TryWriteLayout` の例外（`[BackcastWorkspaceRoot] layout
  write failed: …` を確認）。`LayoutSidecarStore.WriteLayout` の merge 失敗・書込権限。
- **`save CLOBBERED the scenario key`**: coexist merge が壊れている（`MultiDocLayoutProbe` S2 と突合）。`JsonUtility`
  で write すると相手キーを silent drop する（CONTEXT.md「scenario sidecar」_Avoid_）。
- **`canvas/tile/window not restored` / steps 10-13 落ち**: `ApplyLayout` が呼ばれていない（layoutOk=false で skip
  された＝TryReadLayout が false。sidecar に valid layout キーが無い）。あるいは `_baseLive` の mode 不一致で
  profile が canonical に落ちた（base 集合検証 `is_valid_for` を確認）。
- **`geometry was wiped by a bare open` / step 14 落ち**: layout キー無し/破損 open が ApplyLayout を呼んだ
  （no-wipe 違反・findings 0048 D4）。`TryReadLayout` の厳格性（empty/version<=0→false）を `MultiDocLayoutProbe`
  S3 と突合。
- **`Save As mutated the OLD file` / step 15 落ち**: `StrategyDocument.SaveAs` / `_coordinator.SaveAs` の独立性違反
  （`MultiDocLayoutProbe` S4 と突合）。
- **per-mode profile が漏れる**: Replay 変更が Live profile に伝播（共有参照リーク）。`_profiles.Set` が `Clone()`
  前に呼ばれていないか・`CaptureLayout` の deep clone（`_profiles.Clone()`）を確認。

## `LayoutPersistenceJourneyE2ERunner.cs` 実装方針（実装済み 2026-06-19・Section1=JOURNEY-LAYOUT-01..13 round-trip 本体 / Section2=14 no-wipe / Section3=15 Save As fork・findings 0065）

- `ReplayToHakoniwaE2ERunner` と同型に **実 `BackcastWorkspaceRoot` を反射合成**（`OpenScene` →
  `SetSynthesizer(FakeMarimoSynthesizer)` → `ResolvePaths` → `BuildWorkspace`、`_font` を builtin に注入）。
  **Python-FREE**（host.InitializePython は呼ばない・配置 round-trip に kernel 不要）。`_isOwner=true` は配置層には
  不要だが、Save/Open の owner ガードがあれば設定する（**要確認**）。
- ファイルダイアログは `StubFileDialog`、File 操作は `OnFileOpen`/`OnFileSave`/`OnFileSaveAs`/`OnFileNew` を反射
  invoke。配置の変更は controller API を反射駆動（`_hako`／`_windows`／`_canvas`／`_coordinator` の private を
  reflection で取得）。
- temp の `<strategy>.json`＋`.py` ペアを用意（`scenario` キー入りで coexist を検証可能に）。書込先は
  `Application.temporaryCachePath` 配下の自前 temp（owner の本番 sidecar に触れない・`ReplayLayoutProbe` 同様）。
- per-mode profile は `HakoniwaLayoutProfiles.FromDocument`／`_baseLive` を反射操作して replay/live を切替える
  （footer の実 UI 操作は使わない・**要確認**: 反射で `_baseLive` を flip しつつ `StashActiveProfile`/`ApplyProfileOrder`
  を駆動する手順）。
- セクション構成は操作一覧表の `自動(*)`／`要新規自動化` 行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを返す
  `Execute()`（null=PASS）パターン。teardown は temp ディレクトリ削除のみ（host を起こさないので Stop 不要）。
