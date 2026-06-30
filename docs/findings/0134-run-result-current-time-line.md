# findings 0134 — Run Result ポップアップに「現在検討中の時刻」行を足す（Replay=バー時刻 / LiveAuto=壁時計）

**方針: [[ADR-0037]]（run_result popup）の下位決定**（ADR 本体は無編集・本 finding に記録して参照）。関連: [[0125-run-result-popup-screen-anchored-content-derived]]（popup 本体）/ [[0133-replay-minute-bars-collapse-daily-basis-mesh-overflow]]（basis=scenario granularity が正本）/ [[0044-replay-panel-real-data]]（#65 running snapshot pipeline）。

> 番号注記: `ls docs/findings/ | sort` の次空き番号 0134 で採番（0131 が並行ブランチで重複しているが本 finding は影響なし）。

## 要求（owner・2026-06-30）

Run Result（右上 screen-anchored ポップアップ）に **現在の時刻** を表示する。

- **Replay**（`for bar in bt.replay(bars_per_second=N): pass` の観察ループを含む）: いま **検討中のバーの時刻**（＝ストリーム中のローソク1本の足時刻＝グローバル replay 時計）。
- **LiveAuto**: **現在の壁時計**（real wall clock）でよい。
- LiveManual: popup 自体が出ない（[[0125]] D3）ので対象外。

## 設計判断（owner AskUserQuestion・2026-06-30）

- **Q1 時刻フォーマット → チャート軸（`ChartScale`）と同じ basis 連動。ただし owner 注記で Minute はフル日時。**
  - Daily basis → `yyyy-MM-dd`（足が日単位なので時刻は無意味）。
  - Minute basis → **`yyyy-MM-dd HH:mm`（フル日時）**。owner 注記「Minute は overnight があるので日付まで要る」＝分足シナリオは複数日に跨るため `HH:mm` だけだと曖昧。`ChartScale` の `HH:mm` をそのまま流用すると overnight で日付が落ちるので、ここだけ日付を前置する（ChartScale 規則の精緻化）。
  - LiveAuto（壁時計）→ `HH:mm:ss`（秒で刻む）。
- **Q2 完走後（full-stats 表示）も時刻行を残す → 残す（最後のバー時刻）。** 完走後は「現在検討中」が無いので、ストリームした **最終バーの時刻**（シナリオ終端）を時刻行に出す。「いつまでの run か」が一目で分かる。

## codebase 裏取り（grill F1–F7・2026-06-30・実コード確認）

- **F1 popup 本文は文字列 sink で dedup ゲート付き。** `LivePanelTileView.Refresh/ShowText` は `if (next == _last) return;`（`LivePanelTileView.cs:64/91`）＝**整形文字列が変わった時だけ** uGUI へ書く。
- **F2 LiveAuto は毎フレーム Refresh、Replay は payload ゲート。** `PushLiveTiles` は毎 Update `_runResultView?.Refresh(p)`（`BackcastWorkspaceRoot.cs:1730`・gate 無し）。`PushReplayTiles` は portfolio/summary json が変わった時だけ decode+ShowText（`:1747`）。
  - ∴ **LiveAuto に秒精度 `DateTime.Now` を入れると、F1 の dedup で「秒が変わった時だけ書く＝1Hz の時計」に自然になる**（毎フレーム mesh rebuild にはならない）。追加の per-frame ドライバ不要。
- **F3 running snapshot にバー時刻が無い。** `ReplayKernelObserver._publish_snapshot`（`replay_kernel_observer.py:144`）の `last_portfolio` dict は `buying_power/cash/equity/positions/orders/realized_pnl/unrealized_pnl` のみ。バー時刻（replay 時計）は別経路（`ReplayWindow` の `rs.timestamp_ms`・チャート用）にしか無い。**→ running snapshot に `clock_ms` を1個足す必要がある。**
- **F4 per-bar の確実な時刻供給源 = `on_equity`。** `stepper.py:431` は **毎バー無条件**（"Called unconditionally"）に `self._sink.on_equity(bar.ts_event_ns // 1_000_000, mtm_equity, cash)` を呼ぶ。観察のみ（売買ゼロ）の `pass` ループでも発火する。`ReplayKernelObserver.on_equity`（`:105`）は `_publish_snapshot` を呼ぶ。**∴ `on_equity` の `ts_event_ms` を `_clock_ms` に保持→publish に載せれば、観察ループでも毎バー時刻が更新される。**
  - ただし fill 処理は `push_portfolio`（`stepper.py:418`）が **同バーの `on_equity` より前**に publish しうる。初バーで fill が先行すると `clock_ms` が未設定（0=1970）になる窓があるので、**`push_bar`（バー先頭で発火）でも `_clock_ms` をセット**して取りこぼしを防ぐ。
