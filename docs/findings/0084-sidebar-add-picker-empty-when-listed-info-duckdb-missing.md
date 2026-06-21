# 0084 — sidebar `[+ Add]` で銘柄一覧が出てこない（picker が scenario.end を受け取らずハードコード）

報告（2026-06-22, owner）: sidebar の **[+ Add]** ボタンを押しても銘柄一覧が出てこない。

## 結論（root cause）

**C# 配線バグ。** sidebar の picker は実際の scenario.end を受け取らず、ハードコードの
`"2024-12-31"` で `list_instruments("local", end)` を叩く。`listed_info.duckdb` のスナップショットは
**2025-12-03〜05 の 3 日分しか無い**ため、point-in-time（`MAX(Date) <= end`）クエリは 2024-12-31
時点に該当スナップショット無し → **空ユニバース** → picker は `No instruments for this date`
プレースホルダを描画 → ユーザーには「銘柄一覧が出てこない」と見える。

実機での決定的証拠（DB マウント済み・到達OK, 2026-06-22）:

| 渡す end | 銘柄数 |
|---|---|
| 実 scenario.end（factory default = 本日 2026-06-22） | **4424** |
| picker のハードコード `2024-12-31` | **0** |

→ 正しく配線されていれば 4424 件出る。データもマウントも正常。**端的に「picker が常に
2024-12-31 を使う」のが原因。**

## 配線バグの所在

- `Assets/Scripts/Universe/UniverseSidebarView.cs:51` — `string _replayEnd = "2024-12-31";`
  （フォールバック既定値）, `_mode = UniverseSourceMode.Replay`（固定）。
- 同 `:76` `Bind(..., string replayEnd = null)` は `replayEnd` を受け取れるが、
- `Assets/Scripts/Live/BackcastWorkspaceRoot.cs:534`
  `_sidebarView.Bind(_sidebarCtrl, EditorFileProvider, _font);` ← **`replayEnd` を渡していない。**
- root 内で `_sidebarView` を参照するのは field 宣言（:75）と上記 Bind（:534）のみ。
  **per-frame の mode/end push が無く、view に setter も無い** → `_mode`/`_replayEnd` は構築時の
  ハードコードのまま固定。
- 一方 `DrivePrune()`（:1046）は `_scenario.Params.End` を毎フレーム再解決している。picker も
  同様に scenario.end（と mode）を受け取るべきだが、その配線が欠落している。

帰結:
1. scenario.end が何であれ picker は 2024-12-31 を使う（本バグ）。
2. mode が Live でも picker は Replay 固定（venue universe を引けない・派生バグ）。

## あるべき形（fix の方向。実装は別途）

`DrivePrune` と同じく root が毎フレーム（または scenario.end 変更イベントで）
現在の `UniverseSourceMode` と `scenario.end` を sidebar view へ push し、picker の
`Toggle`/`Query` がそれを使う。view に `SetContext(mode, replayEnd)` 相当を足し、
`BackcastWorkspaceRoot` の Drive ループから driven にする。回帰ゲートは下記の C# AFK
characterization（picker が「ハードコード 2024-12-31」でなく「与えられた scenario.end」で
provider.Query されることを stub provider の記録で assert・現状 RED）。

## フォローアップ: 最新一覧フォールバック（owner request 2026-06-22）

配線修正後、scenario.end が空だと picker は honest に「Set scenario.end first」を表示し、2025-12 より
前の end では「No instruments for this date」になる（実機確認済み）。owner 要望で **「条件が揃わない
ときは最新の一覧を出す」フォールバック**を追加（範囲: **空 end でも・指定日に銘柄無しでも**最新へ）。

実装（picker 供給経路のみ。prune とは decoupled）:
- `BackendAvailableInstrumentsProvider.Query` — Replay の `EndUnset` 早期 return を撤去。空 end は
  backend に `""` を渡す（→ `read_listed_snapshot("")` = overall MAX = 最新スナップショット）。
- `_backend_impl._list_instruments_local` — point-in-time snapshot が空（end が全スナップショットより
  前）なら `_read_local_snapshot("")` で最新へフォールバック。**shared な `_read_local_snapshot` /
  `read_listed_snapshot` / `list_all_listed_symbols` は触らず**（honest point-in-time 維持）、picker RPC
  だけに限定（shared-contract 安全）。DuckDB-unavailable のエラー経路は不変（typed failure を mask しない）。

安全性（prune 非干渉）: picker provider と prune は別経路。prune は `_pruneSource.ReplayCatalog`
（現状 `NullUniversePruneSource`・dormant）＋ `UniversePruneGate` 自身の `IsNullOrEmpty(ScenarioEnd)→
no-prune` guard を持つ。picker の空 end 挙動を変えても prune の破壊的刈り込みは広がらない
（`UniversePruneGate.cs` の「Query を prune allowlist に流用するな」契約を遵守）。

ゲート: `python/tests/test_replay_instrument_picker_supply.py` を 8 件へ更新（旧「空 end/早 end → 空」を
フォールバック spec へ反転）。RED→GREEN litmus: `_list_instruments_local` の `if not snapshot.codes`
retry を消すと早 end/前 end の 2 件が RED。C# 側（`Query` の空 end ルーティング）は compile-gate ＋
HITL（Editor で空 End のまま [+ Add] → 最新一覧）。

