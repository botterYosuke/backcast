# findings 0107 — Settings ダイアログのデザイン刷新（2タブ化・入力欄判別）＋ DuckDB root を Settings へ移設

方針の正本は **[ADR-0026](../adr/0026-settings-dialog-consolidates-venue-mode-scenario.md)**（Settings 集約・immutable）、
**[ADR-0006](../adr/0006-replay-market-data-from-jquants-duckdb-retire-nautilus.md)**（Replay 市場データ源＝J-Quants
DuckDB 直読み・env/config 解決・immutable）、**[ADR-0028](../adr/0028-light-whiteboard-theme-miro-live-switch.md)**
（Appearance）、および **CONTEXT.md「Settings ダイアログ」/「catalog_path（環境/配置の関心・scenario 外）」/
「市場データソース（J-Quants DuckDB 直読み）」**。本 findings は owner の HITL 不満（「どれが入力か分からない・
雑多に並んでいるだけ」＋「.env の `BACKCAST_JQUANTS_DUCKDB_ROOT` を Settings に移動」）に対する grill（2026-06-25）で
確定した**下位決定（設計の木）**を記録する。ADR には書き戻さない（各 ADR は自己保護条項で固定）。

## owner 決定（grill 2026-06-25・AskUserQuestion）

- **D-A — 見た目方向 = 「2列フォーム + カード」**。各節を薄いカード面で囲い、ラベルを左列・入力欄を右列の2列に。
  入力欄は **枠線(border)＋プレースホルダ** でボタン・ラベルと明確に区別する（最大の不満点の直接修正）。
- **D-B — タブ構成 = 2タブ「実行 / 外観」**。「実行」= Venue + Mode + Scenario + Data、「外観」= Appearance。
  縦長回避のためタブ化（owner は「実行」タブがそれなりに縦長になることを了承済み）。
- **D-C — DuckDB root の正本 = アプリ UI（PlayerPrefs）一本化**。owner は .env を編集せず Settings ダイアログで完結。
  ただし**範囲は「アプリの UI だけ一本化」**——`engine/paths.py` の `.env` ローダ（`_load_dotenv_once`・setdefault）は
  残し、pytest / headless E2E runner / hitl の Python 単体経路は従来どおり `.env`/env で動く（PlayerPrefs は Unity 専用で
  これらは読めないため）。
- **D-D — 反映タイミング = 次の Replay 実行から即反映**。`engine/paths.py:jquants_duckdb_root()` は `os.environ` を
  毎回 lazy 読みするので、`os.environ` を更新すれば再起動不要で次 run に効く。
- **D-E — 入力 UI = テキスト欄 + フォルダ参照ボタン**。`[...]` でネイティブフォルダ選択。存在しないパス／
  `listed_info.duckdb` 欠落は赤エラーで表示（per-field error の既存パターン踏襲）。

## 下位決定（設計の木）

- **D1 — DuckDB root の配線は「既存 env 解決 seam に書き手を1つ足す」だけ（ADR-0006 と無矛盾）**。
  アプリ（`BackcastWorkspaceRoot`）が PlayerPrefs の値を **`Py.GIL()` 内で `os.environ["BACKCAST_JQUANTS_DUCKDB_ROOT"]`
  へ `SetItem`**（既存 `V19ReplayLiveE2ERunner.SetOsEnviron` と同型）。Python 側は従来どおり
  `os.environ.get("BACKCAST_JQUANTS_DUCKDB_ROOT")` で解決するので **ADR-0006 / CONTEXT「env/config で解決」を
  そのまま満たす**。書き込みタイミングは ① 起動時（Python init 後・初回 Replay 前）と ② Settings での保存/onEndEdit 時。
