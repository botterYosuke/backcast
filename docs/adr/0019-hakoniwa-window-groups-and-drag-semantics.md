---
status: accepted
---

# Hakoniwa window group とドラッグ移動セマンティクス（persistent `groupId`／一体移動／swap／detach／ghost）

owner 依頼（2026-06-21）「floating-window のドラッグ移動仕様を変える: ①くっついている window があるなら group 一体移動 ②`startup` / `run_result` を含む group は Hakoniwa group として全体移動禁止 ③強く引き剥がすと個別 window に分離 ④Hakoniwa group 内のドラッグは group 全体移動ではなく位置の入れ替え ⑤入れ替え／引き剥がしのドラッグ中はドラッグ後の状態をゴースト表示」を `grill-with-docs`（2026-06-21・owner HITL Q1–Q9）で設計ロックした決定。

これは [[ADR-0017]] Decision 2（owner-locked「グループ化・一体移動・detach 状態は持たない——常に各 window 独立」）と ADR-0017 §6（owner-locked「スキーマ追加 0」）と findings 0075 §1（「snap は関係を保存しない」）を **正面から覆す**ため、新規 ADR で明示記録する（非可逆・将来の読者が驚く・実トレードオフの 3 条件を満たす）。

ADR-0017・ADR-0018 ともに自己保護条項を持つため、両 ADR は **無改変**で本 ADR が差分だけを明示 supersede する流儀（[[ADR-0018]] が ADR-0017 を 2 点 supersede した先例と同型）。

関連: [[ADR-0017]]（Hakoniwa ドッキング化・本 ADR が D2 と §6 を supersede）／[[ADR-0018]]（深さプレーン分離・cross-plane snap 禁止・本 ADR と無矛盾＝group も plane に閉じる）／[[ADR-0003]]（capability parity・無矛盾＝本 ADR は capability surface に group / swap / detach / ghost を追加）／findings 0075（ドッキング化下位設計・本 ADR が §1 結合なしを supersede）。実装下位事実は findings 0082。

## Context

ADR-0017 は Hakoniwa を「磁石スナップでくっついた集合・結合なし・各 window 常に独立」と決め、release 時に辺を揃えるだけで group 概念は持たないモデルにした。これは TTWR の旧 split-grid（slot/swap）からの意図的逸脱で、`FloatingWindowMath.SnapOffset` の pure 算術 + `SnapOnRelease` の release-only commit という最小設計を生んだ。

owner は使ってみた結果、以下の不足を挙げた:

1. **隣接 window を一体的に動かしたい**: chart と orders をくっつけて配置した後、chart の title bar を drag したら orders も付いてきてほしい（実際は ADR-0017 では chart 単独しか動かないので、配置を維持したまま位置だけ変える操作が「全 window を 1 つずつ動かす」になる）。
2. **Hakoniwa の核は固定したい**: `startup` / `run_result` を含むクラスタは "ワークスペースの本陣" なので、不意に全体が動くのは嫌。代わりに内部の並びを **swap** で組み替えたい（旧 split-grid の tile-swap 精神を Hakoniwa に閉じ込めて復活）。
3. **引き剥がしも可能にしたい**: くっついた window を強く drag したら group から外して単独に。
4. **どうなるか事前に見せたい**: swap / detach は結果が drag 終了時に分かるが、release してから「やっぱり違う」と戻すのは UX が悪い。ドラッグ中にゴーストで結果を予告したい。

これらは ADR-0017 の「結合なし」と直接矛盾する。ADR-0017 はこの問題が起きたら新 ADR で supersede する設計（自己保護条項）。本 ADR がその supersede。

ADR-0018 が定めた **2 プレーン分離（dock 奥 1.0倍 / floating 手前 1.2倍）と cross-plane snap 禁止** は本決定と無矛盾——group 概念もプレーンに閉じる（cross-plane group は構造的に作れない＝controller が plane ごとに分かれているため）。restore 時の cross-plane group fail-safe だけが追加の整合作業。

## Decision

ADR-0017 Decision 2／§6 と findings 0075 §1 を、以下の **9 点**で supersede する（他は不変＝split-grid 退役・mode 別配置廃止・chart universe 同期・dead schema 不読み・plane 分離・cross-plane snap 禁止 はすべて維持）。

