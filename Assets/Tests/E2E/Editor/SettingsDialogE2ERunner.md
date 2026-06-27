# SettingsDialogE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`SettingsDialogE2ERunner.cs` が自動検証する **Settings モーダル サーフェス**（ADR-0026・findings 0102）の台本。
これは調査メモではなく、**このサーフェスでユーザーができる行動すべての網羅台帳と、E2E の観測点・合格条件を定義する正本**。
Action ID 採番・カバー状態の語彙・共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

## 対象サーフェス

Help→Settings で開く screen-fixed モーダル（`SettingsModalOverlay` chrome ＋ pure `SettingsModalController` ＝開閉/ESC-guard）。
**#137 redesign（findings 0107）以降は 2 タブ chrome**:「実行」タブ＝① Venue 接続/切断（`SettingsVenueSectionView`）・
② 実行モード切替（`SettingsModeSegmentView`）・③ Scenario Startup（`ScenarioStartupTile`）・④ DuckDB root（`SettingsDataSectionView`・#137 S4）、
「外観」タブ＝⑤ Dark/Light（`SettingsAppearanceSegmentView`・ADR-0028）。各節は **テーマ role 解決のカード面**で囲い、入力欄は
**枠線(border)＋プレースホルダ＋muted ラベルの 2 列フォーム**（S1/S3）。brain は不変——ビュー層のみ再構築（ADR-0026）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*。Settings の各セクションが宿す **brain の挙動**は各正本が担う——
> Mode は [FooterModeE2ERunner](./FooterModeE2ERunner.md)（FOOTER-06/07 を `SettingsModeSegmentView` に retarget 済み）、
> Scenario は [ScenarioStartupE2ERunner](./ScenarioStartupE2ERunner.md)（SCENARIO-01..17）、Venue gating は `VenueMenuM3Probe`、
> DuckDB root の store/validation/browse/os.environ 注入は [DuckDbRootSettingsE2ERunner](./DuckDbRootSettingsE2ERunner.md)（DUCKROOT-01..04）。
> 本台本は **モーダルの SHELL（開閉/ESC-guard/z-order/chrome）＋ 2 タブ切替 ＋ section hosting ＋ 入力欄判別/カード面（視覚）＋ venue 表面の退役**を観測する。

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
| SETTINGS-09 | 入力欄を入力欄と判別できる（#137 S1） | `ScenarioStartupTile.MakeField` | 各 InputField に枠線(Outline=`border`)・非空プレースホルダ(`text_placeholder`)・沈め面(`surface_background`)・本文(`text`)、ラベルは `text_muted` で本文と別 hue | tile を built し全 InputField の border/placeholder/fill/body role ＋ muted ラベル存在を assert | 自動(E2E済) | `SettingsDialogE2ERunner`(S09) |
| SETTINGS-10 | 「実行 / 外観」タブを切替える（#137 S2） | `SettingsModalOverlay.SelectTab` / `tab_run`・`tab_appearance` | 既定=実行 active・Venue/Mode/Scenario/Data は実行 group・Appearance は外観 group の子・タブクリックで group の activeSelf とタブ色(`tab_active/inactive_background`)が入替わる | overlay を built し SelectTab/タブ onClick で activeSelf・section parent・タブ色を assert | 自動(E2E済) | `SettingsDialogE2ERunner`(S10) |
| SETTINGS-11 | 各節がカード面でグループ化（#137 S3） | `SettingsModalOverlay.MakeCard` | panel 面=`panel_background`・各 `card:*` 面=`elevated_surface_background`（panel より一段上げ）・ヘッダ=`text_muted` | overlay を built し panel/5 カードの role 解決と card≠panel を assert | 自動(E2E済) | `SettingsDialogE2ERunner`(S11) |
| SETTINGS-12 | 実 OS でモーダルを目視操作（実 EventSystem raycast・実ピクセル・secret over settings の実描画・Dark/Light 美観・自動クローズの実挙動） | findings 0102 D3 / 0107 / 0127 | backdrop が下層クリックを食う・secret が Settings の前面に実描画（**入力中**は Settings がその裏で生存＝z-order 契約不変）・**login 完了＋venue live 化で Settings が自動クローズ**（ADR-0039・旧「送信後 Settings 残存」を部分 supersede）・モード/テーマ切替の確定でも自動クローズ・カード/枠線/プレースホルダが Dark/Light 双方で可読 | — | HITL専用（実 GPU/EventSystem・実 venue secret・実ピクセル美観・実 RPC 確定での close 体感） | — |
| SETTINGS-13 | テーマ切替時の Settings 内ライブ再描画（#137 review fixes・findings 0107 追補） | `BackcastWorkspaceRoot.ApplyViewportTheme` / `SettingsModalOverlay.ApplyTheme` / `SettingsVenueSectionView.ApplyTheme` / `SettingsModeSegmentView.Refresh` | Dark↔Light 切替で Settings モーダル開放中の全要素（close ボタン・Venue 行ボタン/ラベル・Mode セグメントラベル）が `panel_background/text/border/elevated_surface_background/text_muted` の現在テーマへ即時 repaint される | overlay を built しテーマ切替後に `_closeBtnImg.color`/`_closeBtnText.color`/Venue `_items[].image.color`/Mode label `text` role 解決値が新パレットと一致するかを反射 assert | 自動(E2E済) | `SettingsDialogE2ERunner`(S13) |
| SETTINGS-14 | 単発アクション auto-close — 同期成功は即クローズ・no-op は開いたまま（#171 Slice 1・ADR-0039） | `SettingsAutoCloseController.OnThemeSelected`/`OnModeRequest` ← `BackcastWorkspaceRoot.ApplyAppearance`/`OnFooterMode` | 外観テーマ変更＝`CloseNow`（無変化＝`Stay`）・モード Replay（`SwitchImmediate`）＝`CloseNow`・モード no-op（`Ignore`）/venue 未接続 block（`BlockedVenueNotLive`）＝`Stay`・同期 close は latch しない（`IsWaiting==false`） | pure controller を直接駆動し各 decision と非 latch を assert | 自動(E2E済) | `SettingsDialogE2ERunner`(S14) |
| SETTINGS-15 | 単発アクション auto-close — 非同期モード Manual/Auto は確定 poll でクローズ（#171 Slice 2・ADR-0039） | `SettingsAutoCloseController.OnModeRequest`/`OnPoll` ← `OnFooterMode`/`DriveFooter` | Live target は `Wait` で `Goal.Mode` latch・lock 中/別モード poll では閉じない・lock 解除＋target 到達＋venue live で close＋latch クリア・LiveAuto 離脱（`StopRunThenSwitch`→Replay）は DisplayMode→Replay で close（Replay は venue 非依存） | controller を駆動し latch→poll 確定/非確定を assert | 自動(E2E済) | `SettingsDialogE2ERunner`(S15) |
| SETTINGS-16 | 単発アクション auto-close — モード拒否・auto-replay 巻き込みは開いたまま（#171 Slice 2・ADR-0039） | `SettingsAutoCloseController.NotifyFailed`/`NotifyLiveModeAbandoned`/`OnPoll` ← `DriveFooter`（`_footerModeRejected` / `ShouldAutoReplay`） | 拒否（`NotifyModeResult(false)`→`NotifyFailed`）は latch を落とし target poll でも閉じない・venue 落ち poll（target 到達・lock 解除だが venue 非live）は閉じない＝auto-replay 失敗・auto-replay 分岐は `NotifyLiveModeAbandoned`（**surgical**＝Replay-target `Goal.Mode` は落とさず Replay 到達で閉じる） | controller を駆動し失敗 2 経路＋surgical 性で「閉じない/閉じる」を assert（RED litmus 中核） | 自動(E2E済) | `SettingsDialogE2ERunner`(S16) |
| SETTINGS-17 | 単発アクション auto-close — Venue Connect/Disconnect は確定 poll でクローズ（#171 Slice 3・ADR-0039） | `SettingsAutoCloseController.ArmVenueConnect`/`ArmVenueDisconnect`/`OnPoll` ← `OnVenueConnect`/`OnVenueDisconnect`/`DriveFooter` | Connect＝`Goal.VenueLive` latch・venue 非live poll では待機（secret login 中含む）・venue live poll で close。Disconnect＝`Goal.VenueDown` latch・venue live poll では待機・非live poll で close。**Disconnect-from-Live**＝同じ venue 落ちで auto-replay 分岐（`NotifyLiveModeAbandoned`）も走るが `VenueDown` goal は成就側ゆえ消えず非live poll で close | controller を駆動し venue 状態遷移＋Disconnect-from-Live 回帰で close を assert | 自動(E2E済) | `SettingsDialogE2ERunner`(S17) |
| SETTINGS-18 | 単発アクション auto-close — Venue 接続失敗/secret 取消/logout 失敗・idle は開いたまま（#171 Slice 3・ADR-0039） | `SettingsAutoCloseController.NotifyFailed`/`OnPoll` ← login `lr==2` / `CancelSecret` / `_venueLogoutFailed` | 接続失敗・パスワード取消は venue-live latch を落とし venue live poll でも閉じない（z-order 契約は不変）・logout 失敗は venue-down latch を落とす・idle（未 latch）は無関係 poll で閉じない | controller を駆動し失敗/取消/idle で「閉じない」を assert | 自動(E2E済) | `SettingsDialogE2ERunner`(S18) |
| SETTINGS-19 | フォーム系（Scenario Startup・Data）の編集では閉じない（自動クローズ非適用・#171・ADR-0039） | `BackcastWorkspaceRoot`（フォーム系 section は `SettingsAutoCloseController` に配線されない＝by-construction） | Scenario/Data の編集は単発の成功境界が無く、`OnThemeSelected`/`OnModeRequest`/`ArmVenue*` のいずれも呼ばない＝auto-close seam に到達しない | 対象外（by-construction：フォーム系 section view は seam に配線されない・配線したら HITL/コードレビューで検出） | 対象外（フォーム系は据え置き＝seam 非配線。負の不在を pure assert すると vacuous） | — |

