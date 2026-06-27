# findings 0129 — Replay cold preview は scenario 窓でなく「全期間（カタログ全履歴）」を描く

**方針: [[ADR-0034]] §5「全期間データ supply」の下位決定**（ADR 本体は不変・自己保護条項あり）。実装 issue: **#156 reopen**（2026-06-27）。関連: [[0119-chart-virtualized-mesh-upgrade]]（fit-all 表示＝D-4 追補）/ [[0124-new-seeds-observe-notebook-and-default-toyota-universe]]（#169 のトヨタ種付け）/ findings 0104（ChartSpawnPreview preview seed 経路）。

> 番号注記: 0119/0120/0123/0126 は並行ブランチで番号重複している。本 finding は `ls docs/findings/ | sort` の次空き番号 0129 で採番。

## 症状（owner・実機 HITL・2026-06-27 reopen）

Replay モードで `7203.TSE`（トヨタ）チャートが**完全に空**（背景のみ・ローソク 0 本）。状態は `mode: Replay` / `Disconnected`（venue 未接続＝Replay では正常）/ `New: workspace cleared` / **run 未実行**。前任セッションの fit-all（表示のみ・[[0119-chart-virtualized-mesh-upgrade]] D-4 追補）を入れても user-visible な改善はゼロ。

## 真因（コードで確定・実機不要）

**空の原因は描画でも DuckDB root でもなく、「初期化直後（Run 前）のプレビューへ渡す日付窓」だった。** 因果は 1 本につながる:

1. **#169**（[[0124-new-seeds-observe-notebook-and-default-toyota-universe]]）の New は `ScenarioStartupController.SeedFreshDefaults(DateTime.Now, ["7203.TSE"])` で**有効な今日基準の窓** `[今日-3ヶ月, 今日]`（`ScenarioStartupParams.SeedDefaults`: `start = today.AddMonths(-3)`、`end = today`）を種付けする。
2. 初期化直後（Run 前）のチャートは `DataEngine.populate_replay_preview`（`python/engine/core.py`）が描く。修正前はこれが**シナリオの `start..end` をそのまま** `load_bars` に渡していた（全期間フォールバック `get_date_range` は**日付が空/無効のときだけ**発火する D3 設計）。
3. J-Quants の DuckDB は**過去の凍結スナップショット**（取り込み時点で固定・「今日」までは伸びない）。よって `[今日-3ヶ月, 今日]` は**カタログの末尾より後ろ**にあり、`load_bars` は **0 本**を返す → `per_id_ohlc_points["7203.TSE"] = []` → 空チャート。
4. **#169 が #156 を退行させた**: #156 当時は New の日付が空＝無効だったので全期間フォールバックが効き全履歴が出ていた。その後 #169 が**有効な**今日基準日付を種付けしたため、`_is_valid_iso_date` ガードがフォールバックを黙って無効化した。

> 補足: #156 D-5（[[0119-chart-virtualized-mesh-upgrade]]）の「全期間 cold load」は **RUN 経路**（`load_replay_data` / `_load_replay_duckdb_locked`）の話で、scenario 窓内の全 bar を載せる。owner が居た「run 未実行」状態が描くのは **preview 経路**で、ここは D-5 のスコープ外だった。preview と RUN で「全期間」の意味が割れていたのが穴。

## 決定（owner・2026-06-27）

**初期化直後（Run 前）の cold preview は、scenario の日付窓に関係なく、その銘柄の「全期間（カタログの MIN..MAX 全履歴）」を常に描く。** scenario の `start..end` は **RUN 範囲**（`load_replay_data` がその窓内でストリーミング）を定義するもので、idle なチャートが何を見せるかとは分離する。owner が AskUserQuestion で「全期間（データがある全範囲）」を選択（対案「シナリオ窓だけ＋種付け日付をカタログ範囲に合わせる」は New 時 root 未設定だと引けない等で不採用）。

