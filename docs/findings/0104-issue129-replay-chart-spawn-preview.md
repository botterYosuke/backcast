# findings 0104 — Replay chart spawn preview（#129・DuckDB cold preview）

issue #129 の設計の木と実装着地の記録。issue 本文に凍結された D0–D4 を canonical 化し、
`/grill-with-docs`（本セッション 2026-06-25・owner HITL Q1）で refine された C# トリガ seam を
固定する。方針は **ADR-0025**（mode-aware cell）と **ADR-0016**（per-cell RUN）に従属し、
**execution 契約（exactly-once / no-look-ahead の strategy への bar 供給）は変えない** —— preview
は human が見る画面だけの cold 表示で、`on_bar` 経路に bar を流さない。

> **採番経緯**: 当初 **0103** で起草したが、本セッション中に並行 PR で
> `0103-kabu-connected-but-sidebar-venue-not-connected.md`（#125-#128 fix・upstream `f79290a`）が先着
> で 0103 を占有 → 衝突回避のため **0104** へ rename（grill-with-docs 「番号重複は採番ずらし」原則）。
> 既存にもう一つ別軸の重複（`0101-issue34-modify-order-ui.md` と `0101-replay-add-picker-…-virtualized.md`）が
> あり、issue 本文の `0101-replay-chart-spawn-preview.md` は未着地のまま。本 findings の本文は無改変。

## 凍結済み設計の木（issue 本文より）

- **D0** — Replay モードのみ。Manual/Auto は Venue API 由来の株価表示で対象外。
- **D1** — spawn 直後は全期間の実ローソクを描く（空 placeholder ではない）。
- **D2** — preview は RUN 前限定。RUN を押すとクリアされ、現行どおり 1 bar ずつ exactly-once で
  累積描画へ移行（`TryStartRun→load_replay_data` の `_rs` fresh reset で自然にクリア＝
  `core.py:_load_replay_duckdb_locked` で `self._rs = ReducerState(...)` 再代入 → `per_id_ohlc_points` が
  空 dict へ戻る）。
- **D3** — preview 期間 = scenario の start〜end。未入力/不正なら DuckDB 全期間(min〜max) へ
  per-instrument にフォールバック（DB が無い銘柄は空のまま）。
- **D4** — Python に preview RPC（IDLE 中に `per_id_ohlc_points` を populate）＋
  `duckdb_bars.get_date_range`。C# は spawn / scenario commit / universe 変化で RPC を呼ぶ
  トリガのみ追加。poll lane は replay_state 不問で常時稼働するため、既存 `Update()` の
  decode→`ChartView.Render` がそのまま描画する（**ChartView/Render 経路は無改変**）。

## 下位決定（grill 本セッションで固定）

### F1 — 単一 helper `RequestChartPreviewsForAllLiveCharts()` を `_chartViews.Keys` で iterate

issue 本文 D4 の「spawn / scenario commit / universe 変化」を C# で 2 つの seam に折りたたむ：
**spawn と universe 変化は実質同じ呼び出し点**（`SyncChartWindowsToUniverse`）に合流する。
ただし **scenario commit は別 SoT**（`_scenario.Params` は `Universe` と独立で、`Universe.Changed`
は params 単独編集では発火しない）なので、scenario commit hook は別配線が必要。

- (1) `SyncChartWindowsToUniverse` 末尾（helper 呼出点 `Assets/Scripts/Live/BackcastWorkspaceRoot.cs:1084`、
  helper 定義 `:1097`）— Universe.Changed / BuildWorkspace / ReseedFromEditor unconditional tail
  （`BackcastWorkspaceRoot.cs:367` の `ReseedFromEditor` 内 Sync 呼出が helper 経由で発火）の 3 入口を全てカバーする。
  restored chart も `RestoreFloating → _chartViews` 経由でこの helper の iterate に乗る。
- (2) `ScenarioStartupController.Committed` event（新設、`ScenarioStartupController.cs:46`）に subscribe
  （`BackcastWorkspaceRoot.cs` の `BuildWorkspace` で `_scenario.Committed += RequestChartPreviewsForAllLiveCharts`）
  — Settings の scenario section の Commit、OnFileSaveAs の auto-Commit、TryStartRun の Commit を 1 本で拾う。
  RUN 直後の commit→start は IDLE guard で no-op になるので二重発火しても安全。

### F2 — 契約は Python 側に置く（C# は dumb トリガ）

- IDLE / Replay guard は Python RPC 内部（`engine.replay_state == "IDLE"` AND `engine.mode == "replay"`）。
- C# は `_chartViews.Keys` を毎回素朴に投げるだけ。Manual/Auto モード、RUN 中、LOADED 中はすべて
  Python 側で no-op。「contract lives in one place」。

