# Floating windows Findings: spec 駆動 floating window（drag-move / z-order / canvas 論理座標 rect・z persist）

- Issue: #15 (S7 UI shell — floating windows: drag/z-order/spec 駆動) — 親 #1 (Epic)・**#3 の外**
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)（accepted, self-protection 節あり）, [ADR-0001](../adr/0001-unity-pythonnet-embedded-frontend.md)（proposed）
- 配置の根拠: ADR-0003 self-protection 節（capability surface の具体項目など下位事実は ADR に書き戻さず本 findings に記録し ADR を「方針: ADR-0003」として参照）。本 findings は #15 で確定した下位事実の記録であり ADR 方針を変更しない。**新規 ADR も起こさない**（floating window の rect/z-order persist は ADR-0003 が**既に**ロックした capability surface 項目＝「floating window の rect / z-order」、zOrder field は findings 0004 §3 が予約済み、追加は additive・version 戦略で reverse 可能）。
- 先行: #9（Replay tracer / 0001）, #10（Replay chart / 0002）, #11（Replay panels / 0003）, #12（Replay layout / 0004）, #13（infinite canvas / 0006）, #14（Hakoniwa split-grid / 0007）
- 実行環境（先行 slice と同一）: Intel x86_64 / macOS 13.7.8 / Unity 6000.4.11f1（standalone=Mono）/ uGUI 2.0 / Input System 1.19.0（新 Input System）
- 設計確定: `grill-with-docs`（2026-06-13、owner インタビュー）。

> **状態: AFK ゲート + HITL 目視の両方 GREEN（Mac leg, 2026-06-13）。** 設計は `grill-with-docs` で確定、直接実装（単一言語
> C#・Python 非依存・仕様完全確定・小スコープ＝#12/#13/#14 と同じ Unity スライス逸脱理由。CLAUDE.md 規約）。
> `FloatingWindowProbe.Run` が batchmode で `exit=0`、CS エラー 0・自ファイル警告 0。#12/#13/#14 回帰も GREEN。HITL は owner が
> §7 全項目を目視確認し実 disk round-trip も実証。実装結果・ゲートログは §11。

---

## 0. スコープと段階づけ（owner 確定 2026-06-13）

#15 は **spec 駆動 floating window システム**（spawn / title-drag move / click-to-front z-order / persist）を durable に
立て、window の rect（canvas 論理座標 position+size）・z-order・visible を **#12 スキーマへ additive 永続化**する。
証明は Python-free demo window（#13/#14 と同じ HITL demo 規律）。

**TTWR 裏取り**（owner、`src/ui/floating_window/`）: `FloatingWindowSpec { title, size, position, accent, closeable,
resizable }`（spec 駆動）／z-order = `transform.translation.z`、click で `WindowManager.max_z += 2.0`（click-to-front）／
title bar drag で move／**chart は floating window から廃止**（`dispatcher.rs` が `PanelKind::Chart` spawn を
`"Chart is deprecated; ignored"` で拒否）／persist は `WindowGeometry { visible, position[2], size[2], z }`。

### issue 記載との不一致を解消（owner 確定）

issue #15 本文の「Chart / Strategy Editor / Order などを drag 可能」の **Chart 記載は #14 より前の stale wording**。
chart は Hakoniwa tile（#14・findings 0007 §1・CONTEXT「Hakoniwa」）であり floating window ではない。#15 の demo kind は
**`strategy_editor`（多 instance）+ `order`（singleton）** の 2 種。

### 採用 / 不採用

- **採用**: Content 直下の単一 **FloatingWindowLayer**（全 window をその子に・z-order を HakoniwaRoot から分離）/ spec
  catalog 駆動 spawn / title-drag free-move / `SetAsLastSibling` click-to-front / canvas 論理座標 position+size + zOrder +
  visible persist / 純算術 AFK 権威ゲート + Python-free HITL demo。
