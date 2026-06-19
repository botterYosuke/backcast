# findings 0053 — E2E: 立花 demo ライブ「ログイン→成行発注→約定」自動ゲート

`Assets/Tests/E2E/Editor/TachibanaLiveE2ERunner.{cs,md}`（[ADR-0015](../adr/0015-e2e-runner-layout-and-script-convention.md)）の設計の木。
これまで HITL でしか確認していなかった「実 venue（立花 demo）への接続→発注→約定」を batchmode で完全自動化する初の **live** E2E。
ドメイン不変条件は [`/tachibana` SKILL.md](../../.claude/skills/tachibana/SKILL.md)、約定経路は本 findings で固定。

## スコープ（owner と grill で確定 2026-06-19）

「ログイン→成行発注で**約定確認**」までを自動検証する（owner 選択）。MOCK ではなく実 demo venue（`demo-kabuka.e-shiten.jp`）を叩く。

| 決定 | 値 | 根拠 |
|---|---|---|
| 範囲 | login → 成行発注 → **FILLED 確認** | owner 選択（.env に第二暗証があるため発注経路まで） |
| 環境 | **demo 固定**（`TACHIBANA_ALLOW_PROD=1` は付けない・1 なら即 FAIL で拒否） | R1 / `tachibana.py` `require_prod_env` |
| 銘柄・数量・方向 | **7203.TSE / 100 株 / BUY / MARKET / TIF=DAY** | owner 指定。`price=null`（成行は `sOrderPrice="0"`） |
| 閉局・約定無しの扱い | **常に厳密＝約定無しは FAIL**（exit 1）。場中判定は診断ログのみ | owner 選択。場中（前場 09:00–11:30 / 後場 12:30–15:30 JST）運用が前提。`tachibana_ws_codec.is_market_open` |
| 建玉の後始末 | **残置（flatten しない）** | owner 選択。demo の建玉積み上がりは許容 |
| ハーネス | `WorkspaceEngineHost` を**単体 new**（scene 不要）。host が `_sink/_panel/_lanes` を自己完結で所有 | replay runner と違い render 経路は対象外。venue 往復が主眼 |

## 配線（コードで裏取り済み）

- **venue 固定**: `WorkspaceEngineHost.InitializePython("TACHIBANA")`（`WorkspaceEngineHost.cs:144,182`）。server は venue 固定で一度だけ構築。
- **ログイン（tkinter 不要）**: `host.VenueLogin("TACHIBANA","env","demo",onResult)`。`credentials_source="env"` 経路は `tachibana.py:255-282` で `DEV_TACHIBANA_USER_ID/PASSWORD` を `os.environ` から直読みし `_auth_login`。tkinter を spawn しない。`IS_DEBUG_BUILD=True`（`_build_mode.py`、editor batchmode で True）。
- **接続観測**: 連続正本は poll（`get_state_json` の `venue_state ∈ {CONNECTED,SUBSCRIBED,RECONNECTING}` + `venue_id`）。C# は `VenueConnectionViewModel.ApplyStatePoll(host.LatestStateJson)` → `IsConnected`（`VenueConnectionViewModel.cs`、`_backend_impl.py:772`）。
- **発注**: `host.Lanes.SubmitPlaceOrder("TACHIBANA","7203.TSE","BUY",100,null,"MARKET","DAY",onResult)`（`LiveRpcLanes.cs:77`）。`order_facade.py` が `side/order_type` を upper 正規化・検証（`{"BUY","SELL"}` / `{"MARKET","LIMIT"}`）、`tachibana_orders.py` が `MARKET→sOrderPrice="0"`。
- **資格情報の解決**: `EnvConfig.Get`（`Assets/Scripts/ScenarioStartup/EnvConfig.cs`）を使う。解決順は **process env 優先 → `<repo>/.env` → `<repo>/python/.env`**。⚠️ `PythonRuntimeLocator.ProjectRoot` は `<repo>/python` であり repo root ではない（.env は repo root にある）。自前 `.env` パス組み立ては `<repo>/python/.env` を見て creds を取りこぼすので **EnvConfig に統一**（process env / `source .env` / CI 注入も拾える）。`DEV_TACHIBANA_USER_ID/PASSWORD` は `os.environ` へ（"env" credentials_source が直読み）。
- **第二暗証（完全自動化の肝）**: 第二暗証は **Python の env 経路には載らない**（R10、Python は `DEV_TACHIBANA_SECOND` を読まない）。発注中に `place_order` が `SecondSecretResolver.resolve` で `SecretRequired` を sink に push（write lane が `.result()` で GIL を解放しブロック）。runner は **main で `host.DrainLiveEvents()` を pump** し、`panel.SecretRequiredCount` の edge で `host.Lanes.SubmitSecret(requestId, secondPwCopy)`（urgent-secret lane、別スレッド）で応答（`LivePanelViewModel.cs:37`、`LiveRpcLanes.cs:200`、`secret_provider.py:57`）。
  - 第二暗証は runner が **`char[]` で保持**し submit_secret に clone を渡す（`SubmitSecret` が payload を zeroize するため）。**os.environ には入れない**。batchmode は終了即 exit なので EnvConfig の string キャッシュ寿命は許容。
