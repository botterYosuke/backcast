# SettingsDialogE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`SettingsDialogE2ERunner.cs` が自動検証する **Settings モーダル サーフェス**（ADR-0026・findings 0102）の台本。
これは調査メモではなく、**このサーフェスでユーザーができる行動すべての網羅台帳と、E2E の観測点・合格条件を定義する正本**。
Action ID 採番・カバー状態の語彙・共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

## 対象サーフェス

Help→Settings で開く screen-fixed モーダル（`SettingsModalOverlay` chrome ＋ pure `SettingsModalController` ＝開閉/ESC-guard）。
**3 機能を集約**: ① Venue 接続/切断（`SettingsVenueSectionView`・`VenueMenuViewModel` 再利用）、② 実行モード切替
（`SettingsModeSegmentView`・`FooterModeViewModel` 再利用）、③ Scenario Startup（`ScenarioStartupTile`・`ScenarioStartupController` 再利用）。
brain は不変——ビュー層のみ再構築（ADR-0026）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*。Settings の各セクションが宿す **brain の挙動**は各正本が担う——
> Mode は [FooterModeE2ERunner](./FooterModeE2ERunner.md)（FOOTER-06/07 を `SettingsModeSegmentView` に retarget 済み）、
> Scenario は [ScenarioStartupE2ERunner](./ScenarioStartupE2ERunner.md)（SCENARIO-01..17）、Venue gating は `VenueMenuM3Probe`。
> 本台本は **モーダルの SHELL（開閉/ESC-guard/z-order/chrome）＋ section hosting ＋ venue 表面の退役**を観測する。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存Probe |
|---|---|---|---|---|---|---|
| SETTINGS-01 | Help→Settings で開く / `[x]`・ESC で閉じる | `MenuBarView.BuildHelpMenu`→`OpenSettings` / `SettingsModalController` | `IsOpen` が false→true→false（Open 冪等・Close） | controller を直接駆動し open/close を assert | 自動(E2E済) | `SettingsDialogE2ERunner`(S01) |
| SETTINGS-02 | ウィンドウ drag 中の ESC | `BackcastWorkspaceRoot.DriveSettings`→`OnEscape` | drag 中は `DeferToDrag`・`IsOpen` 不変（drag-revert 優先・ADR-0024 §8） | `OnEscape(drag:true,…)` を assert（開/閉 両状態） | 自動(E2E済) | `SettingsDialogE2ERunner`(S02) |
| SETTINGS-03 | secret / save-guard が開いている間の ESC | 同上 | `ConsumedByBlockingModal`・`IsOpen` 不変（Settings は裏で開かない／secret の下で閉じない） | `OnEscape(blocking:true)` を assert（開/閉 両状態） | 自動(E2E済) | `SettingsDialogE2ERunner`(S03) |
| SETTINGS-04 | それ以外で ESC | 同上 | `Toggled`・open↔close が反転 | `OnEscape(false,false)` を 2 回 assert | 自動(E2E済) | `SettingsDialogE2ERunner`(S04) |
| SETTINGS-05 | `[x]` クリック / 表示トグル | `SettingsModalOverlay.SetVisible` / `CloseClicked` | Build 後 `IsVisible=false`・SetVisible 反映・`btn_x` クリックで `CloseClicked` 発火 | overlay を built し `[x]` onClick を invoke | 自動(E2E済) | `SettingsDialogE2ERunner`(S05) |
| SETTINGS-06 | z-order（secret が Settings の上） | `SettingsModalOverlay.SETTINGS_SORT=900` | menu(600) < footer(550) < settings(900) < secret/save-guard(1000)。**#128: Venue 接続の second password は secret(1000) が Settings の上に重なる** | 定数の relation を assert（`<1000`） | 自動(E2E済) | `SettingsDialogE2ERunner`(S06) |
| SETTINGS-07 | （観測点）chrome ＋ 3 セクション container | `SettingsModalOverlay.Build` | backdrop・panel・`btn_x`・Venue/Mode/Scenario container が存在し相互に別 RectTransform・自前 ScreenSpaceOverlay canvas が SETTINGS_SORT | built tree を反射 | 自動(E2E済) | `SettingsDialogE2ERunner`(S07) |
| SETTINGS-08 | Venue セクションで Connect/Disconnect・menu Venue 退役 | `SettingsVenueSectionView.Build/Refresh` / `MenuBarView`（Venue dropdown 削除） | 接続項目数＝`VisibleConnectItems`+Disconnect・**切断時は全 connect（prod 含む / ADR-0027）が enable / Disconnect disable**・接続時は connect 全 disable（gating が VM 追従）・`MenuBarView.OpenMenu` enum に Venue 無し | view の `_items`/interactable を反射＋enum 反射（切断時 connectEnabled==connectCount） | 自動(E2E済) | `SettingsDialogE2ERunner`(S08) |
| SETTINGS-09 | 実 OS でモーダルを目視操作（実 EventSystem raycast・実ピクセル・secret over settings の実描画） | findings 0102 D3 | backdrop が下層クリックを食う・secret が Settings の前面に実描画・送信後 Settings 残存 | — | HITL専用（実 GPU/EventSystem・実 venue secret） | — |

> Venue 接続の **実 RPC（login/logout）/ LIVE_VENUE 絞り込み**は `VenueMenuViewModel` が brain で、（ADR-0027: prod 解禁の env ゲートは廃止）
> `VenueMenuM3Probe`（MENU-11/14/19）が正本。本台本は「Settings の venue 表面が同じ VM を gating 込みで宿す」までを観測する。

## 自動判定（合格条件）

- 各 section が null（pass）を返したら `[E2E SETTINGS-0N PASS]`（単一トークン＝rollup-visible）を吐く。最後に `[E2E SETTINGS DIALOG PASS]`。
- いずれかが落ちたら `[E2E SETTINGS DIALOG FAIL] <id: msg>` で `EditorApplication.Exit(1)`。`error CS` 0 件。
- delete-the-production-logic litmus（findings 0102）: `SETTINGS_SORT`≥1000 で S06 RED / `OnEscape` の guard 順を崩すと S02/03 RED /
  `SettingsVenueSectionView.Refresh` の gating を消すと S08 RED。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値に従う。`HITL専用`（SETTINGS-09）は理由併記済み。

## 実行コマンド

```
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod SettingsDialogE2ERunner.Run -logFile <abs log>
```
compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8＝**Bash `grep -a`** で確認。