- **不採用（= #15 外・将来 slice の additive 拡張、または別 slice の責務）**:
  - **resize gesture / resize handle**（TTWR `resizable`）→ 将来 slice。ただし persist 表現は position **+ size** で、
    非 default size の restore まで AFK ゲートで証明する（owner 指示）。
  - **実 Strategy Editor content / 実 Order form**（実 content は別 slice。catalog は content factory を持たない）。
  - **mode lifecycle / autosave / per-workspace / Windows leg**。
  - **Strategy Editor の常時最前面 pin**（TTWR `pin_strategy_editor_front_system`・issue #90）→ 実 editor content/overlay
    由来の例外であり #15 の**汎用** window system には含めない（owner 確定）。

## 1. 構造モデル（owner 確定 2026-06-13）— FloatingWindowLayer

```
Canvas (ScreenSpaceOverlay)
└─ Viewport（固定・#13。raycast bg + InfiniteCanvasInputSurface）
   └─ Content（pan=anchoredPosition / zoom=localScale・#13）
      ├─ HakoniwaRoot（#14）
      └─ FloatingWindowLayer（**identity transform** under Content）
         ├─ Window[strategy_editor:region_001]
         ├─ Window[strategy_editor:region_002]
         └─ Window[order]
```

- **FloatingWindowLayer** = Content 直下の単一コンテナ。全 floating window はその子。Content の子なので **pan/zoom に
  自動追従**（#13 の child-follow 機序）。HakoniwaRoot と sibling order（z-order）を**混在させない**（owner 修正点 1）。
- **z-order = FloatingWindowLayer 内の sibling index**（uGUI は child 0 = 最背面で描画）。`BringToFront` = `SetAsLastSibling`
  （TTWR `WindowManager.max_z` bump の capability parity・形式非互換）。
- window body は **raycastTarget=false** → body drag は InfiniteCanvasInputSurface へ落ちて **canvas pan**（#14 tile body と
  同規律）。title bar のみ raycast target（move/raise handle）。

## 2. 座標規約（owner 確定 2026-06-13）

- window RectTransform: **anchorMin/Max=(0.5,0.5)、pivot=(0,1)（top-left）** を正準形。
- `x,y` = **top-left pivot の anchoredPosition**（Content canvas-logical）。`x` 右向き正、`y` 上向き正。
- `w,h` = canvas logical px。
- FloatingWindowLayer は Content に対して **identity transform**（座標変換しない）。よって window の `anchoredPosition` が
  そのまま canvas 論理 top-left、child-follow は #13 の `CanvasViewMath.LogicalToViewport` を再利用できる。
- drag move: title input が screen delta → **Viewport-local delta**（`RectTransformUtility`・CanvasScaler-safe・#13 と同機構。
  **raw `eventData.delta` を直接 ÷zoom しない**＝owner 修正点 2）→ `logicalDelta = viewportLocalDelta / zoom`。

## 3. persist 表現（owner 確定 2026-06-13）— additive・**version 据え置き v1**

`floatingWindows` は互換を壊さない capability surface 追加で version bump 対象ではない（#12 findings 0004 §6 の
forward-evolution 寛容＝unknown/欠落 field 寛容がまさにこのケース・#13 `canvasView` と同判断）。

```
[Serializable] class FloatingWindowLayout {
    string id;      // document 内で一意。重複は最初を採用し後続 drop
    string kind;    // spec catalog の再 spawn キー（id と別。多 instance は kind 共有）
    float x, y;     // top-left pivot の canvas 論理座標（0..1 正規化ではない）
    float w, h;     // canvas logical px（非有限/<=0 は LayoutStore が entry drop）
    int   zOrder;   // 0=最背面。**verbatim 保存**（load では正規化しない）
    bool  visible;
}
class LayoutDocument { int version;           // CURRENT_VERSION = 1（据え置き）
                       List<PanelLayout> panels;
                       CanvasView canvasView;          // #13
                       List<FloatingWindowLayout> floatingWindows; }  // #15 additive
```

- **canvas 論理座標 position+size**（panel/tile の 0..1 正規化 `LayoutRect` とは別次元）。floating window は**無限 canvas 上の
  自由配置**で正規化する bounded parent が無いため絶対論理座標を持つ（findings 0006 §2 の unbounded pan と整合）。
  **`PanelLayout.rect` を流用しない**（CONTEXT「floating window」_Avoid_）。
