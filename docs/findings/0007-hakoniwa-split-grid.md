# Hakoniwa split-grid Findings: chart+status tile を infinite canvas に載せる split-grid サーフェス（capability parity）

- Issue: #14 (S6 UI shell — Hakoniwa split-grid: tile/swap/順序/永続化) — 親 #1 (Epic)・**#3 の外**
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)（accepted, self-protection 節あり）, [ADR-0001](../adr/0001-unity-pythonnet-embedded-frontend.md)（proposed）
- 配置の根拠: ADR-0003 self-protection 節（capability surface の具体項目など下位事実は ADR に書き戻さず本 findings に記録し ADR を「方針: ADR-0003」として参照）。本 findings は #14 で確定した下位事実の記録であり ADR 方針を変更しない。**新規 ADR も起こさない**（tile 順 persist は ADR-0003 が既にロックした capability surface の予約項目＝findings 0004 §10「Hakoniwa tile 順」、かつ #14 は #12 の既存 `slot` フィールドのみ使用しスキーマ追加 0）。
- 先行: #9（Replay tracer / 0001）, #10（Replay chart / 0002）, #11（Replay panels / 0003）, #12（Replay layout / 0004）, #13（infinite canvas / 0006）
- 実行環境（先行 slice と同一）: Intel x86_64 / macOS 13.7.8 / Unity 6000.4.11f1（standalone=Mono）/ uGUI 2.0 / Input System 1.19.0
- 設計確定: `grill-with-docs`（2026-06-13、owner インタビュー）。

> **状態: AFK ゲート GREEN（Mac leg, 2026-06-13）。** 設計は `grill-with-docs` で確定、直接実装（単一言語 C#・
> Python 非依存・仕様完全確定・小スコープ＝#12/#13 と同じ Unity スライス逸脱理由。CLAUDE.md 規約）。
> `HakoniwaProbe.Run` が batchmode で `exit=0`、CS エラー 0・自ファイル警告 0。#12/#13 回帰も GREEN。実装結果は §11。

---

## 0. スコープと段階づけ（owner 確定 2026-06-13）

#14 は **Hakoniwa split-grid**（infinite canvas Content 上の単一サーフェス）を durable に立て、tile の
**swap・順序変更**を可能にし、tile 順を **#12 スキーマの既存 `slot` フィールド**へ persist/restore する。
findings 0006 §0 が #14 の責務として予約した「chart + status 系を Content へ載せる」を満たす。

**TTWR 裏取り**（owner）: TTWR の現行 Hakoniwa（`src/ui/hakoniwa.rs`・ADR 0011/0014/0015/0016）は floating chart を
撤去し chart を Hakoniwa tile に統合済み。よって backcast でも chart は Hakoniwa の 1 tile（「chart は別 floating
window」という旧 ADR 0011 初版の前提は stale）。

### 採用 / 不採用

- **採用**: root+tile-children 構造（TTWR 同型）/ chart 込み 5 tile / 等分 `ceil(√n)` グリッド固定 / header-drag swap /
  `slot` のみで persist / AFK 権威ゲート + Python-free HITL demo tile。
- **不採用（= #14 外・将来 slice の additive 拡張）**:
  - **divider resize**（列幅/行高比率の変更・TTWR ADR 0015）→ cols/rows の永続化が要るため。将来は
    `HakoniwaGridLayout { cols, rows }` 等を `LayoutDocument` に additive 追加（slot のみ決定と整合）。
  - **box 移動 / root の canvas 位置・サイズ永続化**（TTWR drag-handle・ADR 0016 の box 共有）→ root の canvas 論理
    座標を表す専用 persist field が要るため。drag-handle 自体も #14 では付けない。
  - 実 Replay データ（Python / adapter / `ReplayPanelsHarness` / Replay lifecycle）の tile への統合。

## 1. tile セット・グリッド（owner 確定 2026-06-13）

初期 tile 集合（安定 id・#12 default schema = `LayoutDocument.cs` の 5 entry と機械一致）:

```
order(初期) = [chart, status, positions, orders, run_result]   # slot 0..4
grid        = ceil(√n) 等分 → n=5 → 3 列 × 2 行（row-major: 左→右／上→下）
6 番目 cell  = 空（tile なし）。grid は ceil(√n) を維持する。
```

- グリッド形状と各 tile rect は **n + slot 順から決定的に導出**（cols/rows フラクションは持たない＝等分固定）。
- chart も swap・順序変更・永続化の対象（他 tile と対等）。

## 2. 構造モデル（owner 確定 2026-06-13）

