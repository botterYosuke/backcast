# 0123 — タイトルバー・ダブルクリック「ウィンドウフレーミング」（中心＋目一杯ズーム）

**正本**: issues #166 / #167 / #168。本 finding は 3 スライス（S1: pure 数学＋即時 apply、S2: ~200ms ease-out camera glide、S3: sidebar universe 行クリックで chart 窓フレーミング）の凍結設計の木と AFK 検証契約。

## §0. 設計サマリ（owner-locked）

floating window のタイトルバーを **ダブルクリック**したら、その窓を viewport 中心に据え、窓全体が入る最大ズーム（contain-fit）にジャンプする。S1 は瞬間 apply、S2 は 200ms ease-out のカメラグライド、S3 は sidebar universe 行クリックで対応 `chart:<iid>` 窓を同じ挙動でフレーミング（dock plane）。

- **全窓対象**（`FloatingWindowTitleInput` を持つ全窓 = dock 6 種 / strategy_editor / order / HITL）— ウィンドウタイプによる分岐は一切無い。
- **トグル無し**（常に fit・冪等）— 2 回目のダブルクリックは直前視点へ戻さない（owner 決定）。
- **plane 不可知**で動かす — 各窓は所属 plane（floating=1.2× / dock=1.0×）の parallax factor を Initialize で受け取り、`CanvasViewMath.FrameWindow` は plane を引数で受ける pure 関数。

## §1. 数学 seam（pure・AFK 権威）

新規 `CanvasViewMath.FrameWindow(topLeft, size, viewportSize, parallaxFactor, marginFraction) → CanvasView`。`ZoomAtCursor` と同じ headless 規律（render 触らず・input 触らず）。

### §1.1 入力

- `topLeft` … 窓の RectTransform.anchoredPosition（pivot=(0,1)・x 右+、y 上+、findings 0008 §2）。
- `size` … 窓の sizeDelta（logical px）。
- `viewportSize` … `_viewport.rect.size`（logical px、CanvasScaler 越しの実寸）。
- `parallaxFactor` … plane の parallax 係数。floating plane=1.2、dock plane=1.0（findings 0006 §2 / 0075 §10）。
- `marginFraction` … 0.06（窓の左右上下に 6% 余白を残して fit、owner デフォルト）。

### §1.2 窓中心

```
centre = topLeft + (size.x / 2, -size.y / 2)   // pivot 左上・Y up
```

### §1.3 fit zoom（contain＋margin）

screen で窓が viewport の (1−m) 倍以内に入る最大の uniform zoom：

```
zoom_fit = min((1 − m) · vw / w, (1 − m) · vh / h)
zoom     = clamp(zoom_fit, CanvasView.MIN_ZOOM, CanvasView.MAX_ZOOM)   // [0.2, 5.0]
```

zoom は plane 不可知（Content.localScale は両 plane 共有のため、screen size は両 plane とも `zoom · sizeDelta` で一致）。

### §1.4 pan（parallax 補正＝load-bearing）

base plane では `viewport(L) = zoom · (L − pan)`、parallax plane では `viewport(L) = zoom · (L − factor · pan)`（findings 0006 §2 のオフセット導出を inverse に取ったもの＝`O = (1−factor)·pan` を足すと d(viewport)/d(pan) = −factor·zoom になる関係）。窓中心を viewport (0,0) に置くには：

```
0 = zoom · (centre − factor · pan)
⇔ pan = centre / factor          // factor=1.0 (dock plane) でも自然に成立
```

**省くと前面 plane の窓が中心からズレる**（floating plane で `pan=centre` を使うと、`viewport(centre) = zoom · (centre − 1.2·centre) = −0.2 · zoom · centre` の分だけオフセット）。AFK probe（§4）が dock と floating の両方で「centre は viewport 原点に乗る」を assert する。

### §1.5 clamp による subsumed centring の保持

