# E2E 台本 INDEX — ユーザー行動 網羅台帳（rollup）

`Assets/Tests/E2E/Editor/*E2ERunner.md` 全台本のロールアップ。「ユーザーができる行動」が**どの台本のどの Action ID
に入っているか**を一覧で追える上位台帳。語彙・規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)。

> これは「E2E 実装済みリスト」ではなく「ユーザー行動の網羅台帳」。`HITL専用`・`対象外` も理由付きで各台本の
> 操作一覧表に必ず載っている。各台本がそのサーフェスの正本で、本 INDEX は件数の集計のみを持つ（drift を避けるため
> 行の中身は各台本を正とする）。

## Surface E2E（13 本・176 行）

| 台本 | Action ID | 行数 | 自動(E2E済) | 自動(Probe有・要昇格) | 要新規自動化 | HITL専用 | 対象外 |
|---|---|---:|---:|---:|---:|---:|---:|
| [MenuBarE2ERunner](./MenuBarE2ERunner.md) | `MENU-01..18` | 18 | 0 | 7 | 5 | 2 | 4 |
| [UniverseSidebarE2ERunner](./UniverseSidebarE2ERunner.md) ✅ | `SIDEBAR-01..14` | 14 | 12 | 0 | 0 | 2 | 0 |
| [FooterModeE2ERunner](./FooterModeE2ERunner.md) ✅ | `FOOTER-01..13` | 13 | 11 | 0 | 0 | 1 | 1 |
| ~~HakoniwaE2ERunner~~ **RETIRED #99 / ADR-0017** | — | — | — | — | — | — | — |
| [InfiniteCanvasE2ERunner](./InfiniteCanvasE2ERunner.md) ✅ | `CANVAS-01..09` | 9 | 8 | 0 | 0 | 1 | 0 |
| [FloatingWindowE2ERunner](./FloatingWindowE2ERunner.md) ✅ | `WINDOW-01..10,SNAP-01,02,DOCK-01,02` | 14 | 12 | 0 | 0 | 1 | 1 |
| [StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) ✅ | `STRATEGY-01..33` | 33 | 29 | 0 | 1 | 2 | 1 |
| [ScenarioStartupE2ERunner](./ScenarioStartupE2ERunner.md) ✅ | `SCENARIO-01..15` | 15 | 13 | 0 | 0 | 2 | 0 |
| ~~RunButtonE2ERunner~~ **RETIRED #95 Phase 6 / findings 0075 §3c** | — | — | — | — | — | — | — |
| [OrderTicketE2ERunner](./OrderTicketE2ERunner.md) ✅ | `ORDER-01..16` | 16 | 15 | 0 | 0 | 1 | 0 |
| [DepthLadderE2ERunner](./DepthLadderE2ERunner.md) ✅ | `DEPTH-01..11` | 11 | 9 | 1 | 0 | 1 | 0 |
| [SecretModalE2ERunner](./SecretModalE2ERunner.md) ✅ | `SECRET-01..13` | 13 | 8 | 0 | 3 | 2 | 0 |
| [ReplayRunResultTileE2ERunner](./ReplayRunResultTileE2ERunner.md) ✅ | `RRT-01..05` | 5 | 5 | 0 | 0 | 0 | 0 |

## Journey E2E（3 本＋既存 1 本・50 行）

`自動(E2E済)` 列は `自動(E2E済・<別Runner>)`（別 Runner が正本のもの）も合算する。

| 台本 | Action ID | 行数 | 自動(E2E済) | 自動(Probe有・要昇格) | 要新規自動化 | HITL専用 | 対象外 |
|---|---|---:|---:|---:|---:|---:|---:|
| ~~ReplayToHakoniwaE2ERunner~~ **RETIRED #99 / ADR-0017** | — | — | — | — | — | — | — |
| [LayoutPersistenceJourneyE2ERunner](./LayoutPersistenceJourneyE2ERunner.md) ✅ | `JOURNEY-LAYOUT-01..15` | 15 | 15 | 0 | 0 | 0 | 0 |
| [AuthorToRunJourneyE2ERunner](./AuthorToRunJourneyE2ERunner.md) ✅ | `JOURNEY-AUTHOR-01..13` | 13 | 13 | 0 | 0 | 0 | 0 |
| [LiveManualTradeJourneyE2ERunner](./LiveManualTradeJourneyE2ERunner.md) ✅ | `JOURNEY-LIVE-01..15` | 15 | 14 | 0 | 0 | 1 | 0 |

※ `ReplayToHakoniwa` は Action-ID 採番より前に書かれた台本で 7 = ストーリーの step 数（Action-ID 行ではない）。
第二波で他台本と同じ操作一覧表へ整形する際に Action ID を採番する。

## 合計

