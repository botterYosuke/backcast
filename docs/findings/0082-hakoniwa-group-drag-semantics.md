# findings 0082 — Hakoniwa window group とドラッグ移動セマンティクス（persistent `groupId`／一体移動／swap／detach／ghost）

方針: **[[ADR-0019]]**（Hakoniwa window group とドラッグ移動セマンティクス）。本 findings はその下位設計の木を固定する。
grill: `grill-with-docs`（2026-06-21・owner HITL Q1–Q9）。supersede: findings 0075 §1（snap は関係を保存しない）／ADR-0017 D2（結合なし）／ADR-0017 §6 スキーマ追加 0（groupId 1 フィールドだけ）。再利用: [[ADR-0018]]（plane 分離・cross-plane snap 禁止）／findings 0008（floating window seam）／findings 0075（dock plane 構成）。

実装着地（§13）は未記入（実装後に追記）。AFK 正本拡張（§12）は実装着手前に `behavior-to-e2e` を formal invoke して固定する。

## §0 owner が選んだ 9 つの分岐（HITL）

1. **group の保持** = persistent `groupId`（B）+ cross-plane group 禁止 + snap/contact 判定は座標 / 関係は groupId の二層モデル。
2. **Hakoniwa group** = 同一 `groupId` を持つ visible/live ≥ 2 ∧ core（`startup` ∨ `run_result`）含む。core は **detach 不可**。
3. **detach 閾値** = `D_DETACH = 64f` canvas-logical px・距離一本（A）。zoom 非依存。
4. **swap drop target** = カーソル直下の同 group メンバー（α）／**(x,y,w,h) 4 値交換**（P）／target なし `< D_DETACH` は **snap-back**（I）。
5. **drag 中の役割分担** = hybrid（A）= 単独 / 通常 group translate は実描画・detach / Hakoniwa swap / snap-back / core lock は ghost。**commit-on-release**（I）。**ghost 視覚** = alpha 0.45・kind accent border・dragged solid / target dashed。
6. **attach トリガ** = flush 隣接のみ（α）= `|edge - opposite_edge| ≤ 1px` ∧ 直交軸 overlap > 0。**merge 衝突** = Hakoniwa group 単独 > size 最大 > 辞書順最小 > 新規 GUID（i）。**groupId 形式** = `grp_<Guid.NewGuid().ToString("N")>`。
7. **detach commit** → dragged.groupId=null、残 visible/live 1 → 連鎖 dissolve（残 1 も null）、0 → no-op。**Hakoniwa 判定** = drag 開始時点の visible/live 集合で動的再評価（mode-switch で昇格/降格）。**hidden core** は明示ルール不要（hidden は pointer event 受けない）。**mode-switch hide は groupId 温存**（detach commit のみ dissolve）。
8. **永続スキーマ** = `floatingWindows[*].groupId: string?` 追加（A）（ADR-0017 §6 を 1 フィールドだけ supersede）。**cross-plane restore split** = 多数派 plane 残し / 同数 dock 優先 / 負け側 null（I）。**Close** = 連鎖 dissolve、**Spawn** = null（attach はユーザ drag-release だけ）。
9. **ドキュメント着地** = 新規 ADR-0019（1 枚で 9 点 supersede）+ findings 0082（本書）+ CONTEXT.md inline 更新（`Hakoniwa` / `floating window` refine + 新設 6 用語）+ `behavior-to-e2e` 実装前 formal invoke。

## §1 persistent `groupId` モデル

- **データモデル**: `FloatingWindowLayout.groupId: string?`（nullable・GUID `grp_<hex32>`・例 `grp_a1b2c3d4e5f6470b9c2d1a3f4b5c6d7e`）。null = 単独 window。
- **唯一の真実源**: 座標から group を再導出しない。`groupId` フィールドが all-source。
- **生成**: `"grp_" + Guid.NewGuid().ToString("N")`（hex32・hyphen なし）。
- **runtime**: `FloatingWindowController.Entry` に `string groupId` を追加（既存 `kind / id / rt` と並ぶ）。
- **二層モデル**: 「くっついている」の判定は **groupId 共有**（運用層）／新規 attach 検出は **flush 隣接判定**（座標層）。snap on release で座標判定→groupId update。
- 同一 `groupId` 共有メンバーが visible/live で 2 個以上のときだけ group として運用される（singleton は実質単独）。

## §2 Hakoniwa group / core 定義