1. **window group を `floatingWindows[*].groupId: string?` で persist する**（ADR-0017 §6「スキーマ追加 0」を **groupId 1 フィールドだけ** supersede）。GUID `grp_<hex32>` 形式、null は単独、optional additive field なので version bump 無し。group メンバーシップの **唯一の真実源**は groupId（座標から再導出しない）。

2. **group の定義 = 同一 `groupId` を共有する visible/live window ≥ 2**。singleton は group 不成立。`groupId` を持つ単一 visible window は通常の単独 window として扱う。

3. **[[Hakoniwa group]] の定義 = group ∧ core（`startup` ∨ `run_result`）を visible で含む**。drag 開始時点で動的再評価（mode-switch で昇格/降格しうる）。

4. **attach トリガ = flush 隣接判定**: release commit / dock spawn 後、`|dragged.edge - other.opposite_edge| ≤ 1px` ∧ 直交軸 overlap > 0 を満たす window と attach（merge or 新規 GUID 生成）。same-edge 整列だけ（離れて並んだ状態）では attach しない。

5. **merge 衝突解決 cascade**: Hakoniwa group 単独 → size 最大 → 辞書順最小 → 新規 GUID。Hakoniwa identity を保護する優先順位。

6. **drag mode 判定 = `D_DETACH = 64f` canvas-logical px**（pure 定数・`FloatingWindowMath` 側）。`|cursor - rest| > D_DETACH` で detach 状態へ遷移。zoom 非依存。距離一本（速度・修飾キー・方向条件なし）。

7. **操作タイプ別の commit セマンティクス**（commit はすべて release 一括＝snap-on-release の精神を継承）:
   - 単独 window: 通常 drag→snap（既存 #15／ADR-0017 §1 のまま不変）
   - 通常 group・`< D_DETACH`: group 全体 translate（実描画で平行移動・ghost 無し）
   - 通常 group・`≥ D_DETACH`: dragged のみ detach（dragged.groupId=null・残 visible/live 1 なら連鎖 dissolve）
   - Hakoniwa group・`< D_DETACH`・target あり: `(x,y,w,h)` 4 値 swap（kind/id 不変・group footprint 不変）
   - Hakoniwa group・`< D_DETACH`・target なし: snap-back（dragged は元 rest）
   - Hakoniwa group・`≥ D_DETACH`・非 core: detach commit
   - Hakoniwa group・`≥ D_DETACH`・core: detach 不可（rest snap-back・core の identity 保護）

8. **drag ghost = post-release プレビュー**（alpha 0.45・kind accent border・dragged solid / target dashed）。実装は別 GameObject（pool）・drag 中だけ実 window より前面 sibling。commit-on-release の規律で drag 中は groupId・geometry 不変（通常 group translate のみ実描画）。

9. **cross-plane group は禁止**（ADR-0018 plane 分離と整合）。runtime では controller が plane ごとに分かれており構造的に起きない。restore 時に doc 編集 / 旧 build 後方互換で同一 `groupId` が両 plane に跨ったら、多数派 plane を残し・同数なら dock plane を優先し・負け側 member は `groupId=null` に分割（fail-safe）。

programmatic Close（universe sync 削除等）は detach と同型に **連鎖 dissolve**（残 visible/live 1 で残 1 も null）。programmatic Spawn（`SpawnDockedToFocus` 等）は `groupId=null`（attach はユーザの drag-release が唯一の起点＝「ユーザが望んだ時だけ group」）。**Hide（mode-switch hide）は groupId 温存**（Replay/Live 行き来で group 復活可能）。

## Considered Options