- **Surface 13 本 ＝ 182 行 ／ Journey 4 本 ＝ 50 行 ／ 総計 232 行**（#100: `ReplayRunResultTileE2ERunner` RRT-01..05 を新規追加＝#99 が `HakoniwaE2ERunner` 削除で落とした RunResult タイル（running↔full-stats）C# AFK カバレッジを Panel surface として復活し、#100 ① の再実行 render 不変条件 RRT-04 を追加・findings 0077。#95 Phase 2: `StrategyEditorNotebookE2ERunner` に STRATEGY-19/20 per-cell RUN を追加・findings 0071。#95 Phase 4: STRATEGY-21/22/23 bt.replay() control を追加・findings 0073。#95 Phase 5: STRATEGY-24/25/26 bt.step() persistence + NoScenarioBacktester を追加・findings 0074。#95 Phase 6: STRATEGY-27..33（per-cell stale badge / block popup / document badge #90 / rich output routing・S16–S19）を追加・findings 0075）。
- `ReplayToHakoniwaE2ERunner` が第一波時点の実装済み回帰ゲート（`自動(E2E済)`）。**第二波1本目 = `ScenarioStartupE2ERunner`
  を昇格済み**（throwaway `ScenarioStartupProbe` → 改名＋SCENARIO-12 追加・AFK RED→GREEN、findings 0054）。
  **2本目 = `FooterModeE2ERunner`**（`FooterLiveAutoVerify` → 改名・FOOTER-06/07 view section＋FOOTER-10 追加・LiveAuto は
  supporting pin・AFK RED→GREEN、findings 0055）。
  **3〜5本目 = `InfiniteCanvasE2ERunner`（findings 0056）/ `FloatingWindowE2ERunner`（0057）/ `UniverseSidebarE2ERunner`（0058）**
  も昇格済み（throwaway probe → 改名・新規 section [CANVAS-05 / WINDOW-04,05,08 / SIDEBAR-11,14] 追加・AFK GREEN 確定 2026-06-19）。
  rollup 件数を 3 行とも `E2E済` へ整合（FloatingWindow の WINDOW 1 行残を解消）。
  **6本目 = `DepthLadderE2ERunner`**（`WorkspaceDepthLadderProbe` → git mv・新規 section [DEPTH-03 固定21行/"---" / DEPTH-06 受信順 /
  DEPTH-09 Replay decode-skip / DEPTH-10 signature early-out] 追加・AFK RED(S6)→GREEN 確定 2026-06-19・findings 0059）。
  DEPTH-04（per-side 色）は `ThemeProbe` が正本のまま据え置き（findings 0054 で `hakoniwa_up/down/last` へ移行済）、DEPTH-11 は HITL。
  **7本目 = `HakoniwaE2ERunner`**（4 probe [`HakoniwaProbe` / `HakoniwaChartTileProbe` / `HakoniwaBaseModeProbe` / `HakoniwaProfileProbe`] → 1 runner 16 section へ集約・assert verbatim 移送・AFK GREEN 確定 2026-06-19・findings 0060）。HAKONIWA-01..10 を `自動(E2E済)` へ。ChartTile S2（ohlc decode）/ BaseMode S5（#65 panel empty-state）は Hakoniwa 外の関心事として元 probe に trim 据え置き（将来 Chart/Panel runner へ）、HakoniwaProbe/HakoniwaProfileProbe は git rm。
  **8本目 = `StrategyEditorNotebookE2ERunner`**（`StrategyEditorProbe` → git mv・12 section verbatim 移送・各 section に
  Covers 付与・findings 0061）。STRATEGY-01..04,06..10,12,15,16,17 を `自動(E2E済)` へ、STRATEGY-13（CapturePositions）は
  S12 が既存 assert のため `自動(E2E済)` へ再分類。STRATEGY-11（placeholder hint）は実 view harness を要するため
  `要新規自動化` のまま据え置き。STRATEGY-15 の MenuBar 側は `MenuBarCutoverProbe`（MENU-02）が正本のまま。
  **9本目 = `SecretModalE2ERunner`**（`SecretModalM2Probe` → git mv・5 section verbatim 移送・各 section に Covers 付与・
  findings 0062）。SECRET-01/02/04/05/06/11＋03/10（controller leg）を `自動(E2E済)` へ。SECRET-07/08/09（root 連携=
  focus drop / open gate / open-time id バインド）は実 BackcastWorkspaceRoot 反射 harness を要するため `要新規自動化` の
  まま据え置き、SECRET-12/13 は HITL専用。SECRET-03 の lane roundtrip / SECRET-10 の wire no-leak は `VenueLoginSecretProbe`
  （pythonnet・据え置き）が正本のまま。INDEX 旧値 `7 | 4` は台本正本（8 要昇格 / 3 要新規）との drift だったため昇格に合わせ整合。
  **10本目 = `RunButtonE2ERunner`**（throwaway `WorkspaceUiCutoverProbe` の S1=readiness 真理値表・S2=single Run entry を
  verbatim 移送＝SectionA/C、新規 SectionB=block-reason ラベル view（RUN-03）・SectionD=OnRun→host 配線（RUN-01/05/06）。
  RUN-01/05/06 は sealed `WorkspaceEngineHost` のため spy 不可 → 実 root＋`host.InitializePython("MOCK")` で server-ready にし
  host private `_req` を観測（非 vacuous: blocked が `_req` 既定・ready が `_req` 充填）。findings 0063）。RUN-01..08 を
  `自動(E2E済)` へ、RUN-09/10 は `要新規自動化` 据え置き・RUN-11 HITL・RUN-12 対象外。`WorkspaceUiCutoverProbe` は
  S3（boot→File→New）のみ残置。**【RETIRED #95 Phase 6 / global ▶ Run sunset・findings 0075 §3c】** runner は削除（`StrategyEditorRunButton`/`_editorRunButton`/`OnRun` 直参照が撤去後 compile 不能）。RUN-01..08 の readiness/trigger 契約は per-cell RUN（STRATEGY-19/20・S13/S14/S16）＋ `test_notebook_replay_afk` へ移送、cutover 負 invariant **U4→FOOTER-13（`FooterModeE2ERunner`）／ U5→SCENARIO-15（`ScenarioStartupE2ERunner`）** へ re-home（commit d19dec6＝section 移送、Slice 9＝ID 衝突修正：当初 FOOTER-11/SCENARIO-13 を焼いたが台本で実 venue Live HITL／theme HITL に既割当だったため空き番号へ振り直し）。
  **11本目 = `OrderTicketE2ERunner`**（全行新規オーサリング＝既存 view 側 probe ゼロ。RunButton SectionD と同型に実
  root を反射合成し `OnManualPlace`/`OnManualCancel`/`DriveOrderTicket` を反射 invoke。production の
  `OrderTicketValidation` 抽出は不要＝検証ゲートが `SetStatus` を同期で出すので `_status` 反射で観測（parity-first・最小
  diff）。SectionA=フォームトグル[ORDER-01..04]・SectionB=検証拒否ゲート[ORDER-06/07/08/11a]・SectionC=表示/状態
  [ORDER-12/13off/14/15]・SectionD=接続済み MOCK lane[ORDER-05/09/10/11b/13on]。gate 順 connect→instrument より
  ORDER-09 は接続済み host で非 vacuous 検査・ORDER-05 happy place が同一 host で lane 到達を実証。findings 0064）。
  ORDER-01..15 を `自動(E2E済)` へ、ORDER-16 は HITL専用。
  **12本目 = `LayoutPersistenceJourneyE2ERunner`**（Journey・全行新規オーサリング。実 `BackcastWorkspaceRoot` を反射合成し配置 5 次元〔canvas pan/zoom・箱庭 tile 順・floating window rect・notebook cell 位置・per-mode profile〕を `OnFileSave`→File→New→`OnFileOpen` で round-trip。Section1=JOURNEY-LAYOUT-01..13 の round-trip 本体・Section2=14 no-wipe bare open・Section3=15 Save As fork。per-mode profile は footer UI でなく `SyncBaseTilesToMode(bool)` 反射で駆動〔BackcastWorkspaceProbe S12 parity〕。⚠️ 生産の `OnFileNew` は canvas/Hakoniwa/floating geometry を reset しないので、round-trip 非空虚化は File→New 後の明示 perturb で担保。純データ probe〔`ReplayLayoutProbe`/`MultiDocLayoutProbe`〕は移送せず、それらが HITL と切り出した実 root 配線を縫う層として共存。findings 0065）。JOURNEY-LAYOUT-01..15 を `自動(E2E済)` へ。
  **13本目 = `AuthorToRunJourneyE2ERunner`**（Journey・全行新規オーサリング。実 `BackcastWorkspaceRoot` を反射合成し author→run の縦縫い〔空 notebook→セル編集→universe/scenario→Save As→provider 5 条件 supplyable→run gate commit→`host.TryStartRun` 受理〕を観測。Section1=JOURNEY-AUTHOR-01..10 の happy path〔steps 2-9 Python-FREE・step 10 のみ `host.InitializePython("MOCK")`＋`_isOwner=true` で受理観測〕・Section2=11/12 の reject〔空 universe→`BlockedInvalidScenario`＋sidecar 不変／dirty editor→`BlockedNoStrategy`〕。非空虚化: provider 弧 false〔03 未バインド〕→true〔08 Save As〕→false〔12 dirty〕、scenario commit は Save As も書くため step 9 は Save As 後の universe 成長分を TryStartRun の Commit に帰属。各 surface 単体は既存 runner が正本＝移送せず横断縫い目を観測。findings 0066）。JOURNEY-AUTHOR-01..12 を `自動(E2E済)` へ、13 は `自動(E2E済・ReplayToHakoniwa)`。
  **14本目 = `LiveManualTradeJourneyE2ERunner`**（Journey・全行新規オーサリング。手動実取引フローの横断縫い目を二基層で観測: SectionA=接続済み MOCK root〔OrderTicket SectionD 同型〕で Order ticket 表示/操作可〔04〕・ManualInstrument 解決/refuse〔05〕、SectionB-E=secret-mock lanes〔VenueLoginSecretProbe 同型・`build_secret_mock_server` で `SecretMockAdapter` 注入〕で接続/モードゲート/発注/第二暗証/mock fill/Positions/取消/logout/直列化/logout-gate〔02/03/06-13/15〕。production root の `host.InitializePython` は `MockVenueAdapter`〔SecretRequired を出さない〕を built するため secret 縫い目は lanes 直駆動で別基層に分離。step10 は `get_portfolio_json`〔live は `engine.last_portfolio` gated で空〕でなく `force_account_snapshot`→AccountEvent→production `FormatPositions` 反射で観測〔arm 前 flat→後 建玉で非空虚〕。`VenueLoginSecretProbe` は #2 据え置きの上流＝移送せず secret/lane recipe の手本として参照。JOURNEY-LIVE-14〔実 venue 実約定〕は HITL のまま。findings 0067）。JOURNEY-LIVE-01..13,15 を `自動(E2E済)` へ、14 は HITL専用。
  残り未昇格: MenuBar のみ。順次昇格。

