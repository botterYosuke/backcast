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
| KABU-LIVE-02 | 実 kabu 本番購読→実板＋実ローソク | （実 venue） | 実 prod の per_instrument ohlc 充填 | **HITL専用**（場中・本番本体・`spike/kabu_pipeline_probe.py` で実証済） | HITL専用 | spike |

## litmus（delete-the-production-logic）

- `InstrumentOhlcDecoder` を壊す（valid な per_instrument ohlc に対し Empty を返す／top-level を拾う）→ **CHARTRENDER-01 RED**。
- `ChartView.Render` の `AddCandleRect` を抜く → **CHARTRENDER-01 RED**。
- `Render` が空 series でも描く → **CHARTRENDER-03 RED**。
- 01 と 03 で「ローソクが出る ⇔ per_instrument ohlc_points が非空」を両側から pin（02 が decoder の id 無視を floor）。

## 再走

```
& "$env:UNITY_EDITOR_PATH" -batchmode -nographics -quit -projectPath . \
  -executeMethod KabuLiveChartRenderE2ERunner.Run -logFile (Resolve-Path Temp/Unity_E2E.log)
# PASS: grep -a "CHARTRENDER" で [E2E CHARTRENDER-01/02/03 PASS] と [E2E KABU LIVE CHART RENDER PASS]、exit 0
```
