# Infinite canvas Findings: pan/zoom 土台（capability parity・Unity 自前 versioned スキーマへ canvas view を additive 追加）

- Issue: #13 (S5 UI shell — infinite canvas / pan・zoom)
- 親: #1 (Epic: Bevy→Unity 移行) — UI シェル（**#3 の外**。#3 AC は Replay parity に限定）
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)（accepted, self-protection 節あり）, [ADR-0001](../adr/0001-unity-pythonnet-embedded-frontend.md)（proposed）
- 配置の根拠: ADR-0003 self-protection 節（capability surface の具体項目など下位事実は ADR に書き戻さず本 findings に記録し ADR を「方針: ADR-0003」として参照）。本 findings は #13 で確定した下位事実の記録であり ADR 方針を変更しない。**新規 ADR も起こさない**（canvas pan/zoom の persist は ADR-0003 が既にロックした capability surface の予約項目＝findings 0004 §10、かつ追加は additive・version 戦略で reverse 可能）。
- 先行: #9（Replay tracer / 0001）, #10（Replay chart / 0002）, #11（Replay panels / 0003）, #12（Replay layout / 0004）
- 実行環境（先行 slice と同一）: Intel x86_64 / macOS 13.7.8 / Unity 6000.4.11f1（standalone=Mono）/ uGUI 2.0 / Input System 1.19.0（`activeInputHandler:1` = 新 Input System）
- 設計確定: `grill-with-docs`（2026-06-13、owner インタビュー）。

---

## 0. スコープと段階づけ（owner 確定 2026-06-13）

#13 は **infinite canvas の土台**（pan/zoom できる Content 面）を durable に立て、canvas view 状態を #12 スキーマへ
additive 永続化する。AC2「panel/window を canvas に置いて追従」は **Content 配下の demo widget** で証明する。

**TTWR の実態に基づく最終構造**（owner が TTWR src を裏取り: `src/camera.rs:9` PanCam / `src/ui/hakoniwa.rs:672`
world-space Hakoniwa root / `src/ui/floating_window/spawn.rs:55` world-space floating window / `docs/wiki/screen-layout.md:28`
sidebar 画面固定）:

```
Canvas (ScreenSpaceOverlay)
├─ Viewport (固定・screen-anchored, GraphicRaycaster, optional RectMask2D)
│  └─ Content (RectTransform)         ← pan = anchoredPosition / zoom = localScale
│     ├─ Hakoniwa（chart tiles + status tiles）   … #14
│     └─ floating windows（Strategy Editor 等）    … #15
└─ screen-fixed chrome（menu / sidebar / footer / modal）  ← Content の外・追従しない
```

- **#13**: Content 面 + pan/zoom + canvas view persist。証明は Content-child demo widget（#11 panel/LayoutRect は触らない）。
- **#14 Hakoniwa**: chart + status 系を Content へ載せる。
- **#15 floating windows**: canvas 論理座標で rect/z-order を永続化。
- **#11 panels は恒久 HUD ではない**（暫定。#14 で Hakoniwa として canvas へ載る）。本 findings でも HUD と定義しない。

不採用: (A) #11 panels を #13 で Content へ移す（#12 の 0..1 正規化 LayoutRect 契約を乱す・責務先取り）/
(C) chart だけ canvas・status は HUD（TTWR に存在しない分割）。採用は **B: underlay + demo widget**（段階実装）。

## 1. uGUI 実現（owner 確定 2026-06-13）

**固定 Viewport ＋ 単一 Content transform**。pan = `Content.anchoredPosition`、zoom = `Content.localScale`。
canvas 上の widget は Content の子なので pan/zoom に自動追従（AC2 の機序）。world-space＋camera は不採用
（ScreenSpaceOverlay shell と別系統・camera/raycaster/座標変換の不要な別系統・headless 純算術ゲートと噛み合わない）。

## 2. pan/zoom セマンティクス（owner 確定 2026-06-13）

1. **zoom 方向 = Unity ネイティブ**: scroll up = zoom in（`localScale` 増加）。TTWR の `OrthographicProjection.scale`
   （大きいほど zoom out）とは **数値互換にしない**（uGUI は逆向き。capability parity であって format interop ではない）。
