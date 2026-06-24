# E2E 台本 INDEX — ユーザー行動 網羅台帳（rollup）

`Assets/Tests/E2E/Editor/*E2ERunner.md` 全台本のロールアップ。「ユーザーができる行動」が**どの台本のどの Action ID
に入っているか**を一覧で追える上位台帳。語彙・規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)。

> これは「E2E 実装済みリスト」ではなく「ユーザー行動の網羅台帳」。`HITL専用`・`対象外` も理由付きで各台本の
> 操作一覧表に必ず載っている。各台本がそのサーフェスの正本で、本 INDEX は件数の集計のみを持つ（drift を避けるため
> 行の中身は各台本を正とする）。

## Surface E2E（14 本・224 行）

| 台本 | Action ID | 行数 | 自動(E2E済) | 自動(Probe有・要昇格) | 要新規自動化 | HITL専用 | 対象外 |
|---|---|---:|---:|---:|---:|---:|---:|
| [MenuBarE2ERunner](./MenuBarE2ERunner.md) | `MENU-01..19` | 19 | 0 | 8 | 5 | 2 | 4 |
| [UniverseSidebarE2ERunner](./UniverseSidebarE2ERunner.md) ✅ | `SIDEBAR-01..19` | 19 | 17 | 0 | 0 | 2 | 0 |
| [FooterModeE2ERunner](./FooterModeE2ERunner.md) ✅ | `FOOTER-01..13` | 13 | 11 | 0 | 0 | 1 | 1 |
| ~~HakoniwaE2ERunner~~ **RETIRED #99 / ADR-0017** | — | — | — | — | — | — | — |
| [InfiniteCanvasE2ERunner](./InfiniteCanvasE2ERunner.md) ✅ | `CANVAS-01..09` | 9 | 8 | 0 | 0 | 1 | 0 |
| [FloatingWindowE2ERunner](./FloatingWindowE2ERunner.md) ✅ | `WINDOW-01..12,SNAP-01,02,DOCK-01..05,PLANE-01..04,GROUP-01,02,04,11,DRAG-01..14` | 45 | 40 | 0 | 0 | 4 | 1 |
| [StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) ✅ | `STRATEGY-01..52` | 52 | 49 | 0 | 0 | 2 | 1 |
| [StrategyEditorZoomCrispnessE2ERunner](./StrategyEditorZoomCrispnessE2ERunner.md) ✅ | `ZOOM-01..04` | 4 | 4 | 0 | 0 | 0 | 0 |
| [ScenarioStartupE2ERunner](./ScenarioStartupE2ERunner.md) ✅ | `SCENARIO-01..15` | 15 | 13 | 0 | 0 | 2 | 0 |
| ~~RunButtonE2ERunner~~ **RETIRED #95 Phase 6 / findings 0075 §3c** | — | — | — | — | — | — | — |
| [OrderTicketE2ERunner](./OrderTicketE2ERunner.md) ✅ | `ORDER-01..16` | 16 | 15 | 0 | 0 | 1 | 0 |
| [DepthLadderE2ERunner](./DepthLadderE2ERunner.md) ✅ | `DEPTH-01..11` | 11 | 9 | 1 | 0 | 1 | 0 |
| [SecretModalE2ERunner](./SecretModalE2ERunner.md) ✅ | `SECRET-01..13` | 13 | 8 | 0 | 3 | 2 | 0 |
| [ReplayRunResultTileE2ERunner](./ReplayRunResultTileE2ERunner.md) ✅ | `RRT-01..05` | 5 | 5 | 0 | 0 | 0 | 0 |

## Journey E2E（6 本・85 行）

`自動(E2E済)` 列は `自動(E2E済・<別Runner>)`（別 Runner が正本のもの）も合算する。

