# UniverseSidebarE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`UniverseSidebarE2ERunner.cs`（第二波で実装）が自動検証する **ユニバース sidebar サーフェス**の台本。実装者は
`.cs` と本 `.md` をセットで読む。これは調査メモではなく、**この サーフェスでユーザーができる行動すべての網羅台帳と、
E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（1サーフェスでユーザーができる操作を網羅する回帰ゲート）。
> 複数サーフェスをまたぐ実ユーザーストーリー（universe 編集 → run → 箱庭/depth 更新）は *Journey E2E*
> （`ReplayToHakoniwaE2ERunner`）が担う。本 Surface 台本は「sidebar の入力が正しい SoT 更新・writeback・focus
> 遷移を起こすか」までを観測し、その先（depth tile の再配置・run 結果）は Journey 側へ寄せる。

## 対象サーフェス

画面固定の左 sidebar の Instruments セクション（`UniverseSidebarView` ＋ 頭脳 `UniverseSidebarController`、TTWR
`sidebar.rs` の `update_sidebar_system` + `instrument_row_click_system` + `instrument_remove_button_system` parity）。
ユニバースの SoT は `InstrumentRegistry`（#29 が所有、一 workspace 一 universe）で、sidebar/picker（#31）と #29 の
text editor が**同じ SoT に差し込む**。picker は `InstrumentPickerController`（#31）を再利用。focus（depth ターゲット）は
`SelectedSymbol`、disk 反映は `UniverseWriteback`（Replay-gated）。Python-FREE（候補供給 `IAvailableInstrumentsProvider`
は root が注入し、実 DuckDB/venue universe は別 issue 所有）。

## 対象ユーザー行動

