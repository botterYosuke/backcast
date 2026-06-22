# 0086 — #107 LiveManual market-data 購読の本番配線＋検知ゲート

方針: **ADR-0022**（LiveManual の本番購読配線と venue 実上限への委譲）。用語: CONTEXT.md [[market-data 購読（subscribe）vs universe membership（#107）]] / [[live market-data 購読の本番配線（#107）]]。

grill HITL（2026-06-22）。owner all-in 指示「実装コストは度外視して理想的な完成形をめざせ。手を抜くなよ」＋ Q1=人工 cap 撤去 / Q2=full-stack 回帰ゲート / HITL=membership 不可侵。

## 真因（コード裏取り済み）

| 要素 | 現状 | file:line |
| :--- | :--- | :--- |
| `LiveSubscribeHook` | 宣言のみ・本番未代入（#31 DEFERRED seam） | `UniverseSidebarController.cs:43,102` |
| `SubmitSubscribeMarketData` 本番呼出元 | **ゼロ**（唯一 caller が E2E ランナー自身） | `LiveRpcLanes.cs:181` / `TachibanaLiveE2ERunner.cs:157` |
| LiveManual 一括購読 | **無し** | — |
| 人工 50 件 cap | venue 非依存・**テスト無し**・重複定義 | `live_orchestrator.py:906,917-921` / `_backend_impl.py:448` |
| kabu 実上限 | adapter 側で別途 typed エラー | `kabusapi_register.py:51`→`KabuRegisterFullError(4002006)` |
| 立花 実上限 | **無し**（per-ticker WS hub） | `tachibana.py:390` |
| venue typed エラーの surface | `subscribe_market_data` が全例外を `SUBSCRIBE_FAILED` に握り潰す | `live_orchestrator.py:928-930` |
| mock adapter（gate 用） | 既存・`subscribe`/`inject_tick`/`emit_depth_snapshot` あり | `python/.../mock_adapter.py` |
| adapter factory 注入 seam | `build_live_adapter_factory` monkeypatch | `live_orchestrator.py:122,323` |
| depth → UI flow | adapter push → bridge → `get_state_json` poll → `DepthDecoder.Decode` → `DepthSnapshotView.HasDepth` | `DepthLadderE2ERunner.cs` |

## 設計の木（下位決定）

- **F1（配線の置き場）**: plain C# `LiveSubscriptionCoordinator`（UnityEngine-free・AFK 権威）に集約。MonoBehaviour `BackcastWorkspaceRoot` に直書きしない（gate が headless 駆動できる・[[pair-relay-optional-for-grilled-csharp-slices]] の AFK probe gate 流儀）。Coordinator は subscribe sink（interface）+ universe registry + 「現在 Live か」を受け、(a) `LiveSubscribeHook` 実装、(b) LiveManual 突入エッジで一括購読、を担う。
- **F2（LiveManual 突入の検知）**: poll の `execution_mode` が Replay→{LiveManual,LiveAuto} へ変わった**立ち上がりエッジ**で一括購読。`FooterModeViewModel.ApplyPoll` が既に `DisplayMode` を持つので、`BackcastWorkspaceRoot` の poll ループでエッジを取り Coordinator へ渡す。Live→Live（LiveManual⇄LiveAuto）は再発火しない（既購読 dedup で二重防御）。
- **F3（一括購読のバッチ化）**: `subscribe_market_data_batch(ids)` を新設。orchestrator が runner 経由で gather し per-id 結果を返す。理由: (1) N 個別 write-lane RPC が order 操作をブロックしない、(2) kabu は `adapter.subscribe` が毎回累積 `_put_register(all_symbols())` を撃つので per-id だと O(N²) symbol 送信＋burst で `4001006`（R5）。バッチで register gate（`kabusapi_ratelimit`）を尊重しつつまとめる。row-select / [+ Add] は従来の単数 `subscribe_market_data` を使う。
- **F4（人工 cap 撤去＋typed surface）**: `_MAX_LIVE_SUBSCRIPTIONS` 判定を orchestrator/`_backend_impl` から撤去。`subscribe_market_data` / batch で venue typed 上限エラー（`KabuRegisterFullError` 等）を catch し、汎用 `SUBSCRIBE_FAILED` ではなく `SUBSCRIPTION_LIMIT_EXCEEDED`（venue コード付帯）で返す。その他の例外は従来どおり `SUBSCRIBE_FAILED`。立花は無制限なので per-ticker WS が universe 件数ぶん開く点だけ留意（owner 公認・人工 cap は付けない）。
- **F5（membership 不可侵）**: Coordinator も orchestrator も `InstrumentRegistry` / `scenario.instruments` に**書き込まない**。購読失敗・上限超過でも membership から銘柄を落とさない。prune は #41 専用 gate のみ。
- **F6（production-binding gate / D4）**: full-stack AFK runner（mock adapter を `build_live_adapter_factory` 経由で embedded Python に注入・venue-free・CI 常時）。実 `UniverseSidebarController.SelectRow` / universe 復元を駆動 → 実 subscribe RPC → mock adapter が購読受領＋depth 注入 → poll → `DepthDecoder` → `HasDepth=true` を assert。**テスト自身は `SubmitSubscribeMarketData` を呼ばない**。litmus: `LiveSubscribeHook` 代入と一括購読を消すと RED。
- **F7（実 venue 検知 leg / D5）**: `TachibanaLiveE2ERunner` の手動 carrier subscribe（:157）を撤去し本番トリガ（universe 投入→選択/一括購読）へ置換。`KabuLiveE2ERunner` を新設（同型・demo verify port・HITL）。

## 実装着地（2026-06-22）

**Python（venue 非依存）**
- `adapter.py`: 新 `SubscriptionLimitExceeded(venue_code)`（venue 非依存上限例外）。
- `live_runner.py`: `subscribe_many(ids)`（逐次・per-id (id,ok,error_code) 集約・F3）。
- `kabusapi.py` `subscribe`: `KabuRegisterFullError` → `SubscriptionLimitExceeded(venue_code=4002006)` に翻訳（F4）。
- `live_orchestrator.py`: 人工 cap（`_MAX_LIVE_SUBSCRIPTIONS`＋`already` 計算）撤去。`subscribe_market_data` が
  `SubscriptionLimitExceeded` を `SUBSCRIPTION_LIMIT_EXCEEDED` で surface（汎用 `SUBSCRIBE_FAILED` に握り潰さない）。
  新 `subscribe_market_data_batch(ids)`（dict: success/error_code/results・F3）。
- `_backend_impl.py`: 重複 `_MAX_LIVE_SUBSCRIPTIONS` 撤去・`subscribe_market_data_batch` delegate。
- `backend_service.py` / `inproc_server.py`: batch RPC を dict 素通しで C# 境界へ公開。
- `spike/live_adapter/mock_inject.py`: gate 用 `emit_depth_for(server, instrument_id, i, bid, ask)`（subscribe-gated）。

**C#（本番配線）**
- `LiveSubscriptionCoordinator.cs`（新・plain・UnityEngine/pythonnet-free）: (1) `OnModePoll` rising edge → `BulkSubscribeUniverse`、
  (2) `OnLiveRowSelected` = `LiveSubscribeHook`。Changed 自動購読は採らない（hook を non-redundant に保つ・F1）。membership 不可侵。
- `LaneSubscribeSink.cs`（新）: `ISubscribeSink` → 実 `LiveRpcLanes`（host.Lanes 遅延解決）。
- `LiveRpcLanes.cs`: `SubmitSubscribeMarketDataBatch`（write lane・PyList→`subscribe_market_data_batch`）。
- `BackcastWorkspaceRoot.cs`: `_subCoord` 構築（実 sink + `_scenario.Universe`）・`LiveSubscribeHook` 本番代入・`DriveFooter` で `OnModePoll` 給餌。
- `UniverseSidebarController.cs`: `AddFromPicker` が Live で `LiveSubscribeHook` 発火（AC#2 [+ Add]）＋ field doc 更新。

## RED→GREEN（確認済み 2026-06-22）

- [x] production-binding gate `LiveSubscribeWiringE2ERunner`（full-stack MOCK・AFK）: **PASS**（SUBWIRE-01..04）。
      litmus: `BulkSubscribeUniverse` を no-op 化 → **SUBWIRE-01 RED**（"board did not render after LiveManual entry"）を実機確認 → 復帰。
      （exit=139 は MOCK live 系 runner 共通の shutdown segfault 環境ノイズ＝`OrderTicketE2ERunner` も同値。verdict は PASS/FAIL タグ。）
- [x] cap 撤去の Python seam `test_subscribe_market_data_batch.py`: **5 passed**。60 銘柄でも人工 cap で弾かれない（litmus）／
      venue 実上限のみ typed surface／立花無制限／kabu `KabuRegisterFullError`→`SUBSCRIPTION_LIMIT_EXCEEDED`／1 銘柄失敗が他を止めない。
- [x] batch RPC: per-id 結果集約・逐次で kabu register gate を尊重（pytest）。
- [x] 既存テスト一式 GREEN（`test_kabu_register_cap` / `test_live_auto_lifecycle_inproc_server` / `test_venue_mismatch_inproc_server` /
      `test_orchestrator_exposes_ec_ws_subscribed_in_state` / `test_health_watchdog_live` ＝20 passed）。compile gate `error CS` 0 件。
- [x] controller half `UniverseSidebarE2ERunner` Section6: **PASS**（row-select + [+ Add] が Live で hook 発火・Replay 不発）。
- [ ] 立花 / kabu live leg（HITL・demo・場中）: owner 手元。`TachibanaLiveE2ERunner`（self-subscribe 撤去→本番トリガ）／`KabuLiveE2ERunner`（新・Windows・verify）。

## AFK 再走手順

```
U="/Applications/Unity/Hub/Editor/6000.4.11f1/Unity.app/Contents/MacOS/Unity"
# production-binding gate（full-stack MOCK・CI 常時）
"$U" -batchmode -nographics -quit -projectPath /Users/sasac/backcast \
     -executeMethod LiveSubscribeWiringE2ERunner.Run -logFile /tmp/subwire.log
grep -a "E2E LIVE SUBSCRIBE" /tmp/subwire.log     # expect [E2E LIVE SUBSCRIBE PASS]（exit 139 は shutdown ノイズ・タグが verdict）
# controller half（Python-free）
"$U" ... -executeMethod UniverseSidebarE2ERunner.Run -logFile /tmp/sidebar.log   # [E2E UNIVERSE SIDEBAR PASS] / exit 0
# Python seam
uv run pytest tests/test_subscribe_market_data_batch.py -q
# compile-only: -executeMethod を外して error CS 0 件
# 実 venue leg（HITL・owner）: TachibanaLiveE2ERunner.Run（demo・場中）／KabuLiveE2ERunner.Run（Windows・本体・verify・場中）
```
注意: `.cs` 編集直後の初回 `-executeMethod` は recompile で実行されないことがある（2 回目で走る）。確認は Bash `grep -a`。

## code-review(high) 適用（2026-06-22）

3 finder（correctness / cleanup・altitude / conventions・gate-非vacuity）で計 17 候補 → verify。修正:
- **correctness**: `subscribe_many` の per-id catch を `except BaseException` → `except Exception` に（`asyncio.CancelledError` を握り潰さず teardown の cooperative cancel を保つ。単数 `runner.subscribe` は re-raise する契約に揃える）。
- **coverage（Medium）**: orchestrator `subscribe_market_data_batch` の dict 組立（precondition / empty / first_err / venue_sm→SUBSCRIBED）を inproc MOCK で pytest 追加（3 本）。
- **doc 正確化**: `LaneSubscribeSink` / `BackcastWorkspaceRoot` のコメントが存在しない「universe-Changed 再発火」を参照していたのを single-trigger 設計に整合（AC#6 litmus を守る maintainer 誤誘導を防止）。`LiveRpcLanes` batch の「N 個別 RPC で order をブロックせず」根拠を訂正（実際は 1 タスクで write lane を占有・狙いは O(N²)→O(N) と burst 回避）。
- **cleanup**: gate の `EmitAndWaitDepth`/`DepthEverRenders` 重複を 1 本に統合。venue-leg PASS 文言を「coordinator を駆動（root の配線は MOCK gate が gate）」に正確化。
- **REFUTED/意図的（非修正）**: mark-before-send 汚染（実 rising edge では lanes 常に ready・Replay 往復で回復・OnResult からの un-mark は cross-thread 化するので不可）／立花 60s 診断（fire-and-forget 本番トリガの意図・EC WS gate が捕捉）／batch timeout（rate-limit に対し十分）／kabu full-state ログ（既存 tachibana runner と同等）。

最終: pytest 41 passed・compile `error CS` 0・production-binding gate PASS（litmus RED 実機確認済み）。
