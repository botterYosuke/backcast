# findings 0111 — kabu live「板は出るがチャートが更新しない」調査と RENDER ゲート

2026-06-25。報告: kabu 本番に live ログイン→Manual/Auto に入ると、**板(depth)は出るのにチャート(ローソク)が
更新しない**（スクショ: 板満杯・LAST「——」・チャートは軸のみ）。

## 結論（先に）

**バグではなく、チャートは「板」ではなく「約定(trades)」で給餌される**ため、約定が疎ら／無い間は
「板満杯・チャート空」になる。加えて切り分けで infra 級の事実2件が判明:

1. **kabu 検証環境(verify, 18081, `DEV_KABU_API_PASSWORD`)は market-data PUSH を一切流さない**
   （login/register/WS は通る）。→ 検証でログインすると board も chart も永遠に空。実データは
   **本番(prod, 18080, `PROD_KABU_API_PASSWORD`)** のみ。memory `kabu-verify-no-marketdata-push`。
2. partial-push が forming bar を毎秒「更新」せず「追記」し、同一分に重複 `open_time_ms` 点が積み上がる
   （描画は index 配置で出るが、1 分が N 本の重複点になるデータモデルのワート＝**未修正の別 issue 候補**）。

C# の decode+render・Python の集約パイプラインは**実 prod データで end-to-end 正常**であることを実機実測＋
AFK ゲートで実証した（下記）。

## 実機実測（spike・実 kabu・ザラ場 13:30+ JST）

`python/spike/kabu_push_probe.py`（adapter 直）/ `kabu_pipeline_probe.py`（DataEngine+LiveRunner+
LiveReducerBridge+DepthCache の本番配線ミラー＋`get_state_json` live 分岐の忠実再現）:

| 計測 | verify(18081) | prod(18080) |
|---|---|---|
| PUSH フレーム | **0**（無送信） | 56 / 18s |
| 板の変化(best bid/ask distinct) | 0 | 動く |
| CurrentPrice / TradingVolume | 無 | 有・連続変化 |
| TradesUpdate | 0 | 14–47 /分（7203/8306/9501）|
| `per_instrument[id].ohlc_points`（C# が読む JSON） | — | 約定が来れば充填（9501@480 で 14本・depth と同居） |

- 約定が**疎ら／遅い** window では `ohlc_points` 空・depth は流れ続ける＝報告症状を再現。
- prod では低位株(9501 @ 480円・ユーザー帯)でも 60s で 14 本 populate。価格帯は無関係。
- `get_state_json` live 分岐の `v.model_copy(update={"depth": d})` は `ohlc_points` を保持（depth と同居で
  C# へ届く）。reducer は live KlineUpdate で `per_id_close` も `per_id_ohlc_points` も埋める（`is_primary`
  非依存）。＝Python 側は全段シロ。

## RENDER ゲート（新設 `KabuLiveChartRenderE2ERunner`・CHARTRENDER-01..03）

DATA 半分（実 venue 購読）ではなく、**state JSON → `InstrumentOhlcDecoder.Decode` → `ChartView.Render`
→ ローソク描画**の RENDER 半分を Python-FREE で gate（既存 `AddChartLadderJourney` は spawn/合成のみで
**描画ローソクを見ない**穴）。fixture = 実 prod 9501 capture（`Assets/Tests/E2E/Editor/Fixtures/
KabuProdChartState.json`・per_instrument 14本＋10×10 板＋**top-level ohlc も同梱**＝locator が
per_instrument 側を拾うかを試す）。

- **CHARTRENDER-01**: 実 prod JSON → `Decode("9501.TSE")` HasSeries=true・count=**14**（top-level 1本ではない）
  → `ChartView.Render` が **2*14=28** candle rect を描画。
- **CHARTRENDER-02**（非空虚 floor）: 不在 id → HasSeries=false → Render(empty) → 0 candle。
- **CHARTRENDER-03**（症状の決定的再現）: depth あり・`ohlc_points=[]` → HasSeries=true/count=0 → 0 candle
  ＝「板満杯・チャート空」は空 trade series（decode/描画は正常）。

litmus（delete-the-production-logic）: `InstrumentOhlcDecoder` を壊す/ top-level を拾う → 01 RED ／
`ChartView.Render` の `AddCandleRect` を抜く → 01 RED ／ 空 series でも描く → 03 RED。01⇔03 で
「ローソクが出る ⇔ per_instrument ohlc_points 非空」を両側 pin・02 が decoder の id 無視を floor。

**結果: AFK GREEN・exit 0・`error CS` 0 件**（compile-only → `-Method` 実行）。
`[E2E CHARTRENDER-01 PASS]`=28 rect / `-02 PASS` / `-03 PASS` / `[E2E KABU LIVE CHART RENDER PASS]`。

再走:
```
& .\scripts\run-live-e2e.ps1 -CompileOnly            # error CS 0
& .\scripts\run-live-e2e.ps1 -Method KabuLiveChartRenderE2ERunner.Run
# grep -a "CHARTRENDER" Temp/Unity_E2E.log → 01/02/03 PASS + KABU LIVE CHART RENDER PASS
```

## ユーザー向けの切り分け（残 HITL）

本番ログインで、その銘柄が**現に約定しているのにチャートが数分空**なら別途調査（が、RENDER も Python も
シロなので、その場合の容疑は「銘柄ID が `<symbol>.TSE` と不一致」＝universe picker 側）。約定が疎ら／
検証ログインなら設計どおり。実 venue 購読→実板＋実ローソクの確認は KABU-LIVE-02（HITL・場中・本番本体）。

## throwaway

`python/spike/kabu_push_probe.py` / `kabu_pipeline_probe.py` は使い捨て診断（regression gate ではない）。
回帰の正本は本 RENDER ゲート＋ spike が実証した「prod は板 live・pipeline 充填」の HITL 記述。