銘柄行クリックで focus（depth）を移す、× で universe から削除、[+ Add] でピッカー開閉、検索入力でフィルタ、候補(+)で
追加（100ms debounce ＋ sidecar flush）、追加済み(✓)候補の再クリック、ロック registry（`instruments_ref`）での編集抑止、
供給ステータス別 placeholder 表示。z-order/前面描画と実 InputField の focus 保持は視覚/EventSystem 依存なので HITL。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| SIDEBAR-01 | 銘柄行をクリックして focus（depth ターゲット）を移す | `UniverseSidebarView.cs:140`→`UniverseSidebarController.cs:99` | `_selected.Set`＋`Changed` 発火、行 `Selected` フラグ（`▶`）、`focus → depth` ラベル更新、`DepthDecoder` が focus を追従 | `UniverseSidebarProbe.Section6`＋`Section8` を昇格して assert | 自動(Probe有・要昇格) | `UniverseSidebarProbe` |
| SIDEBAR-02 | Live モードで行クリック → deferred subscribe hook（Replay は発火しない） | `UniverseSidebarController.cs:102` | `LiveSubscribeHook` が Live のみ発火（#31 は null seam）、Replay は focus 移動のみ | `Section6` を昇格、hook を差し込んで Live/Replay の差を assert | 自動(Probe有・要昇格) | `UniverseSidebarProbe` |
| SIDEBAR-03 | × で universe から削除（SoT 更新 ＋ sidecar flush） | `UniverseSidebarView.cs:148`→`UniverseSidebarController.cs:88` | `registry.Remove`、`writeback.Flush`（Replay-gated・既存 sidecar を mutate-existing、起動 window 保持）、`Registry.Changed`→`Rebuild` | `Section5`（remove）＋`Section7`（writeback）を昇格 | 自動(Probe有・要昇格) | `UniverseSidebarProbe` |
| SIDEBAR-04 | ロック registry（`instruments_ref`）で × が no-op | `UniverseSidebarController.cs:90` | `Editable=false` → `Remove` 拒否（TTWR parity）、SoT 不変、flush なし | `Section5` のロック分岐を昇格 | 自動(Probe有・要昇格) | `UniverseSidebarProbe` |
| SIDEBAR-05 | [+ Add] でピッカー開閉 | `UniverseSidebarView.cs:83`→`UniverseSidebarController.cs:73`→`InstrumentPickerController.cs:52` | `Picker.Visible` トグル、Replay は `ReplayEndSnapshot`（scenario.end）取得・Live は snapshot なし、ボタンラベル `+ Add`⇄`− Close` | `Section1` の open/close/snapshot を昇格 | 自動(Probe有・要昇格) | `UniverseSidebarProbe` |
| SIDEBAR-06 | 検索入力で候補をフィルタ | `UniverseSidebarView.cs:164`→`InstrumentPickerController.cs:82` | `SetQuery`→`BuildList`：case-insensitive contains、ordinal sort、`take(15)`（`MaxRows`）、絞り込み 0 件で `No matches` placeholder | `Section3` を昇格（filter/sort/cap/no-matches） | 自動(Probe有・要昇格) | `UniverseSidebarProbe` |
| SIDEBAR-07 | 候補(+)行クリックで追加（100ms debounce ＋ flush） | `UniverseSidebarView.cs:193`→`UniverseSidebarController.cs:80`→`InstrumentPickerController.cs:129` | `registry.Add`、同一 id 100ms `DebounceMs` 抑止、`writeback.Flush`、ピッカーは開いたまま（連続追加） | `Section4`（add/debounce/lock）＋`Section7`（flush） | 自動(Probe有・要昇格) | `UniverseSidebarProbe` |
| SIDEBAR-08 | 追加済み(✓)候補の再クリック | `InstrumentPickerController.cs:120,134` | `AlreadyAdded` フラグ表示（`✓`）、`registry.Add` が false（dedup no-op・SoT 不変） | `Section3`（already-added フラグ）＋`Section4`（dedup） | 自動(Probe有・要昇格) | `UniverseSidebarProbe` |
| SIDEBAR-09 | 供給ステータス別の placeholder 表示 | `InstrumentPickerController.cs:91`-`101` | EndUnset/Loading/Error/NotConnected/Empty で各 placeholder（mode 別メッセージ）。Ready ＋ 0 件は Empty 扱い | `Section2`（全 `UniverseStatus`→placeholder）を昇格 | 自動(Probe有・要昇格) | `UniverseSidebarProbe`（※実 provider 配線は別 issue＝**要確認**） |
| SIDEBAR-10 | ロック中の [+ Add] 抑止 / 開放中ロックで force-close | `InstrumentPickerController.cs:54,68` | `Editable=false` → `Toggle` 開かない、開いている最中に lock → `ForceCloseIfLocked` で閉じ query クリア | `Section1` のロック分岐を昇格 | 自動(Probe有・要昇格) | `UniverseSidebarProbe` |
| SIDEBAR-11 | 外部編集（#29 text field / system prune）が sidebar に反映 | `UniverseSidebarView.cs:54,98`（`Registry.Changed`→`Rebuild`） | SoT の `Changed` で行を再構築。brain 側 SoT 回帰（`Rows`/`PruneRetain`→`Changed`）は別 Probe 所有。view の再構築配線（uGUI）は未テスト | view の `Changed`→`Rebuild` 反映を反射で assert（SoT 回帰は `UniversePruneProbe` 所有） | 要新規自動化 | `UniversePruneProbe`（SoT 側） |
| SIDEBAR-12 | 検索フィールドのキーボード編集（focus 保持） | `UniverseSidebarView.cs:154`-`168`（field を open 毎に 1 回だけ生成） | per-keystroke の list 再構築で field を破棄せず focus を奪わない。brain は `AppendChar`/`Backspace`/`Escape`（`InstrumentPickerController.cs:75`-`77`） | brain の query 編集のみ自動化可。実 `InputField` の focus 保持は実 EventSystem 依存 | HITL専用（実 EventSystem の focus 保持・GPU/実ウィンドウ前提） | `UniverseSidebarHitlMenu` |
| SIDEBAR-13 | dropdown z-order / sidebar 前面描画（視覚） | `UniverseSidebarView.cs:28,66`-`67`（`SIDEBAR_SORT` ＜ `MENU_SORT`） | sidebar が menu dropdown の背面、windows の前面、クリック取りこぼし無し（findings 0045） | — | HITL専用（実ピクセル＋EventSystem raycaster・GPU/実ウィンドウ前提） | `UniverseSidebarHitlMenu` |
| SIDEBAR-14 | 空ユニバースの "No instruments" 表示 | `UniverseSidebarView.cs:110`-`111` | `registry.Count==0` で空ラベル、1 行ぶんの高さを確保（rows 高さ計算） | rows 描画（uGUI）を反射確認。brain 側は `Count` で自明 | 要新規自動化 | — |

> focus→depth ラベル・行ハイライト（`▶`/`element_selected`）・ボタンラベルは入力のない**表示**なので独立行にせず、
> SIDEBAR-01/05 の観測点として併せて確認する（`UniverseSidebarView.Rebuild` の合成）。

## 観測点（詳細）

- **SIDEBAR-01/02（行クリック→focus）**: `UniverseSidebarController.SelectRow` が `SelectedSymbol.Set`（移動時のみ
  `Changed` 発火）。Replay は focus のみ、Live は `LiveSubscribeHook`（#31 は null の deferred seam）も叩く。実消費者
  `DepthDecoder.Decode(state, SelectedSymbol.Value)` が focus を追従する（`Section8` が正本）。