| 台本 | Action ID | 行数 | 自動(E2E済) | 自動(Probe有・要昇格) | 要新規自動化 | HITL専用 | 対象外 |
|---|---|---:|---:|---:|---:|---:|---:|
| ~~ReplayToHakoniwaE2ERunner~~ **RETIRED #99 / ADR-0017**（縦串の席は ↓ が継承） | — | — | — | — | — | — | — |
| [NotebookToHakoniwaJourneyE2ERunner](./NotebookToHakoniwaJourneyE2ERunner.md) ✅ | `JOURNEY-NBHAKO-01..14` | 14 | 12 | 0 | 0 | 1 | 1 |
| [LayoutPersistenceJourneyE2ERunner](./LayoutPersistenceJourneyE2ERunner.md) ✅ | `JOURNEY-LAYOUT-01..15` | 15 | 15 | 0 | 0 | 0 | 0 |
| [AuthorToRunJourneyE2ERunner](./AuthorToRunJourneyE2ERunner.md) ✅ | `JOURNEY-AUTHOR-01..13` | 13 | 13 | 0 | 0 | 0 | 0 |
| [LiveManualTradeJourneyE2ERunner](./LiveManualTradeJourneyE2ERunner.md) ✅ | `JOURNEY-LIVE-01..15` | 15 | 14 | 0 | 0 | 1 | 0 |
| [ChartPlacementJourneyE2ERunner](./ChartPlacementJourneyE2ERunner.md) | `CP-S0-01..04, CP-S1-01..05, CP-S2-01, CP-S2-02a/b, CP-S3-01..04, CP-S4-01..04` | 19 | 11 | 0 | 8 | 0 | 0 |
| [AddChartLadderJourneyE2ERunner](./AddChartLadderJourneyE2ERunner.md) ✅ | `ADDLADDER-01..09` | 9 | 8 | 0 | 0 | 1 | 0 |
| [ChartOrphanSyncJourneyE2ERunner](./ChartOrphanSyncJourneyE2ERunner.md) ✅ | `CHART-ORPHAN-01..04` | 4 | 4 | 0 | 0 | 0 | 0 |

※ `ReplayToHakoniwa` は Action-ID 採番より前に書かれた台本で 7 = ストーリーの step 数（Action-ID 行ではない）。
第二波で他台本と同じ操作一覧表へ整形する際に Action ID を採番する。

## 合計