- **約定観測**: `place_order` の ack は `status="ACCEPTED"`（`tachibana.py:610`、未約定）。FILLED は EC（CLMEventDownload/EC frame）が WS push → `OrderEvent(status="FILLED")` → sink → `panel.FilledOrderCount++`（`LivePanelViewModel.cs:58`、`tachibana.py:841-902`、`tachibana_orders.py:298`）。runner は ack 後も `DrainLiveEvents()` を pump し `panel.FilledOrderCount>0` かつ `panel.LatestOrder.Status=="FILLED"` かつ `FilledQty>=100` を assert。

## 観測点と合格条件

| step | 観測 | 合否 |
|---|---|---|
| login | `VenueLogin` onResult ok==true、poll で `conn.IsConnected`（venue_state CONNECTED） | demo へログイン成立 |
| place | `SubmitPlaceOrder` onResult `Success==true`（ack `ACCEPTED`）、secret edge を 1 回応答 | 発注が venue に受理 |
| fill | `panel.FilledOrderCount>0` ＆ `LatestOrder.Status=="FILLED"` ＆ `FilledQty>=100` | 成行が約定 |

PASS ログ: `[E2E TACHIBANA-LIVE PASS] ...`、exit 0。FAIL は `EditorApplication.Exit(1)`。

## 完全自動化の前提・落とし穴

- **場中限定**: 成行は前場/後場のみ約定。閉局・週末・認証未済では FAIL（厳密運用）。FAIL メッセージに閉局診断（`is_market_open`）を添える。
- **電話認証**は別途 owner が demo 口座で完了済みであること（API 前提条件、SKILL.md）。
- **CI 非組込**: 立花 demo ジョブは `workflow_dispatch` 限定（閉局による偽陰性回避、open-questions Q21）。本 E2E も PR/push に載せない。
- **本番二重ガード**: `TACHIBANA_ALLOW_PROD=1` を検出したら発注前に拒否。常に `environment_hint="demo"`。
- 関連 memory: [[hitl-surfaces-bugs-afk-gates-miss]]（実 venue HITL が掴むバグの AFK 化）、[[unity-batchmode-probes-runnable-here]]（batchmode 実走可）。

## § issue #85 改訂（初回実走で観測した 4 件の修正 / grill 2026-06-19）

初回実走（後場 12:36 JST）で `venue_login → CONNECTED → place_order ack(ACCEPTED) → SecretRequired 応答` までは通り、**FILLED OrderEvent の 30s タイムアウトで FAIL**。真因は **EVENT WebSocket (`wss://`) の SSL 証明書検証失敗**で、EC push が一度も購読確立しなかったこと。発注は REQUEST(HTTP) で成立しているが、約定通知は WS push 経路のため届かない。

### TLS trust = certifi で固定

