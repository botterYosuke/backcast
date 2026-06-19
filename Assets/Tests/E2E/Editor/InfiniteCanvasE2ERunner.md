# InfiniteCanvasE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`InfiniteCanvasE2ERunner.cs`（第二波・実装済み）が自動検証する **infinite canvas サーフェス**の台本。実装者は `.cs` と
本 `.md` をセットで読む。これは調査メモではなく、**このサーフェスでユーザーができる行動すべての網羅台帳と、
E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E*（1サーフェスでユーザーができる操作を網羅する回帰ゲート）。
> 「pan/zoom が保存され File→Open で復元される」等の横断ストーリーは *Journey E2E* が担い、本 Surface 台本は
> 「drag/wheel が正しい Content 変換（pan=anchoredPosition／zoom=localScale）と pure-math 委譲を起こすか」までを観測する。

## 対象サーフェス

chart / status tile（Hakoniwa）/ floating window が乗る、無限スクロール・ズーム可能な**同じ空間**の土台
（`InfiniteCanvasController` ＋ 入力境界 `InfiniteCanvasInputSurface`）。uGUI 実現は **固定 Viewport ＋ 単一 Content
transform**：**pan = Content の canvas 論理座標移動（anchoredPosition）**、**zoom = Content scale（localScale・カーソル
中心）**。canvas 上の widget は Content の子なので pan/zoom に自動追従し、**screen-fixed chrome（menu/sidebar/footer/
modal）は Content の外**で追従しない。Bevy 同等機能の capability parity（ADR-0003・形式非互換）。コントローラは
stateless（毎回 Content transform から view を読み戻す）で、算術は pure `CanvasViewMath`、永続値は `LayoutDocument`
の `canvasView`（panel `LayoutRect` とは独立した additive フィールド・findings 0004 §10/0006）。

## 対象ユーザー行動

