# 0131 — Replay 実行中チャートが再生ペースで1本ずつ前進しない（watchable playback の cursor）

issue: #182 / 方針は本 findings が正本。関連: #156・findings 0119 D-5（cold-seed 全期間）/ 0129（preview 全期間）。

> 番号注記: issue #182 本文は新規 finding 番号を名指していないが、`docs/findings/` の次空き番号として
> 0131 を採番（0130 = macOS venue-login prompt が最新）。

## 症状（再生で確認済み）

`bt.replay(bars_per_second=N)` の唯一の存在意義は「観られる再生（watchable playback）」（`backtester.py`
`replay()` docstring が明言）なのに、実行中チャートは最初のポーリングから全期間を一括描画し、`bars_per_second`
は Python ループを遅くするだけでチャートは1本も前進しない。

## 根本原因（2系統の乖離）

本番経路（`run_cell` → `bt.replay` → `ReplayKernelObserver.push_bar` → reducer → `get_current_state`）で
**チャートが読む系列**と**ストリーミングが進める系列**が乖離していた:

| 系統 | 中身 | 振る舞い |
|---|---|---|
| top-level `ohlc_points`（reducer.py:90, is_primary） | replay 1本ずつ蓄積。**ステップ追従する** | streamed count に一致 |
| `per_id_ohlc_points[iid]`（C# `ChartView` が描く） | LOAD 時に**全期間 cold-seed**（#156/0119 D-5）＋ streaming は **ts dedupe**（reducer.py:113）で no-op | 全期間のまま |

- C# `BackcastWorkspaceRoot.cs:1495` は `per_instrument[id].ohlc_points` **だけ**を描画し、top-level の
  streamed 系列は明示的に無視（`:1479` コメント）。`_chartRendered` は**系列長で dedup**。
- `core.py` `_build_trading_state_locked` は `list(points)` で per-id 系列を**全量**射影していた。
- 結論: per-id は cold-seed の全期間で固定 → チャートは1本も前進しない。

## 設計の木（grill 確定）

**決定 D1 — クリップは Python 側（`get_state_json` を作る `_build_trading_state_locked`）で行う。** C# は変更しない。
- 根拠: cursor の単一 SoT（reducer の replay clock）を Python に閉じる。C# の既存 `_chartRendered`（系列長 dedup）は
  「毎ポーリングで件数が増える」前提に**そのまま合致**＝RUNNING 中の再描画が自然に効く。pytest（`get_current_state`）で
  決定論的に回帰固定でき、issue が推奨した「get_state_json の可視カーソルを検証する pytest」を満たす。境界跨ぎ
  （pythonnet seam）を増やさない（[[issue102-console-context-thread-gap]] の教訓）。

**決定 D2 — cursor は streamed top-level `ohlc_points` の**件数**でなく**replay timestamp**（`rs.timestamp_ms`）。**
- 根拠: timestamp は global replay clock（reducer は is_primary bar でだけ `state.timestamp_ms` を進める）。
  multi-instrument では各 per-id 系列を**同じ時刻 cursor**でクリップ → 全チャートが同じ再生時刻に追従。件数 cursor は
  primary 件数を非 primary に誤適用してしまう。
- クリップ式: `per_id_ohlc_points[iid]` のうち `open_time_ms <= visible_cursor_ms` のバーだけを射影。

**決定 D3 — クリップの gate は `_mode=="replay" and (replay_state=="RUNNING" or len(rs.ohlc_points)>0)`（RUNNING-only ではない）。**
- `_mode=="replay"` 前段（code-review 反映）: クリップは Replay 概念。Live は cold-seed しないので clip は reducer の
  `primary_id=None` 不変条件（Live は全 event が primary → `rs.timestamp_ms` ≥ 全 per-id `open_time_ms`）によってのみ
  no-op になる——その不変条件に依存するのは脆い。mode guard で「Replay 限定」を明示し、不変条件が将来変わっても Live の
  サイレントなバー脱落を防ぐ（REPLAY-PLAY-05 が pin）。`force_stop_replay` は `_mode` を戻さないので break 後も clip 継続（AC4）、
  `load_replay_data` が次シナリオで再設定。