- **D2 — 永続化は app-global PlayerPrefs（`AppearanceStore` と同型）。sidecar には焼かない**。
  新 `JquantsDuckdbRootStore`（PlayerPrefs key `backcast.jquants_duckdb_root`）。**これは CONTEXT の不変条件を破らない**：
  CONTEXT「catalog_path（環境/配置の関心・scenario 外）」が禁ずるのは *per-run scenario panel フィールド化* と
  *scenario sidecar への焼き込み*（非可搬化）であって、**per-machine の app-global 設定面（Settings/PlayerPrefs）は
  まさに環境/配置の関心の正しい置き場**。#29 当時の「ユーザー向け panel フィールドへの昇格ではない」は *Scenario
  パネルへの per-run 昇格*を否定したもので、app-global Settings 面への移設はこれと別物（意図的な進化）。
- **D3 — 実効的な優先順位 = `DataEngine` ctor 引数 > os.environ（UI/PlayerPrefs、無ければ .env setdefault）**。
  アプリは `SetItem`（上書き）で書くので、`.env` が import 時に setdefault した値より UI 値が常に勝つ。CONTEXT の
  「ctor 引数 > .env」「未設定は hard error・nautilus catalog へ silent fallback しない」はそのまま維持。
- **D4 — フォルダ参照は `IFileDialog` を拡張**（FileDialog.cs）。現状 `SaveStrategyAs`/`OpenStrategy` は .py 専用で
  フォルダ選択メソッドが無い。`string BrowseFolder(string title, string initialDir)` を1本足し、
  `MacFileDialog`=`EditorUtility.OpenFolderPanel`（`#if UNITY_EDITOR`・owner は Mac/Editor 運用）、
  `Win32FileDialog`=native フォルダ選択（`IFileOpenDialog`+`FOS_PICKFOLDERS` 等）、`StubFileDialog`=`NextResult` seam で
  AFK headless 駆動。ビルド済み Player でネイティブ未対応の場合はボタンを隠してもテキスト欄は機能する（fail-soft）。
- **D5 — 視覚トークンは既存パレット role で賄う（新 role 追加なし）**。入力欄＝`surface_background`（節カードより一段
  沈めた面）＋`Outline`/`border`（focus は `border_focused`）＋ placeholder は `text_placeholder`、ラベルは `text_muted`、
  節ヘッダは `text_muted`（小・大文字）。Dark/Light 双方で role 解決（ADR-0028）。`#52`（spacing/typography トークン）は
  未着手なのでスペーシングは本スライス内の共有定数で統一する（後で #52 に寄せられる形）。
- **D6 — タブ切替は section container の表示トグル**。Venue の per-frame `Refresh()`（interactable 追従）/ Mode・
  Appearance の VM 連動 Refresh / Scenario tile の universe 購読は**タブ非表示中も無害に走る**（GameObject inactive でも
  `btn.interactable=` は安全）。「実行」タブが panel 高を超える場合は ScrollRect で吸収するか panel を実高に合わせる
  （実装時に確定）。

## 影響範囲（実装スライス）

- `Assets/Scripts/Live/SettingsModalOverlay.cs` — 絶対 y のセクション積み上げ → 2タブ chrome＋カード化。
- `Assets/Scripts/ScenarioStartup/ScenarioStartupTile.cs` — `MakeField` を枠付き・プレースホルダ・2列ラベルへ。
- `Assets/Scripts/Live/SettingsVenueSectionView.cs` / `SettingsModeSegmentView.cs` / `SettingsAppearanceSegmentView.cs` —
  カード/2列フォームへ視覚統一（brain は無改変）。
- 新規 `JquantsDuckdbRootStore.cs`（PlayerPrefs）＋ `Settings` の DATA 節ビュー＋ host 配線（`os.environ` への注入）。
- `Assets/Scripts/Layout/FileDialog.cs` / `MacFileDialog.cs` / `Win32FileDialog.cs` — `BrowseFolder` 追加。
- AFK probe: 入力欄判別（border/placeholder の非空）・2タブ切替・DuckDB root 保存→`os.environ` 反映・BrowseFolder stub の
  Action-ID 付き回帰（`behavior-to-e2e` で起こす）。

