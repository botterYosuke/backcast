# findings 0106 — 掴む対象でモード固定するドラッグ（gesture-channel・距離トリガ撤廃・eject/Alt 単窓ピックアップ・swap 島内局所 reflow）

方針: **[[ADR-0029]]**。本 findings はその下位設計の木を固定する。
grill: `grill-with-docs`（2026-06-25・owner HITL Q1–Q5）。supersede: findings 0088 §1（cursor 位置 3 mode 判定 `ResolveDragMode`）／§4 の swap 4 値交換と detach 距離経路／§11 の `D_DETACH_PX` 定数／§13 の DRAG-* のうち cursor 判定・距離 detach・swap exact を assert する section。維持: 0088 §2（磁石吸着 R_SNAP）／§3（spring）／§5（merge cascade）／§6（ESC）／§12（cross-plane）／§9（factory grouping）。

実装着地（§14）は未記入（実装後に追記）。AFK 正本再構成（§AFK）は実装着手前に `behavior-to-e2e` を formal invoke して固定する。

## §0 owner が選んだ 5 の分岐（HITL・2026-06-25）

1. **単窓操作を 1 本に集約**（Q1）= swap / detach / 別島 merge を「単窓ピックアップ」1 ジェスチャに集約し**ドロップ先で決定**。ジェスチャは「島移動」「単窓ピックアップ」の 2 種だけ。
2. **eject つまみ＋Alt-drag 併用**（Q2）= 単窓ピックアップの起動は title-bar の常時可視 "⤴" つまみ（主アフォーダンス）＋ `Alt`+drag（ショートカット）。
3. **島移動でも merge する**（Q3）= ジェスチャ①で別島に磁石で寄せて release し flush なら 2 島→1 島 merge。detach はジェスチャ①では起きない。
4. **global reflow しない**（Q4）= 単窓を抜いた跡の穴は空けたまま・触っていない窓は動かさない（ADR-0017 free-placement 維持）。
5. **swap はサイズ維持＋島内局所 reflow**（Q5）= サイズ違いの 2 窓を swap でき、各自サイズを保ったまま位置交換 → 同じ島の隣窓だけが `ぷるん` 寄って自動調整（はみ出し/隙間解消）。perfect tiling は非保証・残隙間は許容。

## §1 ドラッグの状態（gesture 開始でチャンネル固定・per-frame 化けなし）

記号: A = 掴んだ window、I₁ = A が属する island（A.groupId 共有・visible/live ≥ 2、singleton なら {A}）、`drag_start` / `cursor` = canvas-logical 座標。

`OnBeginDrag` でチャンネルを 1 度だけ確定し、drag 中は不変（ADR-0024 の毎フレーム `ResolveDragMode` を退役）:

```
function BeginDrag(A, modifiers, hitTarget):
    if hitTarget == EJECT_HANDLE or modifiers.alt:
        channel = SINGLE_WINDOW_PICKUP      # 島から A を 1 枚抜き出して運ぶ
    else:
        channel = ISLAND_MOVE               # I₁ 全体を平行移動（singleton なら A 単独）
    snapshot I₁ ids + rest rects + nonIsland rects   # DragSession に固定
```

- **distance 概念なし**: `D_DETACH_PX` を削除。detach は距離ではなくドロップ先で決まる（§3）。
- **singleton**: |I₁| = 1 のとき ISLAND_MOVE は A 単独移動。SINGLE_WINDOW_PICKUP は意味的に同じ（既に独立）だが実装上は ISLAND_MOVE に畳んでよい。

## §2 ジェスチャ①＝島移動（ISLAND_MOVE）

```
function DragApplyDelta_IslandMove(I₁, offset = cursor - drag_start):
    snap = ComputeMagneticSnap(I₁.outerEdges, nonIslandEdges, R_SNAP)   # 0088 §2 維持
    for w in I₁: renderActual(w, w.restRect + offset + snap)            # 島全員・実描画・ghost なし
    # 距離無制限。swap も detach も起こさない
```

