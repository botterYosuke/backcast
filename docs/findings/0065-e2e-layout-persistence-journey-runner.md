# findings 0065 — E2E LayoutPersistenceJourney runner（第二波・Journey・全行新規）

`Assets/Tests/E2E/Editor/LayoutPersistenceJourneyE2ERunner.cs`（台本: 同ディレクトリ `.md`）。第二波 12 本目・
横断ストーリーの Journey E2E。複数サーフェスをまたぐ実ユーザーストーリー＝**配置 5 次元**（infinite-canvas
pan/zoom・箱庭 tile 順・floating window rect・notebook cell 位置・per-mode profile）を `<strategy>.json` の
`layout` キーへ Save → 汚す → File→Open で復元、の round-trip を **実 `BackcastWorkspaceRoot` を反射駆動**して観測する。

## 実行コマンド

```text
# compile-only ゲート（error CS\d+ が 0 件・return code 0）
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "C:\Users\sasai\Documents\backcast" -logFile "<log>"

# AFK GREEN
& "C:\Program Files\Unity\Hub\Editor\6000.4.11f1\Editor\Unity.exe" -batchmode -nographics -quit \
  -projectPath "C:\Users\sasai\Documents\backcast" \
  -executeMethod LayoutPersistenceJourneyE2ERunner.Run -logFile "<log>"
# expect: [E2E LAYOUT JOURNEY PASS] ... / exit=0
# 確認は Bash `grep -a "E2E LAYOUT JOURNEY" <log>`（ripgrep/Select-String は → や一部 PASS 行を取りこぼす）
```

## section ↔ Action ID

| section | Covers | 観測 |
|---|---|---|
| `Section1_RealRootRoundTrip` | JOURNEY-LAYOUT-01..13 | 1 つの composed root で baseline open → 5 次元を非既定へ capture → `OnFileSave` → File→New＋明示 geometry 汚し → `OnFileOpen` → 5 次元復元＋per-mode 独立 |
| `Section2_NoWipeBareOpen` | JOURNEY-LAYOUT-14 | scenario-only/no-layout sidecar の open は bare open（`ApplyLayout` skip）で live geometry を wipe しない（findings 0048 D4） |
| `Section3_SaveAsFork` | JOURNEY-LAYOUT-15 | `OnFileSaveAs` が新 `.py`＋`.json`（scenario＋layout）を fork し、旧 `.json` は無改変・`_currentLayoutPath` は新へ rebind |

## 設計判断

- **実 root を反射合成（Python-FREE）**: `ComposeRoot` = `OpenScene` → `_font` 注入 → `SetSynthesizer(FakeMarimoSynthesizer)`
  → `ResolvePaths` → `BuildWorkspace`。配置 round-trip は kernel も描画も要さない。`OnFileSave`/`OnFileOpen` の mode
  副作用は disconnected なら `SendModeSideEffect` が null mode を skip して host を触らない。RunButton SectionD /
  OrderTicket / BackcastWorkspaceProbe S12/S14 と同型（parity-first・production 無改変）。
- **per-mode profile は `SyncBaseTilesToMode(bool)` 反射で駆動**（footer UI は使わない）。台本「要確認: AFK で footer mode
  切替を反射駆動できるか」への回答＝**できる**。BackcastWorkspaceProbe S12 が同じ反射経路で Replay⇄Live の profile 独立を
  実証済みで、本 Journey はそれを実 `OnFileSave`/`OnFileOpen`（sidecar 経由）へ縫い直した。footer の実 UI 操作は
  FooterMode Surface 台本 / LiveManualTrade Journey の責務。
- **純データ probe との層分け（非移送）**: 各次元の純データ round-trip は `ReplayLayoutProbe`（panels Capture/Apply・
  Default 一致）と `MultiDocLayoutProbe`（layout-key round-trip・scenario⇄layout coexist・TryReadLayout 厳格性・
  SaveAs）が scene-free / Python-FREE で AFK 権威。それらは「picker UI＋root 配線は HITL」と明記しており、本 Journey は
  その HITL 部分＝**実 root を通した配置→Save→Open→復元**を埋める（重複ではなく縫い目）。よって純データ probe は git mv
  しない（別レイヤ・別正本）。

## VACUITY — 生産 `OnFileNew` の落とし穴（本 runner の急所）

台本の vacuity 規約は「File→New の『汚し』が効いていること（New 後に値が baseline/別物）を assert してから Open で戻す」
だが、生産コードを読むと **`OnFileNew` は canvas / Hakoniwa / floating の geometry を reset しない**（`BackcastWorkspaceRoot`
の OnFileNew コメント「canvas pan/zoom + Hakoniwa are NOT reset」）。cell も `_coordinator.New()` →
`SyncWindowsToNotebook(null)` が region_001 を `Show`（現在位置維持）で保つので reset しない。File→New が真に汚すのは
`_currentLayoutPath`（→`""`）と notebook 内容だけ（＝JOURNEY-LAYOUT-08 の観測対象）。