## 不変条件（保つ）

- runtime nautilus-free（ADR-0006）。DuckDB root 未設定時に nautilus catalog へ fallback しない。
- DuckDB root を **scenario sidecar / scenario panel の per-run フィールドにしない**（非可搬化禁止・CONTEXT）。
- 各節の brain（`VenueMenuViewModel`/`FooterModeViewModel`/`ScenarioStartupController`）は無改変（ADR-0026）。
- theme は inline color を足さず ThemeService role で解決（findings 0020）。

## 実装前の codebase 裏取り（grill 2026-06-25・実装着手時に確認）

設計の木を実コードに 1 項ずつ突き合わせた結果（#137 実装着手）。下位決定はすべて実コードと無矛盾で、
追加の owner 判断を要するフォークは無し。以下は実装スコープを確定させる裏取りメモ。

- **D5 の role は全て存在**（`ThemePalettes.cs:104-106`）: `surface_background`/`border`/`border_focused`/
  `text_placeholder`/`text_muted`/`element_background`/`element_selected`。新 role 追加不要を確認。
- **D1 注入の雛形** = `V19ReplayLiveE2ERunner.SetOsEnviron`（`Py.GIL()`＋`PyString`＋`environ.SetItem`、
  `Assets/Tests/E2E/Editor/V19ReplayLiveE2ERunner.cs:160-168`）。これを production host へ移植（テスト専用に留めない）。
- **D1 起動時注入点** = `BackcastWorkspaceRoot.Awake` の `_host.InitializePython(_venue)` 成功直後
  （`BackcastWorkspaceRoot.cs:285`・初回 Replay より前）。Python 未 init（非 owner / batchmode）時は no-op。
- **D4 再起動不要** = `engine/paths.py:jquants_duckdb_root()` が毎回 `os.environ.get` を lazy 読みするのを確認。
- **D2 store 雛形** = `AppearanceStore.cs`（PlayerPrefs key・`Save`/`Load`/`ClearForTests`）をそのまま写経。
- **D4 IFileDialog** = `FileDialog.cs:24` の `IFileDialog` に `BrowseFolder(title, initialDir)` を 1 本足す。
  `StubFileDialog`（`NextResult`/`LastInitialDir` seam）/`MacFileDialog`（`EditorUtility.OpenFolderPanel`・
  `#if UNITY_EDITOR`）/`Win32FileDialog`（native フォルダ選択）。owner は Mac/Editor 運用なので Editor 経路が主。

### 実装で確定した 2 つの下位決定（設計の木の枝・フォークではない）

- **F1 — ScenarioStartupTile の入力欄 role を Hakoniwa-isolated → Settings role へ repoint**。tile は元 Hakoniwa
  Startup スロットだが、ADR-0026 で Settings modal へ re-home 済みで現在の host 先は **Settings modal の
  ScenarioSection と `ThemeHitlHarness` のテーマ preview の 2 箇所のみ**（Hakoniwa canvas には載っていない）。
  よって `MakeField` を `hakoniwa_tile_background`/`hakoniwa_text` → `surface_background`+`border`+`text_placeholder`+
  `text_muted`（D5）へ移すのは findings 0054 違反ではなく**意図的進化**（screen-fixed modal の入力欄面）。
  field role を pin する probe は無し（`ScenarioStartupE2ERunner.cs` は validation/merge/registry のみ assert）。
- **F2 — tile/overlay の live re-theme を host に配線**。現状 `_tile.ApplyTheme()` は `ThemeService.Changed` に
  未購読（`ApplyViewportTheme` は 4 base dock panel と `_settingsAppearanceView.Refresh()` のみ呼ぶ）。Appearance
  切替は **この Settings modal 内**で起きるので、redesign 後の入力欄が live 切替で追従するよう
  `ApplyViewportTheme` に `_tile?.ApplyTheme()` ＋ overlay/Data 節の `ApplyTheme()` を足す（S1 AC「Dark/Light 両方」）。

