# FloatingWindowResizeE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`FloatingWindowResizeE2ERunner.cs`（#139・ADR-0030）が自動検証する **floating-window リサイズ**の台本。実装者は `.cs` と
本 `.md` をセットで読む。これは調査メモではなく、**右下つまみリサイズと島内 flush 追従押し出しでユーザーができる
行動すべての網羅台帳と、E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・
責務境界の共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名規約 [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **方針正本**: [ADR-0030 — window resize grip / island-scoped flush push](../../../../docs/adr/0030-window-resize-grip-island-scoped-flush-push.md)（accepted・自己保護節あり）。
> 設計の木・下位事実・RED→GREEN は [docs/findings/0112](../../../../docs/findings/0112-window-resize-grip-island-push.md)。
> floating-window サーフェスの drag/snap/group/z-order/persist 本体は別 runner [`FloatingWindowE2ERunner`](./FloatingWindowE2ERunner.md)（WINDOW-/SNAP-/DOCK-/PLANE-/GROUP-/DRAG-）。
> 本 runner は **resize 専用**（RESIZE-）に分離（ADR-0015「1 サーフェス 1 runner」・既存 41 section を温存）。

## 対象サーフェス

infinite canvas の floating window（Strategy Editor / Order / login / HITL …）の **右下角つまみによるリサイズ**と、
**同じ島の flush 追従押し出し**（`FloatingWindowResizeHandle`＝つまみ builder ＋ `FloatingWindowResizeGrip`＝入力境界
＋ `FloatingWindowController` の resize セッション ＋ `FloatingWindowMath.ResizeIslandPush`＝純算術）。**左上 pivot 固定で
右・下に伸縮**（`anchoredPosition` 不動・`sizeDelta` のみ変化）。本文（Body 子）は anchor stretch で自動伸縮。永続化は
スキーマ追加ゼロ（既存 `FloatingWindowLayout.w/h` + `x/y` を既存 `Capture` が拾う）。**ADR-0029 のドラッグ 2 チャンネル
（島移動／単窓ピックアップ）とは別系統**（つまみは title-bar drag ではないので `ResolveChannel` に入らない）。

## 対象ユーザー行動

右下つまみを drag して window をリサイズ（左上固定・右下伸縮）、同じ島の flush メンバーを追従押し出し（対称・連鎖）、
`spec.minSize` 未満に縮まない（max なし）、つまみ掴みで最前面化、resize 中 ESC でサイズ＋押した全メンバーが rest へ
revert、リサイズ後の geometry が persist round-trip。本文の自動伸縮・つまみの実ジェスチャ発火の実感は HITL。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| RESIZE-01 | つまみ drag で左上固定・右下伸縮（純算術） | `FloatingWindowMath.ResizeIslandPush` | resized は `topLeft` 不動・`size`=newSize（右辺 +dW・下辺 -dH）、grow/shrink 両方 | 単窓 grow/shrink・unknown id guard を assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S1） |
| RESIZE-02 | 同じ島の flush メンバーが追従押し出し（対称・連鎖・x/y 独立・size 保持・純算術） | `FloatingWindowMath.ResizeIslandPush`（`PropagateFlush`） | 動く辺（右/下）に flush するメンバーが同符号 Δ で平行移動・grow=押し出し/shrink=引き戻し・島内連鎖・x/y 独立・各自 size 保持 | 横行 A\|B\|C grow/shrink、縦 bottom-flush、x/y 独立を assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S2） |
| RESIZE-03 | island scope 厳守＝非 flush・左/上メンバーは動かない（負コントロール・純算術） | `FloatingWindowMath.ResizeIslandPush` | 左辺/上辺（動かない辺）に flush するメンバー・disjoint メンバーは不動、右 flush のみ追従 | 左 flush・上 flush・disjoint 不動 ＋ 右 flush 追従を assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S3） |
| RESIZE-04 | つまみ drag で window 実体が左上固定・右下伸縮（controller・engine==math） | `FloatingWindowController.BeginResize`/`ResizeApply`（#139） | 実 RectTransform で `anchoredPosition` 不動・`sizeDelta`=rest+(Δx,-Δy)、純 `ResizeIslandPush` と一致 | spawn→BeginResize→ResizeApply の anchored 不動・size 増・engine==math を assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S4） |
| RESIZE-05 | グループ島の flush 追従押し出し（controller 実 rect・対称・連鎖）＋ 非グループ隣接は不動（負コントロール） | `FloatingWindowController.ResizeApply`（`ResolveIslandIds`） | グループ A\|B\|C を grow→B,C 連鎖追従・shrink で対称引き戻し、外部（非グループ）Z は island scope 外で不動・押されたメンバーは size 保持 | grouped 島の grow/shrink 追従 ＋ 外部 Z 不動 ＋ size 保持を assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S5） |
| RESIZE-06 | `spec.minSize` 未満に縮まない・max なし・**clamp 時は追従も clamped 量**（controller） | `FloatingWindowController.ResizeApply`（`ClampSize`→`ResizeIslandPush`） | desired < minSize は minSize へ clamp UP、巨大 grow は verbatim（無限 canvas）、grouped follower は **clamped delta** で追従（pre-clamp desired ではない） | 巨大 negative drag→min clamp・巨大 grow→unbounded・grouped follower が clamped 量だけ pull-back を assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S6） |
| RESIZE-07 | resize 中 ESC でサイズ＋押した全メンバーが rest へ revert（commit skip） | `FloatingWindowController.CancelResize`/`ReleaseResize`（#139） | ESC で resized の `sizeDelta` ＋ 押したメンバーの `anchoredPosition` が rest へ、state（groupId）不変、後続 release は何も commit しない | grow→ESC→revert・groupId 不変・post-ESC ResizeApply/release が no-op を assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S7） |
| RESIZE-08 | 本文（Body 子）が親 `sizeDelta` に追従して自動伸縮 | `StrategyEditorWindowFrame.Build`（**実 builder を駆動**・Body anchor stretch・findings 0008 §0） | Body は `anchorMin=(0,0)/anchorMax=(1,1)`+insets、親 `sizeDelta` 変化で `ForceRebuildLayoutImmediate` 後に解決 rect が同 Δ で伸縮 | **production `Build` の Body** で親 size 変化前後の body.rect が `TitleHeight`+insets を維持して伸びる（非空虚＝TitleHeight/anchor 回帰で RED）を assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S8） |
| RESIZE-09 | リサイズ＋押し出し後の geometry が persist round-trip（スキーマ追加なし） | `FloatingWindowController.Capture`/`Apply`（既存・schema-add 0） | resize で grow した resized の `w` ＋ 押されたメンバーの `x` が `Save`→disk→`Load`→fresh `Apply` で復元、on-disk text に grown size | 非デフォルト .5 値で grow＋push→capture→on-disk text 証明→fresh restore を assert（vacuous-green kill） | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S9） |
| RESIZE-10 | つまみは ADR-0029 の 2 チャンネルと別系統（チャンネル不変条件 無傷） | `FloatingWindowController.IsResizing`/`IsDragging`／`FloatingWindowResizeGrip`（ADR-0030 §3） | resize 中 `IsResizing` は立つが `IsDragging` は false のまま（gesture-channel 機構を触らない）、つまみは独自 `IDragHandler`（press は bubble せず `ResolveChannel` に入らない＝eject つまみと逆） | BeginResize で IsResizing↑/IsDragging 不変・grip が独自 IDragHandler を持つを assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S10） |
| RESIZE-11 | 全 window 右下角に常時可視つまみ（chip・raycast target・独自 drag handler・最前面・本文/pan に落ちない）＋ **本番配線 seam** | `FloatingWindowResizeHandle.Attach`／`FloatingWindowTitleInput.Initialize`→`AttachResizeGrip`→`grip.Initialize`（ADR-0030 §1/§2） | window root の右下角に常時可視 "◢" chip（chip Image=raycast target・独自 `IDragHandler`・bottom-right anchor・last sibling・glyph 非 raycast）、idempotent find-or-create。**`TitleInput.Initialize` 経由で grip が attach＋Initialize される**（未 Initialize だと OnDrag 無言 no-op） | bare root に `Attach`→名前/active/raycast target/own-drag-handler/bottom-right/last-sibling/glyph 非 block/idempotent ＋ **`TitleInput.Initialize`→root に grip 生え＋`_windows/_windowId` がセット**を assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S11） |
| RESIZE-12 | つまみ掴みで最前面化（raise + focus 記録） | `FloatingWindowController.BeginResize`（`NoteUserFocus`・ADR-0030 §6） | grip 掴みで window が最前面（`SetAsLastSibling`）＋ focus target 記録（title-bar press parity） | BeginResize で back→last sibling・`TryResolveDockTarget` が back を返す（focus 記録）を assert | 自動(E2E済) | `FloatingWindowResizeE2ERunner`（S12） |
| RESIZE-13 | 実つまみジェスチャ発火＋本文伸縮の実感（目視） | `FloatingWindowResizeGrip`（実 playmode・EventSystem raycast） | カーソルをつまみに当ててドラッグ→resize 発火、左上固定で右下が伸びる、本文（入力欄・出力）が滑らかに伸縮、全 window で出る、島内押し出しの対称性、min で止まる、ESC で戻る | — | HITL専用（実ピクセル＋実マウス＋EventSystem raycast。raycast+pointer glue は batchmode 駆動不能＝unity-afk-gesture-glue-hitl-only / ADR-0029・#136 と同じ責務分割） | `FloatingWindowHitlMenu`（既存ハーネスに grip 自動 attach） |

## 観測点（詳細）

- **RESIZE-01/02/03（純算術）**: `FloatingWindowMath.ResizeIslandPush` を直接駆動。resized は `topLeft` 不動で `size`=newSize、
  右辺は +dW・下辺は -dH（top-left pivot・y up-positive）。flush 追従は **動く辺（右/下）に flush するメンバーを REST rect 上で
  BFS（`PropagateFlush`）**＝同符号 Δ で平行移動・島内連鎖・x/y 独立。左辺/上辺（動かない辺）に flush するメンバー・disjoint は不動。
- **RESIZE-04/05/06/07（controller wiring）**: `BeginResize`（rest snapshot ＋ `NoteUserFocus`）→`ResizeApply`（rest+(Δx,-Δy)→
  `ClampSize`→`ResizeIslandPush`→実 rect 書き込み・絶対モデル）→`ReleaseResize`（close）/`CancelResize`（rest へ revert）。
  実 RectTransform で engine==math、grouped 島の連鎖追従、外部窓の不動（island scope）、min clamp、ESC revert を assert。
- **RESIZE-08（本文伸縮）**: **production `StrategyEditorWindowFrame.Build` を駆動**して実 Body（`anchorMin=(0,0)/anchorMax=(1,1)`
  +insets・`TitleHeight` 由来）を取得し、親 `sizeDelta` 変化後に `ForceRebuildLayoutImmediate` で body.rect が同 Δ 伸縮することを
  非空虚に assert（content reflow 算術なし）。手組み hierarchy ではなく実 builder を使うので `TitleHeight`/Body anchor 回帰で RED になる。
- **RESIZE-09（persist）**: 既存 `Capture`/`Apply` がリサイズ結果（grown size ＋ pushed position）を拾うことを、非デフォルト .5 値の
  on-disk text 証明 ＋ fresh controller 復元で固定（スキーマ追加ゼロ）。
- **RESIZE-10/11（別系統・affordance）**: つまみは独自 raycast target ＋ 独自 `IDragHandler` ＋ controller の別 resize セッションで、
  `ResolveChannel`（IslandMove / SingleWindowPickup）に一切入らない（`IsDragging` 不変）。eject つまみ（drag handler 無し・bubble）と
  対照的に grip は press を swallow する。affordance は全 window root 右下角に常時可視 chip。

## 自動判定（合格条件）

- ログに `[E2E FLOATING WINDOW RESIZE PASS] <要約>` ＋ per-Action-ID `[E2E RESIZE-NN PASS]`（01–12）、プロセス exit 0、`error CS\d+` 0 件。
- 各 `自動(E2E済)` 行の観測点を 1 つでも落としたら `[E2E FLOATING WINDOW RESIZE FAIL] <msg>` で exit 1（手前で抜けるので当該 id タグは出ない＝rollup は「未到達」と読む）。
- delete-the-production-logic litmus: `ResizeIslandPush` の flush 追従（`PropagateFlush` の BFS）を消す（=resized だけ size 変更し隣を動かさない）と S2/S5 が落ち、`ClampSize` を外すと S6 が落ち、`CancelResize` の rest 書き戻しを消すと S7 が落ちること。
- RESIZE-13 は HITL 専用＝AFK タグを吐かない（rollup の id 不在は「HITL であって AFK miss ではない」）。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用` は理由を併記する（RESIZE-13＝実つまみジェスチャ発火・unity-afk-gesture-glue-hitl-only）。

## 実装方針

- `FloatingWindowE2ERunner` 同型に **headless な Content→FloatingWindowLayer（identity）の RectTransform ツリー**を組み、factory は
  bare RectTransform を mint・destroy は `DestroyImmediate`。`FloatingWindowController` の resize セッションと `FloatingWindowMath.ResizeIslandPush`
  を pure に直駆動（実 `BackcastWorkspaceRoot` 合成は不要・Python-FREE・render-FREE・実 root 不要）。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod FloatingWindowResizeE2ERunner.Run -logFile <abs log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 = ripgrep / bash `grep -a` で grep。
- `scripts/run-all-tests.ps1 -Method FloatingWindowResizeE2ERunner.Run` で pytest と merged rollup に合流（per-Action-ID `RESIZE-NN`）。
