# ChartSpawnPreviewE2ERunner — 台本（Issue #129 release-gate slice / 操作網羅台帳）

`ChartSpawnPreviewE2ERunner.cs` が自動検証する issue #129 の C# wiring 半分の release gate。実装者は `.cs` と本
`.md` をセットで読む。下位事実: [findings 0104](../../../../docs/findings/0104-issue129-replay-chart-spawn-preview.md)。
方針親: [ADR-0025](../../../../docs/adr/0025-marimo-cell-drives-both-replay-and-live-mode-aware-bt.md) /
[ADR-0016](../../../../docs/adr/0016-notebook-equals-backtest-per-cell-run.md)。共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)。

> **位置づけ**: *Issue release-gate slice runner*（特定 issue #129「Replay chart 空 spawn の usability 解消」が
> *C# 配線で正しい seam に* fire することを正本化）。**execution 契約は touch しない** —— preview は cold 表示で
> `on_bar` 経路に入らない。Python 側の guard semantics（D0 mode / D2 RUN / D3 fallback / S1 graceful）は
> `python/tests/test_replay_chart_spawn_preview.py` の **PREVIEW-01..07** が gate（2 ゲート分割）。

## 直す不具合

Replay モードで chart window を spawn した直後（RUN 前）は何の系列も描画されず、ユーザが「これは何の window か」を
判断できない。期待は **DuckDB の実ローソクが spawn 直後から表示される**こと（issue #129 / D1）。

**根本原因**: 既存 poll 経路（`Update()` 内 `InstrumentOhlcDecoder.Decode(state, iid) → ChartView.Render`）は
`per_instrument[iid].ohlc_points` を読むだけで、RUN streaming が無いと空のまま。`load_replay_data → start_engine`
までは `per_id_ohlc_points` が populate されない。spawn 時に cold preview を seed する seam が無い。

## 直し方（採用）と不採用案

- **採用**: 単一 helper `BackcastWorkspaceRoot.RequestChartPreviewsForAllLiveCharts()` を立て、`_chartViews.Keys` を
  iterate して per-iid に `WorkspaceEngineHost.RequestReplayChartPreview(iid, start, end, granularity)` を呼ぶ。
  **(a) `SyncChartWindowsToUniverse` 末尾**（Universe.Changed / BuildWorkspace / ReseedFromEditor unconditional tail
  の 3 入口を集約）と、**(b) `ScenarioStartupController.Committed` event 購読**（params-only Commit を拾う）の 2 seam で発火。
  IDLE/Replay/RUN guard は **Python 側** `DataEngine.populate_replay_preview` に集約（contract lives in one place）。
- **不採用**: 「`SpawnChartWindowAt` だけで trigger」案。`RestoreFloating` が `_dockWindows.Spawn(KIND_CHART,…)` で
  chart 窓を spawn しても `SpawnChartWindowAt` を通らない → 復元 chart が preview を取れない silent regression
  （owner Q1 で却下、最も頻出するブート/Open 経路）。
- **不採用**: 「IDLE guard を C# 側で持つ」案。複数 seam がそれぞれ guard を持つと drift する。Python 単一実装に集約。

## 最重要の不変条件（litmus）

- **テストは Python-FREE**: `WorkspaceEngineHost.TestReplayPreviewOverride` で pythonnet RPC を fake に差し替え、
  C# が `RequestReplayChartPreview(iid, start, end, granularity)` を**どの seam で・どの iid に・何の params で**
  発火したかを captures に取って assert。engine の D0/D2/D3/S1 は pytest が正本（2 ゲート分割）。
- **`_chartViews` を反復元（SoT）にする**: 「spawn したばかりの iid」ではなく **live chart の SoT**を反復元にする
  ので、`RestoreFloating` 由来も `SpawnChartWindowAt` 由来も同一 helper でカバー（owner Q1 refined seam）。
- **Commit event は無条件発火**: TryStartRun→Commit→start のように RUN 直後にも Commit は走るが、Python 側 IDLE
  guard が no-op 化するので二重発火は無害。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| CHARTPREVIEW-01 | universe に銘柄追加（spawn 経路）→ preview RPC が新 iid に発火 | `Universe.Changed:427`→`SyncChartWindowsToUniverse:1036`→末尾`RequestChartPreviewsForAllLiveCharts` | TestReplayPreviewOverride 捕捉 calls に当該 iid 1 件 | 反射で hook 注入＋calls assert | 自動(E2E済) | — |
| CHARTPREVIEW-02 | Settings → scenario commit（universe 同じで start/end だけ変更）→ 全 _chartViews に発火 | `ScenarioStartupController.Commit:121`→`Committed?.Invoke`→`RequestChartPreviewsForAllLiveCharts` | calls 内 distinct iids が _chartViews の全 key を含む／End が fresh | 反射で hook 注入＋distinct/fresh-end assert | 自動(E2E済) | — |
| CHARTPREVIEW-03 | layout 復元（File→Open）由来の chart → ReseedFromEditor 末尾 sync で発火 | `OnFileOpen`→`ApplyLayout`→`RestoreFloating`→`ReseedFromEditor:367`→末尾`Sync...`→helper | 復元 chart の iid が calls に出現 | 反射で hook 注入＋calls assert | 自動(E2E済) | — |
| CHARTPREVIEW-04 | scenario の Start/End/Granularity / 空 Start / Granularity.None → params 転送形 | `RequestChartPreviewsForAllLiveCharts`→`host.RequestReplayChartPreview` | calls の last hit が (Daily/Minute/""/"Daily" 各形) を満たす | 4 case の args assert | 自動(E2E済) | — |
| CHARTPREVIEW-05 | 実ブートで universe 銘柄 chart に DuckDB の実ローソクが描画される（目視） | 実 `Awake()`→`ResumeLastDocumentOrDefault`→実 Python RPC | 実ピクセルでローソク列が出る | 実ウィンドウ・実ピクセル | HITL専用（実ブート・目視） | — |

## litmus（delete-the-production-logic）

- `SyncChartWindowsToUniverse` 末尾の `RequestChartPreviewsForAllLiveCharts();` を削除 → **CHARTPREVIEW-01 /
  CHARTPREVIEW-03 RED**（Universe.Changed と Restore 由来 chart の preview が飛ばない）。
- `_scenario.Committed += RequestChartPreviewsForAllLiveCharts` の購読を消す → **CHARTPREVIEW-02 RED**（params-only
  Commit が silent — owner Q1 が特定した穴）。
- `Params.End` でなく captured stale 値を渡す → **CHARTPREVIEW-04 RED**（fresh-params 転送が壊れる）。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath C:\Users\sasai\Documents\backcast \
        -executeMethod ChartSpawnPreviewE2ERunner.Run -logFile <abs log>
# expect: [E2E CHART SPAWN PREVIEW PASS] / exit=0  （確認は Bash `grep -a "CHART SPAWN PREVIEW"`）
# per-Action-ID タグ: [E2E CHARTPREVIEW-01 PASS] … [E2E CHARTPREVIEW-04 PASS]（rollup は単一トークン id で集計）
# compile-only ゲート: -executeMethod を外した同コマンドで error CS\d+ が 0 件。
# ランチャ経由: pwsh scripts/run-live-e2e.ps1 -Method ChartSpawnPreviewE2ERunner.Run
# Python 半分: cd python && uv run pytest tests/test_replay_chart_spawn_preview.py -v
# merged rollup: pwsh scripts/run-all-tests.ps1 -Method ChartSpawnPreviewE2ERunner.Run
```