## フォローアップ2: 初回 open の "Loading..." 固着（非同期供給の自動再描画）

実機 HITL で発覚: **1 回目の [+ Add] は "Loading..." のまま固まり、2 回目以降で一覧が出る**。

原因: `BackendAvailableInstrumentsProvider.Query` は背景スレッドで fetch し、cache が埋まるまで
`Loading` を返す非同期設計。provider のコメントは「picker visible 中は毎 tick polling される」前提だが、
**view は discrete な Rebuild イベント（toggle / Registry.Changed / 検索キー）でしか Query しない**ため、
fetch 着地後に再描画する契機が無く初回は Loading 固着。2 回目は cache 済みで同期 Ready が返り表示。

修正: `UniverseSidebarView.Update()` が open 中の picker を毎フレーム poll し、候補リストの署名
（`PickerSignature`：placeholder/candidate・label・already-added）が変わったら `RebuildPickerList`。
安定時は署名一致で GameObject churn 無し（cached Query 1 回/frame のみ）、closed 時は早期 return。
provider の「per-tick polling される」契約を view 側が満たす形＝bandaid でなく設計どおり。

ゲート: `Section13`（SIDEBAR-17）— StubProvider を Loading→Ready に flip し `Update()` を 1 tick 駆動。
open 直後 Loading・候補不在 → Ready 化 → `Update()` で `cand:7203.TSE` 出現・Loading 消滅。RED→GREEN
litmus: `PollOpenPickerForAsyncSupply()` を Update から消すと `did not auto-refresh` で FAIL（AFK 実証済み）。

## データ前提（副因・owner 環境）

`/Volumes/StockData/jp/listed_info.duckdb`（AFP: `sasaco-ds218`）はマウント済み・5.5MB・
13268 行だが Date は **2025-12-03〜05 のみ**。配線を直しても、scenario.end が 2025-12-03 より前の
Replay は依然 point-in-time で空になる（仕様どおり）。歴史的バックテスト期間を Replay するには
当該期間をカバーする listed_info スナップショットの取り込みが必要。

## ゲート（Python supply 経路・self-contained pytest）

本 supply 経路は Python テスト皆無だった（C# `UniverseSidebarE2ERunner` は `StubProvider` のみ
駆動し実 DuckDB を踏まない・runner L19-21 が「対象外」と明記）。合成 listed_info.duckdb を都度
生成する決定論ゲートを追加:

`python/tests/test_replay_instrument_picker_supply.py`
- `test_picker_supply_returns_universe_when_duckdb_present` — DB あり → Ready 候補 ids
- `test_picker_reproduces_stale_end_empty_when_db_has_only_recent_snapshots` — **本バグ再現**:
  DB が最近日だけ持つとき、ハードコード `2024-12-31` → 空 / 実 end（最近日）→ 4 件
- `test_picker_supply_unavailable_when_duckdb_missing` / `_when_root_unset` — DB 不在 → `LOCAL_UNIVERSE_UNAVAILABLE`
- `test_picker_supply_point_in_time_excludes_future_listings` — 将来上場を除外
- `test_empty_universe_before_first_snapshot_is_distinct_from_unavailable` — 「日付前で空」≠「DB 不達」
- `test_picker_supply_rejects_malformed_end_date` — 非 ISO end は typed error

RED→GREEN litmus（delete-the-production-logic）: `read_listed_snapshot` を「常に None」へ変異
させると DB-present 系が RED、unavailable 系は GREEN のまま。変異戻しで全 GREEN。

## 再走手順

```
cd python
BACKCAST_JQUANTS_DUCKDB_ROOT=/Volumes/StockData/jp uv run python -c "from datetime import date; from engine._backend_impl import DataEngineBackend as B; b=B.__new__(B); print('today', len(b.list_instruments('local', date.today().isoformat()).instrument_ids)); print('hardcoded', len(b.list_instruments('local','2024-12-31').instrument_ids))"
# expect: today 4424 / hardcoded 0  ← 配線バグの再現

env -u BACKCAST_JQUANTS_DUCKDB_ROOT uv run pytest tests/test_replay_instrument_picker_supply.py -v
# expect: all passed (self-contained 合成 DB)
```

## 関連

- ソース: `Assets/Scripts/Universe/UniverseSidebarView.cs`（_mode/_replayEnd 固定）,
  `Assets/Scripts/Live/BackcastWorkspaceRoot.cs:534`（Bind に end 未供給）/`:1046`（prune は再解決）,
  `Assets/Scripts/Universe/BackendAvailableInstrumentsProvider.cs`, `engine/jquants_listed_info.py`,
  `engine/_backend_impl.py`（list_instruments / _list_instruments_local）, `engine/paths.py`
- 既存 C# ゲート: `Assets/Tests/E2E/Editor/UniverseSidebarE2ERunner.cs`（StubProvider・実 supply 配線は対象外）
- ADR-0006（Replay universe を listed_info.duckdb point-in-time に確定）