- **`zOrder` は `slot` と同一視しない別 field**（findings 0004 §3 が予約済み）。
- **id は document 一意**。重複は最初を採用・後続 drop（`LayoutStore.NormalizeFloatingWindows`）。多 instance は
  `id="strategy_editor:region_001"` で `kind="strategy_editor"` を共有。`order` は singleton `id="order"`。

### 責務分界（owner 確定）

| 事象 | LayoutStore（persistence 境界） | restore controller（spawn 境界） |
|---|---|---|
| 非有限 `x/y` | → 0（軸ごと） | — |
| 非有限 or `<=0` の `w/h` | **entry を drop**（汎用 fallback にしない） | — |
| 小さいが正の `w/h` | そのまま保存 | **spec.minSize へ clamp up** |
| 重複 id | 最初を採用・後続 drop | — |
| **unknown kind** | **保持**（catalog を知らない） | **spawn skip**（forward-evolution 規律） |
| `zOrder` 正規化 | しない（**verbatim**） | Apply で stable normalize → sibling index |

- `Default()` は **空の `floatingWindows`**（既定で開く window なし。HITL/本番が on-demand spawn）。
- `Clone()` / `StructurallyEqual()` に `floatingWindows` を含める（id 突合・list 順は非依存・null↔empty を coalesce）。

## 4. z-order 正規化と Apply/Capture（owner 確定 2026-06-13）

純算術（headless・AFK 権威）:
```
ViewportDeltaToLogical(viewportDelta, zoom) = viewportDelta / zoom      # zoom<=0/非有限 → zero（guard）
SiblingOrder(zOrders) → permutation[0..n)：zOrder 昇順、同値は元 list 順で安定 → sibling slot（0=最背面）
```

- **Capture**: 各 window を sibling index 順に並べ、`zOrder = 0..n-1`（**contiguous**・live sibling index を再 rank）として
  `floatingWindows` へ書く。x,y=anchoredPosition / w,h=sizeDelta / visible=activeSelf。panels は空・canvasView は呼び出し側が merge。
- **Apply = full replacement**（owner 確定）: document に無い既存 window は**除去**（destroy）／document の window は spawn
  （or 既存なら reposition/resize/再表示）／`visible=false` は**登録を残して非表示**。document 自体の `zOrder` は変更せず、
  **stable normalize した結果だけ**を `SetSiblingIndex` に適用。unknown kind は spawn せず（document には保持）。
- window 生成は **factory 注入**（controller は placement/z/capture/apply のみ所有・視覚階層と title input 配線は caller の
  factory が構築）。除去も **destroy 注入**（runtime=`Object.Destroy` / edit-mode probe=`Object.DestroyImmediate`）。

## 5. 入力（title-drag move / click-to-front）— owner 確定 2026-06-13

- **click-to-front = title bar の pointer-down / begin-drag のみ**（owner 修正）。body は raycastTarget=false で canvas pan へ
  落ちるため「body press → front」とは両立しない。将来の実 content は自身が pointer event を受けた際に `BringToFront(id)` を呼ぶ。
- `FloatingWindowTitleInput.Initialize(windowController, infiniteCanvasController, viewport, windowId)`。input boundary が
  `RectTransformUtility` で delta を Viewport-local へ変換し、`CaptureView().zoom` を読み、`controller.MoveByLogical(id, logicalDelta)`
  を呼ぶ。**controller は screen/render 座標変換を一切持たない**。
- pan/zoom（#13 `InfiniteCanvasInputSurface`）とは別 drag target。uGUI イベントは topmost handler（title bar）で消費。
- **input-reading は throwaway にしない**（durable。#13/#14 の規律）。

## 6. durable / throwaway 構成（owner 確定 2026-06-13）

durable **`Assets/Scripts/FloatingWindow/`**（成果物 5 型）:

