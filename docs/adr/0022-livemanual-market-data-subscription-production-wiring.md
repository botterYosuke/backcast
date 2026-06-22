---
status: accepted
---

# LiveManual の本番購読配線と venue 実上限への委譲（人工 50 件 cap の撤去）

owner 依頼（2026-06-22・issue #107）「立花/kabu の LiveManual で sidebar universe の銘柄が live market-data 未購読になり、選択銘柄の Chart が更新されず板が `(no board)` のままになる不具合を直す。あわせて今後検知できるゲートを整備する」を受けた決定。grill HITL（2026-06-22）で 3 点を確定した:

1. **人工的な件数上限を撤去し、venue 実上限へ委譲する**（owner Q1）。
2. **回帰ゲートは full-stack（mock adapter・batchmode）**で本番経路を駆動する（owner Q2）。
3. **universe membership はユーザー所有で不可侵**——購読は membership に**従属**し、購読の都合で銘柄を足す/減らす/間引くことは一切しない（owner HITL 明示確認）。

## なぜ新規 ADR か

購読チェーンを起動する本番トリガの欠落（`LiveSubscribeHook` 未代入・本番呼出元ゼロ）は単なる配線バグで ADR 不要だが、その修正に伴う 2 つの下位決定は (a) hard-to-reverse、(b) コードを読んだ人が「なぜ？」と思う、(c) 実トレードオフがある、の 3 条件を満たす:

- **人工 50 件 cap の撤去**は `live_orchestrator.subscribe_market_data` の `_MAX_LIVE_SUBSCRIPTIONS = 50`（コメント「kabuステーション API 上限 (R6)。servicer 層で拒否する」）という**明文化された不変条件を正面から反転**する。この cap は ADR ではないので supersede 対象の ADR ファイルは無い——本 ADR がその servicer-cap 面を置換し、コード/findings の参照は本 ADR を指す。
- **production-binding gate の存在理由**（テスト自身が手動 subscribe してはいけない／実 `SelectRow` を駆動する）は、読者が「なぜ普通に subscribe を呼んで確認しないのか」と疑問に思う非自明な設計で、過去 12+ 回の invoke 漏れと同じ death-zone を塞ぐためのもの。

関連: findings 0086（下位事実・RED→GREEN・AFK 再走手順）／findings 0024（#31 が `LiveSubscribeHook` を DEFERRED seam として残した元実装・本 ADR が代入する）／findings 0053（`TachibanaLiveE2ERunner` の元設計・本 ADR が手動 self-subscribe を撤去）／ADR-0021（実行時再バインド venue・本 ADR の購読は再バインド後の bound venue に対して走る）。

## Context

