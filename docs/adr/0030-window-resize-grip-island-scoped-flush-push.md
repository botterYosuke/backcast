---
status: accepted
---

# floating-window のリサイズ（右下つまみ・左上固定で右下伸縮・島内 flush 追従押し出し）

owner 依頼（2026-06-25）「Strategy Editor をリサイズ可能にして」を、`grill-with-docs`（2026-06-25・owner HITL Q1–Q5）で設計ロックした決定。findings 0008 §0/§8 が「resize gesture / resize handle（TTWR `resizable`）→ 将来 slice」と deferred していた当の slice を起こす。永続スキーマ（`FloatingWindowLayout.w/h` + `x/y`）は #15 で既に揃い AFK 実証済み（findings 0008 §11）なので**スキーマ追加ゼロ**、本文は anchor stretch で**自動伸縮**するので content reflow 算術も不要——本 ADR が固定するのは「掴み方」と「島内の押し出し挙動」の 2 点。

本 ADR は **[[ADR-0017]]（free-placement・自動タイリングしない・resize 連動を持ち込まない）／[[ADR-0029]] D7（global reflow しない・唯一例外は swap 時の島内局所 reflow）／findings 0075 §1（「結合なし・resize push-out なし」）** を、**resize というトリガに限って点 supersede** する（island scope 厳守は維持・global 化はしない）。ADR-0017 / ADR-0024 / ADR-0029 は全て自己保護条項を持つため**無改変**で、本 ADR が差分だけを明示 supersede する（[[ADR-0018]]→0017、[[ADR-0019]]→0017、[[ADR-0024]]→0019、[[ADR-0029]]→0024 の先例と同型）。

関連: [[ADR-0029]]（2 ジェスチャチャンネル・eject つまみ・island scope reflow の規律は維持／本 ADR は reflow トリガに resize を追加し、reflow を「best-effort magnetic flush」から「常時タイル維持の flush 追従押し出し」へ resize 時だけ強化）／[[ADR-0017]]（free-placement・global タイリングしないは維持／「resize 連動なし」を resize 時の島内に限り carve-out）／[[ADR-0018]]（plane 分離・cross-plane 禁止 維持）／[[ADR-0019]]（groupId lifecycle 維持）。実装下位事実は後続スライスの findings 0112。

## Context

floating-window システム（#15・findings 0008）は spawn / title-drag move / z-order / persist を備えるが、**resize は意図的に「将来 slice」へ deferred**された（findings 0008 §0/§8・§11 で「persist 表現は position + size、非 default size の restore まで AFK 証明済み」＝スキーマだけ先行整備）。owner が今その slice を要求した。

設計上、ほとんどの土台は既にある:

- **永続化**: `FloatingWindowLayout` は `w/h`（サイズ）と `x/y`（top-left anchor）を持ち、`Capture` が `sizeDelta` / `anchoredPosition` を読む。リサイズ結果も押された隣窓の位置も既存 Capture が拾う＝**スキーマ追加なし**。
- **本文の伸縮**: window frame の Body は `anchorMin=(0,0) / anchorMax=(1,1)` + offset insets（`StrategyEditorWindowFrame.Build`）で枠に貼り付くため、枠の `sizeDelta` を変えれば入力欄・出力は自動追従＝**content reflow 算術なし**。
- **座標規約**: window は pivot=(0,1)（top-left）。右下つまみで伸縮すれば **`anchoredPosition` 不動・`sizeDelta` のみ変化**＝座標モデルと綺麗に噛み合う。

残る本質は (1) 掴み方（どこを掴んでサイズを変えるか）と、(2) **昨日固めた [[ADR-0029]] の「2 ジェスチャチャンネルは掴んだ対象で gesture 開始時に固定」不変条件との共存**、そして (3) owner が選んだ「**隣を押しのける**」挙動が、**ADR-0017 / ADR-0029 / findings 0075 が固定した「自動タイリングしない・resize push-out なし・reflow は swap 限定」と正面から食い違う**点をどう収めるか。

owner は「島内押し出し」を、具体シナリオ（A の右に B が並ぶ島で A を広げ→縮める）で **対称（広げる→押し出し／縮める→引き戻し・島は常にタイル状を維持）** と確定した（Q5）。これは distance/magnet では表現できない「動いた辺に flush しているメンバーが辺に張り付いたまま追従する」不変条件であり、swap reflow の best-effort magnetic flush（隙間を閉じるだけ・重なりは押し離さない）より**強い**。よって resize 専用の押し出し算術を新設し、ADR-0017/0029 の「resize 連動なし」を resize トリガに限って carve-out する。

## Decision

