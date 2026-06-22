# findings 0088 — パズルゲーム体感のドラッグセマンティクス（in-drag 磁石吸着・cursor 位置で 3 mode 動的判定・release-position commit）

方針: **[[ADR-0024]]**（puzzle-feel drag magnetic snap / swap-translate-detach / merge）。本 findings はその下位設計の木を固定する。
grill: `grill-with-docs`（2026-06-22・owner HITL Q1–Q18）。supersede: findings 0082 §2（Hakoniwa group 判定）／§4（Hakoniwa-priority merge cascade）／§7（commit-on-release ghost spec の ghost-only 部）／§5 中 `D_DETACH=64f` の値部・`EvaluateDragMode` の単純距離判定。維持: 0082 §1（groupId persist）／§3（flush attach）／§6（cross-plane restore split）／§8（schema）／§9（ADR-0019 D1/D2/D4/D9 不変）。

実装着地（§14）は未記入（実装後に追記）。AFK 正本拡張（§13）は実装着手前に `behavior-to-e2e` を formal invoke して固定する。

## §0 owner が選んだ 18 の分岐（HITL）

1. **Hakoniwa special 廃止**（Q1 A）= startup / run_result を含む island を特別扱いしない・core member 概念退役。
2. **同 island swap on overlap**（Q2 A）= 同じ groupId のメンバー B の rect 内で release → A・B の (x,y,w,h) を 4 値交換。
3. **cross-island swap 禁止**（Q3 B）= 別 groupId メンバーへの drop は swap にならない（merge 経由 or snap back）。
4. **2-tier 閾値**（Q4 B）= 短い外方向 drag → island translate / 強い引き剥がし → detach。
5. **cancel = 自動 + ESC**（Q5 B）= 無効ドロップ自動 cancel + drag 中の ESC で能動 cancel。
6. **離散 snap**（Q6 A）= 物理シミュレーションではなく閾値で一発吸着・spring easing で "プルン"。
7. **center-in-rect swap trigger**（Q7 A）= dragged center が target rect 内に入った瞬間に swap プレビュー ON。
8. **island 外周全体が磁石**（Q8 B）= dragged window 自身の辺だけでなく、移動中の island の outer 全辺が吸着判定対象。
9. **release on overlap → 最寄り flush merge**（Q9 B）= release で別 island と overlap してたら最寄り flush へ snap → merge。
10. **同 island は swap 優先 / 別 island は merge**（Q10 A・owner refine）= cursor が同じ island のメンバー rect 内なら swap、外（空き or 別 island）なら island translate（release で別 island overlap なら merge）。
11. **detach は 2-tier の外側**（Q11 A）= 短い外方向 → island translate、ある閾値（D_DETACH）超え → detach 単独。
12. **detach + overlap → merge（singleton 経由）**（Q12 B）= release-position rule universal、detach 中でも overlap 位置で別 island に編入。
13. **混合描画**（Q13 B）= translate / detach は実描画・swap は ghost 2 枚。
14. **spring 200ms / 1-overshoot**（Q14 B）= "プルン" の onomatopoeia 直訳。
15. **R_SNAP = 96px**（Q15 C）= タイル幅 250px の ~1/2.6・"もっと強く" 要件に応えるが超強めではない。
16. **D_DETACH = 256px**（Q16 C）= R_SNAP の ~2.7 倍・タイル 1 つ分・意図的引き剥がし。
17. **factory grouping 維持**（Q17 A）= first-launch の 5 base 窓 1 island は ADR-0020 通り。
18. **ADR-0024 + findings 0088 草稿化** で確定（Q18 A）。

## §1 ドラッグ判定の状態機械（cursor 位置で動的決定）

毎フレーム evaluate。記号: A = dragged window、I₁ = A が属する island（A.groupId 共有・visible/live ≥ 2、singleton なら I₁ = {A}）、`drag_start` = pointer-down 時の cursor の canvas-logical 座標、`cursor` = 現フレーム cursor 座標。

