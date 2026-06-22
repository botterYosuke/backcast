# LiveSubscribeWiringE2ERunner — 台本（#107 production-binding gate / 操作網羅台帳）

`LiveSubscribeWiringE2ERunner.cs` が自動検証する **#107「LiveManual market-data 購読の本番配線」の release
gate**。実装者は `.cs` と本 `.md` をセットで読む。方針: [ADR-0022](../../../../docs/adr/0022-livemanual-market-data-subscription-production-wiring.md)、
下位事実: [findings 0086](../../../../docs/findings/0086-issue107-livemanual-market-data-subscription-wiring.md)。
採番・カバー語彙・責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)、配置は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。

> **位置づけ**: *Issue release-gate slice runner*（特定 issue の本番配線を細く gate する）。owner 決定（grill
> 2026-06-22 Q2）で **full-stack**（mock adapter・venue-free・CI 常時）を採用。実 `UniverseSidebarController.SelectRow`
> / `AddFromPicker` / LiveManual 突入という**本番経路**を実 `BackcastWorkspaceRoot` 合成上で駆動し、実 subscribe RPC
> → engine → runner → MOCK adapter → depth poll → `DepthDecoder.HasDepth` まで通す。

## 直したバグ（死角）

購読チェーン（`SubmitSubscribeMarketData`→`subscribe_market_data`→`runner.subscribe`→`adapter.subscribe`→venue WS）は
全段そろっていたが、**起動する本番トリガが無かった**：`UniverseSidebarController.LiveSubscribeHook` 未代入（#31 DEFERRED
seam）・本番呼出元ゼロ（唯一の caller が E2E ランナー自身＝production-binding の死角）。本 gate はその死角を kill する。

## 最重要の不変条件（litmus）

- **テスト自身は `SubmitSubscribeMarketData` / `SubmitSubscribeMarketDataBatch` を一度も呼ばない。** 旧 gate（`TachibanaLiveE2ERunner`）が
  自分で購読していたのが死角だった。本 gate は SelectRow / AddFromPicker / mode 突入という本番トリガだけを駆動する。
- **MOCK adapter の depth 注入は subscribe-gated**（`inject_tick` が `instrument_id in self._subscribed` を要求）。
  ゆえに「購読されていなければ板は出ない」＝depth が出た ⟺ その銘柄が本番経路で購読された、が成り立つ（非 vacuous）。
- **membership 不可侵（ADR-0022 D3）**: gate は universe へ銘柄を出し入れするが、それは「ユーザー操作の代行」であって
  購読側が membership を触ることは production にも gate にも無い。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| SUBWIRE-01 | LiveManual 突入で universe 全銘柄が購読され板が出る（AC#1/#5） | `BackcastWorkspaceRoot.DriveFooter:1600`→`LiveSubscriptionCoordinator.OnModePoll`/`BulkSubscribeUniverse` | universe[A,B] を seed→`SetExecutionMode(LiveManual)`→DriveFooter で rising edge→batch 購読→各銘柄 emit_depth で `HasDepth=true` | MOCK で実 root を駆動、`DepthDecoder.Decode(state,id).HasDepth` を assert（自己 subscribe しない） | 自動(E2E済) | `DepthLadderHitlHarness`（LiveAuto・HITL のみ） |
| SUBWIRE-02 | Live 行選択で当該銘柄が購読され板が出る（AC#2 row-select） | `UniverseSidebarController.SelectRow:102`→`LiveSubscribeHook`→`OnLiveRowSelected` | 突入後に追加した C を `SelectRow(C,Live)`→`HasDepth(C)=true` | 実 sidebar の SelectRow を駆動、`HasDepth(C)` を assert | 自動(E2E済) | — |
| SUBWIRE-03 | Live [+ Add] で追加銘柄が購読され板が出る（AC#2 [+ Add]） | `UniverseSidebarController.AddFromPicker:84`→`LiveSubscribeHook`→`OnLiveRowSelected` | `AddFromPicker(D,Live,…)`→`HasDepth(D)=true` | 実 AddFromPicker を駆動、`HasDepth(D)` を assert | 自動(E2E済) | — |
| SUBWIRE-04 | 未購読銘柄は板が出ない（非 vacuity / litmus floor） | `mock_adapter.inject_tick`（subscribe gating） | 未購読 `0000.TSE` へ emit_depth→`HasDepth=false` のまま | 1.5s 窓で板が出ないことを assert | 自動(E2E済) | — |
| SUBWIRE-05 | venue 実上限超過は typed エラーで surface（人工 cap 撤去・AC#4） | `live_orchestrator.subscribe_market_data*`／`kabusapi.subscribe`→`SubscriptionLimitExceeded` | kabu 51 銘柄目が `SUBSCRIPTION_LIMIT_EXCEEDED`、立花は無制限、人工 50 cap 撤去 | **pytest** `test_subscribe_market_data_batch.py`（engine seam・C# 不要） | 自動(E2E済・pytest) | — |
| SUBWIRE-06 | 立花 demo で本番トリガ経由の板取得 | `TachibanaLiveE2ERunner` | universe 投入→突入/選択購読で実 demo FD 板 | 実 demo 資格情報・場中が必要 | HITL専用（実 venue・場中） | `TachibanaLiveE2ERunner` |
| SUBWIRE-07 | kabu demo で本番トリガ経由の板取得 | `KabuLiveE2ERunner` | universe 投入→突入/選択購読で実 demo PUSH 板 | 実 demo（検証 18081）・本体起動が必要 | HITL専用（実 venue・本体） | `KabuLiveE2ERunner` |

## litmus（delete-the-production-logic）

- `LiveSubscriptionCoordinator.BulkSubscribeUniverse` の本体を消す → **SUBWIRE-01 RED**（突入で誰も購読しない）。
- `BackcastWorkspaceRoot` の `_sidebarCtrl.LiveSubscribeHook = _subCoord.OnLiveRowSelected` 代入を消す → **SUBWIRE-02/03 RED**。
- `mock_adapter.inject_tick` の subscribe gating を外す → **SUBWIRE-04 RED**（未購読でも板が出て assertion が vacuous 化）。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
        -executeMethod LiveSubscribeWiringE2ERunner.Run -logFile /tmp/live_subscribe.log
# expect: [E2E LIVE SUBSCRIBE PASS] / exit=0  （確認は Bash `grep -a "E2E LIVE SUBSCRIBE"`）
```