- **F5 完走 snapshot は別 dict（`compute_portfolio`）で上書きされる。** `_finalize_run`（`_backend_impl.py:1236`）が `self.engine.last_portfolio = compute_portfolio(reader.fills, reader.equity_points, scenario)` で observer の dict を置換。∴ observer に足した `clock_ms` は完走後に消える。**→ `compute_portfolio` の返す dict にも `clock_ms` を足す（= `equity_points[-1].ts`＝最終バー時刻、空なら 0）。**
- **F6 JSON 経路は `PortfolioResult`→`get_portfolio` dict→`json.dumps`。** `get_portfolio_json`（`backend_service.py:103-117`）は `self.get_portfolio()` を dumps。`get_portfolio`（`backend_service.py:78` / `_backend_impl.py:1244`）は `last_portfolio` dict から `PortfolioResult` を組み、dict へ戻す。**∴ `clock_ms` は (a) `PortfolioResult` フィールド追加 (b) `_backend_impl.get_portfolio` で `p.get("clock_ms",0)` 読み出し (c) `backend_service.get_portfolio` dict に `"clock_ms": resp.clock_ms` の3点で透過する。** honest-empty（`last_portfolio is None`→`""`）は不変。
- **F7 basis の正本 = `_scenario.Params.Granularity`。** `BackcastWorkspaceRoot.cs:1517`（[[0133]] 配線）と `:1415` が同じ source を使う。`Minute`→Minute / それ以外→Daily に正規化。**popup フォーマッタは static なので granularity を引数で渡す。** epoch ms→表示は `ChartScale.cs:136` と同じ `DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime`（データ epoch を JST として扱い二重変換を避ける規約）。LiveAuto は `DateTime.Now`（この機の時計は JST・[[git-bash-tz-asia-tokyo-double-shift]] は shell 限定で C# には無関係）。

## 設計の木（D1–D6）

- **D1 Replay running 時刻 = ストリーム中バー時刻。** observer に `_clock_ms`（初期 0）を持ち、`push_bar` と `on_equity` の双方で `ts_event_ms` を代入、`_publish_snapshot` に `"clock_ms": self._clock_ms` を載せる。
- **D2 Replay complete 時刻 = 最終バー時刻。** `compute_portfolio` の返す dict に `"clock_ms": equity_points[-1].ts if equity_points else 0`。
- **D3 JSON 透過。** `PortfolioResult.clock_ms: int = 0` 追加 / `_backend_impl.get_portfolio` で読み出し / `backend_service.get_portfolio` dict に追加。honest-empty 不変。
- **D4 C# decode。** `PortfolioSnapshot.ClockMs`（long）追加・`ReplayPanelDecoder` の portfolio DTO に `clock_ms` 追加・hand-map。JsonUtility の verbatim-name 規律（欠落=silent 0 埋め）どおり DTO は snake_case。
- **D5 整形。** 新規 helper（`ChartScale` 規則の精緻化）:
  - `Daily` → `UtcDateTime.ToString("yyyy-MM-dd")`
  - `Minute` → `UtcDateTime.ToString("yyyy-MM-dd HH:mm")`（overnight 対応・owner 注記）
  - `clock_ms <= 0` → 行を出さない（pre-data 保険・通常は honest-empty で popup ごと非表示）
  - `FormatReplayRunResultRunning(snap, gran)` / `FormatReplayRunResultComplete(r, snap, gran)` に granularity と clock を渡す（complete は snap.ClockMs を流用＝finalize 後も portfolioJson は非空）。
  - `FormatRunResult(vm)` 先頭に `time <DateTime.Now:HH:mm:ss>` 行。
  - 各 view の先頭行に `time <…>` を前置（既存2行の上）。
- **D6 カード高さ。** `RunResultPopup.CardHeight` を 112→**136**（3行ぶん）に拡げる。位置（右上アンカー）・幅・他は不変。

## ADR 不要の根拠

`clock_ms` は既存 portfolio JSON への **additive フィールド**で後方互換（C# decoder は欠落を 0 埋め）。整形は既存 `ChartScale` 規約の流用・精緻化で、設計の驚きも不可逆性も無い。∴ 新 ADR を起こさず本 finding に記録し [[ADR-0037]] を参照（grill skill「ADR は sparingly」）。

## 実装スライス（behavior-to-e2e で RED-first 化）

