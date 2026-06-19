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
| [UniverseSidebarE2ERunner](./UniverseSidebarE2ERunner.md) | `SIDEBAR-01..14` | 14 | 0 | 10 | 2 | 2 | 0 |
| [FooterModeE2ERunner](./FooterModeE2ERunner.md) ✅ | `FOOTER-01..12` | 12 | 10 | 0 | 0 | 1 | 1 |
| [HakoniwaE2ERunner](./HakoniwaE2ERunner.md) | `HAKONIWA-01..13` | 13 | 0 | 10 | 0 | 1 | 2 |
| [InfiniteCanvasE2ERunner](./InfiniteCanvasE2ERunner.md) | `CANVAS-01..09` | 9 | 0 | 7 | 1 | 1 | 0 |
| [FloatingWindowE2ERunner](./FloatingWindowE2ERunner.md) | `WINDOW-01..12` | 12 | 0 | 7 | 3 | 1 | 1 |
| [StrategyEditorNotebookE2ERunner](./StrategyEditorNotebookE2ERunner.md) | `STRATEGY-01..18` | 18 | 0 | 13 | 2 | 2 | 1 |
| [ScenarioStartupE2ERunner](./ScenarioStartupE2ERunner.md) ✅ | `SCENARIO-01..14` | 14 | 12 | 0 | 0 | 2 | 0 |
| [RunButtonE2ERunner](./RunButtonE2ERunner.md) | `RUN-01..12` | 12 | 0 | 6 | 4 | 1 | 1 |
| [OrderTicketE2ERunner](./OrderTicketE2ERunner.md) | `ORDER-01..16` | 16 | 0 | 0 | 15 | 1 | 0 |
| [DepthLadderE2ERunner](./DepthLadderE2ERunner.md) | `DEPTH-01..11` | 11 | 0 | 6 | 4 | 1 | 0 |
| [SecretModalE2ERunner](./SecretModalE2ERunner.md) | `SECRET-01..13` | 13 | 0 | 7 | 4 | 2 | 0 |

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
  supporting pin・AFK RED→GREEN、findings 0055）。残りは順次昇格。
- 第二波の昇格優先度の目安: `自動(Probe有・要昇格)`（既存 assert 流用で安い）→ `要新規自動化`（新規）。
  `OrderTicket` は view 側フォーム＋検証ゲートの Probe が無く全 15 行が `要新規自動化` なので、第二波で最も手が要る。

## 第一波で残った確認待ち（`要確認` の所在）

各台本に「要確認」として明記済み（コードで挙動が裏取りできなかった点）。第二波の runner 実装前に解消する:

- `UniverseSidebarE2ERunner`（2）— 実 `IAvailableInstrumentsProvider` の DuckDB/venue 配線、実 `set_execution_mode` 実体。
- `FooterModeE2ERunner`（1）— 実 `set_execution_mode`/`stop_live_strategy` RPC 実体。
- `InfiniteCanvasE2ERunner`（1）・`FloatingWindowE2ERunner`（2）— input-surface 層の per-event clamp / cascade の Probe 未カバー seam。
- `LayoutPersistenceJourneyE2ERunner`（4）・`AuthorToRunJourneyE2ERunner`（1）・`LiveManualTradeJourneyE2ERunner`（3）—
  mock fill が portfolio を state JSON に載せるか、AFK で footer mode を反射切替できるか、`ScenarioStartupValidation` の具体規則 等。
