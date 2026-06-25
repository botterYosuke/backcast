# Window resize Findings: 右下つまみリサイズ・島内 flush 追従押し出し（設計ロック・実装は後続スライス）

- 依頼: owner「Strategy Editor をリサイズ可能にして」（2026-06-25）。findings 0008 §0/§8 が deferred した「resize gesture / resize handle（TTWR `resizable`）→ 将来 slice」の当該 slice。
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0030 — window resize grip / island-scoped flush push](../adr/0030-window-resize-grip-island-scoped-flush-push.md)（accepted, 自己保護節あり）。supersede 元 [[ADR-0017]] / [[ADR-0029]] D7 / findings 0075 §1 はいずれも無改変（ADR-0030 が resize トリガに限り点 supersede）。
- 先行・正本: [findings 0008](0008-floating-windows.md)（floating window システム・persist スキーマ・座標規約）／[findings 0106](0106-grab-target-fixed-drag-gesture-channel-eject-pickup.md)（ADR-0029 gesture-channel 実装）。
- 設計確定: `grill-with-docs`（2026-06-25・owner インタビュー Q1–Q5）。

> **状態: 設計ロックのみ。実装は後続スライス（owner 指示「文書だけ今書く」2026-06-25）。** 本 findings は grill で固めた設計の木の記録であり、AFK/HITL ゲートは実装スライスで GREEN 化する。

---

## 0. なぜほとんど土台が揃っているか（grill 冒頭のコード裏取り）

- **永続化＝スキーマ追加ゼロ**: `FloatingWindowLayout` は `w/h`（size）と `x/y`（top-left anchor）を持ち、`FloatingWindowController.Capture` が `sizeDelta` / `anchoredPosition` を読む。findings 0008 §11 が非 default size（520.5×380.5）の disk round-trip を AFK 実証済み。リサイズ結果も押された隣窓の新位置も既存 Capture が拾う。
- **本文の自動伸縮＝content reflow 算術なし**: `StrategyEditorWindowFrame.Build` の Body は `anchorMin=(0,0) / anchorMax=(1,1)` + `offsetMin=(4,4) / offsetMax=(-4,-(TitleHeight+2))` で枠に貼り付く。枠の `sizeDelta` を変えれば入力欄・出力は自動追従。
- **座標が綺麗**: window は pivot=(0,1)（top-left・findings 0008 §2）。右下つまみで伸縮すれば `anchoredPosition` 不動・`sizeDelta` のみ変化。
- 残る本質は「掴み方」「ADR-0029 チャンネル不変条件との共存」「owner が選んだ島内押し出しと既存『resize 連動なし』原則の衝突」の 3 点のみ。

## 1. owner 決定（grill Q1–Q5・2026-06-25）

| # | 問い | 決定 | 備考 |
|---|---|---|---|
| Q1 | 掴み方 | **右下角の常時可視つまみ** | 左上固定・右下伸縮。四隅＋辺案・右下+右辺/下辺案は不採用。 |
| Q2 | 対象範囲 | **全 window**（editor / order / login / HITL …） | per-spec `resizable` フラグは死に設定ゆえ省略。 |
| Q3 | 隣の扱い | **隣を押しのける**（対象は**同じ島の他メンバーだけ**） | 「独立（重なり許容）」「磁石吸着」は不採用。「進路の全 window」も不採用＝island scope 厳守。 |
| Q5 | 縮めたとき | **隣も追従して端に付いたまま（対称・常にタイル維持）** | 「広げたときだけ押す（縮めても戻らない）」は不採用。 |
| 進め方 | — | **文書だけ今書く・実装は後で** | ADR-0030 + 本 findings + CONTEXT 更新まで。 |

## 2. 設計の木（owner 決定 ＋ 既存コードと整合させた下位決定）

