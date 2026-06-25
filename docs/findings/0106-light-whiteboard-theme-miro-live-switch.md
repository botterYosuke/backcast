# findings 0106 — Light（Miro 風ホワイトボード）テーマの追加とライブ切替（ADR-0028）

方針の正本は **[ADR-0028](../adr/0028-light-whiteboard-theme-miro-live-switch.md)**（immutable）と
**CONTEXT.md「Appearance（Dark / Light）」/「Whiteboard（light テーマ・Miro 風）」/「Card chrome ⇔ HUD chrome」**。
本 findings は ADR-0028 が findings に委ねた**下位決定（設計の木）**・実装スライス分解・RED→GREEN 検証計画を記録する。

`grill-with-docs`（2026-06-25, owner HITL）で 6 問の往復により導出。owner 回答の確定列：
①「見た目を明るいホワイトボード風に」②「アプリ全体を明るく」③「暗いテーマを残して切替」
④「ドット方眼／カードの影／角丸（付箋多色は無し）」⑤「動かしたままその場で切替・スイッチは設定ダイアログ」
⑥「覚えておいて次回もそのテーマで起動」。

## 設計の木（下位決定）

- **D1 — Dark 温存・Light 追加・切替式**。`Theme.Dark()`（宇宙 HUD）は不変。`Theme.Light()` を実体化し
  `Appearance` を「記録ラベル」から「ユーザ切替値」へ格上げ。divergence は *light を実 shipped 変種にし
  Miro 構造差を載せる*点に限る（light の存在自体は TTWR parity 沿い）。
- **D2 — 共有 chrome は本物の Radix light scales**。`ColorScales.Light()` の `Dark()` 丸投げを解消し、
  `NeutralLight()/AccentLight()/RedLight()/GreenLight()/YellowLight()/BlueLight()` を実装。`from_scales` は
  appearance 非依存（Radix 設計：step_1=app bg, step_12=高コントラスト text）なので **`ThemeColors`/`StatusColors`/
  `SyntaxColors`/`PlayerColors` の導出は再配線ゼロ**で明るくなる。
- **D3 — キャンバス隔離直値は appearance 別に出し分け**。`workspace_background`・`hakoniwa_*` は **スケール非依存の
  owner 直値**で、現状 `ThemeColors.FromScales(ColorScales s)` 内にハードコードされ dark/light 共通＝**そのままだと
  Light に切替えても盤面は宇宙の暗いまま**。→ `FromScales` に `Appearance` を渡し、dark 直値群と light 直値群を
  出し分ける。隔離継ぎ目（findings 0054）は維持し、共有 scale が盤面/ローソク足を勝手に塗らないこと。
  - light 盤面 = 明るいオフホワイト（方眼が乗る）。light タイル = 白カード。ink text は濃色。
  - `hakoniwa_up/down/last`（ローソク足・change%・板 bid/ask/LAST）は **light 盤面で読める**緑/赤/金へ。
- **D4 — 色じゃない Miro 要素は Light のときだけ**。ドット方眼の盤面背景・カードのドロップシャドウ・角丸カードを
  Light 限定で付与。**付箋多色は不採用**（タイトル帯は accent 系のまま・[[PlayerColors]]）。Dark は現行 HUD のまま。
- **D5 — Card chrome ⇔ HUD chrome は appearance で構造ごと切替**。`HudFrameChrome`（シアン括弧＋縁グロー）と並ぶ
  `CardFrameChrome`（仮称・ドロップシャドウ＋角丸）を新設。`DockWindowFrame` / 他 `*WindowFrame` は appearance を見て
  どちらを `Decorate` するか分岐。**色の塗り替えではなく GameObject 構造が違う**ので、切替時はサブツリー再生成。
- **D6 — 切替はライブ（最も load-bearing な配線）**。現状、盤面 viewport / chart / ladder は `ThemeService.Changed`
  購読済みで live 追従するが、**浮遊カードのフレーム・HUD 装飾は未購読＝build 時焼き込み**（`HudFrameChrome` ヘッダ
  L11-12 が明言）。→ フレームを `Changed` 購読化し、`ApplyTheme()` で (a) 色再適用 (b) 装飾を appearance に応じ
  HUD↔Card で**再 Decorate**。盤面の方眼背景も `Changed` で on/off。
- **D7 — スイッチ UI = 設定ダイアログ**（ADR-0026 集約口）。`SettingsModeSegmentView` を雛形に Appearance セグメント
  （Dark / Light）を追加。クリックで `ThemeService.SetTheme(Theme.Dark()/Light())` をライブ発火。