```
Canvas (ScreenSpaceOverlay)
└─ Viewport（固定・#13）
   └─ Content（pan=anchoredPosition / zoom=localScale・#13）
      └─ HakoniwaRoot（固定 canvas 論理位置・既定サイズ。grid 全体の矩形を所有）
         ├─ Tile[slot=0] … Tile[slot=4]   ← root-local 等分グリッドから配置
         └─（空き cell は tile なし）
```

- HakoniwaRoot は Content の子なので **pan/zoom に自動追従**（#13 の機序）。screen-fixed chrome は追従しない。
- root の drag-handle・box 移動・resize・その永続化は #14 では実装しない（§0 不採用）。
- **`PanelLayout.rect` を root 位置や split 比率の代用に流用しない**（slot が正本・rect は派生）。

## 3. persist 表現（owner 確定 2026-06-13）— スキーマ追加 0

- **正本は `PanelLayout.slot`**（#12 既存フィールド。「論理並び順専用」＝findings 0004 §3）。tile 順 = slot 順。
- `LayoutBinder.Apply` が #12 で **意図的に slot を transform へ適用しなかった**（findings 0004 §11「射程逸脱」）
  のは、まさにこの shell slice（#14）が slot の UI 解釈（grid slot 配置）を担うため。#14 がその約束を果たす。
- **rect は派生 snapshot として書く**: Capture 時に各 tile の実表示矩形（grid cell の 0..1 正規化）を
  `PanelLayout.rect` へ書き、doc が画面を忠実反映する（#12「`rect`=正規化表示矩形」契約と live UI を食い違わせない）。
  **restore 時は保存 rect を配置根拠にせず n+slot から再導出**（rect は再生可能・split 比率や自由配置の正本ではない）。
- **不正・重複・欠落 slot は正規化してから配置**（#12 の tolerance 規律を踏襲。§5 の Apply 規則）。

## 4. 配置算術と Apply/Capture（owner 確定 2026-06-13）

純算術（headless・AFK 権威。TTWR `split_grid_tile_rects` / `grid_dims` / `slot_at_local` の port）:

```
GridDims(n)            → (cols=ceil(√n), rows=ceil(n/cols))
CellRects(n)           → LayoutRect[n]（row-major・等分・0..1 正規化。各 cell = grid 区画）
SlotAt(cells, point)   → cell index | none（point は root-local 0..1）
Swap(order, from, to)  → order の 2 要素を入れ替え（from==to / 範囲外 は no-op）
```

- **Apply(doc, liveById)**: doc の panels を slot で正規化整列 → n cell を `CellRects` で生成 → slot i の tile に
  cell i の正規化 rect を **canonical anchor**（`anchorMin/Max = cell 角・offset=0`・#12 と同手法・解像度非依存）で
  設定。`visible` も適用。doc にあり live に無い id は skip／live にあり doc に無い id は default slot へ（§5）。
- **Capture(liveById)**: 各 tile の現 slot + 派生 cell rect + visible を `LayoutDocument.panels` へ書く。
- swap は order（= slot）の入れ替えのみ。gesture 検出（drag）と state 変更（swap→Apply）を分離し、headless 回帰は
  pointer を模さず `Swap` + `Apply` を直接呼んで検証（TTWR ADR 0014 と同方針）。

## 5. tolerance / 正規化規則（owner 確定 2026-06-13）

restore 時に disk 由来の slot 群から **canonical 順**を導く（#12 の forward-evolution 寛容と整合）:
- slot 昇順で整列、同値は id で安定 tie-break。
- 範囲外 / 負 / 重複 slot は整列後の連番に正規化（panic しない）。
- doc に無い live id は末尾へ append（既定順を保つ）。
- 欠落・破損・全 default → 既定 order `[chart,status,positions,orders,run_result]`（#12 `LayoutStore` の fail-soft fallback を再利用）。

## 6. 入力（header-drag swap）— owner 確定 2026-06-13

- tile ヘッダ帯に薄い durable MonoBehaviour（`IBeginDragHandler`/`IDragHandler`/`IEndDragHandler`）。DragStart で
  source slot + grab 点を記録、Drag で累積、DragEnd で drop 点（root-local 0..1）を `SlotAt` で hit-test し
  target≠source なら controller の `Swap(from,to)`→`Apply`（TTWR ADR 0014 の drop=実グラブ点基準を踏襲）。
- pan/zoom（#13 の `InfiniteCanvasInputSurface`・背景 drag/scroll）とは **別 drag target**。uGUI イベントは
  topmost handler（tile ヘッダ）で消費されるので、ヘッダ drag が canvas pan に化けない。