- **SIDEBAR-03/04（× remove）**: `Remove` は `Editable` ゲートで no-op 可。削除成功時のみ `UniverseWriteback.Flush`
  を呼び、**既存 sidecar を mutate-existing**（start/end/granularity/initial_cash を保持・#67）。Live は Replay-gated
  で flush しない。`Section7` が writeback 不変条件（content-diff coalesce / path-unresolved skip / Prime）を所有。
- **SIDEBAR-05/10（picker 開閉・ロック）**: `Toggle` は Replay でのみ `ReplayEndSnapshot` を取り、Live は null。ロック
  registry は開かず、開放中にロックされたら `ForceCloseIfLocked` が閉じて query をクリア（TTWR
  `force_close_picker_on_lock_system`）。
- **SIDEBAR-06/08/09（候補リスト）**: `BuildList` は status placeholder → empty-source → query filter+sort+take(15)
  → no-matches の順（`picker_list_rebuild_system` parity）。`AlreadyAdded` は `registry.Ids.Contains(id)`。**要確認**:
  実 `IAvailableInstrumentsProvider`（DuckDB/venue universe）は別 issue 所有で、本台本は stub provider で status→行
  マッピングのみを観測する。
- **SIDEBAR-07（候補追加）**: `ClickRow` は同一 id 100ms（`DebounceMs`）debounce、`registry.Add`（dedup）、ピッカーは
  開いたまま。`AddFromPicker` が追加成功時に `writeback.Flush`。
- **SIDEBAR-11（外部反映）**: sidebar は `Registry.Changed` を購読して `Rebuild`（同一 SoT を編集する #29 text field と
  system prune の双方を polling 無しで反映）。SoT 側の prune→`Changed`→downstream 反映は `UniversePruneProbe.Section5`
  が所有。view の再構築配線は uGUI なので新規に反射で押さえる。

## 自動判定（合格条件）

- ログに `[E2E UNIVERSE SIDEBAR PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+`
  が 0 件。
- 各 `自動(*)` 行の観測点を 1 つでも落としたら `[E2E UNIVERSE SIDEBAR FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus:
  - `InstrumentPickerController.ClickRow` の debounce 分岐（`_lastAddedId == id && ... < DebounceMs`）を消すと
    SIDEBAR-07 の debounce assert が落ちる。
  - `UniverseSidebarController.Remove` の `if (!_registry.Editable) return false` を消すと SIDEBAR-04 が落ちる。
  - `SelectRow` の `if (mode == Live) LiveSubscribeHook?.Invoke(id)` を消すと SIDEBAR-02 が落ちる。
  - `UniverseWriteback` の Replay ゲートを外すと SIDEBAR-03/07 の Live-no-flush assert が落ちる。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `UniverseSidebarProbe` | batchmode・Python-FREE・brain seams | SIDEBAR-01〜10 の正本。`Section1`〜`Section8` を E2ERunner へ昇格（picker open/lock、status placeholder、filter/sort/take15、add/debounce、remove、focus/Live-hook、writeback、depth-follow） |
| `UniversePruneProbe` | batchmode・SoT prune | SIDEBAR-11 の SoT 側回帰（`PruneRetain`→`Changed`→downstream mirror）を所有。sidebar の view 反映は本 runner が補う |
| `UniverseSidebarHitlMenu`（`UniverseSidebarHitlHarness`） | HITL ハーネス（Play mode・OnGUI） | SIDEBAR-12/13 の focus 保持・z-order 視覚確認用に**探索 Probe として残す** |

## 将来の `UniverseSidebarE2ERunner.cs` 実装方針（第二波）

- `UniverseSidebarProbe` は既に実 root を要しない Python-FREE な brain ゲート。E2ERunner 化は **`UniverseSidebarProbe`
  の `Section1`〜`Section8` を移送**し、PASS/FAIL ログ規約（`[E2E UNIVERSE SIDEBAR PASS/FAIL]`）と exit code を揃える
  のが主作業。stub（`StubProvider` / `StubStrategyProvider`）はそのまま流用。
- SIDEBAR-11/14 の view 反映（`Registry.Changed`→`Rebuild`、空ラベル）を押さえるには、`MenuBarCutoverProbe` と同型に
  **実 `BackcastWorkspaceRoot` を反射合成**（`OpenScene` → `SetSynthesizer(FakeMarimoSynthesizer)` → `ResolvePaths` →
  `BuildWorkspace`）して `UniverseSidebarView.Bind` を実行し、private（`_rowsRoot` の childCount 等）を反射確認する。
  Python-FREE を既定とする（picker 供給は stub provider）。
- セクション構成は操作一覧表の `自動(*)` 行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを返す `Execute()`
  （null=PASS）パターン。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod UniverseSidebarE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep で grep**（PowerShell `Select-String` は取りこぼす）。