巨大窓（`zoom_fit < MIN_ZOOM`）/ 極小窓（`zoom_fit > MAX_ZOOM`）でも、`pan` は clamp 前の中心式を用いて先に決め、zoom は別途 clamp する（CanvasView.MIN_ZOOM=0.2、MAX_ZOOM=5.0、`LayoutDocument.cs` §75 で定義）。これにより clamp が効いても「中心は合う・ズームだけ bounds」という直感的な縮退になる（AFK §4-D）。

### §1.6 安全規律

- `parallaxFactor <= 0` や NaN/Inf は 1f に倒す（`ParallaxLayerOffset` と同じ SafeFactor 規律）。
- `size.x <= 0` や `size.y <= 0`（degenerate）は zoom 計算で 0 除算を避け、MAX_ZOOM へ倒す。
- `viewportSize.x|y <= 0` のときは現視点をそのまま返す（headless 初期化途中での call を no-op 化）。

## §2. 検出 seam（タイトルバー・ダブルクリック）

`FloatingWindowTitleInput` に `IPointerClickHandler` を追加。`OnPointerClick` で `eventData.clickCount >= 2` を判定。

- 新 Input System の `InputSystemUIInputModule` は `PointerEventData.clickCount` を populate する（Unity 2022.2+ / 6000.x で確認、本 repo は 6000.4.11f1）。
- `IPointerClickHandler` は **drag が起きた pointer-up では fire しない**（uGUI EventSystem 仕様）。これにより issue #166 AC#3「drag 中・単クリックでは発火せず」を自然に満たし、追加の `_dragging` ガードは不要（既存 `OnBeginDrag/OnEndDrag` 経路と非干渉）。
- 単クリック（clickCount==1）は no-op。既存の `OnPointerDown` 経由 focus / island / Alt 単窓ピックアップ / ESC は無回帰。

`Initialize` に `parallaxFactor` を追加（default 1f で後方互換）。floating plane の Initialize（`_strategyEditorTitleInput`, `_orderWindowTitleInput`, BuildFloatingWindowFrame）は `_floatingParallaxFactor`（=1.2f）、dock plane の Initialize（BuildDockWindowFrame）は明示 1.0f（または default）。HITL / Resize E2E ランナーは framing 検査外なので default 1.0f のままで良い。

### §2.1 適用 seam（S1 即時 apply / S2 グライド開始）

`Initialize` に `Action<CanvasView> applyView` 引数を足す（default null）。null のときは `_canvas.ApplyView` を直呼び（S1 immediate）。S2 land 後は BackcastWorkspaceRoot が `view => _glideDriver.BeginGlide(view)` を供給する。タイトル入力は「どう適用するか」を知らない（plane 不可知＋tween 不可知）。

## §3. グライド seam（S2、~200ms ease-out）

新規 `CameraGlideDriver` MonoBehaviour（`RectSpringDriver` のミラー）。

- API:
  - `BeginGlide(CanvasView target)` — 現在の `controller.CaptureView()` を `from` に取り、`to` に格納、`elapsedMs=0` で開始。in-flight があれば置換（kill）。
  - `Stop()` — in-flight をクリア（テスト用 / 手動切り替え用）。
  - `IsAnimating` — 観測子。
- `Update()`:
  - `elapsedMs += Time.unscaledDeltaTime * 1000`（time-scale 不変）。
  - `t = clamp01(elapsedMs / DURATION_MS)`。
  - **割り込み検知（findings 0088 §14）**: `actual = controller.CaptureView()`. 直前 frame で書いた値 `_lastApplied` と `eps=1e-3` 以上離れていれば「外部（input surface の Pan/Zoom 等）が書き戻した」=「割り込み」として `Stop()`。
  - `eased = 1 − (1 − t)^3`（cubic ease-out、overshoot 無し、単調増加）。
  - `view_t = lerp(from, to, eased)`（pan・zoom 各成分で線形補間）。
  - `controller.ApplyView(view_t)` + `_lastApplied = view_t`。
  - `t >= 1` で `controller.ApplyView(to)` + クリア。
- 定数:
  - `DURATION_MS = 200f`（findings 0088 §3 spring と整合）。
  - `EPS_INTERRUPT = 1e-3f`。
