# findings 0029 — v19 戦略の backcast 移植（grill-with-docs 設計ドリル）

`grill-with-docs`（2026-06-16）で導出。移植元 TTWR（`C:\Users\sasai\Documents\The-Trader-Was-Replaced`）で
動作した戦略 **v19**（`C:\Users\sasai\Documents\_blacksheep\strategies\v19_live_morning.py`）を、
nautilus を退役済みの backcast（pure-Python kernel・ADR-0006）で動かすための設計判断を固定する。
方針: ADR-0006（nautilus 退役・kernel-native strategy API）/ ADR-0001。

## v19 とは（要約）

朝の momentum クロスセクション・ランキング戦略。1分足から 23 特徴量を作り、学習済み
`HistGradientBoostingRegressor`（`v19_live_model_o3histgb_10h00.joblib`）で 50 銘柄
（liquid300 top49 + ベンチマーク `1306.TSE`）を採点。**10:00 JST に上位 K 銘柄を成行買い、14:55 JST に全手仕舞い**。
特徴量入力は板そのものではなく **毎分の「最新確定分足の OHLCV」**（`_take_snapshot` が `self.cache.bars()[-1]` を拾う）。

## API ギャップ（v19=nautilus → kernel）

v19 は `nautilus_trader.trading.strategy.Strategy` をフル活用。kernel-native `engine.kernel.strategy.Strategy`
は遥かに小さい（`on_bar` + `submit_market` + `log` + lifecycle hook のみ）。移植は字面コピーではなく**書き直し**。

| v19（nautilus） | kernel 移植先 |
|---|---|
| `self.clock.set_time_alert`（09:01–10:00 毎分スナップ / 10:00 entry / 14:55 exit / +1s model load） | `on_bar` で `bar.ts_event_ns` → JST(時,分) を復元してタイミング再現（clock/timer は kernel に無い） |
| `_take_snapshot` via `self.cache.bars()` | `on_bar` で各銘柄のバー OHLCV を直接 append |
| `order_factory.market(...) + submit_order + TimeInForce.DAY` | `submit_market(iid, side, qty)`（成行のみ。v19 も成行だけ） |
| `on_order_filled(event)` | `on_order(event)` + `isinstance(event, OrderFilled)` |
| `_query_buying_power_yen`（`cache.account_for_venue` / HTTP） | **新 seam**: `StrategyContext.buying_power()`（下記） |
| exit: `cache.orders_open`/`cancel_order`/`positions_open` | 約定から自前で建玉数量を追跡し SELL（Replay の成行は即約定→取消不要） |
| model + JSON 3点を `_resolve`（repo 2階層上 + `data/models`） | backcast 内へコピーし `__file__` 相対で解決 |
| telemetry CSV（`V19LIVE_*`） | ライブ計測 — 初手では落とす |

## 確認した実現可能性（実機）

- **ML ライブラリ**: `pyproject.toml` に joblib/scikit-learn/numpy/pandas が明記、venv に全部導入済み。kernel/engine 本体は sklearn を import しない（戦略専用依存）。
- **分足データ**: v19 の 50 銘柄は `S:\jp\stocks_minute\<code>.duckdb` に実在（抜き取り 10/10 hit）。
- **モデルのロード**: v19 model は **sklearn 1.8.0** で保存（ファイル内バージョントークン）。backcast の **1.9.0 ではロード失敗**（`ModuleNotFoundError: No module named '_loss'`）。使い捨て env で **1.8.0 ならクリーンにロード&predict 成功**（`HistGradientBoostingRegressor`・`n_features_in_=23`＝v19 `_FEATURES` と一致）。

## 決定（owner 確定 2026-06-16）