- **D8 — 永続化はアプリ全体グローバル**。`PlayerPrefs` 単一キー（例 `"appearance"`=`"dark"|"light"`）。per-document
  sidecar（`<strategy>.json`）には載せない（戦略を開くたびにテーマが変わる不具合を避ける）。boot 時、最初のテーマ
  適用前に読み込み、`ThemeService` の遅延 dark 既定の前段で `SetTheme` する。次回起動で選択を復元（#43 相当）。

## 既知の負債・落とし穴（実装前に把握）

- **`ThemeProbe.cs:87` が stale**：`workspace_background == #7fa4be`（旧農場の水色）を assert したままで、現値
  `#02050a` と食い違う。再スキン実装で現値へ修正し、light 値＋構造切替の非空検証を追加する。
- `BackcastWorkspaceSceneBuilder` は viewport bg を **編集時プレビューとして bake** する（Play 時 `ApplyViewportTheme`
  が live 再適用）。方眼背景も同じ「bake は editor preview・Play で live 上書き」規律に乗せる。
- `Theme.NonDefault()`（検証パレット）は `hakoniwa_*` の dark 直値前提でインデックスを割当済み。appearance 別出し分けで
  light 直値を足すなら NonDefault 側の検証範囲も見直す。

## 実装スライス（順序）

1. **S1 — Radix light scales**（D2）：`ColorScale.*Light()` 6 本＋`ColorScales.Light()` 配線。`from_scales` 不変。
   AFK：`ThemeProbe` に Light の step_1≠step_12・dark≠light の非空 assert。
2. **S2 — キャンバス appearance 別直値**（D3）：`FromScales(scales, appearance)` 化＋light 盤面/タイル/ローソク足直値。
   `ThemeProbe.cs:87` stale 修正。AFK：Light の workspace_background≠Dark・hakoniwa_* 出し分け。
3. **S3 — Card chrome ＋ ライブ装飾切替**（D4/D5/D6）：`CardFrameChrome`（影＋角丸）新設、フレーム購読化、
   appearance で HUD↔Card 再 Decorate。盤面ドット方眼背景（Light のみ）。AFK：SetTheme(Light)→Card 装飾出現／
   SetTheme(Dark)→HUD 復帰の非空切替（probe）。
4. **S4 — Settings の Appearance セグメント**（D7）：`SettingsAppearanceSegmentView`（`SettingsModeSegmentView` 雛形）。
   AFK：クリック→`ThemeService.Current.appearance` がトグルすること（headless）。
5. **S5 — 永続化**（D8）：`PlayerPrefs` 保存＋boot 読み込み。AFK：保存→`ResetForTests` 後に boot 経路が復元すること。

各スライスは `behavior-to-e2e` で AFK probe / E2E gate に固定してから着手。HITL（実 Play での見た目確認＝方眼・影・
角丸・ローソク足の light 可読性）は owner 専用。

## 検証メモ

- 切替の **非空性**（vacuous でないこと）は既存規律：dark↔light で実際に色/装飾が変わることを probe が assert。
- ライブ切替後、浮遊カードが**重複装飾を積まない**こと（`HudFrameChrome`/`CardFrameChrome` は find-or-create で冪等）。
- light の syntax 色（コードエディタ）が light 盤面で読めること（HITL）。

## 実装着地（2026-06-25）

全 5 スライス実装済み・専用 AFK ゲート GREEN（Unity 6000.4.11f1 batchmode）：

- **S1**：`ColorScale.*Light()` 6 本（Radix slate/indigo/red/grass/amber/blue・hex→`Rgb()` ヘルパで転記）＋ `ColorScales.Light()` 配線。accent=Indigo.9 `#3e63dd`（Miro-blue）。
- **S2**：`ThemeColors.FromScales(scales, appearance)` 化。canvas 隔離直値を `CanvasLiterals.Dark()/Light()` に集約し appearance で出し分け（light=オフホワイト盤面 `#eef0f3`・白カード・ink text・light 可読ローソク `#16863f`/`#d23f3f`/`#b07700`）。
- **S3**：`CardFrameChrome`（`RoundedRectSprite` 9-slice 角丸＋`Shadow` ドロップシャドウ）／`HudFrameChrome.Remove()`／`WindowChrome`＋`WindowChromeApplier`（per-window `Changed` 購読でライブ HUD⇔Card 切替＋surface 再塗り）。5 フレーム site（Dock/Editor/Order 各 builder＋adopted editor/order）を `HudFrameChrome.Decorate`→`WindowChrome.Attach` に置換。`HudGridBackground` に dotted light 変種追加（grey dots on off-white）。
- **S4**：`SettingsAppearanceSegmentView`（`SettingsModeSegmentView` 雛形）を Settings モーダルの新 `Appearance` セクションへ。クリック→`ApplyAppearance`（`SetTheme` ライブ＋`AppearanceStore.Save`）。パネル高 600→668。
- **S5**：`AppearanceStore`（`PlayerPrefs` 単一キー）。`BackcastWorkspaceRoot.ApplyPersistedAppearance()` を `BuildWorkspace` 冒頭で呼び boot 復元。