- **つまみ**: window root 右下角・常時可視チップ（◢ 風）。`FloatingWindowEjectHandle` をミラーした find-or-create builder（idempotent・raycast target・`SetAsLastSibling` で最前面）を、`FloatingWindowTitleInput.Awake` から**全 window に一律 attach**（eject つまみと同じ uniform 経路）。本文 InputField / canvas pan へ落ちないよう自前 raycast target。
- **delta 規約**: title-drag と同一。screen→Viewport-local（`RectTransformUtility`）→ `FloatingWindowMath.ViewportDeltaToLogical(delta, zoom)`。raw `eventData.delta` を直接使わない（findings 0008 §2）。
- **ADR-0029 と別系統**: つまみは独自 `IDrag*Handler` ＋ controller の別 resize セッション。`ResolveChannel`（IslandMove / SingleWindowPickup）に入らない＝チャンネル不変条件は無傷。
- **島内 flush 追従押し出し**: リサイズ窓と同じ island のメンバーで、**動いている辺（右/下）に flush**（`IsFlushAdjacent` 規約＝対辺一致 ∧ 直交軸 overlap>0）のものが辺に張り付き追従。**対称**（広げる→押し出し・縮める→引き戻し）・島内**連鎖**・メンバーは**移動のみ（size 保持）**。scope は island 厳守（他島・他 plane・非グループ隣接は不動）。
- **clamp**: `spec.minSize` 未満に縮まない（findings 0008 §3 の spawn clamp と同じ下限）。max なし（無限 canvas）。
- **ESC**: resize 中 ESC でリサイズ窓 size ＋押した全メンバー位置を rest（resize 開始スナップ）へ revert。
- **最前面化**: つまみ掴みで `NoteUserFocus`。
- **persist**: 既存 Capture が拾う（スキーマ追加ゼロ）。

## 3. 既存設計との衝突と解消（grill で surface）

| 衝突元（固定文書） | 食い違い | 解消 |
|---|---|---|
| [[ADR-0017]] free-placement「自動タイリングしない・resize 連動を持ち込まない」 | resize で隣が動く | ADR-0030 が resize トリガ**かつ island scope 内**に限り carve-out。global タイリングは持ち込まない＝resize 外では ADR-0017 完全維持。 |
| [[ADR-0029]] D7「global reflow しない・唯一例外 swap 島内 reflow」 | reflow トリガに resize 追加 | ADR-0030 が「reflow トリガに resize を追加」と明示。scope=island は同じ規律。 |
| findings 0075 §1「結合なし・resize push-out なし」 | push-out を導入 | ADR-0030 が supersede（findings 0075 は無改変・dangling 防止に本 findings が参照）。 |
| CONTEXT.md:336 _Avoid_「resize 連動を持ち込むこと」 | 反転 | 同 _Avoid_ を ADR-0030 carve-out へ更新（§4）。 |

- **swap reflow（ADR-0029 §6）との差**: swap は best-effort magnetic flush re-snap（隙間を閉じるだけ・重なりは押し離さない・残隙間許容）。resize 押し出しは**動いた辺への強制 flush 追従**（常にタイル維持・対称・連鎖）＝別アルゴリズム。`ReflowIslandAfterSwap` は流用せず resize 専用の純関数を新設。
- 全 supersede 元は自己保護条項を持つため**無改変**、ADR-0030 が差分だけ supersede（先例 0018→0017 / 0024→0019 / 0029→0024 と同型）。

## 4. CONTEXT.md 整合（本 findings と同時更新）

- `Hakoniwa` glossary の _Avoid_「**global** タイリング強制・resize 連動を持ち込むこと（free-placement・例外は swap 時の島内局所 reflow のみ＝ADR-0029 D6）」に **resize 時の島内 flush 追従押し出し（ADR-0030・island scope 厳守）も carve-out** を追記。

## 5. 実装スライスの残務（順序・後続セッション）

1. `behavior-to-e2e` を formal invoke し AFK section を固定（Action-ID 採番）。→ **§6 で確定（2026-06-25 実装セッション）**。
2. durable 実装: `FloatingWindowMath` 押し出し関数 ／ `FloatingWindowController` resize セッション ／ `FloatingWindowResizeGrip`（input）＋つまみ builder ／ `FloatingWindowTitleInput` から一律 attach。→ **§6/§7 で実装済み**。
3. HITL（owner）: つまみでリサイズ・本文伸縮・島内押し出しの対称性・min・全 window で出る・ESC を目視（RESIZE-13）。
4. `code-review(simplify)` を Medium 以上が無くなるまで `/pair-relay` で回す（CLAUDE.md 規約）。

## 6. AFK section 確定（behavior-to-e2e formal invoke・2026-06-25 実装セッション）