- **Hakoniwa group の判定**: `visibleGroup.Count >= 2 && (startup ∈ visibleGroup || run_result ∈ visibleGroup)`。drag 開始時点で動的再評価。
- **core member**: `kind == "startup"` または `kind == "run_result"`。
- **挙動分岐**:
  - 通常 group（core 非含み）: drag 全体 translate 可能・非 core メンバー detach 可能。
  - Hakoniwa group: group 全体 translate 禁止・内部 drag は swap・非 core は detach 可・core は detach 不可。
- **動的再評価**: Replay → Live で `startup` hidden、`(startup, orders)` group は visible 1 に縮退 → 通常単独 window として `orders` が自由移動可能。Replay 復帰で再昇格。
- **hidden core**: hidden = pointer event 受けない → drag 対象にならない → 明示「hidden core は detach 不可」のルール不要。

## §3 attach 条件（flush 隣接）

- **トリガ**: drag release の `SnapOnRelease` 適用後、または `SpawnDockedToFocus` 配置後に評価。
- **物理条件**: dragged window の任意の辺と、他 visible/live window の対辺が `ε = 1px` 以内（canvas-logical）∧ **直交軸の overlap > 0**（辺がセグメントとして触れている・コーナーのみの接触は除外）。
- **same-edge 整列のみ**（left↔left / top↔top 等）かつ flush でないものは attach しない（離れて並んだ状態は group ではない）。
- **対象外**: hidden / inactive な window は判定対象外。
- **pure 算術**: `FloatingWindowMath.IsFlushAdjacent(DockRect a, DockRect b, float eps)` を新設（headless / AFK 権威）。

## §4 merge 衝突解決

dragged window が release で複数 window と flush 隣接した場合、それぞれが既に group を持ちうるので merge が起きる。生き残る `groupId` を以下の cascade で決定:

1. **Hakoniwa group が単独**: 関与 group のうち Hakoniwa group が 1 つだけなら、その `groupId` が生き残る。
2. **Hakoniwa group が複数 or 不在**: メンバー数が最大の `groupId`。
3. **同数**: 辞書順最小の `groupId`。
4. **全部 null（singleton 同士）**: 新規 GUID を生成。

merge 対象には dragged の隣接相手だけでなく、それらの**既存 group の全メンバー**を含めて 1 つにまとめる。

## §5 detach / dissolve lifecycle

- **detach commit**（release 時に `|cursor - rest| > D_DETACH` を満たす非 core 操作）:
  1. `dragged.groupId = null`
  2. 元 group の残メンバー（dragged を除く同 groupId の visible/live 集合）を数える
  3. 残 visible/live `>= 2` → group 維持（Hakoniwa 判定は visible/live ベースで再評価）
  4. 残 visible/live `== 1` → 残 1 人の groupId も null（連鎖 dissolve）
  5. 残 visible/live `== 0` → no-op
- **core member の detach 試行**: 不可。release 時に rest snap-back（位置は drag 開始時の rest へ戻る）。
- **hide では dissolve しない**: mode-switch で `startup` hidden になっても groupId 温存（Replay 復帰で group 復活）。dissolve は detach commit と Close のみ。
- **例**:
  - `(startup, run_result, positions)` から `positions` detach → 残 `startup + run_result` → Hakoniwa group 維持
  - `(chart, orders)` から `chart` detach → 残 `orders` → orders.groupId=null
  - `(run_result, orders)` から `orders` detach → 残 `run_result` → run_result.groupId=null

## §6 drag mode 判定と `D_DETACH = 64f`

- **閾値**: `D_DETACH = 64f` canvas-logical px。`FloatingWindowMath` 側 pure 定数（例: `public const float D_DETACH = 64f;`）。
- **判定**: `|cursor.position - rest.position| > D_DETACH` で detach 状態へ遷移。zoom 非依存。
- **rest** = 各操作タイプの「離したら戻る位置」:
  - 通常 group: drag 開始時の当該 window 位置 + group_translation
  - Hakoniwa group: swap 判定後の "離したら置かれる" 位置（target hover あり→target rect / target なし→元 rest）
