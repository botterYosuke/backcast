# ScenarioStartupE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`ScenarioStartupE2ERunner.cs`（第二波で実装済み・全 14 section AFK GREEN＝#128/ADR-0026 で S13=SCENARIO-16/S14=SCENARIO-17 追加）が自動検証する **Scenario Startup tile サーフェス**（Replay 実行設定
パネル）の台本。実装者は `.cs` と本 `.md` をセットで読む。これは調査メモではなく、**この サーフェスでユーザーが
できる行動すべての網羅台帳と、E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション
構成・責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は
[ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（1サーフェスでユーザーができる操作を網羅する回帰ゲート）。
> 「scenario を commit → run → kernel replay → 箱庭更新」の縦串は *Journey E2E*（`ReplayToHakoniwaE2ERunner`）が担う。
> 本 Surface 台本は「フィールド編集が正しい 3 projection 状態遷移・検証・persist を起こすか」までを観測する。
> ▶ Run の起動ゲートは **per-cell RUN**（[StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) の STRATEGY-19/20）の責務（旧 `RunButtonE2ERunner` は #95 Phase 6 global ▶ Run sunset で retire・findings 0075 §3c。本台本は editing→validated-for-write の
> ゲートと「不正値は run を起動しない」前提までを観測点に持つ）。

## 対象サーフェス

**Settings ダイアログの Scenario セクションが宿す `ScenarioStartupTile`**（#128/ADR-0026 で移設。旧: dock `KIND_STARTUP` window／さらに旧: Hakoniwa `PanelKind::Startup` タイル slot 0・TTWR `populate_startup_tile` parity）。uGUI ビュー `ScenarioStartupTile`
＋頭脳 `ScenarioStartupController`（input-agnostic な PLAIN C#）。#76 S6b-β-clean U5 でタイルは **scenario 編集専用**に
なり、Run ボタン＋run-readiness は Strategy Editor タイトルバーへ移動済み（タイルは scenario フィールドと per-field
エラーラベルだけを持つ）。4 フィールド = Start / End / Granularity / Initial cash（CONTEXT「run 期間 vs lookback」）。
universe（instruments）は**第 5 フィールドではなく独立 SoT**（`InstrumentRegistry`・CONTEXT「universe registry vs
scenario panel」）で、タイルは最小テキスト入力（カンマ/空白区切り→ReplaceAll）として差し込む。

## 対象ユーザー行動

Start/End 日付編集（検証・Dirty）、Daily/Minute granularity 切替、Initial cash 編集、Universe 編集
（カンマ/空白区切り→ReplaceAll→Dirty）、Universe フィールド blur（onEndEdit）で SoT 再 pull（stale 上書き防止）、
共有 SoT が外部（#31 sidebar/picker）で変わったときの held-mode 再 sync、per-field エラーラベル（read-only=観測点）、
editing buffer→validated-for-write のゲート（「不正値は run を起動しない」=AC④）。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| SCENARIO-01 | Start 日付を編集 | `ScenarioStartupTile.cs:64`→`SetStart`(`ScenarioStartupController.cs:95`) | `Params.Start` 更新＋`Dirty=true`、`Refresh()`→`_startErr` 再導出（空/書式/cross-field） | 反射で `SetStart` 駆動→`Params.Start`・`Dirty`・`Validate().Start` を assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S2,S5) |
| SCENARIO-02 | End 日付を編集 | `ScenarioStartupTile.cs:66`→`SetEnd`(`:96`) | `Params.End` 更新＋Dirty、`_endErr` は `End ?? CrossField`（start>end） | `SetEnd` 後の Dirty・`Validate().End`/`.CrossField` を assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S2,S5) |
| SCENARIO-03 | Granularity を Daily/Minute 切替 | `ScenarioStartupTile.cs:70-71`→`SetGranularity`(`:97`) | `Params.Granularity` 更新＋Dirty、選択ボタン `Highlight`（element_selected）、未選択は `Validate().Granularity` エラー | `SetGranularity` 後の値・Dirty・選択 highlight・None 時のエラーを assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S2,S5) |
| SCENARIO-04 | Initial cash を編集 | `ScenarioStartupTile.cs:74`→`SetInitialCash`(`:98`) | `Params.InitialCash` 更新＋Dirty、負/零/非整数は `Validate().InitialCash` エラー | `SetInitialCash` で `-5`/`0`/`abc` がエラー・正値が無エラーを assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S2,S5) |
| SCENARIO-05 | Universe を編集（テキスト→ReplaceAll） | `ScenarioStartupTile.cs:112`→`OnUniverseChanged`→`Universe.ReplaceAll` | カンマ/空白/タブ/改行で split→trim→`ReplaceAll`（dedup・順序保持）＋`Params.Dirty=true`、空 universe は `Validate().Universe` エラー | raw 文字列を `OnUniverseChanged` へ→`Universe.Ids`（順序・dedup）・Dirty・空時エラーを assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S3,S5) |
| SCENARIO-06 | Universe フィールド blur で SoT 再 pull | `ScenarioStartupTile.cs:82`→`onEndEdit`→`PullUniverseField` | blur 時に SoT から field を再 pull（**stale 文字列を ReplaceAll しない**）→次キーで stale 上書きを防止（findings 0025 §12 Finding2） | field を stale 化→`onEndEdit.Invoke`→field が SoT に一致・registry 不変（ReplaceAll しない）を assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S8) |
| SCENARIO-07 | 共有 SoT が外部編集されたら held-mode 再 sync | `ScenarioStartupTile.cs:128`→`OnUniverseRegistryChanged`（`Universe.Changed` 購読） | 非フォーカス時のみ `PullUniverseField`、フォーカス中は rewrite skip（live editor 保護）、常に `Refresh()` でエラー再導出 | sidebar 相当の `Universe.Add/Remove`→field が追従、`Dispose` 後は追従しないを assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S6,S7) |
| SCENARIO-08 | per-field エラーラベル表示 | `ScenarioStartupTile.cs:145`→`Refresh`→`Validate`→`SetErr` | 各フィールドのエラー文字列が対応ラベルに出る/消える（read-only 観測点・入力ではない） | 不正/正 buffer で各 `_*Err.enabled`/`.text` を反射 assert（`Validate` は純ロジックで直接 assert 可） | 自動(E2E済) | `ScenarioStartupE2ERunner`(S2) |
| SCENARIO-09 | editing→validated-for-write ゲート（不正値は run 起動しない） | `ScenarioStartupController.cs:111`→`Commit`→`TryBuildForWrite` | 不正 buffer は ② へ昇格できず sidecar 不変・`Commit` false（AC④ の (1)→(2) ゲート、CONTEXT「3 projection」） | 不正 buffer で `Commit` false・on-disk 不変、正 buffer で sidecar 書込＋Dirty クリアを assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S2,S5) |
| SCENARIO-10 | Populate（sidecar/inline/seed の優先） | `ScenarioStartupController.cs:46`→`Populate`/`PopulateFrom`(`:56`) | sidecar > inline fallback > seed（start=end−3mo/end=今日）、Dirty 中は再 sync を guard（in-flight 編集を守る） | 各優先順位・dirty-guard・seed 既定値を assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S4,S5) |
| SCENARIO-11 | Commit が sidecar を merge 書き（兄弟キー保全） | `ScenarioStartupController.cs:120`→`SetStartupParamsAndInstruments` | v3 optional（`account_type`/`strategy_init_kwargs` nested/`instruments_ref`）と `layout` キーを保ったままマージ、start/end/cash/instruments を atomic co-write | seed→commit→on-disk TEXT に新値あり＋未編集兄弟（nested dict 含む）verbatim 残存を assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S1,S9) |
| SCENARIO-12 | File→New で scenario を in-memory クリア | `ScenarioStartupController.cs:87`→`Clear` | editing buffer を空 unbound へ・universe 空・errors クリア（on-disk sidecar は触らない・dirty-guard 非適用） | `Clear` 後に Params 空・Universe 空・Dirty=false・on-disk sidecar byte 不変を assert | 自動(E2E済) | `ScenarioStartupE2ERunner`(S11) |
| SCENARIO-13 | theme 切替でタイル再描画 | `ScenarioStartupTile.cs:161`→`ApplyTheme` | 保持グラフィックを active theme で再塗り＋`Refresh()`（granularity 選択 highlight 再導出） | — | HITL専用（実ピクセルの美観・GPU/実ウィンドウ前提） | — |
| SCENARIO-14 | フィールドのフォーカス/IME/caret 編集フィール | `ScenarioStartupTile.cs:203`（uGUI InputField boundary） | 実キー入力→InputField.text→onValueChanged、IME 合成、blur イベント発火タイミング | — | HITL専用（uGUI InputField・IME・実フォーカス遷移） | — |
| SCENARIO-15 | startup タイルに Run 起動ボタンが無いこと（#76 S6b-β-clean／Phase 6 global ▶ Run sunset の cutover 負 invariant・RunButton retire から U5 re-home） | `ScenarioStartupTile.Build`（MakeButton が `btn:`+label 命名） | タイル配下に `btn:Run Replay` 等 run-trigger ボタンが無いこと（granularity ボタン存在を non-vacuity pin した上で・Run は Strategy Editor タイトルバーへ移動） | Section12 が run-trigger 不在を assert（`ScenarioStartupTile.Build` に `Run Replay` を再追加で RED） | 自動(E2E済) | `ScenarioStartupE2ERunner`（findings 0063→0075 §3c re-home） |
| SCENARIO-16 | dock base window が全廃＝dock=chart のみ（#126/ADR-0026 startup・ADR-0037 run_result・#174-178/ADR-0038 で 3 panel 退役） | `DockShape.IsDockKind`（chart のみ）／ base-spawn＋factory-grouping 機構（BaseDockWindowIds 等）は削除済 | dock kind は `chart` のみ＝退役 kind（startup/run_result/buying_power/orders/positions）はどれも dock kind でない。各機能は Settings / popup / account bar が宿す | Section13 が `IsDockKind("chart")` 真 ＆ 退役 5 kind すべて非 dock を behavioural assert（退役 kind を IsDockKind に戻すと RED） | 自動(E2E済) | `ScenarioStartupE2ERunner`(S13) |
| SCENARIO-17 | `KIND_STARTUP` を含む旧保存 layout を開いても skip（forward-compat） | `FloatingWindowCatalog.Default()`（startup spec 退役）→ `RestoreFloating` の TryGet=false 継続 | catalog が `"startup"` を resolve しない＝spawn skip・layout エントリは保持（crash 無し）。surviving dock kind は resolve | Section14 が `TryGet("startup")==false` ＆ run_result は resolve を assert（startup spec を戻すと RED） | 自動(E2E済) | `ScenarioStartupE2ERunner`(S14) |