## Issue release-gate slice runners

Surface/Journey の網羅台帳とは別に、特定 issue の release-gate を細く正本化する slice runner。Surface 台本（特に
[StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) の STRATEGY-16 が generic Open 経路を担う）と
重複させず、その issue の wrap policy / decision の 1 不変条件をピンポイントで gate する。

| 台本 | Action ID | 行数 | 自動(E2E済) | 関連 issue / findings |
|---|---|---:|---:|---|
| [FileOpenNonMarimoE2ERunner](./FileOpenNonMarimoE2ERunner.md) ✅ | `OPEN-NM-01..04` | 4 | 4 | #86 / [findings 0054](../../../../docs/findings/0054-file-open-non-marimo-py-wraps-as-1-cell.md) |
| [QuitConfirmE2ERunner](./QuitConfirmE2ERunner.md) ✅ | `QUIT-01..10` | 10 | 8 | #89 / [findings 0068](../../../../docs/findings/0068-e2e-quit-confirm-runner.md) |

- `QuitConfirmE2ERunner`: 終了確認ダイアログ（autosave-on-quit → 保存/破棄/キャンセル）の純ロジック `SaveGuardController`
  を真理値駆動。QUIT-01..07＋QUIT-10（save-failure→abort の decision）を `自動(E2E済)`。QUIT-08（batchmode 抑制・latch）は
  実 `BackcastWorkspaceRoot` 反射 harness を要し `要新規自動化`、QUIT-09（実 OS ウィンドウ close＋実 native picker）は `HITL専用`。

