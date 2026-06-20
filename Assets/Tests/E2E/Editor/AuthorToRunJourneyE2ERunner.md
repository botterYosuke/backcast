# AuthorToRunJourneyE2ERunner — 台本（Journey E2E 仕様 / 観測点 / 合格条件）

`AuthorToRunJourneyE2ERunner.cs`（第二波で実装）が自動検証する **Journey E2E** の台本。実装者は `.cs` と本 `.md` を
セットで読む。これは調査メモではなく、**横断ストーリーの仕様・観測点・合格条件を定義する正本**。Action ID 採番・
カバー状態の語彙・責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は
[ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Journey E2E*。ここでは **空ノートブック→セル編集→scenario 設定→保存→
> 戦略パス供給可能→scenario commit→`host.TryStartRun` 受理まで（横断の縫い目）**を観測する。run 開始**後**の
> kernel replay→箱庭更新は [ReplayToHakoniwaE2ERunner](./ReplayToHakoniwaE2ERunner.md) が担うので、本 Journey の
> 終端（run 受理）からそちらへ参照を張る（2 本で「白紙→約定可視化」の全長を覆う）。各 File 操作の単体挙動は
> [MenuBarE2ERunner](./MenuBarE2ERunner.md) を参照。

## 対象ストーリー（9 ステップ）

1. アプリ起動（Unity）
2. File→New（notebook が 1 空セル・untitled・universe/scenario クリア）
3. セル本文を編集する（host-API を使う戦略本体を書く）
4. （任意）セルを追加／削除する
5. scenario を設定する（universe 銘柄追加・start/end・granularity・initial cash）
6. File→Save As で新規パスへ保存する（`.py` 書込・rebind・dirty クリア）
7. 戦略パスが供給可能になる（provider の 5 条件 supplyable）
8. ▶ Run を押す → run gate → scenario sidecar を commit（書込）
9. `host.TryStartRun` が受理して run が始まる（→ ここから箱庭更新は ReplayToHakoniwa 参照）

## アーキテクチャ前提

- **戦略 `.py` の正本は notebook**（#81・marimo cell model）。N 個の cell window が 1 つの `.py` に synth される
  （`NotebookCellCoordinator` が New/Open/Save/Delete を所有）。run が対象にするのは **エディタが見せている物**
  ＝`EditorFileProvider`（`RegistryStrategyFileProvider`→`MarimoNotebookDocument`、WINDOW_ID 固定）。
- **provider は PATH を渡す（buffer ではない）**: engine は disk の `.py` を開く（`_backend_impl._load_strategy`）。
  `IStrategyFileProvider.TryGetStrategyFile` の **supplyable は厳格な 5 条件**（`IStrategyFileProvider.cs`）:
  ①path がバインド済 ②dirty でない ③直近の Open/Save 成功 ④canonical 絶対 `.py` ⑤呼出時にファイル実在。dirty 時は
  stale path ではなく **false** を返す（WYSIWYR）。
- **scenario は engine 所有の v3 dict**（CONTEXT.md「scenario sidecar」）。`<strategy>.json` の `scenario` キーに
  co-locate し、run は **完全に sidecar 駆動**（`start_engine(strategy_file)` が granularity/instruments/start/end/
  cash を全部駆動）。Unity は v3 のみ書く。`scenario` キーと `layout` キーは同一ファイルに共存（LayoutPersistence
  Journey 参照）。
- **run gate の 3 projection**（CONTEXT.md「scenario 編集の 3 projection」）: editing buffer→validated-for-write→
  on-disk。不正な editing buffer は ② へ昇格できず run も persist もしない（AC④）。`ScenarioStartupController.
  TryStartRun` が provider 再照会＋`Commit`（検証通過時のみ sidecar 書込）→ `RunGateResult.Ready`。
- 本 Runner は **steps 2-8 を Python-FREE で観測**できる（New/編集/scenario/Save/provider/commit は host 非依存）。
  **step 9 の `host.TryStartRun` 受理**のみ実 server を要するので、その観測点に限り `host.InitializePython("MOCK")`
  を直呼びする（batchmode の所有権スキップを迂回する正当手・`ReplayToHakoniwaE2ERunner` と同型）。

## 操作一覧表（網羅台帳）

| Action ID | ステップ/行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 関連Surface台本 |
|---|---|---|---|---|---|---|
| JOURNEY-AUTHOR-01 | 起動＋合成 root | `BackcastWorkspaceRoot.cs:295` `BuildWorkspace` 系 | root が build され notebook/scenario/provider が配線済 | reflection で `_coordinator`/`_scenario`/`EditorFileProvider` 取得 | 自動(E2E済・Section1) | — |
| JOURNEY-AUTHOR-02 | File→New（空セル・untitled） | `BackcastWorkspaceRoot.cs:1526` `OnFileNew`→`1542 _coordinator.New()` | notebook 1 空セル・region_002+ despawn・`_scenario.Clear()`・`_currentLayoutPath=""` | New を invoke し空セル＋universe 空＋untitled を assert | 自動(E2E済・Section1) | [MenuBar](./MenuBarE2ERunner.md) MENU-02 |
| JOURNEY-AUTHOR-03 | セル本文を編集 | `NotebookCellCoordinator.cs:24`（cell 編集→notebook dirty） | 編集後 notebook が dirty → provider が **false**（未保存は供給不可） | 本文セット後 `EditorFileProvider.TryGetStrategyFile`==false を assert | 自動(E2E済・Section1) | [StrategyEditorNotebook](./StrategyEditorNotebookE2ERunner.md) |
| JOURNEY-AUTHOR-04 | セル追加／削除 | `NotebookCellCoordinator.cs:92` `DeleteCell` / 追加 | cell window 再構築・cell 順更新（adopt 不変条件：region_001 は破棄せず） | add/delete 後 cell 数と order を assert | 自動(E2E済・Section1) | [StrategyEditorNotebook](./StrategyEditorNotebookE2ERunner.md) |
| JOURNEY-AUTHOR-05 | universe 銘柄を追加 | `ScenarioStartupController.cs:99` `AddInstrument` | universe SoT（`InstrumentRegistry`）に id 追加 → chart tile spawn（#60 universe→tile） | `AddInstrument("8918.TSE")` 後 `Universe.Ids`＋`_chartViews` を assert | 自動(E2E済・Section1) | [UniverseSidebar](./UniverseSidebarE2ERunner.md), [ReplayToHakoniwa](./ReplayToHakoniwaE2ERunner.md) steps 2-3 |
| JOURNEY-AUTHOR-06 | scenario 期間/granularity/cash 編集 | `ScenarioStartupController.cs:95-98` `SetStart`/`SetEnd`/`SetGranularity`/`SetInitialCash` | Params が editing buffer に入り `Validate()` が errors 無し | 妥当値セット後 `Validate().Any`==false を assert | 自動(E2E済・Section1) | [ScenarioStartup](./ScenarioStartupE2ERunner.md) |
| JOURNEY-AUTHOR-07 | File→Save As（新規パスへ） | `BackcastWorkspaceRoot.cs:1615` `OnFileSaveAs`→`1625 _coordinator.SaveAs` | `<newname>.py` 書込・notebook rebind・dirty クリア・`_currentLayoutPath` 更新 | StubFileDialog で新パス→`.py` 生成＋rebind を assert | 自動(E2E済・Section1) | [MenuBar](./MenuBarE2ERunner.md) MENU-06 |
| JOURNEY-AUTHOR-08 | 戦略パスが供給可能 | `IStrategyFileProvider.cs:20` / `RegistryStrategyFileProvider.cs:30` | Save 後 provider が **true**＋canonical 絶対 `.py`（5 条件すべて成立） | save 後 `EditorFileProvider.TryGetStrategyFile(out p)`==true ∧ p==保存先 を assert | 自動(E2E済・Section1) | [StrategyEditorNotebook](./StrategyEditorNotebookE2ERunner.md) |
| JOURNEY-AUTHOR-09 | ▶ Run gate→scenario commit | `BackcastWorkspaceRoot.cs:842` `_scenario.TryStartRun(EditorFileProvider)`→`ScenarioStartupController.cs:130` | gate Ready・`Commit` が `<strategy>.json` の `scenario` v3 を書く・`StrategyPath` 返却 | TryStartRun→Ready＋sidecar の scenario キー（ReadScenario）を assert | 自動(E2E済・Section1) | [ScenarioStartup](./ScenarioStartupE2ERunner.md), [StrategyEditorNotebook](./StrategyEditorNotebookE2ERunner.md) |
| JOURNEY-AUTHOR-10 | `host.TryStartRun` 受理（run 開始） | `BackcastWorkspaceRoot.cs:861-869` `RunRequest`→`_host.TryStartRun(req)` | serverReady/running guard を通過し run 受理（true） | `host.InitializePython("MOCK")` 後 OnRun 経由で TryStartRun==true を assert | 自動(E2E済・Section1) | [ReplayToHakoniwa](./ReplayToHakoniwaE2ERunner.md) step 5 |
| JOURNEY-AUTHOR-11 | 不正 scenario で run 拒否 | `ScenarioStartupController.cs:141` `Commit` 失敗 → `BlockedInvalidScenario` | 空 universe / 不正期間で gate が `BlockedInvalidScenario`・sidecar 不変・host 未起動 | 不正 buffer で TryStartRun→IsReady==false を assert | 自動(E2E済・Section2) | [ScenarioStartup](./ScenarioStartupE2ERunner.md) |
| JOURNEY-AUTHOR-12 | dirty editor で run 拒否 | `ScenarioStartupController.cs:132` provider false → `BlockedNoStrategy` | 未保存編集で provider false → gate `BlockedNoStrategy`・run 起動せず | 編集後（save 前）TryStartRun→`BlockedNoStrategy` を assert | 自動(E2E済・Section2) | [StrategyEditorNotebook](./StrategyEditorNotebookE2ERunner.md) |
| JOURNEY-AUTHOR-13 | run 中 kernel→箱庭更新 | （参照） | per-bar streaming→chart tile 更新 | — | 自動(E2E済・ReplayToHakoniwa) | [ReplayToHakoniwa](./ReplayToHakoniwaE2ERunner.md) steps 6-7 |

## 自動検証する範囲（この Runner がゲートする）

- **step 2（白紙）**: `OnFileNew` が notebook を 1 空セルへ・universe/scenario をクリアし untitled へ落とす
  （adopt 不変条件＝region_001 は破棄せず in-place）。
- **steps 3-4（オーサリング）**: cell 編集で notebook が dirty になり、**dirty 中は provider が供給不可（false）**＝
  WYSIWYR の核（編集途中の戦略は run できない）。cell add/delete が cell order を更新する。
- **steps 5-6（scenario）**: universe 追加が SoT に届き **chart tile を spawn**（#60 の universe→tile 配線）、期間/
  granularity/cash の妥当値が `Validate()` を通る。
- **steps 6-8（保存→供給可能・縫い目）**: `Save As` が `.py` を書き dirty をクリアした瞬間、**provider が false→true へ
  反転**し canonical 絶対パスを返す（5 条件成立）。
- **step 9（commit→受理・縫い目）**: `TryStartRun(provider)` が Ready で `scenario` v3 を sidecar に書き、`OnRun` が
  その `StrategyPath`＋universe/期間/granularity で `RunRequest` を組み `host.TryStartRun` が**受理**する。
- **拒否経路**: 不正 scenario（step 11）と dirty editor（step 12）で run が**起動しない**（AC④ の (1)→(2) ゲート）。

## 自動検証しない範囲

- **marimo cell 合成の Python 経路**（cell ソース→`.py` の実 synth）。本 Runner は `FakeMarimoSynthesizer`（Python-free）
  を使う（`BackcastWorkspaceProbe` S9/S11 が実合成をカバー）。→ **対象外**（既存 Probe が担当・本 Journey は配線縫い目）。
- **エディタの実テキスト入力・キャレット・undo/redo**（uGUI InputField の実打鍵）。本 Runner は coordinator/document の
  API で本文をセットする。実打鍵は → **HITL専用**（実 EventSystem・実ウィンドウ前提）。
- **run 中の kernel replay→state→箱庭描画**（per-bar streaming・実描画）。→ **対象外**（[ReplayToHakoniwa](./ReplayToHakoniwaE2ERunner.md)
  steps 6-7 が自動検証済。本 Journey は step 9＝受理までで縫い、そこへ参照を張る）。
- **OS ネイティブファイルダイアログ**の実選択。`StubFileDialog` で差し替える。→ **HITL専用**（OS ダイアログ依存）。
- **データ忠実性**（実 catalog/DuckDB の中身の正しさ）。本 Runner は run の**受理**までを観測し、実データは見ない。
  → **対象外**（owner mount に触れない）。

## 観測点（step ごと）

| step | 観測 | 合否の意味 |
|---|---|---|
| 2 | New 後 cell 数==1（空）・`_scenario.Universe.Count==0`・`_currentLayoutPath==""` | 白紙＋untitled 成立 |
| 3 | cell 本文セット後 `EditorFileProvider.TryGetStrategyFile(out _)`==false | 未バインドは供給不可（条件①/③ — この時点の notebook は untitled で `_path==null`。dirty 由来の false は step 12） |
| 4 | cell add→数+1、delete→数-1、order 整合（region_001 残存） | cell 集合操作が破綻しない |
| 5 | `Universe.Ids` に追加 id ∧ `_chartViews` に同 id の chart tile | universe→tile 配線（#60） |
| 6 | `Validate().Any`==false（妥当 scenario） | scenario が validated-for-write 可能 |
| 7-8 | Save As 後 `_currentLayoutPath`==新 `.py` ∧ provider true ∧ 返却 path==新 `.py`（絶対・実在・not dirty） | 保存で供給可能へ反転（縫い目） |
| 9 | `TryStartRun(provider).IsReady`==true ∧ sidecar に `scenario`（v3）キー（`ReadScenario` 非 null・instruments 一致） | gate Ready＋commit |
| 10 | `host.InitializePython("MOCK")` 後 `OnRun`→`host.TryStartRun(req)`==true（受理） | run 開始の受理（縫い目終端） |
| 11 | 空 universe で `TryStartRun`→`BlockedInvalidScenario`・sidecar 不変 | 不正 scenario は起動しない |
| 12 | 未保存（dirty）で `TryStartRun`→`BlockedNoStrategy` | WYSIWYR：編集途中は起動しない |

> **delete-the-production-logic litmus**: `OnFileSave`/`SaveAs` の `_coordinator.Save*()`（dirty クリア）を消すと
> steps 7-8 が落ちる（provider が true へ反転しない）。`TryStartRun` の `Commit` 呼びを消すと step 9 の sidecar
> 観測が落ちる。provider の dirty ガード（条件②）を消すと **step 12** が落ちる（dirty でも true を返してしまう）。
> （step 3 の false は未バインド由来＝条件①/③ なので条件②削除では落ちない。）

## 合格条件

- ログに `[E2E AUTHOR→RUN PASS] blank notebook → authored cell + scenario → save made the strategy supplyable → run gate committed the scenario sidecar → host.TryStartRun accepted the run.`
- プロセス exit code 0（`-quit` 併用、self-failing gate）。`error CS\d+` が 0 件。
- いずれかの観測点を落としたら `[E2E AUTHOR→RUN FAIL] <msg>` で exit 1。

## 実行コマンド

```text
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod AuthorToRunJourneyE2ERunner.Run -logFile <log>
```

このマシンの Unity: `C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe`。
compile だけ先に通すゲート: `-executeMethod` を外して同コマンド（`error CS\d+` 0 件＋`return code 0`）。
Unity ログは UTF-8 なので **ripgrep で grep**（PowerShell `Select-String` は取りこぼす）。

## 失敗時に確認するログ・代表的な原因

- **`provider still false after save` / steps 7-8 落ち**: `_coordinator.SaveAs/Save` が dirty をクリアしていない、
  または書込先が canonical 絶対 `.py` でない／実在しない（5 条件の ②④⑤）。`IStrategyFileProvider.cs` の契約と突合。
- **`run gate not Ready` / step 9 落ち**: `Commit` が false（scenario が validated-for-write を通らない＝universe 空・
  期間/cash 不正）。`Validate()` の errors を確認。あるいは provider が false（step 7 が未達）。
- **`scenario sidecar missing the scenario key`**: `Commit` の `ScenarioSidecarStore.SetStartupParamsAndInstruments`
  が書けていない。`<strategy>.json` を直接読み `scenario.schema_version==3` を確認（Unity は v3 のみ書く）。
- **`host.TryStartRun refused` / step 10 落ち**: server 未 ready（`InitializePython("MOCK")` 失敗）か running guard。
  `host.ServerReady` と既存 run の有無を確認（`ReplayToHakoniwaE2ERunner` の同 guard と突合）。
- **`run started despite invalid scenario / dirty editor` / steps 11-12 落ち**: (1)→(2) ゲートの漏れ。`TryStartRun`
  が provider 再照会と `Commit` 検証を両方通しているか（gate を bypass して `host.TryStartRun` を直叩きしていないか）。
- **chart tile が出ない（step 5）**: universe→tile 配線（#60）の不整合。`ReplayToHakoniwaE2ERunner` の steps 2-3 と
  同じ `_chartViews` 観測で突合。

## 将来の `AuthorToRunJourneyE2ERunner.cs` 実装方針

- `ReplayToHakoniwaE2ERunner` と同型に **実 `BackcastWorkspaceRoot` を反射合成**（`OpenScene` →
  `SetSynthesizer(FakeMarimoSynthesizer)` → `ResolvePaths` → `BuildWorkspace`、`_font` を builtin に注入）。
  **steps 2-9 は Python-FREE**、**step 10 の `host.TryStartRun` 受理**でのみ `host.InitializePython("MOCK")` を直呼び
  （所有権スキップ迂回・`_isOwner=true` を設定）。
- ファイルダイアログは `StubFileDialog`（Save As の保存先を temp `.py` に差し替え）。File 操作は `OnFileNew`/
  `OnFileSaveAs`/`OnRun` を反射 invoke。cell 編集／scenario 設定は `_coordinator`／`_scenario` の API を反射駆動。
- provider は `EditorFileProvider`（root の private property）を reflection で取得し `TryGetStrategyFile` を直叩き
  （5 条件の反転を save 前後で観測）。
- temp の作業ディレクトリ（`Application.temporaryCachePath` 配下）に `.py`/`.json` を吐き、owner の本番 sidecar に
  触れない。scenario の妥当値は `ScenarioStartupValidation` の要件（universe≥1・start≤end・cash>0 等）に合わせる
  （**要確認**: 具体的な validation 規則は `ScenarioStartupValidation` を読んで合わせる）。
- セクション構成は操作一覧表の行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを返す `Execute()`（null=PASS）
  パターン。teardown は `host?.Stop()`（MOCK を起こした step 10 のみ）＋temp 削除。
