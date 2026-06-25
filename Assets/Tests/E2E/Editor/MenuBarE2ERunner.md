# MenuBarE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`MenuBarE2ERunner.cs`（第二波で実装）が自動検証する **メニューバー サーフェス**の台本。実装者は `.cs` と
本 `.md` をセットで読む。これは調査メモではなく、**この サーフェスでユーザーができる行動すべての網羅台帳と、
E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（1サーフェスでユーザーができる操作を網羅する回帰ゲート）。
> 複数サーフェスをまたぐ実ユーザーストーリーは *Journey E2E*（例 `ReplayToHakoniwaE2ERunner`）が担う。
> File→Open の「開いた後どうなるか（universe seed → run → 箱庭更新）」は Journey 側の責務で、本 Surface 台本は
> 「メニュー操作が正しい host 呼び出し・状態遷移を起こすか」までを観測する。

## 対象サーフェス

グローバルメニューバー（`MenuBarView` ＋ 頭脳 `MenuBarViewModel`）。**#128/ADR-0026 で Venue トップレベル＋
dropdown は退役** → File / Edit / Help の **3 トップレベル**。Venue 接続/切断は [SettingsDialogE2ERunner](./SettingsDialogE2ERunner.md)
（SETTINGS-08）へ移設（`VenueMenuViewModel`#21 は不変・再利用）。venue 接続状態の **badge** は menu に残る。
Help→Settings は **#125/ADR-0026 で Settings モーダルを開く**（旧 deferred stub を実体化）。File は **レイアウト/
ドキュメントの I/O**（戦略 `.py` 本体の編集は Strategy Editor サーフェス）。

## 対象ユーザー行動