- **drag mode 列挙**:
  1. `SoloDrag`（groupId=null）= 既存 #15 挙動
  2. `NormalGroupTranslate`（group ∧ 非 Hakoniwa ∧ `< D_DETACH`）
  3. `NormalGroupDetach`（group ∧ 非 Hakoniwa ∧ `≥ D_DETACH`）
  4. `HakoniwaSwap`（Hakoniwa ∧ `< D_DETACH` ∧ target あり）
  5. `HakoniwaSnapBack`（Hakoniwa ∧ `< D_DETACH` ∧ target なし）
  6. `HakoniwaDetach`（Hakoniwa ∧ `≥ D_DETACH` ∧ 非 core）
  7. `HakoniwaCoreLock`（Hakoniwa ∧ `≥ D_DETACH` ∧ core）
- **距離一本**: 速度・修飾キー・方向条件なし。pure 算術 = AFK で検証可能。

## §7 Hakoniwa swap

- **drop target**: カーソル直下にある、同じ `groupId` の他 visible/live メンバー。
  - dragged 自身を除外
  - hidden / inactive を除外
  - 複数 window がカーソル直下に重なっていたら最前面 sibling を優先
  - カーソルがどの member 矩形にも乗っていなければ no-target
- **交換**: dragged ↔ target で `(x, y, w, h)` 4 値を入れ替え。kind / id / content / groupId は不変。group footprint 不変。
- **target なし `< D_DETACH`**: snap-back（dragged は元 rest へ戻す・group は変化なし）。
- **pure 算術**: `FloatingWindowMath.ResolveDropTarget(Vector2 cursor, IList<(string id, DockRect rect, int siblingIndex)> groupMembers, string draggedId)` → `string?`。

## §8 ghost preview

- **視覚仕様**:
  - alpha = 0.45
  - kind accent 色の border（kind ごとに `FloatingWindowCatalog` の spec.accent から取得）
  - dragged ghost = solid border 1px
  - target ghost = dashed border 1px（swap drop target を示す）
  - 描画位置: 当該操作の post-release rect
  - 描画層: drag 中だけ実 window より前面 sibling（plane 末尾子）
- **drag mode 別の ghost 構成**:
  - `SoloDrag`: ghost なし（実 window がカーソル追従＝既存 #15）
  - `NormalGroupTranslate`: ghost なし（実 group 全員が平行移動＝post-release と同じ）
  - `NormalGroupDetach`: dragged 1 枚 ghost をカーソル位置に
  - `HakoniwaSwap`: dragged ghost を target rect / target ghost を dragged 元 rect（2 枚同時）
  - `HakoniwaSnapBack`: dragged ghost を元 rest 位置（snap-back プレビュー）
  - `HakoniwaDetach`: dragged 1 枚 ghost をカーソル位置に（detach プレビュー）
  - `HakoniwaCoreLock`: dragged 1 枚 ghost を rest 位置（detach 不可フィードバック・「引っ張っているが核なので外に出ない」を可視化）
- **commit-on-release**: drag 中は groupId・geometry 不変、ghost はあくまで preview。release で初めて detach/swap/snap-back/translate を確定。ADR-0017 §1 snap-on-release 精神と整合。
- **実装**: `DragGhostLayer`（仮称）= drag 中だけ存在する半透明オーバーレイ。pool で reuse（毎 drag で alloc しない）。

## §9 restore / cross-plane split

- **永続化**: `LayoutDocument.floatingWindows[*].groupId: string?`（optional・既存 doc は null として読まれる・version bump 無し）。
- **両 controller の Capture**: dock plane と floating plane の `Capture()` 結果をマージし `floatingWindows` に union。
- **kind→plane ルーティング restore**: `DockShape.IsDockKind(kind)` で plane を決定（ADR-0018 §10 のまま）。
- **cross-plane group fail-safe**: restore 時に同一 `groupId` を持つ window を集計:
  1. plane 別に member 数をカウント
  2. 片 plane のみなら何もしない
  3. 両 plane にいるなら member 多い plane を残す
  4. 同数なら **dock plane を優先**（core が dock 側なので Hakoniwa identity 保護）
  5. 負けた plane の member は `groupId = null`
  6. 残った plane 側が visible/live 1 未満なら通常 dissolve ルールで残 1 も null
- **runtime では起きない**: cross-plane snap が ADR-0018 §10 で構造的に禁止されているため、cross-plane group は手編集 doc / 旧 build 後方互換のみの fail-safe。

## §10 Close / Spawn / Hide lifecycle