2. **cursor-centered zoom**: cursor 直下の canvas 論理点が zoom 前後で動かない。
3. **uniform scalar**: zoom は単一 float（`localScale = (zoom,zoom,1)`）。
4. **zoom clamp = `[0.2, 5.0]`**。TTWR PanCam の `min_scale=1e-5 / max_scale=∞ / pan bounds=±∞` は **bevy_pancam の
   ライブラリ既定値**（`bevy_pancam-0.20.0/src/lib.rs:420`）であり TTWR が意図した UX 境界ではない。極端 scale・精度劣化・
   操作不能を避けるため `[0.2,5.0]` を capability parity として採用（TTWR sidecar の `zoom=2.7293382` も逆数換算で
   uGUI ≈0.366 で範囲内）。
5. **pan = unbounded**（無限 canvas）。pan clamp も soft recenter も #13 では入れない（recenter は保存済み widget 座標
   全体の原点変更を伴い #14/#15 契約を複雑化。将来も「Reset View」等の表示 guidance に留め canvas 論理座標は書き換えない）。
6. **persist 表現 = `CanvasView { panX, panY, zoom }`**、pan は **Viewport 中心の canvas 論理点**（画面ピクセルでも
   zoom 後ピクセルでもない）。これで解像度非依存（「中心に何があるか + zoom」が view を完全決定）。**Y 軸は上向きが正**
   （uGUI ローカル座標に合わせる）。

### 純算術（CanvasViewMath が権威・headless）

Viewport 中心を原点とした座標で（Content は center anchor/pivot・回転なし）:

```
Apply : Content.localScale     = (zoom, zoom, 1)
        Content.anchoredPosition = -zoom * (panX, panY)
Capture: zoom = Content.localScale.x
         pan  = -Content.anchoredPosition / zoom

logicalUnderCursor = pan + c / zoom         # c = cursor の Viewport 中心相対座標
ZoomAtCursor: newZoom = clamp(zoom * factor, 0.2, 5.0)
              newPan  = logicalUnderCursor - c / newZoom   # ★ clamp 後の newZoom を使う
PanByScreenDelta: newPan = pan - dScreen / zoom            # drag 画面 delta → 論理 pan
LogicalToViewport(L, view) = -zoom*pan + zoom*L = zoom*(L - pan)   # child-follow 予測
```

## 3. スキーマ追加（owner 確定 2026-06-13）— additive・**version は据え置き v1**

`canvasView` は **互換を壊さない capability surface 追加**で version bump 対象ではない（#12 findings 0004 §6 の
forward-evolution 寛容＝unknown/欠落 field 寛容がまさにこのケース）。

```
[Serializable] class CanvasView { float panX; float panY; float zoom = 1f; }   // identity = (0,0,1)
class LayoutDocument { int version;            // CURRENT_VERSION = 1（据え置き）
                       List<PanelLayout> panels;
                       CanvasView canvasView; } // additive
```

正規化規則（**`LayoutStore.Sanitize()` が所有**・`LayoutBinder` には混ぜない）:
- 欠落 / null → identity `(0,0,1)`（panels は維持）。
- 非有限 pan → 軸ごとに 0。
- 非有限 または `zoom <= 0` → zoom = 1。
- 続けて zoom を `[0.2, 5.0]` へ clamp。
- `LayoutDocument.Default()` = identity view。`Clone()` / `StructurallyEqual()` に canvasView を含める
  （ゲートの mutated≠default が view 次元でも効くため）。

旧 v1 sidecar（#12 製・canvasView 無し）→ panels を保持しつつ view は identity に正規化（graccefully load）。

## 4. durable / throwaway 構成（owner 確定 2026-06-13）

**durable `Assets/Scripts/InfiniteCanvas/`**（`Canvas/` は `UnityEngine.Canvas` と検索衝突するため `InfiniteCanvas/`）:

| 型 | 役割 | 層 |
|---|---|---|
| `CanvasView`（`LayoutDocument.cs` 内 or 同フォルダ） | persist POCO（additive field） | schema |
| `CanvasViewMath` | §2 の純 float 算術（**AFK 権威ゲート**・playmode 非依存） | pure core |
| `InfiniteCanvasController` | input-agnostic plain class。`RectTransform content` を包み `PanByScreenDelta`/`ZoomAtCursor`/`CaptureView`/`ApplyView`。`CanvasViewMath` を呼び Content を読み書き | Unity boundary |
| `InfiniteCanvasInputSurface` | **薄い durable MonoBehaviour**。`IBeginDragHandler`/`IDragHandler`/`IScrollHandler` を実装し `PointerEventData` を controller へ渡すだけ（mouse/touchpad を直接参照しない・InputSystem polling を controller に混ぜない） | input boundary |