- **input-reading は throwaway にしない**（durable。#13 の規律と同じ — 後続 shell slice が同じ surface を再利用）。

## 7. durable / throwaway 構成（owner 確定 2026-06-13）

durable **`Assets/Scripts/Hakoniwa/`**（`Canvas/` 同様 `UnityEngine` 衝突を避けた命名）:

| 型 | 役割 | 層 |
|---|---|---|
| `HakoniwaGridMath` | §4 の純 float 算術（`GridDims`/`CellRects`/`SlotAt`/`Swap`）・**AFK 権威**・playmode 非依存 | pure core |
| `HakoniwaController` | input-agnostic plain class。tile id→RectTransform map と order を保持。`Apply`/`Capture`/`Swap`/`Rebuild`。`HakoniwaGridMath` を呼び RectTransform を読み書き（`InfiniteCanvasController` と同方針） | Unity boundary |
| `HakoniwaTileHeaderInput` | 薄い durable MonoBehaviour。ヘッダ drag → `SlotAt` hit-test → `controller.Swap` | input boundary |

persist は #12 の `LayoutDocument` / `LayoutStore` / `LayoutPathResolver` を**そのまま再利用**（スキーマ追加 0）。
`HakoniwaController` の Capture/Apply は grid tile 専用で、#11 の anchor-panel 用 `LayoutBinder` とは別レイヤ（同じ
`LayoutDocument.panels` へ書くが、Hakoniwa 文脈ではこの 5 id を Hakoniwa が所有するので衝突しない）。

throwaway:
- `HakoniwaHitlHarness`（MonoBehaviour）: infinite canvas（#13 の surface/controller を再利用）+ HakoniwaRoot +
  5 枚の **ラベル付き demo tile**（chart/status/positions/orders/run_result・本番と同じ安定 id）+ Save/Load **のみ**。
  入力処理は複製しない。**Editor menu `Tools > Backcast > Hakoniwa HITL` で Play 中に明示 spawn**（auto-bootstrap 無し・
  Python 非依存・`ReplayPanelsHarness.AutoBootstrapEnabled` に触れない → single-Play-owner 衝突回避、findings 0003 §8）。
- `Assets/Editor/HakoniwaHitlMenu.cs`（`Tools > Backcast > Hakoniwa HITL`）。
- `Assets/Editor/HakoniwaProbe.cs`（`-executeMethod HakoniwaProbe.Run`・AFK 回帰ゲート）。

## 8. ゲート（owner 確定 2026-06-13）— AFK 権威 + HITL 入力検証

### AFK probe セクション（純 math ⟂ Unity transform engine ⟂ serialization の三者独立 cross-check）

1. **grid 算術**: `GridDims(5)=(3,2)`。`CellRects(5)` が row-major・等分・0..1 を被覆（6 番目 cell は空）・非重複。
2. **SlotAt hit-test**: cell i 内の点 → i、cell 外 → none。
3. **swap reorder（engine==math 非トートロジー）**: `Swap` 後 `Apply` で tile が新 cell へ。Unity の RectTransform に
   設定した anchor を読み戻し `CellRects` 予測と一致（同実装を両辺で呼ぶ偽緑を避ける）。
4. **非空転 slot disk round-trip（vacuous-green kill）**: swap（例 chart↔run_result）→ Capture → **slot 値が default と
   非一致** → save → **新規 instance** load → slot 順生存 → Apply で swap 後 cell 配置 ∧ `loaded!=default` ∧
   **disk JSON テキストに変更後 slot 値が実在**。
5. **invalid op no-op / tolerance**: `Swap(i,i)`・範囲外は order 不変。重複/範囲外/欠落 slot を §5 規則で正規化して配置。
6. **back-compat + malformed fallback**: 旧 doc の未知 id skip・live のみ id は default slot・欠落/破損 → 既定 order（`LayoutStore` 規律）。
7. **#12/#13 回帰**: `ReplayLayoutProbe` / `InfiniteCanvasProbe` が GREEN 継続（#14 はスキーマ非改変なので無傷の想定を実証）。

### HITL gate（owner 起動・menu spawn）

5 tile が 3×2（1 空き）で表示／ヘッダ drag で他 tile へ swap（drop=実グラブ点基準）／canvas を pan/zoom すると
Hakoniwa が追従・chrome 不動／Save→さらに swap→Load で swap 後の順序が復元。AC の入力配線は HITL 検証
（headless で全 AC 証明とは over-claim しない）。

## 9. 射程外（#14 に含めない）

