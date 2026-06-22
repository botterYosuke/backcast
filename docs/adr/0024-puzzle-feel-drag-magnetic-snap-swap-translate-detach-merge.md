---
status: accepted
---

# パズルゲーム体感のドラッグセマンティクス（in-drag 磁石吸着・cursor 位置で swap/translate/detach 動的判定・release で merge）

owner 依頼（2026-06-22）「floating window のドラッグを "プルン" とくっつくパズルゲーム体感にする: ①磁石吸着をもっと強く（in-drag に発動）／②ドラッグ中に post-state プレビュー・MouseUp で確定・ESC でキャンセル／③他 window に重ねたら入れ替え（プレビュー付き）／④グループの 1 つを drag したら島ごと移動／⑤強く引き剥がしたら孤立／⑥重なり docking 禁止・辺と辺がくっつき条件不成立ならキャンセル」を `grill-with-docs`（2026-06-22・owner HITL Q1–Q18）で設計ロックした決定。

これは **[[ADR-0019]] Decision 3 / 5 / 6 / 7 / 8 を点 supersede** する（D1 groupId persist / D2 group=≥2 / D4 flush attach / D9 cross-plane group 禁止 は維持）。ADR-0019 / 0020 ともに自己保護条項を持つため、両 ADR は無改変で本 ADR が差分だけを明示 supersede する（[[ADR-0018]] → ADR-0017、[[ADR-0019]] → ADR-0017、[[ADR-0020]] → ADR-0019 の先例と同型）。

関連: [[ADR-0019]]（D3/D5/D6/D7/D8 を本 ADR が supersede・D1/D2/D4/D9 は維持）／[[ADR-0020]]（first-launch factory grouping は維持・本 ADR と無矛盾）／[[ADR-0018]]（plane 分離・cross-plane snap 禁止は維持）／findings 0082（ADR-0019 下位事実・本 ADR で旧くなる項は findings 0088 が訂正）／**実装下位事実は findings 0088**。

## Context

ADR-0019 は Hakoniwa の "本陣 identity" を保護するため、core（startup / run_result）を含む group を **Hakoniwa group** として特別扱いし（D3）、全体 translate を禁止・内部 swap 限定・core detach 不可（D7）の二系統 drag mode を持っていた。owner は使ってみた結果、以下の不足を挙げた:

