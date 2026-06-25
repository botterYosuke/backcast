---
status: accepted
---

# 掴む対象でモード固定するドラッグセマンティクス（gesture-channel で mode 確定・距離トリガ撤廃・eject つまみ/Alt 単窓ピックアップ・swap 島内局所 reflow）

owner 依頼（2026-06-25）「floating-window のドラッグを直す: (1)『強く引き剥がす』の感度が高すぎてすぐ detach してしまう／島の遠距離移動もできない、(2) 同一ドラッグが cursor の内外で swap / translate に化けて分かりにくい。根底を覆す案で良い」を、方向A「掴む対象でモード固定」として `grill-with-docs`（2026-06-25・owner HITL Q1–Q5）で設計ロックした決定。

これは **[[ADR-0024]] Decision 2 / 4 / 6 と swap 4 値交換を点 supersede** する（ADR-0024 D1 Hakoniwa 退役 / D3 磁石吸着 R_SNAP / D5 merge cascade / D7 混合描画 / D8 ESC は維持）。ADR-0024 は自己保護条項を持つため**無改変**で、本 ADR が差分だけを明示 supersede する（[[ADR-0018]]→0017、[[ADR-0019]]→0017、[[ADR-0024]]→0019 の先例と同型）。

関連: [[ADR-0024]]（D2/D4/D6/swap を本 ADR が supersede・D1/D3/D5/D7/D8 は維持）／[[ADR-0019]]（D1 groupId persist / D2 group=≥2 / D4 flush attach / D9 cross-plane 禁止 は維持）／[[ADR-0020]]（first-launch factory grouping 維持）／[[ADR-0018]]（plane 分離・cross-plane snap 禁止 維持）／[[ADR-0017]]（free-placement・自動タイリングしない＝本 ADR は swap 時の **島内局所 reflow** だけを明示 carve-out）。実装下位事実は findings 0106。

## Context

ADR-0024 は "puzzle game プルン" 体感のため、ドラッグ mode を **cursor 位置で毎フレーム動的決定**（同 island メンバー rect 内 → swap / 島外 ∧ < D_DETACH(256px) → island translate / 島外 ∧ ≥ 256px → detach）し、detach を **drag-start からの距離閾値**で起動するモデルにした。owner は実際に使った結果、2 つの不満を挙げた:

1. **detach の感度が高すぎる + 島の遠距離移動ができない**: detach が「drag-start から 256px 以上」の距離トリガなので、(a) 少し大きく動かすつもりが意図せず detach に化ける、(b) そもそも island translate は `< D_DETACH` のみ＝**島を 256px 以上動かすと必ず detach になり、島ごと遠くへ運ぶ操作が構造的に不可能**。距離という 1 軸では「島を遠くへ動かす」と「1 枚を引き剥がす」を区別できない（両方とも大きく動く）。数値較正では直らない。
2. **同一ドラッグが cursor 内外で化けて予測できない**: 1 つのドラッグジェスチャに swap / translate / detach の 3 mode を相乗りさせ、cursor の位置と距離という **不可視の状態**で毎フレーム切り替えるため、結果を見るまで何が起きるか分からない。

根本原因は **「1 ジェスチャに複数 mode を相乗りさせ、暗黙の空間状態（cursor 内外・距離）で多重化する」設計そのもの**。本 ADR は **mode を「何をどう掴んだか」という明示的なチャンネルで gesture 開始時に固定**するモデルへ反転する（距離判定を全廃）。

ADR-0017 の free-placement（自由配置・自動タイリングしない）と CONTEXT.md の Avoid「タイリング強制・resize 連動を持ち込むこと」は維持するが、owner は swap について「サイズ違いの 2 窓でも入れ替えでき、`ぷるん` と局所的に自動調整してほしい」（Q5）と要求した——これだけは **島内に限定した局所 reflow** として明示 carve-out する。

## Decision

ADR-0024 D2 / D4 / D6 と swap 4 値交換を以下の **8 点**で supersede する。

1. **2 ジェスチャモデル・チャンネルは gesture 開始時に固定**（ADR-0024 D2 の per-frame cursor 判定を supersede）。mode はもはや毎フレーム cursor 位置で再評価しない。`OnBeginDrag` 時点でチャンネルが決まり、その drag の間は化けない:
   - **ジェスチャ①＝島移動**（plain な title-bar drag）: island 全体を平行移動。**距離無制限**・in-drag 磁石吸着あり・別 island に flush で release すれば merge。swap も detach も**起こさない**。
   - **ジェスチャ②＝単窓ピックアップ**（eject つまみ or `Alt`+drag）: island から 1 枚だけ抜き出して運ぶ。ドロップ先で結果が決まる（D4）。
   - singleton（island 非所属の単独窓）の plain drag は「1 枚 island の島移動」＝自由移動 + flush で attach。

