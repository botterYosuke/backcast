# Per-mode layout profile Findings: Replay/Live で Hakoniwa tile 並び順を別々に保存・復元（TTWR `HakoniwaLayoutProfiles` parity）

- 受け皿 issue: **#62**（layout: Hakoniwa per-mode profile）。親 #1 (Epic) / #5 (Step3 cutover)。**4-stage 計画の stage③（P）**。
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0005 — 1:1 surface parity](../adr/0005-cutover-scope-1to1-surface-parity-with-ttwr-ui.md)（accepted・自己保護節）, [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)（accepted・自己保護節）。下位事実は ADR に書き戻さず本 findings に記録し ADR を「方針: ADR-0005/0003」として参照。
- 先行: **stage② #61（findings 0028・mode-conditional base / `SyncBaseTilesToMode` / `ReassertBaseAfterRestore`）**, **stage① #60（findings 0027・chart tile family / box-grow / base orchestration）**, #59（workspace root / findings 0025・§12 universe 共有 SoT）, #39（footer mode / findings 0026・`FooterModeViewModel`）, #12（layout schema / findings 0004・`LayoutDocument`/`LayoutStore`）。
- 設計確定: `grill-with-docs`（2026-06-16・owner インタビュー 4 問）。AC を TTWR 実ソース（`src/ui/hakoniwa.rs` の `HakoniwaLayoutProfile`/`HakoniwaLayoutProfiles`/`build_hakoniwa_snapshot`/`apply_hakoniwa_restore_resources`/`reconcile_hakoniwa_tiles`・`src/ui/layout_persistence/{restore,sidecar}.rs`・ADR 0013/0016）と照合し、**AC は実態の正確な言い換え**であることを確認（#42 のような乖離なし）。

> **状態: 設計確定。** grill で全分岐を lock。実装着手時に §10 へ証跡を追記する。

---

## 0. スコープ（owner 確定 2026-06-16・grill Q1）

TTWR の `HakoniwaLayoutProfiles { current, replay, live }`（Replay/Live の 2 profile・`from_mode`: Replay→Replay / LiveManual・LiveAuto→Live）を backcast へ移植する。**#61 までは単一共有レイアウト**で、mode を跨ぐと最後の配置を共有していた。本 issue で **Replay と Live が各々の Hakoniwa tile 並び順を別々に覚え**、mode 切替で当該 profile を復元する。

### 採用 / 不採用

- **採用**: per-mode profile = **Hakoniwa の tile 並び順（`_hako.Capture().panels`）だけ**／disk スキーマに nested `hakoniwaProfiles{replay,live}` を **additive**（version bump 無し・null defaulting）／既存単一 `panels` doc から **forward-compat seed**／mode flip は TTWR `reconcile_hakoniwa_tiles` 準拠（旧 profile に退避→current 切替→新 profile を `is_valid_for` 検証して load）／検証ロジックは **pure class `HakoniwaLayoutProfiles`** に抽出し AFK 権威化／AFK probe + owner HITL。
- **不採用（= #62 外）**:
  - **canvas pan/zoom・floating window・Strategy Editor の開きファイルの per-mode 化**（grill Q1）。TTWR `restore.rs` は camera/windows を **flat 復元**＝mode 横断で単一。backcast も `canvasView`/`floatingWindows`/`strategyEditors` は doc 直下に flat 保持。
  - **box 位置/サイズ・cols/rows（divider）の永続化＋per-mode 化** → stage④ ~~#63~~ **ボツ**（#63 close 2026-06-24・[ADR-0017](../adr/0017-hakoniwa-dockable-floating-windows.md) が split-grid を退役・box geometry/cols/rows の概念ごと消え、floating window の自由配置＋geometry 永続化＝[ADR-0024](../adr/0024-puzzle-feel-drag-magnetic-snap-swap-translate-detach-merge.md)で代替）。〔参考・退役前の設計〕TTWR は box geometry を `HakoniwaSnapshot` 直下（**共有**）、cols/rows を `HakoniwaGridSnapshot`（per-mode）に持ち、backcast は #63 で同コンテナ（`HakoniwaProfile`）へ additive 拡張する予定だった。#62 の box は #60 の derived-grow（n_total 由来）を維持。

## 1. モデル（owner 確定・grill Q1/Q2）

