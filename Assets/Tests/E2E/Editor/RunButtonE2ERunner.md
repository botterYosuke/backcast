# RunButtonE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`RunButtonE2ERunner.cs`（第二波で実装）が自動検証する **Strategy Editor title-bar Run ボタン サーフェス**
（再生 ▶・run lifecycle の起動ゲート）の台本。実装者は `.cs` と本 `.md` をセットで読む。これは調査メモではなく、
**この サーフェスでユーザーができる行動すべての網羅台帳と、E2E の観測点・合格条件を定義する正本**。Action ID 採番・
カバー状態の語彙・セクション構成・責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)
（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（1サーフェスでユーザーができる操作を網羅する回帰ゲート）。
> 「▶ クリック後に kernel が replay → 箱庭（Hakoniwa）を bar-by-bar 更新する」縦串は *Journey E2E*
> （[`ReplayToHakoniwaE2ERunner`](./ReplayToHakoniwaE2ERunner.md)）の責務。本 Surface 台本は「▶ が正しいゲート評価と
> `host.TryStartRun` 呼び出しを起こすか」までに絞る。scenario フィールドの編集・検証は
> [ScenarioStartupE2ERunner](./ScenarioStartupE2ERunner.md)、provider の supplyable 契約は
> [StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) が担う。

## 対象サーフェス

採用された（adopted）Strategy Editor floating-window タイトルバー上の単一 ▶ Run ボタン（#76 S6b-β-clean U1・
findings 0046）。uGUI ビュー `StrategyEditorRunButton`（PLAIN C# builder）＋頭脳 `RunReadinessViewModel`（pure・
no UnityEngine）＋ host 配線 `BackcastWorkspaceRoot.OnRun`。marimo RunButton 原則: Run は**エディタが映すもの**を
対象（常に `NOTEBOOK_ID` の `.py`・`RegistryStrategyFileProvider`）にし、block reason を隣に出す。Run を持つのは
adopted editor だけ（二次 spawn セルは Run 無し＝後 slice）。

## 対象ユーザー行動