### S2 縦長の扱い（AC で実装時確定とされた項目）

「実行」タブ（Venue+Mode+Scenario+Data）は縦長になるため、**panel を実コンテンツ高に合わせて伸ばす**方針で
着地（ScrollRect は uGUI 依存が増え AFK 反射が複雑化するため見送り）。タブ chrome はカード積み上げの実高から
panel sizeDelta を算出する。`SettingsModalOverlay` は元々 panel 高を固定値（580×668）で持っていたので、
タブごとのコンテンツ高に追従する形へ変更する。

### S4 BrowseFolder のプラットフォーム（AC で実装時確定とされた項目）

`MacFileDialog` は `EditorUtility.OpenFolderPanel`（`#if UNITY_EDITOR`）で実装＝owner の Mac/Editor 運用を主経路に。
`Win32FileDialog` は `IFileOpenDialog`+`FOS_PICKFOLDERS`（native フォルダ選択）。Player でネイティブ未対応時は
**参照ボタンを隠してもテキスト欄は機能する**（fail-soft）。AFK は `StubFileDialog.BrowseFolder` の seam で駆動。

## 実装着地 + 回帰ゲート（#137・2026-06-25・全 GREEN）

S1–S4 を 1 セッションで実装。AFK 全 section GREEN・compile `error CS` 0 件・pytest GREEN。

### 実装ファイル

- 新規: `JquantsDuckdbRootStore.cs`（PlayerPrefs・`AppearanceStore` 同型）/ `JquantsDuckdbRootValidator.cs`（folder+listed_info
  存在の純 validation）/ `JquantsDuckdbRootInjector.cs`（`Py.GIL()`+`PyString` で `os.environ` SetItem・V19 `SetOsEnviron` 同型）/
  `SettingsDataSectionView.cs`（Data 節 view）/ `DuckDbRootSettingsE2ERunner.{cs,md}`。
- 改修: `SettingsModalOverlay.cs`（絶対 y 積み上げ → 2 タブ chrome＋カード・全 role 解決＋`ApplyTheme`）/
  `ScenarioStartupTile.cs`（`MakeField` を 2 列・枠線(Outline)・プレースホルダ・muted ラベル・role を Hakoniwa→Settings へ repoint）/
  `SettingsDataSectionView` を含む host 配線（`BackcastWorkspaceRoot.cs`: Data 節 build・起動時 `os.environ` 注入・`ApplyViewportTheme`
  に tile/overlay/data の `ApplyTheme` 追加）/ `FileDialog.cs`+`MacFileDialog.cs`+`Win32FileDialog.cs`（`BrowseFolder` 追加）。

### Action-ID ゲート（rollup レール）と delete-the-logic RED litmus

| Action-ID | 検証 | RED litmus（production を壊すと落ちる） |
|---|---|---|
| SETTINGS-09 | tile 全 InputField の border(`Outline`=border)・placeholder(`text_placeholder` 非空)・fill(`surface_background`)・body(`text`)・muted ラベル存在 | `MakeField` の Outline/placeholder を消す or インライン色にする → RED |
| SETTINGS-10 | 2 タブ既定=実行・section の tab-group 親・`SelectTab`/onClick で activeSelf＋タブ色入替 | `SelectTab` を no-op / 2 group を alias → RED |
| SETTINGS-11 | panel=`panel_background`・5 カード=`elevated_surface_background`・card≠panel | カードをインライン色 / カード面撤去 → RED |
| DUCKROOT-01 | store Save→Load 往復・空=clear・ClearForTests | sidecar/per-run 化（非可搬）→ 再 process で保持されず |
| DUCKROOT-02 | 純 validator（空=OK・無フォルダ=error・listed_info 欠落=error・有=OK） | listed_info 存在チェック撤去 → RED |
| DUCKROOT-03 | `[...]`→field/store/onCommit 反映・border/placeholder・cancel fail-soft・不正で赤エラー | `[...]` 未配線 → field/store 空のまま RED |
| DUCKROOT-04 | MOCK Python: Inject→`os.environ` readback 一致→`engine.paths.jquants_listed_info_path()` 解決（再起動不要） | `Inject` を no-op → readback≠root RED |
| pytest `test_paths_dotenv.py` | engine 側 lazy re-read（runtime 更新が次 call に反映）＋listed_info 解決 | 既存 resolver の lazy 読みを cache 化 → RED |