- 根本原因: `tachibana_ws.TachibanaEventWs._connect_once` は `websockets.connect(wss://…)` に `ssl=` を渡さず、`ssl.create_default_context()` の OS system trust store fallback に依存していた。Windows-Unity-embedded Python は OS の証明書ストアを引かないため、CA bundle 不足で握手失敗（`[SSL: CERTIFICATE_VERIFY_FAILED] unable to get local issuer certificate`）。REQUEST 側は httpx の transitive 依存に乗って certifi を使えており login が通るが、WS はぶら下がっていなかった非対称が「login OK / EC NG」の正体。
- 採用: (α) certifi 明示 + module-level `_TLS_CTX = ssl.create_default_context(cafile=certifi.where())` + (δ) ctor の `ssl_ctx` optional 注入。`_connect_once` 内 lazy 解決で、`_TLS_CTX` への `monkeypatch.setattr` も effective に保つ。
- 却下案: (β) `truststore.inject_into_ssl()` は社内 CA / proxy で価値があるが、本ゲート（demo 固定）のスコープを超える ＆ Windows 側機械依存を増やすため将来検討。 (γ) `SSL_CERT_FILE` env は 3 系統配線（runner / 本番 launcher / CI）が必要で抜けが silent regression 化、明確に劣る。
- 依存宣言: `python/pyproject.toml [project.dependencies]` に **`certifi>=2024.0` を direct 宣言**。httpx の transitive に乗らないことを invariant にする（next major で外された/差し替えられた時の静かな破損経路を断つ）。
- 適用範囲: `self._url.startswith("wss://")` の scheme gate でのみ `connect_kwargs["ssl"]` を立てる（`ws://` + `ssl=` は `websockets` が `ValueError`）。`TickerEventWsHub` 経由の FD/trades 経路も module-level default で無料で塞がる。
- ADR は切らず本 findings + `CONTEXT.md` の `certifi` glossary エントリで締める（hard-to-reverse は ★☆☆、surprising は ★★★、tradeoff は ★★☆ で ADR の閾値未満）。社内 CA / proxy が浮上した時点で再評価。

### Step 2.5 = EC WS handshake gate（不具合 2 の正しい形）