入力経路（**本番と同一**・#14/#15 が同じ surface+controller を再利用）:
`InputSystemUIInputModule → InfiniteCanvasInputSurface → InfiniteCanvasController → CanvasViewMath → Content`。
**input-reading は throwaway にしない**（throwaway にすると AC1 実装が throwaway にしか無く #14 が入力配線を再実装する）。

**throwaway**:
- `InfiniteCanvasHitlHarness`（MonoBehaviour）: UI 階層構築・grid 背景・demo widget・panX/panY/zoom readout・Reset View・
  Save/Load **のみ**所有（入力処理は複製しない）。**Editor menu `Tools > Backcast > Infinite Canvas HITL` で Play 中に
  明示 spawn**（auto-bootstrap 無し・Python 非依存・`ReplayPanelsHarness.AutoBootstrapEnabled` に触れない → single-Play-owner
  衝突回避、findings 0003 §8）。
- `InfiniteCanvasProbe`（`Assets/Editor`・`-executeMethod InfiniteCanvasProbe.Run`）: AFK 回帰ゲート。

## 5. ゲート（owner 確定 2026-06-13）— AFK 権威 + HITL 入力検証

- **AFK release gate**（headless・Python 非依存・決定的）: 変換数学・clamp・cursor invariant・child-follow・永続化。
- **HITL gate**（owner 起動）: Input System の drag/wheel が **実 controller** へ届き、操作感と screen-fixed chrome の
  不動を確認。AC1 の入力配線は HITL 検証（headless で全 AC 証明とは over-claim しない）。

### AFK probe の 6 セクション（三者独立 cross-check で false-green を排除）

純 math ⟂ Unity transform engine ⟂ serialization を **独立に**突き合わせ、同じ実装を両辺で呼ぶ偽緑を避ける:

1. **pan 算術**: `PanByScreenDelta` が論理 pan を `-dScreen/zoom` 移動・解像度非依存。
2. **zoom clamp**: `0.2 / 5.0` で停止（範囲外入力は飽和、overshoot しない）。
3. **cursor invariant（非空転）**: 通常 step ＋ clamped step の両方で `logicalUnderCursor` 前後一致。**両 step とも
   non-no-op**（`newZoom != zoom` を assert）。clamped step は **clamp 後の newZoom** で不変を確認。
4. **real RectTransform child-follow（非トートロジー）＋ controller Apply/Capture**: Content の実 child を既知論理点に置き
   `ApplyView` 後、**Unity の transform 合成**を Viewport ローカルへ戻して `CanvasViewMath.LogicalToViewport` 予測と一致:
   `viewport.InverseTransformPoint(content.TransformPoint(child.localPosition)) == LogicalToViewport(L, view)`。
   Viewport=identity transform・Content=center anchor/pivot・回転なしに固定。続けて `CaptureView()` が適用済み view へ
   戻ることも assert（Apply/Capture 境界の検証）。
5. **non-identity disk round-trip（vacuous-green kill）**: 安定表現値 `panX=123.25, panY=-45.5, zoom=2.5` へ mutate →
   save → **新規 instance** load → `loaded==mutated` ∧ `loaded!=default` ∧ **disk JSON テキストに field 名と各変更値が実在**。
6. **back-compat + sanitize + malformed-document fallback**:
   - 旧 v1 JSON（`canvasView` 無し）→ panels 維持・view identity。
   - JSON `zoom:0` → 1 / JSON `zoom:99` → 5（clamp）。
   - **非有限値は `CanvasView` へ直接 `float.NaN`/`Infinity` を設定し正規化関数で検証**（NaN は標準 JSON でないため
     JSON 経由だと文書全体 parse fallback を検証してしまう → sanitize ケースと分離）。
   - malformed JSON → 文書全体が default へ fallback（既存 `LayoutStore` 規律）。

## 6. 射程外（#13 に含めない）

- #11 panels を Content へ移すこと（#14 Hakoniwa）。floating window の canvas 論理座標 rect/z-order persist（#15）。
- pan clamp / soft recenter / origin 書き換え（§2.5）。autosave wiring（#12 §7 同様、変更確定/正常終了トリガは後送り）。
- per-workspace、multi-instrument、Windows leg。RectMask2D での実 clipping は optional（#13 は追従が本質）。