- **overshoot 無し**（spring の 8% は外す。zoom が MAX/MIN を超えると clipping 体感になるため）。`lerp(from.zoom, to.zoom, eased)` は `eased∈[0,1]` の単調 lerp なので、両端が `[MIN_ZOOM, MAX_ZOOM]` に clamp 済みなら途中も `[MIN_ZOOM, MAX_ZOOM]` 内に収まる（AFK §4-E）。

### §3.1 割り込み (kill) の規律

「fire-point は確定点のみ・途中状態を persist しない」（findings 0088 §14）。割り込み時はその時点の中間 view を最終値として残す（Content transform に既に書かれている）。新しい framing が来たら BeginGlide で再開、ユーザーの pan/zoom 中なら何もしない（ユーザー操作優先）。

### §3.2 headless 駆動

`Update()` はテスト性のために `Advance(float dtMs)` という pure-style 内部メソッドへ delegate する（AFK で `Update()` を呼ばずに `Advance` を直接 tick できる、`RectSpringDriver` と同じ規律）。

## §4. AFK 検証契約（behavior-to-e2e の Action-ID 紐づけ）

`Assets/Editor/FramingProbe.cs` に集約。`scripts/run-all-tests.ps1` の rollup へ Action-ID タグで載せる。

### §4-A `[E2E FRAMING-S1-MATH-CENTRE PASS]`

dock plane（factor=1.0）と floating plane（factor=1.2）の双方で、ランダム的な topLeft/size に対して `FrameWindow` の結果 view を使い `LogicalToViewport(centre)` が `(0, 0)`（eps 1e-3）になることを assert。**dock と floating の両方を assert**（issue #166 AC#1）。floating plane で `pan = centre`（factor 無視）を使った場合と比較して、`viewport(centre)` が原点から外れることも示し parallax 補正が load-bearing であることを pin。

### §4-B `[E2E FRAMING-S1-MATH-FIT PASS]`

複数 (vw, vh, w, h) 組で、結果 zoom が `(1−m)·vw/w` と `(1−m)·vh/h` の小さい方に一致（eps）。さらに `viewport(corner)` が `±(1−m)·vw/2 × ±(1−m)·vh/2` の枠内に収まる（contain 不変条件）。`m=0.06` を明示的に検証する別ケース（`m=0` で枠ぴったり）。

### §4-C `[E2E FRAMING-S1-MATH-MARGIN PASS]`

`m=0.06` のとき、窓の screen 上の幅が viewport の 94% 以内に収まる（`zoom · w ≤ 0.94 · vw`）。

### §4-D `[E2E FRAMING-S1-MATH-CLAMP PASS]`

- 極小窓（w=h=1）: `zoom_fit > MAX_ZOOM` → 結果 zoom == MAX_ZOOM、ただし centre は依然 viewport 原点。
- 巨大窓（w=h=100000）: `zoom_fit < MIN_ZOOM` → 結果 zoom == MIN_ZOOM、centre は依然 viewport 原点。

### §4-E `[E2E FRAMING-S2-GLIDE PASS]`

`CameraGlideDriver` を headless RectTransform に対し起動。

- `Advance(0)` で開始時 view が `from`。
- 200ms 等価分 advance（複数刻みでも一括でも）した時点で `to` に収束（eps）。
- 途中時点の view が `from` と `to` の lerp（cubic ease-out）に一致（複数 t で確認）。
- 途中 zoom が `[MIN_ZOOM, MAX_ZOOM]` を超えない（overshoot 無し）。

### §4-F `[E2E FRAMING-S2-INTERRUPT PASS]`

グライド中に `controller.PanByScreenDelta` で外部書き戻し → 次の `Advance` で `IsAnimating == false`（kill された）。kill 後の view はその時点で外部が書いた値に等しい（途中状態が上書きされない）。

### §4-G `[E2E FRAMING-S3-SIDEBAR PASS]`

`UniverseSidebarController` を生成、`FocusChartHook` を assign、`SelectRow(id, Replay)` を **同じ id で連続 2 回**呼ぶ → hook は 2 回 invoke される（SelectedSymbol.Changed への相乗りだと 1 回しか発火しない）。さらに `_selected.Value` が変わるか否かによらず hook が発火することを assert。