- LOADED / preview（pre-run）: top-level 空 ＆ not RUNNING → **クリップせず全期間**（#156 / 0119 D-5 fit-all を保存）。
- RUNNING 開始直後（warmup・timestamp=1・未ストリーム）: RUNNING → cursor=1 → per-id は空（"warming up"・top-level と整合）。
  ＝**全期間の一瞬フラッシュを防ぐ**。
- 完走 → host が `force_stop_replay`（RUNNING→IDLE）: top-level は全件・cursor=最終 ts → **全本描画**（AC3）。
- **観察専用セルが break**（`submit_market` なし・途中 break）→ IDLE 後も top-level=break 件数・cursor 据え置き →
  **break 時点の本数のまま**（snap back しない）＝AC4。RUNNING-only gate だとここで全期間へ戻ってしまう（force_stop が
  IDLE にするため）——これが gate を「RUNNING **or** streamed」にした理由。
- 再 load（`load_replay_data`）は新しい `ReducerState`（`ohlc_points` 空）を建てる → 次シナリオの全期間 preview が復活。

cursor 自体は reducer から導出され新規 state field 不要。ただし D4（post-run preview）で 1 つだけ bookkeeping set を足した。

**決定 D4 — post-run の `populate_replay_preview` 再シードは clip 免除（`_full_preview_iids`）。** ※レビュー指摘 H1（2026-06-28 確証 repro）の修正:
- 問題: D3 の「RUNNING **or** streamed」gate は run 完走後（`force_stop_replay`→IDLE・`_mode="replay"`・`rs.ohlc_points` 非空・
  `rs.timestamp_ms`=run 終端）も clip を armed のまま保つ。ところが `populate_replay_preview` は **IDLE で発火**し（`_scenario.Committed`＝
  Run-commit/Save As・チャート spawn・layout 復元のたび・`BackcastWorkspaceRoot.cs:1383/516`）、per-id 系列を **全カタログ**で再シードする
  （#156 reopen で run 窓から decouple 済）。その全期間 preview が**古い run cursor でクリップ**され、tail が脱落する（実機 repro: run 窓 10 本→
  preview 全 30 本が 10 本に切られる）。最も自明な実害＝完走後に**新銘柄チャートを追加**すると、その銘柄は run に居なかったのに全期間 preview が出ない。
- 修正: `populate_replay_preview` 成功時に iid を `_full_preview_iids` へ登録 → `_build_trading_state_locked` の clip はこの iid を skip
  （`visible_cursor_ms is not None and iid not in self._full_preview_iids`）。`load_replay_data` が次シナリオで clear、`forget_instrument` が discard。
- D3 の「次の preview は必ず `load_replay_data` の後」前提が不完全だった（preview は load と独立に発火する）ため、Python SoT 側で明示免除する
  （C# が「全チャートを毎回 preview する」挙動に依存しない＝D3 mode-guard と同じ堅牢化方針）。REPLAY-PLAY-06 が pin。

## 副次 AC — 空チャートの 1970 エポック軸抑制（#182 副次・owner: 一緒に直す）

確定窓に当該銘柄のバーが無くストリームも 0 本 → `per_instrument` がその銘柄を持たない → `InstrumentOhlcDecoder`
`HasSeries=false` → `BackcastWorkspaceRoot` per-id loop が `continue` で `ChartView.Render` を呼ばない → チャートは
既定状態のまま。`ChartScale.FormatTimeLabel(0)`（`translation_ms=0`）が **1970-01-01** を出すため空チャートに 1970 軸が残る。
さらに「系列があってから空になった」場合、`Render(empty)` は早期 return で `_lastTimeTicks` を**クリアせず**、stale な軸が残った。

修正（`Chart/ChartView.cs`）:
- real frame を持たないとき "NO DATA" マーカー（`_noDataLabel`・`NoDataShown`）を中央表示（ASCII＝LegacyRuntime.ttf で
  グリフ欠落 □ を回避。CJK フォント配線後に日本語化可・[[unity-mojibake-is-missing-cjk-glyph]]）。
- `Render` の空分岐で `_lastTimeTicks`/`_lastPriceTicks` を Clear ＋ `_axisLabelsDirty=true` → `RebuildAxisLabels` が
  `EnsureLabelPool(needed=0)` で全ラベルを非 active 化 → stale/1970 軸が消える。

## RED→GREEN

