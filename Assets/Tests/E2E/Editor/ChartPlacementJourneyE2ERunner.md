# ChartPlacementJourneyE2ERunner — 台本（chart 配置の Journey E2E 仕様 / 観測点 / 合格条件）

`ChartPlacementJourneyE2ERunner.cs` が自動検証する **issue #114 (chart grid placement & cascade kill)**
の Journey E2E 台本。実装者は `.cs` と本 `.md` をセットで読む。設計の木の正本は
[`docs/findings/0091-issue114-chart-grid-placement-and-cascade-kill.md`](../../../../docs/findings/0091-issue114-chart-grid-placement-and-cascade-kill.md)
（F0–F6 + 3 note）。Action ID 採番・カバー状態の語彙・責務境界の共通規約は
[E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名・配置の上位規約は
[ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Journey E2E*（複数サーフェスをまたぐ実ユーザーストーリー）。
> chart window の配置データが「universe SoT → `RestoreFloating` / `SyncChartWindowsToUniverse` → live
> chart window の rect」へ流れて、(a) saved 位置を honor し、(b) saved 無しは grid で整列し、(c)
> cascade staircase を作らない、の 3 つの不変条件を実 `BackcastWorkspaceRoot` で観測する。
> 各次元の純データ round-trip は既存 [`LayoutPersistenceJourneyE2ERunner`](./LayoutPersistenceJourneyE2ERunner.md)
> が AFK 権威。本 Journey はそれを chart 配置の特性化（cascade kill / grid 整列 / sad-path 全部）に縫う。

## 対象ストーリー（不変条件の 1 文版）

> **戦略 .json を File→Open したとき、saved 位置のある chart は saved 通り honor、saved 位置の無い chart は
> base cluster 下方の non-overlapping grid に整列し、cascade staircase を作らない。**

15 pattern (P1–P15) の expectation matrix と判断の根拠は findings 0091 に固定。本台本は 19 probe の操作一覧表。

## アーキテクチャ前提

- chart window は `_dockWindows`（back-plane / 1.0× layer / ADR-0018）の `KIND_CHART`、id 規約は `chart:<iid>`
  （`DockShape.ChartId`）。defaultSize = (520, 360)（`FloatingWindowCatalog.cs:83`）。
- spawn 経路は **2 つだけ**: (a) `RestoreFloating` (`BackcastWorkspaceRoot.cs:2103`) が `floatingWindows`
  にある saved chart を `ApplyGeometry`/`Spawn` する、(b) `SyncChartWindowsToUniverse`
  (`BackcastWorkspaceRoot.cs:922`) が universe にあるが live で missing な chart を spawn する。
- 本 issue 修正前は (b) が 1 個ずつ `SpawnDockedToFocus` で focus snap → cascade staircase。修正後は
  `ChartGridPlacement.AllocateNonOverlappingTopLefts(1, ceil(√universe.Count), …)` で 1 件ずつ次スロットへ。
- pure helper `ChartGridPlacement`（新規 `Assets/Scripts/FloatingWindow/ChartGridPlacement.cs`）は canvas-LOGICAL
  座標で grid セルを返す。viewport を一切知らない（findings 0091 F6 の β: canvas-bound clamp 採用、viewport 無依存）。
- legacy sidecar の `panels` / `hakoniwaProfiles` は ADR-0017 § 6 / `ApplyLayout` docstring (L2083) で IGNORE 既定。
  read 側 promote は しない (findings 0091 F3-P12)。write 側は `CaptureLayout` (L2055) が常に `panels = []` を書く。
- Python-FREE（chart placement は kernel / market data を要さない）。`FakeMarimoSynthesizer` で `BuildWorkspace`。

## 操作一覧表（網羅台帳）

| Action ID | ステップ/行動 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 関連Surface台本 |
|---|---|---|---|---|---|---|
| CP-S0-01 | `ComputeFlushSlots(n)` の rect 数学 (n=0/1/4/9/52) | `ChartGridPlacement.cs:ComputeFlushSlots` | slot k = (col=k%cols, row=k/cols)、cols=ceil(√n)、row 0 最上段 | 直接呼んで返値 rect 列を assert | 自動(E2E済・Section0) | — (pure helper) |
| CP-S0-02 | `AllocateNonOverlappingTopLefts(1, gridCols=8, avoid=full row 0)` | `ChartGridPlacement.cs:AllocateNonOverlappingTopLefts` | row 0 全 8 slot を avoid で塞いだら row 1 col 0 へ spill | 返値 = (anchor.x, anchor.y-(chartH+gap)) | 自動(E2E済・Section0) | — (pure helper) |
| CP-S0-03 | `AllocateNonOverlappingTopLefts(N, gridCols=ceil(√N), avoid=[])` ≡ `ComputeFlushSlots(N)` | 同上 | avoid 無しでは 2 関数が同じ slot 列を生む | 各 i で `Approx2(allocFree[i], flushFree[i].topLeft)` | 自動(E2E済・Section0) | — (pure helper) |
| CP-S0-04 | 半重なり avoid で該当 slot を skip し N 個返す | 同上 | slot 0 を 1px overlap → 返値先頭が slot 1 | 返値先頭 ≠ slot 0、= slot 1 | 自動(E2E済・Section0) | — (pure helper) |
| CP-S1-01 (**P3**) | full saved (5 chart 全 saved) → 全 honor | `BackcastWorkspaceRoot.cs:RestoreFloating` / `ApplyLayout` | 5 chart 全部 saved x/y、grid placement は 0 件発火 | 各 chart の `RectOf(id).anchoredPosition == saved` | 要新規自動化(S1 slice) | [LayoutPersistenceJourney](./LayoutPersistenceJourneyE2ERunner.md) JOURNEY-LAYOUT-12 |
| CP-S1-02 (**P4**) | partial saved (3 of 5 saved) → 3 honor + 2 grid | 同上 + `SyncChartWindowsToUniverse` | 3 saved は honor、2 unsaved は grid `cols=ceil(√5)=3`、重なり 0 | saved 3 個 = saved x/y、unsaved 2 個 = grid slot、ペア 0 overlap | 要新規自動化(S1 slice) | 同上 |
| CP-S1-03 (**P9**) | saved off-screen x=-9999 → canvas-bound clamp ±4000 | `RestoreFloating` 内 clamp 経路（実装後） | x が `[-4000, +4000]` に clamp、entry 自体は保持 | clamp 後 x ≤ -4000+ε、CaptureLayout で entry 残存 | 要新規自動化(S1 slice) | — |
| CP-S1-04 (**P10**) | 2 saved 同座標 → JSON 配列順で先勝、後発 grid 退避 | `RestoreFloating` collision de-collide | 配列 [0] は saved x/y、配列 [1] は grid 次スロット | [0]=saved、[1]=grid、両者重なり 0 | 要新規自動化(S1 slice) | — |
| CP-S1-05 (**P13**) | saved w=0/h=-1 → default size 差戻、x/y は honor | `RestoreFloating` 内 invalid w/h guard | size = (520, 360) に差戻、位置は saved | RectOf(id).sizeDelta == (520,360)、anchoredPosition == saved | 要新規自動化(S1 slice) | — |
| CP-S2-01 (**P2**) | legacy `panels` 形 (`v19_morning_cell.json` 同型) → cascade ゼロ、52 chart 全部 grid 整列 | `OnFileOpen` → `ApplyLayout` (panels ignore) → `SyncChartWindowsToUniverse` 置換 | live 52 chart 全部 `cols=8` grid 上に整列、互いに非 overlapping | 全 chart の rect を取って互いに `Overlaps` false、各 chart x ≤ +3124 (sanity bound) | 要新規自動化(S2 slice・**bug fix gate**) | [LayoutPersistenceJourney](./LayoutPersistenceJourneyE2ERunner.md) |
| CP-S2-02a | legacy open → Save → reread で `panels=[]` | `OnFileOpen` → `OnFileSave` → `LayoutSidecarStore.TryReadLayout` | 次 Save で legacy panels は物理 0 件化（migration 完了の唯一 signal） | reread した doc.panels.Count == 0、doc.floatingWindows.Count == 52 (grid 配置の chart 群) | 要新規自動化(S2 slice) | — |
| CP-S2-02b | legacy open → drag 1 件 → Save → reread で `floatingWindows` に drag 後 saved x/y | `RectOf(id)`/`MoveByLogical` → `OnFileSave` | drag した chart の x/y が persist | reread doc.FindWindow(draggedId) の x/y ≈ drag 後座標 | 要新規自動化(S2 slice) | — |
| CP-S3-01 (**P8**) | corrupted JSON (truncated) → `.bak` 退避 + grid fallback | `LayoutSidecarStore.TryReadLayout` (false) → ApplyLayout skip → `SyncChartWindowsToUniverse` で grid | `.bak` 生成、起動 non-blocking、universe 5 chart は grid 配置 | `File.Exists(path + ".bak")` && live chart 5 件 grid、`Exit(0)` 経路 | 要新規自動化(S3 slice) | — |
| CP-S3-02 (**P11**) | ghost symbol (saved に居て universe 不在) → spawn 0、`CaptureLayout` で entry 保持 | `RestoreFloating` (universe-membership filter) + `CaptureLayout` (ghost retention) | spawn skip、capture で entry 出現 (TTL 無し永遠保持・findings 0091 F3-P11) | live `_dockWindows.Has(ghostId)` == false、`CaptureLayout().FindWindow(ghostId)` != null | 要新規自動化(S3 slice) | — |
| CP-S3-03 (**P14**) | 同 iid x 2 entry → 先頭採用、`CaptureLayout` で de-dup | `RestoreFloating` 重複 guard + `CaptureLayout` de-dup | 2 件目以降は spawn 試行で no-op、capture は 1 件 | live `_dockWindows.Has(id)` == true、`CaptureLayout().floatingWindows` 内同 id 1 件のみ | 要新規自動化(S3 slice) | — |
| CP-S3-04 (**P15**) | viewport 極小 (canvas zoom-out 状態) → grid は canvas で計算、runaway scroll 不発 | `ChartGridPlacement` (viewport 不参照) + `SyncChartWindowsToUniverse` | grid 配置は canvas-LOGICAL 座標で deterministic、viewport 値に依存しない | viewport (`canvasView.zoom`) を 0.1 にして再計算しても chart の anchoredPosition は変わらず | 要新規自動化(S3 slice) | — |
| CP-S4-01 (**P1**) | no sidecar (`.py` のみ) → grid placement | `OnFileOpen` (layoutOk=false → ApplyLayout skip) → `SyncChartWindowsToUniverse` grid | 全 chart が grid、ペア 0 overlap | 同 CP-S2-01 と同型 | 要新規自動化(S4 slice) | — |
| CP-S4-02 (**P5**) | post-Open `_scenario.Universe.Add(iid)` → 1 件 grid 次スロット | `SyncChartWindowsToUniverse` (Changed event handler) | cascade 不発、grid 次スロットへ追加 | 既存 chart 群 (k 件) 後の追加 chart anchoredPosition == grid slot k | 要新規自動化(S4 slice) | — |
| CP-S4-03 (**P6**) | universe 52 件 → cols=8 で 7 row、x ≤ +3124 | `SyncChartWindowsToUniverse` grid | grid `cols=ceil(√52)=8`、x 上限 sanity bound | 全 chart x ∈ [-600, +3124]、ペア 0 overlap | 要新規自動化(S4 slice) | — |
| CP-S4-04 (**P7**) | empty universe (chart 0 件) → base cluster 健在、chart window 0 件 | `BuildWorkspace` + universe.Ids 空 | 5 base dock window のみ live、chart 0 件 | `_dockWindows` の chart kind 件数 == 0、base 5 件 live | 要新規自動化(S4 slice) | — |

> **probe ID 規約**: `CP-S{N}-{NN}[a/b]` (CP = Chart Placement、N = section index、NN = probe index 内 section、
> a/b = 同 ID の段分け)。既存 `JOURNEY-LAYOUT-NN` / `<SURFACE>-NN` と同型。
> **カバー状態**: 第 1 commit (S0) で「自動(E2E済・Section0)」のみ true、S1-S4 は実装後に「要新規自動化」→「自動(E2E済・SectionN)」へ更新。

## 自動検証する範囲（この Runner がゲートする）

- **S0**: pure helper `ChartGridPlacement` の rect 数学 4 不変条件 (列数 ceil(√n)、row-major、avoid skip、caller cols)。
- **S1**: `RestoreFloating` 経路の saved-honor 5 シナリオ (full / partial / off-screen clamp / collision de-collide / invalid w/h)。
- **S2**: legacy `panels` 形 sidecar の **cascade kill (bug fix の本丸)** + migration cycle 2 段。
- **S3**: sad-path 4 シナリオ (corrupted .bak / ghost retention / dedup / viewport 無依存)。
- **S4**: 回帰 4 シナリオ (no-sidecar / universe-grow / 52-chart / empty-universe)。

## 自動検証しない範囲

- **実ピクセルの見た目**（chart 描画位置の美観・色・font）。`-batchmode -nographics` は GPU 無し、本 Runner は配置を
  rect データで観測する → **HITL専用**。
- **実 EventSystem 経由の drag/click**（title bar drag の screen→canvas 論理 delta）。本 Runner は controller API
  (`_dockWindows.ApplyGeometry` / `MoveByLogical` / `RectOf`) を直接駆動 → **HITL専用**。
- **実 monitor 解像度差** での viewport-relative clamp（F6 α）。canvas-relative β 採用のため probe では不要 →
  **対象外**（findings 0091 F6 で α 棄却）。

## RED→GREEN litmus (vacuity kill)

| Section | RED 取り方 |
|---|---|
| S0 | `ChartGridPlacement.AllocateNonOverlappingTopLefts` の `gridCols` を `Mathf.Max(1, gridCols)` の代わりに `Mathf.CeilToInt(Mathf.Sqrt(n))` で hard-code すると CP-S0-02 が「row 0 全部 avoid → col 0 縦積み」で RED |
| S1 | `RestoreFloating` の `ctrl.ApplyGeometry(w)` 呼びを削ると CP-S1-01 (P3) が「全 chart が default 位置 = grid 配置」で RED |
| S2 | `SyncChartWindowsToUniverse` を旧 cascade (`SpawnDockedToFocus` ループ) に戻すと CP-S2-01 (P2) が「chart が階段配置で互いに overlap」で RED |
| S3 | `CaptureLayout` が ghost entry を strip すると CP-S3-02 (P11) が「reopen 後 ghost が消える」で RED |
| S4 | `SyncChartWindowsToUniverse.Add` 経路を grid から `SpawnDockedToFocus` に戻すと CP-S4-02 (P5) が「追加 chart が cascade に巻き戻る」で RED |

## 再走手順 (Windows)

```powershell
# AFK 実走 (recompile 待ちで 2 回目以降が走る)
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
    -batchmode -nographics -quit `
    -projectPath C:\Users\sasai\Documents\backcast `
    -executeMethod ChartPlacementJourneyE2ERunner.Run `
    -logFile C:\Users\sasai\AppData\Local\Temp\chart_placement_e2e.log

# 結果確認 (Bash grep -a が ripgrep より確実 — `→` PASS 文字列を取りこぼさない)
bash -c "grep -a 'E2E CHART PLACEMENT' /c/Users/sasai/AppData/Local/Temp/chart_placement_e2e.log"

# compile-only (AFK 罠回避: -executeMethod を抜く)
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" `
    -batchmode -nographics -quit `
    -projectPath C:\Users\sasai\Documents\backcast `
    -logFile C:\Users\sasai\AppData\Local\Temp\compile.log
bash -c "grep -a 'error CS' /c/Users/sasai/AppData/Local/Temp/compile.log" # 0 件を期待
```
