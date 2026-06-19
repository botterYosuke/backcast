# E2E 台本 INDEX — ユーザー行動 網羅台帳（rollup）

`Assets/Tests/E2E/Editor/*E2ERunner.md` 全台本のロールアップ。「ユーザーができる行動」が**どの台本のどの Action ID
に入っているか**を一覧で追える上位台帳。語彙・規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)。

> これは「E2E 実装済みリスト」ではなく「ユーザー行動の網羅台帳」。`HITL専用`・`対象外` も理由付きで各台本の
> 操作一覧表に必ず載っている。各台本がそのサーフェスの正本で、本 INDEX は件数の集計のみを持つ（drift を避けるため
> 行の中身は各台本を正とする）。

## Surface E2E（12 本・162 行）

| 台本 | Action ID | 行数 | 自動(E2E済) | 自動(Probe有・要昇格) | 要新規自動化 | HITL専用 | 対象外 |
|---|---|---:|---:|---:|---:|---:|---:|
| [MenuBarE2ERunner](./MenuBarE2ERunner.md) | `MENU-01..18` | 18 | 0 | 7 | 5 | 2 | 4 |
| [UniverseSidebarE2ERunner](./UniverseSidebarE2ERunner.md) ✅ | `SIDEBAR-01..14` | 14 | 12 | 0 | 0 | 2 | 0 |
| [FooterModeE2ERunner](./FooterModeE2ERunner.md) ✅ | `FOOTER-01..12` | 12 | 10 | 0 | 0 | 1 | 1 |
| [HakoniwaE2ERunner](./HakoniwaE2ERunner.md) ✅ | `HAKONIWA-01..13` | 13 | 10 | 0 | 0 | 1 | 2 |
| [InfiniteCanvasE2ERunner](./InfiniteCanvasE2ERunner.md) ✅ | `CANVAS-01..09` | 9 | 8 | 0 | 0 | 1 | 0 |
| [FloatingWindowE2ERunner](./FloatingWindowE2ERunner.md) ✅ | `WINDOW-01..12` | 12 | 10 | 0 | 0 | 1 | 1 |
| [StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) ✅ | `STRATEGY-01..18` | 18 | 14 | 0 | 1 | 2 | 1 |
| [ScenarioStartupE2ERunner](./ScenarioStartupE2ERunner.md) ✅ | `SCENARIO-01..14` | 14 | 12 | 0 | 0 | 2 | 0 |
| [RunButtonE2ERunner](./RunButtonE2ERunner.md) ✅ | `RUN-01..12` | 12 | 8 | 0 | 2 | 1 | 1 |
| [OrderTicketE2ERunner](./OrderTicketE2ERunner.md) | `ORDER-01..16` | 16 | 0 | 0 | 15 | 1 | 0 |
| [DepthLadderE2ERunner](./DepthLadderE2ERunner.md) ✅ | `DEPTH-01..11` | 11 | 9 | 1 | 0 | 1 | 0 |
| [SecretModalE2ERunner](./SecretModalE2ERunner.md) ✅ | `SECRET-01..13` | 13 | 8 | 0 | 3 | 2 | 0 |

## Journey E2E（3 本＋既存 1 本・50 行）

`自動(E2E済)` 列は `自動(E2E済・<別Runner>)`（別 Runner が正本のもの）も合算する。

| 台本 | Action ID | 行数 | 自動(E2E済) | 自動(Probe有・要昇格) | 要新規自動化 | HITL専用 | 対象外 |
|---|---|---:|---:|---:|---:|---:|---:|
| [ReplayToHakoniwaE2ERunner](./ReplayToHakoniwaE2ERunner.md) ✅ | （7 step※） | 7 | 7 | 0 | 0 | 0 | 0 |
| [LayoutPersistenceJourneyE2ERunner](./LayoutPersistenceJourneyE2ERunner.md) | `JOURNEY-LAYOUT-01..15` | 15 | 0 | 5 | 10 | 0 | 0 |
| [AuthorToRunJourneyE2ERunner](./AuthorToRunJourneyE2ERunner.md) | `JOURNEY-AUTHOR-01..13` | 13 | 1 | 3 | 9 | 0 | 0 |
| [LiveManualTradeJourneyE2ERunner](./LiveManualTradeJourneyE2ERunner.md) | `JOURNEY-LIVE-01..15` | 15 | 0 | 9 | 4 | 2 | 0 |

※ `ReplayToHakoniwa` は Action-ID 採番より前に書かれた台本で 7 = ストーリーの step 数（Action-ID 行ではない）。
第二波で他台本と同じ操作一覧表へ整形する際に Action ID を採番する。

## 合計

- **Surface 12 本 ＝ 162 行 ／ Journey 4 本 ＝ 50 行 ／ 総計 212 行**。
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
  S3（boot→File→New）のみ残置。
  残り未昇格: MenuBar / OrderTicket ＋ Journey 3 本。順次昇格。

## Issue release-gate slice runners

Surface/Journey の網羅台帳とは別に、特定 issue の release-gate を細く正本化する slice runner。Surface 台本（特に
[StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) の STRATEGY-16 が generic Open 経路を担う）と
重複させず、その issue の wrap policy / decision の 1 不変条件をピンポイントで gate する。

| 台本 | Action ID | 行数 | 自動(E2E済) | 関連 issue / findings |
|---|---|---:|---:|---|
| [FileOpenNonMarimoE2ERunner](./FileOpenNonMarimoE2ERunner.md) ✅ | `OPEN-NM-01..04` | 4 | 4 | #86 / [findings 0054](../../../../docs/findings/0054-file-open-non-marimo-py-wraps-as-1-cell.md) |

- 第二波の昇格優先度の目安: `自動(Probe有・要昇格)`（既存 assert 流用で安い）→ `要新規自動化`（新規）。
  `OrderTicket` は view 側フォーム＋検証ゲートの Probe が無く全 15 行が `要新規自動化` なので、第二波で最も手が要る。

## 第一波で残った確認待ち（`要確認` の所在）

各台本に「要確認」として明記済み（コードで挙動が裏取りできなかった点）。第二波の runner 実装前に解消する:

- `UniverseSidebarE2ERunner`（2）— 実 `IAvailableInstrumentsProvider` の DuckDB/venue 配線、実 `set_execution_mode` 実体。
- `FooterModeE2ERunner`（1）— 実 `set_execution_mode`/`stop_live_strategy` RPC 実体。
- `InfiniteCanvasE2ERunner`（1）・`FloatingWindowE2ERunner`（2）— input-surface 層の per-event clamp / cascade の Probe 未カバー seam。
- `LayoutPersistenceJourneyE2ERunner`（4）・`AuthorToRunJourneyE2ERunner`（1）・`LiveManualTradeJourneyE2ERunner`（3）—
  mock fill が portfolio を state JSON に載せるか、AFK で footer mode を反射切替できるか、`ScenarioStartupValidation` の具体規則 等。