```
function ResolveDragMode(A, I₁, drag_start, cursor):
    # 1. swap: cursor が同じ island の別メンバーの rect 内
    for B in I₁ \ {A}:
        if rectOf(B).Contains(cursor):
            return SWAP(target = B)

    # 2. detach: drag-start から D_DETACH 以上離れた
    if distance(cursor, drag_start) >= D_DETACH:
        return DETACH(A)

    # 3. island translate（残りのケース・|I₁| == 1 でも適用＝singleton 自由移動）
    return TRANSLATE(I₁, offset = cursor - drag_start)
```

- **center-in-rect**: swap trigger は dragged window の center（rect の中央）が target rect 内に入った瞬間。`Rect.Contains(center)` で 1 行判定。
- **A の center の出元**: A は drag 中 cursor 追従するので、center = cursor + (drag-start での A.center - drag_start のオフセット)。実装簡略化のため `cursor` 直接でも可（A が小さい場合の差は無視できる）。
- **singleton の扱い**: |I₁| = 1 のとき SWAP target は無く、TRANSLATE か DETACH のどちらか。singleton も island 同様に動く（detach 概念は無関係＝既に独立）。

## §2 in-drag 磁石吸着（R_SNAP = 96px・離散 spring snap）

translate / detach モードで毎フレーム発動。swap モードでは発動しない（swap は target rect 内に center が来た時のみで、edge attraction は不要）。

```
function ComputeMagneticSnap(movingEdges, otherEdges, R_SNAP):
    # movingEdges: TRANSLATE なら I₁ の outer 4 辺、DETACH なら A の 4 辺
    # otherEdges: I₁ に属さない visible/live window の 4 辺の集合
    best_offset = (0, 0)
    best_dist = ∞
    for me in movingEdges:
        for oe in otherEdges:
            if isOppositeEdge(me, oe):                        # left vs right / top vs bottom
                if hasOrthogonalOverlap(me, oe):              # 直交軸で線分が重なる
                    d = abs(me.coord - oe.coord)
                    if d <= R_SNAP and d < best_dist:
                        best_dist = d
                        best_offset = snapOffsetFor(me, oe)   # me を oe にぴったり寄せる offset
    return best_offset
```

- **発動結果**: 戻り値 offset を実描画位置に加える（TRANSLATE なら I₁ 全メンバーに、DETACH なら A 単独に）。
- **animation**: offset がフレームをまたいで変化する場合（吸着が ON になった瞬間）は **spring 200ms** で補間（§3）。継続的な吸着中は cursor が動いても snap 位置に "貼り付いた" 表示になる（cursor が R_SNAP の外に出るまで離れない＝stickiness）。
- **release で flush merge**: release 時点で magnetic snap が active なら、その offset を確定した位置で commit（§4 の overlap merge と統合：snap 位置は最寄り flush と一致するので "release on overlap" の極限 case と同等）。

## §3 spring animation（200ms / 1-overshoot "プルン"）

```
function AnimateRectSpring(window, fromRect, toRect, ms = 200):
    # Unity Animator / LeanTween.tween で 1 関数化
    # overshoot 量 = 8% 程度（toRect 中心から (toRect - fromRect) 方向に 8% 行き過ぎてから戻る）
    # easing curve: ease-out-back（overshoot 1 回・収束）
```

- **trigger**:
  - magnetic snap 発動の瞬間（cursor が R_SNAP 圏に入った時）
  - ESC キャンセル（rest へ revert）
  - merge / detach commit 完了時の rect 確定
  - swap commit 完了時の A・B 入れ替え
- **重複発火**: 同じ window で前の spring が走っている間に次の spring が走っても汚く見えない範囲（overshoot 8% / 200ms / 1 回）に値を抑える。前 tween は kill して新 tween へ。

## §4 release-position commit（universal rule）