### F3 — RPC は 1 銘柄 1 コール（per-iid）

`load_universe_bars` は global stream の merge 用なので、preview は per-id の `per_id_ohlc_points`
への書き込みが目的：シグネチャは **`replay_preview_for_instrument(iid, start, end, granularity)`** を
1 銘柄ずつ呼ぶ形にして、C# 側は `_chartViews.Keys` をループ。granularity / start / end は
`_scenario.Params` から取り、空 / 不正のときは Python 側で per-instrument の
`get_date_range(root, iid, granularity)` フォールバック。

### F4 — D2 の RUN 整合は既存挙動の formalize（追加コードは guard だけ）

- `start_engine` 直前の `load_replay_data` が `self._rs = ReducerState(...)` を新規生成するため
  `per_id_ohlc_points` は構造的に空 dict へ戻る（追加実装不要）。
- AC#3「RUNNING 中は preview RPC が no-op」は F2 の IDLE guard でカバー（追加実装はガード文だけ）。
- AFK probe で「spawn 非空 → RUN → `ohlc_points` 本数が streamed 本数に一致」を回帰固定する。

### F5 — DB ファイル不在は graceful

- `load_bars` が `FileNotFoundError` を投げる場合、preview RPC は **per-instrument で catch**
  して空のまま返す（クラッシュしない）。silent ではなく `logging.info("preview: no db for %s")` で
  痕跡を残す。`get_date_range` も missing → `None` を返して fallback も諦める。

## 実装着地（S1 / S2 / S3）

- **S1**: 
  - Python `engine/_backend_impl.py` に `replay_preview_for_instrument(iid, start, end, granularity)`
    と `backend_service.py` に薄いラッパーを追加。`engine._rs.per_id_ohlc_points[iid]` を populate。
  - C# `BackcastWorkspaceRoot.RequestChartPreviewsForAllLiveCharts()` helper を追加し、
    `SyncChartWindowsToUniverse` 末尾と新 `_scenario.Committed` 配線から呼ぶ。
  - AFK probe を追加（`ChartSpawnPreviewE2ERunner` または既存 `ChartPlacementJourneyE2ERunner` に
    タグ追加で実 RPC 経路を経由）。
- **S2**: `duckdb_bars.get_date_range(root, iid, granularity) -> (min, max) | None` を新設し、
  preview RPC で start/end が空 / 不正のとき per-instrument フォールバック。AFK probe で空 start/end
  → 全期間描画を assert。
- **S3**: IDLE/Replay guard を unit test で固定、AFK で RUN→streamed と等しい本数の累積を assert。

## RED→GREEN

- **RED**: `_chartViews` に iid が居るのに `state.per_instrument[iid].ohlc_points` が
  空 → `ChartView.Render` が `n==0` で no-frame（issue #129 の症状）。
  litmus 再現: `BackcastWorkspaceRoot.cs:1084` の `RequestChartPreviewsForAllLiveCharts();` を削ると
  CHARTPREVIEW-01/03（`ChartSpawnPreviewE2ERunner.cs:100, :177`）が RED、`_scenario.Committed +=` の
  subscribe を削ると CHARTPREVIEW-02（`:136`）が RED、Python guard を弱めると pytest
  `test_replay_chart_spawn_preview.py::test_preview_*_run_guard` が RED。
- **GREEN**: helper トリガ → preview RPC populate → 次の poll で
  `InstrumentOhlcDecoder.Decode(state, iid).Ohlc` 非空 → `ChartView.Render` が描画。
  検証: `pwsh scripts/run-live-e2e.ps1 -Method ChartSpawnPreviewE2ERunner.Run` で
  `[E2E CHART SPAWN PREVIEW PASS]`、`cd python && .venv/Scripts/python.exe -m pytest tests/test_replay_chart_spawn_preview.py -v`
  で 9 passed。

## 関連 ADR / findings

- **ADR-0025 / ADR-0016**: 方針親。execution 契約は本 issue で touch しない。
- **ADR-0006**: J-Quants DuckDB 直読み（`duckdb_bars`）。preview もこの読み経路を再利用。
- **findings 0091 / 0095 / 0099 / 0100**: chart 窓 / Universe SoT の周辺。helper は `_chartViews`
  を読むだけで、これらの不変条件は触らない。
- **findings 0102**: Settings dialog cutover — scenario commit hook はこの再構成後の path に乗る
  （tile は Settings の Scenario section に居る）。

## 自己保護

本 findings は #129 の slice 着地記録。ADR-0025 / ADR-0016 / ADR-0006 は無改変。
issue 本文の D0–D4 を canonical として fix し、下位決定 F1–F5 を本ファイルで pin する。