drag で pan、wheel/scroll で zoom（カーソル中心）、zoom の clamp、カーソル下の論理点を固定する不変条件、
1 イベントあたりの scroll 量 clamp、canvas 上 widget の追従（chrome は非追従）、pan/zoom の永続化（disk round-trip・
File→Save 連動）、旧 doc / 異常値の back-compat・sanitize。pan/zoom の実感（実マウス・実ホイール・GPU）は HITL。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| CANVAS-01 | drag で canvas を pan（掴んで移動） | `InfiniteCanvasInputSurface.cs:48,56`（`OnDrag`）→`InfiniteCanvasController.cs:48`（`PanByScreenDelta`） | logical move = `-dScreen/zoom`（zoom 非依存の論理単位）、Content の anchoredPosition が更新、zoom 不変 | 反射で `PanByScreenDelta` を駆動し pan 算術（resolution-independent）と anchoredPosition を assert | 自動(E2E済) | `InfiniteCanvasE2ERunner`（S1/S4） |
| CANVAS-02 | wheel/scroll で zoom（カーソル中心） | `InfiniteCanvasInputSurface.cs:59,69`（`OnScroll`）→`InfiniteCanvasController.cs:54`（`ZoomAtCursor`） | factor=`1.1^ticks`、Content の localScale が更新、カーソル位置を中心に拡縮 | 反射で `ZoomAtCursor` を駆動し localScale と cursor-centred 結果を assert | 自動(E2E済) | `InfiniteCanvasE2ERunner`（S3/S4） |
| CANVAS-03 | 端まで zoom（範囲 clamp） | `CanvasViewMath.ZoomAtCursor`（`MIN_ZOOM`/`MAX_ZOOM`） | zoom は `[0.2, 5.0]` に飽和（overshoot 無し）、範囲内ステップは無改変 | clamp 値（4×4→5.0／0.3×0.1→0.2／範囲内 1.5×1.4→2.1）を assert | 自動(E2E済) | `InfiniteCanvasE2ERunner`（S2） |
| CANVAS-04 | カーソル下の点を固定したまま拡縮（不変条件） | `CanvasViewMath.LogicalUnderCursor`／`ZoomAtCursor` | zoom 前後でカーソル下の論理点が不変（clamp ステップでも CLAMPED newZoom で成立・両ステップ非 no-op） | 通常＋clamp ステップで `LogicalUnderCursor` の before/after 一致を assert | 自動(E2E済) | `InfiniteCanvasE2ERunner`（S3） |
| CANVAS-05 | 1 ホイールイベントの過大 scroll を抑える（tick clamp） | `InfiniteCanvasInputSurface.cs:66`（`Mathf.Clamp(scrollDelta.y,±MAX_SCROLL_TICKS)`） | `scrollDelta.y` を ±4 notch に clamp（raw wheel ~120 が 1 notch で zoom 端へ飛ばない） | input surface の tick clamp を `PointerEventData` 注入で駆動し factor 上限を assert（in-range scroll の liveness 兼用で vacuous-green kill） | 自動(E2E済) | `InfiniteCanvasE2ERunner`（S8） |
| CANVAS-06 | canvas 上の widget が pan/zoom に追従（chrome は非追従） | `InfiniteCanvasController.cs:39`（`ApplyView`）＋Content の子配線 | 子 widget は Content の TransformPoint で `CanvasViewMath.LogicalToViewport` 通りに追従、Viewport-centre coords で engine==math | 実 RectTransform 親子ツリーで Unity の transform 合成と pure-math を突き合わせ（非自明 cross-check） | 自動(E2E済) | `InfiniteCanvasE2ERunner`（S4） |
| CANVAS-07 | pan/zoom が保存・復元される | `InfiniteCanvasController.cs:33`（`CaptureView`）／`LayoutDocument.canvasView` | `canvasView`（panX/panY/zoom）が `Save`→disk→`Load` で round-trip（on-disk text に `"panX"`/`"zoom"` が出る・panel rect とは別フィールド） | 一時パスへ save→load で構造一致＋vacuous-green kill を assert（File→Save 連動は Journey/MenuBar 側） | 自動(E2E済) | `InfiniteCanvasE2ERunner`（S5） |
| CANVAS-08 | 旧 doc / 異常値を安全に読む（back-compat・sanitize） | `LayoutStore.LoadFromJson`／`NormalizeCanvasView` | 旧 v1（canvasView 無し）→identity、zoom 0→1（pan 保持）、zoom 99→5.0、非有限→identity、破損 JSON→document default | 各ケースを `LoadFromJson`/`NormalizeCanvasView` で assert | 自動(E2E済) | `InfiniteCanvasE2ERunner`（S6） |
| CANVAS-09 | pan/zoom の実感（実マウス drag・実ホイール・経路分離） | `InfiniteCanvasInputSurface.cs:74`（`ScreenToViewportCentered`）／`OnDrag`/`OnScroll` | 実ポインタ drag で全体が滑らかに pan、ホイールでカーソル中心に zoom、chrome は画面固定、Hakoniwa tile body・floating window body の drag が canvas pan に落ちる経路 | — | HITL専用（実ピクセル＋実マウス/ホイール＋EventSystem raycast・GPU/実ウィンドウ前提） | `InfiniteCanvasHitlMenu` |

> pan の永続値は **canvas 論理座標**（画面ピクセルでも zoom 後ピクセルでもない）。zoom 値は Unity ネイティブ
> `localScale` の意味で持つ（TTWR `OrthographicProjection.scale` とは逆向き・数値互換にしない＝capability parity）。

## 観測点（詳細）

- **CANVAS-01〜04/06（pan/zoom 算術・追従）**: `InfiniteCanvasE2ERunner` が正本で **三方向 cross-check**（pure
  `CanvasViewMath` × Unity の `RectTransform.TransformPoint` × `LayoutStore` 直列化）。S4 の child-follow は
  「Unity 自身の transform 合成 == math の予測」を assert する非自明セクション。
- **CANVAS-05（tick clamp）**: `InfiniteCanvasInputSurface.OnScroll` の `MAX_SCROLL_TICKS=4` clamp は input 境界の
  ロジックで、controller を直叩きする S1〜S7 の射程外。新規 **S8** が実 MonoBehaviour（viewport に attach し
  `Initialize(controller, viewport)`）へ `PointerEventData.scrollDelta` を注入し、raw wheel ~120 が 4 notch
  （factor `1.1^4`）に capped され zoom が MAX_ZOOM へ飽和しないことを assert。同 section は in-range scroll（2 notch
  →`1.1^2`）の liveness check を先に置き、surface が unwired だと false-green になる穴を塞ぐ（vacuous-green kill）。