> per-field エラーラベル・granularity 選択 highlight は入力のない**表示**なので、SCENARIO-01〜05/08 の観測点として
> 確認する（`Refresh` の `Validate`→`SetErr`/`Highlight`）。universe は CONTEXT「universe registry vs scenario panel」
> 通り **独立 SoT**で、タイルは薄いテキスト入力（#31 picker が同じ SoT/writeback に後で差し込む）。

## 観測点（詳細）

- **SCENARIO-01〜05/08/09（3 projection）**: CONTEXT「scenario 編集の 3 projection」= ①editing buffer（不正値も保持）→
  ②validated-for-write→③on-disk。各 setter が ①を変え `Dirty` を立て、`Validate` がエラーラベルを駆動。`Commit`/
  `TryStartRun` の `TryBuildForWrite` が ①→②ゲート（AC④「不正値は run を起動しない」）。`ScenarioStartupE2ERunner`
  Section2（validation 個別）・Section5（controller round-trip + run gate）が正本。
- **SCENARIO-06/07（universe 再 sync）**: universe は SHARED SoT。held-mode の text field が SoT とズレると次キーで
  `ReplaceAll(stale)` が sidebar の add を消す（findings 0025 §12）。`onEndEdit`→`PullUniverseField`（blur 回収）と
  `Universe.Changed`→`OnUniverseRegistryChanged`（focus-guard 付き外部 sync）の双方を `ScenarioStartupE2ERunner`
  Section7/Section8 が REAL uGUI tile（reflection で `_universeField` 取得）で assert。`Dispose` の unsubscribe も。