> Venue 接続の **実 RPC（login/logout）/ LIVE_VENUE 絞り込み**は `VenueMenuViewModel` が brain で、（ADR-0027: prod 解禁の env ゲートは廃止）
> `VenueMenuM3Probe`（MENU-11/14/19）が正本。本台本は「Settings の venue 表面が同じ VM を gating 込みで宿す」までを観測する。
> **#171（ADR-0039）以降、単発アクション系（Venue 接続/切断・実行モード切替・外観テーマ）は確定成功で Settings を自動クローズ**する。判定は pure
> `SettingsAutoCloseController`（SETTINGS-14..18 が全分岐を headless 駆動）に置き、host（`BackcastWorkspaceRoot`）は decision に従って `_settings.Close()` を呼ぶだけ。
> z-order 契約（secret 入力中は Settings が裏で生存）は不変——変わるのは「login 完了＋venue live 化した後」だけ。

## 自動判定（合格条件）

- 各 section が null（pass）を返したら `[E2E SETTINGS-0N PASS]`（単一トークン＝rollup-visible）を吐く。最後に `[E2E SETTINGS DIALOG PASS]`。
- いずれかが落ちたら `[E2E SETTINGS DIALOG FAIL] <id: msg>` で `EditorApplication.Exit(1)`。`error CS` 0 件。
- delete-the-production-logic litmus（findings 0102/0107）: `SETTINGS_SORT`≥1000 で S06 RED / `OnEscape` の guard 順を崩すと S02/03 RED /
  `SettingsVenueSectionView.Refresh` の gating を消すと S08 RED / `MakeField` の Outline 枠線やプレースホルダを消す・インライン色に
  すると S09 RED / `SelectTab` を no-op にすると S10 RED / カードをインライン色にする・カード面を消すと S11 RED。 / `ApplyViewportTheme` から `_settingsVenueView?.ApplyTheme()` / `_settingsModeView?.Refresh()` 呼出を消す・あるいは `SettingsModalOverlay.MakeButton` が Image/Text の参照を保持しないと S13 RED。