- **Close**（universe sync 削除・`_dockWindows.Close(id)` 等の programmatic 永続消失）:
  - 当該 window 消滅
  - 元 group の残 visible/live member を再評価
  - 残 visible/live `== 1` → 残 1 人の groupId も null（連鎖 dissolve）
  - 残 visible/live `>= 2` → group 維持
- **Spawn**（universe add で新 chart `SpawnDockedToFocus` 等）:
  - 新 window の groupId は **null**（attach はユーザの drag-release だけ）
  - `SpawnDockedToFocus` で flush 配置されても自動 attach しない
  - 「ユーザが望んだ時だけ group」のメンタルモデル統一
- **Hide**（mode-switch で `startup` Hide 等）:
  - groupId 温存（Replay 復帰で group 復活）
  - hidden member は visible/live 集計から外れる→ Hakoniwa 判定は visible/live ベースで動的再評価

## §11 永続スキーマ

```jsonc
{
  "version": "<current>",
  "floatingWindows": [
    {
      "id": "run_result",
      "kind": "run_result",
      "x": 100,
      "y": 200,
      "w": 380,
      "h": 220,
      "zOrder": 4,
      "visible": true,
      "groupId": "grp_a1b2c3d4e5f6470b9c2d1a3f4b5c6d7e"
    },
    // ...
  ],
  // ADR-0017 と同じく panels=[] / hakoniwaProfiles=null は dead schema 温存
  "panels": [],
  "hakoniwaProfiles": null
}
```

- **既存 doc**: `groupId` フィールド不在 → null として読まれる（forward tolerance）。migration 不要。
- **両 plane**: 同一スキーマで union 保存・kind で restore 時ルーティング。
- **ADR-0017 §6「スキーマ追加 0」を 1 フィールドだけ supersede**: 本フィールド以外は不変。

## §12 AFK / HITL gate 設計

実装着手前に `behavior-to-e2e` を formal invoke して **正本 section の節番号と PASS テキストを確定**する。本書では範囲だけ列挙:

- **pure 算術 probe**（`FloatingWindowMath`・headless）:
  - `IsFlushAdjacent`: flush（ε=1px 以内・overlap > 0）/ same-edge 整列だけ（離れて並ぶ）/ 閾値外 / 直交軸 overlap 0 のコーナー接触
  - `EvaluateDragMode`: 7 モード（Solo/NormalTranslate/NormalDetach/HakoniwaSwap/HakoniwaSnapBack/HakoniwaDetach/HakoniwaCoreLock）の境界
  - `ResolveDropTarget`: カーソル直下 / 重なり最前面 / target なし / hidden 除外
  - merge cascade: Hakoniwa 単独 / Hakoniwa 複数 / size 最大 / 辞書順 / 全 null
- **controller wiring probe**（`FloatingWindowController`・headless RectTransform）:
  - groupId persistence + Capture/Apply round-trip
  - flush attach トリガ / same-edge no attach
  - 通常 group translate（全メンバー同一 delta で平行移動）
  - Hakoniwa swap commit（(x,y,w,h) 4 値交換・kind/id/content 不変）
  - detach commit + 連鎖 dissolve（残 1 で null）
  - core detach 不可（rest snap-back）
  - Close 連鎖 dissolve / Spawn null
  - Hide で groupId 温存
- **restore probe**:
  - cross-plane split（多数派 plane 残し / 同数 dock 優先 / 負け側 null）
  - 既存 doc（groupId 不在）→ null restore
- **ghost preview composition probe**（HITL 一部・AFK は構造のみ）:
  - 7 モードで ghost 数 / border style / 描画位置の構造検証（実 alpha / accent 色は HITL）
- **HITL**:
  - ghost の見た目（alpha 0.45 / kind accent 色 / dashed vs solid）
  - drag 感（D_DETACH の felt 距離）
  - パン中の plane / ghost 挙動（ADR-0018 と整合）
  - core detach 不可の rest snap-back のフィードバック品質
- **既存 AFK の互換性**:
  - 単独 window drag・ADR-0017 §1 snap-on-release・ADR-0018 plane 分離・cross-plane snap 禁止 はすべて維持（既存 section が GREEN を保つこと）

## §13 実装着地

設計の木が以下のコード seam に固定された（2026-06-21・8 slice 一括）。slice 単位の実装履歴ではなく **設計上 binding な事実**:

