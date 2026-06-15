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
現状 `NoopLiveEngineController`（TEST PLACEHOLDER）で **「Phase 10 に延期」**（`engine_controller.py` /
`order_facade.py` / `nautilus_exec_client.py`）＝未完。`LiveManual` の demo roundtrip 統合ゲートは #23。
_Avoid_: 「Live」と「Auto」を同義で使うこと（手動発注 vs 自律売買で別物）／`LiveAuto` を実装済みと見なすこと（placeholder）

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
変える権威ではない**——通知後も badge は poll の収束を待つ。secret flow とは独立。記録: findings 0012・#21。
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

**Hakoniwa（split-grid surface）**:
infinite canvas の Content 上に乗る単一の **split-grid サーフェス**。chart + status 系 tile（`chart` /
`status` / `positions` / `orders` / `run_result`）を **locked `ceil(√n)` グリッド**（n=5 → 3 列×2 行・最終
cell は空）に並べる。TTWR の Hakoniwa（`src/ui/hakoniwa.rs`・ADR 0011/0014）の **capability parity**（ADR-0003・
形式非互換）。Content の子なので pan/zoom に自動追従する（chrome は追従しない）。実装は #14。
_Avoid_: free-float／overlap（tile は grid slot を占めるだけ）／chart を Hakoniwa の外の常設 floating window と
定義すること（TTWR 現行も chart は Hakoniwa tile）

**tile / slot / tile swap**:
**tile** = Hakoniwa の 1 区画（安定 `id` で同定）。**slot** = tile が占める grid スロット番号（row-major・
左→右／上→下）＝ #12 `PanelLayout.slot`（**順序の正本**）。tile の実表示矩形（`LayoutRect`）は n+slot から
**等分グリッドで派生**する snapshot で、自由配置や split 比率の正本ではない。**tile swap** = ヘッダ drag で 2 tile の
slot を入れ替える操作（**swap であって自由配置ではない**・TTWR ADR 0014 parity）。divider resize（列幅/行高の
比率変更・ADR 0015 parity）と box 移動（root の canvas 位置永続化）は #14 **外**＝将来 slice の additive 拡張。
_Avoid_: slot を rect から導く／rect を split 比率や root 位置の正本に流用すること（slot が正本・rect は派生）

**floating window / FloatingWindowLayer / z-order**:
infinite canvas の Content 上を **自由配置（free placement）**で漂う window（Strategy Editor / Order 等）。Hakoniwa の
**tile swap とは別物**（tile は grid slot を占めるだけ・自由配置不可。floating window は canvas 論理座標で position+size を
自由に持つ）。**chart は floating window ではない**（Hakoniwa tile。TTWR で chart floating は廃止＝`dispatcher.rs` が
`PanelKind::Chart` spawn を拒否）。**FloatingWindowLayer** = Content 直下の単一コンテナで、全 floating window はその子。
HakoniwaRoot と sibling order（z-order）を混在させないための層（Content の子なので pan/zoom には追従する）。**z-order** =
window の前後関係。live は **FloatingWindowLayer 内の sibling index**（後の sibling ほど前面）、persist は **`zOrder` int**
（#12 `PanelLayout.slot` とは同一視しない＝findings 0004 §3 が「zOrder は別 field」と予約済み）。**click-to-front** =
window をクリック/drag したとき最前面へ（TTWR `WindowManager.max_z` bump の capability parity・形式非互換）。**move** =
title bar drag で position を移動（screen delta / zoom → canvas 論理 delta）。実装は #15。
_Avoid_: chart を floating window と呼ぶこと（Hakoniwa tile が正）／zOrder を `slot` に相乗りさせること（別 field）／
floating window rect を panel の 0..1 正規化 `LayoutRect` で持つこと（floating は canvas 論理座標の position+size）／
resize/常時最前面 pin を #15 の汎用 window system に含めること（前者は将来 slice・後者は実 editor content 由来の例外）

**Strategy Editor（code buffer）**:
floating window kind `strategy_editor` の**実 content**。strategy `.py` を編集する code buffer（Python の lexical
syntax highlight / undo-redo）。#15 は generic な window frame（spawn/move/z-order/persist）だけ立て、実 content を
deferred した — その content がこれ（#16）。編集対象は**実在する `.py`**（新規作成・ピッカーは射程外）で、編集は
buffer 上で行い save でディスクへ書き戻す。highlight は**意味解析ではなく lexical**（builtin を固定リストで着色しない＝
shadow 可能なので構文ではない）。LSP / autocomplete / Python parser / 実行検証は射程外。実装は #16。
_Avoid_: chart/order と混同すること（別 kind）／#15 の汎用 floating window system に content を混ぜること（content は
caller の window factory が `kind=="strategy_editor"` のとき合成・controller 境界は不変）／「Python parser を載せた」と
表現すること（lexical tokenizer であって parser ではない）

**strategy file provider（供給 seam）**:
編集・保存済みの strategy `.py` の**パス**を Replay/Live に `strategy_file` として渡す durable な境界（#16）。engine は
パス（≠ ソース文字列）を消費し `_load_strategy` がディスクから開くため、供給するのはソースではなく**保存済みパス**。
「**供給可能**」= path バインド済み ∧ not dirty ∧ 直近 Open/Save 成功 ∧ canonical absolute `.py` ∧ 呼出時点で実在、の
すべてを満たすときに限る（dirty 時は stale パスを返さず拒否＝「provider が返すパス = ディスク内容が buffer と一致」を保証）。
**active/current/default strategy の選択も run lifecycle も持たない**（run-UI / 別 slice の責務）。multi-instance（#15 の
`strategy_editor:region_001` 等）は window id → provider の registry で lookup/列挙する（active 選択はしない）。
_Avoid_: **adapter と呼ぶこと**（adapter は engine/pythonnet 境界専用の予約語）／run trigger・`start_nautilus_replay`
呼出・active 選択を #16 / provider に含めること／ソース文字列を供給すると解釈すること（パスが正）

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
本 CONTEXT を参照）。**panel 配置**: scenario 実行設定 UI は [[Hakoniwa（split-grid surface）]] の **`PanelKind::Startup`
タイル（slot 0）**として載せる（TTWR `populate_startup_tile` の直 parity・TTWR Replay Hakoniwa = [Startup, BuyingPower,
Orders, Positions, RunResult]）。backcast 現タイルセット [chart, status, positions, orders, run_result] は「Python-free
HITL demo」placeholder（findings 0007 §0）で、#29 が Startup タイルを実体化する。floating window kind 新設は TTWR
逸脱で却下。検証は AFK probe（headless: populate→edit→`<strategy>.json` persist→restore→run→engine が当該 scenario で
run したことを assert）＋ HITL harness（owner 目視）。
_Avoid_: scenario panel を floating window kind / screen-fixed chrome として新設すること（TTWR は Hakoniwa Startup タイル）／
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