実走（`/Applications/Unity/Hub/Editor/6000.4.11f1`・batchmode）: `[E2E SETTINGS-01..11 PASS]`＋`[E2E SETTINGS DIALOG PASS]` /
`[E2E DUCKROOT-01..04 PASS]`＋`[E2E DUCKROOT PASS]`（DUCKROOT-04 は MOCK Python・exit 0 だが #107 規約でタグが正本）/
`ScenarioStartupE2ERunner` 回帰 GREEN（tile の role/2 列化が SCENARIO-07/08/12 の behavioral contract を壊していない）/
`uv` の cryptography 再ビルド不可のため pytest は `.venv/bin/python -m pytest`＝10 passed。

### 実装中に出た判断

- **SETTINGS-09 採番衝突**: 旧台本の SETTINGS-09 は HITL（実 OS 目視）だったが、自動セクション 3 本を 09/10/11 に採り、
  HITL 行を **SETTINGS-12 へ採番替え**（HITL 行は tag/コード参照が無いので安全・E2E-INDEX も追従）。
- **DUCKROOT-03 の test 忠実度**: 当初 `onEndEdit.Invoke(bad)` で field.text を据え置いたため `RefreshError` が旧有効値を
  検証し赤エラーが立たず初回 RED。production は正しい（Unity の onEndEdit は常に field.text を渡す）。test を Unity 忠実に
  `SetTextWithoutNotify(bad)`→`onEndEdit.Invoke(field.text)` へ直して GREEN（＝gate が非 vacuous であることの確認）。
- **Venue 節の live re-theme は範囲外**: `SettingsVenueSectionView` は build 時に色を焼くため Dark/Light 即時追従しないが、
  本 issue の対象（入力欄/カード/タブ/Data/tile/mode/appearance は追従）外。再オープンで正しい色になる。将来 view に `ApplyTheme` 追加可。

### code-review(high) 後の修正（同セッション・全 GREEN 再確認）

high-effort code-review が 4 correctness/regression ＋ 1 Medium cleanup を検出、全て修正:

1. **ThemeProbe §4a RED（既存ゲート回帰）**: tile bg を `Color.clear` にしたため `ThemeProbe` の wiring-kill（旧
   `hakoniwa_panel_surface` 採取）が RED。**field FILL（`surface_background`）採取へ re-point**（findings 0054 の
   tile-panel-isolation は #137 で card へ移譲＝tile は surface を持たない）。`[THEME PASS]` 再確認。
2. **Injector の clear が os.environ を残す（Medium correctness）**: `Inject("")` が早期 return で既注入値を残し、UI を空に
   しても次 Replay が stale path を使う。**初回 Inject で .env baseline を捕捉し、空入力時は baseline を復元**（baseline 無し
   なら DelItem で UNSET＝ADR-0006 hard error）。`ResetBaselineForTests` も追加。
3. **Inject が InitializePython の try 内（Medium）**: 注入例外が `_isOwner=false` を立て初期化済みインタプリタを捨てる。
   **Inject を独立 try/catch（log-and-continue）に分離**。
4. **MakeCard の `sizeDelta` が横 inset を消す（視覚）**: 横ストレッチ rect に `sizeDelta.x=0` を後設定すると Margin/CardInsetX
   inset が 0 に上書きされカードが panel 端に張り付く。**offsetMin/offsetMax で横 inset＋縦 top/height を両方エンコード**へ修正。