| 型 | 役割 | 層 |
|---|---|---|
| `FloatingWindowMath` | `ViewportDeltaToLogical` / `SiblingOrder`（純算術・**AFK 権威**・playmode 非依存） | pure core |
| `FloatingWindowSpec` | window kind の spec 駆動定義（title/defaultSize/minSize/accent/closeable・**content factory 無し**） | spec |
| `FloatingWindowCatalog` | `kind → spec` registry。`Default()` = `strategy_editor` + `order`（**chart 無し**）。unknown kind は `TryGet=false` | spec |
| `FloatingWindowController` | input-agnostic plain class。layer 配下の window を管理。`Spawn`/`MoveByLogical`/`BringToFront`/`Capture`/`Apply`（full replacement・spec-min clamp・z normalize）。factory/destroy 注入 | Unity boundary |
| `FloatingWindowTitleInput` | 薄い durable MonoBehaviour。title drag → Viewport-local delta → `MoveByLogical`、pointer-down/begin-drag → `BringToFront` | input boundary |

persist は #12 の `LayoutDocument` / `LayoutStore` / `LayoutPathResolver` を再利用（`FloatingWindowLayout` 型 + `floatingWindows`
field + `NormalizeFloatingWindows` を additive 追加）。

throwaway:
- `FloatingWindowHitlHarness`（MonoBehaviour）: infinite canvas（#13 surface/controller 再利用）+ FloatingWindowLayer + 3 demo
  window（`strategy_editor:region_001` / `:region_002` / `order`・Python-free placeholder body）+ HUD（Reset View / **Mutate
  Demo** / Save / Load）+ title bar × hide。**Editor menu `Tools > Backcast > Floating Window HITL` で Play 中に明示 spawn**
  （auto-bootstrap 無し・`ReplayPanelsHarness.AutoBootstrapEnabled` に触れない → single-Play-owner 衝突回避、findings 0003 §8）。
- `Assets/Editor/FloatingWindowHitlMenu.cs`（`Tools > Backcast > Floating Window HITL`）。
- `Assets/Editor/FloatingWindowProbe.cs`（`-executeMethod FloatingWindowProbe.Run`・AFK 回帰ゲート）。

## 7. ゲート（owner 確定 2026-06-13）— AFK 権威 + HITL 入力検証

### AFK probe の 6 セクション（純 math ⟂ Unity transform engine ⟂ serialization の三者独立 cross-check）

1. **drag→論理算術**: `ViewportDeltaToLogical = viewportDelta/zoom`（zoom 2 で半分・0.5 で倍・解像度非依存）、`zoom<=0/NaN/負` は
   zero guard。
2. **z-order 正規化**: 非連続/重複/負 `[5,2,99,2,-1]` → 安定 `[4,1,3,0,2]`（zOrder 昇順・同値は元 list 順）。
3. **real-RectTransform placement + child-follow（engine==math・非トートロジー）**: identity layer を含む実 transform 合成
   `viewport.InverseTransformPoint(window.position) == CanvasViewMath.LogicalToViewport(L, view)`（owner: anchoredPosition を
   既知論理点に設定し identity layer 込みで検証）。layer identity・pivot(0,1)・sizeDelta も assert。`MoveByLogical` 後も follow 維持。
4. **z-order live application + BringToFront（engine==math）**: 非連続 z `{5,2,99}` を Apply → sibling index `{0,1,2}`。Capture が
   contiguous 0..n-1 を出力。`BringToFront` → last sibling。
5. **非空転 disk round-trip（vacuous-green kill）**: 2 strategy_editor + order、非 default size/position、非連続 z、`visible=false`
   1 枚 → save → **新規 instance** load → `loaded==mutated`（zOrder verbatim）∧ `loaded!=default` ∧ **on-disk JSON テキストに
   id/kind/各値が実在** → fresh controller へ Apply で sibling 順・geometry・visibility 復元。
6. **back-compat + sanitize + fallback**: 旧 #13 sidecar（`floatingWindows` 無し）→ 空 list・panels/canvasView 維持／重複 id →
   最初採用／**非有限 or <=0 の w/h → entry drop**（**非有限は JSON ではなく POCO に NaN/Inf を直接設定**して検証・malformed
   JSON fallback と分離）／非有限 x/y → 0／**unknown kind → store 保持・Apply spawn skip**／spec-min clamp（spawn 境界）／
   malformed JSON → 文書全体 default。