**AFK ゲート**：`ThemeProbe`（S1+S2、derivation＋light 非空）/ `WindowChromeProbe`（S3、HUD⇔Card 構造切替＋surface）/ `SettingsAppearanceProbe`（S4+S5、segment 切替＋persist＋boot 復元）すべて GREEN。回帰：`SettingsDialogE2ERunner` 8/8 GREEN。

### 既存 probe の stale 同期

- **`ThemeProbe` Section1 を同期**（S2 必須）：農場パレットの旧 assert（`workspace_background==#7fa4be`・`hakoniwa_*` の grass/earth 値、L87-103）が space 再スキン（2026-06-20/21）以後 stale で **RED のまま放置**されていた → 現 dark space 値へ同期。さらに space 再スキンで `hakoniwa_up==green.9`（=`status.long`）と一致するようになったため、旧 `Ne(hakoniwa_up, status.long)` distinctness assert を撤去（隔離は「別ロール」であって値の相違ではない）。新規 `Section1b_LightVariant`（light derivation＋dark≠light 非空）を追加。

### 本作業と無関係の pre-existing RED（スコープ外）

- **`BackcastWorkspaceProbe` は main 時点で既に RED**（本 theme 作業とは無関係）。Section10 は #103/ADR-0018 の front/back プレーン分割（base dock + chart は `_dockWindows`、probe は `_windows` を見ていた）と #126/ADR-0026 の `startup` 退役で stale。さらに後段 **S14a**（`File→Open` の bare v19 は abort せず open すべき・#80/findings 0051）も pre-existing で fail。本セッションの diff は theme/appearance のみで `OnFileOpen`/scenario-open 経路には一切触れていない（`git diff` で確認）ため、これらは別途 #103/#126/#80 系のクリーンアップで対応すべき残務。本 findings の changeset には含めない（probe への暫定修正は revert 済み）。

### code-review(simplify) 結果（2026-06-25）

8 finder angle × verify。**Medium 1 件を修正**、残りは cosmetic/HITL か Low cleanup。

- **F1（Medium・修正済み）**：`WindowChrome.Apply` が全 window の root 色を無条件で `hakoniwa_panel_surface` に上書きしており、**dark でも** Order ticket の authored body `#11162b`→`#0e1626`、editor の alpha 0.98→1.0 を破壊していた（コメントの「dark では no-op」は虚偽）。→ **role ベースに修正**：`Attach(root, Color? darkSurface)`。dock は `null`（両 appearance で theme panel_surface 追従）、editor/order は authored 色（alpha 込み）を渡し、**dark では authored を保持・light では白カード・light→dark で正しく復元**。`BodyColor` を両 frame で public 化。`WindowChromeProbe` に preservation ケース追加 → GREEN。
- **F2（HITL）**：タイトル帯 accent ＋ DockWindowFrame のタイトル文字（`Color.white`）が light 切替に追従せず、light の明るい accent 上で白文字が読みにくい。owner 決定「タイトル帯は accent 系のまま」（CONTEXT.md）に沿う **deferred 境界**だが、light 上の文字可読性は HITL 調整項目。
- **F3（HITL）**：`CardFrameChrome` がタイトルバーを 4 隅丸めるため下辺隅に小さなノッチ。top-only 角丸 sprite が必要なら別途。cosmetic。
- **F4（HITL/deferred）**：Settings モーダル（および secret/save-guard）は hardcoded dark navy で theme 非購読＝light でも暗いパネル。screen-fixed modal 群の theme 化は別スコープ。
- **Low（許容）**：`WindowChromeApplier` の Attach 時 explicit Apply ＋ play-mode OnEnable で二重 apply（build 時のみ・冪等・hot path 外）／`FindChild`・`DestroyCompat`・segment builder の重複（既存コードに遍在するパターン・抽出は別タスク）。

### HITL 残（owner 実機確認）

light テーマの見た目微調整は実 Play での owner HITL 専用（headless 検証不可）：盤面ドット方眼の濃さ/間隔・カードの角丸半径とシャドウの柔らかさ・タイトル帯 accent の light 上での見え（タイトル色は build 時のままでライブ切替に追従しない＝小領域の既知の deferred 項目）・ローソク足/板の light 盤面可読性・コードエディタ syntax 色の light 可読性。