release commit:
```
function CommitIslandMove(I₁, cursor):
    Y = islandOverlappingAtCursor(cursor) excluding I₁
    if Y != null:                                  # 別島と overlap
        snapped = ResolveNearestFlush(I₁.bbox + offset, Y.bbox)   # 0088 §4 / 0099 修正の bbox 基準
        applyOffset(I₁, snapped - I₁.bbox); mergeIslands(I₁, Y)   # owner Q3: 島移動でも merge
    else:
        applyOffset(I₁, offset)
        if isFlushAdjacent(I₁.bbox, some Y): mergeIslands(I₁, Y)  # 偶発 flush attach (D4)
```

## §3 ジェスチャ②＝単窓ピックアップ（SINGLE_WINDOW_PICKUP）

```
function DragApplyDelta_Pickup(A, cursor):
    snap = ComputeMagneticSnap(A.edges, nonIslandEdges, R_SNAP)
    renderActual(A, A.restRect + (cursor - drag_start) + snap)     # A だけ実描画で運ぶ
    B = siblingUnderCursor(A, I₁)                                  # 同 island の他メンバー rect 内？
    if B != null: renderGhost(post_swap_reflow_layout(A, B, I₁))   # swap 候補＝post-swap+reflow を ghost 予告
    else:         clearGhosts()
```

release commit（**ドロップ先で決定**・owner Q1）:
```
function CommitPickup(A, I₁, cursor):
    B = siblingUnderCursor(A, I₁)
    if B != null:                                  # 兄弟上 → swap
        ReflowIslandAfterSwap(I₁, A, B)            # §4・サイズ維持＋島内局所 reflow
    else:
        Y = islandFlushAtRelease(A, cursor)        # 磁石 engaged で別島に flush？
        if Y != null:                              # 別島に flush → merge（singleton 経由）
            A.rect = ResolveNearestFlush(A.rect, Y.bbox)
            detachFrom(A, I₁); A.groupId = Y.groupId   # merge cascade で確定
        else:                                      # 空き地 → detach
            detachFrom(A, I₁)                       # A.groupId = null・残 <2 で連鎖 dissolve
```

- `siblingUnderCursor` は center-in-rect（0088 §1 の判定を流用・dragged 除く・hidden 除く・最前面優先）。
- detach の trigger は**距離ではなくドロップ先**（兄弟でも別島 flush でもない）= 不満 1 の根治。

## §4 swap = サイズ維持＋島内局所 reflow（owner Q5）

```
function ReflowIslandAfterSwap(I₁, A, B):
    # 1. サイズ維持で anchor だけ交換
    swap(A.anchor, B.anchor)            # (x,y) のみ。 (w,h) は各自保持（4 値交換しない）
    # 2. 島内 best-effort magnetic flush re-snap（island scope に厳密限定）
    for w in I₁ ordered by 安定走査:     # 走査順の厳密仕様は実装時に固定（findings 追記）
        d = ComputeMagneticSnap(w.edges, (I₁ \ {w}).edges, R_SNAP)
        if d != 0: w.rect += d          # はみ出し/重なりを解消する向きへ寄せる
    # 3. 残った隙間は free-placement として許容（perfect tiling 非保証・Q4）
    fire spring 200ms on moved windows  # "ぷるん"
```

- **scope は I₁ に厳密限定**: 他 island・他 plane の窓は絶対に動かさない（global no-reflow 維持・Q4）。
- **size は変えない**: owner「多少サイズが違っても許容」= 強制サイズ均等化（4 値交換）はしない。
- **best-effort**: 任意サイズの完全タイリングは一般に不能。解消しきれない隙間/微小重なりは許容し、playmode HITL で体感確認（§HITL）。アルゴリズムの厳密手順（走査順・収束条件・重なり優先解消）は実装時に findings へ追記。

## §5 描画・spring・ESC（ADR-0024 から維持＋拡張）