- **Surface 13 本 ＝ 222 行 ／ Journey 4 本 ＝ 57 行 ／ 総計 279 行**（#116: `StrategyEditorNotebookE2ERunner` に **STRATEGY-51/52**（Section24）を追加＝live cell run lifecycle edges。51=start-in-flight deferred-stop（in-flight 窓の ■ を捨てず confirmed で適用）、52=起動元 cell 削除/File→New で `_liveRunRegion`/`_liveRunCell` を cell-identity 基準で reconcile（venue run は止めない）。Python-FREE control-logic・AFK RED(51, 52b)→GREEN・findings 0092 §5.3）。（#108–111（ADR-0024 puzzle-feel drag）: `FloatingWindowE2ERunner` の旧 GROUP-03/05/06/07/08/09/10/12/13/14 を退役/書換し **DRAG-01..14** へ反転＝cursor 位置 3 mode dispatcher（Swap/Translate/Detach）・in-drag 磁石吸着（R_SNAP=96）・spring 200ms/overshoot 8%・release-position commit（overlap→最寄り flush+merge）・ESC キャンセル・Hakoniwa special 退役・merge cascade simplify（S22,S24–S40）。GROUP-01/02/04/11 は維持・findings 0088 §14。#105: `FloatingWindowE2ERunner` に GROUP-14（S32）を追加＝初回起動の base dock cluster を 1 つの Hakoniwa group に束ねる工場出荷値（`FormGroup` / `FormFactoryBaseGroup`・no-resume 分岐のみ・saved layout は RestoreFloating の groupId を尊重）。ADR-0019 D8 amendment / findings 0082 §12 / findings 0083）。（#104: `FloatingWindowE2ERunner` に GROUP-01..13 を追加＝Hakoniwa window group / drag semantics の永続 groupId・flush attach・merge cascade・通常 group 一体 translate・detach + dissolve helper・Hakoniwa translate ban + core lock・swap (x,y,w,h)・cross-plane restore split・drag ghost preview の 8 vertical slice。S20–S31 の 12 新規 section + HITL GROUP-13。ADR-0019 / findings 0082）。（#103: `FloatingWindowE2ERunner` に PLANE-01..04（2 深さプレーン分離の parallax 速度差 / またぎ吸着禁止・プレーン内吸着 / 2 controller persist round-trip、＋HITL 目視 PLANE-04）を追加・ADR-0018・findings 0075 §10。#100: `ReplayRunResultTileE2ERunner` RRT-01..05 を新規追加＝#99 が `HakoniwaE2ERunner` 削除で落とした RunResult タイル（running↔full-stats）C# AFK カバレッジを Panel surface として復活し、#100 ① の再実行 render 不変条件 RRT-04 を追加・findings 0077。#101: `FloatingWindowE2ERunner` に DOCK-03/04（DockSnapPlacement flush 吸着 / focus-adjacent 固定サイズ chart spawn）を追加・findings 0078。#95 Phase 2: `StrategyEditorNotebookE2ERunner` に STRATEGY-19/20 per-cell RUN を追加・findings 0071。#95 Phase 4: STRATEGY-21/22/23 bt.replay() control を追加・findings 0073。#95 Phase 5: STRATEGY-24/25/26 bt.step() persistence + NoScenarioBacktester を追加・findings 0074。#95 Phase 6: STRATEGY-27..33（per-cell stale badge / block popup / document badge #90 / rich output routing・S16–S19）を追加・findings 0075。**#95 E2E 仕上げ**: `NotebookToHakoniwaJourneyE2ERunner`（JOURNEY-NBHAKO-01..14）を新設＝退役 `ReplayToHakoniwa` の縦串の席を新 Hakoniwa モデルで継ぎ「per-cell RUN→実エンジン駆動→base tile 逐次更新→停止」を 1 本の release-gate に。`StrategyEditorNotebookE2ERunner` の最後の `要新規自動化` STRATEGY-11 を Section20 で昇格・findings 0076。#102: STRATEGY-34..38（per-cell console stdout/stderr セグメント表示 + 動的出力レイアウト・S21）を追加・findings 0079。#102 audit gaps: STRATEGY-39..46（`&` escape regression / multi-cell routing / re-press replace+empty / overflow ScrollRect / bodyH==0 first-frame / `</color>` 注入耐性 / dormant-reuse race・S22）を追加・findings 0079 §6）。（behavior-to-e2e 2026-06-24: `AddChartLadderJourneyE2ERunner`（ADDLADDER-01..09）を新設＝owner ストーリー「`.env` kabu ログイン→7203 +Add→Ladder 付チャート表示」の **UI 半分**（Live 突入後に +Add した chart が spawn 時点で Ladder 可視＋inset＝`BuildChartContent` の `_lastLadderLive` spawn 時読み取り）を Python-FREE で gate。DATA 半分=SUBWIRE-02/03・ログイン半分=KABU-LIVE-01 を台本で参照。AFK RED(ADDLADDER-04)→GREEN・rollup 5 PASS・findings 0094）。
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
| [FileOpenNonMarimoE2ERunner](./FileOpenNonMarimoE2ERunner.md) ✅ | `OPEN-NM-01..04` | 4 | 4 | #113（marimo-or-error・#86 wrap 反転）/ [findings 0098](../../../../docs/findings/0098-issue113-open-layer-marimo-only.md)（旧: [0054](../../../../docs/findings/0054-file-open-non-marimo-py-wraps-as-1-cell.md)） |
| [QuitConfirmE2ERunner](./QuitConfirmE2ERunner.md) ✅ | `QUIT-01..10` | 10 | 8 | #89 / [findings 0068](../../../../docs/findings/0068-e2e-quit-confirm-runner.md) |
| [LiveSubscribeWiringE2ERunner](./LiveSubscribeWiringE2ERunner.md) ✅ | `SUBWIRE-01..07` | 7 | 5 | #107 / [findings 0086](../../../../docs/findings/0086-issue107-livemanual-market-data-subscription-wiring.md) / ADR-0022 |
| [TachibanaLiveE2ERunner](./TachibanaLiveE2ERunner.md) | `SUBWIRE-06`（立花 leg） | — | — | #92（v4r9 公開鍵認証カットオーバー・[findings 0087](../../../../docs/findings/0087-tachibana-v4r9-pubkey-migration.md)/ADR-0023）/ #107（self-subscribe 撤去→本番トリガ）/ #85 / findings 0053 |
| [KabuLiveE2ERunner](./KabuLiveE2ERunner.md) | `KABU-LIVE-01..03` | 3 | 1 | #107 / findings 0086 / ADR-0022 |
| [V19ReplayLiveE2ERunner](./V19ReplayLiveE2ERunner.md) ✅ | `V19REPLAY-01..03` | 3 | 3 | v19 marimo cell 実 mount Replay / [findings 0089](../../../../docs/findings/0089-v19-marimo-cell-file-anchor.md) / ADR-0011・0016 |
| [V19ReplayPressE2ERunner](./V19ReplayPressE2ERunner.md) ✅ | `V19PRESS-01..06` | 6 | 6 | v19 Replay 実ボタン press 単一縦串（2 ゲートの継ぎ目） / [findings 0090](../../../../docs/findings/0090-v19-press-driven-replay-single-vertical.md) / ADR-0016 |
| [ChartUniverseSyncE2ERunner](./ChartUniverseSyncE2ERunner.md) ✅ | `CHARTSYNC-01..04` (+05 HITL) | 5 | 4 | #123（layout 復元が universe-sync をバイパスし孤児 `chart:<iid>` が残る・[findings 0095](../../../../docs/findings/0095-issue123-chart-window-universe-sync-on-restore.md)） |
| [ChartUniverseWriteConsistencyE2ERunner](./ChartUniverseWriteConsistencyE2ERunner.md) ✅ | `CHARTWRITE-01..08` (+09 HITL) | 9 | 8 | #124（矛盾 doc を write-side で作らない＝#123 の defense-in-depth・[findings 0099](../../../../docs/findings/0099-issue124-chart-universe-write-side-consistency.md)） |