- **採用：persistent `groupId` ＋ flush attach ＋ Hakoniwa-priority merge ＋ swap/detach 双方の release-commit ＋ post-release ghost**。owner Q1–Q9 で全分岐を選択。pure 算術（`FloatingWindowMath`）と controller commit / ghost UI の薄い分離で AFK 検証可能。
- **不採用：座標から group を再導出（state なし）**。「強く引き剥がして隙間が空いたら自動解消」は嬉しいが、Hakoniwa identity（同じ "本陣" の連続性）が一切保存できず Q2 の core 概念が成立しない。owner Q1 で persist 選択。
- **不採用：辺情報（adjacency edges）を persist**。「どの辺で接続していたか」をモデルに持つ。group 関係を直接表現できるが、edge 整合性管理（window 削除時の edge 掃除）と JSON 読みづらさが嵩む。owner Q1 で「辺情報は保存しない・group membership だけ」選択。
- **不採用：通常 group も swap（split-grid 全復活）**。Hakoniwa 以外も swap にすれば挙動が一様だが、ADR-0017 split-grid 退役の意図（free placement）に反する。owner は「通常 group は一体移動、Hakoniwa group だけ swap」と明示分岐。
- **不採用：detach を速度ベース or 修飾キー**。pure 算術で AFK 検証可能な距離一本に統一（Q3）。
- **不採用：drag 中 commit（detach を閾値 cross で即適用）**。snap-on-release（ADR-0017 §1）の精神と整合する release commit で統一（Q5-b）。
- **不採用：全操作 ghost-only（通常 group translate もゴーストで予告）**。実描画で動かす方が自然（owner Q5-a）。

## Consequences

- **CONTEXT.md glossary** を本決定に整合（同時更新）: `Hakoniwa`（ADR-0017→0018→0019 履歴付き）／`floating window`（ADR-0019 で group 関係を持つ、cross-plane group 禁止を追記）／**新設**: `window group / groupId`／`Hakoniwa group`／`core member`／`D_DETACH`／`swap drop target`／`drag ghost`。
- **永続スキーマ**: `FloatingWindowLayout` に `groupId: string?` 追加（既存 doc は null として読まれる・forward tolerance）。version bump 無し。両 controller（`_dockWindows`・`_windows`）の Capture/Apply に groupId pass-through を実装。
- **pure 算術の追加**: `FloatingWindowMath` に `IsFlushAdjacent(a, b, eps)` / `EvaluateDragMode(rest, cursor, D_DETACH)` / `ResolveDropTarget(cursor, groupMembers, dragged)` / merge cascade の純関数を追加（headless・AFK 権威）。
- **controller の拡張**: `FloatingWindowController` に group lifecycle（attach commit / merge / detach commit / dissolve / Close 連鎖）を追加。group メンバーの translate batch・swap commit（`(x,y,w,h)` 4 値交換）も実装。
- **input 層の拡張**: `FloatingWindowTitleInput` に drag mode 判定（通常 group translate / Hakoniwa swap / detach / snap-back）と ghost 表示の orchestration を追加。
- **ghost 描画層の新設**: `DragGhostLayer`（仮称）= drag 中だけ存在する半透明オーバーレイ。kind accent border + dragged solid / target dashed。pool で reuse。
- **restore の cross-plane 分割**: `BackcastWorkspaceRoot.RestoreFloating` に「同一 groupId が両 plane に跨ったら多数派 plane 残し・同数なら dock 優先・負け側 null」の fail-safe ロジックを追加。runtime では起きないが手編集 doc / 旧 build 互換のため。
- **AFK 正本の拡張**: `FloatingWindowE2ERunner` に「groupId persistence / round-trip」「flush attach / same-edge no attach」「Hakoniwa-priority merge」「通常 group translate」「detach threshold + dissolve」「Hakoniwa swap (x,y,w,h)」「core detach 不可」「ghost preview composition」「cross-plane restore split」「Close 連鎖 dissolve / Spawn null / Hide preserve」の section を追加（実装着手前に `behavior-to-e2e` を formal invoke 済み予定）。
- **下位事実は findings 0082 に固定**: 設計の木 §0–§13。本 ADR は方針のみで実装下位事実（GUID 生成・ghost prefab 構造・閾値定数の置き場・pool サイズ等）は書き戻さない。
- **#15 / #99 / #103 の AFK は既存挙動互換**: 単独 window drag・通常 snap-on-release・2 プレーン分離・cross-plane snap 禁止 はすべて維持。ADR-0019 は「上に重ねる」変更で、既存 section を破壊しない。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
`D_DETACH` の最終値・ghost の alpha / border 仕様・GUID 形式・merge cascade の同点 tie-break 詳細・cross-plane 分割の負け側処理の細部などの下位事実は本 ADR に書き戻さず、`docs/findings/0080` に記録し本 ADR を「方針: ADR-0019」として参照する。