2. **detach の距離トリガ撤廃**（ADR-0024 D6 を supersede・`D_DETACH_PX` 定数退役）。detach はもはや「距離」ではなく「**ジェスチャ②で兄弟でも別 island flush でもない空き地にドロップした結果**」。閾値 magic number を持たない（owner 不満 1 の根治＝誤離脱が距離で起きない）。

3. **単窓ピックアップ（ジェスチャ②）の起動 = eject つまみ（常時可視・主アフォーダンス）＋ `Alt`+drag（ショートカット）**（owner Q2）。invisible mode 批判を構造的に消すため**見えるアフォーダンス**を主にする。`OnBeginDrag` で eject-handle の raycast or `Keyboard.current.leftAltKey.isPressed` を読みチャンネル確定。title-bar の plain drag = ジェスチャ①、window body = canvas pan（既存不変）。

4. **ジェスチャ②のドロップ結果（release-position rule・ジェスチャ②内に閉じる）**:
   - cursor が**同 island の兄弟メンバー rect** 上 → **swap**（D6・サイズ維持＋島内局所 reflow）
   - **別 island に flush**（磁石吸着 engaged）で release → **merge**（singleton 経由・merge cascade ADR-0024 D5）
   - **空き地**（兄弟でも flush でもない）→ **detach**（dragged.groupId=null・残 visible/live < 2 で連鎖 dissolve）
   運搬中は picked window を実描画で cursor 追従。swap 候補（兄弟 rect 上）では **post-swap+reflow を ghost プレビュー**で予告（対象がハイライト）。

5. **島移動（ジェスチャ①）の merge は維持**（ADR-0024 D4 の精神・owner Q3）。島を別 island に磁石で寄せて release し flush なら 2 島→1 島に merge（"プルン" くっつき・ESC 取消あり）。**detach はジェスチャ①では絶対に起きない**（membership を減らす操作はジェスチャ②に限定＝不満 1 の根治）。

6. **swap = サイズ維持＋島内局所 reflow**（ADR-0024 swap の `(x,y,w,h)` 4 値交換を supersede・owner Q5）。2 窓は各自の `(w,h)` を保持し、**位置（anchor）だけ**交換。交換後、**同じ island のメンバーだけ**を対象に best-effort な magnetic flush re-snap（はみ出し/重なりを解消する向きに隣窓が `ぷるん` 寄る）。perfect tiling は保証せず、解消しきれない隙間は free-placement として許容（Q4）。reflow scope は **island に厳密に限定**（他 island・他 plane に波及しない）。

7. **global reflow しない**（ADR-0017 free-placement 維持・owner Q4）。単窓を抜いた跡の穴は埋めない・触っていない窓は動かさない。唯一の例外が D6 の swap 時**島内**局所 reflow。

8. **磁石吸着 [[R_SNAP]]=96 / [[spring animation]] 200ms・overshoot 8% / ESC キャンセル / merge cascade / cross-plane 禁止 / factory grouping は維持**（ADR-0024 D3/D5/D8 + ADR-0019 D1/D2/D4/D9 + ADR-0020）。"プルン" spring の trigger に **swap 島内局所 reflow の rect 補間**を追加。

programmatic Spawn = `groupId=null`（ADR-0024 D8 維持）。programmatic Close = 連鎖 dissolve（維持）。Hide = groupId 温存（維持）。chart:&lt;id&gt; は他 window と全く同じドラッグルール（両ジェスチャ適用）。

## Considered Options

- **採用：gesture-channel で mode 固定（plain=島移動 / eject・Alt=単窓ピックアップ）＋ 距離トリガ撤廃 ＋ swap 島内局所 reflow**。owner Q1–Q5 で全分岐を選択。チャンネルを gesture 開始で固定するため drag 中に mode が化けない（不満 2 の根治）。detach を距離でなくドロップ位置の結果にするため誤離脱が起きず島の遠距離移動も可能（不満 1 の根治）。pure 算術（`FloatingWindowMath`）＋ controller / input / ghost / reflow の薄い分離で AFK 検証可能。
- **不採用：cursor 位置で per-frame mode 判定を維持（ADR-0024 D2 のまま値だけ較正）**。owner が「根底を覆す案で良い」と明言・距離 1 軸では島移動と detach を原理的に分離できないため却下。
- **不採用：swap / detach を完全に別ジェスチャに割る**（Q1 の対案）。より明示的だが覚える操作種が増え、ジェスチャ②内の「ドロップ先で決まる」単純さを失う。owner Q1=「1 本に集約」。
- **不採用：Alt-drag のみ / eject つまみのみ**（Q2 の対案）。modifier だけは発見性が低く（invisible mode 批判が残る）、つまみだけは速いショートカットが無い。owner Q2=両用。
- **不採用：島移動は純平行移動のみで merge もジェスチャ②限定**（Q3 の対案）。ジェスチャ①が絶対に membership を変えない保証は得られるが、島ごとくっつける素直な経路を失う。owner Q3=島移動でも merge。
- **不採用：単窓を抜いた跡を reflow で詰める / swap でスロットごとサイズ交換**（Q4/Q5 の対案）。前者は ADR-0017 free-placement を覆す自動タイリング・後者は「窓サイズが勝手に変わる」で owner の「サイズ違いを許容」とズレる。owner Q4=空けたまま・Q5=サイズ維持＋島内局所 reflow。

