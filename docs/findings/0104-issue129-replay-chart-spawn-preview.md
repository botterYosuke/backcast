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

## 回帰と再復旧（2026-06-25 #132 が C# トリガを巻き込み削除）

owner 報告「#129 が機能していない」（spawn しても chart が空・スクショ）を実機 AFK probe で再現確認した
結果、**#132「Miro 風テーマ」(`79b3978`) が `BackcastWorkspaceRoot.cs` 編集時に #129 の C# トリガ配線を
丸ごと削除**していた（テーマ変更とは無関係＝bad merge/rebase での巻き戻し）。`git log -S
RequestChartPreviewsForAllLiveCharts` で 12bcd1b 追加 → 79b3978 削除を裏取り。

- 削除されたもの: `SyncChartWindowsToUniverse` 末尾の unconditional `RequestChartPreviewsForAllLiveCharts();`
  （早期 return `if (missing.Count == 0) return;` 版に戻された）／ヘルパ本体／`_scenario.Committed += / -=`。
- 無傷だったもの: Python `populate_replay_preview` / `get_date_range`（pytest 9 passed のまま）と
  `WorkspaceEngineHost.RequestReplayChartPreview`。＝「呼ぶ人」だけ消え RPC が永遠に未発火 → 構造的に空。
- 再現（目視と同経路の AFK probe）: `[E2E CHART SPAWN PREVIEW FAIL] S1 CHARTPREVIEW-01: preview RPC
  NOT fired for 7203.TSE (SyncChartWindowsToUniverse tail helper missing/wrong _host)` exit=1・compile error 無し。
- 復旧: 上記 3 seam を現行ツリー（#137 で Settings 改修後）へ再適用し、probe を GREEN へ
  （CHARTPREVIEW-01..04 PASS / exit=0）。probe `ChartSpawnPreviewE2ERunner` は実 production トリガ
  （`Universe.Add → Sync` / `scenario.Committed`）を駆動し override seam で捕捉するため、self-trigger では
  なくこの回帰を実際に RED で捕まえた——AFK gate が機能した数少ない実例。

## 第2回帰: 配線復旧後も chart 空（`NO_DATA`）— poisoned DuckDB root（2026-06-25）

C# トリガ復旧後も owner の実機は空のまま、status bar に `[WorkspaceEngineHost] preview 8035.TSE: NO_DATA`。
diagnose loop で **真因＝#137 が DuckDB root を PlayerPrefs へ移設した際の test-isolation 欠陥**と判明。

**因果連鎖（empirical に裏取り）:**
1. `DuckDbRootSettingsE2ERunner` DUCKROOT-03（`:160-164`）が `backcast_duckroot_bad_<guid>` という temp パスを
   **実アプリ共有 PlayerPrefs** key `backcast.jquants_duckdb_root` へ `JquantsDuckdbRootStore.Save` 経由で
   書く（`Save`→`PlayerPrefs.Save()` で disk 即フラッシュ）。
2. cleanup `ClearForTests()` は `DeleteKey` のみで **`PlayerPrefs.Save()` を呼ばなかった**＝削除は in-memory のみ。
3. 本 runner は **shutdown segfault（exit 139・本 findings 0107 記載）** で終わるため Unity の正常終了フラッシュが
   走らず、disk 上の key は `Save(bad)` が焼いた dead パスを保持。
4. owner 実機 boot → `Load()` が dead パスを返す → `JquantsDuckdbRootInjector.Inject` が**検証せず** os.environ へ
   注入（非空＝.env baseline を上書き）→ preview/RUN が `<dead>/stocks_minute/*.duckdb` を読む →
   `FileNotFoundError` → `NO_DATA`。
   - 実証: `defaults read unity.DefaultCompany.backcast backcast.jquants_duckdb_root` が dead temp パスを返し、
     当該 dir は既に消失。env を正す Python 直叩きでは `populate_replay_preview("8035.TSE","2025-01-06","2025-01-10","Minute")`
     → `(True,"",1632)`＝関数は正常。差分は **resolved root だけ**。

**「目視と同じテスト」の不備（owner の問い）:** `ChartSpawnPreviewE2ERunner` は `TestReplayPreviewOverride` で
pythonnet RPC を**差し替える Python-FREE** 設計＝C# が RPC を「呼ぶ」ことしか検証せず、実 Python / 実 DuckDB /
実 root 解決を一切通らない。だから RPC 配線が直った後も GREEN のまま実画面は `NO_DATA`。
[[e2e-self-triggers-masks-missing-prod-wiring]] / [[hitl-surfaces-bugs-afk-gates-miss]] の典型。

**修正（3層・RED→GREEN 実証済み）:**
- **store**: `JquantsDuckdbRootStore.ClearForTests()` に `PlayerPrefs.Save()` を追加＝削除を disk に永続化し
  shutdown segfault を生き延びる（再 poison 防止）。実走後 `defaults read` で key 非残存を確認。
- **injector（最深修正）**: `JquantsDuckdbRootInjector.Inject` が非空 stored root を `JquantsDuckdbRootValidator.Validate`
  で検証し、解決しなければ（dead/typo/owner が消したフォルダ）注入せず .env baseline へ fail-soft。dead な
  override で全 DuckDB 読みを沈黙破壊しない。