▶ クリックで run 開始（scenario を sidecar へ commit → ゲート検証 → `host.TryStartRun`）、ボタンの enable/disable
（owner / not running / strategy 供給可能 / scenario valid の 4 ゲート）、block reason ラベル表示（read-only=観測点・
greyed Run が無言にならない）。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| RUN-01 | ▶ クリックで run 開始（happy path） | `StrategyEditorRunButton.cs:57`→`_onRun`→`BackcastWorkspaceRoot.cs:833`→`OnRun` | `_scenario.TryStartRun(EditorFileProvider)`→Ready→sidecar commit→`PrimeWritebackFromCurrent`→`RunRequest` 組立→`_host.TryStartRun(req)` | MOCK host で全ゲート pass を仕込み `OnRun` 反射 invoke→`host.TryStartRun` 受領＋`req`（instruments/start/end/gran/StrategyPath）一致を assert | 要新規自動化 | `WorkspaceUiCutoverProbe`(S2) |
| RUN-02 | ▶ enable/disable（4 ゲート評価） | `BackcastWorkspaceRoot.cs:879`→`Update`→`RunReadinessViewModel.cs:33`→`Evaluate` | gate 順 running→no-strategy→invalid-scenario→not-owner、全 pass のみ `CanRun=true`→`Refresh(vm)` で `_btn.interactable`＋alpha | `RunReadinessViewModel.Reason/Evaluate` の真理値表（純ロジック）を直接 assert | 自動(Probe有・要昇格) | `WorkspaceUiCutoverProbe`(S1) |
| RUN-03 | block reason ラベル表示 | `StrategyEditorRunButton.cs:77`→`Refresh`→`_status` | `vm.BlockReason` を `_status.text` に出し `enabled` 切替（Running/NoStrategy/InvalidScenario/NotOwner の単一語彙） | `Refresh(vm)` 後の `_status.text`/`.enabled` を reflection で assert（read-only 観測点） | 要新規自動化 | `WorkspaceUiCutoverProbe`(S1) |
| RUN-04 | ▶ が running 中はクリックしても起動しない | `BackcastWorkspaceRoot.cs:835`（`_host.IsRunning` early-return）＋`RunReadinessViewModel.cs:42` | running 中は `CanRun=false`＋reason `Running…`、`OnRun` も先頭で return（多重起動防止） | running フラグ立て→`Reason` が `Running` ＆ `OnRun` が `host.TryStartRun` を呼ばないを assert | 自動(Probe有・要昇格) | `WorkspaceUiCutoverProbe`(S1) |
| RUN-05 | strategy 未供給で Run ブロック | `BackcastWorkspaceRoot.cs:842`→`TryStartRun`→`ScenarioStartupController.cs:132`（provider false） | supplyable provider 0 → `BlockedNoStrategy`＋`NoStrategy` 文言、`host.TryStartRun` 不呼出（CONTEXT「active strategy 選択」） | unbound/dirty editor で `OnRun`→`ShowMessage(NoStrategy)`・host 不呼出を assert | 自動(Probe有・要昇格) | `ScenarioStartupProbe`(S5) `WorkspaceUiCutoverProbe`(S1) |
| RUN-06 | scenario 不正で Run ブロック | `BackcastWorkspaceRoot.cs:844`→`gate.IsReady` false（`Commit` false） | 供給可能だが scenario 不正 → `BlockedInvalidScenario`＋`InvalidScenario` 文言、sidecar 不変・host 不呼出（AC④） | 空 universe 等で `OnRun`→`ShowMessage(InvalidScenario)`・host 不呼出を assert | 自動(Probe有・要昇格) | `ScenarioStartupProbe`(S5) |
| RUN-07 | 非 owner で Run ブロック | `BackcastWorkspaceRoot.cs:853`（`!_isOwner`）＋`RunReadinessViewModel.cs:45` | readiness は毎フレーム owner ガード前に評価され、非 owner は Run greyed＋`NotOwner`、click-time も防御 return | `_isOwner=false` で `Reason==NotOwner`＋`OnRun` が host 不呼出を assert | 自動(Probe有・要昇格) | `WorkspaceUiCutoverProbe`(S1) |
| RUN-08 | 単一 Run 入口の構造（タイトルバーに ▶・footer/tile に無し） | `BackcastWorkspaceRoot.cs:408`→`new StrategyEditorRunButton(OnRun)`／`:409` Build | adopted editor タイトルバーに `RunButton` GameObject、footer に transport ボタン無し、startup tile に Run 無し（U1/U4/U5） | 実 scene 合成で `_editorRunButton` 構築＋`RunButton` 子・footer/tile 不在を assert | 自動(Probe有・要昇格) | `WorkspaceUiCutoverProbe`(S2) |
| RUN-09 | commit I/O 例外/race を click-time に surface | `BackcastWorkspaceRoot.cs:843`（try/catch）／`:844` | commit 例外は `ShowMessage("Could not save scenario: …")`、race で readiness が反転しても `gate.IsReady` false で防御 | commit を投げる stub で `OnRun`→notice 表示＋host 不呼出を assert（防御経路） | 要新規自動化 | — |
| RUN-10 | readiness 変化時のみ uGUI Refresh（per-frame churn 抑制） | `BackcastWorkspaceRoot.cs:881-882`（`readySig` 変化 gate） | `CanRun|BlockReason` の signature が変わったときだけ `_editorRunButton.Refresh`（steady workspace は read のみ） | signature 不変で `Refresh` を呼ばない（変化時のみ呼ぶ）を観測 | 要新規自動化 | — |
| RUN-11 | ▶ クリックの実 enable/grey・色/alpha の視覚 | `StrategyEditorRunButton.cs:82-84`（interactable/alpha） | greyed 時 alpha 0.35・有効時 1.0、reason ラベルの実 truncate/wrap、タイトルバー drag が下層へ透過 | — | HITL専用（実ピクセルの美観・EventSystem raycast・GPU/実ウィンドウ前提） | — |
| RUN-12 | ▶ → kernel replay → 箱庭 bar-by-bar 更新 | `BackcastWorkspaceRoot.cs:869`→`_host.TryStartRun` 以降 | run 開始後の replay clock・per-bar state stream・Hakoniwa tile 更新 | — | 対象外（複数サーフェスをまたぐ縦串＝Journey の責務。[ReplayToHakoniwaE2ERunner](./ReplayToHakoniwaE2ERunner.md) が正本。本 Surface は host 呼出まで） | `ReplayToHakoniwaE2ERunner` |

> block reason ラベル（`_status`）と enable 状態は入力のない**表示**だが、greyed Run の理由を必ず出す（無言禁止）
> ので RUN-02/03〜07 の観測点として確認する。block 文言の単一語彙は `RunReadinessViewModel` の const
> （`Running`/`NoStrategy`/`InvalidScenario`/`NotOwner`）で、`ScenarioStartupController.TryStartRun` と `OnRun` が参照
> （steady title-bar reason と click-time message のドリフト無し）。

## 観測点（詳細）