1. **対象モード = Replay と Auto の両方**。両者は同一 kernel-native strategy API の上で動くため、**移植は 1 本**で両対応（実 venue Auto の最終確認のみ別途 HITL）。「Phase 10 に延期」は #38（CLOSED・#50/ADR-0006 で `KernelLiveEngineController` に置換済）。`NoopLiveEngineController` は現在テスト専用。
2. **最初の到達点 = (A) コアを Replay で通す**（分足収集→10:00 採点→上位K成行買い→14:55 手仕舞い→結果がアプリに出る）。フル機能（買付余力・配分・テレメトリ・実 venue Auto）は **(B) として後続**だが、後付けでリワークにならないよう seam を設計する。
3. **`scikit-learn==1.8.0` に固定**（`>=1.8,<1.9`）。v19 model が 1.8.0 保存で 1.9.0 だと読めないため。engine は sklearn 非依存なので影響は戦略のみ。再学習（より高コスト）は採らない。配布ビルド #33 でも 1.8.0 を同梱する含意あり。
4. **成果物は backcast 内へコピー**（`python/strategies/v19/`：戦略 `.py` + sidecar + `artifacts/`）。`__file__` 相対で解決。`_blacksheep` 現物の絶対パス参照（非可搬）は採らない。
5. **買付余力ゲートは (B) を採用＝生かす**。Replay でも backtest の現金に収まるよう間引く。実装は **kernel `StrategyContext` に `buying_power()` を additive 追加**：Replay は `portfolio.cash`、Live/Auto は venue 余力を返す。価格は移植済みスナップショット（`_current_price` = `_snapshots[-1]["close"]`）から取れるので追加 seam は現金のみ。配分は v0（cumulative-greedy・shares=order_qty）既定、`A0_EQUAL_NOMINAL_E1` は opt-in。
6. **多銘柄 Replay 約定の修正（必須・golden 安全）**: kernel の Replay 約定は単一銘柄前提で、`fill_market(order, bar)` が**現在バーの close** で約定する（行 `runner.py` ~253-261 / `broker.py` `fill_market`）。v19 は 10:00 に複数別銘柄を一括発注するため、約定価格を **`last_prices[order.instrument_id]`（注文銘柄の直近 close）** に修正する。単一銘柄では `order.instrument_id == bar.instrument_id` ゆえ凍結 golden #24 とバイト一致を保つ。あわせて kernel の他の単一銘柄前提を軽く点検する。

   **実装時の確定（#70・grill-with-docs 2026-06-17）**:
   - **参照価格の正本を scalar → dict 化**: `_Context.reference_price`（単一 `bar.close`）を `reference_prices: dict[iid → 直近 close]` に置換。runner は各バー先頭で `{**last_prices, bar.instrument_id: bar.close}` を作り、**現在バーの銘柄だけ当該 close を overlay**する。これで単一銘柄は「当該 bar close 約定」を維持（byte-identical golden）、クロス銘柄注文は注文銘柄の直近 close で約定。
   - **`fill_market` を bar 非依存化**: `fill_market(order, *, price, ts_event_ns)` に変更。約定価格は runner が決めた注文銘柄の close、**ts は現在バーの `ts_event_ns`**（注文が出た stream 位置＝約定時刻。価格の出所銘柄の時刻ではない）。
   - **AC#3 未確定価格の扱い＝拒否**: 注文銘柄が `reference_prices` に未登録（その銘柄のバー未到来）なら **submit 時に `OrderDenied(kind=KIND_NO_REFERENCE_PRICE)`** で拒否（約定させず on_order に通知・sink へは流さない＝既存リスク拒否と同経路）。owner 確定（提示の推奨どおり）。`KIND_NO_REFERENCE_PRICE` は live/driver の同条件で既存の定数を再利用（Replay/Live で同義）。
   - **AC#4 リスク参照価格も per-instrument 化（副次の正し化）**: pre-trade gate へ渡す `reference_price` を注文銘柄の close にし、あわせて `order_notional_jpy` を旧 `0.0`（live 由来の「MARKET は submit 時 price 不明」）から **`reference_price × qty`** に変更。Replay は close が既知なので `max_order_value_jpy` rail が**従来 silently bypass されていたのを実効化**（`max_position_size_jpy` は従来どおり reference_price 由来の projected position value）。golden #24 は rails 無しゆえ不変。**この notional 実効化は #70 本筋（fill 価格）の外側の rail tightening** で、Replay のみ・live 経路は不変。
   - テスト: `python/tests/test_replay_universe_order_prices.py`（クロス銘柄が注文銘柄の close で約定＝rails 下で A 価格なら過大拒否になる構成で discriminate / 未確定は `NO_REFERENCE_PRICE` 拒否）＋ 凍結 golden `test_kernel_golden_cpython.py` GREEN。
7. **日次リセット**: v19 は「1日1プロセス」で `_placed` を一度立てると再エントリーしない。Replay は複数日を連続再生するので、**新しい取引日になったら per-day 状態（snapshots / placed / 建玉追跡）をリセット**し、毎営業日 10:00 entry / 14:55 exit を再現する（v19 の本番=毎日1往復の挙動に忠実）。

## 小ノブ（owner 確定 2026-06-16）

- entry/exit = **10:00 / 14:55**（model が `_10h00` 学習なので 10:00 決定が正。proposals の昇格候補 (10:30,14:00) は別モデル前提で射程外）。
- `top_k` = **5**（docstring/proposals の検証・昇格構成。v19 コンストラクタ既定の "10" ではなく検証済み構成を採る・owner 確定）。
- run 窓 = v19 同梱の `2025-01-06`〜`2025-01-10`（分足・5営業日）。
- **prev_close / adv_baseline は静的 JSON**（特定日に算出）。backtest 日付と厳密一致しないため `gap` / `rs` 系特徴量は近似になる。初手は artifact をそのまま使い、日次の正確な前日終値導出は将来スライス。

## faithfulness の限界（明示）

データ源が別物（v19=kabu 配信の分足 / backcast Replay=J-Quants DuckDB 分足）かつ静的 prev_close のため、
**v19 ライブと同一の売買を bit 単位で再現することは目標にしない**。目標は「v19 の**ロジック**（特徴量→モデル採点→上位K→時刻手仕舞い）が
backcast 上で忠実に動き、結果がアプリに出る」こと。

## 自己保護 / 参照

下位事実（scheduling の境界・seam シグネチャ・配分の数値）は本 findings と実装 PR に記録する。
方針レベルの上位決定は ADR-0006（nautilus 退役・kernel-native API）に従属する。