```
function CommitOnRelease(mode, A, I₁, cursor):
    match mode:
        case SWAP(B):
            swap(A.rect, B.rect)                        # (x,y,w,h) 4 値交換
            # groupId は両者不変

        case TRANSLATE(I₁, offset):
            # cursor が別 island Y のメンバー rect と overlap してたら最寄り flush + merge
            Y = findIslandAtCursor(cursor) \ I₁         # I₁ 除く
            if Y is not None:
                snappedRect = resolveNearestFlush(I₁.bbox + offset, Y.bbox)
                appliedOffset = snappedRect - I₁.bbox
                applyOffset(I₁, appliedOffset)
                mergeIslands(I₁, Y)                     # merge cascade で生き残る groupId 決定
            else:
                # 空きスペース：そのまま translate
                applyOffset(I₁, offset)
                # 偶発的 flush attach: 端の magnetic snap で flush になってたら attach
                if isFlushAdjacent(I₁.bbox, some Y):
                    mergeIslands(I₁, Y)

        case DETACH(A):
            # A は I₁ から離脱
            A.groupId = null
            if |I₁.remaining_visible_live| < 2:
                dissolveIsland(I₁)                       # 残メンバーも groupId = null

            # cursor が別 island Y のメンバー rect と overlap してたら singleton 経由で merge
            Y = findIslandAtCursor(cursor) \ I₁
            if Y is not None:
                snappedRect = resolveNearestFlush(A.rect, Y.bbox)
                A.rect = snappedRect
                A.groupId = Y.groupId                    # merge cascade で確定
            else:
                # 空きスペース：A は singleton（groupId = null）でその位置に置かれる
                A.rect = cursor 中心の rect
                # 偶発的 flush attach: A.edges が他 window と flush なら attach
                if isFlushAdjacent(A.rect, some Y):
                    A.groupId = Y.groupId
```

- **`resolveNearestFlush(movingRect, targetIslandBbox)`**: moving と target の 4 辺ペア（left/right, top/bottom）の中で、moving を target に寄せるための最小オフセットを返す。具体的には moving.right を target.left に / moving.left を target.right に / moving.bottom を target.top に / moving.top を target.bottom に それぞれ寄せた 4 候補から、現在位置からの移動距離が最小のものを選ぶ。直交軸の overlap が ≥ 1px ある候補のみ valid。
- **複数候補の tie-break**: 同じ距離なら left/right 優先（横ドッキング）→ top/bottom（縦）。
- **A の rect サイズ**: detach 時の A.rect は drag 開始時の A の (w, h) を維持・(x, y) のみ変更。

## §5 merge cascade（Hakoniwa-priority 除去で simplify）

ADR-0019 D5 を以下に置換:

```
function mergeCascade(islands):
    # islands: 同時に flush 隣接で merge 対象になった ≥ 2 個の island の集合
    # 生き残る groupId を以下の順で決定:
    return max(islands, key = lambda I:
        (I.size,           # 1. メンバー数最大
         -I.groupId))      # 2. 同点なら辞書順最小（マイナスで max → 最小取得）
    # 全部 size 1（singleton 同士）なら新規 GUID を mint
```

- ADR-0019 D5 の「Hakoniwa group 単独」優先は退役（Hakoniwa special 廃止＝Q1）。
- 同点が新規 GUID になるケースは singleton 同士の attach（例: chart:A と chart:B を flush attach）。
- merge 後の負け側 island のメンバーは生き残る groupId へ rewrite。

## §6 ESC キャンセル（drag 中の能動 revert）

```
function OnEscapeKey(dragState):
    if dragState.active:
        # 実描画していた位置（TRANSLATE / DETACH）を rest（drag 開始時の位置）へ spring 200ms で戻す
        # ghost（SWAP）は消すだけ
        for w in I₁:
            AnimateRectSpring(w, current = w.actualRect, to = w.restRect, ms = 200)
        clearDragGhosts()
        dragState.cancel()   # commit を skip して mouse release で何もしない
```

- **state 復元**: drag 中は groupId / geometry の真の state は drag 開始時のスナップショット（`drag_start_snapshot`）で凍結されており、ESC は実描画レイヤーを snapshot へ revert するだけ。state は元から動いてない。
- **キーバインド衝突**: ESC は他に何かに使われていないか確認（Window close / dialog close 等の競合チェック→§13 AFK で assert）。