1. **磁石吸着が弱い**: release 時の 1px flush 判定だけでは "プルン" とくっつく puzzle game 体感が出ない。in-drag に視覚的な引き寄せが欲しい（#1）。
2. **drag mode が group の core 含み判定で固定される**: Hakoniwa group では swap しかできず、startup を含む島ごと別の場所へ動かす操作ができない（#4）。逆に通常 group では translate しかできず、内部並び替えに swap が使えない（#3 の universal 化要求）。
3. **drag 中の予告が ghost のみ**: post-release の半透明 ghost で結果を見せるだけで、translate / detach の実描画感が弱い（#2）。
4. **キャンセル経路がない**: 結果が気に入らない時に release 前に取り消す手段が無い（#2 後半）。
5. **detach の閾値が小さい**: `D_DETACH=64px` は意図しない detach を生みやすく、"強く引き剥がす" (#5) の意図性が低い。

これらは ADR-0019 の Hakoniwa 特別扱い（D3/D7）と "release-only ghost" 設計（D8）と直接矛盾する。本 ADR は **cursor 位置で drag mode を動的に決める** モデルへ反転する。

ADR-0018 の **2 プレーン分離 / cross-plane snap 禁止** と ADR-0019 D1 `groupId` persist / D2 group=≥2 / D4 flush attach / D9 cross-plane group 禁止 / ADR-0020 first-launch factory grouping は本決定と無矛盾で全て維持。

## Decision

ADR-0019 D3 / D5 / D6 / D7 / D8 を以下の **8 点**で supersede する。

1. **[[Hakoniwa group]] 概念を退役**。core member 概念も廃止。全 group は groupId 共有 + visible/live ≥ 2（ADR-0019 D2 維持）の 1 種類だけ。startup / run_result は他の窓と全く同じ扱い。

2. **drag mode = cursor 位置で動的決定**。同 window A をドラッグ中、毎フレーム cursor 位置で 3 状態を判定:
   - **swap モード**: cursor が同じ island のメンバー B の rect 内（A 除外、center-in-rect 判定）→ A と B の (x,y,w,h) を交換するプレビュー
   - **island translate モード**: cursor が island の外 ∧ drag-start からの距離 < **D_DETACH = 256px** → island 全体が cursor offset 分だけ平行移動するプレビュー
   - **detach モード**: cursor が island の外 ∧ drag-start からの距離 ≥ D_DETACH → A が island から離脱して単独 cursor 追従するプレビュー（island の残メンバーは rest）

3. **in-drag 磁石吸着 = R_SNAP = 96px** canvas-logical px（離散 snap・物理シミュレーションではない）。
   - island translate 中: island の **外周辺** と他 window の辺との最短距離が ≤ R_SNAP なら flush 位置へ吸着（島同士の壁が引き合う）
   - detach 中: A の辺と他 window の辺との最短距離が ≤ R_SNAP なら flush 位置へ吸着
   - 吸着は **spring 200ms / 1-overshoot** の easing animation で発動（"プルン" 体感）

4. **release-position commit**（universal rule）:
   - swap プレビュー → A・B の (x,y,w,h) を交換、両者 groupId 不変
   - island translate プレビュー（empty space）→ island 全メンバーを cursor offset 分シフト
   - island translate プレビュー（別 island Y と overlap）→ **最寄り flush 位置へ snap → merge**（島① ∪ 島②、merge cascade で生き残る groupId 決定）
   - detach プレビュー（empty space）→ A.groupId = null、残 visible/live < 2 で島① 連鎖 dissolve
   - detach プレビュー（別 island Y と overlap）→ A は singleton 経由で島② に merge（A.groupId = 島②.groupId）

5. **merge cascade**（ADR-0019 D5 を Hakoniwa-priority 除去で simplify）= **size 最大 > 辞書順最小 > 新規 GUID**。

6. **D_DETACH 再較正**: ADR-0019 D6 `64f` → **256f** canvas-logical px。理由: 2-tier 閾値（translate < D_DETACH < detach）になり、translate 範囲を広く取らないと意図しない detach が頻発する。R_SNAP=96 の ~2.7 倍で 3 段階閾値（吸着 < 移動上限 < 千切れ）が綺麗に並ぶ。

7. **drag 中の描画 = 混合**（ADR-0019 D8 ghost-only を反転）:
   - **island translate / detach は実描画**（実窓 / 実 island が cursor について動く・磁石吸着は spring で実窓ごと snap）
   - **swap は ghost**（A・B の (x,y,w,h) 交換後の位置に 2 枚の ghost・実窓は rest 不変）
   - commit は MouseUp 一括（実描画していた位置も state 反映は MouseUp）

8. **キャンセル経路 = ESC キー**（既存仕様には無い能動キャンセル）。drag 中のいつでも ESC で revert（spring 200ms で rest 位置へ戻る animation、state は commit しない）。マウス操作だけの cancel は無い（release は何かを必ず commit する＝release-position rule の universal 化）。

programmatic Spawn（chart:&lt;id&gt; 等）は ADR-0019 D8 通り **groupId=null**（ユーザ drag-release だけが attach 起点）。ADR-0020 の first-launch factory grouping（5 base 窓を 1 island で起動）も維持。chart:&lt;id&gt; は他 window と全く同じドラッグルール。

## Considered Options

- **採用：cursor 位置で動的 drag mode 判定 ＋ in-drag 磁石吸着 ＋ 実描画 / ghost 混合 ＋ release-position commit**。owner Q1–Q18 で全分岐を選択。pure 算術（`FloatingWindowMath`）＋ controller / ghost / spring animation の薄い分離で AFK 検証可能。
- **不採用：Hakoniwa special を維持（D3 / D7 のまま）**。Q1 で「特別扱いしない」を選択。理由: drag mode が core 含み判定で固定されると操作のメンタルモデルが二系統になり予測しづらい・owner が #4 で「島ごと移動」を universal に要求している。
- **不採用：物理シミュレーション（バネ係数で連続的引き寄せ）**。Q6 で離散 snap を選択。理由: 毎フレーム座標が連続変動すると "snap-on-release" の AFK 権威モデル（pure 算術で位置確定）が崩れる・誤操作（意図せず吸い込まれる）を生みやすい。離散 snap + spring 200ms で "プルン" 体感は十分。
- **不採用：cross-island swap（Q3 A）**。Q3 で「同じ island の中だけ swap」を選択。理由: 別 island 同士の swap はメンバーシップ管理が複雑（誰がどの island に行くか・flush 維持できるか）。別 island とくっつけたい場合は flush merge の経路が別にあるので表現力は落ちない。
- **不採用：detach は singleton 強制（Q12 A）**。Q12 で「release-position rule に従う = detach 後でも overlap 位置で別 island に merge」を選択。理由: 経路（どう辿ってきたか）に依存させず release 位置だけで commit が決まる方が spec clean。
- **不採用：実描画のみ（Q13 C）**。Q13 で「混合（translate/detach 実描画・swap ghost）」を選択。理由: swap で A・B 両方の post-state を同時に見せるには 2 枚の ghost が必要・実描画だと cursor 直下から B が逃げる挙動が混乱を招く。
- **不採用：自動キャンセルのみ（Q5 A）**。Q5 で「自動 + ESC」を選択。理由: ESC は puzzle/UI 業界で広く期待される手動キャンセル経路で、ユーザーが結果プレビューを見て "違う" と思った時の逃げ道。実装と AFK は ESC ハンドラ 1 本の追加なので低コスト。

## Consequences

- **CONTEXT.md glossary** を本決定に整合（同時更新）: `Hakoniwa（ドッキング window クラスタ）` の Hakoniwa group / core member 概念を SUPERSEDED 表記・`window group / groupId` の drag mode 説明を「cursor 位置で動的判定」へ書換・`Hakoniwa group` / `core member` を SUPERSEDED マーク・`D_DETACH` を 256px へ再較正・`swap drop target` を「同 island メンバー全般」へ汎化・`drag ghost` を「swap のみ」へ縮小・**新設**: `R_SNAP（磁石吸着半径）`・`spring animation`。
- **永続スキーマ**: 追加なし。`FloatingWindowLayout.groupId: string?` は維持（ADR-0019 D1）。Hakoniwa-priority 関連の追加フィールドは不要（D3 退役で参照源が消える）。
- **pure 算術の追加 / 改修**:
  - `FloatingWindowMath`: `IsFlushAdjacent` / `ResolveDropTarget` 維持。新規 `ResolveDragMode(cursor, islandRects, dragStart, A, D_DETACH)` で 3 状態（swap / translate / detach）を返す。新規 `ComputeMagneticSnap(islandOuterEdges, otherEdges, R_SNAP)` で吸着 offset を返す。新規 `ResolveNearestFlush(islandRect, targetIslandRects)` で overlap drop 時の最寄り flush 位置。
  - `EvaluateDragMode`（ADR-0019 D6 の単純距離判定）は `ResolveDragMode` に吸収して退役。
- **controller の改修**: `FloatingWindowController` の group lifecycle（attach commit / merge / detach commit / dissolve / Close 連鎖）は維持・Hakoniwa identity 優先の merge cascade を D5 simplify 版に置換・swap commit（4 値交換）も同 island 全般へ汎化。
- **input 層の改修**: `FloatingWindowTitleInput` の drag mode 判定を `ResolveDragMode` 呼び出しへ置換。ESC キーハンドラ新設（drag 中の能動キャンセル）。in-drag 磁石吸着は毎フレーム `ComputeMagneticSnap` を呼んで実描画位置を補正（island translate / detach 両モード）。
- **ghost 描画層の縮小**: `DragGhostLayer` は **swap のみ** で使用（A・B 2 枚 ghost）。translate / detach は実描画で ghost なし。
- **spring animation の新設**: 磁石吸着発動時・ESC キャンセル時・merge commit 時に spring 200ms / 1-overshoot easing。`LeanTween.easeSpring` or `Animator` で 1 関数（`AnimateRectSpring(target, ms=200)`）を新設し全 trigger から共有。
- **D_DETACH 値の見直し**: 64 → 256（4 倍）。AFK で値そのものは pure 定数として 1 箇所参照されていれば変更は trivial。
- **AFK 正本の拡張**: `FloatingWindowE2ERunner` に新 section 群を追加:
  - DRAG-01..04: cursor-position drag mode（swap / translate / detach の正しい mode 選択）
  - DRAG-05..06: in-drag magnetic snap（R_SNAP 内で flush 吸着・外で自由ドラッグ）
  - DRAG-07..08: release-position commit（overlap → 最寄り flush + merge / empty space → translate or detach / singleton merge for detach + overlap）
  - DRAG-09: ESC キャンセル（drag 中の能動 revert）
  - DRAG-10: spring animation の rect 補間（200ms / 1-overshoot を decode rect で assert）
  - DRAG-11..13: 旧 ADR-0019 D3/D5/D7 の Hakoniwa special 挙動の **退役 assert**（startup を含む island でも translate / detach 可能・core detach 不可だった挙動が消える）
  - DRAG-14: ADR-0019 D8 ghost の縮小（translate / detach に ghost が出ない・swap だけ ghost 2 枚）
- **既存 AFK の影響**: ADR-0019 D6/D7/D8/D3 を assert していた既存 section（旧 GROUP-* 系・FloatingWindowE2ERunner の Hakoniwa-only swap / Hakoniwa-only translate 禁止）は本 ADR の挙動と矛盾するため **削除 / 書き換え**（findings 0088 で section ごとに対応リストを管理）。
- **chart:&lt;id&gt; への波及**: ADR-0019 D8 の programmatic Spawn = null は維持。本 ADR の全ルール（swap / translate / detach / merge）が chart にも適用される（owner が #1 panel 列挙で chart を含めた）。
- **ADR-0020 first-launch factory grouping への波及**: 維持（Hakoniwa special が消えても、5 base 窓を 1 island で起動する usability 決定は別軸）。Q17 A で確認済み。
- **下位事実は findings 0088 に固定**: 設計の木 §0–§14。本 ADR は方針のみで実装下位事実（spring 関数の具体・magnetic snap algorithm 詳細・閾値定数の置き場・ESC キーバインドの衝突回避・各 AFK section の RED→GREEN 手順）は書き戻さない。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
R_SNAP / D_DETACH の最終値・spring animation の specific easing 関数・ghost 仕様の細部・ESC キーバインドの衝突回避・merge cascade の同点 tie-break 詳細などの下位事実は本 ADR に書き戻さず、`docs/findings/0088` に記録し本 ADR を「方針: ADR-0024」として参照する。