```
HakoniwaLayoutProfile = { Replay, Live }              # TTWR enum。from_mode: Replay→Replay / LiveManual・LiveAuto→Live（AC3）
profile(mode) = List<PanelLayout>                      # その mode の Hakoniwa tile 並び順（_hako.Capture().panels）
disk: LayoutDocument {
  panels: List<PanelLayout>                            # legacy: forward-compat SEED + active-mode mirror（新 SoT ではない）
  hakoniwaProfiles: HakoniwaProfiles {                 # ← 新 SoT（additive・#63 拡張点はボツ＝下記）
    replay: HakoniwaProfile { panels }                 # #63(ボツ): cols/rows/box 拡張は ADR-0017 退役で不発
    live:   HakoniwaProfile { panels }
  }
  canvasView / floatingWindows / strategyEditors       # flat 共有（§0 不採用どおり）
}
```

- **per-mode 化対象 = Hakoniwa tile 順のみ**（grill Q1）。chart の per-mode 順も保存対象（membership は universe 再導出＝#60 不変）。
- **正本 = hakoniwaProfiles**、`panels` は active mode の互換ミラー（read は常に profiles 優先で drift しない）。
- mode の正本は footer `FooterModeViewModel.DisplayMode` → `HakoniwaBaseTiles.IsLiveShape` の 2 値（#61 と同じ・LiveManual/LiveAuto は同一 Live profile＝AC3）。backcast の current-shape SoT は `BackcastWorkspaceRoot._baseLive`（TTWR `profiles.current` 相当を root が所有）。

## 2. mode 切替（owner 確定・grill Q3）— TTWR reconcile_hakoniwa_tiles parity

TTWR `reconcile_hakoniwa_tiles` の手順を `SyncBaseTilesToMode(live)` に写経:

1. **退避**: 切替前に現 layout を旧 mode profile へ（`_profiles.Set(_baseLive, _hako.Capture().panels)`）。
2. **membership**: 新 mode の base 集合へ（startup の add/remove＝#61 の既存ロジック）。
3. **load（検証）**: 新 mode profile を `ApplyProfileOrder(live)` で適用。

## 3. base 順の honor / canonical（owner 確定・grill Q3）— TTWR `is_valid_for` parity

論点は **base tile の並び順**（chart 順はどの案でも honor）。TTWR `is_valid_for` は「保存 order の**集合**が当該 mode の `hakoniwa_tile_kinds` と一致するか」で検証し、一致なら user 順 honor／不一致・無は mode default。これを採用:

- `IsValidForMode(live)`: profile の **非 chart（base）id 集合** == `HakoniwaBaseTiles.Kinds(live)` の集合（Replay は startup 込み・Live は無し）。chart id は判定対象外（dynamic・universe 所有）。
- `BaseOrderForMode(live)`: 一致 → profile の base 順（user swap を honor）／不一致・無 → canonical `Kinds(live)`。
- **#61 の `ReassertBaseAfterRestore`（常に canonical 強制）を strict superset として包含**: legacy/#60 世代/Default() doc は base id 集合不一致 → canonical に落ちる＝[[backcast-layout-default-id-collision]] の HIGH 衝突安全をそのまま GREEN 維持。`#62` が正しく書いた profile のみ集合一致 → user 順 honor（#61 が捨てていた新能力）。実装は `Reorder` の prefix を `Kinds(mode)` 固定から「検証済 profile base 順 or canonical」に差し替えるだけ。
- `ReassertBaseAfterRestore` は **`ApplyProfileOrder(_baseLive)` に置換**（empty profiles → canonical fallback で #61 と同一挙動）。

## 4. 永続化と forward-compat（owner 確定・grill Q2）— additive・version bump 無し

- **read**: `hakoniwaProfiles` が非空ならそれを SoT に（`HakoniwaLayoutProfiles.FromDocument`）。無/空（旧 doc）なら `SeedFromLegacy(doc.panels)` で **両 profile を legacy panels から seed**。validity gate（§3）が seed 後の各 mode を正す（legacy shape が当該 mode と不一致なら canonical）。
- **write**: `hakoniwaProfiles` を正本に出力＋`panels` に active mode をミラー（pre-#62 リーダ互換）。
- **version**: `canvasView`/`floatingWindows`/`strategyEditors` と同じ additive・null defaulting なので `CURRENT_VERSION` は **1 のまま**（findings 0004 §6 の forward-evolution 寛容）。`LayoutStore.Sanitize` に `NormalizeHakoniwaProfiles`（各 profile panels の null/empty-id/null-rect drop）を追加。

## 5. ロジック置き場所（owner 確定・grill Q4）— pure class に抽出し AFK 権威化

