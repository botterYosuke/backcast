# findings 0106 — 掴む対象でモード固定するドラッグ（gesture-channel・距離トリガ撤廃・eject/Alt 単窓ピックアップ・swap 島内局所 reflow）

> ⚠️ **一部 superseded（2026-06-25・[[ADR-0032]] / findings 0113）**: 本 findings の **eject つまみ "⤴" 起動経路**（§0-Q2・§1 の `EJECT_HANDLE` 分岐・§3 起動経路・§14 の `FloatingWindowEjectHandle.Attach`・S41/S24 の eject 行）は **ADR-0032 で廃止**され、単窓ピックアップの起動は `Alt`+drag 単独になった。それ以外（2 チャンネル固定・距離撤廃・ドロップ結果・swap reflow・island move・merge・spring・ESC）は**全て有効**。詳細は findings 0113。

方針: **[[ADR-0029]]**。本 findings はその下位設計の木を固定する。
grill: `grill-with-docs`（2026-06-25・owner HITL Q1–Q5）。supersede: findings 0088 §1（cursor 位置 3 mode 判定 `ResolveDragMode`）／§4 の swap 4 値交換と detach 距離経路／§11 の `D_DETACH_PX` 定数／§13 の DRAG-* のうち cursor 判定・距離 detach・swap exact を assert する section。維持: 0088 §2（磁石吸着 R_SNAP）／§3（spring）／§5（merge cascade）／§6（ESC）／§12（cross-plane）／§9（factory grouping）。

実装着地は §14 に記録（#136 S1–S5・2026-06-25・AFK 緑）。AFK 正本再構成（§AFK）は `behavior-to-e2e`（#136 S1・formal invoke）で固定済み＝S24–S41。S6 owner playmode HITL のみ残。

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

## §14 実装着地（#136 S1–S5・2026-06-25）

実装は Unity C# 単言語。AFK 緑（`FloatingWindowE2ERunner.Run` → `[E2E FLOATING WINDOW PASS]`・`error CS` 0・S1–S41 全 GREEN）。S6 owner playmode HITL（§HITL）のみ残。

### pure 算術（`FloatingWindowMath`）
- `enum DragChannel { IslandMove, SingleWindowPickup }` 新設。`enum DropOutcome { Swap, MergeToIsland, Detach }` ＋ `struct DropResolution { outcome, swapTargetId, mergeTargetId }` 新設。
- `ResolveDragMode`（cursor 3 mode）・`D_DETACH_PX`・`enum DragMode`・`struct DragResolution` を**削除**（全ファイルで参照 0 を確認）。
- `ResolveChannel(bool hitEjectHandle, bool altHeld)` ＝ `(eject||alt) ? Pickup : IslandMove`（input 由来の唯一の選択・真理値表 S24/S41）。
- `ResolveDropOutcome(cursor, pickedRect, islandMembers, pickedId, otherWindows, rSnap)`: ① sibling 上（`ResolveDropTarget` 流用）→ Swap、② picked rect が非 island 窓に磁石 engaged **または既に flush**（`ComputeMagneticSnap!=0 || IsFlushAdjacent(…,1f)`）→ MergeToIsland（最前面 sibling）、③ それ以外 → Detach。**罠**: in-drag 磁石で既に gap=0 まで寄った rect は `ComputeMagneticSnap` が 0 を返すので、`IsFlushAdjacent` 併用が必須（S37b で発覚）。
- `ReflowIslandAfterSwap(islandMembers, aId, bId, rSnap) -> Dictionary<string,DockRect>`: ① size 維持で A/B の anchor だけ交換（4 値交換しない）、② id 序数で安定走査・2 pass で島内メンバーを `ComputeMagneticSnap` で flush re-snap、③ 残隙間は許容。scope は渡された島メンバーに厳密限定（他 island/plane は引数に現れず動かない）。`StringComparer.Ordinal` で決定論。
- 維持: `ResolveDropTarget`/`ComputeMagneticSnap`/`ResolveNearestFlush`/`IsFlushAdjacent`/`SpringEase`/`ResolveMergeWinner`/`R_SNAP_PX=96`/`SPRING_*`。

### controller（`FloatingWindowController`）
- `DragSession.channel` 追加（BeginDrag で確定・drag 中不変）。`enum ReleaseResult { IslandMoved, Swapped, Merged, Detached }` 新設（`ReleaseDrag` の戻り値）。
- `BeginDrag(id, start)` は `IslandMove` 既定の overload・`BeginDrag(id, start, channel)` が本体。
- `DragApplyDelta` をチャンネル分岐: IslandMove=`RenderTranslate`（島全員・無制限・magnet・ghost clear）／Pickup=`FreezeIslandToRest`+`RenderDetach`（picked のみ）+ sibling 上なら `ComposeReflowGhosts` を ghost。戻り値 `DragChannel`。
- `ReleaseDrag` をチャンネル分岐: IslandMove=`CommitTranslate`（既存・overlap/flush merge・**detach しない**）→ IslandMoved／Pickup=`CommitPickup`。
- `CommitPickup`: pickedRect=rest+offset+magnet を作り `ResolveDropOutcome` で分岐 — Swap→`CommitSwapReflow`／Merge→`groupId=null` 先行→A を flush 位置へ→`CommitMergeWithTarget`（A 単独だけ Y へ・cascade）→ oldGroup dissolve／Detach→`groupId=null`→ oldGroup dissolve。`CaptureNonIslandMembers` を merge 候補に。
- `CommitSwap`（4 値交換）→ `CommitSwapReflow`（`ReflowIslandAfterSwap` 駆動・各メンバーを spring）に置換。`CommitDetach` は `CommitPickup` に吸収（削除）。
- `ComposeSwapGhosts`（2 枚）→ `ComposeReflowGhosts`（post-swap+reflow で動くメンバーだけ・picked Solid/隣窓 Dashed・size 維持）に置換。