- **CANVAS-07（永続化）**: `InfiniteCanvasE2ERunner` S5 が on-disk TEXT 証明（`"panX":123.25` 等）＋fresh load で
  vacuous-green を kill。File→Save の発火・sidecar 統合は MenuBar / Journey 側の責務。
- **CANVAS-08（sanitize）**: S6 が旧 v1・zoom 0/99・非有限・破損 JSON の正規化を網羅。
- **（参考）S7 = parallax foreground layer**: 操作一覧表に対応 Action 行は無い depth-cue 拡張（foreground が base
  より MORE 移動する engine==math cross-check）。実証済み assert なので回帰網保全のため温存。

## 自動判定（合格条件）

- ログに `[E2E INFINITE CANVAS PASS] <要約>`、プロセス exit code 0（`-quit` 併用・self-failing gate）、`error CS\d+` が 0 件。
- 各 `自動(*)` 行の観測点を 1 つでも落としたら `[E2E INFINITE CANVAS FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus: `CanvasViewMath.PanByScreenDelta` の `/zoom` 項・`ZoomAtCursor` の cursor 補正・
  `NormalizeCanvasView` の clamp を消すと、対応する assert が必ず落ちること。

## カバー状態の語彙

[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) の 5 値（`自動(E2E済)` / `自動(Probe有・要昇格)` / `要新規自動化` /
`HITL専用` / `対象外`）に従う。`HITL専用` と `対象外` は理由を併記する。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `InfiniteCanvasE2ERunner`（旧 `InfiniteCanvasProbe` を昇格・改名） | batchmode・pure＋実 RectTransform＋直列化＋input 境界 | CANVAS-01/02/03/04/05/06/07/08 の正本。S1–S7 移送＋S8（CANVAS-05）追加 |
| `InfiniteCanvasHitlMenu` | HITL ハーネス | CANVAS-09 の実感・経路確認用に**探索 Probe として残す**（名称据え置き） |

## `InfiniteCanvasE2ERunner.cs` 実装方針（第二波・実装済み）

section ↔ Action ID は **(B) 自然な検証単位 ＋ `Covers:`** に従う（共有 pure 算術 `CanvasViewMath` を Action ID ごとに
人工分割しない。規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)）。各 section header の `// Covers: CANVAS-xx` で台本
の操作一覧表と双方向に追える。gate 形は昇格元 probe の `Execute()`-形（各 section が null=PASS、最初の失敗文字列を返す
`?? チェーン`）を温存。`EditorApplication.Exit` は self-failing gate として無条件化。

- 昇格元同型に **headless な Viewport→Content→child の RectTransform ツリー**を組み、`InfiniteCanvasController` を pure に
  駆動（実 `BackcastWorkspaceRoot` 合成は不要・Python-FREE）。child-follow は Viewport-centre coords で engine==math を cross-check（S4）。
- CANVAS-05（S8・新規）は `InfiniteCanvasInputSurface` を viewport へ attach し `Initialize(controller, viewport)`、`OnScroll`
  に `PointerEventData`（`scrollDelta.y`±120）を渡して tick clamp 後の factor 上限を assert。先頭で in-range scroll（2 notch）の
  liveness を確認し、surface が unwired のとき clamp assert が false-green にならないよう塞ぐ。
- disk round-trip（CANVAS-07/08）は production sidecar を汚さない一時パスへ。teardown は spawned GameObject の
  `DestroyImmediate` ＋ 一時 dir 削除。
- 実行コマンド: `<Unity> -batchmode -nographics -quit -projectPath . -executeMethod InfiniteCanvasE2ERunner.Run -logFile <log>`。
  compile-only ゲートは `-executeMethod` を外した同コマンド（`error CS\d+` 0 件）。Unity ログは UTF-8 なので
  **`grep -a "E2E INFINITE CANVAS" <log>`（ripgrep/Select-String は `→` 含む行を取りこぼす）**。
