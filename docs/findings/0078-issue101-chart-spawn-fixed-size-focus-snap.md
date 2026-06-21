# findings 0078 — [+Add] Chart パネル spawn サイズ一定化 + フォーカス吸着配置（#99 回帰修正）

方針: **ADR-0017**（Hakoniwa = ドッキング可能な独立 floating window 群）。本 findings はその下位設計の木の一部を
固定する（ADR は無改変・自己保護条項どおり下位事実は findings に委譲）。issue: **#101**（fix #99 回帰・2 slice）。
grill: `grill-with-docs` / `behavior-to-e2e`（2026-06-21・owner HITL）。supersede: findings 0075 §3/§4 の
「chart の rect = `DockDefaultPlacement.ComputeRects(N)[i]`」（chart についてのみ・base 5 枚は据え置き）。

## §0 何が壊れていたか（#99 回帰）

#99（ADR-0017）で Hakoniwa を split-grid から独立 floating window 群へ移植した際、chart の universe 同期
（`SyncChartWindowsToUniverse`）が新規 chart の **rect を `DockDefaultPlacement.ComputeRects(N)` のグリッド 1 マス**で
決めていた（findings 0075 §3 実装着地）。`ComputeRects(N)` は 1200×640 の箱を `ceil(√N)` グリッドに割るため、
**spawn サイズが「その時点の chart 総数 N」に依存**（N=1→1200×640 / N=2→594×640 / N=3→594×314 …）。既存ウィンドウは
再配置されないので、`[+Add]` を押すたび大きさがバラバラになり、`FloatingWindowCatalog` の `KIND_CHART` 固定既定
サイズ（520×360）が spawn 経路で無視されていた（owner 意図と相違）。

## §1 配置 helper `DockSnapPlacement`（Slice 1・pure・AFK 権威）

`Assets/Scripts/FloatingWindow/DockSnapPlacement.cs`（pure static・`DockDefaultPlacement` / `FloatingWindowMath` と
同じ two-tier 規律）。

- **API**: `PlaceAdjacent(DockRect target, Vector2 newSize, IList<DockRect> others, float gap) → Vector2 topLeft`。
- **空き辺の探索順 = 右→下→左→上**。最初に「`others` のどれとも重ならない」辺を採用。
- **flush 隣接 + 同辺整列**: 右/左へ置くときは **上辺を揃える**、下/上へ置くときは **左辺を揃える**（perpendicular-edge
  align）。`gap` は seam 間隔で、production は **0（flush・隙間 0）**を渡す。helper は reuse のため正の gap も honor する。
- **重なり判定は strict AABB**（辺が接触するだけ＝非重複）。よって target に flush（接吻）する candidate は「空き」扱い。
- **全 4 辺が埋まっていれば対角カスケード**（`+CascadeStep, -CascadeStep`・`CascadeStep = SpawnPlacement.DefaultOffset`
  を再利用）で重なりを避けて決定的に返す。`gap` から独立なので flush(gap=0) でも前進する（step 0 で停滞しない）。
- **サイズは入力 `newSize` を verbatim**（helper は WHERE のみ・HOW BIG は触らない）。
- AFK gate: `FloatingWindowE2ERunner` **S14**（DOCK-03）— flush-right / right→down→left→up フォールバック / overflow
  カスケード（非重複も sweep で確認）/ strict-touch / gap 適用。

## §2 chart spawn を固定サイズ + フォーカス吸着へ（Slice 2・HITL）

`FloatingWindowController.SpawnDockedToFocus(kind, id, anchorTopLeft, visible)` を新設し、chart 経路を切替えた。

- **サイズ = catalog spec の defaultSize**（`KIND_CHART`＝520×360）。`_windows.Count`（= N）に**非依存**。`Spawn` の
  spec-min clamp は defaultSize ≥ minSize なので no-op。
- **位置 = `DockSnapPlacement.PlaceAdjacent(target, size, CaptureVisibleRects(id), gap=0)`**。`others` は**可視窓のみ**
  （dormant は slot を塞がない）・自分（まだ未 spawn の id）は除外。
- `BackcastWorkspaceRoot.SyncChartWindowsToUniverse` から **`ComputeRects(N)` を撤去**。`SpawnChartWindow(iid)` は
  `_windows.SpawnDockedToFocus(KIND_CHART, "chart:<iid>", SpawnAnchorTopLeft(), true)` を呼ぶだけ。
  **base 5 枚の初回配置（`DockDefaultPlacement.ComputeRects`・line ~633）は据え置き＝スコープ外**（issue 設計ロック）。
- 既存の保存レイアウト復元（`RestoreFloating` / `ApplyGeometry`・`Spawn(w.kind,…)`）は本変更を通らない＝**保存 geometry を
  そのまま honor**（live geometry が SoT・本修正は新規 spawn のみを支配）。

