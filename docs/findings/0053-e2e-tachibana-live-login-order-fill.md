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