### input（`FloatingWindowTitleInput`）＋ eject つまみ
- `OnBeginDrag` で `channel = ResolveChannel(pointerPressRaycast.gameObject==_ejectHandle, Keyboard.current.leftAltKey.isPressed)` を計算し `BeginDrag(id, rest, channel)`。
- **eject つまみ**は `FloatingWindowEjectHandle.Attach(titleBar, font)`（新規・ThemeService 非依存）が build。`FloatingWindowTitleInput.Awake()` が自分の title-bar に 1 度 attach＝dock/editor/order/HITL の全 title-bar に factory 改変ゼロで均一に出る。つまみ＝chip Image（raycast target）＋ glyph Text（非 raycast）・**drag handler 無し**（press は title input へ bubble・`pointerPressRaycast` で識別）・最後 sibling・**left inset 4px**（当初 right inset 30px だったが close ✕/run ▶ の raycast cluster に埋もれる regression を review が検出し LEFT へ統一＝後述 §code-review 着地）。最終 icon/配置は owner HITL で調整（ADR-0029 §自己保護）。

### AFK 正本（`FloatingWindowE2ERunner`）
S24–S41 を ADR-0029 へ書換/新設（§AFK 対応表どおり）。**RED→GREEN litmus**: 実装中に S25c が実 RED（pickup carry の rest snapshot が前 sub-test の live render で displaced）→ sub-test 間に `CancelDrag` で rest 復帰して GREEN＝ゲートが非空虚であることを実証。delete-the-production-logic litmus（設計上）: `DragApplyDelta` を常時 IslandMove にすると S25c/S29d/S31a が RED、`ReflowIslandAfterSwap` を 4 値交換に戻すと S29a/d・S31a が RED、`ResolveChannel` の真理値表を壊すと S24/S41f が RED。

> ⚠️ **AFK カバレッジの正直な限界（#136 レビュー 2026-06-25 で是正）**: pickup section（S25c/S26a/S29d/S31a/S37g/S40d…）は `c.BeginDrag(id, start, DragChannel.SingleWindowPickup)` で**チャンネルを直接注入**しており、`ResolveChannel` を壊しても RED に**ならない**（RED になるのは pure 真理値表を叩く S24/S41f の 2 本のみ）。**入力層の glue**（`OnBeginDrag` で `eventData.pointerPressRaycast.gameObject == _ejectHandle` を読む eject ヒット判定 ＋ `Keyboard.current` の Alt poll ＋ `BeginDrag(channel)` への受け渡し＝`FloatingWindowTitleInput.cs:98-104`）は EventSystem raycast と実 Keyboard デバイスを要するため batchmode AFK では駆動できず、**どの section もここを通らない**。したがって「eject つまみ/Alt で pickup に入る」起動そのものが実機で死んでも全 AFK が緑のまま——この単一統合点の検証は **owner playmode HITL（§HITL・DRAG-20-HITL）専任**。pure `ResolveChannel` は AFK が、glue は HITL が守る二層であることを明記する（litmus を実態より広く書かない＝#125 の偽 litmus 教訓の継承）。

### code-review(simplify high) 着地（#136・Medium 全消し）
- **pickup merge は island 外 bbox へ flush**（findings 0106 §3 `ResolveNearestFlush(A.rect, Y.bbox)` 準拠）: 当初 in-drag 個別窓 magnet で位置決めしていたが、多メンバー島の**内部 seam に flush して隣窓に重なる**バグ（IslandMove 側 S37f が守っていたのと同型）＋ 位置 target と group target の不一致を review が検出。`CommitPickup` の merge 分岐を `ResolveTargetIslandBbox`+`ResolveNearestFlush`（CommitTranslate overlap 分岐と同じ helper）へ修正＝位置と group を `res.mergeTargetId` に単一化。S37g（縦 2 窓島の内部 seam ではなく外 bbox へ flush）で pin。
- **eject つまみは title-bar の LEFT** へ移動: 当初 right-inset 30px だったが editor/order 窓の右側は close ✕(-3)・cell run ▶(~-28) の **raycast Button cluster**で、後付け sibling に埋もれて press が IslandMove に化ける regression を review が検出。left 端（title Text は非 raycast＝衝突なし）へ統一。最終 icon/配置は owner HITL（DRAG-20-HITL）。
- `FloatingWindowEjectHandle.Attach` は **find-or-create**（NodeName 重複防止）。`ComposeReflowGhosts` は swap target 変化時のみ再計算（per-frame GC 回避）＝この memoize で `_lastSwapTargetId` を BeginDrag で reset する必要が露見し修正（S31f）。Alt は left/right 両対応。`REFLOW_PASSES` 定数化。S25e（singleton-in-name 退役カバレッジ復活）。

### 走査順・残隙間の確定（ADR-0029 §自己保護で findings に記録する下位事実）
- reflow 走査順 = **id 序数昇順・2 pass**（決定論・1 pass では下流隣窓を引き切れないケースを 2 pass が拾う）。収束保証はせず 2 pass 固定（実用上の島サイズで十分・perfect tiling 非保証は owner Q4 どおり）。
- 残隙間/微小重なりは許容（free-placement）。owner HITL（§HITL・DRAG-20-HITL）で体感の許容範囲を確認。