- 第二波の昇格優先度の目安: `自動(Probe有・要昇格)`（既存 assert 流用で安い）→ `要新規自動化`（新規）。
  `OrderTicket` は view 側フォーム＋検証ゲートの Probe が無く全 15 行が新規 ——11本目で昇格済み（findings 0064）。

## 第一波で残った確認待ち（`要確認` の所在）

各台本に「要確認」として明記済み（コードで挙動が裏取りできなかった点）。第二波の runner 実装前に解消する:

- `UniverseSidebarE2ERunner`（2）— 実 `IAvailableInstrumentsProvider` の DuckDB/venue 配線、実 `set_execution_mode` 実体。
- `FooterModeE2ERunner`（1）— 実 `set_execution_mode`/`stop_live_strategy` RPC 実体。
- `InfiniteCanvasE2ERunner`（1）・`FloatingWindowE2ERunner`（2）— input-surface 層の per-event clamp / cascade の Probe 未カバー seam。
- `LayoutPersistenceJourneyE2ERunner`（4）・`AuthorToRunJourneyE2ERunner`（1）— AFK で footer mode を反射切替できるか、`ScenarioStartupValidation` の具体規則 等。
- `LiveManualTradeJourneyE2ERunner`（解決済み・14本目）— mock fill は portfolio を **live state JSON に載せない**（`get_portfolio_json` は `engine.last_portfolio` gated で live は空）。Positions は `force_account_snapshot`→AccountEvent→`FormatPositions` で観測する（findings 0067）。