- 問題: 当初案「poll の `venue_state` が **SUBSCRIBED** まで進むことを発注前 gate にする」は **コード裏取りで誤りと判明**。`SUBSCRIBED` への遷移は `live_orchestrator.subscribe_market_data:914-918` 経由（market-data 購読成立時のみ）で、`_ensure_ec_stream` は `venue_sm` を一切触らない。本 Runner は market-data を購読しないため SUBSCRIBED に永遠に到達せず、額面 gate は常時 RED 確定。
- 採用 (Q1 (A')): adapter に EC WS 受信シグナル 2 つを生やし、state に top-level として露出する。
  - `TachibanaAdapter.ec_ws_first_recv_ts_ms` / `ec_ws_last_recv_ts_ms` — `_dispatch_event_frame` の frame_type 分岐より前で set（KP / SS / EC どの frame_type でも前進、閉局時の SS-only 経路でも誤陰性にならない）。`_stop_ec_stream` でのみ reset、単発の WS reconnect (`run()` 内 backoff) では sticky に保つ。**public name (no leading underscore)**: cross-module で `_backend_impl.get_state_json` が getattr 文字列読みする契約 — `_`-prefix の private 名前を文字列で読むと rename refactor が silent regression する（code-review B#3）。
  - `TradingState.ec_ws_subscribed: bool` / `last_event_ws_recv_ts_ms: int`（**`0 = 未受信` sentinel**）を `live_last_error` の **直前** に追加（§9.14 「`live_last_error` は末尾固定」の inline invariant を保つ）。`Optional[int]=None` ではなく `int=0` 既定にしているのは Unity JsonUtility が `long` フィールドの `null` 入力で例外を投げ得るため（code-review G#3）。
  - `_backend_impl.get_state_json` で `getattr(getattr(runner, "adapter", None), "ec_ws_first_recv_ts_ms", None)` のように **2 段 getattr** で kabu/mock runner（`.adapter` 属性自体が無いケース）まで venue-agnostic に bind（code-review A#4 / C#1）。
  - C# `VenueConnectionViewModel.StateDto` に `bool ec_ws_subscribed` / `long last_event_ws_recv_ts_ms`（`0 = 未受信` sentinel、JsonUtility は `Nullable<long>` を扱えないため）を追加。`EcWsSubscribed` / `LastEventWsRecvTsMs` / derived `HasEventWsRecvTs` を expose。`IsConnected` の D6 契約には触れない。
- Runner: **step 2.5** として `SpinUntil(conn.IsConnected && conn.EcWsSubscribed, 60s)`。fail 時は verdict に `state=${LatestStateJson}` を添えて exit 1。**SSL 失敗時 / WS 未確立時は place する前に確実に fail-fast** し、demo に未約定 ACCEPTED が残置するルートを根本から塞ぐ。60s budget は SSL handshake (2-5s) + KP keepalive 初回 (5-12s server-driven) + 1 reconnect cycle (backoff ≥1s) + flaky network 余裕（code-review G#2）。
- 却下案: (B) `_ensure_ec_stream` で `venue_sm.transition_to("SUBSCRIBED")` は `_LIVE_OK_VENUE_STATES` / `IsConnected` の意味を broken にするため NG。(D) Python 側 cancel pre-gate は「直近 N 秒以内に EC が来てなかったら reject」を place_order に持たせると、開局直後の数百 ms 窓で偽陰性 ＋ place_order を時間依存ゲートに変える。

### 後始末ポリシー（不具合 3 改訂）

| 状況 | ポリシー |
|---|---|
| 約定後 (FILLED) | 残置（flatten しない）。既存方針継続。 |
| 未約定 FILL timeout (W1) | Runner 自身が `Lanes.SubmitCancelOrder` を 1 回叩く（cancel pump は place と同じ `DrainLiveEvents` + `SecretRequiredCount` edge → `SubmitSecret` パターン。`CANCEL_TIMEOUT_MS=45000`）。verdict は元の `NO FILLED` を保持し、cancel 結果を suffix に追記（Q3 verdict matrix）。 |
| EC WS 未確立 (W2 / step 2.5 fail) | `place` 自体を呼ばないので cancel 不要。verdict に `LatestStateJson` を添える。 |
| 既存残置 (W3 / `be2aa1d0…`) | owner が立花 demo の Web 管理画面から手動 cancel。CLI helper は採用しない（Q3.1 で W1 を塞げば次回以降の累積も止まる）。 |

cancel pump の **race re-check**: 「cancel 発射前」と「cancel 完了直後」の **2 段階**で late-fill grace を効かせる（code-review A#1 / B#6）。前段は timeout exhausted 直後にもう 1 回 `DrainLiveEvents()` を回し late fill が到着していたら PASS 経路に合流（pump cadence 50ms と venue RTT 数百 ms の窓を救う）。後段は cancel pump が 45s 走る間に到着した fill を、cancel 完了後にもう 1 回 drain して救う（fill が成立していれば cancel 結果は best-effort・CANCELED でも REJECTED でも PASS 経路へ）。Python 側 cancel の `EC seen ⇒ no-op REJECTED 短絡` 追加は別 issue 相当（race window は確率的に十分小さい）。

### 不具合 4（secret 応答 last-write-wins）

- 範囲外。本 issue では修正しない。将来 modify/cancel が連発するシナリオで顕在化する備忘として `LivePanelViewModel` の `PendingSecretRequests` queue 化を検討（別 issue）。

### 受け入れ基準（改訂後の umbrella）

- [x] 不具合 1: `tachibana_ws.py` の `_TLS_CTX = ssl.create_default_context(cafile=certifi.where())` を module-level + `wss://` scheme gate + ctor 注入 (δ)。`pyproject.toml` に `certifi>=2024.0` を direct 宣言。
- [x] 不具合 2: state に `ec_ws_subscribed` / `last_event_ws_recv_ts_ms` を露出、Runner に step 2.5 を挿入、fail 時 verdict に `LatestStateJson` を添付。
- [x] 不具合 3: Runner step 4 FAIL 経路で late-fill race re-check → SubmitCancelOrder + secret pump (45s)、verdict matrix で結果を suffix に追記。
- [ ] 不具合 4: 範囲外（備忘のみ）。
- [ ] 再走 GREEN: owner が場中（前場 09:00–11:30 / 後場 12:30–15:30 JST）に batchmode 実走、`[E2E TACHIBANA-LIVE PASS]` を取得し exit 0。**完了条件**。
- [ ] W3 cleanup: `be2aa1d0de8b4164aae40eaf12fb17bb` を owner が demo Web 管理画面で手動 cancel。