これは ADR-0034 §5 の決定（「Replay は全期間を supply」）の**確認・精緻化**であって反転ではない（∴ ADR 本体は無編集・本 finding に記録して ADR を参照）。

## 修正（`python/engine/core.py` `populate_replay_preview`）

- `_is_valid_iso_date` ベースの「窓が有効ならその窓・無効なら全範囲」分岐を撤去し、**常に `get_date_range(root, iid, granularity)` で全範囲を引く**。`None`（行なし/ファイル欠落）は従来どおり `per_id_ohlc_points[iid] = []` ＋ `NO_DATA` で graceful（クラッシュなし）。
- `start` / `end` 引数は表示窓決定では未使用化。wire 契約（C# trigger / backend_service / inproc）維持のため**シグネチャは温存**（コメントで明記）。
- 死んだ `_is_valid_iso_date` ヘルパは削除（他参照なし grep 確認）。`time` import は `time.time()` で存続。
- RUN 経路（`load_replay_data`）は**無変更**＝scenario 窓を尊重（D-5 のまま）。

## gate（RED→GREEN・実 DuckDB）

`python/tests/test_replay_chart_spawn_preview.py`:

- **新規 PREVIEW-11** `test_valid_window_outside_catalog_still_draws_full_history`: カタログ＝2024 年の日足 10 本、preview に**有効だがカタログ範囲より後ろ**の窓 `["2026-03-27","2026-06-27"]`（#169 の `SeedDefaults(today)` を模す）を渡す。`count == 10`（全履歴）かつ `get_current_state().per_instrument[iid].ohlc_points` 非空を assert。**HEAD（旧コード）で RED（`count == 0`＝owner の症状そのもの）→ 修正で GREEN** を実証（2026-06-27）。
- 既存 PREVIEW-01..10 は全 GREEN 維持（各テストの渡し窓がたまたまデータ範囲と等しい/広いため全範囲化で不変）。PREVIEW-05 のドキュメント文言を「empty 時のフォールバック」→「cold preview は常に全範囲」へ更新（挙動は不変）。
- 確認: `test_replay_chart_spawn_preview.py` 13 passed、`test_replay_chart_full_period_seed.py` ほか replay 群 15 passed。

> 既存の「PREVIEW-01/07 はデータと等しい窓」「PREVIEW-05 は空/無効窓」しか無く、**有効だがカタログを外す窓**を叩く gate が皆無だったため #169 退行が緑のまま出荷された。PREVIEW-11 がその穴を deterministic に塞ぐ。

## 残る検証ギャップ（reopen item C・正直に明記）

実 Python + 実 DuckDB で `engine → state JSON → InstrumentOhlcDecoder → ChartView.RenderedBarCount > 0` を**1 本**で跨ぐ AFK runner は依然不在（Unity batchmode で実 Python を回す harness は repo が意図的に未着手・`ChartSpawnPreviewE2ERunner.cs` ヘッダの LIMITATION）。本修正は**データ層の境界**（実 DuckDB → projection）を PREVIEW-11 で deterministic に閉じた。C# decode→render は既存 Python-FREE gate（`ChartFitAllE2ERunner` / `ChartVirtualizationE2ERunner`・合成 OhlcPoint）でカバー。`per_instrument[id].ohlc_points`（実 JSON）→ C# decode の seam だけが残ギャップ。[memory: #102 console context thread gap] と同型の「境界跨ぎゲート不在」として記録。

## fit-all（表示）との合成

本修正でデータ（全履歴）がチャートへ届くようになり、それを**実際に画面で見せる**のが前任セッションの fit-all（[[0119-chart-virtualized-mesh-upgrade]] D-4 追補・`SetFitAllOnAutoScale`）。データ修正（本 finding）＋ 表示修正（D-4 追補）の 2 つが揃って初めて「初期化時に全期間 bar が見える」が成立する。前任の fit-all は棚上げではなく合成相手。