- **pure 算術 (§3/§4/§6/§7)** — すべて `Assets/Scripts/FloatingWindow/FloatingWindowMath.cs`:
  - `IsFlushAdjacent(DockRect a, DockRect b, float eps)`：辺一致 ε 以内 ∧ 直交軸 overlap > 0。`DEFAULT_FLUSH_EPS = 1f` は controller 側定数。
  - `MergeCandidate` struct + `ResolveMergeWinner(IList<MergeCandidate>)`：Hakoniwa-priority > member-count max > `StringCompareOrdinal` min > null（caller mint）。
  - `D_DETACH = 64f`（canvas-logical px・zoom 非依存）。
  - `DragMode` enum 7 値 + `DragContext` struct + `EvaluateDragMode(in DragContext)`：strict `>` 境界。
  - `GroupMember` struct + `ResolveDropTarget(Vector2, IList<GroupMember>, draggedId)`：最前面 sibling 優先。

- **controller 拡張 (§5/§7/§10)** — `Assets/Scripts/FloatingWindow/FloatingWindowController.cs`:
  - `Entry.groupId` 追加（runtime SoT）／`GroupIdOf(id)` 読み seam ／`SetGroupId(id, gid)` internal 書き seam。
  - `Spawn(... groupId)` overload：restore のみ非 null（production Spawn は groupId=null＝findings §10 不変）。
  - `Capture` / `Apply` の groupId pass-through（adopted 既存エントリの update path も含む）。
  - `SnapOnRelease(id)` 末尾で `CommitFlushAttachOnRelease(id)` → flush 隣接 partner 列挙 → `MergeCandidate` 集計 → `ResolveMergeWinner` → 必要なら `MintGroupId()`（`"grp_" + Guid.NewGuid().ToString("N")`）→ winners に groupId 適用。
  - `DragApplyDelta(id, rest, cursor, frameDelta)` = 7 mode classifier + 各 mode の live geometry 振り分け。NormalGroupTranslate のみ全 member へ delta、他 mode は freeze（findings §8 commit-on-release）。`BuildDragContext` が `isInGroup`/`isHakoniwa`/`isCore`/`hasTarget` を `_windows` から動的計算。
  - `ReleaseDrag(id, rest, cursor)` = 単一 production release entry。各 mode で commit（Solo/NormalTranslate→SnapOnRelease、NormalDetach/HakoniwaDetach→cursor へ移動＋groupId=null＋SnapOnRelease＋DissolveIfShrunkTo、HakoniwaSnapBack/HakoniwaCoreLock→rest 戻し、HakoniwaSwap→`CommitHakoniwaSwap`）。
  - `CommitHakoniwaSwap` = `(x,y,w,h)` 4 値交換、kind/id/groupId 不変。target は release 時に再 resolve（drag 中と同一 helper）。
  - `DissolveIfShrunkTo(groupId, threshold=2)` = **共有 dissolve helper**。Close cascade（`Close` 内）／detach commit（`ReleaseDrag`）／cross-plane split tail（`BackcastWorkspaceRoot.RestoreFloating`）すべてが同じ helper を呼ぶ。
  - `Close(id)` も oldGroup を保持し helper 経由で chain dissolve。
  - `AttachGhostLayer(DragGhostLayer)` / `ComposeDragGhosts(id, mode, rest, cursor)` = ghost 構成。

- **input 層 (§6/§8)** — `Assets/Scripts/FloatingWindow/FloatingWindowTitleInput.cs`:
  - `OnBeginDrag` で `_restAtDragStart = rt.anchoredPosition`、`_cursorLogical = rest`。
  - `OnDrag`：`_cursorLogical += frameDelta` → `_windows.DragApplyDelta(id, _restAtDragStart, _cursorLogical, frameDelta)`（旧 `MoveByLogical` 直叩きは退役。controller が 7 mode を分岐）。
  - `OnEndDrag`：`_windows.ReleaseDrag(id, _restAtDragStart, _cursorLogical)`（旧 `SnapOnRelease` 直叩きから昇格）。

- **ghost 描画 (§8)** — `Assets/Scripts/FloatingWindow/DragGhostLayer.cs`（**新規ファイル**）:
  - `GhostStyle { Solid, Dashed }` + `GhostSpec { kind, topLeft, size, style }` POCO。
  - `Render(IList<GhostSpec>)`：pool 拡張（monotonic）、ApplyGhost で位置/サイズ/active/name 書き換え（`GhostWindow_Solid` / `GhostWindow_Dashed`）、container を `SetAsLastSibling()`。
  - `Clear()` = ActiveCount を 0 に、pool は保持。
  - `ALPHA = 0.45f` 定数。
  - production ghost factory（`CreateUguiGhost`）= GameObject + Image + CanvasGroup（alpha=0.45・blocksRaycasts=false・interactable=false）。
  - AFK ctor overload で bare-RectTransform 注入可能（Section31 は同経路で構造 pin）。