## §7 描画レイヤー分担（混合：translate/detach 実描画・swap ghost）

```
function RenderDragPreview(mode):
    match mode:
        case SWAP(target):
            # 実窓は drag-start snapshot で rest 表示・preview は ghost 2 枚
            renderGhost(A, target.restRect)          # solid border ghost at target slot
            renderGhost(target, A.restRect)          # dashed border ghost at A's slot

        case TRANSLATE(I₁, offset):
            # 実窓を offset 分シフトして描画（magnetic snap 補正込み）
            actualOffset = offset + ComputeMagneticSnap(I₁.outerEdges, otherEdges, R_SNAP)
            for w in I₁:
                renderActual(w, w.restRect + actualOffset)
            # ghost なし

        case DETACH(A):
            # A だけ実窓を cursor 位置で描画・残メンバーは rest
            actualOffset = (cursor - drag_start) + ComputeMagneticSnap([A.edges], otherEdges, R_SNAP)
            renderActual(A, A.restRect + actualOffset)
            for w in I₁ \ {A}:
                renderActual(w, w.restRect)              # rest
            # ghost なし
```

- **commit-on-release**: 実描画していた rect は drag 中は state に書き戻さない。MouseUp で `CommitOnRelease`（§4）が呼ばれて初めて state 更新。
- **ESC キャンセル**: render を rest 位置へ spring 200ms で戻すだけで、state は touch しない。

## §8 chart:&lt;id&gt; への波及

- chart は他 window と全く同じドラッグルール（owner panel 列挙で chart を含む）。
- programmatic Spawn = groupId null（ADR-0019 D8 維持）。
- universe sync 削除（programmatic Close）= 連鎖 dissolve（findings 0082 §7 維持）。
- chart の右 strip（depth ladder）は drag 中も window 本体に追従（chart の rect transform 子）＝ drag 実描画と一緒に動く。

## §9 ADR-0020 first-launch factory grouping への影響

- ADR-0020 維持（Q17 A）。`FormFactoryBaseGroup()` が saved layout 無しの初回起動で 5 base 窓に 1 つの groupId を stamp する経路は touch しない。
- Hakoniwa special が消えても「5 base 窓を 1 island で起動」する usability 決定は独立で生きる。
- factory island は通常 island なので、本 ADR の全ルール（swap / translate / detach / merge）がそのまま適用される。

## §10 旧 AFK section の退役 / 書換リスト

`FloatingWindowE2ERunner` の以下 section は本 ADR の挙動と矛盾するため修正対象:

| 旧 section | 旧 assertion | 本 ADR での扱い |
|---|---|---|
| GROUP-01..05（Hakoniwa group 判定） | core 含み判定で Hakoniwa group に昇格・全体 translate 禁止 | **削除**（Hakoniwa special 廃止） |
| GROUP-06..08（Hakoniwa swap） | core 含み group の内部 drag は swap のみ | **書換**: 同 island swap が全 group に適用（cursor-in-rect 判定） |
| GROUP-09..10（通常 group translate） | non-core group は drag で全体 translate | **書換**: cursor が island 外 ∧ < D_DETACH の時に translate（全 group 共通） |
| GROUP-11..12（detach + core lock） | non-core は D_DETACH=64 超で detach・core は detach 不可 | **書換**: D_DETACH=256 へ再較正・core lock 削除 |
| GROUP-13（ghost spec） | ADR-0019 D8 ghost-only preview | **書換**: translate/detach は実描画 assert・swap のみ ghost 2 枚 |
| S32（factory grouping） | ADR-0020 first-launch base cluster | **維持**（factory grouping は本 ADR と無矛盾） |

新 section は §13 で命名（DRAG-01..14）。

## §11 R_SNAP / D_DETACH / spring の定数置き場

- `FloatingWindowMath.R_SNAP_PX = 96f`
- `FloatingWindowMath.D_DETACH_PX = 256f`
- `FloatingWindowMath.SPRING_DURATION_MS = 200`
- `FloatingWindowMath.SPRING_OVERSHOOT_RATIO = 0.08f`（8%）