- 自動クローズ（#171 / findings 0127）の litmus: `SettingsAutoCloseController.OnThemeSelected` が `changed` を無視して常時 `CloseNow` を返すと
  S14 の no-op assert が RED / `OnModeRequest` が `SwitchImmediate` で `Wait`/`Stay` を返すと S14 RED / `OnPoll` が `modeLocked` を無視すると
  S15 RED（lock 中に閉じる）/ `NotifyFailed` を no-op にすると S16・S18 RED（失敗/取消/拒否でも閉じる）/ `OnPoll` の Live target 用 `venueLive`
  ガードを外すと S16 の auto-replay 巻き込みが RED（venue 落ち poll で閉じる）/ **`NotifyLiveModeAbandoned` を blanket `Disarm` にすると S16(c)・S17
  の Disconnect-from-Live が RED**（Replay-target/`VenueDown` goal を誤って消し閉じない）/ `ArmVenueConnect` を `VenueDown` latch にすると S17 RED。
  実証済み RED→GREEN（2026-06-27）: `NotifyFailed` no-op で S16/S18 RED→復元で全 GREEN（findings 0127 §実装着地）。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値に従う。`HITL専用`（SETTINGS-12）は理由併記済み。

## 実行コマンド

```
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod SettingsDialogE2ERunner.Run -logFile <abs log>
```
compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8＝**Bash `grep -a`** で確認。