- **RUN-02/04〜07（4 ゲート）**: `RunReadinessViewModel.Reason(isOwner, isRunning, strategyReady, scenarioValid)` の
  gate 順 = `OnRun` の順（running → no-strategy → invalid-scenario → not-owner）。VM は**非 mutating** な readiness 読み
  （`OnRun` の `TryStartRun` は sidecar を commit する mutating なので click 時に残す）。`WorkspaceUiCutoverProbe`
  Section1 が真理値表＋precedence＋`Evaluate`→`CanRun`/`BlockReason` の正本。
- **RUN-01/05/06/09（click-time ゲート）**: `OnRun` は ①`IsRunning` early-return → ②`TryStartRun`（commit 含む）→
  ③`gate.IsReady` 判定 → ④`PrimeWritebackFromCurrent` → ⑤`!_isOwner` 防御 → ⑥`RunRequest` 組立 → ⑦`host.TryStartRun`。
  `ScenarioStartupController.TryStartRun`（`ScenarioStartupProbe` Section5）が `BlockedNoStrategy`/`BlockedInvalidScenario`/
  `Ready` の振り分けを既に assert。Surface 側で残るのは「`OnRun` が gate 結果に応じて host を呼ぶ/呼ばない・notice を
  出す」配線で、**要新規自動化**（MOCK host で host 呼出有無を観測）。
- **RUN-08（単一 Run 入口）**: `WorkspaceUiCutoverProbe` Section2 が U1（タイトルバーに `RunButton`）/U4（footer に
  transport ▶/⏸/⏭/⏹/速度ボタン無し）/U5（startup tile に Run 無し）を実 scene 合成で assert。
- **RUN-12（縦串は Journey）**: ▶ 以降の replay→箱庭は本 Surface の責務外。`host.TryStartRun(req)` が正しい `RunRequest`
  で呼ばれるところまでを観測し、その先は `ReplayToHakoniwaE2ERunner` へ参照を張る（責務境界・E2E-CONVENTIONS §3）。

## 自動判定（合格条件）

- ログに `[E2E RUN BUTTON PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+` が 0 件。
- 各 `自動(*)` 行の観測点を 1 つでも落としたら `[E2E RUN BUTTON FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `RunReadinessViewModel.Reason` の gate 順を入れ替えると RUN-04〜07 の precedence が
  落ちる／`OnRun` の `gate.IsReady` 分岐を消すと RUN-05/06 で host が誤って呼ばれて落ちる／`OnRun` 先頭の
  `_host.IsRunning` return を消すと RUN-04 の多重起動防止が落ちること。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `WorkspaceUiCutoverProbe` S1 | EditMode・pure VM | RUN-02/03/04/07 の readiness 真理値表・precedence の昇格元 |
| `WorkspaceUiCutoverProbe` S2 | batchmode・root 合成 | RUN-08 の単一 Run 入口（U1/U4/U5 構造）の正本 |
| `ScenarioStartupProbe` S5 | pure controller | RUN-05/06 の `TryStartRun` ゲート振り分け（BlockedNoStrategy/InvalidScenario/Ready） |
| `ReplayToHakoniwaE2ERunner` | Journey | RUN-12（▶→replay→箱庭）の縦串の正本。本 Surface は参照のみ |

## 将来の `RunButtonE2ERunner.cs` 実装方針（第二波）

- pure 行（RUN-02/03/04/07）は `RunReadinessViewModel` を直接駆動（`WorkspaceUiCutoverProbe` Section1 を昇格）。
- host 配線行（RUN-01/05/06/09）は `WorkspaceUiCutoverProbe` の `ComposeRoot` と同型に **実 `BackcastWorkspaceRoot` を
  反射合成**（`OpenScene`→`SetSynthesizer(FakeMarimoSynthesizer)`→`ResolvePaths`→`BuildWorkspace`）し、`_host` を
  **MOCK/spy host** に差し替えて `OnRun` を反射 invoke、`TryStartRun(req)` の受領と `req` フィールドを assert。
  Python kernel が要るなら `host.InitializePython("MOCK")` を直呼び（batchmode 所有権スキップを迂回する正当手・
  `ReplayToHakoniwaE2ERunner` と同型）だが、本台本は host 呼出までなので原則 Python-FREE。
- uGUI 行（RUN-03/08）は `StrategyEditorRunButton` の `_btn`/`_status`/`_btnBg` を reflection で観測、`Refresh(vm)` を駆動。
- セクション構成は操作一覧表の `自動(*)`/`要新規自動化` 行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを返す
  `Execute()`（null=PASS）パターン。teardown は `host?.Stop()`（MOCK を起こした場合のみ）＋spawned の `DestroyImmediate`。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod RunButtonE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep で grep**（PowerShell `Select-String` は取りこぼす）。