- **immediate unblock**: owner の poisoned PlayerPrefs key を削除（→ .env baseline `/Volumes/StockData/jp` へ復帰）。
- **faithful gate（不備の是正）**: `DuckDbRootSettingsE2ERunner` に **DUCKROOT-05** を新設＝MOCK Python を起動し
  real injector で「dead stored root → os.environ が baseline へ revert（poison されない）」を assert。
  RED litmus 実証: injector の Validate guard を外すと `[E2E DUCKROOT-05 FAIL] stale stored root POISONED os.environ`、
  戻すと `[E2E DUCKROOT-05 PASS]`。`ChartSpawnPreviewE2ERunner` header にも Python-FREE の限界を明記。

## 第3回帰（真因）: poll 投影が preview iid を落とす — projection union 修正（2026-06-25）

層1（C# トリガ欠落）・層2（root poison）を直しても実機が空だった**真因**。`populate_replay_preview`
は `engine._rs.per_id_ohlc_points[iid]` を populate するが、RUN 前なので `per_id_close[iid]` は
未確定（書かない）。一方 `core.py:_build_trading_state_locked`（`get_current_state` の実体・poll JSON の
per_instrument 正本）は **`per_id_close` のキーだけを iterate** していた（旧 `core.py:484`）ため、
preview 専用 iid が `per_instrument` から脱落 → poll JSON 空 → C# `InstrumentOhlcDecoder.Decode` が
`HasSeries=false` → `ChartView.Render` されず **chart 空**。#129 初期実装からあったが層1・層2 に隠れていた。

**設計判断（grill-with-docs 2026-06-25・owner HITL B 選択）:** 候補 (a)「preview 時に `per_id_close` も書く」は
`per_id_close` の意味（=streaming で見た最終 close のみ・`get_replay_last_prices` / Replay サイドバーの正本）を
**汚す**（RUN 前の preview 銘柄にサイドバー価格が出る意味の混線）。候補 **(b)「projection をキー和集合で回す」を採用**
＝`per_id_close` の意味を保ったまま preview の cold chart を surface する。`price` は `per_id_close` が無ければ
最終ローソク close から導出（streaming の last-close と同じ意味論・`OhlcPoint.close` は model 検証で gt=0）。
- **RUN 整合**: `load_replay_data` が `_rs` を再生成し両 dict を空に戻すので和集合も空＝stale preview 無し（無改変）。
  streaming 中は両 dict が毎 bar 同時に書かれる（`reducer.py:72-80`）ので和集合＝従来と同一（挙動不変）。
- **`forget_instrument`**: 両 dict を pop 済み＝和集合からも消える（安全）。

**実データ再現（owner の正確な経路・`S:\jp`・8035.TSE・Minute）:**
```
populate_replay_preview("8035.TSE","2025-01-06","2025-01-10","Minute") -> (True,"",1632)
_rs.per_id_ohlc_points["8035.TSE"]: 1632 points ; _rs.per_id_close has "8035.TSE": False
BEFORE (per_id_close-only projection): get_current_state().per_instrument keys: []   ← owner が見た空
AFTER  (union projection):             get_current_state().per_instrument keys: ['8035.TSE']  (1632 bars, price 27025.0)
```

**faithful gate（検証不備の根治・最重要）:** 旧 PREVIEW-01..07 は `eng._rs.per_id_ohlc_points[iid]` を**直接** assert＝
poll 投影を build せず層3 をすり抜けた。新設:
- **PREVIEW-08** `test_preview_surfaces_through_get_current_state_projection` — 実 DuckDB temp fixture で
  `populate_replay_preview → get_current_state().per_instrument[iid].ohlc_points` が非空＝**production が poll する投影**を
  駆動。RED litmus 実証: 投影を `per_id_close`-only に戻すと FAIL（preview iid 脱落）。
- **PREVIEW-09** `test_projection_union_does_not_disturb_streamed_instruments` — streamed iid（両 dict 在り）は price=streamed close を
  保ち重複せず、preview-only iid と共存。union が streaming を壊さない不変条件を固定。
- **PREVIEW-10** `test_projection_surfaces_quote_only_instrument_with_empty_series` — close-only 方向の characterization。
  `per_id_close` は全イベントで書かれる（reducer.py:76）が `per_id_ohlc_points` は KlineUpdate のみ（reducer.py:102-103）＝
  TradeUpdate 由来の quote-only iid は close 在り・candle 無し。union が price=streamed close・空 series で従来どおり surface する
  ことを固定（dict.fromkeys union の将来 refactor が silent に落とさない gate）。どちらの dict も相手を包含しないので和集合が必須。
- 検証: `cd python && ./.venv/Scripts/python.exe -m pytest tests/test_replay_chart_spawn_preview.py -v` で 12 passed
  （PREVIEW-01..10 ＋ bad_granularity ＋ backend_service）。
- **限界（HITL 残）:** C# `InstrumentOhlcDecoder.Decode → ChartView.Render` の実描画は AFK/pytest では見ない。
  最終確認は owner 実機目視（CLAUDE.md「UI 操作で確認」）。投影が非空になれば decode→render は既存無改変経路。

## 自己保護

本 findings は #129 の slice 着地記録。ADR-0025 / ADR-0016 / ADR-0006 は無改変。
issue 本文の D0–D4 を canonical として fix し、下位決定 F1–F5 を本ファイルで pin する。