- `QuitConfirmE2ERunner`: 終了確認ダイアログ（autosave-on-quit → 保存/破棄/キャンセル）の純ロジック `SaveGuardController`
  を真理値駆動。QUIT-01..07＋QUIT-10（save-failure→abort の decision）を `自動(E2E済)`。QUIT-08（batchmode 抑制・latch）は
  実 `BackcastWorkspaceRoot` 反射 harness を要し `要新規自動化`、QUIT-09（実 OS ウィンドウ close＋実 native picker）は `HITL専用`。

- `LiveSubscribeWiringE2ERunner`（#107）: LiveManual の本番購読配線を full-stack（MOCK adapter・venue-free・CI 常時）で gate。
  実 `SelectRow`/`AddFromPicker`/LiveManual 突入を駆動 → 実 subscribe RPC → mock adapter → depth poll → `DepthDecoder.HasDepth`。
  SUBWIRE-01..04 を `自動(E2E済)`、SUBWIRE-05（人工 cap 撤去・venue typed 上限）は pytest `test_subscribe_market_data_batch.py`、
  SUBWIRE-06/07（立花/kabu 実 venue）は `HITL専用`。litmus: 配線（bulk / LiveSubscribeHook 代入）を消すと RED。
- `TachibanaLiveE2ERunner` / `KabuLiveE2ERunner`（#107 実 venue leg）: 手動 self-subscribe を撤去し本番トリガ（coordinator）
  経由に置換。実 demo 資格情報・本体・場中が要るため `HITL専用`（owner 手元）。kabu は Windows 限定（kabuステーション本体）。
- `V19ReplayLiveE2ERunner`（v19 marimo cell 実 mount Replay）: `InitializePython("MOCK")` で実 Python を立て、owner の実 J-Quants
  DuckDB（`BACKCAST_JQUANTS_DUCKDB_ROOT`）で実 v19 cell を本番 4-arg `InvokeRunCell` marshaling 経由で駆動。V19REPLAY-01..03＝
  per-cell RUN 成立 / cell が `__file__` 相対で artifacts 自己ロード（FileNotFound 無し）/ 実約定（`fills_count>0`）を `自動(E2E済)`。
  litmus: `strategy_path`→`__file__` 配線（findings 0089）を外すと `__file__=None`→`TypeError`→約定ゼロで RED。mount 不在は SKIP。
  ⚠️ NAS を読むため Unity はサンドボックス無効起動。`InitializePython("MOCK")` の shutdown segfault で exit=139＝verdict はタグで判定。
- `V19ReplayPressE2ERunner`（v19 Replay 実ボタン press 単一縦串）: owner が v19 を *開いて ▶ を押す* のと同じ経路を実 NAS で
  end-to-end 駆動。`V19ReplayLiveE2ERunner`（press 配線をバイパス＝`InvokeRunCell` 直叩き）と `NotebookToHakoniwaJourneyE2ERunner`
  （fake executor・同期レーン）の **どちらも跨がない継ぎ目**——実 decompose→synthesize 往復・実 worker thread 上の実 Python・
  実ファイルでの `__file__` 解決——を 1 本で踏む。production の synthesizer/executor/worker lane を fake へ差し替えず、
  `_coordinator.Open(v19)`→`ReseedFromEditor`→実ボタン `.onClick`→worker lane→`fills_count>0` を `自動(E2E済)`（V19PRESS-01..06）。
  litmus は findings 0090（0089 の `__file__` inject 撤去 / 往復破壊 / worker thread 契約破壊で RED）。mount 不在は SKIP・NAS 読取で
  サンドボックス無効起動・exit=139 はタグ判定。