## §3 ターゲット選択 = フォーカス追跡（Slice 2）

`TryResolveDockTarget(anchorTopLeft, excludeId, out target)`:

1. **最後にユーザがフォーカスしたウィンドウ**（`_lastUserFocusedId`）が、まだ registered・live・可視・`excludeId` で
   ないなら、それ。
2. 無ければ **`anchorTopLeft`（ビューポート中央＝注視点）に中心が最も近い可視ウィンドウ**（同距離は id ordinal で
   決定的 tie-break）。
3. 可視窓が皆無のときだけ false（caller は anchor を verbatim 使用）。

- **フォーカス記録は `FloatingWindowController.NoteUserFocus(id)` だけが書く**。`FloatingWindowTitleInput.OnPointerDown` /
  `OnBeginDrag`（title-bar の実 press）から呼ぶ。**programmatic な `Show` / `BringToFront` / `Spawn` は記録しない**
  ——`RestoreFloating` が全窓を `BringToFront` するので、記録すると毎回フォーカスを偽造してしまう（この分離が肝）。
- `Close(id)` は `_lastUserFocusedId == id` のとき focus を null クリア（vanished target を捨てる）。stale でも resolver が
  liveness を再検証するので無害。
- AFK gate: `FloatingWindowE2ERunner` **S15**（DOCK-04）— focus 勝ち / **サイズ N 非依存（3 枚とも 520×360）** /
  programmatic BringToFront は focus を偽造しない / no-focus→最近傍 fallback / closed-focus→fallback / dup・unknown guard。

## §4 RED→GREEN litmus（`behavior-to-e2e` 正本）

- **S11 の pre-existing 偽 RED（#99 由来）を発見・修正**: `Section11`（snap controller wiring）が `strategy_editor` を
  **200×100** で spawn していたが、同 kind の minSize は **280×180**。`Spawn` の spec-min clamp が 280×180 へ拡大し、
  「drag」窓の右辺が 380 へずれて neighbour(305) と**重なり**、12px 閾値内の辺が無く `SnapOffset→(0,0)`。clean HEAD でも
  `[E2E … FAIL] S11: applied offset (0,0) expected (5,0)` を再現＝**#99 着地時から RED**（findings 0075 §9 の S11 GREEN
  主張は誤り）。修正は **test-only**（production は正しい）: サイズを minSize ちょうど（280×180）にし neighbour を 385 へ
  （flush gap 5px）＋ clamp 仮定を直接 guard。
- **DOCK-03（S14）**: 開発中に S14f が cascade の off-by-one（1 step 期待→実は 2 step が正）を捕捉＝非空虚を実証。
- **DOCK-04（S15）**: `SpawnDockedToFocus` のサイズを `spec.defaultSize / (_windows.Count+1)`（＝#99 の count-scaling を
  再導入）に差し替えて RED 化を確認: `[E2E … FAIL] S15a: chart size (280,200) != spec-fixed (520,360)` → 復帰で GREEN。
- 最終: 全 S1–S15 GREEN・exit 0・`error CS` 0 件（`Logs/fw_e2e_101_final.log`）。

## §5 再走手順

```
<Unity> -batchmode -quit -projectPath . -logFile Logs/compile.log                         # compile-only（error CS 0 件）
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod FloatingWindowE2ERunner.Run -logFile Logs/fw.log
# expect: [E2E FLOATING WINDOW PASS] … [WINDOW-01..10,SNAP-01,02,DOCK-01,02,03,04] / exit 0
# 確認は Bash `grep -a "E2E FLOATING WINDOW"`（Select-String / Grep ツールは取りこぼす）。
# Unity は同時 1 本（lock-abort 回避: 次起動前に Get-Process Unity が空か確認）。
```

## §6 HITL 目視（DOCK-05・実ピクセル必須＝AFK 不可）

owner playmode で確認（issue Slice 2 AC）:
- `[+Add]` で chart を 2〜3 枚足す → **何枚目でも同じ大きさ（520×360）**で出る。
- 直前にフォーカスした窓の**辺にくっついて**出る（フォーカス履歴が無ければ画面中央近くの窓へ）。
- chart の add/remove・mode 切替（Replay↔Live）・layout の persist round-trip を跨いでも大きさ一定＆吸着が崩れない。

## §7 不変条件（壊さない）

- base 5 枚の初回グリッド配置（`DockDefaultPlacement`）と既存保存 geometry は不変（スコープ外）。
- `DockSnapPlacement` は pure（live レイアウトの SoT ではない・spawn 後は自由移動・#99 snap 可）。
- ADR-0017 は無改変（自己保護条項）。本変更は findings 0075 §3/§4 を chart について supersede するのみ。