これら 4 つを pure 定数として 1 箇所に集約。controller / input / ghost / spring animation 全レイヤーが同 source から参照。AFK は数値そのものを assert（chunk 単位の感覚値ではなく exact value gate）。

## §12 cross-plane / restore 整合

- ADR-0019 D9 維持: cross-plane group は禁止・restore で多数派 plane 残し / 同数 dock 優先 / 負け側 null。
- 本 ADR で新たに cross-plane 状態が起こることはない（_dockWindows と _windows は controller 分離・drag は per-plane）。

## §13 AFK 正本拡張（実装着手前に `behavior-to-e2e` を formal invoke して固定）

新規 section（数値・mode・preview の assert を pure 算術 + scene probe で行う）:

- **DRAG-01**: cursor が同 island メンバー rect 内 → SWAP mode・ghost 2 枚（dragged at target slot / target at dragged slot）・実窓は rest
- **DRAG-02**: cursor が island 外 ∧ < D_DETACH → TRANSLATE mode・全 island メンバーが offset 分実描画でシフト・ghost なし
- **DRAG-03**: cursor が island 外 ∧ ≥ D_DETACH → DETACH mode・A だけ実描画で cursor 追従・残メンバー rest・ghost なし
- **DRAG-04**: SWAP の center-in-rect trigger（cursor が target rect 内に入った瞬間に発火・出た瞬間に消える）
- **DRAG-05**: TRANSLATE 中の magnetic snap（island outer edge が他 window edge と R_SNAP 内・flush 位置へ実窓ごと snap・cursor が R_SNAP 外に出るまで貼り付く stickiness）
- **DRAG-06**: DETACH 中の magnetic snap（A の edge が他 window edge と R_SNAP 内・A だけ flush へ snap）
- **DRAG-07**: release on overlap with different island → 最寄り flush + merge（TRANSLATE / DETACH の両方で）
- **DRAG-08**: release on empty space → TRANSLATE は island translated commit / DETACH は A.groupId = null（残 < 2 で島 dissolve）
- **DRAG-09**: ESC during drag → spring 200ms で rest へ revert・state は不変・mouse release で何も commit しない
- **DRAG-10**: spring animation の rect 補間（200ms / overshoot 8%・1-overshoot で収束を assert）
- **DRAG-11**: 旧 Hakoniwa special の **退役 assert**（startup を含む island でも cursor 位置に応じて translate / detach が発火・core 概念無し）
- **DRAG-12**: ADR-0019 D5 Hakoniwa-priority merge cascade の退役（size 最大 / 辞書順最小 / 新規 GUID のみで決定）
- **DRAG-13**: chart:&lt;id&gt; が他 window と同等のドラッグルールで動く（startup と chart:&lt;id&gt; が island を組める・swap も translate も merge も発火）
- **DRAG-14**: ADR-0020 first-launch factory grouping を変えない（5 base 窓 1 island で起動・本 ADR ルールで分解可能）

数値（R_SNAP=96 / D_DETACH=256 / spring 200ms / overshoot 8%）は section 内で exact value assert。

## §14 実装着地（2026-06-22）

issue #108–111（S1–S4）を 1 セッションで連続実装（owner all-in 指示「実装コストは度外視・理想的な完成形」）。

