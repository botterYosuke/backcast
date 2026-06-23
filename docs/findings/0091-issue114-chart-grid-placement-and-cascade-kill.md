# 0091 — issue #114: chart grid placement & cascade kill (grill design tree)

> **Numbering note (2026-06-23 post-rebase)**: originally authored as 0089; renumbered to 0091 on rebase onto origin/main since 0089/0090 were claimed by the v19 marimo replay finding pair (#112). All in-text references to `findings 0089` have been updated to `findings 0091` across the runner, helper, and BackcastWorkspaceRoot.cs.

**Status**: GRILL CLOSED — design tree fixed, implementation order S0 → S1 → S2 → S3.
**Scope**: supersedes issue #114 本文 S1/S2/S3。owner reorg の P1–P15 + S1/S2/S3 を正本。
**Companion**: 本 finding は実装スライス共通の設計の木。スライス毎の RED→GREEN・再走手順は実装時に追記する。

---

## F0 — issue 本文の事実誤認 (empirical correction)

issue 本文は「全 52 chart に x/y を保存済み・(+360, -360) の階段保存」と書いたが、`python/strategies/v19/v19_morning_cell.json` 実体は:

| 場所 | chart 件数 | 形 |
|---|---|---|
| `layout.floatingWindows` | **0** | 唯一 entry は `order:region_001` |
| `layout.panels` | 52 | normalized rect (dead schema) |
| `layout.hakoniwaProfiles.replay.panels` | 52 | 同上 (dead schema) |

`RestoreFloating` (`Assets/Scripts/Live/BackcastWorkspaceRoot.cs:2103`) は `doc.floatingWindows` しか読まない。`ApplyLayout` docstring (L2083) と `DockDefaultPlacement.cs` docstring (L1-20) は legacy `panels` / `hakoniwaProfiles` を IGNORE と明示。

**真の根本原因**: legacy sidecar は chart が `floatingWindows` に無い → `RestoreFloating` で chart 0 件復元 → 続く `ReseedFromEditor()` → `SyncChartWindowsToUniverse` (`BackcastWorkspaceRoot.cs:937-942` の `for` ループ) が 52 chart を `SpawnDockedToFocus` で 1 個ずつ snap → 直前 spawn が focus を取り、次は隣にフラッシュ → 階段。**階段は disk ではなく runtime で発生**。

cascade 増分も誤: KIND_CHART 実 default = (520, 360) (`FloatingWindowCatalog.cs:83`)。issue 本文「(+360, -360)」は KIND_ORDER の値の取り違え。

---

## F1 — scope の超過: runtime cascade 修正の所在

owner reorg は P5 (post-Open universe grow) を「回帰防止」枠に置いたが、**P5 と P2 は同一経路 (`SyncChartWindowsToUniverse`) の cascade で発生**。saved-honor だけ強化しても universe grow で必ず階段する → P5 は new spec gate に昇格。中核実装は `SyncChartWindowsToUniverse` の cascade→grid 置換、これだけで P2 / P5 が同時に GREEN。

---

## F2 — グリッド配置 = 4 slice 共通 foundation

P4 (mixed) / P2 (legacy) / P8 (corrupted) / P10 (collision) の 4 site が「saved 無し chart の置き場」を求める。各 slice inline 実装はロジックが分散し S1/S2/S3 で挙動 divergence する (例: P10 de-collide 先と P4 grid が重なる)。pure helper を 1 本に集約することで構造的に防ぐ。

---

## F3 — judgment calls の決着

- **P10 (UX)**: ✅ de-collide 一択。「saved 通り重ねる」は UX 退行で正当化できない (下の chart は永遠に触れない)。手編集ミスからの recovery と割り切る。
- **P11 (TTL)**: ✅ TTL 無し・永遠保持。理由 = (a) cost 無視できる (1 entry ≈ 100 byte、52 chart で 5KB)、(b) TTL は boot counter or wall-clock の永続化を要する schema 拡張、(c) delist→relist 往復で位置が消える方が UX 退行。
- **P12 (migration timing)**:
  - **read 側**: legacy `panels` は normalized rect で**絶対座標復元には保存時 viewport size の仮定が要る** (disk に無い) → 安全に promote できない → 「saved 無し」扱い→グリッド送り。P2 期待動作と一致。
  - **write 側**: `CaptureLayout` (`BackcastWorkspaceRoot.cs:2055`) は既に `panels = new List<PanelLayout>()` を書く。次 Save の瞬間に panels は物理 0 件化 = **migration は user 1 回 Save で自動完了**。issue 本文 S3 (HITL「既存階段データの是正」) の justification が消えるので**ドロップ**。

---

## F4 — helper contract: `ChartGridPlacement`

**Pure**, `UnityEngine` 依存は `Vector2` のみ (FloatingWindowMath / DockDefaultPlacement と同じ two-tier discipline)。

```csharp
public static class ChartGridPlacement
{
    // N 枚 chart 用の grid セル矩形を canvas-LOGICAL coord で返す。
    // cols = ceil(sqrt(n))、row-major、slot k → (col = k % cols, row = k / cols)。
    // y は canvas-LOGICAL up-positive (row 0 が最上段)。
    // DockDefaultPlacement とは別物 — chartSize は count-scale せず spec-fixed (#101)。
    public static List<FloatingWindowMath.DockRect> ComputeFlushSlots(
        int n,
        Vector2 anchorTopLeft,
        Vector2 chartSize,    // KIND_CHART defaultSize = (520, 360)
        Vector2 gap);

    // N 個の non-overlapping 配置点 (top-left) を返す。
    // slot k を anchor + (col=k%gridCols, row=k/gridCols) で生成し、avoid と overlap
    // しないものを順に採用。grid は無限拡張 (rows を増やす)、必ず N 個返る。
    // gridCols は caller が指定 (incremental n=1 でも cols を caller 側 = ceil(√total)
    // で渡す → row-major に右へ分岐する。helper 内で ceil(√n) すると n=1 で
    // single-column 縦カスケードを誘発する構造欠陥がある)。
    public static List<Vector2> AllocateNonOverlappingTopLefts(
        int n,
        int gridCols,
        Vector2 anchorTopLeft,
        Vector2 chartSize,
        Vector2 gap,
        IReadOnlyList<Rect> avoid);
}
```

### F4 採用 sub-decisions

| # | 採用 | 理由 |
|---|---|---|
| cols | `ceil(√total)` を **caller 側**で算出して `gridCols` で渡す | viewport 由来は canvas 無限 / saved の absolute 性と非整合。`DockDefaultPlacement` 規約継承。helper 内 `ceil(√n)` は incremental n=1 で col=1 縦積みになる構造欠陥 (owner 検出)。 |
| anchorTopLeft | `(-600, -332)` = base cluster 下端 + 12px gap | base cluster `(-600, +320)` ～ `(+600, -320)` の 1200×640 真下に置けば overlap ゼロ。user mental model「上=panel、下=charts」と一致。 |
| avoidance 解像度 | exact saved rect avoidance (穴あき) | grid-cell quantize は 1 chart が 2-3 セル分 block する過剰回避。穴あきは user 視点で問題にならない。実装短い。 |
| avoid 中身 | (a) 既存全 chart rect (saved+既配置 grid) + (b) base cluster 全 5 window rect | 同 plane (`_dockWindows`) のみ avoid。order ticket は front plane (`_windows`)、z 的に被ってよい。 |

### F4 call site 使い分け (4 site)

| site | call | n | gridCols | avoid |
|---|---|---|---|---|
| `SyncChartWindowsToUniverse` 置換 (per missing iid) | `AllocateNonOverlappingTopLefts` | 1 | `ceil(√universe.Count)` | 既存全 chart + base cluster |
| `ApplyLayout` 後 P4 残余 | `AllocateNonOverlappingTopLefts` | N−K | `ceil(√universe.Count)` | saved K + base cluster |
| P8 corrupted fallback | `ComputeFlushSlots` | N | (内部 `ceil(√n)` で OK) | — |
| P10 de-collide (S1) | `AllocateNonOverlappingTopLefts` | 1 per collision | `ceil(√universe.Count)` | 既存全 chart + base cluster |

### F4 future polish (今 fix しない)

`anchor.x = -600` (base cluster 左端揃え) は n=52 cols=8 で grid が `-600 + 7*(520+12) = +3124` まで右に伸び、base cluster との水平中央が大きく崩れる。将来 user が「grid が右に偏る」と指摘したら `anchor.x = -(gridCols*w + (gridCols-1)*gap) / 2` (cols 由来の水平中央) に切り替える余地を残す。

---

## F5 — AFK gate 設計: `ChartPlacementJourneyE2ERunner`

**ファイル**: `Assets/Tests/E2E/Editor/ChartPlacementJourneyE2ERunner.cs` + `.md`台本 (ADR-0015 規約 `<Surface>E2ERunner`、`<Surface> = ChartPlacement`)。

**Base template**: `LayoutPersistenceJourneyE2ERunner.cs` (S1-S4 が全部 sidecar fixture → 実 root 駆動 → live 観測型で同型)。**S0 スタイル** (pure helper unit) は `FloatingWindowE2ERunner.cs` から借用 (FloatingWindowMath / DockSnapPlacement の pure 部と同型)。

### Section 構成 (5 section × 19 probe)

| Section | 範囲 | probe |
|---|---|---|
| **S0 pre-flight** (pure helper unit) | `ChartGridPlacement` 直接 | CP-S0-01: `ComputeFlushSlots(n)` n=0/1/4/9/52 の rect 数学。CP-S0-02: `AllocateNonOverlappingTopLefts(1, cols=8, avoid=full row 0)` → row 1 col 0 返す。CP-S0-03: `AllocateNonOverlappingTopLefts(N, cols=8, avoid=[])` ≡ `ComputeFlushSlots(N)` (ただし gridCols=8 を caller 指定)。CP-S0-04: avoid 半重なりでも skip 動く |
| **S1 saved-honor** | 実 root 経由 | CP-S1-01 (**P3** full saved): 5 chart 全 saved → 全 honor、grid 0 件。CP-S1-02 (**P4** partial): 5 chart 中 3 saved → 3 honor + 2 grid、重なり 0。CP-S1-03 (**P9** off-screen): saved x=-9999 → canvas-bound ±4000px clamp、entry 非破棄。CP-S1-04 (**P10** collision): 2 saved 同座標 → JSON 配列順で先勝、後発 grid。CP-S1-05 (**P13** invalid w/h): w=0 / h=-1 → default size 差戻、x/y は honor |
| **S2 legacy migration + cascade kill** | sidecar I/O + Sync 置換 | CP-S2-01 (**P2** legacy panels): `v19_morning_cell.json` fixture → cascade ゼロ、52 chart が cols=8 grid 整列、ペア 0 重なり (= 本 bug fix gate)。CP-S2-02a (panels=[] 物理 0 件化): open legacy → Save → reread で `panels=[]`。CP-S2-02b (drag persist): open legacy → in-memory drag 1 件 → Save → reread で `floatingWindows` に 52 entry (drag chart は saved x/y) |
| **S3 resilience** | sad-path | CP-S3-01 (**P8** corrupted): truncated JSON → `.bak` 退避 + grid fallback、起動 non-blocking。CP-S3-02 (**P11** ghost): saved に居て universe に居ない iid → spawn 0、`CaptureLayout()` で entry が出てくる (TTL 無し永遠保持)。CP-S3-03 (**P14** duplicate): 同 iid x 2 → 先頭採用、`CaptureLayout` で de-dup。CP-S3-04 (**P15** tiny viewport): canvas-relative 計算で viewport 無依存、runaway scroll 不発 |
| **S4 regression/characterization** | 既存 invariant 防衛 | CP-S4-01 (**P1** no sidecar): grid 通り。CP-S4-02 (**P5** post-Open grow): `_scenario.Universe.Add(iid)` 後の `SyncChartWindowsToUniverse` で cascade 不発・grid 次スロット。CP-S4-03 (**P6** 52 chart): cols=8 で 7 row、`x` 上限 `anchor.x + 7*(520+12) = +3124` を超えない (sanity bound)。CP-S4-04 (**P7** empty universe): chart window 0 件・base cluster 健在 |

### probe ID 規約
`CP-S{N}-{NN}[a/b]` (CP = Chart Placement、既存 JOURNEY-LAYOUT-* / SECTION 形式と同型)。

---

## F6 — P9 (off-screen clamp) の implementation site

**採用**: canvas-relative 固定 bound clamp (option β)。

`RestoreFloating` (`BackcastWorkspaceRoot.cs:2126` の `ApplyGeometry`) で saved x/y を `[-4000, +4000]` の canvas-bound に clamp する。元 entry は破棄せず x/y のみ書き換え。

### 採用根拠 (主従)

- (a) **主**: DI seam 不要 (`_dockWindows.ApplyGeometry` 直前で `Mathf.Clamp` 2 行で済む)。viewport を import せず canvas 座標のみで完結 → probe deterministic。
- (b) **主**: bound ±4000px が monitor 差分 ±2000px (4K→FHD で約 ±1000、4K→WQHD で約 ±500) を救う。off-screen からの rescue が必要な実シナリオを十分包む。
- (c) **補強**: Unity batchmode `-nographics` で `Screen.width/height` の値は environment 依存 (HMI 無しのため代替 0/1 を返す build もある general 注意)。viewport-relative (option α) は AFK で fake viewport の DI seam が要る → コスト高。

---

## 実装順 (固定)

1. **S0** — `ChartGridPlacement` pure helper 新設 + CP-S0-01..04 RED→GREEN
2. **S1** — `RestoreFloating` 修正 (clamp + invalid w/h + collision dedup + JSON 配列順先勝) + CP-S1-01..05 RED→GREEN
3. **S2** — `SyncChartWindowsToUniverse` の cascade→grid 置換 + `LayoutSidecarStore.WriteLayout` の panels=[] 確認 + CP-S2-01 / 02a / 02b RED→GREEN
4. **S3** — corrupted .bak 退避 + ghost 保持 + de-dup + viewport 無依存 確認 + CP-S3-01..04 RED→GREEN
5. **S4** characterization は S1/S2/S3 の副作用で自然 GREEN になる前提、最後に確認のみ

S0 を先頭に置く理由: TDD として helper unit (S0) が pin されていないと S1 の integration RED の原因切り分けが「helper 自体のバグ」「helper の呼び方」「root 配線」の 3 通りに発散する。S0 GREEN 確定後に S1 へ進めば、S1 RED は root 配線の問題に絞れる。

---

## CP-S2-02 を 2 段化した理由

migration gate (panels=[] の物理 0 件化) は **drag 無しでも証明できる**。drag 1 件は「saved 位置が同時に floatingWindows へ persist される」を pin する**別 assertion**。

- **CP-S2-02a**: open legacy → Save (drag 無し) → reread → `panels.Count == 0`、`floatingWindows` には grid 配置の 52 entry
- **CP-S2-02b**: open legacy → 1 chart を任意座標へ in-memory ApplyGeometry → Save → reread → `floatingWindows[]` 内のその chart entry が drag 後座標

これを 1 probe に混ぜると RED 時に「migration が壊れたのか persist が壊れたのか」が判別不能になる。

---

## CONTEXT.md / ADR 影響

- **CONTEXT.md**: 影響なし (glossary 不変条件は変わらない)。
- **ADR**: 新規 ADR 不要。
  - ADR-0017 (Hakoniwa dockable floating windows): 不変、本 finding は `SyncChartWindowsToUniverse` の cascade vs grid 内部実装を切り替えるのみで、ADR-0017 §6 (floatingWindows が SoT) は無傷。
  - ADR-0018 (back plane chart): 不変、`_dockWindows` 経由は維持。
  - ADR-0019 (window groups & drag): 不変、groupId 復元 (RestoreFloating) は無触。
  - 将来 helper の anchor / cols 戦略を大改修するならその時に ADR 立てる。

---

## §S1/S3-deferred — 本 PR で未実装の 6 probe + 残作業

本 PR は **cascade-kill 中核 (S2) + saved-honor 主 case (S1 P3/P4) + S3 viewport-無依存 (P15) + S4 regression (P1/P5/P6/P7)** を実装。**11/19 probe GREEN**。残 6 probe は production hardening / sad-path で、cascade-kill による v19 ユーザーペイン解消とは独立に追加できる:

| 残 probe | 必要な production 変更 | 想定実装ポイント |
|---|---|---|
| CP-S1-03 (P9 off-screen clamp) | `RestoreFloating` 直前に `NormalizeFloatingWindowsForRestore` pre-pass を入れて `Mathf.Clamp(w.x/w.y, -4000, +4000)` | `BackcastWorkspaceRoot.cs:RestoreFloating` |
| CP-S1-04 (P10 collision de-collide) | 同 pre-pass で chart kind の overlap 検出 → `ChartGridPlacement.AllocateNonOverlappingTopLefts(1, …, accumulatedAvoid)` で再配置 | 同上 |
| CP-S1-05 (P13 invalid w/h) | 同 pre-pass で `w.w<=0 \|\| w.h<=0 \|\| !IsFinite` 時に `_catalog.TryGet(w.kind).defaultSize` 差戻 | 同上 |
| CP-S3-01 (P8 corrupted .bak) | `DoFileOpen` で `TryReadLayout` 失敗時、corrupted (parse error) と missing (no file) を区別し、corrupted のみ `<path>.bak` へ rename | `DoFileOpen` / `LayoutSidecarStore.TryReadLayout` の戻り値を 3 値化 |
| CP-S3-02 (P11 ghost retention) | `_lastSavedFloatingWindows` フィールドを Open で cache、`SyncChartWindowsToUniverse` の despawn パスで cache を引いて `_ghostChartEntries` へ stash、`CaptureLayout` で append+dedup | `BackcastWorkspaceRoot` に state field 追加 |
| CP-S3-03 (P14 dedup) | 同 pre-pass で `HashSet<string> seenIds` で先頭採用、後発 `wins.RemoveAt(i)`。`CaptureLayout` 出力も dedup | `BackcastWorkspaceRoot.cs:RestoreFloating` + `CaptureLayout` |

**recommended follow-up sequencing**:
1. P9 + P13 + P14: 同 pre-pass で 1 PR (`NormalizeFloatingWindowsForRestore` ヘルパ新設)
2. P10: 上記 pre-pass を P10 にも拡張 (1 PR)
3. P8: `TryReadLayout` の 3 値化 + `.bak` rename (1 PR)
4. P11: state field + ghost stash (1 PR — schema 影響無し、最も複雑)

stub の `Section1` 末尾 / `Section3` で SKIP コメントを残してあり、上記順序で実装するたびに追加 probe を unstub する。

## 関連参照

- 移植元: なし (新規 helper)
- 関連 finding: 0078 (#101 KIND_CHART spec-fixed size — cascade の前歴), 0075 (#99 dock windows = floatingWindows SoT)
- 関連 ADR: 0017, 0018, 0019, 0015 (E2E runner 配置規約)
- issue: #114