ADR-0017「resize 連動なし」／ADR-0029 D7「reflow は swap 限定」／findings 0075 §1「resize push-out なし」を、**resize トリガに限り**以下の **6 点**で supersede する。island scope 厳守・global タイリングしないは維持。

1. **掴み方＝右下角の常時可視つまみ**（owner Q1）。window root の右下角に小さな見えるつまみ（◢ 風チップ・eject つまみと同じ「invisible mode を作らない」思想）を置き、ドラッグでサイズ変更。**左上 pivot 固定で右・下に伸縮**（`anchoredPosition` 不動・`sizeDelta` のみ変化）。delta は title-drag と同じく Viewport-local→`FloatingWindowMath.ViewportDeltaToLogical(delta, zoom)` で canvas 論理化（raw `eventData.delta` を直接使わない・findings 0008 §2 規律）。

2. **全 window 対象**（owner Q2）。eject つまみと同じく `FloatingWindowTitleInput.Awake` 経由で**全 window のルートに一律 attach**（dock / editor / order / HITL）。TTWR の per-spec `resizable` フラグは「全部 true」なら死に設定なので**採らない**（eject つまみが無条件なのと同じ前例＝owner が全 window 希望ゆえの正当な parity 逸脱）。

3. **[[ADR-0029]] の 2 ジェスチャチャンネルとは別系統**。つまみは独自の raycast target ＋ 独自 `IDrag*Handler` ＋ controller の**別 resize セッション**で駆動し、`ResolveChannel`（IslandMove / SingleWindowPickup）には**一切入らない**。つまみのドラッグは「title-bar drag」ではないのでチャンネル判定の対象外＝**ADR-0029 の「掴んだ対象で gesture 開始時にチャンネル固定」不変条件は無傷**。window body の pan・title-bar の 2 ジェスチャ・eject つまみの単窓ピックアップは従来どおり。

4. **島内 flush 追従押し出し（＝carve-out 本体・owner Q3/Q5）**。resize 中、**リサイズ窓と同じ island のメンバー**のうち、リサイズ窓の**動いている辺（右辺 / 下辺）に flush している**もの（[[ADR-0029]] と同じ flush 判定＝対辺一致 ∧ 直交軸 overlap > 0）は、その辺に**張り付いたまま追従して平行移動**する。**対称**: 広げれば押し出し・縮めれば引き戻し（メンバーの該当辺は常にリサイズ窓の辺に kiss）。島内で**連鎖**（追従して動いたメンバーに flush する更なるメンバーも追従）。**メンバーは移動のみ・自身のサイズは保持**（ADR-0029 swap の size-retain と同じ精神）。左辺・上辺は左上固定で動かないので、左・上の隣は無関係。

5. **scope は island に厳密限定**（[[ADR-0029]] §7 / [[ADR-0018]] 維持）。他の island・他の plane・グループ化していない単なる隣接窓は**一切動かさない**（owner「同じ島の他メンバーだけ」Q3）。global reflow / global タイリングは持ち込まない＝ADR-0017 の free-placement は resize の**外**では完全維持。

6. **clamp / cancel / persist / 最前面化**:
   - **min**: リサイズ窓は `spec.minSize` 未満に縮まない（spawn 境界の既存 clamp と同じ下限・findings 0008 §3）。**max なし**（無限 canvas）。
   - **ESC キャンセル**: resize 中 ESC でリサイズ窓のサイズ＋押した全メンバーの位置を **rest（resize 開始時スナップ）へ revert**（title-drag の `CancelDrag` と同じ規律・state は不変）。
   - **最前面化**: つまみ掴みで `NoteUserFocus`（raise + focus）＝title-bar press と同じ。
   - **persist**: スキーマ追加ゼロ。リサイズ窓の新 `sizeDelta` と押されたメンバーの新 `anchoredPosition` は既存 `Capture`（w/h + x/y）が両方拾う（findings 0008 §11 実証済み）。

純算術（`FloatingWindowMath` に resize 押し出しを新設・headless AFK 権威）＋ controller / input の薄い分離で AFK 検証可能にする。本文伸縮は anchor stretch で算術不要。

## Considered Options