新規 runner **`Assets/Tests/E2E/Editor/FloatingWindowResizeE2ERunner.{md,cs}`**（ADR-0015「1 サーフェス 1 runner」＝
既存 41-section `FloatingWindowE2ERunner` を温存し resize を別 namespace に分離）。Action-ID 名前空間 **`RESIZE-NN`**。
Python-FREE・render-FREE・実 root 不要（headless Content→FloatingWindowLayer の RectTransform を反射合成し
`FloatingWindowController` の resize セッション ＋ `FloatingWindowMath.ResizeIslandPush` を pure 直駆動）。

| Action-ID | section | 観測点 | 自動/HITL |
|---|---|---|---|
| RESIZE-01 | S1 | pure `ResizeIslandPush`: 左上固定・右下伸縮（`topLeft` 不動・`size`=newSize・grow/shrink・unknown id guard） | AFK |
| RESIZE-02 | S2 | pure: 島内 flush 追従押し出し＝対称(grow=押し出し/shrink=引き戻し)・連鎖・x/y 独立・メンバー size 保持（横行 A\|B\|C ＋ 縦 bottom-flush ＋ x/y 独立） | AFK |
| RESIZE-03 | S3 | pure: island scope 厳守＝左/上 flush・disjoint メンバー不動（負コントロール）＋ 右 flush のみ追従 | AFK |
| RESIZE-04 | S4 | controller 実 rect: engine==math 左上固定右下伸縮（`anchoredPosition` 不動・`sizeDelta`=rest+(Δx,-Δy)・純算術一致） | AFK |
| RESIZE-05 | S5 | controller 実 rect: grouped 島の flush 追従（対称・連鎖）＋ 外部（非グループ）窓 不動（island scope 負コントロール）＋ size 保持 | AFK |
| RESIZE-06 | S6 | controller: min clamp（`spec.minSize` floor・max なし）＋ **clamp×follower**（grouped follower は clamped delta で追従・pre-clamp desired ではない） | AFK |
| RESIZE-07 | S7 | controller: ESC revert（resized size ＋ 押した全メンバー位置が rest へ・groupId 不変・後続 release no-op） | AFK |
| RESIZE-08 | S8 | Body 子が親 `sizeDelta` に追従（**実 `StrategyEditorWindowFrame.Build` を駆動**・anchor stretch・`ForceRebuildLayoutImmediate` 後に解決 rect・`TitleHeight`/anchor 回帰で RED＝非空虚） | AFK |
| RESIZE-09 | S9 | 非空転 disk round-trip（grow＋push geometry を Capture→on-disk text 証明→fresh Load→Apply 復元・schema-add 0） | AFK |
| RESIZE-10 | S10 | ADR-0029 別系統不変条件（resize 中 `IsResizing`↑/`IsDragging` 不変・grip は独自 `IDragHandler`＝`ResolveChannel` 不可侵） | AFK |
| RESIZE-11 | S11 | grip affordance（全 window root 右下角・常時可視 "◢" chip・raycast target・独自 drag handler・bottom-right・last sibling・glyph 非 block・idempotent）＋ **本番配線 seam**（`FloatingWindowTitleInput.Initialize`→`AttachResizeGrip`→`grip.Initialize` で grip が attach＋`_windows/_windowId` セット・未 Initialize だと OnDrag 無言 no-op） | AFK |
| RESIZE-12 | S12 | 最前面化（`BeginResize`→`NoteUserFocus`＝`SetAsLastSibling` ＋ focus target 記録） | AFK |
| RESIZE-13 | — | **実つまみジェスチャ発火＋本文伸縮の felt（目視）** | **HITL専用**（raycast+pointer glue は batchmode 駆動不能＝unity-afk-gesture-glue-hitl-only / ADR-0029・#136 と同じ責務分割） |

**RED→GREEN litmus（delete-the-production-logic）**: `ResizeIslandPush` の flush 追従（`PropagateFlush` の BFS）を消す
（＝resized だけ size 変更し隣を動かさない）と S2/S5 が RED ／ `ClampSize` を外すと S6 が RED ／ `CancelResize` の rest
書き戻しを消すと S7 が RED ／ **`ResizeApply` が clamped でなく desired delta を `ResizeIslandPush` に渡すと S6（clamp×follower）が RED** ／
**`StrategyEditorWindowFrame` の `TitleHeight` 変更・Body anchor 撤去で S8 が RED**（実 builder 駆動）／ **`AttachResizeGrip` の
`grip.Initialize` を外すと S11（配線 seam）が RED**。負コントロール（S3 disjoint・S5 外部 Z 不動）が島 scope を非空虚に固定する。