**支配的変更（production）**:
- `FloatingWindowMath`: 旧 7-mode `DragMode`/`DragContext`/`EvaluateDragMode`/`D_DETACH=64f` を退役 → 3-mode `DragMode {Swap,Translate,Detach}` + `DragResolution` + `ResolveDragMode`（cursor 位置動的・swap は距離不問・detach は `≥ D_DETACH_PX` inclusive）。新定数 `D_DETACH_PX=256f` / `R_SNAP_PX=96f` / `SPRING_DURATION_MS=200` / `SPRING_OVERSHOOT_RATIO=0.08f` / `SPRING_BACK_S=1.5f`。新 pure 関数 `ComputeMagneticSnap`（flush within R_SNAP・直交 overlap 必須・x/y 独立）/ `ResolveNearestFlush`（4 候補最小移動・tie-break x）/ `SpringEase`（ease-out-back・peak overshoot を s=1.5 で **厳密に 8%**＝`4s³/(27(s+1)²)=0.08`・t=0.6）/ `SpringRectAt`。`ResolveMergeWinner` から Hakoniwa-priority 除去（`MergeCandidate.hasCore` 削除）= size 最大 > 辞書順最小 > null。
- `FloatingWindowController`: `DragSession` snapshot（island ids + rest rects）導入。`BeginDrag`/`EnsureDragSession`/`EndDrag`/`CancelDrag`。`DragApplyDelta` を **絶対オフセット実描画**（rest + (cursor-dragStart) + magnetic snap）の 3-mode へ反転（Swap=freeze+ghost / Translate=島全員 / Detach=A のみ）。`ReleaseDrag`→`CommitOnRelease`（Swap=4 値交換 / Translate=overlap なら ResolveNearestFlush+merge・empty は magnetic+D4 flush attach / Detach=groupId=null+dissolve・overlap は singleton merge・excludeGroupId=oldGroup）。ghost を `ComposeSwapGhosts`（swap 専用 2 枚）へ縮小。spring 注入 seam `SetSpringAnimator` + `FireSpring`（fire-point は**確定点のみ**: swap / translate / detach commit / ESC revert。mid-drag の磁石吸着は離散 hard-set＝per-frame tween で実描画を奪わない・code-review 修正参照）。`IsCoreKind` 参照を drag/merge から除去（`DockShape.IsCoreKind` 自体は ADR-0020 factory 列挙のため残置）。
- `FloatingWindowTitleInput`: `BeginDrag` 呼び出し・`Update()` で InputSystem `Keyboard.current.escapeKey` poll → `CancelDrag`・ESC 後の OnDrag/MouseUp は no-op。
- `RectSpringDriver`（新規 MonoBehaviour）: SpringEase を 200ms サンプリングする production tween（kill-and-replace）。`BackcastWorkspaceRoot` が 1 体生成し両 plane controller へ `SetSpringAnimator`。
- `BackcastWorkspaceRoot.FormFactoryBaseGroup`: 「Hakoniwa group」→「plain island」へコメント整合（挙動は不変＝base 5 窓 1 island 起動）。

**AFK GREEN（`FloatingWindowE2ERunner`・exit 0・error CS 0）**: 旧 GROUP-03/05/06/07/08/10/12/14 を退役/書換、GROUP-01/02/04/11 維持。新 section 24–40 で **DRAG-01..14** 網羅:
- 24=DRAG-01..04（ResolveDragMode pure）/ 25=DRAG-02/03（real-render）/ 26=DRAG-08（detach+dissolve）/ 27=DRAG-11（Hakoniwa 退役）/ 28=DRAG-01/04（drop target）/ 29=DRAG-01（swap commit）/ 31=DRAG-14（ghost swap-only）/ 32=DRAG-14（factory plain island）/ 33=DRAG-05/06 pure / 34=DRAG-07 pure / 35=DRAG-10（spring 8% 厳密）/ 36=DRAG-05/06 wiring / 37=DRAG-07（overlap merge）/ 38=DRAG-09（ESC）/ 39=DRAG-10（spring fire-point）/ 40=DRAG-13（chart 同等）。
- RED→GREEN: 実装中の唯一の RED は S40c（chart を真下 drag で cursor が startup 右端=inclusive 境界に入り Swap 誤判定）→ cursor を矩形外（右下）へずらして GREEN。`ResolveDragMode` の swap 判定が edge-inclusive な性質を突いた正当な検出。

**HITL 未**: spring の体感（200ms/8%）・ESC キーバインドの実機操作感・"プルン" の見た目は owner playmode 目視（findings §3 / §13 DRAG-10 は pure 曲線で gate 済み）。