## 7. 実装結果（ゲートログ・Mac leg, 2026-06-13）

durable 4 ファイル（`Assets/Scripts/InfiniteCanvas/`）+ schema 拡張（`Layout/`）+ throwaway 2 ファイルを直接実装
（pair-relay/parallel は使わず — 単一言語 C#・Python 非依存・仕様完全確定・小スコープ。#12 と同じ Unity スライス
逸脱理由。CLAUDE.md 規約）。

成果物:
- **durable schema**: `Assets/Scripts/Layout/LayoutDocument.cs`（`CanvasView` POCO 追加 + additive field +
  `Default()` identity + `Clone()`/`StructurallyEqual()`。**`StructurallyEqual` は null canvasView と identity を
  同一視**＝Capture(null) と Default(identity) が一致する coalesce 規律）。`Assets/Scripts/Layout/LayoutStore.cs`
  （`Sanitize` に `NormalizeCanvasView` を追加。public で probe が非有限を直接検証可能）。
- **durable InfiniteCanvas**: `CanvasViewMath.cs`（純算術・AFK 権威）/ `InfiniteCanvasController.cs`（input-agnostic
  plain class・stateless・Content 読み書き）/ `InfiniteCanvasInputSurface.cs`（durable 薄 MonoBehaviour・
  `IBeginDragHandler`/`IDragHandler`/`IScrollHandler` → controller）。
- **throwaway**: `Assets/Scripts/InfiniteCanvas/InfiniteCanvasHitlHarness.cs`（Editor-menu spawn・grid+demo+readout+
  Reset+Save/Load）/ `Assets/Editor/InfiniteCanvasHitlMenu.cs`（`Tools > Backcast > Infinite Canvas HITL`）/
  `Assets/Editor/InfiniteCanvasProbe.cs`（6 セクション AFK ゲート）。

ゲート（VERBATIM, `UNITY_EXIT=0`, CS エラー 0, 自ファイル警告 0）:
```
[INFINITE CANVAS PASS] pan arithmetic + zoom clamp[0.2,5.0] + cursor-centred invariant (normal+clamped, non-no-op) + real-RectTransform child-follow (engine==math) + Apply/Capture boundary + non-vacuous CanvasView disk round-trip + back-compat/sanitize (Unity-owned versioned schema, additive capability surface, ADR-0003 capability parity, under Unity Mono)
```
**#12 回帰**: `LayoutDocument`/`LayoutStore` 変更（additive）後も `ReplayLayoutProbe` GREEN（`UNITY_EXIT=0`）を再確認。

AC 達成の証跡:
- **AC1（pan/zoom できる）**: AFK = pan 算術 / zoom clamp / cursor invariant（§5 S1-S3）。HITL = drag/wheel が
  `InfiniteCanvasInputSurface → controller` 経由で実 Content を動かす（owner 目視・menu 起動・owner 待ち）。
- **AC2（canvas 上の widget が pan/zoom 追従）**: AFK = real RectTransform child-follow を **Unity transform engine ==
  CanvasViewMath** で非トートロジー検証（§5 S4）。HITL = demo panel が追従・HUD は不動。
- **AC3（pan/zoom 状態が #12 スキーマへ persist・capability surface 追加）**: AFK = `CanvasView` の非空転 disk
  round-trip（mutated≠default・on-disk テキストに値実在）＋ 旧 v1 後方互換 + sanitize（§5 S5-S6）。

**HITL（owner 目視）GREEN（2026-06-13）**: `Tools > Backcast > Infinite Canvas HITL` を Play 中に spawn し、
findings §5 のチェックリスト全項目を確認（drag=pan / wheel=zoom@cursor＝カーソル直下不動 / demo panel 追従 /
HUD バー不動 / zoom 0.2–5.0 停止 / `Reset View`）。**Save→変更→Load の view 復元も実 round-trip で確認**:
`saved view -126.7,163.0,0.261 → loaded -126.7,163.0,0.261`（完全一致・zoom 0.261 は clamp 範囲内、
`persistentDataPath/infinite_canvas_hitl.json` 経由）。本番入力経路
（`InputSystemUIInputModule → InfiniteCanvasInputSurface → InfiniteCanvasController → CanvasViewMath → Content`）が
実機で機能することを実証。これで **AFK ゲート ＋ HITL 目視の両方 GREEN**＝#13 の release gate 完全達成。