トップレベルの開閉、File（New/Open/Save/Save As）、Edit（Undo/Redo＝現状 stub）、Venue（Connect 各種/Disconnect）、
Help（Settings＝現状 stub）、メニュー外クリックで閉じる backdrop。badge は入力のない表示なので「行動」ではなく
観測点として扱う。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| MENU-01 | File メニューを開閉 | `MenuBarView.cs:113`→`Toggle` | private `_open` が `File`⇄`None`、dropdown `SetActive`、backdrop 表示 | 反射で `_open` 遷移を assert | 要新規自動化 | — |
| MENU-02 | File → New（クリア） | `MenuBarView.cs:186`→`OnFileNew` | notebook が 1 空セルへ・`!IsBound`、region_001 は**リセット（破棄せず同 rect）**、region_002 は despawn、universe クリア、接続中は `SetExecutionMode("LiveManual")` | `MenuBarCutoverProbe.Section1` を昇格して assert | 自動(Probe有・要昇格) | `MenuBarCutoverProbe` `MenuBarVerify` |
| MENU-03 | File → New（実行中は拒否） | `MenuBarViewModel.cs:57` | `FileNewDecision.RefusedRunning`＋拒否メッセージ、workspace 不変 | replay/auto 実行フラグを立てて拒否を assert | 自動(Probe有・要昇格) | `MenuBarVerify` |
| MENU-04 | File → Open…（ドキュメント） | `MenuBarView.cs:187`→`OnFileOpen` | native picker → `.py` 読込＋layout sidecar 復元。Live 中なら開く前に `SetExecutionMode("LiveAuto")` | StubFileDialog で picker を差し替え、open 後の状態＋mode 副作用を assert | 自動(Probe有・要昇格) | `BackcastWorkspaceProbe` `MenuBarVerify` |
| MENU-05 | File → Save（レイアウト） | `MenuBarView.cs:188`→`OnFileSave` | `<strategy>.json` の `layout` キーが書かれる（箱庭順・窓 rect・canvas pan/zoom） | 一時パスへ save → 再 load で round-trip 一致 | 自動(Probe有・要昇格) | `ReplayLayoutProbe` `MultiDocLayoutProbe` |
| MENU-06 | File → Save As…（ドキュメント） | `MenuBarView.cs:190`→`OnFileSaveAs` | native picker で `.py`/`.json` ペアを新名へ fork、currentDocumentPath 更新 | StubFileDialog で保存先を差し替え、両ファイル生成を assert | 自動(Probe有・要昇格) | `MultiDocLayoutProbe` |
| MENU-07 | Edit メニューを開閉 | `MenuBarView.cs:114`→`Toggle` | `_open` が `Edit`⇄`None` | 反射で `_open` 遷移 | 要新規自動化 | — |
| MENU-08 | Edit → Undo | `MenuBarView.cs:198` | 無効 stub（`interactable=false`・active editor 未配線） | — | 対象外（未配線 stub。実 Undo は Strategy Editor サーフェス＝`StrategyEditorNotebookE2ERunner`） | — |
| MENU-09 | Edit → Redo | `MenuBarView.cs:199` | 無効 stub | — | 対象外（同上） | — |
| MENU-10 | ~~Venue メニューを開閉~~ | — | — | — | **対象外（#128/ADR-0026: Venue dropdown＋トップレベル退役 → [SettingsDialogE2ERunner](./SettingsDialogE2ERunner.md) SETTINGS-08）** | — |
| MENU-11 | Venue → Connect MOCK（dev・Editor 限定） | `SettingsVenueSectionView`（旧 menu dropdown） | `_onConnect("MOCK","")` 発火、Python 起動・接続 | mock venue で接続 ACK を assert（実 venue 不要） | 自動(E2E済・移設) | `VenueMenuM3Probe` `VenueLoginSecretProbe`（venue 表面は SETTINGS-08） |
| MENU-12 | Venue → Connect（4 parity variant・prod 常時 enable） | `VenueMenuViewModel.CanConnectEnv` | **ADR-0027: prod 解禁の env ゲート廃止**。切断中は prod も含め全 variant が `interactable=true`、接続中は全 disable（`CanConnect` に収斂） | `VenueMenuViewModel` の gate を assert（接続自体は HITL）。Action-ID `PRODGATE-07` | 自動(Probe有・要昇格) | `MenuBarVerify`（表面は SETTINGS-08） |
| MENU-13 | Venue → Connect（実 venue へ接続） | `SettingsVenueSectionView` → `_host.VenueLogin` | 実 kabu/立花 へログイン、secret modal が Settings の上に重なる、badge が `CONNECTED` | — | HITL専用（実 venue 接続・外部認証/秘密情報依存） | `VenueLoginSecretHitlMenu` |
| MENU-14 | Venue → Disconnect | `SettingsVenueSectionView` → `_onDisconnect` | `venue.CanDisconnect` のとき発火、badge が `DISCONNECTED` へ収束 | gate は自動（SETTINGS-08）、切断 RPC は mock で assert | 自動(E2E済・移設) | `VenueMenuM3Probe`（表面は SETTINGS-08） |
| MENU-15 | Help メニューを開閉 | `MenuBarView.cs:116`→`Toggle` | `_open` が `Help`⇄`None` | 反射で `_open` 遷移 | 要新規自動化 | — |
| MENU-16 | Help → Settings | `MenuBarView.BuildHelpMenu`→`OpenSettings` | **#125/ADR-0026: クリックで Settings モーダルが開く**（旧 deferred stub を実体化） | `SettingsModalController.IsOpen` を assert（SETTINGS-01） | 自動(E2E済・SettingsDialog) | `SettingsDialogE2ERunner`(S01) |
| MENU-17 | メニュー外クリックで閉じる（backdrop） | `MenuBarView.cs:139` | backdrop クリックで `_open=None`、クリックは下層 sidebar へ届かない | backdrop の onClick を駆動し `_open` を assert（EventSystem の実 raycast 経路は HITL） | 要新規自動化 | — |
| MENU-18 | dropdown の z-order・前面描画（視覚） | findings 0045 | dropdown が sidebar の前面、secret modal が最前面、クリック取りこぼし無し | — | HITL専用（実ピクセル＋EventSystem raycaster・GPU/実ウィンドウ前提） | `MenuBarHitlMenu` |
| MENU-19 | Venue の LIVE_VENUE 絞り込み（ADR-0021・**Settings の Venue セクションへ移設**） | `SettingsVenueSectionView.Build`→`VenueMenuViewModel.VisibleConnectItems` | LIVE_VENUE 未設定→全 variant 表示（＋Editor 限定 MOCK dev＝Editor 5/Player 4）／明示 pin→その venue の variant のみ（Tachibana=2・kabu=2・MOCK=1）。**pinned MOCK は editor/player 両方で MOCK connect のみ＝player の空 dead-end を回避（#106）**。menu はサーバ venue をロックせず engine が login 時に再バインドする（filter は presentational） | `VenueMenuViewModel.VisibleConnectItems` を直接 assert（未設定/Editor=5・未設定/Player=4・pin 各 venue の項目集合・**pinned MOCK は editor/player 両方=1 #106**・delete-the-filter litmus） | 自動(Probe有・要昇格) | `VenueMenuM3Probe`（`VenueMenuFilterByLiveVenue`） |

> badge（mode/venue 表示・transient orange message）は入力の無い**表示**なので行動行には載せない。MENU-02/04/14 の
> 観測点として badge 文字列を併せて確認する（`MenuBarView.Refresh` の badge 合成）。

## 観測点（詳細）