- **永続化 (§9/§11)** — `Assets/Scripts/Layout/LayoutDocument.cs` + `BackcastWorkspaceRoot.cs`:
  - `FloatingWindowLayout.groupId: string?`（nullable, additive, JsonUtility verbatim）。`Clone` / `Approx` 含む。
  - 旧 sidecar は groupId 不在 → null（forward-evolution tolerance、findings 0008 §3）。
  - `BackcastWorkspaceRoot.RestoreFloating`：(1) `SplitCrossPlaneGroups(wins)` 事前 pass で cross-plane group を分割（多数派 plane 残し / 同数 ⇒ dock 優先 / 負け側 groupId=null）、(2) routing loop で `Spawn(... groupId)` / `SetGroupId` pass-through、(3) groupsToCheck 集計後に各 plane controller の `DissolveIfShrunkTo` で残 1 を chain dissolve。
  - `BuildWorkspace` で 2 plane に 1 つずつ `DragGhostLayer` を `NewGhostLayer()` で mint し各 controller に `AttachGhostLayer`。

- **AFK 正本拡張 (§12)** — `Assets/Tests/E2E/Editor/FloatingWindowE2ERunner.{cs,md}` + `E2E-INDEX.md`:
  - 新 section: **S20–S31**（12 自動 section）。各 Action ID は `GROUP-01..12`、HITL は `GROUP-13`。
    - S20=GROUP-01 (Slice A)・S21=GROUP-02・S22=GROUP-03・S23=GROUP-04 (Slice B)・S24=GROUP-05・S25=GROUP-06 (Slice C)・S26=GROUP-07 (Slice D)・S27=GROUP-08 (Slice E1)・S28=GROUP-09・S29=GROUP-10 (Slice E2)・S30=GROUP-11 (Slice F)・S31=GROUP-12 (Slice G)。
  - PASS text: 既存 PASS log に 12 phrase 追記（Section ID 末尾に `GROUP-01..12` 追記）。
  - `.md` 操作一覧表に GROUP-01..13 を追加（既存行は無改変）。
  - `E2E-INDEX.md` Surface rollup を 23→36 行、自動(E2E済) 19→31、HITL 3→4 へ更新。

- **CONTEXT.md**:
  - 既存「Hakoniwa」「floating window」を再々定義、新設 6 用語「window group / groupId」「Hakoniwa group」「core member」「D_DETACH」「swap drop target」「drag ghost」。

- **`DockShape.IsCoreKind(kind)` 新設**: `Assets/Scripts/FloatingWindow/DockShape.cs` に追加（`startup` / `run_result` 固定）。merge cascade Hakoniwa 判定と BuildDragContext で使用。

- **既存 AFK 互換**: S1–S19 は無改変・PASS 不変。Slice C で `FloatingWindowTitleInput` が `MoveByLogical` 直叩きから `DragApplyDelta` 経由へ移行したが、S3/S11 は controller の `MoveByLogical` / `SnapOnRelease` を直接駆動するため影響なし（DragApplyDelta は SoloDrag mode で MoveByLogical と同等）。

- **設計 binding な不変条件**:
  - groupId は **唯一の真実源**（座標から再導出しない）。
  - **MintGroupId は ResolveMergeWinner が null を返した時のみ呼ぶ**（all-singleton 同士の attach 1 経路のみが GUID 新規発行のトリガ）。
  - **`SnapOnRelease` だけが production の attach commit entry**（programmatic Spawn / 復元では非 attach）。
  - **`ReleaseDrag` だけが production の commit-on-release entry**（detach / swap / snap-back の唯一の路）。
  - **`DissolveIfShrunkTo` は detach / Close / cross-plane split の 3 callers のみ**。Hide は呼ばない（findings §10「Hide で groupId 温存」）。
  - **commit-on-release**: drag 中 NormalGroupTranslate **以外**は live geometry 不変。groupId も drag 中不変（release commit のみで変わる）。

- **post-impl チェック**:
  - `code-review(simplify)` → Medium+ 指摘無くなるまで `/pair-relay` ループ（CLAUDE.md 規律）。
  - `post-impl-skill-update`（CLAUDE.md 規律）。