- divider resize（cols/rows 比率・ADR 0015 parity）／box 移動・root canvas 位置/サイズの永続化（ADR 0016 box 共有）。
- 実 Replay データ（Python/adapter/`ReplayPanelsHarness`/lifecycle）の tile 統合（恒久シェル統合は別 slice）。
- per-mode プロファイル（TTWR ADR 0016 の Replay/Live 2 プロファイル）／per-workspace／multi-instrument／Windows leg。
- chart N 枚（銘柄別）への拡張（現行 capability surface は chart 1 枚）。

## 10. AC 達成方針（実装後に証跡を追記）

- **AC1（grid 分割して複数 tile に panel 配置）**: AFK = §8 S1（`GridDims`/`CellRects`）+ S3（Apply で tile が cell へ）。
  HITL = 5 demo tile が 3×2 で表示。
- **AC2（tile swap・順序変更）**: AFK = §8 S3/S5（`Swap`→`Apply`・engine==math・invalid no-op）。HITL = ヘッダ drag swap。
- **AC3（#12 スキーマへ persist/restore）**: AFK = §8 S4（非空転 slot disk round-trip・on-disk テキストに値実在）+ S6（fallback）。

## 11. 実装結果（ゲートログ・Mac leg, 2026-06-13）

durable 3 ファイル（`Assets/Scripts/Hakoniwa/`）+ throwaway 3 ファイルを直接実装（pair-relay/parallel は使わず —
#12/#13 と同じ Unity スライス逸脱理由）。スキーマは **非改変**（#12 既存 `slot` のみ使用）。

成果物（全て新規・§7 通り）:
- **durable**: `HakoniwaGridMath.cs`（純算術 `GridDims`/`CellRects`/`SlotAt`・AFK 権威）/ `HakoniwaController.cs`
  （input-agnostic plain class・order↔slot・`Capture`/`Apply`/`Swap`/`Rebuild`・slot 正本/rect 派生・tolerance）/
  `HakoniwaTileHeaderInput.cs`（薄い durable MonoBehaviour・header drag → `SlotAt` hit-test → `Swap`・source slot は
  drag END で id から動的引き）。
- **throwaway**: `HakoniwaHitlHarness.cs`（Editor-menu spawn・5 demo tile + header-drag swap + #13 pan/zoom 再利用 +
  Save/Load）/ `Assets/Editor/HakoniwaHitlMenu.cs`（`Tools > Backcast > Hakoniwa HITL`）/
  `Assets/Editor/HakoniwaProbe.cs`（6 セクション AFK ゲート）。

ゲート（VERBATIM, `UNITY_EXIT=0`, CS エラー 0, 自ファイル警告 0）:
```
[HAKONIWA PASS] grid arithmetic (ceil(√n) 5->3x2, equal/cover/non-overlap, empty 6th) + SlotAt hit-test + controller order->cell reorder (real RectTransform) + Capture/Apply boundary + non-vacuous slot disk round-trip (on-disk text proof, fresh load) + invalid-op no-op + duplicate/out-of-range slot tolerance + back-compat/missing-id + corrupt/missing fallback (Unity-owned versioned schema, slot-only persist, ADR-0003 capability parity, under Unity Mono)
```

**#12/#13 回帰**: スキーマ非改変（新規ファイルのみ追加）だが、ゲート規律に従い再確認 — `ReplayLayoutProbe` /
`InfiniteCanvasProbe` ともに `UNITY_EXIT=0` で GREEN 継続。

非空転の証跡: S4 が swap（chart↔run_result）→ Capture → save → **新規 instance** load → slot 順生存 ∧ `loaded!=baseline`
∧ on-disk JSON テキストに `"id":"run_result","slot":0` / `"id":"chart","slot":4` が実在、を構造 assert（vacuous round-trip kill）。

**HITL（owner 目視）GREEN（2026-06-13）**: `Tools > Backcast > Hakoniwa HITL` を Play 中に spawn し、§8 HITL gate の
全項目を確認 — ① 5 tile が 3×2 で表示・右下 6 番目 cell は空（AC1）／② header-drag で 2 tile が swap・HUD の order 表示が
更新（AC2）／③ 背景/本体 drag で Hakoniwa が pan・wheel で zoom 追従し HUD バーは不動（AC2 追従・chrome 固定）／
④ Save→追加 swap→Load で保存時の order に正確復元（AC3）。本番入力経路
（`InputSystemUIInputModule → HakoniwaTileHeaderInput → HakoniwaController` と #13 の pan/zoom 経路の併存）が実機で
機能することを実証。これで **AFK ゲート ＋ HITL 目視の両方 GREEN ＝ #14 の release gate 完全達成**。