- **混合描画**: island move = 島全員実描画 / pickup = A 実描画 + 兄弟上で swap ghost / detach = A 実描画。ghost は **swap プレビューのみ**（post-swap + reflow 後の島レイアウト）。
- **spring 200ms / overshoot 8%**（0088 §3 維持）。trigger に **swap 島内局所 reflow の rect 補間**を追加（§4 step 3）。fire-point は確定点のみ（commit / ESC）＝per-frame tween と controller の毎フレーム書き込みを競合させない（0088 §14 code-review 1 の教訓を継承）。
- **ESC キャンセル**（0088 §6 維持）: drag 中 ESC で実描画 / ghost を rest へ spring revert・state 不変。チャンネル種別に依らず一律。

## §6 定数（`FloatingWindowMath`）

- `R_SNAP_PX = 96f`（維持）
- `SPRING_DURATION_MS = 200` / `SPRING_OVERSHOOT_RATIO = 0.08f` / `SPRING_BACK_S = 1.5f`（維持）
- **`D_DETACH_PX` 削除**（距離トリガ退役）。
- チャンネル enum `DragChannel { IslandMove, SingleWindowPickup }` 新設。

## §7 不変・波及（維持）

- merge cascade = size 最大 > 辞書順最小 > 新規 GUID（0088 §5・ADR-0024 D5 維持）。
- cross-plane group 禁止・restore split（0088 §12・ADR-0019 D9 維持）。
- programmatic Spawn=null / Close=連鎖 dissolve / Hide=温存（ADR-0024 維持）。
- chart:&lt;id&gt; は両ジェスチャ適用（他 window 同等）。chart 右 strip（depth ladder）は本体 rect 子で追従。
- ADR-0020 first-launch factory grouping 維持（5 base 窓 1 island・本 ADR ルールで分解可能）。

## §AFK 正本再構成（実装着手前に `behavior-to-e2e` を formal invoke して固定）

`FloatingWindowE2ERunner` の対応（DRAG-* は ADR-0024 の cursor 判定・距離 detach・4 値 swap を assert しているため要書換）:

| 旧 section（findings 0088 §13） | 本 ADR での扱い |
|---|---|
| DRAG-01..04（cursor 位置 3 mode 判定 / center-in-rect swap trigger） | **書換**: gesture-channel lock（plain=島移動 / eject・Alt=単窓ピックアップ）・per-frame で化けない assert |
| DRAG-03（島外 ∧ ≥ D_DETACH → detach） | **削除/書換**: detach は距離でなくドロップ先（空き地）。`D_DETACH_PX` 退役 assert |
| DRAG-02（島外 ∧ < D_DETACH → translate） | **書換**: island move は距離無制限（256px 超でも detach しない）assert |
| swap 4 値交換（S29 等） | **書換**: サイズ維持＋島内局所 reflow（A/B の (w,h) 不変・隣窓が flush へ寄る・island scope 限定） |
| DRAG-05/06（magnetic snap） | **維持/微修正**: island move / pickup 両チャンネルで R_SNAP 吸着 |
| DRAG-07（overlap merge） | **維持**: 島移動 merge（Q3）+ pickup の別島 flush merge |
| DRAG-08（detach + dissolve） | **書換**: pickup の空き地 drop → detach + 残 <2 dissolve |
| DRAG-09（ESC）/ DRAG-10（spring 8%） | **維持**: spring trigger に swap reflow を追加 assert |
| DRAG-11..14（Hakoniwa 退役 / chart 同等 / factory） | **維持** |

新規 section（命名は behavior-to-e2e で確定）: gesture-channel lock・eject つまみ起動・Alt-drag 起動・距離撤廃 detach・island move 無制限距離・swap 島内局所 reflow（サイズ維持 + island scope 限定 + 残隙間許容）・swap ghost が reflow 後レイアウトを予告。数値（R_SNAP=96 / spring 200ms / 8%）は exact value assert。

## §HITL（owner playmode 目視）

- swap の "ぷるん 自動調整" の体感（サイズ違い窓を入れ替えた時の島内 reflow が気持ちよく収まるか・残隙間が許容範囲か）。
- eject つまみの発見性・押しやすさ・Alt-drag の操作感。
- 島の遠距離移動が引っかからずできること（不満 1 の解消確認）。
- ドラッグ中に mode が化けないこと（不満 2 の解消確認）。

## §14 実装着地

（実装後に追記）
