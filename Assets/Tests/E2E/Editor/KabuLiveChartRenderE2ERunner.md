# KabuLiveChartRenderE2ERunner — 台本（Surface E2E）

## ストーリ

「kabu 本番に live ログインし Manual/Auto に入ると、板（depth）は出るのにチャート（ローソク）が更新しない」報告
（2026-06-25）の調査結論を、決定論的な回帰ゲートに固定する。

調査（`python/spike/kabu_push_probe.py` / `kabu_pipeline_probe.py`・実 prod 18080・ザラ場）で判明:

- **板は prod で live にティックしている**（56 depth frame/18s・板が動く・14–47 trade/分。verify 18081 は完全沈黙＝別バグ）。
- **チャートは「板」ではなく「約定(trades)」で給餌**され、約定が来れば aggregator→bridge→reducer→
  `per_instrument[id].ohlc_points`→`get_state_json`（depth merge）→**C# が読む state JSON まで埋まる**
  （実測: 9501@480 で 14 本・depth と同居）。
- 約定が疎ら／無い window では `ohlc_points` が空のまま・depth は流れ続ける＝**「板満杯・チャート空」**。

→ 本 runner は **DATA 側（実 venue 購読）ではなく、state JSON → `InstrumentOhlcDecoder` → `ChartView`
の RENDER 半分**を Python-FREE で gate する（実 prod capture を fixture 化）。

## 既存カバレッジとの境界（棚卸し）

- `AddChartLadderJourneyE2ERunner`（ADDLADDER-01..05）= spawn/合成（tile が ChartView+ladder を組む・active/inset）。
  **state JSON を食わせない・描かれたローソクを見ない**と明記。← ここが穴。
- `LiveSubscribeWiringE2ERunner`(SUBWIRE-02) / `DepthLadderE2ERunner`(DEPTH-01/02) = depth 半分（`DepthDecoder.HasDepth`）。
- `KabuLiveE2ERunner`(KABU-LIVE-01/02) = 実 kabu .env login + 実板（HITL・場中）。
- **誰も gate していない**: `state JSON → InstrumentOhlcDecoder.Decode → ChartView.Render → ローソク描画`。← 本 runner。

## 操作一覧表

| Action ID | 行動 | 入口(file:line) | 観測点 | 自動判定 | カバー状態 | 既存Probe |
|---|---|---|---|---|---|---|
| CHARTRENDER-01 | 実 prod live-state JSON（9501・per_instrument 14本＋板＋top-level ohlc 1本）を decode→render | `InstrumentOhlcDecoder.Decode(state,"9501.TSE")` → `ChartView.Render` | `HasSeries=true`・count=**14**（top-level の 1本ではない）・`Candles` 配下に **2*14** rect | fixture 読込→decode→render→childCount assert | 自動(E2E済) | — |
| CHARTRENDER-02 | 不在 id を decode→render（非空虚 floor） | `Decode(state,"0000.TSE")` → `Render(empty)` | `HasSeries=false`・ローソク **0** | absent id assert | 自動(E2E済) | — |
| CHARTRENDER-03 | depth あり・`ohlc_points=[]` を decode→render（報告症状の再現） | `Decode(EMPTY_OHLC_STATE,"9501.TSE")` → `Render` | `HasSeries=true`・count=0・ローソク **0**＝「板満杯・チャート空」 | empty series assert | 自動(E2E済) | — |
| CHARTRENDER-04 | **4 銘柄** kabu live-state（7203/8306/9984/285A・実密度 73/62/150/151）を各 id ごとに decode→render | `Decode(state,<id>)` ×4 → `ChartView.Render` ×4 | 各 id `HasSeries=true`・count>0・**2*count** rect・4 count に **≥2 distinct**（locator 銘柄別曖昧性解消＝共有 series 退役） | per-id decode+render+distinct assert | 自動(E2E済) | spike `gen_kabu_4sym_chart_state.py` |
| CHARTRENDER-05 | 同 4 銘柄 state で **削除済み(不在) id** を decode→render／生存 sibling は描く | `Decode(state,"6758.TSE")` → 0 ／ `Decode(state,"285A.TSE")` → 描画 | 不在 id `HasSeries=false`・ローソク **0**・sibling 285A は >0＝removal は当該 chart だけ blank（sibling へ fall-through しない） | absent-among-siblings assert | 自動(E2E済) | — |
| LIVEUNIV-01..05 | live 運用中 add/remove の DATA 半分（実 kabu mock 再生＋`bt.universe.*`） | pytest `test_kabu_live_universe_churn.py` | add→live data／remove→feed 停止／membership 一致／no crash／reducer 一貫 | **自動(E2E済・pytest)**（[findings 0118](../../../../docs/findings/0118-live-universe-churn-kabu-mock.md)） | 自動(E2E済・pytest) | — |
| KABU-LIVE-02 | 実 kabu 本番購読→実板＋実ローソク | （実 venue） | 実 prod の per_instrument ohlc 充填 | **HITL専用**（場中・本番本体・`spike/kabu_pipeline_probe.py` で実証済） | HITL専用 | spike |

## litmus（delete-the-production-logic）

- `InstrumentOhlcDecoder` を壊す（valid な per_instrument ohlc に対し Empty を返す／top-level を拾う）→ **CHARTRENDER-01 RED**。
- `ChartView.Render` の `AddCandleRect` を抜く → **CHARTRENDER-01 RED**。
- `Render` が空 series でも描く → **CHARTRENDER-03 RED**。
- 01 と 03 で「ローソクが出る ⇔ per_instrument ohlc_points が非空」を両側から pin（02 が decoder の id 無視を floor）。
- 4 銘柄 state で locator が銘柄別 series を曖昧性解消できず共有/定数 series を返す → **CHARTRENDER-04 RED**（4 count が全同一）。
- 不在 id が sibling の series へ fall-through する → **CHARTRENDER-05 RED**（削除 id がローソクを描く）。

## 再走

```
& "$env:UNITY_EDITOR_PATH" -batchmode -nographics -quit -projectPath . \
  -executeMethod KabuLiveChartRenderE2ERunner.Run -logFile (Resolve-Path Temp/Unity_E2E.log)
# PASS: grep -a "CHARTRENDER" で [E2E CHARTRENDER-01..05 PASS] と [E2E KABU LIVE CHART RENDER PASS]、exit 0
# 4 銘柄 fixture 再生成: cd python && ./.venv/Scripts/python.exe spike/gen_kabu_4sym_chart_state.py
```