- **RT-CLK-01** Replay running: 合成バー ts を流し snapshot→`get_portfolio_json`→decode→`FormatReplayRunResultRunning` が basis 連動の time 行を出す（Minute=フル日時 / Daily=日付）。
- **RT-CLK-02** Replay complete: finalize 後 `FormatReplayRunResultComplete` が最終バー時刻を time 行に出す。
- **RT-CLK-03** LiveAuto: `FormatRunResult` が `DateTime.Now`（HH:mm:ss）の time 行を出す。dedup で秒が変わった時のみ更新。
- **RT-CLK-04**（litmus）`clock_ms<=0` で time 行が消えること（pre-data 保険）。
- pytest: observer `_publish_snapshot` / `compute_portfolio` / `get_portfolio(_json)` に `clock_ms` が乗る回帰（`test_replay_kernel_observer` / `test_get_portfolio_json` 系）。

## 実装着地・RED→GREEN（2026-06-30）

production:
- **Python plumbing**（`clock_ms` additive field）: `ReplayKernelObserver`（`_clock_ms` を `push_bar` でセット＝bar-open は同バーの fill/equity publish より前・`_publish_snapshot` に載せる）／ `compute_portfolio`（`equity_points[-1].ts_event_ms`＝最終バー時刻）／ `PortfolioResult.clock_ms`／ `_backend_impl.get_portfolio`（`p.get("clock_ms",0)`）／ `backend_service.get_portfolio` dict（`resp.clock_ms`・error fallback も `0`）。
- **C# render**: `ReplayPanelDecoder`（`PortfolioDto.clock_ms`／`PortfolioSnapshot.ClockMs`／`DecodePortfolio` hand-map）／ `BackcastWorkspaceRoot.FormatReplayClockLine(clockMs, minuteBasis)`（`UtcDateTime`・Minute=`yyyy-MM-dd HH:mm`／Daily=`yyyy-MM-dd`／`<=0` で `""`）／ `FormatReplayRunResultRunning(s, minuteBasis)`・`FormatReplayRunResultComplete(r, s, minuteBasis)`（先頭に time 行）／ `FormatRunResult`（先頭に `time DateTime.Now:HH:mm:ss`・1Hz は dedup ゲートで自然刻み）／ `PushReplayTiles` の caller が `_scenario.Params.Granularity` から `minuteBasis` を渡す。`RunResultPopup.CardHeight` 112→136。

test（`ReplayRunResultTileE2ERunner` に RRT-CLK-01..05 追加・`.md` 操作表＋RED litmus 更新）:
- RRT-CLK-01 Replay running・Minute basis → `time 2024-05-10 09:31`（フル日時・`PortfolioRun1WithClock` の `clock_ms=1715333460000`）。
- RRT-CLK-02 complete も最終バー時刻を残す（`snap.ClockMs` 流用）。
- RRT-CLK-03 Daily basis → 日付のみ（`HH:mm` 無）。
- RRT-CLK-04 pre-data（`clock_ms<=0`）→ time 行を出さない。
- RRT-CLK-05 LiveAuto → `time …` 壁時計行。
- pytest: `test_replay_kernel_observer.py` に `test_push_bar_advances_clock_carried_in_running_snapshot` 追加・`_SNAPSHOT_KEYS` に `clock_ms`。

RED→GREEN（実機 Unity batchmode・`scripts/run-live-e2e.ps1`・2026-06-30）:
- `ReplayRunResultTileE2ERunner.Run` → `[REPLAY RUNRESULT TILE PASS]`（RRT-01..09B ＋ RRT-CLK 全 GREEN）。
- `NotebookToHakoniwaJourneyE2ERunner.Run` → `[E2E NB→HAKONIWA PASS]`（formatter 再利用の回帰なし）。
- pytest: `128 passed`（`-k "portfolio or replay or backend or get_portfolio or snapshot or notebook_replay"`・clock_ms 追加で `_SNAPSHOT_KEYS` 同期）。

## 付随して直した既存バグ（本 feature とは独立・main で既に RED だった）

**multi-scene AFK runner の `ThemeService.Changed` static subscriber リーク。** 1 batchmode プロセスで複数 scene を `OpenScene(Single)`＋`BuildWorkspace` する runner（`ReplayRunResultTileE2ERunner` / `NotebookToHakoniwaJourneyE2ERunner`）で、section 1 の root が `ApplyViewportTheme` を static `ThemeService.Changed` に subscribe したまま、section 2 の `BuildWorkspace → ApplyPersistedAppearance → SetTheme` が `Changed` を fire し、**破棄済みの section-1 `SettingsModeSegmentView`（destroyed Button）** を叩いて `MissingReferenceException` で落ちていた（`SettingsModeSegmentView.Refresh:85 ← ApplyViewportTheme:930 ← BuildWorkspace:448`）。

