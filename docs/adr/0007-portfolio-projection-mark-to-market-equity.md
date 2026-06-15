---
status: accepted
decision: portfolio projection の equity を mark-to-market（cash＋建玉×最新値）に倒し、cash / equity /
  buying_power を別フィールドとして分離する（Replay と Live で同義・ソースだけ違う）
accepted-date: 2026-06-15
accepted-by: owner
relates-to: ADR-0006（nautilus runtime 退役・golden 凍結 fixture 化）を前提とし、その golden を本 ADR の
  equity 変更に合わせて kernel characterization として再生成する。ADR-0004（pure-Python kernel）/ findings
  0011（Live の #25 MTM 接続）と整合。
---

# Replay/Live の口座評価額(equity)を mark-to-market に統一し、cash / equity / buying_power を分離する

`grill-with-docs`（2026-06-15・kabu / tachibana 両 venue スキルで余力・建玉時価の権威ソースを確認）で確定。

## Context（現状の取りこぼし）

1. **projection が 3 フィールドを潰している**。`engine.strategy_runtime.portfolio.compute_portfolio` は
   `buying_power = cash = equity = last_equity` と**同一値**で返す。さらにその `last_equity` は
   per-bar equity point ＝ `portfolio.cash`（[[Backcast Execution Kernel（kernel）]] の `ReplayKernelObserver.on_equity`
   が現金を書く）なので、**equity が現金そのもの**になっている。
2. **kernel は既に 2 つの equity を持つ**。`Portfolio.equity`（==cash・sink/oracle の `account.balance_total`
   ビュー＝建玉を時価評価しない買付余力的な値）と `Portfolio.mark_to_market_equity(prices)`（cash＋Σ建玉×price）。
   後者は post-trade rail で使用中（`runner.py`）だが、**get_portfolio の projection には流れていない**。
3. **両ライブ venue は cash・余力・建玉時価を分けて持ち、口座評価は時価ベース**（venue 権威ソース）:
   - kabu: `/wallet/cash`（現物余力）＝余力の権威、`/positions` ＋ `/board` の `CurrentPrice` で建玉時価。
   - tachibana: `CLMZanKaiKanougaku`（買余力）＝余力の権威、建玉リスト＋時価で評価。
   → live の口座評価額は本来 **mark-to-market**（現金＋建玉×最新値）。Replay もこれに揃えるのが筋で、
   建玉を保有したまま run が終わると現状 `equity == cash` は**建玉価値を取りこぼす**（drawdown/sharpe も歪む）。
4. **golden の equity は現状 cash 基準**。`Portfolio.equity` の docstring どおり #24 golden の equity 列は
   nautilus oracle の CASH 口座 `balance_total`（建玉非評価）と一致する値で凍結されている。

## Decision

- **equity = mark-to-market**。portfolio projection の `equity` を `cash ＋ Σ(建玉 × 最新値)` にする。Replay の
  「最新値」は各銘柄の `bar.close`（毎 bar・全銘柄 `last_prices[iid]=bar.close` を追跡＝`rails_active` に限定しない）。
  最終 equity も per-bar equity point も `mark_to_market_equity` 由来にする。
- **cash / equity / buying_power を分離**（潰さない）。同じ projection・**Replay と Live で同義**だがソースが違う:

  | フィールド | 意味 | Replay ソース | Live ソース（venue 権威） |
  |---|---|---|---|
  | `cash` | 実現現金 | `portfolio.cash` | venue 現金 |
  | `equity` | 口座評価額 ＝ cash＋Σ(建玉×最新値) | `mark_to_market_equity({iid: bar.close})` | venue 口座評価 / cash＋建玉×CurrentPrice |
  | `buying_power` | 買付余力 | `cash`（CASH 口座・現状） | venue 余力（kabu `/wallet/cash`・tachibana `CLMZanKaiKanougaku`）が権威 |
  | `positions` | 建玉(qty/avg_px/含み損益) | `bar.close` 評価 | venue `/positions` @ CurrentPrice |

- **golden 再生成 → kernel characterization fixture として再凍結**。equity を MTM 化すると #24 golden の
  equity / drawdown / sharpe が変わるため再 capture が必須（owner 承認済）。ADR-0006 で nautilus は退役済み＝
  **本機ではオラクル再実行不可（precision crash）**なので、再生成 golden は「oracle 検証済み」ではなく
  **kernel 出力の characterization fixture**（認識論的格下げ）。faithfulness は引き続き known-symbol
  data-equivalence（ADR-0006）で担保し、golden は回帰固定の役割に徹する。

## Considered Options

- **(A) equity = MTM（採用）** — live の口座評価と一致・建玉保有時も正しい・kernel が既に MTM を計算済み。
  代償は golden 再生成 1 回。
- **(B) cash 基準を維持** — golden 据え置きで済むが、建玉を保有したまま終わる戦略で equity が現金しか映さず
  live と乖離。drawdown/sharpe が口座評価ベースにならない → 不採用。
- **(C) equity を projection から外す** — 「正しくないなら出さない」案。だが UI は口座評価額を必要とし、
  live は既に MTM を出している。Replay だけ欠落は非対称 → 不採用。

## Consequences

- **C# decoder は無改修**。`PortfolioResult` は既に `cash` / `equity` / `buying_power` を**別フィールド**で持つ
  （`_backend_impl.py`）。今回は値の出し分けであってスキーマ変更ではない。
- `compute_portfolio` の collapse 解消、per-bar equity point の MTM 化（`ReplayKernelObserver.on_equity` /
  runner の equity 算出経路）、`last_prices` の毎 bar 追跡が実装スコープ。下位の RED→GREEN 手順・列対応・
  golden 再生成 provenance は当該スライスの `docs/findings/` に記録し本 ADR を「方針: ADR-0007」として参照する。
- drawdown / sharpe が口座評価ベースになり、live（findings 0011・#25 の MTM 接続）と意味が一致する。
- 用語は CONTEXT.md「[[口座評価額(equity) / 現金(cash) / 買付余力(buying_power)]]」で固定（equity と cash の
  混同を _Avoid_）。

## 自己保護

本 ADR の Decision が確定したら固定する。覆す場合は本ファイルを編集せず supersede する新規 ADR を起こす。
スライス内で確定する下位事実（per-bar MTM の算出位置・golden 再生成手順・buying_power の venue 別ソース詳細等）は
本 ADR に書き戻さず当該スライスの `docs/findings/` に記録し、本 ADR を参照する。golden の再生成は ADR-0006 の
「凍結 fixture・nautilus を起こさない」方針に従い、kernel characterization として行う（oracle を復帰させない）。