板（立花 FD frame / kabu PUSH）と価格は銘柄を `subscribe_market_data` で購読して初めて WS で流れる。チェーン `SubmitSubscribeMarketData`(C#)→`subscribe_market_data`(orchestrator)→`runner.subscribe`→`adapter.subscribe({"trades","depth"})`→venue WS は**全段そろっていた**が、起動する本番トリガが無かった:

- `UniverseSidebarController.LiveSubscribeHook`（Live で行選択時に購読すべき seam）は **一度も代入されていない**（#31 の DEFERRED seam のまま）。
- `LiveRpcLanes.SubmitSubscribeMarketData` の**本番呼出元がゼロ**で、唯一の caller が `TachibanaLiveE2ERunner` 自身（テストが手動 carrier 銘柄を購読）＝production-binding の死角。
- LiveManual には universe 自動購読が無い。

`subscribe_market_data` は venue 非依存（runner/adapter 抽象経由）なので立花・kabu 双方が同じバグ。Positions/Buying Power が出ていたのは `get_portfolio` の REST poll 由来で WS 購読に依存しないため（非対称の説明）。

servicer 層には venue 非依存の人工 cap `_MAX_LIVE_SUBSCRIPTIONS = 50`（`live_orchestrator.py` + `_backend_impl.py` に重複）があり、超過を `SUBSCRIPTION_LIMIT_EXCEEDED` で拒否していた。だが (a) この 50 は kabu の実上限を venue 非依存に流用したもので、**立花（実上限なし）にも 50 で頭打ち**をかけてしまう人工制限であり、(b) kabu の実上限は adapter 側 `RegisterSet`→`KabuRegisterFullError(4002006)` で別途守られている。さらに `subscribe_market_data` は全例外を `SUBSCRIBE_FAILED` に握り潰すため、**kabu の typed 上限エラーが汎用エラーに化けて surface しない**。

## Decision

- **D1（本番購読配線）**: 購読配線を plain C# の `LiveSubscriptionCoordinator`（UnityEngine-free・AFK 権威）に集約する。`BackcastWorkspaceRoot` がこれを実 `LiveRpcLanes` backed の sink で構成する。Coordinator は:
  - **(a) 行選択 / [+ Add]（Live 時のみ）**: `UniverseSidebarController.LiveSubscribeHook` を代入し、当該銘柄を購読（Replay では発火しない）。
  - **(b) LiveManual 突入時**: universe（`InstrumentRegistry`）の**全銘柄を一括購読**。突入検知は poll の `execution_mode` が `LiveManual`/`LiveAuto` へ遷移したエッジ。
  - 既購読は dedup（idempotent）。**membership には一切書き込まない**（subscribe は membership に従属・D3）。
- **D2（人工 cap 撤去・venue 実上限へ委譲）**: `live_orchestrator` / `_backend_impl` の `_MAX_LIVE_SUBSCRIPTIONS` 人工 cap を**撤去**する。件数上限は venue adapter の実上限に委譲し、kabu の `KabuRegisterFullError(4002006)` 等の venue typed エラーを `SUBSCRIBE_FAILED` に握り潰さず、**専用 error_code（`SUBSCRIPTION_LIMIT_EXCEEDED` に venue コードを載せる）で surface** する。立花は実上限なし。一括購読は kabu の burst rate-limit（R5・4001006）を踏まないよう既存 `kabusapi_ratelimit` の register gate を通す**バッチ購読 RPC**で実装する（N 個別 write-lane RPC で order をブロックしない・kabu の累積 re-register を O(N²) にしない）。
- **D3（membership 不可侵）**: 購読は [[market-data 購読（subscribe）vs universe membership]] の従属操作。**システムは購読の都合で銘柄を add/remove/prune しない**。venue 実上限超過時も membership から銘柄を落とさず typed エラーで surface するだけ（#253 の prune 事故と同じ轍を踏まない）。
- **D4（production-binding 回帰ゲート）**: 実 `UniverseSidebarController.SelectRow` / universe 復元という**本番経路**を駆動し、`subscribe_market_data` 発火 → mock adapter が depth 注入 → DepthLadder `HasDepth=true` を assert する full-stack（mock adapter・venue-free・batchmode・CI 常時）AFK ゲートを置く。**テスト自身は手動 subscribe しない**（死角を kill する litmus）。本番配線（`LiveSubscribeHook` 代入 / 初回一括購読）を削除すると必ず RED（delete-the-production-logic litmus）。
- **D5（実 venue 検知 leg）**: `TachibanaLiveE2ERunner` の手動 carrier subscribe を撤去し本番トリガ（universe 投入→選択/一括購読）に置換。kabu 側にも同等の live leg（`KabuLiveE2ERunner`）を新設。実 demo 資格情報・場中が必要なため HITL。

## 不採用

- **不採用：人工 cap を残す**（owner Q1 で撤去を選択）。venue 非依存 50 は立花を不当に頭打ちし、AC#1「人工的な件数上限でサイレント間引きしない」に反する。
- **不採用：軽量 pure-C# ゲート（Python-free）**（owner Q2 で full-stack を選択）。issue 本文は「Python-free」と書いたが、owner は本番に近い高忠実ゲート（実 subscribe RPC→Python→板表示まで通す mock adapter batchmode）を選んだ。**本 ADR D4 が issue 本文の「Python-free」記述を supersede する**。
- **不採用：購読の都合で universe を間引く / 自動補充する**（owner HITL で membership 不可侵を明示・D3）。
- **不採用：複数 venue 同時購読**。engine は単一 venue 前提（ADR-0021）。購読は現 bound venue に対してのみ。

## Consequences

- **engine**: `subscribe_market_data` から人工 cap 判定を撤去。venue typed 上限エラーを catch して専用 error_code に map。バッチ購読 RPC（`subscribe_market_data_batch`）を新設し runner で gather（venue rate-limit を尊重）。`_backend_impl` の重複定数も削除。
- **C#**: `LiveSubscriptionCoordinator`（新規 pure class）。`LiveRpcLanes` にバッチ購読メソッド追加。`BackcastWorkspaceRoot` が Coordinator を構成し poll の mode 遷移エッジを供給。`UniverseSidebarController.LiveSubscribeHook` が本番代入される。
- **E2E**: production-binding gate（新規 AFK runner・mock adapter・CI 常時）。`TachibanaLiveE2ERunner` の self-subscribe 撤去。`KabuLiveE2ERunner` 新設（HITL）。
- **back-compat**: 既存 Replay 経路は購読を発火しない（Live-gate）ので不変。`subscribe_market_data` の単数 RPC は残し row-select が使う。
- **AFK 正本の拡張**: 下位事実・RED→GREEN・AFK 再走手順は findings 0086 に固定。実装着手前に `behavior-to-e2e` を formal invoke 済み。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。下位事実（mode 遷移エッジ検出の細部・バッチ購読の per-id 結果集約形・venue typed エラーの error_code map・gate の section 構成）は本 ADR に書き戻さず findings 0086 に記録し本 ADR を「方針: ADR-0022」として参照する。
