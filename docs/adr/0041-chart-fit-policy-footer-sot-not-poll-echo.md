---
status: accepted
---

# チャート fit-all 方針は C# フッターモード（C# 側の正本）で決める — poll `execution_mode`（写し）を正本にしない

`improve-codebase-architecture`（2026-06-30, owner 承認）で導出。#156/#182 の試行錯誤的修正を
リファクタリングする中で出た候補 B（「Replay/Live の fit 判定をエンジンに一本化」）の取り扱いを記録する。

## 背景

チャートの「Replay は cold-load した全期間を fit-all 表示、Live は 6px 右端寄せ」という方針は、概念上
**1 つの "Replay-ness 判定"** が 2 つのランタイムに現れる:

- **Python（clip）**: 再生カーソルで各 per-id 系列を切り詰めるか。`self._mode == "replay"` ゲート。
  #182 リファクタで [[ReplayWindow]]（`engine.replay_window`）に抽出済み——Python 側の clip 正本はここ。
- **C#（fit）**: チャートを全期間 fit-all にするか。`DockShape.ShouldFitChartToAll(_footerMode.DisplayMode)`
  ＝フッターの選択モードを読む 1 行純粋述語（#156 follow-up で抽出・FITALL-WIRING-05 でゲート済み）。

候補 B の当初案は「C# も poll の `execution_mode` を読んで fit を決め、エンジンを単一権威にする」だった。
実コードを検証した結果、この案は**退化**であると判明した。

## Decision

**チャートの fit-all 方針は C# 側で `_footerMode.DisplayMode`（フッター = C# 側のモード正本/SoT）から決める。**
poll snapshot の `execution_mode` を C# fit の正本にしない。

理由（load-bearing）:

- **フッターが C# 側のモード正本**。ユーザーがフッターでモードを選び、それが Python の `mode_manager` へ
  push される。poll の `execution_mode` はその**フッターを Python が echo して返す派生値**で、最大 1 ポール分
  遅れる。fit を `execution_mode` から引くと「正本（フッター）を捨ててラグする写しを読む」ことになる。
- **clip と fit は別の決定**で、たまたま両方が "Replay か?" を問うだけ。両者を poll 越しに 1 値へ畳むと、
  綺麗な局所述語（`ShouldFitChartToAll`・テスト済み）を**越境依存＋ポールラグ semantics**へ置き換える——
  複雑さの**削減ではなく移動**になる。
- A（[[ReplayWindow]] 抽出）で、本当に散らばっていた **clip ロジックは既に Python で 1 箇所に集約済み**。
  fit 側は元から局所的に綺麗なので、無理に寄せる利得がない。
- ADR-0001（frontend は venue/mode 分岐を持たない）との擦れ: capability を Python が宣言し poll で運ぶ前例
  （`modify_is_cancel_replace`）はあるが、それは「frontend が venue 名で分岐しない」ための仕組み。fit は
  venue 非依存の純粋な表示方針で、C# 側のモード SoT から決めても ADR-0001 の意図（venue 知識を frontend に
  置かない）には反しない。

## Considered Options

- **採用：fit は C# フッター正本／clip は Python 正本（[[ReplayWindow]]）**。各ランタイムが自分の局所 SoT を
  読む。両者は同じ "Replay-ness" の 2 つの適用で、越境同期しない。現状そのまま。
- **不採用：C# が poll `execution_mode` を fit 正本にする**。正本（フッター）を捨ててラグする写しを読む退化。
  モード切替時に fit が 1 ポール遅れる semantics を持ち込み、テスト済みの局所述語を消す利得がない。
- **不採用：Python が `chart_fit_policy` を出し C# 述語＋ゲートを削除（エンジン権威版）**。方向としては
  「単一権威」だが、結局 fit 決定を Python に移して **ポールラグを足す**だけで、複雑さは移動。ADR-0001 擦れも
  解消しない（fit はもともと venue 非依存）。Unity ゲート再実行コストにも見合わない。

## Consequences

- `DockShape.ShouldFitChartToAll` と FITALL-WIRING-05 ゲートは**現状維持**（削除しない）。
- `BackcastWorkspaceRoot` の poll ループは引き続き `_footerMode.DisplayMode` から `fitAll` を導出する。
- clip 側の正本は `engine.replay_window.ReplayWindow`（A で新設）。fit と clip が "Replay か?" を別々に
  問うのは設計どおりで、重複ではない。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
将来のアーキテクチャレビューが「fit を poll execution_mode に寄せよう」と再提案したら、本 ADR を参照して退ける。