- **新規 `Assets/Scripts/Hakoniwa/HakoniwaLayoutProfiles.cs`（plain C#・UnityEngine-free）**: serializable DTO（`HakoniwaProfile{panels}` / `HakoniwaLayoutProfiles{replay,live}`）＋ logic（`Get`/`Set`/`HasAny`/`IsValidForMode`/`BaseOrderForMode`/`SeedFromLegacy`/`Clone`/`FromDocument`）。依存は `LayoutDocument`/`PanelLayout`/`HakoniwaBaseTiles.Kinds` の plain data のみ＝`HakoniwaGridMath`/`HakoniwaBaseTiles` と同じ headless AFK 権威。
- **`HakoniwaController`** が RectTransform への actuation、**`BackcastWorkspaceRoot`** が membership 同期と box-grow（#60 の altitude 維持）。profiles class は **順（id 列）だけ**返す。

## 6. 射程外（#62 に含めない）

- box 位置/サイズ・cols/rows 永続化＋drag-handle/divider resize（④ ~~#63~~ **ボツ**・ADR-0017 退役で代替）／canvas/window/editor の per-mode 化／Replay パネル実データ（follow-up #65）。

## 7. 検証サーフェス（owner 確定・grill Q4・AFK probe が正本ゲート）

### AFK probe（headless・Python-free・batchmode 可）

- **新 pure probe `HakoniwaProfileProbe`**（`HakoniwaLayoutProfiles` を UnityEngine 抜きで直接叩く・4 ケース）:
  - **(a) valid honor**: base を swap した valid profile → `BaseOrderForMode` がその base 順を返す（mode 別に別順）。
  - **(b) legacy/衝突 → canonical**: Default()/#60 世代/集合不一致 doc → `IsValidForMode`=false → `BaseOrderForMode`=canonical `Kinds`（#61 衝突安全の回帰ガード）。
  - **(c) chart per-mode 順**: profile の chart 順が保持され base 判定に混じらない（`IsChartId` 除外）。
  - **(d) seed + round-trip**: 旧 panels-only doc → `FromDocument` が両 profile を同順 seed（**非空 assert**）／`hakoniwaProfiles` 込み doc を `LayoutStore` save→load→`StructurallyEqual`（disk テキストに `hakoniwaProfiles` の per-mode slot が実在）。
- **`HakoniwaBaseModeProbe.Section4`**（更新）: restore の衝突回帰を `ReassertBaseAfterRestore`→`ApplyProfileOrder` 経由に差し替え＋**valid profile honor の新ケース**を追加（valid な base swap が `ApplyProfileOrder` で honor される）。
- **`BackcastWorkspaceProbe`**（拡張・統合冒煙 1 本）: 実 root を headless 合成し、Replay で base swap→flip Live→flip Replay で **Replay profile が復元**／save→新 instance load→現 mode profile 復元、を非トートロジー assert。
- **回帰**: `HakoniwaChartTileProbe`（#60）／`HakoniwaBaseModeProbe`（#61 Section1-5）／`HakoniwaProbe`（#14）／`ReplayLayoutProbe`（#12 schema・null≈empty coalesce で round-trip 不変）／`BackcastWorkspaceProbe`（Section1-10）GREEN 継続。

### owner-run HITL

- Replay で tile を swap→footer で Live へ→Live で別配置に swap→Replay へ戻すと **Replay の配置が復元**・Live へ再切替で **Live の配置が復元**。File Save→再 Play で現 mode の配置が復元。旧 sidecar（per-mode 無し）からの起動で crash せず canonical/honor が正しく立ち上がる。

## 8. AC 達成方針

- **AC1（Replay/Live で別 tile 並び順・mode 切替で復元）**: AFK = §7 (a)(c) + `HakoniwaBaseModeProbe.Section4` valid honor + `BackcastWorkspaceProbe` flip 統合。HITL = swap→flip→復元。
- **AC2（disk スキーマ additive・forward-compat read）**: AFK = §7 (d)（seed 非空 + round-trip・disk テキスト実在）。
- **AC3（LiveManual/LiveAuto は同一 Live profile）**: `from_mode`/`IsLiveShape` の 2 値畳み込み（#61 Section1/Section3 回帰 + 本 slice の profile キーが bool live）。
- **AC4（AFK + HITL + 回帰）**: §7 全項目。

## 9. 関連・正本

