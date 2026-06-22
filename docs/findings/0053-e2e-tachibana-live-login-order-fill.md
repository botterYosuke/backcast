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

### § issue #85 真因 #4: account-level EC WS が server から `ST p_errno=2` 連発（2026-06-19 後場 2 回実証 → piggyback 採用）

> 当初仮説「`p_rid=22→0`」は **同日後場 2 回目の実走で empirically 棄却**。続いて e-station 参照
> 実装と同じ piggyback 方式を採用した — その経緯を下記 § §当初仮説 と §採用 (piggyback) に記録する。

#### §当初仮説（棄却済み）: `p_rid` の値違い

commit b30ca67 で 3 修正を適用した後の同日再走で、新たな真因として **サーバが `ST p_errno=2`
(仮想URL無効) を連発し、FILLED push が永遠に届かない** ことが判明（issue #85 comment §真因シグナル）:

```
WARNING tachibana ws: ST frame ticker=EVENT p_errno=2 (total ST=1)   ← 5 回出現
WARNING tachibana ws: EVENT disconnected (websocket closed); reconnecting in 1.00 → 2 → 4 → 8 → 16 s
```

#### 当初仮説（→ 棄却）

`tachibana.py:_ensure_ec_stream` の URL クエリは長らく ⚠️ **TENTATIVE** で、FD path
(L411–417) から `p_rid="22"` を copy-paste していた。仮説:「`p_rid=22` は App No.2 (時価配信あり)
の specialization で FD ticker params とペアの前提 → EC 単体に流用するとサーバが拒否」。
`samples/e_api_websocket_receive_tel.py` L514–525 の generic 例 docstring が
`?p_rid=0 ... &p_evt_cmd=ST,KP,EC,SS,US` を示しているため、`p_rid` を `"22" → "0"` に変更
（owner AskUserQuestion で確認）。

→ **2 回目の実走 (`tachibana-live-20260619-151930.log`) で同じ `ST p_errno=2` が再発し empirically 棄却**。
`p_rid` の値 (22 / 0) は discriminator ではなく、account-level standalone EC subscription
それ自体が server に受理されないと判明（owner と再 grill）。

#### 採用 (piggyback / e-station 参照実装に揃える)

`event_protocol.md §EC「残る Demo 検証事項 ①」`に記された通り、**e-station 参照実装は
EC を per-ticker FD 接続に相乗りさせる**（account-level standalone EC stream は使わない）。
本実装も同方式へ:

| 変更点 | 旧 | 新 |
| :--- | :--- | :--- |
| `subscribe(instrument_id, ...)` の `p_evt_cmd` | `"ST,KP,FD"` | `"ST,KP,EC,SS,US,FD"` |
| `_make_callback(...)` の非 FD frame | discard | `_dispatch_event_frame` へ転送 (sticky 前進 + EC/SS routing) |
| `_ensure_ec_stream(...)` | account-level EC WS を起動 | **no-op**（backward-compat のため残す） |
| Runner step 2.4 (新規) | — | `host.Lanes.SubmitSubscribeMarketData(7203.TSE, ...)` (carrier ticker) |
| `LiveRpcLanes.SubmitSubscribeMarketData` (新規) | — | write-lane 経由で `server.subscribe_market_data(iid)` を呼ぶ |

EC は口座単位で全件配信されるため、carrier ticker は subscribe 済みなら何でもよい。Runner は
発注対象 7203 を carrier にすることで FD frame が trade 経路として無関係でなくなる効果を期待。

却下案:
- **carrier ticker (7203) を `_ensure_ec_stream` にハードコード**: venue-internal な詳細に
  trader-strategy の選択が漏れる。per-ticker hub と二重 WS になり `(session, p_issue_code)`
  1 接続契約 (`tachibana_ws.py:282-284`) を破る可能性。
- **owner に追加資料を依頼**: `api_event_if.xlsx` は手元になく、e-station の参照実装 (本リポジトリ
  has-not) を採るのが docs/wiki と整合。owner も option 2 を選択。

#### 防御強化（step 2.5 gate の trap door 塞ぎ）