- `ChartUniverseSyncE2ERunner`（#123）: 「chart 窓 == universe (SoT)」が *layout 復元を跨いで* 成立することを gate。
  `RestoreFloating` が layout sidecar の `chart:<iid>` を universe チェック無しで spawn する第二経路を、`ReseedFromEditor`
  末尾の `Changed` 非依存 `SyncChartWindowsToUniverse()` で塞ぐ。CHARTSYNC-01（空→空 `SequenceEqual`・File→Open）/
  03（`Editable=false` `instruments_ref` ロック・File→Open）で孤児 despawn、02（boot resume・universe=[X]）/ 04（subset・
  File→Open）で残存 chart の復元ジオメトリ保持を `自動(E2E済)`。CHARTSYNC-05（実ブート目視）は HITL専用。litmus: 末尾 sync を
  消すと 01/03 RED・seed 前へ移すと 02/04 RED（findings 0095）。
- `ChartUniverseWriteConsistencyE2ERunner`（#124）: #123 の **書き込み側補完**（defense-in-depth・#123 は砦として残す）。on-disk
  不変条件「保存 `.json` の `layout.floatingWindows` の `chart:<iid>` ⇒ 再オープン時に解決される universe（`sidecar??inline`）が
  `<iid>` を含む（ただし unreadable は fail-open）」を gate。`TryWriteLayout` が write 直前に `PruneOrphanChartWindowsForPersistence`
  で「解決済み universe に居ない chart」を captured layout から落とす（scenario は読むだけ＝mutate-existing-only/D5 不変）。
  CHARTWRITE-01（OnFileSave path①）/ 03（subset）/ 04（終了 autosave・reseed 不在経路）/ 08（present-but-empty sidecar の confident-empty 枝）で
  孤児 prune、02（inline universe）/ 07（group）で universe 在の chart の復元ジオメトリ保持、05（sidecar unreadable）/ 06（inline unparseable）で
  fail-open を `自動(E2E済)`。CHARTWRITE-09（実ブート目視）は HITL専用。litmus: prune 呼出削除で 01/03/04/07/08 RED・oracle を
  sidecar キー単体へ狭めると 02 RED・fail-closed 化で 05/06 RED（findings 0099）。
- 第二波の昇格優先度の目安: `自動(Probe有・要昇格)`（既存 assert 流用で安い）→ `要新規自動化`（新規）。
  `OrderTicket` は view 側フォーム＋検証ゲートの Probe が無く全 15 行が新規 ——11本目で昇格済み（findings 0064）。

## 第一波で残った確認待ち（`要確認` の所在）

各台本に「要確認」として明記済み（コードで挙動が裏取りできなかった点）。第二波の runner 実装前に解消する:

- `UniverseSidebarE2ERunner`（2）— 実 `IAvailableInstrumentsProvider` の DuckDB/venue 配線、実 `set_execution_mode` 実体。
  ※ findings 0084 で一部解消: picker が root の mode+scenario.end を引く配線は `SIDEBAR-15`（`Section11`）で AFK 化、
  DuckDB supply の Python 半分は `python/tests/test_replay_instrument_picker_supply.py` で gate。実 venue/Unity-against-real-DuckDB は据え置き。
- `FooterModeE2ERunner`（1）— 実 `set_execution_mode`/`stop_live_strategy` RPC 実体。
- `InfiniteCanvasE2ERunner`（1）・`FloatingWindowE2ERunner`（2）— input-surface 層の per-event clamp / cascade の Probe 未カバー seam。
- `LayoutPersistenceJourneyE2ERunner`（4）・`AuthorToRunJourneyE2ERunner`（1）— AFK で footer mode を反射切替できるか、`ScenarioStartupValidation` の具体規則 等。
- `LiveManualTradeJourneyE2ERunner`（解決済み・14本目）— mock fill は portfolio を **live state JSON に載せない**（`get_portfolio_json` は `engine.last_portfolio` gated で live は空）。Positions は `force_account_snapshot`→AccountEvent→`FormatPositions` で観測する（findings 0067）。