- 移植元: TTWR `src/ui/hakoniwa.rs`（`HakoniwaLayoutProfile::from_mode`/`HakoniwaLayoutProfiles`/`HakoniwaGridSnapshot::is_valid_for`/`build_hakoniwa_snapshot`/`apply_hakoniwa_snapshot_to_profiles`/`apply_hakoniwa_restore_resources`/`reconcile_hakoniwa_tiles`）, `src/ui/layout_persistence/{restore,sidecar}.rs`, ADR 0013/0016。
- backcast: CONTEXT.md「per-mode layout profile」（本 slice で追加）／「mode-conditional base tile / base retile」／「chart tile family / base tile」／「tile / slot / tile swap」。findings 0028（mode-conditional base・前段）/ 0027（chart tile family）/ 0026（footer mode）/ 0025（workspace root）/ 0004（layout schema）。

## 10. 実装証跡（#62・2026-06-17・AFK GREEN）

実装ファイル:
- 新規: `Assets/Scripts/Hakoniwa/HakoniwaLayoutProfiles.cs`（pure・UnityEngine-free: `HakoniwaProfile{panels}` / `HakoniwaLayoutProfiles{replay,live}` DTO ＋ `Get`/`Set`/`HasAny`/`IsValidForMode`/`BaseOrderForMode`/`SeedFromLegacy`/`Clone`/`FromDocument`＝TTWR `HakoniwaLayoutProfiles`/`is_valid_for`/`apply_hakoniwa_restore_resources` parity）、
  `Assets/Editor/HakoniwaProfileProbe.cs`（新 AFK 必須ゲート・pure 4 ケース）。
- 変更: `LayoutDocument`（`hakoniwaProfiles` field additive・`Clone`・`StructurallyEqual` に per-mode 比較＝null≈empty coalesce）、
  `LayoutStore`（`NormalizeHakoniwaProfiles`・各 profile panels の null/empty-id/null-rect drop）、
  `BackcastWorkspaceRoot`（`_profiles` field・`CaptureLayout` で active stash＋`hakoniwaProfiles` 出力＋`panels` ミラー・`ApplyLayout` で `FromDocument`＋`ApplyProfileOrder`・`SyncBaseTilesToMode` で flip stash＋`ApplyProfileOrder`・`ReassertBaseAfterRestore`→`ApplyProfileOrder` に一般化）、
  `HakoniwaBaseModeProbe.Section4`（`ReassertBaseAfterRestore`→`ApplyProfileOrder` 経由の衝突回帰＋valid-honor 新ケース (d)）、
  `BackcastWorkspaceProbe.Section11`（flip→restore per-mode 統合冒煙＋実 `CaptureLayout`/`ApplyLayout` の disk 往復）。

AFK GREEN（Unity 6000.4.11f1 `-batchmode -nographics`・直列実行・全 `UNITY_EXIT=0`・CS エラー無し）:
- **`HakoniwaProfileProbe`**（新・必須）: (a) valid profile が base swap を mode 別に honor／(b) Default()・#60 世代・集合過不足・empty → canonical fallback（#61 衝突安全の strict superset）／(c) chart id は validity に混じらず base prefix から除外・stored chart 順保持／(d) 旧 panels-only doc の両 profile 非空 seed（forward-compat）＋`hakoniwaProfiles` 込み doc の `LayoutStore` save→load→`StructurallyEqual`・disk テキストに `replay`/`live`/per-mode slot 実在・非空転（Replay≠Live）→ `[HAKONIWA PROFILE PASS]`。
- **`HakoniwaBaseModeProbe`**（#61・Section1-5＋更新 Section4）: 実 root で legacy seed→`ApplyProfileOrder`→canonical（衝突回帰）＋valid profile の base swap honor → `[HAKONIWA BASE MODE PASS]`。
- **`BackcastWorkspaceProbe`**（+Section11）: Replay で swap→Live で別 swap→Replay 戻りで Replay 配置復元→Live 再切替で Live 配置独立復元→実 `CaptureLayout`+`LayoutStore` 往復+`ApplyLayout` で現 mode profile 復元 → `[BACKCAST WORKSPACE PASS]`。
- **回帰 GREEN**: `HakoniwaChartTileProbe`（#60）／`HakoniwaProbe`（#14）／`ReplayLayoutProbe`（#12 schema・`hakoniwaProfiles` 追加後も null≈empty coalesce で round-trip 不変）全 PASS。

**owner-run HITL（pending）**: Replay で tile swap→footer で Live→Live で別配置に swap→Replay 戻りで Replay 配置復元・Live 再切替で Live 配置復元。File Save→再 Play で現 mode 配置復元。旧 sidecar（per-mode 無し）起動で crash せず canonical/honor が立ち上がる。**owner 実機 Play で確認待ち。**