- **MENU-02/03（File→New）**: `MenuBarCutoverProbe.Section1` が既に正本。adopt 不変条件（region_001 は破棄せず
  in-place リセット・findings 0025 §8）＋ region_002 despawn ＋ universe クリアを assert。実行中拒否は
  `MenuBarViewModel.FileNew → RefusedRunning`（`MenuBarVerify`）。接続中の `LiveManual` 昇格も `MenuBarVerify`。
- **MENU-04（File→Open）**: open の **副作用（mode）** は `MenuBarViewModel.FileOpenModeSideEffect`（`MenuBarVerify`）。
  open 後の universe seed → tile spawn は **Journey 側**（`ReplayToHakoniwaE2ERunner` steps 2-3）で観測済みなので、
  本 Surface 台本では「StubFileDialog のパスが `coordinator.Open` に渡り mode 副作用が出る」までを観測する。
- **MENU-05/06（Save / Save As）**: layout sidecar の round-trip と `.py`/`.json` ペア fork。`MultiDocLayoutProbe`/
  `ReplayLayoutProbe` の assert を昇格。
- **MENU-12（prod 常時 enable・ADR-0027）**: `VenueMenuViewModel.CanConnectEnv` の gate（`MenuBarVerify`）。prod 解禁の
  env ゲート（`*_ALLOW_PROD` グレーアウト）は廃止＝`CanConnectEnv` は `CanConnect` に収斂し、切断中は prod も含め全 variant が
  enable・接続中は全 disable。delete-litmus: prod を再びグレーアウトすると prod-enable assert が RED（`[E2E PRODGATE-07 PASS]`）。
- **MENU-19（LIVE_VENUE 絞り込み・ADR-0021）**: どの venue variant が *出現するか* を `VenueMenuViewModel.VisibleConnectItems`
  が決める（MENU-12 は出現した変種の *enable 状態*、MENU-19 は *出現集合* ＝直交）。`VenueMenuM3Probe.VenueMenuFilterByLiveVenue`
  が未設定/Editor=5・未設定/Player=4・pin Tachibana/kabu=2・pin MOCK=1 を assert。delete-the-filter litmus: `VisibleConnectItems`
  が絞り込みを止めると pinned ケースが他 venue を漏らし RED。engine 側の再バインド（VENUE_MISMATCH 撤去）は Python seam
  `python/tests/test_venue_mismatch_inproc_server.py`（findings 0085）が正本＝この台本は menu 出現集合のみを担当。

## 自動判定（合格条件）

- ログに `[E2E MENU BAR PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+` が 0 件。
- 各 `自動(*)` 行の観測点を 1 つでも落としたら `[E2E MENU BAR FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `OnFileNew` の reset 本体や `FileOpenModeSideEffect` の分岐を消すと、
  対応する assert が必ず落ちること。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `MenuBarVerify` | EditMode・pure 決定ロジック | MENU-03/04/12 の判定を昇格元として流用 |
| `MenuBarCutoverProbe` | batchmode・root 合成 | MENU-02 の正本。`Section1` を E2ERunner へ移送 |
| `MenuBarHitlMenu` | HITL ハーネス | MENU-18 の視覚確認用に**探索 Probe として残す** |
| `VenueMenuM3Probe` `VenueLoginSecretProbe` | venue 接続 | MENU-11/14 の mock 経路＋MENU-19 の LIVE_VENUE 絞り込み（`VenueMenuFilterByLiveVenue`）。secret 詳細は `SecretModalE2ERunner` が担当 |

## 将来の `MenuBarE2ERunner.cs` 実装方針（第二波）

- `MenuBarCutoverProbe` と同型に **実 `BackcastWorkspaceRoot` を反射合成**（`ComposeRoot`: `OpenScene` →
  `SetSynthesizer(FakeMarimoSynthesizer)` → `ResolvePaths` → `BuildWorkspace`）。Python-FREE を既定とし、
  MOCK 接続を要する MENU-11/14 のみ `host.InitializePython("MOCK")` を**直呼び**（batchmode の所有権スキップを
  迂回する正当手・`ReplayToHakoniwaE2ERunner` と同型）。
- ファイルダイアログは `StubFileDialog`、メニュー操作は `OnFileNew`/`OnFileOpen`/`OnFileSave`/`OnFileSaveAs` を
  反射 invoke。`_open` トグルは `MenuBarView` の `Toggle` を駆動して private `_open` を反射確認。
- セクション構成は操作一覧表の `自動(*)` 行を 1 セクション 1 観測点で並べ、最初の失敗メッセージを返す
  `Execute()`（null=PASS）パターン。teardown は `host?.Stop()`（MOCK を起こした場合のみ）。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod MenuBarE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **ripgrep で grep**（PowerShell `Select-String` は取りこぼす）。
