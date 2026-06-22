# KabuLiveE2ERunner — 台本（#107 kabu 実 venue 検知 leg / HITL）

`KabuLiveE2ERunner.cs` が駆動する **kabu（kabuステーション）verify ライブの検知 leg**。方針:
[ADR-0022](../../../../docs/adr/0022-livemanual-market-data-subscription-production-wiring.md)、下位事実:
[findings 0086](../../../../docs/findings/0086-issue107-livemanual-market-data-subscription-wiring.md)。
共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) / [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。

> **位置づけ**: *Issue release-gate slice runner*（HITL）。`TachibanaLiveE2ERunner` の kabu 版で、#107 の本番購読配線を
> **実 demo venue（kabu verify）**で検知する。立花 leg と対をなす（AC#3「立花 / kabu 双方で板が live 更新」）。

## ⚠️ 前提（HITL・Windows 限定）

- **kabuステーション本体（Windows GUI）が起動・ログイン済み**で、設定→API→「APIを利用する」が有効、API パスワード設定済み。
- **検証モード（localhost:18081）** で起動していること（本番 18080 は触らない）。
- `DEV_KABU_API_PASSWORD` を process env / `.env` で供給。`KABU_ALLOW_PROD=1` は設定しない（verify-only ガード）。
- **場中**（板 PUSH が来る時間帯）であること。
- macOS / Linux / CI では走らない（kabuステーション本体が Windows 専用）。

## 検証する不変条件（#107 の核）

- **テスト自身は `SubmitSubscribeMarketData` を呼ばない。** 本番と同じ `LiveSubscriptionCoordinator` +
  `LaneSubscribeSink` を universe 投入 → LiveManual 突入の一括購読として駆動する（production-binding の死角を作らない）。
- 本番トリガ購読が走ると kabu は `PUT /register`（50 銘柄上限・burst は `kabusapi_ratelimit` が throttle・R5/R6）→
  WS PUSH で板が流れる。runner はその板（depth）が `get_state_json` の `per_instrument[INSTRUMENT].depth` に
  現れることを `DepthDecoder.HasDepth` で gate する。
- kabu に第二暗証は無い（X-API-KEY トークンのみ・R3）。

## 操作一覧表

| Action ID | 行動 | 入口 | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|---|
| KABU-LIVE-01 | ログイン（env / verify） | `WorkspaceEngineHost.VenueLogin` → `KabuStationAdapter.login(env)` → `fetch_token` | `venue_state=CONNECTED` | poll 収束を assert | HITL専用（本体・verify） |
| KABU-LIVE-02 | 本番トリガで universe 一括購読 | `LiveSubscriptionCoordinator.OnModePoll(LiveManual)` → `LaneSubscribeSink` → `subscribe_market_data_batch` → kabu `PUT /register` | 板 PUSH が `per_instrument[INSTRUMENT].depth` に到達 | `DepthDecoder.Decode(state,INSTRUMENT).HasDepth=true` を assert | HITL専用（場中・本体） |
| KABU-LIVE-03 | venue 実上限の typed surface | `kabusapi.subscribe` → `KabuRegisterFullError(4002006)` → `SubscriptionLimitExceeded` | 51 銘柄目が `SUBSCRIPTION_LIMIT_EXCEEDED` | **pytest** `test_subscribe_market_data_batch.py`（AFK・本体不要） | 自動(E2E済・pytest) |

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath . \
        -executeMethod KabuLiveE2ERunner.Run -logFile <abs log>
# expect: [E2E KABU-LIVE PASS] / exit=0  （確認は Bash `grep -a "E2E KABU-LIVE"`）
```