- **採用：右下つまみ（全 window）＋ ADR-0029 と別系統の resize セッション ＋ 島内 flush 追従押し出し（対称・連鎖・island scope 厳守）**。owner Q1–Q5 で全分岐を選択。左上固定の右下つまみは座標モデル（pivot top-left）と綺麗に噛み合い、別系統ゆえ ADR-0029 のチャンネル不変条件を壊さない。島内押し出しは「常にタイル状を維持」したい owner 要件を満たしつつ scope を island に閉じることで free-placement の精神を resize 外で保つ。
- **不採用：四隅すべて＋辺リサイズ**（Q1 対案）。自由度は高いが、左上・上辺・左辺を掴むと `anchoredPosition` と `sizeDelta` が同時に動き座標の扱いが複雑化、テスト面も増える。owner Q1=右下角つまみ。
- **不採用：Strategy Editor だけ resize 可（per-spec `resizable` フラグ）**（Q2 対案・TTWR parity）。TTWR は per-spec フラグを持つが、owner が全 window 希望ゆえフラグは常時 true の死に設定。eject つまみの無条件 attach 前例に合わせ省略。
- **不採用：リサイズ窓だけ変わる（隣は独立・重なり許容）**（Q3 対案・findings 0075 §1 のまま）。最もシンプルで既存原則と一貫するが、owner は「押しのける」を明示選択（タイル状を保ちたい）。
- **不採用：隣の端に磁石吸着（move のプルンを resize に流用）**（Q3 対案）。見た目は揃うが「重なりを押し離す」変位は magnet（gap を閉じるだけ）では表現できず、結局新規算術が要る。owner Q3=押しのける。
- **不採用：広げたときだけ押す（縮めても隣は戻らない・隙間が残る）**（Q5 対案）。実装は単純だが動きが非対称で違和感。owner Q5=対称（縮めると引き戻し・常にタイル維持）。
- **不採用：resize を ADR-0029 の第 3 チャンネルとして DragChannel に足す**。title-drag セッションに resize を相乗りさせると「掴んだ対象でチャンネル固定」の意味が濁る（resize はそもそも title-bar drag ではない）。別系統に切るほうが ADR-0029 の不変条件を保てる。

## Consequences

- **CONTEXT.md glossary** を本決定に整合（同時更新）: `Hakoniwa` の _Avoid_「resize 連動を持ち込むこと（例外は swap 時の島内局所 reflow のみ）」を「**resize 時の島内 flush 追従押し出しも carve-out（ADR-0030・island scope 厳守）**」へ更新。**新設**: `resize つまみ`／`island-scoped resize push`（swap reflow との差＝best-effort flush でなく常時タイル維持の追従）。
- **永続スキーマ**: 追加なし。`FloatingWindowLayout.w/h` + `x/y` は #15 で既存（findings 0008 §3/§11）。
- **pure 算術の新設**（`FloatingWindowMath`）: resize 押し出しを返す純関数（例 `ResizeIslandPush(resizedId, islandRects, newSize, …)` → 各メンバーの post-resize rect）。flush 判定は `IsFlushAdjacent` / `DockRect` の辺規約を再利用。swap の `ReflowIslandAfterSwap`（best-effort magnetic）とは**別物**（こちらは動いた辺への強制 flush 追従・連鎖）。
- **controller の改修**（`FloatingWindowController`）: title-drag の `DragSession` とは別の resize セッション（`BeginResize` で島メンバーの rest rect をスナップ・`ResizeApply` で per-frame に新サイズ＋押し出しを実描画・`ReleaseResize` で commit・`CancelResize` で rest へ revert）。`MoveByLogical` / 既存ドラッグ機構は無改変。
- **input 層の新設**（`Assets/Scripts/FloatingWindow/`）: 右下つまみの durable MonoBehaviour（`FloatingWindowResizeGrip` 等・FloatingWindowTitleInput をミラー）＋ つまみ builder（`FloatingWindowEjectHandle` をミラーした find-or-create・`FloatingWindowTitleInput.Awake` から一律 attach・raycast target・最後 sibling で最前面・本文の InputField/pan へ落ちない）。
- **AFK 正本**: resize 押し出し算術 + real-RectTransform で「左上固定・右下伸縮」「島内 flush 追従（対称・連鎖）」「min clamp」「island scope 厳守（他島・他 plane 不動）」「非空転 disk round-trip（リサイズ＋押し出し後の geometry persist/restore）」「ESC revert」を pin する新 section を、**実装着手前に `behavior-to-e2e` を formal invoke** して固定し、Action-ID タグで `run-all-tests` rollup に載せる（findings 0112 §AFK で section ごと管理）。HITL（つまみ操作・本文伸縮の目視）は owner。
- **下位事実は findings 0112 に固定**: 設計の木・つまみの icon/サイズ/raycast 詳細・押し出しアルゴリズムの厳密手順（連鎖の走査順・clamp 時の追従打ち切り）・各 AFK section の RED→GREEN。本 ADR は方針のみで書き戻さない。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
右下つまみの最終 icon / サイズ / 配置・押し出しアルゴリズムの厳密手順（連鎖走査順・min clamp 到達時の追従打ち切り・FP slack）・resize セッションと title-drag セッションの coexistence の細部などの下位事実は本 ADR に書き戻さず、`docs/findings/0112` に記録し本 ADR を「方針: ADR-0030」として参照する。