帰結: **4 つの geometry 次元（canvas/tile/window/cell）の round-trip 非空虚化を File→New に頼ると空虚**（保存値が live に
居座ったまま「復元 OK」と誤認する）。そこで Section1 は File→New 後に各 geometry 次元を **明示的に第3の値へ perturb** し
「保存値と別物」を assert してから `OnFileOpen` で「保存値へ戻る」を assert する（BackcastWorkspaceProbe S12 の
"perturb the live order, then restore from disk" を実 `OnFileSave`/`OnFileOpen` seam へ持ち上げた形）。各次元で
「baseline → 非既定 capture → Save → 汚し（別物 assert）→ Open → 保存値復元」の 3 点を踏む。

- tile 順は `IsValidForMode` の honor 規則＝**base id SET 一致時のみ honor**。canonical 順を渡すと再ソート no-op（#59
  DepthLadder 教訓）なので、base 同士を `Swap` して **SET は不変・順だけ非既定**にする。chart tile は universe-owned の
  noise なので open 直後に universe を空へして base-only にする（on-disk scenario キーは 7203.TSE のまま＝step7 coexist
  検証用）。reopen 後は scenario 再 seed で chart:7203.TSE が base の後ろに付くが `AssertBaseOrder` が許容する。
- per-mode 独立の非空虚性（S12 reasoning）: doc の `panels` ミラー＝Live 順なので、reopen 後 Replay へ flip して replayBase
  が返るのは **永続化された `hakoniwaProfiles.replay` sub-profile からしか起こり得ない**（field が落ちれば Replay は Live
  ミラーから seed され liveBase になり FAIL する）。

## delete-the-production-logic litmus

- `ApplyLayout` の `_canvas.ApplyView(doc.canvasView)` を no-op 化 → **JOURNEY-LAYOUT-10 FAIL**。
- `ApplyLayout` の `ApplyProfileOrder(_baseLive)` を no-op 化 → **JOURNEY-LAYOUT-11 / 13 FAIL**。
- `ApplyLayout` の `RestoreFloating(doc)` を no-op 化 → **JOURNEY-LAYOUT-12 FAIL**。
- `OnFileOpen` が `_coordinator.Open` に渡す `cellPositions` を `null` 固定 → **JOURNEY-LAYOUT-05(restore) FAIL**。
- `OnFileSave` の `TryWriteLayout(_currentLayoutPath)` を消す → **JOURNEY-LAYOUT-07 FAIL**。
- `CaptureLayout` の任意 1 次元の収集を消す → その次元の capture(step 2-6)/復元観測が FAIL。

## 検証

- compile-only ゲート: `error CS\d+` **0 件**・`Exiting batchmode successfully` / return code 0・新 `.meta` 生成（2026-06-20 lead 実走・確定）。
- AFK GREEN: `-executeMethod LayoutPersistenceJourneyE2ERunner.Run` で `[E2E LAYOUT JOURNEY PASS] real workspace
  round-tripped 5 layout dimensions (canvas/tiles/windows/cells/per-mode profile) through the <strategy>.json layout key
  and restored them on reopen.` を bash `grep -a` で **1 件確認**・FAIL 0 件・sentinel（`Found no leaked weakptrs` /
  Package Manager shutdown）あり＝executeMethod 実走・exit 0（2026-06-20 lead 実走・GREEN 確定。JOURNEY-LAYOUT-01
  separator 修正後の再走で全 section GREEN）。
- RED litmus（上記いずれか 1 つの no-op 化で該当 section が FAIL）: lead 任意。watch-point ①② は GREEN で実証済み。
- 注意点（AFK 初走で確認すべき watch-point）: ① `OnFileSave` 経路で `_coordinator.Save()`（FakeMarimoSynthesizer 合成）が
  true を返し layout キーが書けること、② Section3 の `OnFileSaveAs` → `_scenario.Commit(newPy)` が scenario キーを fork
  すること（buffer が valid 前提）。いずれも RED なら本 findings に原因を追記。
- 初走 RED → 修正（JOURNEY-LAYOUT-01）: `_currentLayoutPath` の bind assert が **片側だけ `Path.GetFullPath`** だった一方、
  production `OnFileOpen`/`OnFileSaveAs` は StubFileDialog が返す raw path（`Application.temporaryCachePath` 由来の `/`混在
  区切り）を無正規化で `_currentLayoutPath` へ格納するため separator mismatch で `!=`（#3 RUN-01 同型）。test 側を両辺
  `Path.GetFullPath` + `StringComparison.OrdinalIgnoreCase` 比較へ修正（JOURNEY-LAYOUT-01/09/15 の 3 assert・production 無改変。
  FileOpenNonMarimoE2ERunner.cs:142 の path-identity 定石に統一）。