## Consequences

- **CONTEXT.md glossary** を本決定に整合（同時更新）: `Hakoniwa`／`window group / groupId`（drag mode を「gesture-channel で gesture 開始時に固定」へ書換・detach を距離撤廃）／`D_DETACH`（**退役マーク**＝距離トリガ廃止）／`R_SNAP`／`swap drop target`（4 値交換→サイズ維持＋島内局所 reflow）／`drag ghost`（swap は post-swap+reflow プレビュー）／`spring animation`（trigger に swap reflow 追加）・**新設**: `gesture channel（島移動/単窓ピックアップ）`／`eject つまみ`／`island-scoped reflow`。
- **永続スキーマ**: 追加なし。`FloatingWindowLayout.groupId: string?` は維持（ADR-0019 D1）。
- **pure 算術の改修**（`FloatingWindowMath`）: `ResolveDragMode`（cursor 位置 3 判定）を退役し、**チャンネル enum**（`IslandMove` / `SingleWindowPickup`）＋ ジェスチャ②の `ResolveDropOutcome(cursor, islandMembers, otherIslands, picked)`（swap / merge / detach を返す）へ置換。`D_DETACH_PX` 削除。新規 `ReflowIslandAfterSwap(islandRects, a, b, rSnap)`（サイズ維持で位置交換 → 島内 best-effort magnetic flush re-snap・island scope 厳密限定）。`ComputeMagneticSnap` / `ResolveNearestFlush` / merge cascade / `SpringEase` は維持。
- **controller の改修**（`FloatingWindowController`）: `DragSession` にチャンネルを記録（gesture 開始時に確定・drag 中不変）。`DragApplyDelta` をチャンネル分岐（IslandMove=島全員実描画 + 磁石 / SingleWindowPickup=picked のみ実描画 + 兄弟上で swap ghost）。`CommitOnRelease` をチャンネル分岐（IslandMove=overlap merge or translate / SingleWindowPickup=swap+reflow / merge / detach）。swap commit を 4 値交換から `ReflowIslandAfterSwap` 駆動へ。
- **input 層の改修**（`FloatingWindowTitleInput`）: `OnBeginDrag` で eject-handle / `Alt` を読みチャンネル確定し `BeginDrag(id, channel, rest)`。**eject つまみ**を title-bar に新設（第 2 raycast target・常時可視 "⤴"）。ESC ハンドラは維持。
- **ghost 描画層の改修**（`DragGhostLayer`）: swap プレビューを「2 枚交換」から「**post-swap + island reflow 後の島レイアウト**」プレビューへ拡張（dragged + target + reflow で動く隣窓の ghost）。island move / detach は実描画で ghost なし。
- **AFK 正本の再構成**: `FloatingWindowE2ERunner` の DRAG-* で **cursor 位置 mode 判定 / D_DETACH 距離 detach / swap 4 値交換**を assert する section（DRAG-01..04 の cursor 判定・DRAG-03 の距離 detach・swap exact）は本 ADR と矛盾するため**削除/書換**。新 section（gesture-channel lock / eject・Alt 起動 / 距離撤廃 detach / swap 島内局所 reflow / island move 無制限距離 + merge）は **実装着手前に `behavior-to-e2e` を formal invoke** して固定（findings 0106 §AFK で section ごとに対応リスト管理）。
- **下位事実は findings 0106 に固定**: 設計の木。本 ADR は方針のみで実装下位事実（eject つまみの icon/配置・Alt キー衝突回避・島内 reflow アルゴリズムの厳密手順・各 AFK section の RED→GREEN）は書き戻さない。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
eject つまみの最終 icon / 配置・Alt キーバインドの衝突回避・島内局所 reflow の厳密アルゴリズム（best-effort magnetic re-snap の走査順 / 残隙間の許容詳細）・swap ghost プレビューの reflow 描画の細部などの下位事実は本 ADR に書き戻さず、`docs/findings/0106` に記録し本 ADR を「方針: ADR-0029」として参照する。