> code-review（2026-06-25・4 並行 review agent）で S8 vacuity（手組み hierarchy で production ロジック不在）・clamp×follower 未カバー・
> grip 配線 seam（`TitleInput.Initialize→grip.Initialize`）未カバー の Medium 3 件を検出 → S8 を実 builder 駆動へ・S6 に grouped
> follower 追加・S11 に配線 seam 追加で解消（Action-ID は据え置き＝既存 RESIZE-06/08/11 に内包）。CONTEXT に約束済み glossary
> 用語 `resize つまみ`/`island-scoped resize push` 欠落（High）も追加。

**rollup**: runner は成功マイルストンで `[E2E RESIZE-01..12 PASS]`（単一トークン）を吐き `scripts/E2ERollup.ps1` が集計。
`pwsh scripts/run-all-tests.ps1 -Method FloatingWindowResizeE2ERunner.Run` で pytest と merged rollup に合流。
RESIZE-13 は HITL ゆえ AFK タグ無し（rollup の id 不在＝HITL であって AFK miss ではない）。

## 7. 実装の下位事実（ADR-0030 自己保護条項に基づき本 findings に記録・ADR は無改変）

- **つまみ builder**: `FloatingWindowResizeHandle.Attach(windowRoot, font)`（`FloatingWindowEjectHandle` をミラーした
  find-or-create・idempotent）。chip = raycast-target Image・bottom-right anchor（`anchorMin/Max=(1,0)`・pivot=(1,0)・inset 2px）・
  size 18px・`SetAsLastSibling`・glyph "◢"（`LowerRight`・raycastTarget=false）。NodeName=`ResizeGrip`。
- **input**: `FloatingWindowResizeGrip`（MonoBehaviour・`IPointerDown`/`IBeginDrag`/`IDrag`/`IEndDrag`＋ESC poll）。
  **eject つまみと逆＝grip は独自 `IDragHandler` で press を swallow**（bubble しない＝`ResolveChannel` に入らない）。
  delta 規約は title-drag と同一（screen→viewport-local→`/zoom`＝`ViewportDeltaToLogical`）。新 size = rest size + (Δx, **−Δy**)
  （bottom-right grip ゆえ右下ドラッグで拡大）。
- **attach 経路の精緻化（ADR は "Awake から一律 attach" と記す・下記理由で `Initialize` から attach）**: eject つまみは
  `Awake` で **title bar 自身**に attach するが、resize grip は **window root**（title bar の `transform.parent`）を対象とする。
  `new GameObject(types)` の `Awake` は親付け前に同期発火するため `transform.parent` が null＝root を解決できない。よって
  grip は `FloatingWindowTitleInput.Initialize`（hierarchy 配線済み・controller/canvas/viewport/id の deps も入手済み）から
  attach する。「title input から一律 attach・per-factory 配線なし」という ADR の意図は不変（`EnsureCloseButton`/`EnsureRunButton`
  の idempotent find-or-create と同型）。全 frame（`StrategyEditor`/`OrderTicket`/`DockWindowFrame`）が title input を `Initialize` するので
  全 window に一律で出る。
- **controller resize セッション**: `BeginResize`（`NoteUserFocus`＝raise+focus → island rest snapshot）/`ResizeApply`
  （rest+(Δx,−Δy) を `ClampSize`→`ResizeIslandPush`→実 rect 書き込み・絶対モデル・discrete render・mid-resize spring なし）/
  `ReleaseResize`（close・絶対モデルゆえ追加 commit 不要）/`CancelResize`（rest へ revert＋spring）。`_resize` は `_drag` と独立フィールド
  ＝gesture-channel 機構を一切触らない（`IsResizing` ⊥ `IsDragging`）。
- **純算術 `FloatingWindowMath.ResizeIslandPush(resizedId, islandRects, newSize, eps)`**: resized は `topLeft` 不動・`size`=newSize、
  flush 追従は **REST rect 上の方向別 BFS（`PropagateFlush`：右辺チェーン / 下辺チェーン）**で x/y 独立に伝播（同チェーンは同一 Δ 平行移動
  ゆえ rest で flush 判定して順序非依存）。swap の `ReflowIslandAfterSwap`（best-effort magnetic）とは別関数（こちらは動いた辺への強制 flush 追従）。