### §4-H `[E2E FRAMING-S3-NOOP PASS]`

`BackcastWorkspaceRoot.FrameChartWindow(iid)` 相当のロジック（または pure helper として抽出した版）に対し、`_dockWindows.RectOf(chartId) == null`、または gameObject inactive のときに glide が始まらない（`BeginGlide` が呼ばれない）ことを assert。

### §4-I `[E2E FRAMING-S1-DBL-CLICK PASS]`

`FloatingWindowTitleInput` インスタンスに `PointerEventData{ clickCount=2 }` を `OnPointerClick` 経由で投げる → 供給した `applyView` コールバックが 1 回呼ばれ、その引数 view が `FrameWindow` の結果と一致。`clickCount=1` では呼ばれない。

## §5. 既存設計との関係

- **findings 0006 §2**: 座標モデル。`viewport(L) = zoom · (L − pan)` の base plane と、`ParallaxLayerOffset` で導出される `O = (1−factor)·pan` の parallax offset 規律を参照。本 finding §1.4 の `pan = centre / factor` はその inverse。
- **findings 0008 §2**: floating 窓の pivot=(0,1)・anchoredPosition は logical 中心相対の top-left、sizeDelta は logical px。本 finding §1.2 の centre 式の根拠。
- **findings 0075 §10 / ADR-0018**: front=1.2× floating plane / back=1.0× dock plane の depth cue。本 finding §1.4 で factor を引数化する根拠。
- **findings 0088 §3 / §14**: spring 200ms / per-frame tween 競合の規律。本 finding §3 で duration と割り込み検知の根拠。`AnimateRectSpring` の overshoot 8% は camera では外す（本 finding §3）。
- **ADR-0029 §1 / findings 0106 §1**: drag channel は OnBeginDrag で固定・mid-gesture で変わらない。本 finding §2 の double-click 検出は drag と排他（`IPointerClickHandler` 仕様で drag-end click は fire しない）→ channel 干渉なし。
- **findings 0024 D2**: SelectedSymbol = focused row。本 finding §3 で `FocusChartHook` を `SelectRow` 末尾に追加するが SelectedSymbol の意味は変えない。

## §6. 用語

- **ウィンドウフレーミング（タイトルバー・ダブルクリック）** … 窓中心を viewport 原点に据え、窓全体が contain-fit する uniform zoom（margin 6%）にジャンプする操作。タイトルバー・ダブルクリック発火、または sidebar 行クリック（chart:<iid> ターゲット、S3）から発火。S1 は即時 apply、S2 は 200ms ease-out グライド。trigger とは独立に math は pure（`CanvasViewMath.FrameWindow`）。

## §7. ロールアップタグ表

| Action-ID | スライス | 内容 |
|---|---|---|
| `FRAMING-S1-MATH-CENTRE` | S1 (#166) | dock/floating 両 plane で centre が viewport 原点 |
| `FRAMING-S1-MATH-FIT` | S1 (#166) | contain-fit zoom が両軸の小さい方に一致 |
| `FRAMING-S1-MATH-MARGIN` | S1 (#166) | screen 上の窓幅が viewport の (1−m) 倍内 |
| `FRAMING-S1-MATH-CLAMP` | S1 (#166) | 極小→MAX, 巨大→MIN（centre は維持） |
| `FRAMING-S1-DBL-CLICK` | S1 (#166) | clickCount==2 で applyView 1 回・==1 で 0 回 |
| `FRAMING-S2-GLIDE` | S2 (#167) | 200ms 後 to に収束・途中 lerp 一致・overshoot 無し |
| `FRAMING-S2-INTERRUPT` | S2 (#167) | 外部書き戻しで kill・kill 後値はその外部値 |
| `FRAMING-S3-SIDEBAR` | S3 (#168) | SelectRow ごとに FocusChartHook 1 発火（再クリックでも） |
| `FRAMING-S3-NOOP` | S3 (#168) | 窓未生成 / inactive で no-op |