7 つ目のゲート項目 = **#12/#13/#14 回帰**は各既存 probe を**個別実行**して確認（§11・本 probe 内からは呼ばない）。

### HITL gate（owner 起動・menu spawn）

3 demo window が表示／title-drag で move（自由配置）／press/drag で front 化（重なりの前後が視認できる）／背景・body drag で pan・
wheel で zoom し全 window が追従・HUD バー不動／× で hide（visible=false）／**Mutate Demo** で size 変更 → Save → Load で
position+size+z+visible 復元（非表示後も復元できるよう HUD の Load は画面固定で維持）。

## 8. 射程外（#15 に含めない）

- resize gesture / handle（TTWR `resizable`・将来 slice）／実 Strategy Editor content・実 Order form（catalog content factory）。
- Strategy Editor 常時最前面 pin（TTWR issue #90・実 content 由来の例外）／mode lifecycle／autosave wiring／per-workspace／
  multi-instrument／Windows leg。

## 9. AC 達成方針

- **AC1（drag 移動・z-order 制御）**: AFK = §7 S1（drag 算術）+ S3（placement/move）+ S4（z live + BringToFront）。
  HITL = title-drag move・press-to-front。
- **AC2（spec 駆動生成）**: `FloatingWindowSpec` + `FloatingWindowCatalog`（kind→spec）。AFK = S4/S5（catalog spawn）+ S6（unknown
  kind skip / spec-min clamp）。HITL = 3 demo window が spec から生成。
- **AC3（#12 スキーマへ rect/z-order persist/restore）**: AFK = S5（非空転 disk round-trip・on-disk テキスト proof）+ S6（旧
  sidecar 後方互換 + sanitize）。HITL = Save/Load round-trip。

## 10. ADR 配置の確認

ADR-0003 self-protection 節に従い **新規 ADR は起こさない・ADR-0003 も編集しない**。floating window の rect/z-order persist は
ADR-0003 decision 2 が**既に列挙した** capability surface 項目（「floating window の rect / z-order」）であり、`zOrder` field は
findings 0004 §3 が予約済み。本 findings が下位事実（型・座標規約・責務分界・ゲート）を記録し ADR-0003 を「方針: ADR-0003」として参照する
（#13/#14 と同パターン）。

## 11. 実装結果（ゲートログ・Mac leg, 2026-06-13）

durable 5 ファイル（`Assets/Scripts/FloatingWindow/`）+ schema 拡張（`Layout/`）+ throwaway 3 ファイルを直接実装
（pair-relay/parallel は使わず — 単一言語 C#・Python 非依存・仕様完全確定・小スコープ。#12/#13/#14 と同じ Unity スライス逸脱理由・
CLAUDE.md 規約）。

成果物:
- **durable schema**: `Assets/Scripts/Layout/LayoutDocument.cs`（`FloatingWindowLayout` POCO 追加 + `floatingWindows` additive field
  + `Default()` 空 list + `Clone()`/`StructurallyEqual()`/`FindWindow()`。StructurallyEqual は null↔empty を coalesce）。
  `Assets/Scripts/Layout/LayoutStore.cs`（`NormalizeFloatingWindows` を `Sanitize` に追加。public で probe が POCO 直接検証可能）。
- **durable FloatingWindow**: `FloatingWindowMath.cs`（純算術・AFK 権威）/ `FloatingWindowSpec.cs`（spec・content factory 無し）/
  `FloatingWindowCatalog.cs`（kind→spec・`Default()` = strategy_editor + order）/ `FloatingWindowController.cs`（input-agnostic
  plain class・factory/destroy 注入・full replacement Apply・spec-min clamp・z normalize）/ `FloatingWindowTitleInput.cs`
  （薄 MonoBehaviour・Viewport-local delta → `MoveByLogical`・pointer-down/begin-drag → `BringToFront`）。