- **SCENARIO-10/11（populate / merge-write）**: sidecar 駆動（CONTEXT「scenario sidecar」）。merge は **非空虚 kill**＝
  JsonUtility 風 lossy writer なら nested `strategy_init_kwargs` を落として strict-validated sidecar を壊す
  （`ScenarioStartupE2ERunner` Section1）。個別 setter は mutate-existing-only で、Run-commit のみ full 5-key 作成（Section9）。
- **SCENARIO-12（File→New Clear）**: in-memory のみ（on-disk sidecar は消さない＝破壊的 over-reach 回避・findings 0017
  §4）。dirty-guard は適用しない（New は意図的 discard）。strategy `.py` despawn と `SetExecutionMode(LiveManual)` は
  メニューバーの責務で本サーフェス外。**Section11（`Section11_FileNewClearsInMemory`）で実装**（blank buffer＋空 universe＋
  Dirty=false＋on-disk sidecar byte 不変を assert。delete-the-logic litmus: `Clear` の `Universe.ReplaceAll` を外すと FAIL）。

## 自動判定（合格条件）

- ログに `[E2E SCENARIO STARTUP PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、
  `error CS\d+` が 0 件。
- 各 `自動(*)` 行の観測点を 1 つでも落としたら `[E2E SCENARIO STARTUP FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `TryBuildForWrite` の検証分岐を消すと SCENARIO-09 が落ちる／merge writer を
  全置換 writer に替えると SCENARIO-11 が落ちる／`onEndEdit`→`PullUniverseField` 配線を外すと SCENARIO-06 が落ちる
  ／`OnUniverseRegistryChanged` の focus-guard を外すと SCENARIO-07 のフォーカス中 skip が落ちること。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `ScenarioStartupE2ERunner` S1/S9 | EditMode・sidecar merge | SCENARIO-11 の非空虚 merge・mutate-existing-only を昇格元 |
| `ScenarioStartupE2ERunner` S2 | pure validation | SCENARIO-01〜05/08/09 の per-field 検証・ゲートを昇格 |
| `ScenarioStartupE2ERunner` S3 | pure registry | SCENARIO-05 の `InstrumentRegistry`（dedup/order/editable gate） |
| `ScenarioStartupE2ERunner` S4/S5 | pure controller | SCENARIO-09/10 の populate 優先・run-gate・restore round-trip |
| `ScenarioStartupE2ERunner` S6/S7/S8 | registry event + REAL uGUI tile | SCENARIO-06/07 の held-mode 再 sync / blur 回収 / Dispose の正本 |
| `ScenarioStartupE2ERunner` S10 | cross-language golden | inline reader の golden pin（本 Surface の populate fallback 裏取り。直接行ではない） |

## `ScenarioStartupE2ERunner.cs` 実装方針（第二波・実装済み）

> 実装済み（findings 0054）。`ScenarioStartupProbe`（throwaway, Assets/Editor）を本 runner へ昇格・改名し、Section11
> （SCENARIO-12 File→New Clear）を追加。AFK RED→GREEN 済み（`[E2E SCENARIO STARTUP PASS]`）。以下は設計メモ（保持）。

- pure 行（SCENARIO-01〜05/08〜11）は `ScenarioStartupController`/`ScenarioStartupValidation`/`InstrumentRegistry`/
  `ScenarioSidecarStore` を直接組んで Python-FREE で駆動（`ScenarioStartupE2ERunner` の Section2/3/4/5/9 をそのまま昇格）。
- uGUI 行（SCENARIO-06/07/12）は `ScenarioStartupTile.Build(RectTransform)` を bare-RT で組み、`_universeField`/`_*Err`
  を reflection で観測し、`onEndEdit.Invoke`・`Universe.Add/Remove`・`Dispose` を直接駆動（Section7/8 と同型）。
  `Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf")` を font に。teardown は GameObject の `DestroyImmediate`。
- セクション構成は操作一覧表の `自動(*)` 行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを返す `Execute()`
  （null=PASS）パターン。sidecar は `Application.temporaryCachePath` 下の scratch に書く（fixtures 非汚染）。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod ScenarioStartupE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep で grep**（PowerShell `Select-String` は取りこぼす）。