- **これは本 feature とは無関係**で、未改変の HEAD（`7f2c6ae`）でも同一例外で FAIL することを stash 検証で確定（production 変更を全 revert しても再現）。sibling merge（settings segment 経路）が `SettingsModeSegmentView.Refresh` を destroyed-button に晒すようになって露見した既存リグレッション。
- **修正**: `ThemeService.ResetForTests()`（docstring 明記「probe section 間で static subscriber を drop する」用途そのもの）を section 境界（RRT は `RunLiveScopeAndLatchSections` 冒頭・NBHAKO は共有 `ComposeRoot` 冒頭）で呼ぶ。production コードは無改変（production は単一 scene/session なので view が破棄後に refresh される状態は起きない＝test 専用のリーク。production hardening は層違い）。
- **bug-class sweep**（[[bug-class-sweep-and-matrix-coverage]]）: `OpenScene` を複数回呼ぶ runner を全掃。`FloatingWindowE2ERunner` / `StrategyEditorNotebookE2ERunner` は grep 上 2-hit だが実 `OpenScene` 呼び出しは **1 回のみ**（残りはコメント・各自「real scene を load する section は1つ・最後に走る」と明記）＝single-scene なので非該当。`BackcastWorkspaceProbe`（7 scene）は findings 0125 §既知の据え置き の legacy Probe（E2E rollup の正規 runner でない）＝本 slice でも rewire せず据え置き。

## code-review(simplify high) 反映（2026-06-30・correctness×cleanup 2 finder × verify）

- **[Fix・Medium／真因] LiveAuto 壁時計が event 間で凍る（1Hz で刻まない）。** 当初 `FormatRunResult` に `DateTime.Now` を入れ「`PushLiveTiles` が毎フレーム `Refresh` するので dedup で 1Hz 化する」と書いたが誤り。`RefreshLiveTiles`（`:1700`）は **Live shape では `LivePanelViewModel.AppliedCount` の drain でしか `PushLiveTiles` を呼ばない**（idle gate）。∴ backend event が来ない静かな帯では `DateTime.Now` が再評価されず時刻が固まる。→ **`DriveRunResultPopup`（毎フレーム駆動・popup 可視と mode を既に持つ）末尾に「popup が live shape で可視のとき、壁時計の秒が変わるたび `_runResultView.Refresh(_host.Panel)`」を追加**（`_runResultClockSec` で 1Hz スロットル・view の dedup ゲートが Text 書換えを秒 1 回に抑える）。Replay は poll payload 進行で bar 時刻が動くので tick 不要。
- **[Fix・Medium／reuse] `minuteBasis` 述語の 3 重複**（`:1415` chart preview / `:1517` chart basis / 新 `PushReplayTiles`）を `bool ScenarioIsMinute()` ヘルパに集約（`_scenario.Params.Granularity` の単一 decode）。ChartReplayBasis gate で chart basis 経路の無回帰を確認。
- **[Fix・Medium／reuse] epoch-ms→JST 整形が `ChartScale` と並行実装。** `ChartScale.TimeLabelStyle` に `DateTime`（`yyyy-MM-dd HH:mm`）case を追加（enum コメント `:125` が既に「DateTime」を予告していた死語を実体化）し、`FormatReplayClockLine` は `ChartScale.FormatTimeLabel` へ委譲（prefix `"time "`＋`clockMs<=0` omit ガードだけ caller に残す）。「データ epoch を JST として表示」の規約を chart 軸と単一ソース化。
- **[受容] correctness finder の他指摘は全て NOT-a-bug 確定**: JsonUtility の `long` binding（1.7e12 は int64 で truncate せず）／timezone（chart 軸と byte 一致）／`push_bar` が全 publisher に先行／`compute_portfolio` の最終 bar ts。
- **[受容・Low] 据え置き**: `ResetForTests` の 6 行コメント 2 runner 重複（test-only）／`FormatReplayRunResultComplete(r, s, …)` が `s` を `ClockMs` だけに使う（Running と signature 対称のため struct 維持）。
- RED→GREEN 再走（2026-06-30）: `ReplayRunResultTileE2ERunner.Run` → `[REPLAY RUNRESULT TILE PASS]`（RRT-CLK 含む全 GREEN・tick 追加後も RRT-06 の可視/非表示/再 arm 不変）／ `ChartReplayBasisE2ERunner.Run` → `[E2E CHART REPLAY BASIS PASS]`（`ScenarioIsMinute` refactor 無回帰）。
