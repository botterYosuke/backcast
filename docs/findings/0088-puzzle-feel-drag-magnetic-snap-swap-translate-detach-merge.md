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

## §14 実装着地（実装後追記）

- 実装スライス（→ `/to-issues` で issue 化）
- 着地コミット
- AFK GREEN（DRAG-01..14）
- code-review(simplify) 後の修正履歴