旧設計 Q1 (A') は「`_dispatch_event_frame` 冒頭で frame_type に依らず `ec_ws_first_recv_ts_ms`
を進める」だったため、**SSL ハンドシェイクは成立しているが server が `ST p_errno=2` を返す
ルートでは、`ec_ws_subscribed` が True に立って Runner step 2.5 を擦り抜けて place に進んでしまう**
trap door があった（issue #85 comment §設計上の含意で「follow-up が妥当」と保留した部分）。

修正: `_dispatch_event_frame` 冒頭の signal 更新ロジックを以下に差し替え:

| frame_type | p_errno | first_recv_ts_ms の扱い | last_recv_ts_ms |
| :--- | :--- | :--- | :--- |
| ST | `"0"` | 通常通り前進（None なら set） | 常に更新 |
| ST | `"0"` 以外 / 欠落 | **None にリセット**（sticky 解除、Runner gate 再 spin） | 常に更新 |
| KP / FD / EC / SS / US | — | 通常通り前進（None なら set） | 常に更新 |

`last_recv_ts_ms` は「サーバが応答した活動の記録」として死フレームでも常に更新する
（staleness watchdog 用 / 診断用）。`p_errno` field 欠落の ST は安全側で error 扱い
（parser 不一致 / server bug 時の trap door 防止）。

これで:
- p_rid=22 の URL クエリ不整合がある状態でも、step 2.5 gate が **place する前に fail-fast**
  し、demo に未約定 ACCEPTED order が残置するルートは塞がれる。
- 健全な session の途中で server が session を殺すケース（夜間閉局越え race など）でも、
  ST p_errno=2 を受けた瞬間に sticky が剥がれ、Runner / state poll が「subscribed 失効」を
  即検知できる。

#### テスト

新規 / 改訂 RED→GREEN（14/14 GREEN・実装後）:
- `python/tests/test_tachibana_ec_stream_url.py`（4 cases・piggyback 採用で改訂）— subscribe()
  の URL に `p_evt_cmd=ST,KP,EC,SS,US,FD` が raw comma で入る／`p_gyou_no=1` ＆
  `p_issue_code=<ticker>` ＆ `p_mkt_code=00` のチケット 3 点があり、`p_rid=22`／`p_board_no=1000`／
  `p_eno=0` を保持／`_ensure_ec_stream` は no-op で task / WS / stop_event を作らない。
- `python/tests/test_tachibana_st_error_clears_ec_subscribed.py`（5 cases）— ST `p_errno=0` は
  signal 前進／`p_errno!="0"` は sticky を立てない／既存 sticky を剥がす／`p_errno` 欠落は
  安全側 error 扱い／非 ST frame (KP/FD/EC/SS/US) は従来通り signal 前進。
- `python/tests/test_tachibana_per_ticker_cb_piggyback.py`（5 cases・新規）— per-ticker
  `_make_callback` が KP / SS / ST / FD / EC frame を `_dispatch_event_frame` に転送する
  (piggyback の中核契約)。

既存の `test_tachibana_ec_ws_signal.py`（4 cases）/ `test_orchestrator_exposes_ec_ws_subscribed_in_state.py`
（2 cases）/ `test_orchestrator_state_ec_ws_subscribed_default_for_non_tachibana.py`（3 cases）
は退行なし（`_dispatch_event_frame` のセマンティクスを直接 assert しているので piggyback の
有無に依存しない）。total 67/67 GREEN（tachibana 関連サブセット）。

#### 残務

- [ ] **再走 GREEN**: owner が場中に batchmode 実走で `[E2E TACHIBANA-LIVE PASS]` を取得し
  exit 0（**本 issue の完了条件**）。
- [ ] **追加観測（任意・本 issue 範囲外）**: `_ensure_ec_stream` を起動した瞬間に発火される
  最初の SS frame（接続毎に必ず再送される）でフィールド名 prefix を確定し、`_handle_system_status`
  の TENTATIVE を解消する。本 fix で EC WS が live になれば自然と確定する副産物。

### § issue #85 真因 #5: piggyback でも `ST p_errno=2` が再発 — owner 側調査へ分離（2026-06-19 後場 第 3 回実走）

piggyback refactor を適用した 3 回目の実走 (`tachibana-live-20260619-154100.log`) でも server が
`ST p_errno=2`（ticker=7203）を返し続け、WS subscription が成立しないことが empirically 確定。
直後 4 回目 (`-diag.log`) では login 自体が `p_errno=-3 "ただいまシステムが大変混み合っております"`
（official manual `api_request_if_v4r7.md:256` で documented = サーバサイド rate-limit / cooldown
が必要）で fail し、これ以上の即時検証は不可能になった。

| Run | JST | Login | WS subscribe | Verdict |
| :---: | :---: | :---: | :---: | :--- |
| 1 (b30ca67) | 14:06 (場中) | OK | ❌ `ST p_errno=2` (account-level, `p_rid=22`, no ticker params) | FAIL @ step 2.5 |
| 2 (p_rid=0) | 15:19 (場中ぎりぎり) | OK | ❌ `ST p_errno=2` (account-level, `p_rid=0`) | FAIL @ step 2.5 |
| 3 (piggyback) | 15:41 (閉局後) | OK | ❌ `ST p_errno=2` (per-ticker FD WS, `p_rid=22` + full ticker params, `p_evt_cmd=ST,KP,EC,SS,US,FD`) | FAIL @ step 2.5 |
| 4 (diag) | 15:50 (閉局後) | ❌ `p_errno=-3` server busy | — | FAIL @ step 2 |

**3 つの URL 仮説がすべて empirically 棄却された**:
- account-level standalone (`p_rid=22`, no ticker params): 失敗
- account-level standalone (`p_rid=0`, no ticker params): 失敗
- per-ticker FD piggyback (`p_rid=22` + ticker params + EC,SS,US in p_evt_cmd): 失敗

3 回目で **FD subscription path も同じ p_errno=2 で蹴られる**ことが分かり、これは URL の形ではなく
session / auth / server-state レベルの問題と判明。本 issue のスコープを超える深掘り診断が必要。

**本 issue の達成状況**:
- [x] **trap door 完全閉鎖**: 3 回連続で「demo に未約定 ACCEPTED order が残置するルート」が
  防御 gate (step 2.5 + ST p_errno!=0 sticky reset) によって塞がれることが empirical 確認済み。
  これは b30ca67 時点よりも厳格な保証（b30ca67 の元設計では SSL OK + ST=2 で sticky が立つ
  trap door があったが、本 follow-up で塞いだ）。
- [x] piggyback refactor の code は clean・tests 67/67 GREEN・runner step 2.4 + lane 追加 完了。
- [ ] **再走 GREEN**: server 側の WS p_errno=2 問題が解消されないと達成不可。

**次の follow-up は [#92](https://github.com/botterYosuke/backcast/issues/92) に切り出し**:
- WS 接続時に websockets ライブラリの handshake response code / headers を WARNING ログ
  （現状の URL diagnostic と同箇所）。これで server-side rejection の理由が拾える可能性。
- 既存 e-station 参照実装（`C:\Users\sasai\Documents\e-station\python\engine\exchanges\tachibana_event.py`、
  本リポジトリに無い）を owner が探し出し、WS handshake の前提条件 / order を直接比較。
- demo 口座の API permission ステータス確認（特に EVENT WS 利用可否）。
- `api_event_if.xlsx` 入手（manual_files に同梱されていない外部資料 / 本 issue でも対応せず）。
- p_errno=-3 cooldown 後の単発 clean run（前 run の影響を排除）で再現性を確認。

### § issue #92 真因 #5 続報: standalone probe で再現確認 — handshake は成功・サーバが `session inactive.` で蹴る（2026-06-22 後場）

issue #92 コメントの方法論メモ（Unity batchmode をループ単位にせず standalone Python probe で
**minimise** する）に従い、`python/scripts/tachibana_ws_probe.py` を新規作成して場中
（JST 14:29）に実走した。fresh login（local session cache を削除して必ず再ログイン）→
**production と完全に同一の `build_event_url` 経路**（`tachibana.py:417` と同じ
`p_rid=22 / p_board_no=1000 / p_gyou_no=1 / p_issue_code=7203 / p_mkt_code=00 / p_eno=0 /
p_evt_cmd=ST,KP,EC,SS,US,FD`）で subscribe URL を組み → WS 接続、の最小再現。

```
login OK. url_event_ws = 'wss://demo-kabuka.e-shiten.jp/e_api_v4r8/event_ws/<token>/'  (wss:// 正常, len=83)
TCP/TLS/WS handshake OK — connection established.
<ST> p_errno=2 fields={'p_no':'1','p_date':'2026.06.22-14:29:06.208','p_errno':'2','p_err':'session inactive.','p_cmd':'ST'}
→ [WS-PROBE FAIL] ST p_errno=2 — #92 STILL REPRODUCES.
```

**#92 は現在も 100% 再現する**。加えて、以前は詳細不明だった `p_errno=2` の中身が判明した:

| 項目 | issue #85/#92 記録時の理解 | 2026-06-22 probe での観測 |
| :--- | :--- | :--- |
| WS handshake | 候補 #2 (undocumented handshake) / #3 (URL構造異常) を疑い | **成功** — TCP/TLS/WS 確立 OK |
| サーバ応答 | `ST p_errno=2`（中身不明・「仮想URL無効」と解釈） | **明示的に `p_err='session inactive.'`** を 1 frame 返す |

→ **handshake と URL の形は真因ではない**ことが empirical に確定（候補 **#2 / #3 を棄却**）。
サーバは subscription を parse できる程度には受理した上で、**session-state レベルで蹴っている**。
manual `api_request_if_v4r7.md:46` で `session inactive.` ＝「セッションが切断しました」、
`event_protocol.md:147` で `p_errno=2` ＝「仮想URL無効 → 再ログイン（電話認証から）」。
残る真因は候補 **#1（server-side session state）／#4（demo 口座の EVENT WS entitlement 欠落）**に絞られた。

**probe の confound（次に切り分けるべき点）**: 本 probe は login 直後に REQUEST を 1 本も挟まず
即 WS connect している。「EVENT session は REQUEST 経由で activate される必要がある」説、あるいは
`.env` の `DEV_TACHIBANA_SECOND`（第二暗証）が WS session 昇格に絡む説は、probe に 1〜2 行
追加すれば次回 login 1 回で切り分け可能（rate-limit `p_errno=-3` を避けるため login 回数を絞ること）。

**オフライン回帰**: `pytest -k "tachibana and (ws|ec|st_error|piggyback|signal)"` = 37 passed。
ただしこれらは mock で trap door / piggyback の**防御挙動**を検証するもので、サーバ受理は検証しない
（#92 とは独立に緑）。

### § issue #92 真因 #5 決着: 公式資料 v4r8→**v4r9** 移行が原因 — EVENT WS が旧式セッションを無効判定（2026-06-22 作業ツリー差分）

owner が立花公式サンプルを v4r8 → v4r9 に更新（作業ツリー差分）。これを精査した結果、
`session inactive.` の原因が**立花 API のバージョン移行 (v4r8 → v4r9) による認証フロー全面刷新**で
あることが判明した。

**差分の切り分け**:
- `e_api_event_receive_tel.py` / `e_api_websocket_receive_tel.py` の diff は**空白・インデント整形のみ
  （実質ゼロ。EVENT WS の接続機構そのものは不変）**。
- 実体は `e_api_sample_v4r9` の README(v3.0) ＋ メインサンプル `e_api_sample_v4r9.py`。

**v4r9 の変更点（README【変更点】＋ `e_api_sample_v4r9.py`）**:

| # | 変更 | 根拠 |
| :---: | :--- | :--- |
| 1 | auth URL `e_api_v4r8/` → **`e_api_v4r9/`** | `e_api_sample_v4r9.py:64` |
| 2 | ログイン引数 `sUserId`+`sPassword` → **`sAuthId`（認証ID）** | `:358-361` `req_login` |
| 3 | 仮想URL5本（`sUrlRequest`…`sUrlEventWebSocket`）が**公開キーRSA暗号化**で返り、**秘密キーで復号**必須 | `:384-432` `decrypt_url`（base64decode → RSA-OAEP-SHA256） |
| 4 | 前提に**利用設定画面で「API利用＝利用する」宣言＋公開キー登録**。無いと**ログインＩ／Ｆがエラー応答** | README【変更点】 |
| 5 | 新依存 `pip install cryptography` | README 5. |
| 6 | README 7.(3)「セッション無効化時」= **`p_errno:[2] / セッションが切断しました。 / "session inactive."`**（=我々の観測エラーの逐語） | README 7.(3) |

**症状の一貫性**: 2026-06-22 probe の観測「**handshake は成功・サーバが `p_errno=2 'session inactive.'`
を返す**」は URL 形でも handshake 不備でもなく**セッション無効判定**。v4r9 README はこれを「セッション
無効化時」として明記。サーバは v4r9 に移行済みで、**v4r8 ログインが返す旧式（非暗号化）EVENT WS 仮想URL
のセッションを無効扱いしている**、で全症状が一貫する。これにより issue #85/#92 の候補 **#1（stale
session）/ #4（entitlement 欠落）も「v4r9 enrollment 未了」に統合**される。

**留保（断定ではなく最有力）**: 我々の v4r8 ログイン自体はまだ成功し REQUEST も通る（v8 に後方互換の
猶予があり EVENT WS だけ先に v9 cutover、と推測）。「v9 移行が EVENT WS 不通の唯一原因」は確証前。
確証には下記 enrollment が要る。

**我々のコードの現状**: 完全に v4r8（`tachibana_url.py:59-60` の `BASE_URL_PROD/DEMO` が `e_api_v4r8`、
`tachibana_auth.py:262-263` が `sUserId`/`sPassword` ログイン、暗号化・`sAuthId`・`cryptography` 一切なし）。

**確証＆修正への道（コードだけでは閉じない・口座側作業が必須）**:
1. 利用設定画面で API 利用「利用する」宣言 → **認証ID** 取得、**公開キー**登録（対の**秘密キー**を保管）。
2. コード v4r9 化: base URL `e_api_v4r9`、ログイン `sAuthId` 方式、仮想URL5本を秘密キー復号、`cryptography` 追加。
3. `tachibana_ws_probe.py` を v4r9 ログインに差し替えて EVENT WS 再走 → `ST p_errno=0` を確認。

→ これは URL パッチではなく **API バージョン移行（v4r8→v4r9）プロジェクト**。#92 のスコープを超えるため
別 issue 起票が実態に合う（owner 判断）。