- **AFK 実走（2026-06-21）状況**:
  - 本セッションで `behavior-to-e2e` で確定した Section20–S31 + GROUP-01..13 の実装は **#104 改変ファイルだけでは compile clean**（ローカル Unity 6000.4.11f1 batchmode で確認）。
  - ただし AFK runner の実走は **pre-existing baseline-RED** で blocked（`git stash push -u` で同じ 3 件が baseline でも再現）。両者とも `origin/main` の `9cce531 feat(replay): switch Replay picker universe to listed_info.duckdb` で導入された：
    - `Assets/Scripts/Universe/BackendAvailableInstrumentsProvider.cs:73,128`: `Environment.TickCount64` 未定義（Unity の .NET API target が .NET 5+ 未満で TickCount64 が無い）。
    - `Assets/Scripts/Live/WorkspaceEngineHost.cs:346`: `foreach (PyObject item in PyObject)` — この pythonnet バージョンの PyObject に IEnumerable が無い。
  - これらは #104 とは無関係（[[behavior-to-e2e]] の "pre-existing-RED baseline" 規律で stash → baseline でも同一 RED を確認）。**owner が baseline RED を解消してから AFK GREEN 確認**。簡易対処案: `Environment.TickCount64` → `(long)Environment.TickCount` ／ PyObject foreach → `using (PyList pl = new PyList(idsObj)) { foreach (PyObject item in pl) ... }`（en-passant 修正は #104 スコープ外）。

- **post-review 修正（2026-06-21・code-review F1–F10）**:
  - F1: `Apply` / `RestoreFloating` で legacy sidecar (groupId 欠落 = null) を許容（forward-evolution tolerance）。
  - F2: 設計通り（変更なし）— groupId は座標から再導出しない `Entry` SoT。
  - F3: `ReleaseDrag` の NormalGroupDetach / HakoniwaDetach 分岐が `CommitFlushAttachOnRelease(id, excludeGroupId: oldGroup)` を経由（findings §5: 直後の magnet snap で旧 sibling と flush になっても silent re-merge しない）。
  - F4: `ReleaseDrag` の NormalGroupTranslate 分岐が `ApplySnapOffset` の戻り offset を **dragged 以外の全 visible/live group member** にも伝播（findings §3-4: group 全体で flush adjacency を保ったまま release 後の attach scan が走る）。
  - F5: `DragGhostLayer` の Render / Clear 入力経路で null tolerance を強化（probe / ghost layer 未注入の controller も crash しない）。
  - F6: `IsFlushAdjacent` を signed-gap 化（`kiss but not overlap`: 接触 == flush、内部食い込みは flush 不成立。findings §3）。
  - F7: `LayoutStore` で `groupId == ""` を null へ coerce（JsonUtility 空文字列を null と同一視＝findings §11 schema invariant の epsilon）。
  - F8: `HakoniwaSwap` 対象 id を DragApplyDelta が `_lastSwapTargetId` にキャッシュし `CommitHakoniwaSwap` がそれを採用、ReleaseDrag 末尾で必ずクリア（findings §7: 最後の ghost が見せた相手と commit 対象を identity 一致させる）。
  - F9 (partial): `CountLiveGroupMembers(groupId, out hasCore)` helper を新設し `BuildDragContext` / `AddCandidateFor` の同一 scalar projection を 1 経路へ統合。**他 5 site（DragApplyDelta NormalGroupTranslate / ResolveHakoniwaSwapTarget / ReleaseDrag F4 offset propagation / DissolveIfShrunkTo / CommitFlushAttachOnRelease winners 展開）は inline 維持** — それぞれが heterogeneous projection（mutation / struct 構築 / id collection）で、callback or IEnumerable 経由にすると DragApplyDelta の 60Hz hot path で per-frame allocation を生む。
  - F10 (defer): `DragApplyDelta` / `ReleaseDrag` / `ComposeDragGhosts` の DragMode 3-switch fan-out は table-dispatch 化せず維持。3 switch は live-geometry mutation / release commit / pure GhostSpec composition と **本質的に異なる仕事**をしており、enum 以外の制御フローを共有しない。table 化すると 1 mode の挙動が 3 表に散逸し、変更頻度の低い stable enum（§6 で closed）の write-cost saving より read-cost regression が大きい。