5. **入力欄 widget の重複（Medium cleanup）**: tile.MakeField と Data view が同じ border+placeholder+fill+body を二重実装。
   **`ThemedInputFieldBuilder`（共有 widget＋`ThemedInputField.ApplyTheme`）へ抽出**し両者が利用＝#52 token 化で 1 箇所修正で済む形に。

再走: `[E2E SETTINGS-01..11 PASS]` / `[E2E DUCKROOT-01..04 PASS]`（exit 139=shutdown segfault・タグが正本）/
`[E2E SCENARIO STARTUP PASS]` / `[THEME PASS]` / compile `error CS` 0 件。Low cleanup（`Anchor`/`Stretch`/button 二重）は据え置き。

### review fixes (HIGH/MEDIUM round 2)

- HIGH 1: ApplyViewportTheme が Venue/Mode/[x] を rebake していなかった漏れを修正（_settingsVenueView.ApplyTheme / _settingsModeView.Refresh / SettingsModalOverlay の [x] retained graphic 追加）
- HIGH 2: ThemeHitlHarness scenario panel の Image が paint されず Unity デフォルト白で表示されていた回帰を修正（panel_background role で paint）
- HIGH 8: SettingsModalOverlay.cs の ApplyTheme doc-comment を実装 (close button + section views 経由) と整合
- MED 5: SETTINGS-13 AFK probe 追加 — Dark→Light の live re-theme で chrome 全 role が rebase されることを assert（HIGH 1+2 の RED litmus）
- HIGH 3: SettingsDataSectionView.Commit を validator-first 順に変更（Save → Validate → valid なら onCommit → RefreshError）。invalid path が os.environ に注入され .env baseline を遮蔽していた D3 違反を修正
- HIGH 4: JquantsDuckdbRootInjector / Store / Validator の string.IsNullOrEmpty を IsNullOrWhiteSpace に変更（空白 1 文字以上が「設定済み」扱いされ Python 側で REPO_ROOT/" " を生成していた bug を修正）
- MED 6: python/tests/test_paths_dotenv.py に @pytest.mark.scenario("DUCKROOT-04") を付与（CLAUDE.md Gap 3 / Action-ID rollup 規約）
- DUCKROOT-04 lazy-reread test を OS-portable に修正（POSIX 絶対パスリテラル `/first/root` は Windows で `Path.is_absolute()=False` のため `resolve_repo_relative` で repo 配下に解決される。`tmp_path` 由来の OS-appropriate 絶対パスに置換。実装側は不変 — `.env` 契約の絶対判定は `Path.is_absolute()` で正しい）
- MED 7: Win32FileDialog.BrowseFolder で `folder` / `result` の IShellItem が AddRef'd 戻り値だが finally で release されていなかった漏れを修正（LIFO で result → folder → dialog を release）

### review fixes (HIGH/MEDIUM round 3)

- HIGH 9 (cross-session re-inject): BackcastWorkspaceRoot 起動時 Inject に validator-first を追加。前回 session で persist された invalid root が boot で env に再注入され Replay を ADR-0006 hard error させていた問題を修正。invalid は empty 文字列で Inject = .env baseline 復帰（Commit 経路の HIGH 3 fix と対称）。
- HIGH 10 (same-session stale env): Commit で invalid 値を受けたとき `_onCommit?.Invoke("")` で env を baseline へ明示 revert。旧経路（valid → invalid の順）は Inject skip で env が前回 valid 値のまま残り UI/Store/env が 3-way 乖離していた。
- MED 8 (SETTINGS-13 Manual/Auto 未 assert): modeVm.ApplyPoll で VenueLive=true を seed し、Manual/Auto seg が active になった状態で 3 seg すべて rebake assert。vacuity guard を `modeChecked < 3` に強化。HIGH 1 RED litmus が Replay 1 個でしか効いていなかった漏れを修正。
- MED 9 (whitespace UX 乖離): Commit で whitespace-only を空文字に正規化し field/store/env/再オープン UI を同一の "no override" 状態に揃える（findings 0107 D3 の UI 波及）。