### Python（primary） — `python/tests/test_replay_chart_watchable_playback.py`（`@pytest.mark.scenario`）

| Action-ID | AC | 内容 |
|---|---|---|
| REPLAY-PLAY-01 | AC1 | RUNNING 中、streamed k 本に対し描画系列長が単調に k に追従（一括全表示にならない・warmup は 0 本） |
| REPLAY-PLAY-02 | AC2 | LOADED（pre-run）は全期間（#156 回帰なし。`REPLAY-FULL-01` も健在） |
| REPLAY-PLAY-03 | AC3+AC4 | 完走 → IDLE で全本／observation break → IDLE 後も break 件数のまま（snap back しない） |
| REPLAY-PLAY-04 | — | multi-instrument: 非 primary も primary cursor に追従 |
| REPLAY-PLAY-05 | — | Live/static mode は clip しない（mode guard を pin・cursor < bar でも全描画＝guard 撤去で 1 本に落ち RED） |
| REPLAY-PLAY-06 | #156 回帰 | run 完走→IDLE で `populate_replay_preview` 再シード→**全期間**（D4 の H1 修正・免除を消すと run 終端でクリップされ RED） |

- RED（fix 前）: 01/03/04 が「全期間（cold-seed 件数）が描かれる」で FAIL（例 break 4 のはずが 10）。02 は元から GREEN（no-regression 床）。
- GREEN（fix 後）: 9 件全 PASS（6 REPLAY-PLAY + 3 REPLAY-FULL）。sibling（preview/state-poll/load/v19/observer/universe）も回帰なし。
- 06 の RED（H1 修正前）: `populate_replay_preview` が全 30 本セットしても `get_current_state` が run 終端で 10 本に切る（`assert 10 == 30` で FAIL）。
- 再走: `cd python && uv run pytest tests/test_replay_chart_watchable_playback.py -v`

### C#（secondary AFK） — `Assets/Tests/E2E/Editor/ChartNoDataE2ERunner.{cs,md}`

| Action-ID | 内容 |
|---|---|
| CHART-NODATA-01 | 新規 ChartView（Render 無し）→ `NoDataShown=true` ＋ active 時間ラベル 0（既定 1970 軸が出ない） |
| CHART-NODATA-02 | 系列 Render（active ラベル>0・marker 無）→ 空 Render → `NoDataShown=true` ＋ active ラベル 0（stale 軸クリア）→ 再 Render で復活 |

- RED（実機実証）: `Render` 空分岐の `_lastTimeTicks.Clear()`＋`_axisLabelsDirty=true` を無効化 → `[E2E CHART NO DATA FAIL]
  S2 ... ActiveTimeLabelCount=3 after the series went empty (1970/stale axis persists)`。01 は marker のみで PASS。
- GREEN: `[E2E CHART-NODATA-01 PASS]` / `[E2E CHART-NODATA-02 PASS]` / `[E2E CHART NO DATA PASS]`・exit 0・`error CS` 0。
- 非空虚: Section2 は「空にする前に active ラベル>0」を precondition 床に取り、空→0 が実遷移であることを保証。
- 再走: `pwsh scripts/run-live-e2e.ps1 -Method ChartNoDataE2ERunner.Run`（.cs 編集直後は recompile-skip で 2 回／
  license は launcher が LicensingClient を起動して解決——直叩きは "Access token is unavailable" で exit 1 になる）。

## 触ったファイル

- `python/engine/core.py` — `_build_trading_state_locked` に watchable-playback cursor クリップ＋D4 の `_full_preview_iids` 免除
  （`__init__` 宣言・`load_replay_data` clear・`populate_replay_preview` add・`forget_instrument` discard）。
- `python/tests/test_replay_chart_watchable_playback.py` — REPLAY-PLAY-01..06（新規。06=H1 post-run preview 回帰ガード）。
- `Assets/Scripts/Chart/ChartView.cs` — `_noDataLabel`/`NoDataShown`/`ActiveTimeLabelCount`/`UpdateNoData`、空 Render で軸クリア。
- `Assets/Tests/E2E/Editor/ChartNoDataE2ERunner.{cs,md}` — CHART-NODATA-01/02（新規）。
- `E2E-INDEX.md` / `CONTEXT.md` — 登録・用語追記。