**code-review(high) 修正履歴**（3 件・2026-06-22）:
1. **mid-drag spring 再発火 + per-frame 書き込み競合**（CONFIRMED）: 旧実装は磁石吸着 engage 時に `RenderTranslate`/`RenderDetach` から `FireSpring` を edge-trigger していたが、(a) DETACH 経路で `FreezeIslandToRest` が `snapEngaged=false` をリセット → 毎フレーム再発火、(b) `RectSpringDriver` の tween が controller の毎フレーム絶対書き込みと競合し 200ms カーソル追従が止まる。**修正**: mid-drag spring を撤去し離散 hard-set に統一（findings §2「離散 snap」と整合）。spring は **commit / ESC の確定点のみ**で発火（その後 per-frame 書き込みが続かないので tween と競合しない）。`DragSession.snapEngaged` 削除。
2. **island-wide merge 漏れ**（CONFIRMED）: `CommitTranslate` が島 bbox を `ResolveNearestFlush` で Y に flush snap しても、merge 駆動の `CommitFlushAttachOnRelease(id)` が **dragged 自身の辺**しか flush 走査しないため、dragged 以外のメンバー辺で Y に接した時に merge し損なう（ADR-0024 §4「snap → merge」契約違反・旧 NormalGroupTranslate から継承した underapprox）。**修正**: flush 走査を **島全メンバーの辺**へ一般化（dragged の現 group の visible/live 全員を source に）。detach は groupId=null 後なので source=dragged 単独（不変）。**回帰テスト S37(d)** 追加（多メンバー島が非 dragged 辺で flush → merge）。
3. **doc-rot コメント**（Low）: `DragGhostLayer` ヘッダ・`FloatingWindowController` の `EvaluateDragMode` 参照・`FloatingWindowTitleInput` の旧 mode 名を ADR-0024 へ整合。

再走: AFK `FloatingWindowE2ERunner` GREEN（exit 0・error CS 0）を 3 件修正後に再確認。

**code-review(high) 第2ラウンド 修正履歴**（4 件・2026-06-22）:
4. **spring tween 再 grab 競合**（CONFIRMED）: commit/ESC の 200ms spring tween が走っている間に同窓を再 grab すると、`BeginDrag` が overshoot 途中の rect を rest として snapshot し、瀕死 tween が新 drag の毎フレーム書き込みと競合。**修正**: 注入 seam に `springStop`（`RectSpringDriver.Stop`＝tween を target へ settle して除去）を追加し、`BeginDrag` で island 全メンバーの tween を停止してから snapshot。**S39d** で検証。
5. **overlap-merge が非矩形 island で漏れる**（CONFIRMED）: `CommitTranslate`/`CommitDetach` の overlap 経路が島 bbox を `ResolveNearestFlush` で Y に snap しても、merge を member-level flush 再走査（`CommitFlushAttachOnRelease`）に委ねていたため、bbox 辺を定義するメンバーが Y と直交 overlap しない（mixed-size 島）と merge し損なう。**修正**: overlap 経路を **release-position rule 直結**の `CommitMergeWithTarget(dragged, Y)` に変更（cursor が Y 上＝幾何に依らず Y へ編入）。empty 経路は従来の incidental flush attach（D4）維持。**S37e** で検証。
6. **per-frame drag アロケーション**（efficiency）: `DragApplyDelta` が毎フレーム `BuildIslandMembers`/`CaptureNonIslandRects`/`IslandRestBbox` を再構築（島・外部窓は drag 中不変）。**修正**: `DragSession` に `islandBbox`/`islandMembers`/`nonIslandRects` を `BeginDrag` で 1 度 snapshot しキャッシュ（3 ヘルパ削除）。
7. **`RectSpringDriver.Update` の毎フレーム List 確保**（efficiency）: `new List(_tweens.Keys)` を毎フレーム確保（自身のコメントの「allocates nothing」と矛盾）。**修正**: 再利用 `_keys` バッファ化。

再走: 第2ラウンド 4 件修正後も AFK GREEN（exit 0・error CS 0・変更ファイル warning 0）。S37e / S39d 追加で回帰網拡張。
