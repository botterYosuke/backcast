# backcast

`The-Trader-Was-Replaced`（Bevy + 埋め込み Python の取引アプリ）の**後継・本線フロントエンド**。
Unity(C#) のゲーム内に同じ空間（Infinite canvas / Hakoniwa / Floating window）を再構築し、
取引 engine（Python/Nautilus）を pythonnet で**同一プロセスに埋め込む**。方針は ADR-0001。

## Language

**backcast**:
本線（going-forward）の Unity フロントエンド。取引 engine を所有し、埋め込み Python で
Replay / Live / Auto を動かす。
_Avoid_: Unity 版（曖昧）、新フロント

**The-Trader-Was-Replaced（TTWR）**:
backcast の前身となる Bevy(Rust) アプリ。カットオーバー（#5）までは凍結された fallback として
本番に温存し、その後**廃止**する。going-forward の開発は行わない。
_Avoid_: Bevy 版を「現行/本番」と呼ぶこと（fallback かつ廃止予定であり本線ではない）

**ExecutionMode（実行モード）**:
engine 正準の実行モード enum＝ **`Replay` / `LiveManual` / `LiveAuto`** の3値（`models.py` /
`mode_manager.py` / TTWR `protocol.rs`）。口語の「Live」は **`LiveManual`（実発注・手動）**、「Auto」は
**`LiveAuto`（実発注・自律戦略実行＝"The Trader Was Replaced" の本丸）**を指す。`LiveAuto` の engine 実配線は
**#38（CLOSED）で実装され、#50/ADR-0006 で pure-Python `KernelLiveEngineController` に置換済**（mock venue AFK GREEN・
実 venue は HITL #23 系）。かつて「Phase 10 に延期」とされた `NoopLiveEngineController` は**現在テスト専用 placeholder**
（gRPC 疎通検証用・engine 未接続）であって本番経路ではない（`engine_controller.py` の docstring 参照）。`LiveManual` の
demo roundtrip 統合ゲートは #23。
_Avoid_: 「Live」と「Auto」を同義で使うこと（手動発注 vs 自律売買で別物）／`LiveAuto` を未実装・placeholder と見なすこと
（#38/#50 で実配線済・`NoopLiveEngineController` はテスト専用）

**engine**:
host 非依存の Python 取引エンジン（Nautilus ベース、`python/engine`）。TTWR から backcast へ
**移植**して backcast が所有する。host（Bevy/Unity）とは **sink 注入点・2 入口モジュール
（`engine.core` / `engine.inproc_server`）・dict 境界**でのみ接し、host 型を import しない。
_Avoid_: backend、Python バックエンド（engine が正）

**Backcast Execution Kernel（kernel）**:
backcast 専用の**最小 pure-Python 取引エンジン**。NautilusTrader（Rust core `nautilus_pyo3`）を
**置換**し、in-proc を保ったまま Windows-Mono の多重 CRT/FLS teardown crash を構造的に消すための実体
（方針: ADR-0004 案 C）。最小コンポーネント = `EventLoop`/`Strategy`/`OrderEngine`/`Portfolio`/
`RiskEngine`/`ReplayBroker`/`LiveBroker`/`EventSink`。Replay と Live で同一 strategy API。
**Nautilus 互換 framework 全体ではない**（多資産・HFT・汎用 message bus 等は非目標）。tracer は #24。
_Avoid_: 「Nautilus の再実装」「汎用取引フレームワーク」と呼ぶこと（backcast 専用・最小スコープが正）／
nautilus の `DataEngine`/`ExecutionEngine`/`RiskEngine` と同一視すること（同名でも別物・kernel は Rust core を import しない）

**KernelLiveEngineController**:
kernel を Live/Auto 経路へ繋ぐ `LiveEngineController` Protocol（`attach`/`detach`/`cancel_inflight_orders`）の
**pure-Python 実体**。本番既定の `NautilusLiveEngineController`（`NautilusKernel` を起こし Rust core をロードする）を
置換し、Live でも Rust core 非ロードを保つ（方針: ADR-0004 案 C・記録: findings 0011・#25）。`NautilusLiveEngineController`
と**同一の ctor seam**（loop/adapter/runner provider・on_order_event/on_telemetry/on_strategy_log/on_safety_violation・
run_gate_provider）を満たし、swap は `live_orchestrator` の生成箇所の class 名だけ。Live UI 配送は既存 backend_events
seam のまま（`EventSink.push_*` を Live 配送路にしない）。
_Avoid_: `NautilusLiveEngineController` と機能等価と見なすこと（後者は Rust core を引く・前者は引かない）／
`EventSink` を Live UI のチャネルにすること（AC④ は projection 互換ゲートであって配送路変更ではない）

**LiveBroker（kernel）**:
kernel `OrderEngine` ↔ 実 venue `OrderingVenueAdapter` の約定 bridge。Replay の `ReplayBroker`（bar close で決定的約定）
に対応する Live 実体で、`adapter.submit_order/cancel_order/modify_order` を叩き、同期 `OrderResult` と（将来の）非同期
EC イベントを**同一入口 `apply_venue_update` に正規化**して order FSM（SUBMITTED 以降）を駆動する。fill 重複排除は
**累積約定数量 delta**（受信イベント数ではない）。mock venue tracer の authoritative fill source は同期 `OrderResult`
（非同期 reconciliation は #23）。記録: findings 0011。
_Avoid_: `ReplayBroker` と同一視すること（fill source・タイミングが別）／受信イベント数で dedup すること（累積数量が正）

**取消受付 / 取消確定（cancel acknowledgment vs confirmation）**:
ack-then-poll venue（kabu）では `PUT /cancelorder` の成立は**取消受付**にすぎず、注文はまだ open（`PENDING_CANCEL`）。
終端の**取消確定**（`CANCELED`・約定残ゼロ）は `GET /orders` polling が後追いで運ぶ。受付を終端と誤認すると、受付〜確定の
隙間で起きた競合約定を取りこぼす（kabu cancel ACK を terminal と誤認・#25 finding 1）。adapter は受付を `PENDING_CANCEL`、
確定を `CANCELED` で**返し分け**、LiveBroker は `PENDING_CANCEL`/`PENDING_UPDATE` を**非終端**として注文を open に保ち、競合
約定を会計し続ける。instant-confirm な mock venue は受付＝確定なので即 `CANCELED` を返す（venue ごとの差は adapter の返り
status だけで表現し broker に venue 分岐を入れない）。同様に**訂正受付**（`PENDING_UPDATE`）も確定まで `new_qty` を注文へ
反映しない。**#25 が実装したのは broker 側の honoring のみ**（mock 経路で証明）。実 kabu の `cancel_order` の
`CANCELED`→`PENDING_CANCEL` 返し分け・poll→`apply_venue_update` の非同期確定配線・sibling consumer
（`ManualOrderFacade` / legacy `NautilusVenueExecClient`）の honoring は **#23 で一括対応**（3 つが揃って実 kabu live で
end-to-end に塞がる）。記録: findings 0011・#25/#23。
_Avoid_: 取消受付（`PENDING_CANCEL`）/ 訂正受付（`PENDING_UPDATE`）を終端・成立扱いすること（受付であって確定ではない）／
mock の即 `CANCELED` を全 venue の cancel 契約と一般化すること／受付時点で `new_qty` を注文数量へ確定反映すること

**訂正の atomicity（`modify_is_cancel_replace`）**:
venue が訂正を atomic に行えるか否かの安定した contract fact。**tachibana** は atomic（`CLMKabuCorrectOrder`・
mock も atomic）＝`False`。**kabu** は訂正 API が無く「取消 → 新規発注」変換で実現する非 atomic＝`True`で、取消成功＋
新規失敗で**原注文だけ消えて代替注文が無い**実害がありうる（adapter は `CANCELED`＋`reject_reason="MODIFY_NEW_FAILED:…"`
で返す）。manual 経路では訂正は**同期確定**（adapter は受付ではなく確定 status を返す）＝cancel と違い `PENDING_UPDATE` は
返らない。capability は **Python（active adapter）が宣言**し poll snapshot（`get_state_json`）で Unity へ運ぶ（frontend は
`venue=="kabu"` 分岐を持たない・ADR-0001）。UI は `True` のとき訂正 modal に警告＋「理解した上で訂正」ack を Confirm の前提に
する。記録: findings 0101・#34。
_Avoid_: frontend に venue 名分岐を置くこと（capability は Python 宣言を読む）／非 atomic venue で事前警告 ack 無しに訂正を
出せること

**確定バー / partial バー（`KlineUpdate.is_closed`）**:
`LiveRunner` は bucket-rollover で生成した**確定バー**（`is_closed=True`）と、UI 用に 1 秒間隔で publish する
進行中の**partial バー**（`is_closed=False`）を同じ `KlineUpdate` 型で bus に流す。kernel live driver は
**確定バーだけ** `on_bar` に渡す（partial を渡すと毎秒重複発注する）。UI 側 `LiveReducerBridge` は partial を含む
従来挙動を維持。記録: findings 0011・#25。
_Avoid_: partial バーを strategy の `on_bar` に渡すこと／`is_closed` 無しで bus の `KlineUpdate` を strategy に流すこと

**板 / depth（`DepthSnapshot`）**:
ある銘柄の最新オーダーブック。買い板 `bids`（price 降順想定）と売り板 `asks`（price 昇順想定）の
段（`DepthLevels`: `price`/`size`）の集合。**Live のみ**で `mock`/kabu/立花 adapter が `DepthUpdate` を
emit → `DepthCache` が `DepthSnapshot` 化して保持 → `get_state_json()` が `per_instrument[id].depth` に合成する。
**Replay では常に `None`**（過去再生は板を持たない）。順序は producer 側の「想定」契約であり `DepthCache`/models は
sort を強制しない（受信順を忠実保持し、消費側は並べ替えない）。
_Avoid_: 板を「価格履歴/チャート」と混同すること（depth は最新断面・時系列ではない）／Replay で板が出ると期待すること（Live 限定）

**bid/ask ladder（C# depth 描画）**:
`get_state_json()` の `per_instrument[id].depth`（`DepthSnapshot`）を C# 側 durable decoder が復元し、
買い板/売り板を段組みで描画する Unity パネル。`per_instrument` は instrument-id キーの dict で `JsonUtility` が
モデル化できないため、decoder は目的銘柄の `depth` オブジェクトだけを構造認識 locator で剥がして `JsonUtility` に渡す
（`LiveBackendEventDecoder.PeelTag` と同型のハイブリッド）。decode は wire 順を忠実復元し、ladder は受信順のまま描く。
_Avoid_: ladder を defensive sort すること（producer 契約違反を隠す）／`per_instrument` 全体を `JsonUtility` で読もうとすること（dict は非対応）

**golden 契約（Backcast vs Nautilus oracle）**:
kernel の正しさを担保するため、**NautilusTrader（standalone CPython）を比較 oracle として温存**し、その実出力を
golden として固定する規律。golden は sink の生 JSON 文字列ではなく **parse・正規化した契約**（order 状態列 /
fill 数・価格 / position 数量 / realized PnL / 最終 cash・equity / **sink イベント順序**）＋ provenance
（nautilus version・`PRECISION_BYTES`・strategy/catalog/scenario の hash）。golden は**計算で組み立てず必ず
oracle 経路から記録**する（自己参照を避ける）。oracle subprocess と kernel subprocess を別プロセスで走らせ、
`capture`（明示生成）／`verify`（read-only・差分で失敗）を分ける。方針: ADR-0004 案 C・記録: findings 0008。
**runtime からの nautilus 完全排除（[[市場データソース（J-Quants DuckDB 直読み）]]・ADR-0006）に伴い、nautilus oracle は
"生かし続ける比較相手" から退役**し、#24 で取得済みの golden を**凍結した正解表（回帰 fixture）**として残す（新規 capture に
nautilus を起こさない）。新データ（DuckDB 直読み）の faithfulness は **既知銘柄の data-equivalence チェック**（例: 8918.TSE 日足の
本数・OHLCV）で担保する。
_Avoid_: golden を kernel と同じ仮定から計算すること（oracle ではなく期待値の自己照合になる）／
生 JSON のバイト一致を parity 条件にすること（正規化値＋イベント順が正）／oracle 退役後に nautilus を runtime/CI 既定へ
復帰させること（凍結 fixture ＋ data-equivalence が正・ADR-0006）

**口座評価額(equity) / 現金(cash) / 買付余力(buying_power)**:
[[get_portfolio]] projection の**別物 3 値**。**equity ＝ 口座評価額 ＝ cash ＋ Σ(建玉 × 最新値)（mark-to-market）**、
cash ＝ 実現現金、buying_power ＝ 買付余力。**Replay と Live で同義・ソースだけ違う**（方針: ADR-0007）:
- **equity**: Replay ＝ [[Backcast Execution Kernel（kernel）]] の `mark_to_market_equity({iid: bar.close})`、
  Live ＝ venue 口座評価（kabu `/positions`×`CurrentPrice`・tachibana 建玉×時価）。
- **cash**: Replay ＝ kernel `portfolio.cash`、Live ＝ venue 現金（kabu `/wallet/cash` 等）。
- **buying_power**: Replay ＝ cash（CASH 口座・現状）、Live は **venue 余力が権威**（kabu `/wallet/cash`・
  tachibana `CLMZanKaiKanougaku`）。
建玉を保有したまま run が終わっても equity は建玉時価を含むので正しく、drawdown/sharpe も口座評価ベースになる
（live の MTM 接続＝findings 0011・#25 と一致）。kernel の `Portfolio.equity`（==cash・sink/oracle の
`balance_total` ビュー）とは別物——projection の equity は MTM の方。
_Avoid_: equity と cash を同義に使うこと（建玉保有時に乖離。現状 `compute_portfolio` が 3 値を cash に潰しているのは
ADR-0007 の退治対象）／buying_power を Replay 固有の venue 概念と思うこと（Live で venue 権威・Replay は cash）／
kernel `Portfolio.equity`（==cash）を口座評価額と混同すること（口座評価は `mark_to_market_equity`）

**Replay portfolio projection（走行中ライブ）**:
Replay 実行**中**に 4 base パネル（BuyingPower/Positions/Orders/RunResult）へ出す実数値。**post-run の
[[get_portfolio]]（`_finalize_run` で 1 回算出）とは別物**——走行中ずっとライブ更新される投影で、ソースは
[[Backcast Execution Kernel（kernel）]] の live `Portfolio`（`cash`/`mark_to_market_equity`/`open_positions`/
`realized_pnl`）＋ `ReplayKernelObserver` が貯める**累積約定ログ**。**transport は `TradingState` poll への additive
ブロック**（depth と同じく毎フレーム payload に相乗り・別 RPC を増やさない）。Orders ＝約定の積み上げログ（最新 1 件では
ない）、RunResult ＝走行中は約定数/realized/equity を逐次・**Sharpe/Sortino/最大ドローダウンは走行完了時に確定**（全 equity
カーブ依存）。最初の売買前は実 cash（=initial_cash）/建玉0/ログ空＝**honest-empty "(no data — Replay)" の脱却**（#65・
findings 0046・方針: ADR-0007/0006）。
_Avoid_: 走行中の投影を post-run `get_portfolio`/`compute_portfolio` で駆動すること（16 分の throttle 中ずっと空になる）／
3 値を cash に潰すこと（equity は MTM・上の [[口座評価額(equity) / 現金(cash) / 買付余力(buying_power)]] 規約）／走行中に
Sharpe 等を部分カーブから出すこと（誤誘導・完了時に確定）／Replay 投影を `_host.Panel` push（Live 専用 seam）に乗せること
（Live は push・Replay は poll で別経路）

**adapter（C# adapter 層）**:
Unity(C#) 側で pythonnet を介し engine を駆動する単一の境界。engine の sink 口に C# 製 sink を
差し、結果を GIL なしで読める C#/native バッファへ渡す。engine を host 非依存に保つための seam。
_Avoid_: bridge、wrapper

**live event sink（C# `LiveBackendEventSink`）**:
Live 経路で engine が押し出す **backend_events**（`OrderEvent`/`AccountEvent`/`LiveStrategyEvent`/
`LiveStrategyTelemetry`/…）を受ける C# 製 sink。`engine.core.set_rust_event_sink(...)` で差し込み、
`push_json(bytes)` を実装して GIL-free な `ConcurrentQueue<string>` に積む。engine 側は
`DataEngineBackend.publish_backend_event` → `_backend_event_to_wire_dict`（ADR-0018 A2 の**外部タグ付き**
wire・例 `{"OrderEvent": {...}}`） → `sink.push_json` の生産経路をそのまま通る。TTWR の Rust sink の役割を
置換する C# 実体（記録: #20・findings 0011）。**Replay の `ReplayEventSink.push_bar` とも `EventSink.push_*`
（kernel 内部 serializer・AC④ projection 互換ゲート専用）とも別物**：前者は market-data bar、後者は kernel→
ReplayPanel JSON projection で **Live 配送路ではない**（D2）。Live UI 配送の権威は本 sink が受ける backend_events。
_Avoid_: `EventSink`/`ReplayEventSink` と同一視すること／#20 コメントの「Replay sink 契約と同型」を Live 配送路の
指定と読むこと（あれは #24/#25 の **projection 互換**を指す。Live sink wire は外部タグ付き BackendEvent）

**venue 接続状態（connection state — AC「VenueLoggedIn/Out」の正体）**:
backcast に `VenueLoggedIn`/`VenueLoggedOut` という push event は**無い**。接続状態は役割の異なる 3 源から
扱う: (1) `venue_login` RPC ACK = login の**即時結果**、(2) `get_state_json` の `venue_state`
（`DISCONNECTED`/`AUTHENTICATING`/`CONNECTED`/`SUBSCRIBED`/`RECONNECTING`/`ERROR`）＋ `venue_id`
（接続中のみ載る）= **唯一の継続 canonical state**、(3) `VenueLogoutDetected` backend event = health watchdog 由来の
外部切断を知らせ**再ログインを促す通知**。UI badge は **(2) の poll から導出**する（接続中＝`CONNECTED/SUBSCRIBED/
RECONNECTING` のみ venue_id を載せる既存規律で stale バッジを防ぐ）。**(3) は badge を直接 `DISCONNECTED` へ
変える権威ではない**——通知後も badge は poll の収束を待つ。secret flow とは独立。記録: findings 0012・#21
（login UI 所有 = D4 は #122/findings 0093 が supersede: subprocess 廃止・in-process tkinter）。
_Avoid_: `VenueLoggedIn`/`VenueLoggedOut` push event を新設すること（存在しない）／`VenueLogoutDetected` を
badge 変更の権威 state として扱うこと（通知であって canonical は (2) の poll）

**セッション当日有効 / セッション生存（date-validity vs startup liveness）**:
立花セッション（仮想 URL 一式・1 日券）の「使えるか」は**二段**で別概念。(1) **当日有効**＝
`tachibana_file_store.is_session_valid_for_today()`：キャッシュの `issued_jst_date` が JST 当日かの
**日付チェックのみ**（necessary but not sufficient）。(2) **生存（liveness）**＝サーバ側でその仮想 URL が
まだ生きているか。同一 JST 日でも夜間閉局越え・サーバ無効化で死に得る（`p_errno="2"`＝仮想 URL 無効）。
当日有効を生存と誤認すると、死んだ session を `is_logged_in=True` のまま掴み発注経路に入る（#35 のバグ）。
起動時の生存確認は `tachibana_auth.validate_session_on_startup()`：純読取りの認証付き REQUEST
（`CLMZanKaiKanougaku` 買余力）を 1 発撃ち `check_response` で `p_errno="2"`→`SessionExpiredError`。
adapter `login()` の `session_cache` 分岐が当日有効チェックの直後・`_ensure_ec_stream()` の前に呼び、
失効なら `SESSION_CACHE_EXPIRED` に翻訳して既存の prompt 再ログイン誘導へ落とす。runtime の失効検知
（[venue 接続状態] の `VenueLogoutDetected`）とは別物——あちらは接続後の外部切断、こちらは起動時の掴み直し。
記録: findings 0015・#35。
_Avoid_: `is_session_valid_for_today`（当日有効）を「session が生きている」の意味で読むこと（日付のみ・
生存は別）／起動時失効を `VenueLogoutDetected` で通知すること（startup 失効は login エラー
`SESSION_CACHE_EXPIRED`＝再ログイン誘導が正・watchdog 通知は runtime 専用）

**公開鍵認証 / 暗号化仮想 URL（v4r9 pubkey auth）**:
立花 API は v4r9（`e_api_v4r9`）で認証が **電話認証単独 → 公開鍵（RSA）認証**へ移行した。ログインは
**認証ID（`sAuthId`）単独**で、パスワード（旧 `sUserId`/`sPassword`）は送らない。本人性は「応答で返る
**暗号化された仮想 URL を秘密鍵で復号できること**」で証明する設計。応答の仮想 URL 5 本
（`sUrlRequest`/`sUrlMaster`/`sUrlPrice`/`sUrlEvent`/`sUrlEventWebSocket`）は RSA 暗号化されて返り、
クライアントが秘密鍵で復号する（base64 → `PKCS1_OAEP`/`SHA256` → `utf-8-sig`）。復号は Python に集約
（`tachibana_auth`、pycryptodome `Cryptodome`）し Rust に鍵を渡さない。認証情報の供給は 2 系統：① Fernet
Fernet `secure_config`+`API_DECRYPT_KEY`（本番級）② dev env（開発）。**demo/prod は別セット**（立花は認証情報が
環境ごとに別物）で `resolve_credentials(is_demo=...)` が env を切替＝**本番=無印 / デモ=`_DEMO` サフィックス**
（demo: `DEV_TACHIBANA_AUTH_ID_DEMO` / `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO`）。**未読書面判定**は旧 `sKinsyouhouMidokuFlg=="1"` フラグ → **`sUrlRequest` 空文字検出**へ置換
（`p_errno=0 && sResultCode=0` でも空なら契約締結前書面 未読＝`UnreadNoticesError`）。新エラー
`p_errno="9"`＝システム停止中（利用時間外）。issue #92 の `ST p_errno=2 "session inactive."` は v4r8 ログインが
返す旧式（非暗号化）EVENT WS URL のセッションをサーバが無効扱いした症状で、本移行が修正。
方針: [ADR-0023](docs/adr/0023-tachibana-v4r9-pubkey-auth-cutover.md)・[findings 0087](docs/findings/0087-tachibana-v4r9-pubkey-migration.md)。
_Avoid_: v4r8 を一部残して EVENT WS だけ v4r9 化すること（暗号化 URL は v9 ログインからしか返らず部分移行不可）／
秘密鍵・復号後の仮想 URL を log/repr すること（セッション秘密・`mask_secrets` 経由）

**certifi（外部 venue WS の TLS trust）**:
外部 venue（立花など）の `wss://` ハンドシェイクで参照する CA bundle。
`tachibana_ws.py` の module-level `_TLS_CTX = ssl.create_default_context(cafile=certifi.where())`
で明示する（Windows-Unity-embedded Python は OS system trust store を引かないため
`websockets.connect` の自動 fallback では失敗する。findings 0053 §issue#85）。`pyproject.toml` に
direct dependency として宣言し、httpx の transitive 依存に**乗っからない**ことを invariant とする
（next major で外された/差し替えられた時に静かに壊れる経路を断つ）。`wss://` URL に対してのみ
`connect_kwargs["ssl"]` を立てる scheme gate と、ctor の `ssl_ctx` optional 注入で将来の社内 CA / proxy
対応の余地を残す。`TickerEventWsHub` 経由の FD/trades 経路も module default に乗って自動で塞がる。
_Avoid_: 起動 script の `SSL_CERT_FILE` env で代替すること（3 系統配線で抜けが silent regression 化）／
`truststore.inject_into_ssl()` を本ゲートで採用すること（社内 CA / proxy 対応は将来検討）／
httpx の transitive に乗ったまま certifi を direct 宣言しないこと（httpx の major で外されると EC push が全滅）

**移植（port）**:
engine のソースを TTWR から backcast へ移し、backcast を唯一の home にすること。
submodule 参照でも pinned-package-from-TTWR でもない（TTWR は廃止されるため）。
_Avoid_: 共有、依存（TTWR を生かしたまま参照する含意を避ける）

**seam ゲート（S0 / S2-spike）**:
threading の継ぎ目を段ごとに検証する throwaway spike。**S0**（#2）= threaded **backtest**
（有界・1 回 run）、**S2-spike**（#7）= live **asyncio loop**（長時間・tokio・venue WS・polling）。
前段の green は後段の保証にならない、を前提に分けて立てる。S2-spike の**核の未知数**は
S0 が触れていない **cross-thread asyncio marshal**：host worker が live loop へ
`run_coroutine_threadsafe(coro, loop).result(timeout)` 越しに仕事を投げ、`.result()` 内部の
GIL 解放→loop スレッドが GIL 取得→coro 実行→worker 再取得、という **Mono 上の GIL 往復**が
健全か。**green 判定は「ハングしない」ではなく「prompt completion」**（elapsed ≈ coro の固有コスト）：
GIL starve でも `.result(timeout)` は無限ハングせず `TimeoutError` を投げるため、毎コール timeout
する壊れた系も「ハングしない」を満たしてしまう。
_Avoid_: spike をまとめて 1 つにすること／「no-hang＝green」と判定すること（prompt completion が正）／
人間向け表記で裸の `S2`（`S2-spike` が正・`Step 2`=#4 と衝突）

**S/Step/slice の呼称規律（命名衝突の回避）**:
接頭辞 `S<n>` の素トークンは **spike 専用に予約**（`S0`=#2 / `S2-spike`=#7）。移行の段は **`Step <n>`**（Step 1=#3 / Step 2=#4 / Step 3=#5）。**Step 1 の子スライスは番号で呼ばず記述名**を使う:
**Replay tracer**（#9・seam tracer・close 済）/ **Replay chart**（#10）/ **Replay panels**（#11）/ **Replay layout**（#12）。
**Step 2（#4）の子スライスも同様に記述名**を使う（依存順）:
**Windows live prerequisites**（S0 Win + S2-spike Win + S2-spike playmode を実測し #7/#2 の残ゲートを閉じる。S0 Windows PASS で ADR-0001 を `accepted` へ昇格）/
**Venue contract verification**（kabu/tachibana の不変条件を実行可能な characterization test + Windows pytest で固定）/
**Live adapter tracer**（C# lifecycle owner・engine marshal・live event sink・Unity panel drain。mock venue で AFK GREEN 先行）/
**Venue login and secret flow**（kabu Verify / tachibana demo・`SecretRequired→submit_secret→SecondSecretResolver`・平文を env/log に残さない）/
**Live safety and graceful shutdown**（rails/gates/watchdog・orphan 不在・`graceful-stop→cancel resting→loop teardown→Python finalize`。demo 発注より前に必須）/
**Live demo roundtrip**（実 venue demo で発注→約定→建玉表示＋正常終了時の残注文取消を owner が確認する最終統合ゲート）。
code / docs / findings / ログの識別子も `replay_chart_*` / `ReplayChart…` / `[REPLAY CHART PASS]`、`live_adapter_tracer_*` / `[LIVE DEMO ROUNDTRIP PASS]` のように記述名で書く（`S2` 等の数字採番は使わない）。
これは `S2-spike(#7)` ≠ `Step 2(#4)` ≠ slice の "2" の三重衝突、および `S1`（slice-1=#9）との再衝突を構造的に消すため。
_Avoid_: Step 1 子スライスを `S1`/`S2`/`S3`/`S4` と数字で呼ぶこと（"S" が Spike/Slice/Step に三重過負荷するため）

**sink（push sink）**:
engine が host へデータを**押し出す**口。Replay では adapter が C# 製 sink を engine の sink 口へ差し、
worker スレッド（GIL 保持）が per-bar で `push_bar` / `push_order` / `push_portfolio` /
`push_run_complete` / `push_run_failed` を呼ぶ。payload は **JSON 文字列**（既存 Bevy `RustBacktestSink`
契約と同一・zero-copy ではない）。C# sink メソッドは enqueue して即 return し、main は GIL なしで drain する。
_Avoid_: bridge、callback（sink が正）／binary buffer と混同すること（zero-copy viz は #8 の別物）

**Replay parity**:
Bevy で出来ていた Replay 体験を Unity 単体で**挙動として**再現すること（status/run_result/positions/
orders/チャートが更新される）。#3 の done ゲートは挙動 parity であり、shippable standalone build は gating
条件ではない（Editor playmode で満たしてよい）。
_Avoid_: バイト/出力完全一致や shippable build を parity の条件に混ぜること

**レイアウト parity（capability parity）**:
レイアウト保存/復元の「Bevy 同等」は**能力等価**であって**形式互換ではない**。Unity は自前の versioned
スキーマで同じ UI 状態（floating window rect/z-order・Hakoniwa tile 順・canvas pan/zoom 等）を round-trip
できればよい。Bevy の sidecar 形式を読む reader は作らない（Bevy は #5 で廃止）。方針: ADR-0003。
_Avoid_: 「Bevy 同等」をバイト互換/同一スキーマと解釈すること

**layout binder**:
live な uGUI（RectTransform: anchor + pixel offset）と、永続化用の **正規化表示矩形**を持つ
`LayoutDocument`（Unity 自前 versioned スキーマ）との間を双方向変換する UI 側の層。`Capture`（live →
document）と `Apply`（document → live）の 2 口。スキーマを RectTransform 実装詳細に固定しないための seam。
実装型は `LayoutBinder`（#12）。
_Avoid_: **adapter と呼ぶこと**（adapter は engine/pythonnet 境界専用の予約語。layout binder は UI⟷document
変換で別物）／bridge、wrapper

**Backcast workspace root（合体 scene / 本番 composition root）**:
全 UI 表面（menu bar / sidebar / 中央 infinite canvas workspace / footer / Python floating editor）を
**1 つの authored scene（`BackcastWorkspace.unity`）に合体**させ、通常 Play の **唯一の起動入口**にする
production composition root。型は `BackcastWorkspaceRoot`（scene 上の root GameObject/component）。
**single Play-owner**：root 有効時は root だけが Python engine を所有し、Replay/Live 駆動は durable な
`WorkspaceEngineHost`（engine lifecycle / launcher / poll / transport+live RPC・**永続単一 server で Replay と Live を兼用**）へ抽出する（**ADR-0010** が ADR-0009 D4 の per-run `ReplayEngineHost` 所有モデルを supersede）。root が UI authored View
（[[Hakoniwa（split-grid surface）]] / [[floating window / FloatingWindowLayer / z-order]] / `MenuBarView` /
`UniverseSidebarView` / footer）と Host を結線し、layout は [[layout binder]] 系の versioned スキーマで
保存/復元する（ADR-0003）。**chrome（menu/sidebar/footer）は Content 外＝画面固定**、中央 workspace のみ
infinite canvas（下記）。`Tools > Backcast` の per-part HITL ハーネスは引き続きメニューから個別起動でき、
root 稼働中に Python 系 HITL を起動しても `PythonEngine.IsInitialized` 判定で安全に拒否される（engine を奪い合わない）。
方針: **ADR-0005**（1:1 表面 parity）。
_Avoid_: 「mainline」を恒久型名・正式 term に使うこと（移行期語）／`RuntimeInitializeOnLoadMethod`
自動 bootstrap を production 起動入口と呼ぶこと（HITL 暫定手段・本線は scene authored）／root を engine
orchestration の置き場にすること（駆動は `WorkspaceEngineHost` へ分離・ADR-0010）／`ReplayEngineHost` を現行型名と呼ぶこと（ADR-0010 で `WorkspaceEngineHost` へ一般化済の旧名）

**infinite canvas**:
chart / status tiles（Hakoniwa）/ floating window が乗る、無限スクロール・ズーム可能な **同じ空間** の土台
（CONTEXT 冒頭の「同じ空間」の具体物）。uGUI 実現は **固定 Viewport ＋ 単一 Content transform**：
**pan = Content の canvas 論理座標移動**、**zoom = Content scale（カーソル中心）**。canvas 上の widget は
Content の子なので pan/zoom に自動追従する。**screen-fixed chrome（menu / sidebar / footer / modal）は
Content の外**に置き追従しない（TTWR 構造と同型: chart+status は Hakoniwa 内＝world-space、chrome は画面固定）。
Bevy の同等機能の **capability parity**（ADR-0003・形式非互換）。土台の実装は #13、Hakoniwa 移設は #14、
floating window は #15（予定）。
_Avoid_: ScrollRect（有界コンテンツ前提で別物）／world-space＋camera（ScreenSpaceOverlay shell と別系統）／
pan を画面ピクセルで保持すること（canvas 論理座標が正）／#11 panels を恒久 HUD と定義すること（暫定であり
#14 で Hakoniwa として canvas へ載る）／TTWR の `OrthographicProjection.scale`（大きいほど zoom out）と
**zoom 値を数値互換にすること**（uGUI `localScale` は逆向き。Unity ネイティブの意味で持ち capability parity）

**canvas 論理座標（canvas logical coordinate）**:
infinite canvas の Content 座標系の座標。**画面ピクセルでも zoom 後ピクセルでもない**。pan の永続値はこの
論理座標で保持し、resolution / zoom / Viewport サイズに非依存にする。永続化される canvas view 状態
（pan の論理座標 + zoom 倍率）は #12 の `LayoutDocument` に **panel の `LayoutRect` とは独立した additive
フィールド**として載る（capability surface 追加・findings 0004 §10 の予約項目）。
_Avoid_: pan/zoom 状態を panel `LayoutRect`（正規化 0..1 表示矩形）に混ぜること（別次元・別フィールド）

**Hakoniwa（ドッキング window クラスタ）** ※2026-06-25 **再々々々定義**・方針 [[ADR-0017]]＋[[ADR-0018]]＋[[ADR-0019]]＋[[ADR-0024]]＋**[[ADR-0029]]**（findings 0075／0080／0088／**0106**）:
infinite canvas の Content 上で、**独立した floating window**（`chart` / `positions` / `orders` / `run_result` /
`buying_power` / `startup`）が **同一 [[window group / groupId]] を共有してくっついた集合**を指す概念ラベル。
専用の bounded サーフェス GameObject（HakoniwaRoot）は持たず、全 window は奥プレーン `DockLayer`（ADR-0018・1.0倍）の子。
くっつき方は drag 中の **in-drag 磁石吸着（[[R_SNAP]] = 96px）+ release-position commit（最寄り flush へ snap して merge）**（ADR-0024）。
**~~[[Hakoniwa group]] 概念は ADR-0024 で退役~~**（startup / run_result の特別扱い無し・全 group が同じ挙動）。drag mode は **[[gesture channel]] で gesture 開始時に固定**（[[ADR-0029]]）: plain title-bar drag → **島移動**（距離無制限・別島に flush で merge・detach は起きない）／ **[[eject つまみ]]・Alt+drag → 単窓ピックアップ**（島から 1 枚抜き出し・ドロップ先で swap / merge / detach 決定）。~~ADR-0024 の cursor 位置動的 3 mode 判定（< [[D_DETACH]] で translate / ≥ で detach）~~は **ADR-0029 で supersede**（距離トリガ撤廃＝owner「detach 感度が高すぎ・島の遠距離移動不可」「内外で化けて分かりにくい」）。
~~旧定義（SUPERSEDED・findings 0007・TTWR `src/ui/hakoniwa.rs`/ADR 0011/0014 parity）: `ceil(√n)` split-grid に tile を
並べ swap で並べ替える単一サーフェス~~——ADR-0017 で TTWR split-grid parity から意図的逸脱し退役（grid/box-grow/swap/per-mode を全廃）。
~~ADR-0017 直後の中間定義「磁石スナップでくっついた集合・結合なし・各 window 常に独立」~~——ADR-0019 で「結合あり・groupId による group lifecycle」へ反転。
~~ADR-0019 の中間定義「core 含み group は Hakoniwa group として全体 translate 禁止 / 内部 swap・core は detach 不可」~~——ADR-0024 で「全 group 同一挙動・cursor 位置で動的 mode 判定」へ再反転（owner の "puzzle game プルン" 体感要件）。
~~ADR-0024 の「cursor 位置で per-frame 3 mode 判定・detach は距離 256px トリガ」~~——ADR-0029 で「**gesture-channel で gesture 開始時に mode 固定**・距離トリガ撤廃・detach はドロップ先の結果」へ反転（owner「すぐ detach する／島の遠距離移動不可／内外で化けて分かりにくい」）。
_Avoid_: 「Hakoniwa = 固有の GameObject/bounded box」と捉えること（クラスタの概念ラベル・実体は floating window 群）／
**global** タイリング強制・resize 連動を持ち込むこと（free-placement・**例外は swap 時の島内局所 reflow のみ**＝[[ADR-0029]] D6）／drag mode を cursor 位置の per-frame 判定で持つこと（ADR-0029 で **gesture-channel 固定**へ・距離トリガは退役）／chart を「Hakoniwa tile」と呼ぶこと（**chart は floating window**＝旧 Avoid 反転・ADR-0017）／「結合なし・各 window 独立」を不変条件と扱うこと（ADR-0019 で反転＝group 関係を `groupId` で持つ・ADR-0024 でも維持）／startup / run_result を core として特別扱いすること（ADR-0024 で Hakoniwa special 廃止＝全 window 同等）

**window group / groupId** ※2026-06-21 新設・2026-06-22 drag mode 更新・**2026-06-25 gesture-channel 化**・方針 [[ADR-0019]]＋[[ADR-0024]]＋**[[ADR-0029]]**（findings 0082／0088／**0106**）:
floating window の **永続的な集合関係**。`FloatingWindowLayout.groupId: string?`（nullable・GUID `grp_<hex32>`）に保存され、同一 `groupId` を共有する **visible/live 集合が 2 個以上** のとき "group"（≒ "island"）とみなす（singleton は group 不成立）。group の attach は **flush 隣接判定**（`|dragged.edge - other.opposite_edge| ≤ 1px` ∧ 直交軸 overlap > 0）が release commit 後にトリガ＝ADR-0024 では **release-position rule**（cursor が別 island と overlap なら最寄り flush へ snap → merge）＋ **in-drag 磁石吸着**（[[R_SNAP]] = 96px で flush 位置へ実描画 snap）が attach 経路。drag 中の挙動は **[[gesture channel]] で gesture 開始時に固定**（[[ADR-0029]]・~~ADR-0024 の cursor 位置 per-frame 3 mode 判定を supersede~~・drag 中は化けない）: **①島移動**（plain title-bar drag）= island 全体を実描画で平行移動・**距離無制限**・magnetic snap・別島に flush で release すれば merge・swap も detach も起こさない／**②単窓ピックアップ**（[[eject つまみ]] or Alt+drag）= 島から 1 枚抜き出して運び**ドロップ先で決定**: 兄弟 rect 上 → **swap**（[[swap drop target]]・サイズ維持＋島内局所 reflow）／別島に flush → **merge**（singleton 経由）／空き地 → **detach**（A.groupId=null・残 <2 で連鎖 dissolve）。merge 衝突は **size 最大 > 辞書順最小 > 新規 GUID**（ADR-0024 simplify＝ADR-0019 D5 の Hakoniwa-priority 退役）。detach commit で dragged の `groupId=null`、残 visible/live が 1 なら連鎖 dissolve（残 1 も null）。**hide は groupId 温存**（Replay/Live mode 切替で復活）、**close（universe sync 削除等）は連鎖 dissolve**、**spawn は groupId=null**（attach はユーザ drag-release だけ）。**cross-plane group は禁止**（ADR-0018 plane 分離と整合）＝restore 時に多数派 plane 残し・同数なら dock plane 優先で分割。group 関係は座標から再導出せず `groupId` が唯一の真実源。**ESC キャンセル**: drag 中 ESC で実描画 / ghost を rest へ spring 200ms で revert（state は不変）。
_Avoid_: same-edge 整列だけで attach すると期待すること（flush 必須）／groupId を doc 外で再導出すること（persist が SoT）／singleton を group と扱うこと（≥2 必須）／cross-plane で同一 `groupId` を許可すること（restore で分割される）／spawn 時に自動 attach すること（ユーザ drag-release だけが attach 元）／core 含み group を Hakoniwa group として特別扱いすること（ADR-0024 で退役）／drag 中に groupId / geometry を commit すること（MouseUp 一括 commit が ADR-0019 D8 規律＋ADR-0024 で維持）

**~~Hakoniwa group~~** ※2026-06-21 新設・**2026-06-22 SUPERSEDED by [[ADR-0024]]**（findings 0088 §0 Q1）:
~~[[window group / groupId]] の特殊形態。判定: **visible/live 集合 ≥ 2 ∧ core（`startup` ∨ `run_result`）を visible で含む**~~。**ADR-0024 で概念退役**——startup / run_result を含む island を特別扱いしない（owner Q1=A・"puzzle game プルン" 体感を全 group 共通にするため）。今後 "Hakoniwa group" の語を新規コードに使わない。drag mode は cursor 位置で動的判定（[[window group / groupId]] 参照）。
_Avoid_: Hakoniwa group を新コード / コメントで使うこと（ADR-0024 で退役）／core 含み group を特別扱いするコードを足すこと

**~~core member~~** ※2026-06-21 新設・**2026-06-22 SUPERSEDED by [[ADR-0024]]**（findings 0088 §0 Q1）:
~~[[Hakoniwa group]] の判定に使う kind 集合 = **`startup` / `run_result`**~~。**ADR-0024 で概念退役**——startup / run_result は他の window と同等のドラッグ挙動。detach 不可 invariant も廃止（全 window が D_DETACH 超えで detach 可）。`DockShape.IsCoreKind` は ADR-0020 first-launch factory grouping の base 窓 ID 列挙には引き続き存在しうるが、ドラッグ判定からは参照されない。
_Avoid_: core / non-core で挙動を分けるコードを新規に書くこと（ADR-0024 で全 window 同一挙動）

**~~D_DETACH（detach 閾値）~~** ※2026-06-21 新設・2026-06-22 値再較正・**2026-06-25 SUPERSEDED（退役）by [[ADR-0029]]**（findings 0082／0088 §11／**0106 §6**）:
~~drag 中の detach 判定閾値 = `256f` canvas-logical px。判定 = `|cursor - drag_start| ≥ D_DETACH` で detach~~。**ADR-0029 で距離トリガごと退役**——detach はもはや距離ではなく「**[[単窓ピックアップ]]（[[eject つまみ]]/Alt+drag）を兄弟でも別島 flush でもない空き地にドロップした結果**」（owner「detach の感度が高すぎてすぐ引き剥がれる・島の遠距離移動も不可」＝距離 1 軸では島移動と detach を分離できない）。`FloatingWindowMath.D_DETACH_PX` 定数は**削除**。新たに distance を判定に使わない。
_Avoid_: detach を距離・速度・閾値で判定するコードを書くこと（ADR-0029 でドロップ先の結果に統一）／`D_DETACH_PX` を新コードで参照すること（削除済み）／島移動に距離上限を設けること（無制限）

**R_SNAP（磁石吸着半径）** ※2026-06-22 新設・方針 **[[ADR-0024]]**（findings 0088 §2・§11）:
in-drag 磁石吸着の発動距離 = **`96f` canvas-logical px**（owner Q15=C）。`FloatingWindowMath.R_SNAP_PX` 定数。island translate / detach mode で毎フレーム発動: dragged の **外周辺**（translate なら island の外 4 辺・detach なら A の 4 辺）と他 window の対辺との最短距離が `≤ R_SNAP` ∧ 直交軸 overlap > 0 のとき、実描画位置を flush 位置へ snap（実窓ごと寄せる）。cursor が R_SNAP 圏外に出るまで貼り付く（stickiness）。発動の瞬間は [[spring animation]] 200ms で補間（"プルン"）。release で snap 位置が確定したら最寄り flush merge へ統合（[[window group / groupId]] release-position rule）。離散 snap で物理シミュレーションではない（owner Q6=A・連続吸引は AFK の "snap-on-release" 権威モデルを崩すため不採用）。
_Avoid_: R_SNAP を screen pixel にすること（zoom 非依存性）／毎フレームの座標変動を AFK で再現しようとすること（mode 状態と最終 rect の assert で十分）／swap mode に R_SNAP を適用すること（swap は center-in-rect 判定で edge attraction 無し）

**swap drop target** ※2026-06-21 新設・2026-06-22 scope 拡張・**2026-06-25 サイズ維持＋島内局所 reflow 化**・方針 [[ADR-0019]]＋[[ADR-0024]]＋**[[ADR-0029]]**（findings 0082／0088 §1／**0106 §4**）:
**[[単窓ピックアップ]] 中、カーソル直下にある同 `groupId` の他メンバー**（dragged 除く・hidden 除く・最前面 sibling 優先・**center-in-rect 判定**）。release で swap commit。**~~ADR-0024 の `(x,y,w,h)` 4 値交換~~は ADR-0029 で supersede**＝**サイズ維持＋[[island-scoped reflow]]**: A/B は各自の `(w,h)` を保ち **位置（anchor）だけ交換**し、はみ出し/隙間が出ないよう**同じ island の隣窓だけ**が `ぷるん` 寄って best-effort に flush 自動調整（owner Q5「サイズ違いを許容して自動調整」）。perfect tiling は非保証・残隙間は free-placement として許容。reflow scope は **island に厳密限定**（他 island・他 plane に波及しない＝global no-reflow 維持）。ghost は **post-swap + reflow 後の島レイアウト**を予告。cross-island swap は不可（同 island 限定）。
_Avoid_: swap で `(w,h)` まで交換すること（窓サイズが勝手に変わる＝owner の「サイズ違いを許容」とズレる・ADR-0029 で退役）／reflow を island の外へ波及させること（global no-reflow 違反）／reflow に perfect tiling を要求すること（best-effort・残隙間許容）／cross-island swap を許可すること

**drag ghost** ※2026-06-21 新設・**2026-06-22 scope 縮小**・方針 [[ADR-0019]]＋**[[ADR-0024]]**（findings 0082／**0088 §7**）:
drag 中の **post-release 状態の半透明プレビュー**（alpha **0.45**・kind accent border・dragged ghost は solid border・target ghost は dashed border）。ADR-0024 で **swap プレビュー専用**へ縮小・**ADR-0029 で swap は [[単窓ピックアップ]] 中の兄弟 rect 上でのみ**: ghost は **post-swap + [[island-scoped reflow]] 後の島レイアウト**（dragged + target + reflow で動く隣窓）を予告。**島移動 / detach は実描画**（実窓 / 実 island が cursor について動く・magnetic snap は実窓ごと spring 補間）＝ ADR-0019 D8 の ghost-only モデルを反転（owner Q13=B）。drag 中だけ実 window より前面 sibling に描画される一時 UI（drag 終了で破棄）。**commit-on-release**: drag 中は groupId・geometry 不変、ghost / 実描画はあくまで preview。release で初めて swap/translate/detach/merge を確定。**ESC キャンセル**: drag 中 ESC で ghost / 実描画とも rest へ [[spring animation]] 200ms で revert（state は不変）。
_Avoid_: ghost を drag 中に commit と扱うこと（preview のみ・release で commit）／translate / detach に ghost を被せること（実描画で十分・冗長）／単独 window drag や island translate に ghost を入れること（#15 AFK と挙動互換を壊す）

**spring animation** ※2026-06-22 新設・方針 **[[ADR-0024]]**（findings 0088 §3・§11）:
"プルン" 体感の rect 補間 animation = **200ms / overshoot 8% / 1-overshoot ease-out-back**（owner Q14=B）。`FloatingWindowMath.SPRING_DURATION_MS = 200` / `SPRING_OVERSHOOT_RATIO = 0.08f`。**trigger**: ① [[R_SNAP]] 磁石吸着発動の瞬間／② ESC キャンセル（rest へ）／③ release commit 完了時（swap / merge / detach の最終 rect）／**④ swap 時の [[island-scoped reflow]] で寄る隣窓の rect 補間**（[[ADR-0029]] D6）。前 tween が走っている間に次が始まったら前を kill して新 tween へ。fire-point は**確定点のみ**（commit / ESC）＝per-frame tween と controller の毎フレーム書き込みを競合させない（findings 0088 §14 code-review 1 の教訓）。Unity の `Animator` / `LeanTween` 等 1 ヘルパー（`AnimateRectSpring(window, from, to, ms=200)`）に集約。
_Avoid_: 200ms を超える長い animation を入れること（連続操作で重い）／overshoot ratio を 8% より大きくすること（puzzle feel を超えて派手・連続発火で汚い）／bounce（2-3 回弾む）にすること（owner Q14=B で却下）／drag 中の毎フレーム rect 補正を spring tween で重ねること（snap 発動瞬間だけ・continuous 補間は free drag）

**gesture channel** ※2026-06-25 新設・方針 **[[ADR-0029]]**（findings 0106 §1）:
floating window のドラッグ mode を決める **2 チャンネル**。`OnBeginDrag` 時点で「何をどう掴んだか」で 1 度だけ確定し、その drag の間は**化けない**（~~ADR-0024 の cursor 位置 per-frame 判定~~を supersede＝owner「内外で swap/translate に化けて分かりにくい」の根治）。`DragChannel { IslandMove, SingleWindowPickup }`。**IslandMove**＝plain title-bar drag（island 全体を平行移動・距離無制限・merge あり・detach なし）。**SingleWindowPickup**＝[[eject つまみ]] or `Alt`+drag（島から 1 枚抜き出し・ドロップ先で swap/merge/detach 決定）。
_Avoid_: drag 途中で channel を切り替えること（gesture 開始で固定）／distance や cursor 内外で mode を再判定すること（ADR-0029 で退役）

**単窓ピックアップ（SingleWindowPickup）** ※2026-06-25 新設・方針 **[[ADR-0029]]**（findings 0106 §3）:
[[gesture channel]] の一方。[[eject つまみ]]（title-bar の常時可視 "⤴"・主アフォーダンス）or `Alt`+title-bar drag（ショートカット）で起動し、island から 1 枚（A）を抜き出して実描画で運ぶ。release のドロップ先で結果確定: 同 island の兄弟 rect 上 → **swap**（[[swap drop target]]）／別 island に flush（磁石 engaged）→ **merge**（singleton 経由）／空き地 → **detach**（A.groupId=null・残 <2 で連鎖 dissolve）。membership を減らす操作（detach）は本チャンネル限定＝[[IslandMove]] では起きない（owner「すぐ detach する」の根治）。
_Avoid_: detach を本チャンネル以外で起こすこと／距離で detach を判定すること（ドロップ先で決まる）

**eject つまみ** ※2026-06-25 新設・方針 **[[ADR-0029]]**（findings 0106 §1）:
各 floating window の title-bar に常時表示する**単窓ピックアップ起動アフォーダンス**（"⤴" アイコン・第 2 raycast target）。「ここを掴めば 1 枚剥がせる」を可視化し invisible mode 批判を構造的に消す（owner Q2＝つまみ＋Alt 併用）。title-bar の plain drag は [[IslandMove]]・body は canvas pan（既存不変）。
_Avoid_: つまみを隠して Alt のみにすること（発見性が落ちる・owner は併用を選択）／body を drag handle にすること（pan が壊れる）

**island-scoped reflow** ※2026-06-25 新設・方針 **[[ADR-0029]]**（findings 0106 §4）:
**swap した瞬間だけ**走る局所自動調整。A/B が**サイズ維持で位置（anchor）交換**した後、はみ出し/隙間を解消する向きに**同じ island の隣窓だけ**を best-effort で magnetic flush re-snap（`ぷるん` spring）。**scope は island に厳密限定**（他 island・他 plane に波及しない）＝ADR-0017 free-placement の唯一の明示 carve-out。perfect tiling は非保証で残隙間は許容（owner Q4=空けたまま・Q5=サイズ違いを許容して自動調整）。
_Avoid_: reflow を island の外へ波及させること（global no-reflow 違反）／単窓を抜いた跡の穴を reflow で詰めること（Q4＝空けたまま・swap 時のみ）／perfect tiling を強制すること（best-effort・隙間許容）
パネルは独立 floating window（canvas 論理座標の position+size）になり、slot 順序の正本・swap・`ceil(√n)` 派生 rect は廃止。
くっつきは磁石スナップ。以下は履歴:
**tile** = Hakoniwa の 1 区画（安定 `id` で同定）。**slot** = tile が占める grid スロット番号（row-major・
左→右／上→下）＝ #12 `PanelLayout.slot`（**順序の正本**）。tile の実表示矩形（`LayoutRect`）は n+slot から
**等分グリッドで派生**する snapshot で、自由配置や split 比率の正本ではない。**tile swap** = ヘッダ drag で 2 tile の
slot を入れ替える操作（**swap であって自由配置ではない**・TTWR ADR 0014 parity）。divider resize（列幅/行高の
比率変更・ADR 0015 parity）と box 移動（root の canvas 位置永続化）は #14 **外**＝将来 slice の additive 拡張。
_Avoid_: slot を rect から導く／rect を split 比率や root 位置の正本に流用すること（slot が正本・rect は派生）

**chart tile family / base tile（銘柄別 chart・計画＝受け皿 issue「動的 N チャート」）** ※**一部 SUPERSEDED 2026-06-21**・[[ADR-0017]]（findings 0075）:
**維持**: chart は universe（`InstrumentRegistry`）と常時同期し銘柄 add/remove で spawn/despawn・membership 正本は universe。
この「chart ⊆ universe」不変条件は **復元時**（[[#123]] / findings 0095・`ReseedFromEditor` 末尾の `SyncChartWindowsToUniverse`）と
**永続時**（[[#124]] / findings 0099・`TryWriteLayout` の `PruneOrphanChartWindowsForPersistence`）の**両側で強制**され、layout doc が
chart 集合の第二 SoT になることを防ぐ（defense-in-depth。永続時 prune の oracle は `sidecar ?? inline` ＝ `SeedScenarioFromEditor` 同一解決・unreadable は fail-open）。
**退役**: chart/base は **floating window**（grid tile ではない）。box-grow / `ceil(√n)` grid / slot 並びは廃止し、actuation は
`FloatingWindowController.Spawn/Close`。doc は座標（floatingWindows）を保存。以下は履歴:
**chart tile family** = Hakoniwa の chart を「固定 1 枚（id `chart`）」から **universe 登録銘柄ごとの動的な tile 集合**へ拡張したもの。各 chart tile の id は `chart:<instrument-id>`。**どの chart が存在するか（メンバーシップ）の正本は universe（`InstrumentRegistry`）**で、universe と**常に同期**する（銘柄 add/remove で即 spawn/despawn＝`InstrumentRegistry.Changed`）。layout doc は **並び順（slot）だけ**を保存し、メンバーは universe から導出する（doc にあって universe に無い id は skip、universe にあって doc に無い id は末尾へ＝既存 tolerance を流用・スキーマ追加 0）。**base tile** = 銘柄に紐づかない常駐 tile（`startup` 等）で、chart tile の**前**に並ぶ。box は銘柄数 n から決定的に grow する（TTWR `compute_hakoniwa_box_size` の port・**位置/サイズは persist しない**＝derived）。TTWR の `InstrumentRegistry`→chart tile sync（ADR 0011 Update／#169）の capability parity。base tile を**モード別**にする（Replay=設定込み／Live=設定無し・ADR 0013）と **per-mode profile**（Replay/Live で別レイアウト）、box 位置/サイズの**永続化＋drag-handle 移動/リサイズ**は、それぞれ別の後続 additive slice。
_Avoid_: chart の集合を layout doc 側の正本にすること（正本は universe）／`chart:<id>` を base tile と同じ「固定 id」前提で persist すること（chart はメンバーが動的）／box の derived-grow を box 位置/サイズの永続化（将来 slice）と混ぜること

**mode-conditional base tile / base retile（モード別 base・[[ExecutionMode（実行モード）]] 所有）** ※**縮退 SUPERSEDED 2026-06-21**・[[ADR-0017]]（findings 0075）:
despawn/respawn の base retile は廃止。~~mode 差は **`startup` window を Replay のときだけ Show / Live で Hide** の可視性トグルだけに縮退
（`FloatingWindowController.Show`/`Hide`・dormant 温存）~~ **→ さらに [[ADR-0026]] で `startup` dock window を完全退役（Settings へ集約）＝この show/hide 可視性トグルも消滅。base クラスタは Replay/Live とも 4 窓で mode 差なし**。chart は mode をまたいで identity 保持（spawn したまま）。以下は履歴:
**base tile の集合（種類）を [[ExecutionMode（実行モード）]] が決める**。Replay = `[startup, buying_power, orders, positions, run_result]`（`startup` を index 0）、Live（LiveManual/LiveAuto 共通）= `[buying_power, orders, positions, run_result]`（`startup` 無し）。mode が切り替わって **base の集合が変わったときだけ** base tile を despawn/respawn する＝**base retile**。chart tile（[[chart tile family / base tile（銘柄別 chart・計画＝受け皿 issue「動的 N チャート」）]]）は mode 切替をまたいで **identity を保持**し、新 grid の後半スロットへ再配置される（**所有権分離**: base=ExecutionMode／chart=universe`InstrumentRegistry`）。`HakoniwaController.Order` は常に `[base…, chart…]` の順を保ち、grid は `n_base + n_chart` から再構築する。backcast には TTWR の `ExecutionMode` enum/Resource が無く、mode の正本は footer の [[FooterModeViewModel.DisplayMode]]（poll が overwrite）なので、base の集合判定は `DisplayMode → {Replay, Live}` の 2 値（LiveManual/LiveAuto は同一 Live base）で行う。base 集合の所有は membership orchestrator（`BackcastWorkspaceRoot`）= chart の universe 同期と対の構造。TTWR `hakoniwa_tile_kinds(mode)`／`reconcile_hakoniwa_tiles`（base-only rebuild）・ADR 0013(#169 amendment) の capability parity。
_Avoid_: mode 切替で chart tile を despawn すること（所有権違反・identity を壊す）／base↔chart の cross-swap 後に base/chart 判定を slot 位置で行うこと（判定は **id prefix `chart:`** で・TTWR は component kind で判定）／LiveManual⇄LiveAuto で retile すること（base 集合が同一なので no-op）／単一共有レイアウトと per-mode profile（別 slice）を混ぜること

**per-mode layout profile（モード別レイアウト・[[ExecutionMode（実行モード）]] 別に Hakoniwa tile 並び順を保存）** ※**SUPERSEDED 2026-06-21**・[[ADR-0017]]（findings 0075）で退役。
配置は全 mode で**単一共有**（floating window の flat 共有へ統一）・`HakoniwaLayoutProfiles` と `hakoniwaProfiles` スキーマ read は廃止。
~~mode 差は `startup` の show/hide のみ~~ **→ [[ADR-0026]] で `startup` dock window 退役＝mode 差は無し（base クラスタは Replay/Live とも 4 窓）**。以下は履歴:
**Replay と Live が各々の Hakoniwa tile 並び順を別の profile として覚え**、mode 切替で当該 profile を復元する（TTWR `HakoniwaLayoutProfiles { replay, live }` の capability parity・`from_mode`: Replay→replay／LiveManual・LiveAuto→**同一 live profile**）。per-mode 化の対象は **Hakoniwa の tile 並び順（`_hako.Capture().panels`）だけ**で、infinite-canvas の pan/zoom・floating window・Strategy Editor の開きファイルは **mode 横断で単一共有**（doc 直下に flat 保持。TTWR `restore.rs` も camera/windows は flat 復元）。disk スキーマは `LayoutDocument.hakoniwaProfiles { replay, live }`（nested・**additive**・version bump 無し）を**正本**とし、`panels` は active mode の互換ミラー＆旧 doc の **forward-compat seed** 用（read は常に profiles 優先で drift しない）。mode 切替は TTWR `reconcile_hakoniwa_tiles` 準拠＝**旧 profile に現 layout 退避 → current 切替 → 新 profile を検証 load**。検証（`is_valid_for` parity）= 保存 profile の **非 chart（base）id 集合が当該 mode の `HakoniwaBaseTiles.Kinds` と一致するか**で、一致なら user の base 並び順を honor・不一致/無は canonical `Kinds(mode)` に落とす（[[backcast-layout-default-id-collision]] の #61 衝突安全を strict superset で包含）。chart の並び順はどの場合も honor し membership は universe 再導出（#60 不変）。ロジックは pure class `HakoniwaLayoutProfiles`（UnityEngine-free・AFK 権威）に集約し、`HakoniwaController` が actuation・`BackcastWorkspaceRoot` が membership/box-grow。box 位置/サイズ・cols/rows（divider）の per-mode 化は後続 additive slice（同コンテナ `HakoniwaProfile` へ拡張）。
_Avoid_: per-mode 化対象を tile 順以外（canvas/window/editor）へ広げること（TTWR は flat 共有）／`panels` を per-mode の正本にすること（正本は `hakoniwaProfiles`・panels はミラー/seed）／検証なしで legacy/衝突 doc の base 順を honor すること（集合不一致は canonical へ＝#61 衝突安全）／LiveManual と LiveAuto を別 profile にすること（同一 live profile を共有）

**floating window / FloatingWindowLayer / z-order** ※2026-06-21 **再拡張**・[[ADR-0017]]＋[[ADR-0018]]＋**[[ADR-0019]]**（findings 0075／**0080**）:
infinite canvas の Content 上を **自由配置（free placement）**で漂う window。**ADR-0017 以降、Hakoniwa のパネル
（`chart` / `positions` / `orders` / `run_result` / `buying_power`）も floating window**＝旧「chart は
floating window ではない」は**反転**（chart は multi-instance kind `chart:<id>`・universe 同期）。
※ かつて base に含まれた `startup` は [[ADR-0026]] で dock から退役し Settings へ集約（dock base クラスタは 4 窓）。
**ドッキング = 磁石スナップ＋group 関係（ADR-0019 ＋ ADR-0024）**: drag 中は **in-drag 磁石吸着**（[[R_SNAP]]=96px で flush 位置へ実描画 snap）、release で **flush 隣接判定**（ε=1px ∧ 直交軸 overlap > 0）／overlap なら最寄り flush へ snap して **[[window group / groupId]]** を付与/merge。drag mode は **cursor 位置で動的 3 判定**（[[window group / groupId]] §1）: 同 island メンバー rect 内 → **swap**（ghost 2 枚）／島外 ∧ < [[D_DETACH]] (256px) → **island translate**（島全員が実描画でシフト）／島外 ∧ ≥ 256px → **detach**（A 単独）。**~~core（startup/run_result）含み時の Hakoniwa 昇格・移動禁止・core 不抜は ADR-0024 で退役~~**——全 island が同一挙動。`D_DETACH=256f` 超で detach commit。**ESC** で drag 中 revert（spring 200ms・state 不変）。
**FloatingWindowLayer** = ADR-0018 §10 で「手前 1.2倍プレーン」用に縮退（`strategy_editor` cell ＋ `order` のみ）。元箱庭 6 種は **`DockLayer`（1.0倍・奥・背面 sibling）** に居る。各プレーンに独立 `FloatingWindowController` が居り、snap/group 母集合はプレーンに閉じる（cross-plane snap・cross-plane group とも禁止＝ADR-0018／ADR-0019）。**z-order** =
window の前後関係。live は **各 plane 内の sibling index**（後の sibling ほど前面）、persist は **`zOrder` int**（plane-relative）。**click-to-front** =
window をクリック/drag したとき最前面へ（TTWR `WindowManager.max_z` bump の capability parity・形式非互換）。**move** =
title bar drag で position を移動（screen delta / zoom → canvas 論理 delta・group 所属時は一体移動 or swap）。**[[drag ghost]]** = drag 中の post-release 状態半透明プレビュー。実装は #15＋#99＋#103＋#104（ADR-0019）。
_Avoid_: ~~chart を floating window と呼ぶこと~~（ADR-0017 で反転＝**chart は floating window**）／~~磁石スナップに group 一体移動・detach 状態を持ち込むこと~~（ADR-0019 で反転＝group 関係を `groupId` で持つ）／zOrder を `slot` に相乗りさせること（別 field）／
floating window rect を panel の 0..1 正規化 `LayoutRect` で持つこと（floating は canvas 論理座標の position+size）／
resize/常時最前面 pin を #15 の汎用 window system に含めること（前者は将来 slice・後者は実 editor content 由来の例外）／元箱庭 6 種を `FloatingWindowLayer` に置くこと（ADR-0018 で `DockLayer` 行き）／cross-plane group を許可すること（ADR-0019 で禁止＝restore 時に分割）

**chrome z-order 前面順序（画面固定 chrome のレイヤリング契約）**:
[[infinite canvas]] の外に置く画面固定 chrome（menu bar / その dropdown / sidebar / footer / secret modal）の
**前後関係の契約**。chrome はすべて uGUI（ScreenSpaceOverlay）で描き、順序は **`Canvas.sortingOrder` で決定的に**持つ。
IMGUI（`OnGUI`）の `GUI.depth` は単一カメラ Screen-Space では無視され、IMGUI 同士の描画順は MonoBehaviour 実行順依存で
制御不能（＝#77 の不具合源：menu と sidebar が両方 IMGUI で、後に走る sidebar が dropdown を上塗りした）。ゆえに
chrome は IMGUI を撤去して uGUI 化する。契約の順序は **field/windows < sidebar < footer < menu+dropdown < secret modal**：
footer は sidebar overflow に決して隠されず（#84/findings 0053）、dropdown は footer/sidebar の前面に描かれ、
secret modal は常に最前面。**EventSystem はクリックを最前面 raycaster だけへ配送**するので、この順序が視覚 z-order と
入力到達の両方を一意に決める（dropdown 直下の sidebar への取りこぼしクリックも構造的に消える）。menu 展開中は menu と
footer/sidebar の間に全画面 backdrop（`sortingOrder=599`＝menu−1, footer より前）を一枚敷き、外側クリックで閉じつつ
footer/sidebar への到達を断つ（menu 開いたままモード切替する exotic state が起きない）。同層内 overflow（特に sidebar の
rows / picker list）は **own RectMask2D + ScrollRect** で各 view 自身が物理的に閉じる二重保証（#84/findings 0053）。数値の
`sortingOrder` は findings 0045（+0053 amendment）。[[floating window / FloatingWindowLayer / z-order]]（Content 内の
window 同士の前後）とは別レイヤ——あちらは pan/zoom 追従、こちらは画面固定。
_Avoid_: chrome の前後を IMGUI の `GUI.depth` や MonoBehaviour 実行順で持つこと（単一カメラでは無効・#77）／secret modal
より前面に menu を置くこと（modal は常に最前面）／footer をメイン Canvas（sortingOrder=0）同居に置くこと（sidebar overflow に
隠される＝#84 の再発）／menu backdrop より前面に footer を置くこと（外側クリック→menu 閉じる semantic を壊す）／
Content 内の window z-order と画面固定 chrome の layering を同一視すること

**Strategy Editor（code buffer）**:
floating window kind `strategy_editor` の**実 content**。strategy `.py` を編集する code buffer（Python の lexical
syntax highlight / undo-redo）。#15 は generic な window frame（spawn/move/z-order/persist）だけ立て、実 content を
deferred した — その content がこれ（#16）。編集対象は**実在する `.py`**（新規作成・ピッカーは射程外）で、編集は
buffer 上で行い save でディスクへ書き戻す。highlight は**意味解析ではなく lexical**（builtin を固定リストで着色しない＝
shadow 可能なので構文ではない）。LSP / autocomplete / Python parser / 実行検証は射程外。実装は #16。
_Avoid_: chart/order と混同すること（別 kind）／#15 の汎用 floating window system に content を混ぜること（content は
caller の window factory が `kind=="strategy_editor"` のとき合成・controller 境界は不変）／「Python parser を載せた」と
表現すること（lexical tokenizer であって parser ではない）
（方向: ADR-0012 — target authored モデルは marimo cell-DAG（"Strategy Editor = cell"）に再定義済み。本 code buffer は
移行期の暫定表面として共存し、実 UI 置換は #15/#16。本エントリの定義は移行期の現実を記述したもの）

**cell window / marimo notebook（cell-DAG authoring 表面）**:
marimo **3D モード**移植（#81・ADR-0013・findings 0050）。**1 セル = canvas 上の 1 floating window**
（`strategy_editor:region_NNN`）で、窓に映るのは**セル本体だけ**（`@app.cell` / `def _(refs)` / `return defs`
は画面に出さない＝marimo の `cellData.code` のみ表示・ラッパは codegen 形式）。N 個のセル窓は**ノート全体で 1 つの
`.py`** を成し、本体↔`.py` の合成/分解と DAG 解析は **Python(marimo) 純正**（`generate_filecontents` /
`load_app`）が担う——C# は空間 UI（窓・位置・矢印）だけを持ち def/ref 解析を再実装しない（[[ttwr-parity-first]]）。
依存の **reactive 解決は marimo 側で閉じている**ので、窓間の依存矢印は refs/defs の**可視化**にすぎず実行には効かない。
セルの追加 [+]・削除・drag/z-order・位置永続化を持つ（**notebook は常に ≥1 セル**＝最後の 1 個は削除不可・marimo parity）。
_Avoid_: 窓ごとに別 `.py` ファイルを持つと解釈すること（ノート = 1 `.py`・[[strategy file provider（供給 seam）]] 参照）／
`def _(refs)` / `return defs` を画面に出すこと（本体のみ）／合成/分解を C# に再実装すること（marimo 純正に委譲）／
セル identity を窓 GameObject に 1:1 固定すること（物理窓 region_001 は never-Destroy・論理セルは hide-not-destroy で
dormant 化・ADR-0013）／依存矢印を実行に必要と見なすこと（純粋可視化・Slice 2）

**strategy file provider（供給 seam）**:
編集・保存済みの strategy `.py` の**パス**を Replay/Live に `strategy_file` として渡す durable な境界（#16）。engine は
パス（≠ ソース文字列）を消費し `_load_strategy` がディスクから開くため、供給するのはソースではなく**保存済みパス**。
「**供給可能**」= path バインド済み ∧ not dirty ∧ 直近 Open/Save 成功 ∧ canonical absolute `.py` ∧ 呼出時点で実在、の
すべてを満たすときに限る（dirty 時は stale パスを返さず拒否＝「provider が返すパス = ディスク内容が buffer と一致」を保証）。
**active/current/default strategy の選択も run lifecycle も持たない**（run-UI / 別 slice の責務）。
cell-DAG モデル（#81・ADR-0013）では provider = **ノート集約（`MarimoNotebookDocument`）**：N 個の [[cell window / marimo notebook（cell-DAG authoring 表面）]] の本体を順に `generate_filecontents` で 1 `.py` に合成・保存し、その 1 パスを供給する。
「供給可能」判定と dirty（本体編集／窓 add・delete・reorder で dirty）は**集約が持つ**（個々の窓は本体断片で path を持たない）。
**非 marimo `.py` の Open は 1-cell wrap（#86・findings 0054）**: File→Open が選んだ `.py` が marimo notebook でない（`load_app` が None・broken / imperative 戦略など）場合、集約は abort せず**生本文をそのまま 1 cell（name=`_`・config=既定）**に詰めて bound する。**ただし wrap は `IsDirty == false` のときに限る**（#86 F1）：dirty な集約に対して非 marimo `.py` を Open すると未保存ワークを暗黙上書きすることになるので、wrap も fail-soft で拒否し既存セルを保つ（valid marimo `.py` は dirty でも置換可＝user の明示的「別 notebook に切替」意図）。Open 直後（clean 経路）は dirty=false で Run 解禁（on-disk は元の `.py` のままなので imperative 経路で走る）。一旦 Save すると `generate_filecontents` が marimo 形式に書き換え＝**一方向のマイグレーション補助**（destructive 変換は owner 公認）。fail-soft の LastError は **path/IO エラー** + **dirty workspace 拒否**（#86 F1）の 2 系統。**#87 slice 3（findings 0069）**: この集約 F1 はそのまま（slice 2 で `discardDirty` 認可緩和を追加）。上位 UX は **File→New / File→Open を SaveGuard（Save/Discard/Cancel）で包む**——dirty な document への New/Open は即実行せず確認を出し、`Discard` 判定時のみ `Open(discardDirty:true)` で F1 を緩めて置換する。これにより **valid marimo `.py` への「dirty でも黙って切替」挙動は廃止**（owner-veto supersession）＝marimo/非marimo の別なく dirty なら一様に確認する。Cancel は editor/universe/path を据え置き、Save は既存 path なら Save・untitled なら Save As へ流す（終了確認 #89 と同じ SaveGuard seam を共有）。
_Avoid_: **adapter と呼ぶこと**（adapter は engine/pythonnet 境界専用の予約語）／run trigger・`start_nautilus_replay`
呼出・active 選択を #16 / provider に含めること／ソース文字列を供給すると解釈すること（パスが正）／cell window ごとに別
provider/別 `.py` を持つと解釈すること（ノート集約が 1 .py の単一 provider・窓は本体断片）／非 marimo `.py` の Open を「fail-soft で abort」と解釈すること（#86 で 1-cell wrap に緩和済）

**notebook = backtest 一本化 / `bt` ハンドル / 土台 vs backtest 駆動 cell**:
**ADR-0016**（#95 Phase 1）で確定。strategy [[cell window / marimo notebook（cell-DAG authoring 表面）]] の cell は **`bt.replay()` / `bt.step()` を呼ぶか呼ばないかで意味論が割れる**（**`bt` を free ref として import するだけでは何も走らない**）:
- **`bt.replay()` / `bt.step()` を呼ばない cell（=「土台」層・engine 非接続）**: marimo native の reactive 再計算（`thin_drain.py:68` が既に import 済みの marimo `Runner` を押した cell を root に in-proc 駆動する net-new 配線＝`HeadlessKernel` 単体の流用では届かない・findings 0070 F1）で per-cell RUN 押下時に押した cell＋下流が DAG 順で再評価される。純粋計算。副作用なし。`bt.bar()` / `bt.portfolio()` を読むことは可能（active bt state の参照は runner を進めない）。
- **`bt.replay()` / `bt.step()` を呼ぶ cell（=backtest 駆動）**: 実 backtest を回す。`bt.replay()` で全 bar / `bt.step()` で 1 bar。`submit_market → ctx.submit_market → OrderEngine → ReplayBroker` が走り bar close で実約定・portfolio 更新・Hakoniwa に実数値が出る。
**駆動と参照の区別**を厳密に保つ: **駆動 operation = `bt.replay()` / `bt.step()` の呼び出し**（KernelRunner の bar pointer を進める唯一の seam）／**active bt state の参照 = `bt.bar()` / `bt.portfolio()`**（現在の bt 状態を読むだけ・runner を進めない・呼び出し前は initial / 終端以降は最後 bar の値）／**`bt.submit_market(qty)`** は `bt.replay()` / `bt.step()` の per-bar context 内**でのみ有効**（context 外発注は Phase 3 で fail-closed）。
**「dry-run preview」のような別系統は作らない**（旧 #95 設計の `submit_market=no-op` 経路は ADR-0016 D1 で却下＝telos「notebook = backtest 一本化」）。「軽く動かす」は `bt.replay()` / `bt.step()` を呼ばない cell が担う。境界は cell の駆動 operation 呼び出しの有無で構造的に決まり、flag や mode を持たない。
**`bt` ハンドル**: host が **commit された startup-panel config 単位で 1 個だけ**生成し、free ref として cell globals に注入（既存 `get_bar` 注入と同型 seam）。**異なる scenario の re-commit で破棄＋作り直し**（同 scenario の re-commit は cache hit reuse＝step の pointer は維持・Phase 5 #98 実装着地・findings 0074 §P5-1）。API は `bt.replay(bars_per_second=N)` / `bt.step()` / `bt.bar()` / `bt.portfolio()` / `bt.submit_market(qty)` の 5 つ（signed delta qty・`cell_api.make_submit_market` 流用）。`bt.replay()` と `bt.step()` は **同一 KernelRunner state machine と同一 bar pointer** を共有する: replay は呼ばれるたび pointer を 0 に reset して end まで走り、step は現在 pointer を 1 進める（終端で `None`）。**step cell の再実行は意図的に stateful**（各実行で 1 bar 進む・marimo の reactive idempotent 前提と意識的に違う＝B3 は「ステッパー」で cell を「ボタン」のように使う）。replay 実行中の重複 RUN は Phase 4/6 の running guard でブロック。ユーザーモデルは 3 行で説明できる: ①**config commit が `bt` を新規作成** ②**replay は常に 0 から** ③**step は現セッションの pointer を進める**。内部は `KernelRunner` 再実装ゼロ＝per-bar 順序（`push_bar → on_bar → fill@close → push_order/push_portfolio → on_equity`）を wrap するだけで [[golden 契約（Backcast vs Nautilus oracle）]] byte-identical を保つ（ADR-0006 不変）。実装は #95 Phase 3。`bt` の module 自体は **marimo-free**（lazy-import 規律＝seam は marimo を引かず実行時のみ marimo 経路へ降りる・findings 0046 の S6 既定）。記録: ADR-0016・findings 0070。
**実装着地（#95 Phase 3・findings 0072）**: per-bar ループを `engine.kernel.stepper.KernelStepper`（3 primitive `open_next_bar`/`close_current_bar`/`finalize`＋`StepEvent`/`StepHandle`）に extract し、`KernelRunner.run()` は「全 bar load → stepper を end まで駆動 → result」の薄い wrapper 化（`RunResult`/`_Context` も stepper へ移設・runner は re-export）。`bt` は `engine.strategy_runtime.backtester.Backtester`（5 API ＋ Phase 2 hook `_close_open_bar`・`Backtester.from_scenario(scenario_dict)` 入口）。`bt.replay()` と `bt.step()` は**同一 stepper の単一 forward-only pointer**を共有し（同じ primitive を順番違いで叩く＝byte-identical）、**Phase 3 では「1 `bt` = 0→end の単一 run」**（replay XOR step-to-end でその 1 run を駆動・終端 `None`）。上の「replay は呼ばれるたび 0 に reset / 再走」は **`bt` ライフサイクル（config 変更で reset・再走）= Phase 5** の領分で、Phase 3 は再走するなら **`bt` を作り直す**（Q6 teardown α: `stop_event.set()` → 参照 drop → 新 `bt`）。**Phase 3 で実装済**: parity 3 系（既存 #24 golden byte-identical / `Backtester` replay parity / step-to-end parity）・`stop_event` seam（pre-set で bar を流さず `STOPPED`）・`submit_market` context-out fail-closed（`ValueError`）・`bt.bar()`/`bt.portfolio()` の 4 状態 lifecycle・`bars_per_second` 引数受理（**Phase 3 は no-op**＝pacing sleep は Phase 4）。signed-delta→side 変換は `engine.kernel.orders.signed_qty_to_side`（`cell_api.make_submit_market` と共有）。`KernelStepper` は単一スレッド前提（running guard / worker-thread driver は Phase 4・Q5 C1/D1）。
_Avoid_: `bt.replay()` / `bt.step()` を呼ばない cell を「dry-run」「preview」と呼ぶこと（純粋計算で別 mode ではない・ADR-0016 D1）／`bt` を free ref として参照しただけで runner を進めると解釈すること（**駆動 operation の呼び出し（`bt.replay()` / `bt.step()`）が唯一の seam**・`bt.bar()` / `bt.portfolio()` は read-only state の参照）／`bt.submit_market` を `bt.replay()` / `bt.step()` の context 外で呼べると期待すること（Phase 3 で fail-closed）／`bt.replay()` を「途中から再開」と解釈すること（**常に 0 から**・B2 の visual playback 直感を壊さない）／`bt.step()` を idempotent と期待すること（**1 実行＝1 bar 進む** stateful 操作）／`bt.replay()` と `bt.step()` を独立 instance と解釈すること（同一 `bt` / 同一 bar pointer 共有）／cell に config（universe/start/end/cash/granularity）を書くこと（[[Replay 実行設定（scenario startup）]]=`ScenarioStartupTile` が所有・[[scenario（実行設定）/ scenario sidecar]] へ persist）／cell に表示ロジックを書くこと（[[Hakoniwa（split-grid surface）]] が orders/positions/buying_power/run_result を所有）／`bt` を 1 process あたり複数同時走らせること（running guard でブロック）／`bt` API に live mid-loop な速度変更 setter を生やすこと（速度は `bt.replay(bars_per_second=N)` の開始時キャプチャのみ・走行中変更は不可・[[per-cell RUN / replay 速度（visual pacing・call-time 引数）]] 参照）

**mode-aware `bt` / live cell run（Replay↔Auto シームレス）**:
**ADR-0025**（#112）で確定。同一 marimo 戦略 cell（`bt.replay()` で書かれたもの）が **Replay と Auto(LiveAuto) の両方で駆動**できる。`bt` は **(BarSource, ExecutionSeam) のペアを隠す façade**: **Replay** = historical iterator ＋ `KernelStepper`（bar-close 約定 sim）／**Auto** = venue の**確定 bar を流すキュー** ＋ 既存 `KernelLiveDriver.ctx`（intent queue → SafetyRails → broker → venue・余力 provider・portfolio）。cell は**一文字も変えない**＝モード分岐を持たない（nautilus DNA「戦略は backtest/live で分岐せず engine が data/exec を供給」の踏襲）。**実行入口は per-cell RUN ただ一つ**で、ExecutionMode を見て Replay bt / Live bt を分岐（[[per-cell RUN / replay 速度（visual pacing・call-time 引数）]] が mode-aware 化）。**footer ▶（LiveAuto 起動）は退役**（mode セグメントは存続）＝live run は「marimo cell を run する」と同一で、システムは live run へ誘導しない（mode はユーザーが明示的に持つ）。走行中は ▶→■ で stop=teardown。**live cell run の寿命 = ■ stop または venue 切断**（自動終了なし・場引け後は idle＝live の `bt.replay()` は「キュー空 ≠ StopIteration」で番兵を引いた時だけ抜ける）。`bt.submit_market(qty)` は両モードで同一シグネチャ（instrument 引数なし）＝**現在 drive 中（=open）bar の instrument** へ発注。`bt.portfolio()`/`bt.buying_power()` は Auto では venue 権威（余力 provider・venue snapshot）。granularity は両モードで scenario 由来（[[scenario（実行設定）/ scenario sidecar]] の `granularity` → live bar 間隔・ADR-0025 D6）。editor で開くのは marimo であり **run/materialize は marimo 強制**（非marimo は `NOT_A_MARIMO_NOTEBOOK` エラー）。記録: ADR-0025・findings 0092。
_Avoid_: 同一 cell の Auto 駆動に reactive 書き直し（`get_bar()`/`MarimoStrategy`/thin_drain）を要求すること（**cell 無改変が telos**・休眠枝の蘇生＋退行＝ADR-0025 D1 で却下）／live 起動を footer ▶ や別ボタンに置くこと（**per-cell RUN 一本**・footer ▶ 退役）／live `bt.replay()` を「データ枯渇で終わる」と解釈すること（**■/切断のみ**・空キューは idle）／Auto の `buying_power` を kernel cash と解釈すること（venue 余力 provider 権威）／granularity を live で 1 分固定と解釈すること（scenario 由来・ADR-0025 D6 で配線）。

**per-cell RUN / replay 速度（visual pacing・call-time 引数）**:
**ADR-0016 D2/D8** で確定した実行入口と pacing seam。
**per-cell RUN**: すべての [[cell window / marimo notebook（cell-DAG authoring 表面）]]（adopted `region_001` ＋ spawned `region_002+`）が個別の RUN ボタンを持つ（ADR-0013 が立てた `StrategyEditorWindowFrame` の idempotent find-or-create・X ボタンと同型）。クリックで押した cell＋reactive 下流が DAG 順で再実行され、`bt` 使用 cell は実 backtest を駆動する（[[notebook = backtest 一本化 / `bt` ハンドル / 土台 vs backtest 駆動 cell]] 参照）。**専用の batch RUN ボタン（#76/#81 merge U1 の global ▶ Run）・title-bar Run（現行 `Assets/Scripts/StrategyEditor/StrategyEditorRunButton.cs`・ADR-0005 由来の 1:1 TTWR parity 単一 Run ボタン surface の現実装で、ADR-0012 partial supersede が strategy-authoring 表面の文脈で明示退役しなかった residual）・global transport（#30 既退役）は ADR-0016 で formal supersede**。**ADR-0005 由来の「アプリ全体で 1 つの再生ボタン」facet（ADR-0012 が strategy-authoring 表面を部分 supersede した時点で reactive cell-DAG モデルとの整合を将来 slice に委ねていた residual）は ADR-0016 で却下**（facet-scoped supersede・ADR-0005 / ADR-0012 本文は無改変＝両者の自己保護条項）。
**命令型 `.py` の UI 実行経路も同時に formal sunset**（ADR-0016 D4）: global ▶ Run 退役と同時に UI batch 実行手段が消える。**File→Open の 1-cell wrap**（findings 0054）は migration / editing affordance として残し、命令型 `.py` を開いて編集→Save すると `generate_filecontents` で marimo canonical 形に書き換わる（一方向マイグレーション補助）。`Strategy` クラス・`strategy_loader.load`・`KernelRunner` boundary は **pytest / [[golden 契約（Backcast vs Nautilus oracle）]] / programmatic oracle 用に存続**だが UI fallback Run は残さない。
**replay 速度**: 旧 `_REPLAY_BAR_INTERVAL_SEC = 0.01`（`_backend_impl.py:189` の hardcoded constant・#76 S6b-β で footer transport (#30) と共に固定された値）を**撤去**し（Phase 4）、速度は **`bt.replay(bars_per_second=N)` の call-time 引数**で与える＝**`replay()` 開始時にキャプチャする値**（**live-mutable な速度レジスタにはしない**・走行中に変える手段が無く RUN は順番待ちなので不要・findings 0070 F6）。**単位は名前に込める**: `bars_per_second` ＝「1 秒あたり N bar 描画・大きいほど速い」で per-bar sleep は概ね `1 / N` 秒（bare な `speed=` は無次元倍率に誤読されるため public API に出さない）。**`bars_per_second` 未指定なら明示 sleep ゼロで worker 全力**（GIL ハンドオフは CPython auto-switch ~5ms に任せ、「Hakoniwa の都合でエンジン速度を縛る per-bar 強制 sleep の床」は入れない＝owner 優先順位「最悪なのは Hakoniwa が速度を拘束すること／見た目の遅れは許容」・`gil_handoff_spike.py` PASS）。**`bars_per_second` 指定時のみ** 視覚機能として per-bar pacing sleep を挿入しエンジンを意図的に遅くする。**user-facing API は `bt.replay(bars_per_second=N)` の引数のみ**で **UI コントロール（slider / pacing button）は作らない**（#30/#68 footer transport の退役判断を維持・findings 0046）。`bt.step()` には速度は無意味で `bars_per_second` 引数を持たない（ユーザー自身が pace する＝ボタンを押すごとに 1 bar）。**cross-thread レジスタは stop（`_replay_stop_event`・`core.py:43`）だけ**。退役した transport を意図的に復活させる理由（B2 の visual playback ニーズが「reactive drain は瞬時に終わる」前提を変えた）は findings 0070 に明示記録（silent な復活でない）。
**実装着地（#95 Phase 4・findings 0073）**: pacing は `KernelStepper.set_pacing(bars_per_second)` を `Backtester.replay()` 開始時に呼んでキャプチャ（`_bar_interval_sec = 1/N`・未指定→0＝全力）。`_REPLAY_BAR_INTERVAL_SEC` は撤去し imperative path も `bar_interval_sec=0`（全力）— **GIL auto-switch ハンドオフは実機 Unity AFK `ReplayToHakoniwaE2ERunner` で reconfirm GREEN**（sleep 撤去 lock 確定・findings 0073 §P4-5）。bt 駆動は host-owned wrap: `_backend_impl.run_cell(source, index, scenario_json)` が `bt.replay`/`bt.step` を含む cell でだけ `_build_notebook_bt`（`load_replay_data` ＋ `ReplayKernelObserver`＝#65 経路 ＋ `stop_event`）で `bt` を構築・marimo globals へ注入（`NotebookSession.run_pressed(inject=)`）。`Backtester` は thin のまま host hook（`on_run_begin`＝駆動初回に engine LOADED→RUNNING / `was_driven`・`result`＝host が finalize / `arm`・`disarm`＝cold-run graph build 中は駆動させない landmine guard）。走行中は observer→`engine.last_portfolio`→poll lane→Hakoniwa 逐次更新、終端で RunBuffer finalize→summary（`SetReplayRunSummary`→run_result tile）。**stop は走行中 cell の ▶→■ トグル**（owner HITL・`force_stop_replay`→`_replay_stop_event`→stepper STOPPED）、**running guard** は in-flight 中の第二 RUN を RunId 相関で reject（ADR-0016 D3）。production parity gate（`test_backtester_phase4.py`）が `bt.replay()` を `ReplayKernelObserver` 経由で imperative `KernelRunner` と byte-identical に pin（実装中に `signed_qty_to_side` の float-qty leak＝fill record `"100.0"` vs `"100"` を検出・int 型保存で修正）。e2e は `test_notebook_replay_afk.py`（run_cell→実 backtest・pacing 実測差・cross-thread stop）＋ AFK `StrategyEditorNotebookE2ERunner` STRATEGY-21/22/23（scenario hand-off・guard・▶↔■）。
**実装着地（#95 Phase 5 #98・findings 0074）**: `bt.step()` の press 跨ぎ persistence と reset/idempotency を確定。`bt.replay()` は Phase 4 「毎 press fresh bt」で **「呼ぶたび 0 reset 再走」（ADR D3）** を観察等価で履行、`bt.step()` は **DataEngineBackend が step 専用 bt cache** を持ち `_acquire_step_bt(scenario_json)` で「same scenario commit→cache hit reuse／diff scenario commit→旧 bt teardown+新 build」を制御（cache 持続中は pointer が press 毎に +1）。**terminal 到達した press** で `bt.result is not None` を検知し finalize→`force_stop_replay()`→cache 破棄＝次 press は同 scenario でも新 bt（pointer 再 0）。**scenario 未 commit で `bt.*` を呼ぶ cell** には `NoScenarioBacktester` placeholder（`step/replay/bar/portfolio/submit_market` が guidance `RuntimeError("commit the startup panel first...")` を raise・`_close_open_bar`/`arm`/`disarm` は no-op）を inject＝cell output に guidance text が出る（`NameError` でも silent no-op でもない・Phase 3 `submit_market` context-out fail-closed と同型・ADR-0016 D1）。**C# 側 running guard / ▶→■ は `bt.step` 単独 source では発火しない**（step は instant ＋ 意図的 stateful＝guard を立てると次 press が拒否されて意図に反する。`NotebookRunController.RunCell` の `drivesReplay` 判定で細分化）。allowed footgun（上流 cell の press→reactive 下流 step cell も走り pointer が進む）は findings 0070 F3 owner 決定「carve-out しない」を Phase 2 reactive cascade と同じ機構で構造的に保証（特別な除外なし）。e2e は `test_notebook_step_afk.py`（cache 持続・scenario reset・terminal finalize・pre-commit guidance・source pivot teardown）＋ AFK `StrategyEditorNotebookE2ERunner` Section15 STRATEGY-24/25/26（step は guard 非活性・連打受理・scenario unset で guidance text）。
**実装着地（#95 Phase 6 sunset・findings 0075 §P4/§3c）**: title-bar Run / global ▶ Run の formal sunset を実コードで履行＝`Assets/Scripts/StrategyEditor/StrategyEditorRunButton.cs` 撤去・`BackcastWorkspaceRoot.OnRun()` 撤去・`OnRun` 専有 caller だった `ScenarioStartupController.TryStartRun` の run-trigger 経路撤去・`RunReadinessViewModel` 退役（NoStrategy/InvalidScenario/NotOwner wording 定数も最後の consumer 消失で除去）。`TryStartRun` の Commit 自体は startup panel / Save As 経路で存続（per-cell bt は live in-memory `_scenario` 駆動で disk commit にも OnRun にも非依存）。`RunButtonE2ERunner`(#63) は retire（runner 削除済）・`AuthorToRunJourneyE2ERunner`(#66) は per-cell RUN へ migrate。RUN-01..08 の readiness/trigger 契約は per-cell RUN section（S13/14/16/17）＋ pytest へ移送。**残課題（follow-up）: cutover 負 invariant U4（footer に transport ボタン無し）/ U5（startup window に Run ボタン無し）の re-home は未了**＝U4→`FooterModeE2ERunner`・U5→`ScenarioStartupE2ERunner`（#99 で startup は floating window 化＝`_windows.RectOf("startup")` で引く）への assertion 移送が要・他 runner にカバー無し（findings 0075 §3c）。block popup（常時 block ラベル撤去→RUN click 時のみ `_menuBarView.ShowMessage` を running guard reject で出す）も Phase 6 で着地（AFK S17・STRATEGY-29）。
_Avoid_: per-cell RUN を「`bt` 使用 cell に限る」と解釈すること（**全 cell 窓に在る**・`bt` 非使用 cell は純粋計算の reactive 再評価）／global ▶ Run / title-bar Run / footer transport を「将来復活」前提で温存すること（ADR-0016 で formal supersede・復活は新規 ADR が要る）／命令型 `.py` の UI 実行を「sidecar 必須で開けない」と解釈すること（1-cell wrap で **Open できる**・実行入口だけが per-cell RUN に一本化）／replay 速度を UI slider に bind すること（API は `bt.replay(bars_per_second=N)` 引数のみ・UI ボタンは作らない）／速度を live-mutable レジスタにすること（**開始時キャプチャ値**・走行中変更手段は無い・cross-thread レジスタは stop だけ・findings 0070 F6）／速度未指定時に per-bar 強制 sleep の GIL 床を入れること（Hakoniwa の都合でエンジン速度を縛る＝owner 優先順位に反する・auto-switch 依存が正）／`_REPLAY_BAR_INTERVAL_SEC` を constant のまま新規参照を増やすこと（Phase 4 で撤去・`bt.replay(bars_per_second=N)` 経路へ）

**per-cell stale / edit-stale / restage（marimo 本来の実行モデル・#95 Phase 6）**:
[[cell window / marimo notebook（cell-DAG authoring 表面）]] の各 cell 窓が **idle（green ▶）/ running（red ■）/ stale（amber ▶）** の 3 状態を持つ。**stale = 「要再実行」**（marimo 本来の実行モデルそのもの・単なる「編集済み」印ではない・findings 0075 P6-1）。stale が立つ契機は 2 つ:
- **press 残 stale**: per-cell RUN を押すと押した cell＋reactive 下流が走り、走った cell は stale から落ちるが、まだ走っていない stale cell は amber のまま（`run_cell` 戻りの `stale` 配列＝press 後の残 stale）。
- **edit-stale（編集時 stale）**: cell 本体を編集して **`onEndEdit`/blur**（input は `MultiLineNewline` ゆえ Enter は改行挿入・blur でのみ発火＝debounce timer 不要）すると、**run せず** stale 集合だけ返す軽量 RPC `notebook_restage(source)` を呼び、編集 cell＋下流窓を amber にする。
**index→窓 map**: Python は `c{i}` cell-id（positional・編集 cell の index `i`）で stale を返し、C# は `ApplyResult` が同じ index→region map で該当窓へ route（`SetRunButtonStale` で amber tint）。**positional cell-id は insert/delete で shift** するので該当窓が re-stale し得る（記録済み許容劣化・完全な region-id 安定化は将来 enhancement）。
**worker-thread 規律**: `IncrementalNotebookSession` は marimo Kernel を rebuild せず差分更新（`_maybe_register_cell` + `set_stale` 下流伝播）し、**最初に駆動した lane worker thread に thread-local 固定**される。ゆえに `notebook_restage` は run_cell と**同一 `NotebookRunLane` worker thread 経由**で route 必須（Unity main の `onEndEdit` から host 直呼びすると second-thread 駆動で拒否される）。AFK synchronous mode では run_cell 同様 inline 実行。実装着地: `python/engine/notebook_session.py`（incremental）／`_backend_impl.notebook_restage`（3層 RPC）／`INotebookCellExecutor.Restage`＋lane restage work-item／`StrategyEditorWindowFrame.SetRunButtonStale`（3状態 glyph）。e2e: pytest `test_notebook_stale.py`＋AFK `StrategyEditorNotebookE2ERunner` STRATEGY-27/28（S16）。記録: findings 0075。
_Avoid_: stale を「軽い編集済み印」と解釈すること（marimo の実行モデル＝要再実行・依存下流へ伝播）／`notebook_restage` を Unity main thread から host 直呼びすること（worker-thread thread-local 違反で拒否・lane 経由が正）／running bool に stale を相乗りさせること（running ■ と stale amber は別 signal・実運用で排他）

**rich output mimetype 契約（`{mimetype, data}`・#95 Phase 6）**:
per-cell RUN の出力を**文字列1本から `{mimetype, data}`** へ拡張し、C# が mimetype ごとに描画を分ける（findings 0075 P6-2）。Python は marimo `try_format(obj)` の `FormattedOutput(mimetype, data)` をそのまま運び、`run_cell` 戻り `ran[]` の各要素が `{index, ok, mimetype, data, output}`。C# renderer 振り分け（`StrategyEditorView.SetOutput(output, mimetype, data)`）:
- `text/plain` → verbatim `Text`
- `text/markdown` → rich-text subset（`<b>` 等・見出し/太字へ変換）
- `text/html`（`<table>`）→ pipe-row 整形（**一般 HTML は table に絞る**・危険に広げない）
- `image/png` / `image/jpeg` → base64 を `Texture2D.LoadImage`→**兄弟 `RawImage`**（matplotlib もここ・self-contained data）
- 未対応 mimetype → `[mimetype]` ラベル付き plain fallback（デバッグ可視）

**matplotlib は Phase 6 で同梱**（出力は image/png＝既存画像経路に乗る）。**altair/vl-convert は見送り**（Unity に Vega 対話 renderer 無し・PNG 化は matplotlib と価値重複＝将来 additive ADR）。**image decode の AFK 制約**: `Texture2D.LoadImage` は headless batch（graphics device 不在）で PNG を decode/upload できない＝RawImage active は **HITL 降格**（S3 save-fail / S8 glyph-count と同型）。AFK は mimetype が end-to-end 伝播すること（`[image/png]` ラベルで routing 非 collapse を証明）までを pin。実装着地: `StrategyEditorView.TryDecodeImage`／`StrategyEditorContentBuilder`（RawImage 兄弟配線）／`_format_output`（Python）。e2e: pytest `test_notebook_rich_output.py`＋AFK STRATEGY-32/33（S19）。記録: findings 0075。
_Avoid_: 一般 HTML を Unity で完全再現すること（table に絞る・別プロジェクト・P6-2）／altair/vega を Phase 6 で積むこと（見送り・将来 ADR）／headless batch で RawImage active を AFK 必須にすること（GPU decode 不在＝HITL 降格・mimetype 伝播のみ AFK）

**console（stdout/stderr）契約 + 動的出力レイアウト（#102 / findings 0079）**:
[[rich output mimetype 契約（`{mimetype, data}`・#95 Phase 6）]] が rich を担当する一方、cell が `print('a')` で吐く **stdout/stderr は別 channel（console）** として運ぶ。rich output と並走で per-cell に独立。Python の `_BridgedConsoleHook` が `HeadlessKernel` の `_SilentStdout`/`_SilentStderr` の `_write_with_mimetype` をフックして `host.stream.cell_id`（marimo `redirect_streams` が `_install_execution_context` で pin する値）で per-cell に分岐し、**arrival 順のセグメント列** `[{stream:"stdout"|"stderr", text}, ...]` を返す（**隣接 same-stream は collapse**＝marimo `cell.ts:133`/`collapseConsoleOutputs.tsx` parity）。`run_cell` 戻り `ran[i]` に `"console"` フィールド追加。C# は `StrategyEditorView.SetConsole(segments)` が `<color=#ffa01c>` で stderr セグメントを wrap（marimo `Outputs.css .stderr` 相当）し、stdout は default 色＝1 つの Text に rich-text タグで色分け。**body レイアウトは動的**: `StrategyEditorContentBuilder` が body に `VerticalLayoutGroup` を載せ、編集 input（min=80px / `body * 0.30`・`flexibleHeight=1`）+ rich 出力ブロック+ console 出力ブロックの 3 段構成。各出力ブロックは `LayoutElement.preferredHeight = min(natural, body * 0.45)` で auto-sizing、空のときは `gameObject.SetActive(false)` で **LayoutGroup から消える**＝editor が body 全高を占有。overflow は `RectMask2D` でクリップ＝window 自体は伸びない（layout 永続化を破らない）。cell rebind 時は rich+console 両方クリア。実装着地: Python `notebook_session._BridgedConsoleHook` / `_ConsoleCapture`（`_backend_impl.run_cell` が `console` を passthrough）／C# `StrategyEditorContentBuilder.BuildOutputBlock` / `StrategyEditorView.SetConsole` / `BuildConsoleRichText` / `ApplyRichBlockSize` / `ApplyConsoleBlockSize`。e2e: pytest `test_notebook_console.py`（7 ケース：plain/multi accumulate/stderr/mixed interleave/descendant attribution/press-clear/rich+console 共存）＋AFK STRATEGY-34..38（S20）。
_Avoid_: stdout と stderr を別フィールドに分けること（`stdout: str / stderr: str` だと `print()` と `print(file=sys.stderr)` 交互発火で arrival 順が失われ marimo らしさを失う・findings 0079 D1）／console を rich の `text/plain` に混ぜること（責務分離が壊れる・cell 出力に副作用を混ぜる）／空の出力ブロックを anchor 予約のまま残すこと（編集領域から固定で削られる＝#102 L2 で fix した旧 `OutputFrac=0.26` の轍）／window 自体を伸ばして overflow を回避すること（layout 永続化を破る・必ず block 内クリップ or スクロール）

**document-identity badge（#90・メニューバー・#95 Phase 6）**:
notebook/document 単位の identity（`Untitled` / `strategy.py`、dirty 時 `* strategy.py`）を**メニューバーの専用 badge** に出す（findings 0075 P6-5・#90）。source = [[cell window / marimo notebook（cell-DAG authoring 表面）]] の集約 `MarimoNotebookDocument`（`CurrentPath`/`IsBound`/`IsDirty`）。表示: bound→basename 常時可視・unbound（File→New `ResetUnboundEmpty`）→`Untitled`・dirty→`* ` prefix・Save で dirty クリア→`* ` 消える。**責務分離**: document identity は menu badge ／ **各 cell 窓は execution state（idle/running/stale）**（[[per-cell stale / edit-stale / restage（marimo 本来の実行モデル・#95 Phase 6）]]）／ block popup は RUN click 時のみ。document-identity badge は **venue/mode/message badge とは `MenuBarView` 内の別レーン**（`_docBadge`・左寄せ）＝#90 AC4「Run-disabled reason と非矛盾」を構造的に満たす。**ADR-0013「1 cell = 1 floating window」ではまとまった notebook タイトルバーが無い**ので、identity をどれか 1 つの cell 窓に背負わせるとモデルが歪む（削除で dormant 化し得る）＝menu badge に寄せるのが正。実装着地: `BackcastWorkspaceRoot.DocumentBadgeText`／`MenuBarView._docBadge`。e2e: AFK STRATEGY-30/31（S18）。これにより #90 を満たし close。記録: findings 0075。
_Avoid_: document identity を代表 cell 窓に出すこと（「代表 cell」という嘘・cell 削除で dormant 化）／venue/mode/message badge レーンに相乗りさせること（別レーン＝AC4 非矛盾の構造保証が崩れる）／cell の execution state（stale/running）を document badge に混ぜること（責務分離）

**orphan-absence invariant（orphan 不在の構造不変条件）**:
「アプリが見かけ上死んでも裏でプロセスだけが実弾を出し続ける」状態が**構造的に在り得ない**こと
（ADR-0001 decision 3）。Python は pythonnet で Unity プロセスに埋め込まれ（同一 PID）、執行を担う
order pump（live loop）は **daemon thread** 上に居り、IPC も execution subprocess も持たない。ゆえに
Unity が死ねば執行も即死する。検証は**「Unity を kill して両方死ぬのを観測する」非決定的テストではなく**、
構造不変条件の assert で行う: ①Python `os.getpid()` == host process id（同一プロセス）②live loop thread
`daemon==True`（プロセスを延命しない）③`multiprocessing.active_children()==[]` ＋ venue adapter が in-proc
（out-of-process な order pump 不在）。記録: findings 0013・#22。
_Avoid_: 「Unity kill → 両方死ぬ」の literal kill テストで証明しようとすること（両方死ぬので観測不能・
非決定的）／orphan 防止に Job Object / heartbeat / dead-man's-switch を足すこと（同一プロセス埋め込みで
自動成立済み・ADR-0001 decision 3 が「不要」と明記）

**scenario（実行設定）/ scenario sidecar**:
Replay run の実行パラメータ（`granularity` / `initial_cash`(cash) / `instruments`(universe) / `start` / `end`）を
持つ **engine 所有**の versioned dict（schema v1/v2/v3・`engine.strategy_runtime.scenario`）。永続形は strategy
`.py` に co-locate した `<strategy>.json` の **`"scenario"` キー**（`_sidecar_path`: `foo.py`→`foo.json`）。engine の
唯一の read 入口は `load_scenario`/`strategy_loader.load`＝**この v3 形式しか読めない**。backcast の Replay 起動
`start_engine(strategy_file)`（`_backend_impl.py`）は **完全に sidecar 駆動**——`strategy_file` 一個だけ受け取り
scenario の granularity/instruments/start/end/initial_cash を全部駆動する（`catalog_path` は別途 `LoadReplayData` が
セットした `last_replay_catalog_path` から取る・panel フィールドではない）。ゆえに「Unity が v3 sidecar を書く →
engine がネイティブに読む」の一直線で AC③ を満たす（**変換層ゼロ**）。`start_nautilus_replay(cfg)` の flat override
入口（instruments 必須・granularity/initial_cash は cfg 優先）は **e-station/live 系譜**で backcast の Replay 経路では
ない。同一 `<strategy>.json` に **`scenario` キー（engine 所有・v3）と `layout` キー（Unity 所有・[[レイアウト parity（capability parity）]] / ADR-0003）が共存**できる（`load_scenario` は layout-only sidecar を許容）。
**Unity が write する schema_version は v3 のみ**（v1/v2 は切り捨て）。AC③「チャート更新」は **bar-by-bar ライブ追従**で
満たす——production path（[[backcast Replay 起動経路（production state-machine path）]]）の現実装は全バーを `engine_run`
**完了後**に `apply_replay_event` で一括注入する（`_backend_impl.py` ~L850）ため、#29 は `engine_run` の**run 中に per-bar
で chart/state を stream** する engine 側変更を含む（run 完了後一括ではない）。
_Avoid_: scenario 永続化に **ADR-0003 を適用すること**（ADR-0003 は layout 専用・scenario は engine 所有の別 seam＝category error）／
Unity 独自スキーマを新設すること（engine が読めず変換層 or engine 改修が要る・第二の真実源）／scenario sidecar を
[[layout binder]] の document と混同すること（別キー・別所有）／`start_nautilus_replay(cfg)` を backcast Replay の起動入口と見なすこと（`start_engine` が正）

**run 期間（start/end）vs lookback（指標窓ハイパラ）**:
backcast の scenario スキーマに run 期間としての `lookback` は**存在しない**（v3 は `start`/`end` の日付文字列のみ）。
移植元 TTWR の startup panel も run 期間は **Start/End を直接編集**で `lookback` フィールドは無い（panel の 4 field =
Start / End / Granularity / Initial cash）。TTWR で `lookback` と名の付くものは**戦略の指標窓幅**（`lookback=20` 等の
`__init__` 引数）で、run 期間ではなく v3 optional の `strategy_init_kwargs` を流れる**別物**。issue #29 が panel field に
挙げた `lookback` は run 期間（→ `start`/`end`）への読み替えで解決し、指標窓ハイパラは #29 の縦切り外（strategy
editor / `strategy_init_kwargs` の seam）。「どれだけ遡るか」の UX は sidecar 未設定時の**初期 seed**（start = end − 3ヶ月 /
end = 今日）で満たし、ユーザー上書き可。
_Avoid_: `lookback` を run 期間フィールドとして scenario sidecar に書くこと（engine が unknown key で reject）／run 期間と
戦略の指標窓 lookback を混同すること（前者 = `start`/`end`・後者 = `strategy_init_kwargs`）／end+lookback→start の
導出層を #29 に入れること（営業日計算でスコープが膨らみ going-forward 利益ゼロ）

**scenario 編集の 3 projection（TTWR 踏襲）**:
panel の scenario 永続化は 3 段に分ける: ①**editing buffer**（不正値も保持できる UI 入力・空 universe や負 cash も一時的に
取り得る）→ ②**validated-for-write**（[[scenario（実行設定）/ scenario sidecar]] へ書ける形に検証通過した値）→
③**on-disk scenario**（`<strategy>.json` の `"scenario"` v3）。AC④ の「不正値は run を起動しない」は ①→② のゲートで
表現する（不正な editing buffer は ② へ昇格できず run も persist もしない）。dirty 中は外部 metadata sync で editing
buffer を上書きしないガードを持つ。
_Avoid_: editing buffer の不正値をそのまま on-disk へ書くこと（②の検証ゲートを飛ばさない）

**universe（語の多義の解きほぐし）— universe SoT vs universe ソース（populate 軸 / prune 軸）**:
「universe」は backcast で **第一義に [[universe registry（instruments SoT）vs scenario panel]]（`InstrumentRegistry`）が保持する銘柄集合＝ワークスペースが今扱っている銘柄の SoT** を指す。これと、SoT を**満たす（populate）/ 絞る（prune）入力**としての universe は**別物**で、語を裸で使わず必ず修飾する。4 つの sense と 2 つの軸を区別する:
- **universe SoT**（=`InstrumentRegistry`・唯一の真実源）: picker / sidebar / startup tile のテキスト入力が編集し、[[chart tile family / base tile（銘柄別 chart・計画＝受け皿 issue「動的 N チャート」）]] がメンバーシップをミラーし、sidecar `scenario.instruments` に永続化される。chart / depth / run が消費するのはこれ。
- **venue live universe**（**prune 軸**のソース・#41）: 接続中の venue が「今これが有効/購読可能」と返す集合（`fetch_instruments` 由来・[[universe prune gate（破壊的 prune の live-source 判定・#41）vs picker status / badge band]] の LiveVenue source）。SoT を**この集合内へ prune（縮小）するための境界**であって、SoT を埋めるソースではない。実 producer は未実装（#41 は dormant・#31 が defer した実供給）。
- **Replay catalog universe**（**prune 軸**のソース・#41 Replay）: ローカル catalog（DuckDB `listed_info`）が `scenario.end` 時点で**データを持つ**銘柄集合。Replay の prune 境界。実 producer は未実装。
- **strategy の銘柄集合**（例 v19 の artifact `universe.json`・#70-75）: ある戦略が候補/売買対象に決め打ちした固定リスト（戦略に同梱・`__file__` 相対）。**populate 軸**の入力（「何を売買するか」）で、SoT を**埋める**側。venue/catalog の universe（「今何が有効/利用可能か」）とは**無関係な別軸**。
- **picker browse universe**（**populate 軸**のソース・#31/#46）: sidebar [+ Add] picker が「これを SoT に足せる」と並べる候補集合（`IAvailableInstrumentsProvider.Query` 由来・status-facing）。ユーザーが SoT を**埋める**ための discovery 一覧であって prune 境界ではない。供給源はモード/venue で分岐: Replay = DuckDB `listed_info`（`scenario.end` の point-in-time）、Live/列挙対応 venue（tachibana）= venue store snapshot、**Live/列挙非対応 venue（kabu・`enumerates_instruments=False`）= `listed_info` 最新スナップショットへ fallback**（#46・id は `<code>.TSE`＝kabu `_parse_instrument_id` と一致・DuckDB 欠落時は `LOCAL_UNIVERSE_UNAVAILABLE`）。**この browse source を [[universe prune gate（破壊的 prune の live-source 判定・#41）vs picker status / badge band]] の allowlist に流用してはならない**（R2 asymmetry "must not be merged"・#253）。kabu Live picker が listed_info で `Ready` を返しても prune は別 seam（`NullUniversePruneSource`・dormant）なので破壊的 prune は arm されない。
- **軸の関係**: **SoT は populate 入力（sidecar / strategy / user 編集）で埋まり、prune allowlist ソース（venue live / Replay catalog）で縮む**。埋める軸（populate）と縮める軸（prune）は逆向きで、同じ「universe」でも役割が反対。
_Avoid_: 裸の「universe」で SoT と prune ソースを混同すること（venue live universe は**縮める境界**であって SoT でも populate 入力でもない）／strategy の銘柄集合（v19）を venue live universe と同一視すること（前者は populate 入力＝何を売買するか・後者は prune 境界＝今何が有効か・別軸）／prune ソースで SoT を「埋まる」と考えること（prune は縮小専用・populate しない）／#41 が dormant なのを「universe が壊れている」と読むこと（実 venue/catalog producer 待ちで SoT 自体は正常に動く）

**universe registry（instruments SoT）vs scenario panel**:
universe（`instruments`）は **panel の 5 番目のフィールドではなく独立 seam**。移植元 TTWR でも startup panel は
start/end/granularity/initial_cash の 4 field だけで、universe は `InstrumentRegistry`（単一 SoT・editable・replace_all）
＋ `writeback_scenario_instruments_system`（registry → sidecar の `scenario.instruments` へ永続化・Replay mode gate）で
別管理される。**#29 の責任** = universe SoT（C# 版 registry）＋ sidecar writeback ＋ engine handoff ＋ validation を所有し、
縦切りを通すための**最小テキストリスト入力**（行/カンマ区切りで instrument ID 直接編集）まで。**#31（=#C instrument picker /
universe sidebar）の責任** = 検索・候補・複数選択のリッチ picker を**同じ SoT/writeback に差し込む**（#29 の薄い入力を
剥がして置換するリワークを出さない）。panel の writeback（start/end/gran/cash）と universe の writeback は**同一
`<strategy>.json` の `scenario` を協調 co-write**＝unknown フィールド（`account_type` / `strategy_init_kwargs` /
`instruments_ref`）と `layout` キーを保ったままマージ書き（TTWR `test_10c_concurrent_writeback_with_instruments` の parity）。
backcast の `start_engine` は sidecar 駆動なので **ADR-0007 の「registry が run 時に SCENARIO に勝つ」override は
「registry → sidecar に書く → engine が sidecar を読む」の永続化経由で実現**する（TTWR の run-time override 配線は #29 外）。
_Avoid_: universe を scenario panel のフィールドとして実装すること（別 seam・別 SoT）／#31 の picker UI を #29 に取り込むこと
（縦切りが太る）／co-write で sidecar を全置換し unknown フィールド・`layout` キーを落とすこと（マージ書きが正）／単一銘柄
（v1 instrument 単数）で実装すること（AC「空 universe 拒否」「銘柄リスト」は複数前提・後で multi 化は手戻り）

**market-data 購読（subscribe）vs universe membership（#107）**:
「登録」という語は backcast で 2 つの**全く別の操作**を指すので裸で使わず必ず修飾する。混同すると #253（system が勝手に銘柄を prune）と同類の所有権侵害になる:
- **universe membership（銘柄の出入り）** = [[universe registry（instruments SoT）vs scenario panel]]（`InstrumentRegistry` / `scenario.instruments`）への銘柄の **add / remove**。**所有者はユーザーだけ**（sidebar [+ Add] / × remove・picker・startup tile のテキスト編集）。**システムは絶対に銘柄を足さない/減らさない/間引かない**（prune は #41 の専用 fail-closed gate のみ・上記参照）。
- **market-data 購読（subscribe）** = *既に membership にある*銘柄を `subscribe_market_data` RPC で venue WS に購読させ、板（depth）/価格（trades）を流す行為（runner→adapter→venue WS）。membership は一切変えない。**Live モード（LiveManual/LiveAuto）でのみ**意味を持ち、Replay では no-op（precondition reject）。購読は idempotent（re-subscribe は no-op）で、**購読失敗・venue 実上限超過でも membership から銘柄を落とさない**——typed エラーで surface するだけ（[[live market-data 購読の本番配線（#107）]]）。
_Avoid_: 「全銘柄を一括登録」を「universe に銘柄を足す」と読むこと（=購読であって membership 追加ではない）／購読の都合（venue 50 上限等）で membership を間引くこと（membership はユーザー所有・購読は membership に従属）／subscribe を Replay モードで意味があると考えること（Live 専用・Replay は precondition reject）

**live market-data 購読の本番配線（#107）**:
LiveManual で sidebar universe の銘柄が WS 未購読のため選択銘柄の Chart/板が更新されない不具合（[[market-data 購読（subscribe）vs universe membership（#107）]] の購読側が**起動されていなかった**）を直す本番トリガ。購読チェーン（`SubmitSubscribeMarketData`(C#)→`subscribe_market_data`(orchestrator)→`runner.subscribe`→`adapter.subscribe({"trades","depth"})`→venue WS）は全段そろっていたが、**起動する本番配線が無かった**（`UniverseSidebarController.LiveSubscribeHook` 未代入・#31 DEFERRED seam のまま／本番呼出元ゼロ＝唯一の caller が E2E ランナー自身＝production-binding の死角）。配線は plain C# の [[LiveSubscriptionCoordinator]] に集約し: (a) **Live で行選択 / [+ Add]** → 当該銘柄を購読、(b) **LiveManual 突入時** → universe **全銘柄を一括購読**（人工的件数上限なし・venue 実上限は typed エラーで surface・方針: ADR-0022）。venue 非依存（runner/adapter 抽象経由）なので立花・kabu 共通。
_Avoid_: 購読を servicer 層の人工 50 件 cap で頭打ちにすること（撤去済み・venue 実上限へ委譲・ADR-0022）／E2E テスト自身が `SubmitSubscribeMarketData` を手動で叩いて「購読経路が動く」と確認すること（本番トリガ配線をゲートできない死角・production-binding gate は実 `SelectRow`/universe 復元を駆動する）

**universe prune gate（破壊的 prune の live-source 判定・#41）vs picker status / badge band**:
universe 外銘柄を [[universe registry（instruments SoT）vs scenario panel]] から削る prune は**破壊的操作**なので、候補表示用の
picker status とも badge 表示用の connection band とも**別の、より厳しい gate**で守る（TTWR universe.rs の "R2 asymmetry"・
"must not be merged" の capability parity）。三つを混同しない:
- **picker status**（`IAvailableInstrumentsProvider.Query()`・status-facing）= `Loading`/`Error`/`NotConnected`/`Unsupported`/`Ready`
  を UI に見せるのが責務。`NotConnected`（未ログイン＝「Venue not connected」）と `Unsupported`（ログイン済みだが銘柄列挙非対応＝
  kabu MVP `enumerates_instruments=False`＝「Venue has no instrument list」）は**別ステータス**（潰すと badge `Connected: KABU` と
  矛盾・findings 0103）。**これを prune の allowlist に流用しない**（status-only で通った `Ready{ids}` を削除根拠にすると #253 stale/
  fallback snapshot 回帰）。
- **badge band**（[[venue 接続状態（connection state — AC「VenueLoggedIn/Out」の正体）]]の `VenueConnectionViewModel.IsConnected`）
  = `{CONNECTED, SUBSCRIBED, RECONNECTING}`。badge が flap しないよう **RECONNECTING を意図的に含む**。**prune gate はこの
  `IsConnected` を再利用しない**（RECONNECTING 中に prune すると TTWR parity 違反）。
- **prune gate**（#41 の prune 専用 resolver・`CurrentUniverse(): IReadOnlySet<string>?`）= 次を**全部**満たす時だけ allowlist を
  返し、それ以外は `null`（=prune 禁止・fail-closed）: source が **LiveVenue**（fallback/stale/unsupported は除外）∧ venue が
  **`{CONNECTED, SUBSCRIBED}` のみ**（`is_venue_live` parity・**Reconnecting 除外**）∧ status `Loaded` ∧ list **non-empty**
  （空 list で registry を wipe しない・TTWR HIGH-1）。空 registry は skip。Replay は別 allowlist（catalog by scenario.end）。
  TTWR では `{LiveVenue, LocalVenueSnapshot}` を許可するが（universe.rs:149-154）、`LocalVenueSnapshot` は firing path 無しの
  reserved 値なので backcast #41 は**本文優先で `LiveVenue` のみ許可**し、divergence を findings に記録（方針: ADR-0005・
  内部 gate 定数は 1:1 表面 parity 対象外）。registry が縮むと `InstrumentRegistry.Changed` が picker/chart tile/depth に自動波及
  （`BackcastWorkspaceRoot` の `SyncChartWindowsToUniverse`）。
_Avoid_: picker `Query()` の `Ready{ids}` を prune allowlist に流用すること（status-only で破壊的 prune＝#253 回帰）／badge の
`IsConnected`（RECONNECTING 込み）を prune の live 判定に使うこと（Reconnecting prune は TTWR 違反）／空 list・空 universe で
registry を wipe すること（non-empty ∧ `null`=未確定 skip が正）／`LocalVenueSnapshot` を live source と見なして prune すること
（#41 本文では fallback 扱い）

**active strategy 選択（run-UI = #29 の責務）**:
#29 の panel が設定対象とする strategy `.py` は `IStrategyFileProvider` / `StrategyProviderRegistry`（#16・findings 0010 §5・
owner-lock）を**消費**して解決する（provider seam はまさに run-UI が消費するために作られた・#16 は run 配線と active 選択を
意図的に作っていない＝#29 が最初の durable consumer）。解決規則は**沈黙 pick を避けた決定的な形**: ①supplyable provider 0 個
→ Run をブロック（「保存済み strategy が無い / editor を保存してください」を scenario バリデーションと区別して surface）／
②1 個 → それを使う（happy path・tracer 本線）／③N 個 → registry の ordinal 決定的列挙で **active windowId を保持し
どの strategy が対象か必ず表示**（黙って先頭を選ばない・リッチ multi-active picker は follow-up）。sidecar path = provider の
canonical `.py` → `<strategy>.json`。**supplyable 5 条件（bound / not dirty / Open|Save 成功 / canonical absolute `.py` /
呼出時点で実在）は Run 起動の瞬間に再問い合わせ**する（populate 結果をキャッシュしない＝設定後に editor を編集すると
provider が non-supplyable へ反転しうるため・条件5「呼出時点で実在」）。「active windowId」は将来の multi-instance picker が
差し込む薄い seam（#31 universe と同じ構図・リワーク無し）。
_Avoid_: panel に strategy path を直接入力させ supplyable 契約（dirty 拒否・exists 保証・canonical 化）を再実装すること
（provider seam の二重定義・stale path 事故）／populate 時の supplyable 判定をキャッシュして Run 時に再チェックしないこと／
N 個のとき黙って先頭を選ぶこと（対象を必ず表示）

**backcast Replay 起動経路（production state-machine path）**:
backcast の通常起動 Replay は **`load_replay_data` →（sidecar 書き）→ `start_engine`** の state-machine path で起動する
（#29 が最初の Unity 配線を作る）。①`load_replay_data(instrument_ids, start, end, granularity, catalog_path)`＝`IDLE→LOADED`・
`last_replay_catalog_path` をセットし provider を prime（`core.py`）／②`start_engine({strategy_file})`＝`LOADED→RUNNING`・
[[scenario（実行設定）/ scenario sidecar]] を `load_scenario` でネイティブ読みし `load_bars_for_scenario` で run。出力は
**RunBuffer + apply_replay_event + `GetState`/`get_portfolio` ポーリング**（Unity が既に使う seam）で AC③ の
status/positions/orders/チャート更新を駆動する＝**`RustBacktestSink` 配線は不要**。`start_nautilus_replay(cfg)`（flat cfg・
`rust_sink` 必須・sidecar を読まない）は **throwaway harness（`ReplayChartHarness` 等）/ e-station 系譜専用**で、#29 後は
production Replay から deprecated（issue #29「harness 依存解消」の実体）。**dual-load 整合ハザード**: `load_replay_data` は
instruments/start/end/gran を**引数**で受け、`start_engine` は**同値を sidecar から再読み**する——両者の真実源は同一 panel
state でなければならない（ズレると prime したチャート初期状態と実 run の scenario が食い違う）。issue 本文の
「`start_nautilus_replay` 入口へ渡す」は imprecise で production path に読み替える（決定は #29 の `docs/findings/` に記録し
本 CONTEXT を参照）。**panel 配置（変遷）**: scenario 実行設定 UI の host 表面は 3 段階で移動した——
**① #29**: [[Hakoniwa（split-grid surface）]] の `PanelKind::Startup` タイル（slot 0・TTWR `populate_startup_tile` 直 parity）。
**② #99/ADR-0017**: Hakoniwa split-grid を退役し、dock クラスタの floating window `KIND_STARTUP`（base window）へ。
**③ ADR-0026（#117 系）**: dock の `KIND_STARTUP` を**完全退役**し、**[[Settings ダイアログ]] の Scenario セクション**へ集約。
いずれの段階でも**コンテンツ（`ScenarioStartupTile`）と所有 controller は不変**——載せ替えのみ。検証は AFK probe
（headless: populate→edit→`<strategy>.json` persist→restore→run→engine が当該 scenario で run したことを assert）＋ HITL harness（owner 目視）。
_Avoid_: scenario controller / `ScenarioStartupTile` を載せ替えのたびに再実装すること（同一 brain を再配置するのが正）／
production Replay を `start_nautilus_replay(cfg)` で起動すること（sidecar が run で読まれず Q1 の「変換ゼロ・
ネイティブ読み」が空文化・真実源が cfg に二重化）／production path に `RustBacktestSink` を配線すること（GetState ポーリングが
正・sink は throwaway 専用）／`load_replay_data` 引数と sidecar の instruments/start/end/gran をズラすこと（同一 panel
state が正）

**catalog_path（環境/配置の関心・scenario 外）**:
catalog_path は「このマシンのどこに市場データがあるか」＝**マシン依存の環境設定**で、[[scenario（実行設定）/ scenario sidecar]]
（何をバックテストするか・strategy に随伴して可搬であるべき値）とは別の関心。engine も catalog を `DataEngine` ctor 引数
（`nautilus_catalog_path`/`jquants_catalog_path`）で持ち、`load_replay_data` は `catalog_path or self._nautilus_catalog_path` で
構築時デフォルトに fallback する（`core.py`）。ゆえに #29 では catalog を **panel フィールドにしない**——app/engine config
（settings/env・`DataEngine` 構築引数）で解決し、`load_replay_data` を catalog_path 省略（engine default fallback）or
config 値で呼ぶ。scenario v3 スキーマに catalog キーは無く（書けば `_check_keys` が unknown key で reject）、絶対パスを
sidecar に焼くと strategy+sidecar が非可搬になる。既存 harness の `const CATALOG_PATH = "/Users/sasac/..."`（Mac 絶対
パス）こそ AC⑤「harness ハードコード依存解消」が消す対象で、#29 はこれを config 層の解決に置換する（ユーザー向け panel
フィールドへの昇格ではない）。**ADR-0006（#49）以降、production Replay の市場データ源は [[市場データソース（J-Quants DuckDB
直読み）]] へ移り、catalog_path の代わりに DuckDB ルート（`BACKCAST_JQUANTS_DUCKDB_ROOT`・`.env`）を同じ「環境/配置の関心」
として env/config で解決する**（ctor 引数 > `.env`・panel/sidecar には焼かない）。未設定は hard error で、nautilus catalog への
silent fallback は持たない（root が解決すれば catalog 引数より優先）。legacy catalog 解決は #50 の nautilus 撤去まで code として残る。
_Avoid_: catalog を panel フィールド / scenario sidecar に入れること（環境設定・非可搬・engine が unknown key で reject）／
catalog の真実源を per-run panel state にすること（config 層が正）／DuckDB root 未設定時に nautilus catalog へ fallback すること
（runtime nautilus-free が env 依存で非決定的になる・ADR-0006）

**ScenarioSidecarStore（merge-write seam）**:
[[scenario（実行設定）/ scenario sidecar]] の `<strategy>.json` を**読み込み→`scenario` object の対象キーだけ更新→
sibling 全保存→atomic write** する単一 seam（TTWR `scenario_sidecar/write.rs` の `atomic_mutate_scenario_object` parity）。
backcast の layout sidecar は**別ファイル**（`LayoutStore` が `LayoutPathResolver` のパスへ `JsonUtility` で `LayoutDocument`
を書く）で、scenario sidecar `<strategy>.json` とは独立。engine の `scenario.validate` は strict（未知キー reject）なので
merge-write が書式・キー順を壊すと strict-validated sidecar を corrupt させる。panel が編集するのは `start`/`end`/
`granularity`/`initial_cash`/`instruments` の 5 キーのみで、**触らない v3 optional（`account_type`/`instruments_ref`/
任意 nested dict の `strategy_init_kwargs`）を無損失 preserve** する必要がある。`JsonUtility` は任意 dict を round-trip
できないため、merge-write は **Newtonsoft `JObject`**（read-modify-write）で行い、Newtonsoft を本 store 一点に封じ込める
（呼出側は `SetStartupParams`/`SetInstruments` だけを見る・`LayoutStore` の parser-hiding 規律踏襲）。方針: **ADR-0005**。
3 projection（[[scenario 編集の 3 projection（TTWR 踏襲）]]）の ③on-disk を所有する層。
_Avoid_: scenario sidecar を `JsonUtility` で write すること（`strategy_init_kwargs` 等を silent drop し sidecar corrupt）／
PeelTag 型 string surgery で nested dict を跨ぐ merge をすること（whitespace/escape/キー順事故で corrupt・PeelTag は READ
専用 decoder の慣例で逆 trust boundary）／Newtonsoft を本 store 外へ漏らすこと（layout は `JsonUtility` 据え置き・ADR-0005）

**broker reconciliation modal（#40）= 移植せず除外（not-applicable・ADR-0008）**:
owner 決定（2026-06-15・案②）で #40 は**実装せず close**、`reconcile_modal` を ADR-0005 の 1:1 surface parity 契約から
**除外**する（ADR-0008＝ADR-0005 を本サーフェス限定で supersede・ADR-0005 本体は自己保護条項により非編集）。理由: TTWR
`reconcile_modal.rs` が開く唯一の契機は**別プロセス backend の crash→自動再起動で記憶を失い UI の楽観的注文とズレる**こと
（`GetOrdersAndReconcile`→`OrdersReconciled`→`ReconcileUnknownOrder{client_order_id,symbol,status}`・`orders_model.rs`
`reconcile_unknown_orders`・通知専従・採用/取消なし §3.8）。backcast は in-proc 埋め込みで「UI 死＝engine 死／orphan 不在」
（[[orphan-absence invariant（orphan 不在の構造不変条件）]]・ADR-0001 dec.3）のため**engine 単独再起動が構造的に発生せず契機が
発火不能**＝実データで一度も開けない dead surface。これは「起こり得ない故障モードへの反応 UI」であり不在は機能後退に当たらない。
起動時の既存 venue 状態は modal ではなく **seed で正として取り込む**：venue resting order は connect-seed
（`fetch_working_orders`・現状 stub `[]`）で注文パネル、venue 建玉は `account_sync` 初回 emit＋kernel `portfolio.py`
`seed_position`（D7・`kernel/live/controller.py:161` wired）で口座。実契機を作る open issue は無い（#40 body の「engine 非同期
reconcile は #23」は stale＝#23 は demo-roundtrip done-gate として close 済み）。詳細調査と Q1–Q4 経路は findings 0021。
_Avoid_: #40 を「未実装 TODO」と読むこと（owner が not-applicable で close 済み＝作らないのが正）／復活時に #40 を再 open すること
（venue 再ログイン突合など in-proc でも成立する別契機を設計するなら新規 issue＋ADR-0008 参照）／起動時の seed 済み venue 状態を
modal 化すること（案A・却下＝"うるさすぎ"・既にパネル表示で二重）／in-modal 採用/取消（§3.8・[[取消受付 / 取消確定（cancel acknowledgment vs confirmation）]]）

**テーマ（配色システム）/ ThemeService / ColorScale / ProbeTheme（#44）**:
UI 配色の集中定義と切替の単一 SoT。**`ColorScale`**＝Radix 12 段スケール（neutral=slate/accent=iris/red/green=grass/
yellow=amber/blue の 6 本）。これを大元に **`from_scales`** が `ThemeColors`(54 ロール)/`StatusColors`(info/warn/error/
success＋long/short/bid/ask)/`SyntaxColors`/`PlayerColors` を**導出**する（TTWR `src/ui/theme/` の配色レイヤー忠実移植）。
**`ThemeService`** ＝アプリ単一 global（TTWR Bevy Resource 同型）で `Current`（遅延 dark 既定）/`SetTheme`/`event Changed` を
持つ。各サーフェスは色を参照し、`Changed` 購読→**`ApplyTheme()`** で塗り直す（平面は `image.color` 再代入、描画時消費＝
syntax mesh/chart candle/ladder は色フィールド再読込＋再描画）。**`ProbeTheme`**（=TTWR `non_default_theme()` 移植・全ロール
別値）は検証専用テーマで、AFK probe/HITL の切替先に使う（shipped は dark のみ・`Light()` は dark stub＝TTWR 踏襲、実 light は
follow-up）。**配色レイヤーのみ**移植（spacing/typography/elevation/radius/layout は別 issue）。記録: findings 0020・方針 ADR-0005。
_Avoid_: テーマに spacing/typography 等の非配色トークンを含めると解釈すること（#44 は配色のみ）／切替伝播を TTWR parity と
呼ぶこと（伝播は backcast 独自・TTWR は swap 未実装で Bevy change-detection に依存）／`ApplyTheme` を [[layout binder]] の
Capture/Apply と混同すること（別系統・前者は配色再適用）／インライン配色を新規追加すること（theme 参照が正・静的ゲートで縛る）

**menu bar（全体メニュー / screen-fixed chrome）**:
アプリ全体の最上段メニュー。[[infinite canvas]] の Content **外**に置く screen-fixed chrome（pan/zoom 非追従）。TTWR
`src/ui/menu_bar.rs` の 1:1 表面 parity（方針: ADR-0005）で、トップレベルは **File / Edit / Venue / Help**。**File は Layout 文書**
（New / Open / Save / Save As・[[レイアウト parity（capability parity）]] / ADR-0003）を対象とし、**strategy `.py` の Open/Save では
ない**（strategy 編集は [[Strategy Editor（code buffer）]]＝#16 が所有・menu bar は再実装しない。layout sidecar が strategy
パスを参照し File→Open で間接復元する）。**[[ExecutionMode（実行モード）]] 切替は menu bar の独立 picker ではなく File 操作の
副作用**：`File→New`→ loaded strategy/panel clear ＋ `SetExecutionMode(LiveManual)`、Live 中の `File→Open`→`SetExecutionMode(LiveAuto)`。
明示的な Replay/LiveManual/LiveAuto picker は **[[Settings ダイアログ]] が所有**（旧 footer mode セグメントを移設・ADR-0026）。
**Venue メニュー（Connect×venue / Disconnect）は退役**し [[Settings ダイアログ]] の Venue セクションへ集約（ADR-0026）。
brain は既存 [[VenueMenuViewModel]]（#21）を**再利用合成**し重複実装しない（AC③）——退役したのは menu の表面のみ。mode 副作用は venue 未接続時に [[ExecutionMode（実行モード）]] の precondition（`ModeManager` が
`CONNECTED`/`SUBSCRIBED` 以外を `EXECUTION_MODE_PRECONDITION` で拒否）に当たるため、menu bar 側で接続中のみ送信ガードし TTWR の
**observable no-op** を再現する（clear 自体は無条件）。backcast に TTWR の `ForceStop`（走行中 replay 停止）等価は無く、replay-stop は
#30 の責務（#42 は clear+LiveManual で先行）。実装は #42。
_Avoid_: File を strategy `.py` opener と解釈すること（File=Layout・strategy 編集は #16）／menu bar に明示 mode picker / run 操作を
持たせること（footer #39/#30 の責務）／venue ロジックを menu bar に再実装すること（[[VenueMenuViewModel]] 再利用が正）／mode 副作用を
venue 未接続でも無条件送信すること（`EXECUTION_MODE_PRECONDITION` 例外＝接続中ガードで no-op 再現が正）

**Settings ダイアログ（screen-fixed modal / 設定の集約口）**:
Help→Settings で開く screen-fixed モーダル（ADR-0026・方針: ADR-0005 を表面配置に限り部分 supersede）。**3 つの設定機能を
1 箇所へ集約**する：① **Venue 接続/切断**（退役した menu Venue dropdown の移設先）、② **実行モード切替セグメント**
（Replay/LiveManual/LiveAuto・旧 [[footer]] から移設）、③ **[[Replay 実行設定（scenario startup）]]**（退役した dock の
`KIND_STARTUP` window の移設先）。**brain（[[VenueMenuViewModel]] / [[FooterModeViewModel]] / scenario controller）は不変**
——ビュー層を同じ VM に作り直すだけで engine 経路は触らない。**ESC で開閉トグル（guard 付き）**：window drag 中は drag-revert
（ADR-0024 §8）優先・[[secret modal]]/save-guard が開いている間はそちらが ESC を消費。`[x]` でも閉じる。z-order は
[[secret modal]](1000)/save-guard より**下**——Venue 接続で second password を要求されたら secret が Settings の上に重なり、
送信後は Settings が開いたまま venue 状態更新を映す。
_Avoid_: brain を Settings 用に再実装すること（VM 再利用が正）／footer を廃止すること（footer は mode ステータス表示専用に残す）／
Settings を secret modal より前面に置くこと（modal は常に secret/save-guard が最前面）

**footer（screen-fixed chrome / mode ステータス表示）**:
アプリ最下段の screen-fixed chrome（[[infinite canvas]] の Content 外・pan/zoom 非追従）。**かつては TTWR `src/ui/footer.rs` の
実行トランスポートを所有していたが、現在は mode ステータス表示専用に縮退**（replay transport=#30 は #76 で撤去済み・mode 切替
セグメントは ADR-0026 で [[Settings ダイアログ]] へ移設）。現在モード / `switching…` / `LiveAuto:<runId>` を [[FooterModeViewModel]]
から映すのみで、切替 control は持たない（「今どのモードか」は menu-bar の `mode: <X>` バッジでも常時可視）。
_履歴（mode 切替を footer が所有していた時代の設計・findings 0025）_: 責務は 2 issue に分かれていた: **Replay transport（#30）**＝
**play / pause / step / speed / stop** で Replay run を進める（▶ は文脈依存: PAUSED→resume・RUNNING→pause・terminal/Idle/Loaded→run
起動＝re-arm）。**mode picker ＋ StartLiveAuto（#39）**＝明示的な [[ExecutionMode（実行モード）]] 切替と Live auto 起動。run の**起動設定**
（universe/期間/granularity/cash）は footer ではなく [[Replay 実行設定（scenario startup）]]＝#29 が所有し、footer はそれを駆動する
transport に徹する。backcast の transport seam は TTWR の `TransportCommand` enum + mpsc ではなく、**in-proc pythonnet の直接メソッド
呼び出し**（[[Backcast Execution Kernel（kernel）]] の `run_event`/`stop_event`/`step_event`/speed holder を host が叩く）。
**#39 の mode 真実源 = 楽観＋poll 答え合わせ**（poll 値で常に上書き）。**TTWR poll 正準からの唯一の逸脱 = engine が断り得ない
切替（Replay/速度/step）は poll を待たず即時反映**（拒否され得ない＝構造的に desync しない・反応速度優先）。Live 遷移は拒否され得るので
ロック→engine の答え待ち＝parity。**LiveAuto の停止 = stop ボタンを出さず、Replay/LiveManual セグメント押下で footer が
`stop_live_strategy(run_id)` を先に呼び成功時のみ mode 切替**（stop 失敗なら LiveAuto 維持＋エラー）。これは TTWR footer に無い
teardown の追加だが、backcast の orphan 禁止不変条件（findings 0017 §4・ADR-0001）が根拠の正当な逸脱。記録: findings 0025。
_Avoid_: mode 切替を純 poll 正準（楽観表示なし）と解釈すること（Replay/速度/step は即時が正）／LiveAuto を mode 切替だけで
止まると見なすこと（`set_execution_mode` は run を止めない＝footer が明示 stop する・findings 0017）
_Avoid_: footer に run 起動設定（scenario）を持たせること（#29 が所有・footer は transport）／#30 と #39 を混同すること（前者=Replay
の play/pause/step/speed/stop、後者=mode 切替＋StartLiveAuto）／`TransportCommand` enum/mpsc を移植すること（backcast は pythonnet 直呼びが正）

**市場データソース（J-Quants DuckDB 直読み）**:
backcast Replay の going-forward な市場データ at-rest 源＝`/Volumes/StockData/jp/` 配下の**銘柄別 DuckDB ファイル**
（`stocks_daily/<code>.duckdb`・`stocks_minute/<code>.duckdb`・`listed_info.duckdb`＝銘柄マスタ）。Replay 実行時に
`duckdb` で**直接クエリ**して [[Backcast Execution Kernel（kernel）]] へ bar を渡す（中間 catalog の生成・変換ステップを持たない）。
これは TTWR 由来の **nautilus `ParquetDataCatalog`（precision-baked `fixed_size_binary` ＝ `nautilus_pyo3` ビルドに data が
縛られる形式）を置換**し、runtime から nautilus を完全排除する（方針: ADR-0006・ADR-0004 案 C の Replay への延伸）。値段は
**生(raw) OHLCV**（調整列は当面 NULL のため不使用）、銘柄IDは `<code>.TSE`（master の市場は全て東証）、当面は **bars（日足/分足）
のみ**（`stocks_board` の歩み値/板は将来スライス）。`jquants_to_catalog.py`（nautilus 書き出し）/ `nautilus_catalog_loader.py`
（nautilus 読み）/ `/Volumes/StockData/artifacts` parquet catalog は本決定で廃止対象。
_Avoid_: 「catalog」と呼ぶこと（中間 parquet を作らない直読み・[[catalog_path（環境/配置の関心・scenario 外）]] の旧 nautilus
catalog とは別物）／DuckDB から parquet へ一度変換する中間層を入れること（第二の真実源・変換ズレ）／調整済み価格を使うと
仮定すること（当面は raw・調整は別スライス）／nautilus を oracle のため runtime に残すこと（[[golden 契約（Backcast vs Nautilus oracle）]]
は凍結 fixture 化し runtime から nautilus を消す）

## Flagged ambiguities

- **「本番」**: backcast の文脈では将来の本線を指すが、移行期間中の **live 実弾**は当面 TTWR(Bevy) が
  担い得る。「本番フロント」=backcast（going-forward）、「現 live 実行系」=TTWR（fallback）と区別する。

- **「≥300fps」（seam ゲート AC の言い回し）**: S0（#2）・viz-spike（#8）の AC が言う「≥300fps」は
  **毎秒 300 フレーム（throughput）ではなく、worker が連続で backtest 実行 / ndarray upload する間に
  main が描画を止めずに ≥300 フレーム継続する**こと＝ main が GIL/upload に一度もブロックされない証明。
  VSync 環境で 300 FPS の throughput を要求するものではない。findings には `frames >= 300` と
  実測 `maxDt`／hitch を記録する。

- **「interpreter pin（CPython patch version）」**: production pin は **`3.13.11 win_amd64`**（deploy=Windows=
  TTWR `.venv` 実測）。docs に一時期 `3.13.13` とあったのは uv index に存在しない phantom pin の誤記で、
  **Mac S0 先行実験のみ** `3.13.13` で走った（patch-version drift・#8 grill 2026-06-13 訂正）。canonical は
  ADR-0001 decision 7。

## Example dialogue

> **Dev:** Live を Unity 側に出すのはいつ？
> **Owner:** S2-spike（#7）が green になってから。S0 は backtest の threading しか見てない。
> **Dev:** engine は TTWR から参照する？
> **Owner:** いや、**移植**。backcast が engine を所有する。TTWR は fallback で温存して、カットオーバーで**廃止**。
> **Dev:** じゃあ engine の host 結合は剥がす必要があるね。
> **Owner:** ほぼ剥がれてる。sink 注入点と `engine.core` / `engine.inproc_server` の 2 入口、dict 境界だけ。
>   そこに C# の **adapter** を差せば host 非依存のまま動く。
