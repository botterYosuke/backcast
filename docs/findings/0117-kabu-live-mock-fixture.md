# findings 0117 — kabu live を回帰テストする mock fixture（実 prod 採取・codec-replayable）

2026-06-26。live チャート/集約の挙動を **実 venue に繋がず決定的に回帰テスト**するための
mock fixture と、その採取・再生ツールを整備した。**他の作業者はこれを正本として使うこと。**

## なぜ mock が要るか（実 venue で回帰テストできない理由）

live 集約パイプライン（adapter codec → aggregator → reducer → chart）の回帰を実 kabu で回すのは不可能/不安定:

- **Windows + kabuステーション本体起動 + ザラ場**が前提（CI 不可・kabu skill S1/R1）。
- **検証 18081 は market-data PUSH を一切流さない**——実データは本番 18080 のみ（memory `kabu-verify-no-marketdata-push` / findings 0111）。
- 本番採取の cleanup は `PUT /unregister/all`＝**body グローバル**で、起動中の live アプリの板/チャートを固める。
- 約定の到着は非決定的——assert が書けない。

→ **一度だけ実 prod から採取し、その PUSH ストリームを再生する**のが唯一の決定的経路。

## 正本（commit 済み）

| パス | 役割 |
|---|---|
| `python/tests/fixtures/kabu_live_mock_4sym.json` | **テスト入力の正本**。実 prod 採取（2026-06-26 10:31 JST・7203/8306/9984/285A・150秒）から trades+best-quote を抽出した **codec-replayable mock**（953KB）。`Symbol/CurrentPrice/CurrentPriceTime/TradingVolume/Buy1/Sell1` を保持し、`KabuPushFrameProcessor` でそのまま再生でき raw と同一の trade/bar を産む |
| `python/spike/kabu_capture_mock.py` | 採取ツール（本番18080・read-only・`PROD_KABU_API_PASSWORD`）。raw 全フィールド（10段板含む）を `python/spike/captures/` に書く。**raw は .gitignore**（14MB+）——必要時に再生成 |
| `python/spike/kabu_replay_multi.py` | mock を実パイプラインで再生し**4銘柄同時更新**を検証（`per_id_ohlc_points` が4本同時に育つ＝Unity per_instrument の真値） |
| `python/spike/kabu_replay_wart.py` | partial-append ワート（finding 0111 §結論-2）の数値再現。`cap` を縮めると「時間が進むのに bar が減る」を再現 |

## 規約（live 由来の挙動をテストするとき）

1. **実 venue に繋がない**。`kabu_live_mock_4sym.json` を読み、`KabuPushFrameProcessor` → `TickBarAggregator(interval=Minute)` → `live_kline_to_reducer_kline` → `apply_event` で再生する（production と同じ実クラス。再実装しない）。
2. partial-push は `live_orchestrator` と同じ毎 1.0s・変更検出ガード付き（`kabu_replay_*.py` がリファレンス実装）。
3. **full 10段 depth ladder のテスト**が要るときだけ raw 採取を再生成（`spike/kabu_capture_mock.py`）。軽量 fixture は best-quote 1段のみ（trade 導出と chart には十分）。
4. これを `behavior-to-e2e` の正本（AFK Unity probe ＋ pytest ＋ Action-ID rollup）に載せて自動ゲート化する。Unity 描画側は `KabuLiveChartRenderE2ERunner`（findings 0111）を4銘柄 fixture に拡張する。

## 実測（このセッションで確認済み）

- 4銘柄が `per_id_ohlc_points` を**同時充填**: 7203=73 / 8306=62 / 9984=150 / 285A=151 点、各3カラム（150秒で約3分足）。軽量 fixture と raw で**完全一致**（codec 忠実）。
- 密度差もリアル: partial 点数 9984/285A=148/149 vs 7203/8306=71/60。

## 再生成手順（raw が要るとき）

```
# live アプリを閉じてから（unregister/all が body グローバル）
cd python
./.venv/Scripts/python.exe spike/kabu_capture_mock.py "7203.TSE,8306.TSE,9984.TSE,285A.TSE" 150 prod
# → python/spike/captures/kabu_mock_<UTC>.json（.gitignore）
# 軽量 fixture 化は本 findings の抽出手順（trades+best-quote, separators 圧縮）に従う
```