- **throwaway**: `FloatingWindowHitlHarness.cs`（Editor-menu spawn・3 demo window + title-drag move/raise + × hide + Mutate Demo +
  #13 pan/zoom 再利用 + Save/Load）/ `Assets/Editor/FloatingWindowHitlMenu.cs`（`Tools > Backcast > Floating Window HITL`）/
  `Assets/Editor/FloatingWindowProbe.cs`（6 セクション AFK ゲート）。

ゲート（VERBATIM, `UNITY_EXIT=0`, CS エラー 0, 自ファイル警告 0）:
```
[FLOATING WINDOW PASS] drag->logical arithmetic (viewportDelta/zoom, resolution-independent, zoom<=0 guard) + z-order normalize (non-contiguous/duplicate/negative -> stable contiguous 0..n-1) + real-RectTransform placement + child-follow through IDENTITY FloatingWindowLayer (engine==math) + MoveByLogical + z-order live application + BringToFront (SetAsLastSibling, engine==math) + non-vacuous disk round-trip (2 strategy_editor + order, non-default size/pos, non-contiguous z, visible=false; on-disk text proof, fresh load + restore) + back-compat (old sidecar -> empty) + sanitize (dup-id first-wins, non-finite/<=0 size drop, x/y->0, unknown-kind preserved+spawn-skipped, spec-min clamp) + malformed -> default (Unity-owned versioned schema, additive capability surface, ADR-0003 capability parity, under Unity Mono)
```

**#12/#13/#14 回帰（S7・各 probe を個別実行）**: 全て `UNITY_EXIT=0` で GREEN 継続 —
- `ReplayLayoutProbe`: `[REPLAY LAYOUT PASS] ... Default()==live-default panel layout ...`
- `InfiniteCanvasProbe`: `[INFINITE CANVAS PASS] ... real-RectTransform child-follow (engine==math) ...`
- `HakoniwaProbe`: `[HAKONIWA PASS] ... non-vacuous slot disk round-trip ...`

非空転の証跡: S5 が 2 strategy_editor + order（非 default size/pos・非連続 z `{5,2,99}`・region_002 は `visible=false`）→ save →
**新規 instance** load → `loaded==mutated`（zOrder verbatim）∧ `loaded!=default` ∧ on-disk JSON テキストに
`"id":"strategy_editor:region_001"` / `"kind":"order"` / `"zOrder":99` / `"w":520.5` / `"visible":false` が実在、を構造 assert
（vacuous round-trip kill）→ fresh controller へ Apply で sibling 順 `{region_002, region_001, order}`・geometry・visibility 復元。

**HITL（owner 目視）GREEN（2026-06-13）**: `Tools > Backcast > Floating Window HITL` を Play 中に spawn し §7 HITL gate の
全項目を確認 — ① 3 spec-driven window 表示（Strategy Editor ×2 + Order・chart 無し）／② title-drag で自由移動（AC1）／
③ 背面 window の title クリックで最前面化・**本体クリックでは前面化しない**（AC1 z-order・body は pan へ fall-through）／
④ 背景/本体 drag・wheel で全 window が pan/zoom 追従・HUD バー不動（AC2 追従・chrome 固定）／⑤ `×` で hide（visible=false）／
⑥ Save→Load の round-trip。本番入力経路（`InputSystemUIInputModule → FloatingWindowTitleInput → FloatingWindowController` と
#13 pan/zoom 経路の併存）が実機で機能することを実証。**実 disk round-trip 検証**: `Save` で
`persistentDataPath/floating_window_hitl.json` に 3 window（`strategy_editor:region_001/region_002` + `order`・各 id/kind/
x,y/w,h/zOrder 0..2/visible・`version:1`・`panels:[]`・identity canvasView 共存）が正しく serialize され、`Load` で
`loaded 3 windows` を確認（本番 `LayoutStore`+`JsonUtility` 経路）。これで **AFK ゲート ＋ HITL 目視の両方 GREEN ＝ #15 の
release gate 完全達成**。
（HITL ログ上の注記: owner は Save→Mutate Demo→Load の順で操作したため Load は保存時の初期サイズへ忠実復元した。非 default
size の persist 自体は AFK probe S5 が 520.5×380.5 で権威証明済み。）
