# Hakoniwa chart tile family Findings: universe 登録銘柄ごとに chart tile を spawn する（TTWR `hakoniwa_chart_tile_sync_system` parity）

- 受け皿 issue: （新規・本 findings で起票）「動的 N チャート」＝ findings 0007 §9 が deferred した「chart N 枚（銘柄別）」の受け皿。親 #1 (Epic) / #5 (Step3 cutover)。
- 方針 ADR（**変更しない**・参照のみ）: [ADR-0005 — 1:1 surface parity](../adr/0005-cutover-scope-1to1-surface-parity-with-ttwr-ui.md)（accepted・自己保護節あり）, [ADR-0003 — layout persistence capability parity](../adr/0003-layout-persistence-capability-parity.md)（accepted・自己保護節あり）, [ADR-0001](../adr/0001-unity-pythonnet-embedded-frontend.md)。
- 配置の根拠: ADR-0005/0003 自己保護節（下位事実は ADR に書き戻さず本 findings に記録し ADR を「方針: ADR-0005/0003」として参照）。本決定は ADR-0005（surface 網羅）を**満たし**、ADR-0003（capability parity・形式非互換）の枠内なので **新規 ADR も amend も起こさない**。
- 先行: #14（Hakoniwa split-grid / findings 0007）, #59（workspace root / findings 0025・**§12 で universe 共有 SoT 配線済み**）, #48（DuckDB 分足＋複数銘柄 universe 時刻順マージ / findings 0018）, #53（ChartView 抽出 / findings 0023）。
- 設計確定: `grill-with-docs`（2026-06-16・owner インタビュー）。**本 findings は設計のみ・実装前**（owner は起票＋findings まで、実装は別途判断）。

> **状態: 設計確定（未実装）。** grill で全分岐を lock。実装着手時に本 findings の §11 に証跡を追記する。

---

## 0. スコープと段階づけ（owner 確定 2026-06-16）

TTWR の「universe 登録銘柄数だけ chart tile が spawn する」機能を backcast へ移植する。grill で、owner の補足「base tile は TTWR と同じくモード別」を起点に、これが **4 つの独立 capability** に分解されることを確認し、TTWR 自身の ADR 境界（chart=ADR 0011+#169、mode-conditional=ADR 0013、box 永続化=ADR 0015/0016）に沿って **4-stage** に分けた。findings 0007 §9 が**まとめて先送りした 3 項目**（per-mode profiles / multi-instrument / chart N 枚）を一度に開けないための段階づけ。

| stage | 内容 | 主な所有者 / 根拠 | 本 findings |
|---|---|---|---|
| **① C（本 findings）** | 動的 N チャート（universe と常時同期 spawn/despawn）＋ derived box-grow（persist 無し） | `InstrumentRegistry`・ADR 0011 Update+#169 | ◯ |
| **② B+M** | base パネル（BuyingPower/Orders/Positions/RunResult）のタイル化 ＋ モード別 base（Replay=5/Live=4・retile） | `ExecutionMode`・ADR 0013 | 別 slice |
| **③ P** | per-mode profile（Replay/Live で別レイアウト保存） | ADR 0013/0016 | 別 slice |
| **④** | box 位置/サイズ永続化 ＋ drag-handle 移動/リサイズ | ADR 0015/0016 | 別 slice |

### 採用 / 不採用（stage ① C）

- **採用**: chart を「固定 1 枚」から **universe 銘柄ごとの動的 tile 集合**へ拡張 / id = `chart:<instrument-id>` / メンバーシップ正本 = universe（`InstrumentRegistry`）/ `Changed` で即時 spawn/despawn / slot 順だけ persist（スキーマ追加 0）/ box は n から決定的に grow（persist 無し）/ AFK 権威ゲート + HITL。
- **不採用（= C 外・後続 stage の additive 拡張）**:
  - **モード別 base tile**（Replay=5/Live=4・retile）＋ base パネルのタイル化 → ② B+M。C の base は `[startup]` のみ（mode 不問）。
  - **per-mode profile**（Replay/Live で別レイアウト）→ ③ P。C は単一共有レイアウト。
  - **box 位置/サイズの永続化 ＋ drag-handle 移動/リサイズ**（ADR 0015/0016・findings 0007 §0/§9 deferred）→ ④。C の box-grow は **derived（persist しない）**。

## 1. モデル（owner 確定 2026-06-16）

```
order = [startup, chart:<id1>, chart:<id2>, ...]   # base tile が前・chart tile が後
grid  = ceil(√n) 等分（HakoniwaGridMath.CellRects・既存）
```

- **chart tile family**: chart tile の id は `chart:<instrument-id>`。固定 `"chart"` は廃止し N 枚に置換。
- **メンバーシップの正本 = universe（`InstrumentRegistry`）**。「どの chart が存在するか」は universe が決め、layout doc ではない。
- **base tile**（C では `[startup]` のみ）は chart tile の前に並ぶ。空 universe → `[startup]` のみ（TTWR の n=0 と同じ＝chart 0 枚）。
- TTWR `hakoniwa_chart_tile_sync_system`（ADR 0011 Update／#169 で base/chart の所有権分離）の capability parity。

## 2. 同期（owner 確定 2026-06-16）— 常に universe と同期

- `InstrumentRegistry.Changed`（findings 0025 §12 で配線済み・実変化時のみ発火）を購読し、**即時** spawn/despawn する。sidebar で銘柄を add した瞬間に chart tile が出る（次の Run まで warming-up/空）、remove で despawn。Run 待ちで遅延させない（TTWR と同挙動）。
- C は単一言語 C#・**engine 変更ゼロ**。Replay は universe（= `scenario.instruments`）が run を駆動し、chart は state からの純読み取り。per-chart の subscribe は不要（subscribe が要るのは Live で ② B+M 以降）。

## 3. データ配線（owner 確定 2026-06-16）— per-instrument OHLC は既に配信済み

- **サーバ側の土台は実装済み**: `python/engine/reducer.py:103` が銘柄ごとに `per_id_ohlc_points[iid]` を蓄積（全 real 銘柄・primary 問わず）。`core.py:463-498` が `get_state_json()` の `per_instrument[id].ohlc_points` として配信（`models.py:50/83`）、unsubscribe で破棄（`core.py:461`）。aggregate な top-level `ohlc_points` は **primary 銘柄のみ**（`reducer.py:89-100` の `is_primary` ゲート）で、現行の単一 ChartView はこれを描画している。
- **C# 側の gap のみ埋める（grill round2 Q2・共有ロケータ抽出）**: `Assets/Scripts/Live/DepthDecoder.cs` の private 構造認識スキャナ（escape-aware・decoy 文字列耐性・malformed→`FormatException`）と「`per_instrument → id → <member>` の生 value span を返す」汎用ロケータを **共有ユニット `PerInstrumentJsonLocator` に昇格**（mirror=複製ではなく抽出＝drift ゼロの終端形）。object/array 要求は呼び出し側に残す（depth=`RequireObjectStart` / ohlc_points=`RequireArrayStart`）。`DepthDecoder` を共有ロケータへ載せ替え（behavior-preserving）→ **`DepthDecodeProbe`(#26) を回帰ゲート**に再実行 GREEN。新 `InstrumentOhlcDecoder.Decode(json, id)` は located な top-level 配列を `{"ohlc_points":<span>}` でラップし固定形状 `OhlcPoint[]`（既存型再利用）を JsonUtility に渡す。CONTEXT.md「Avoid: per_instrument 全体を JsonUtility」を遵守。
- 各 chart tile は本番 `ChartView`（#53・findings 0023）で自銘柄を描画。将来 #55/#56（volume/crosshair・chart chrome）の成果を各 chart が継承する。aggregate `ohlc_points`(primary) は後方互換で残置。
- **#48 の複数銘柄 universe・時刻順マージ**により、run は universe 全銘柄の足を流し `per_id` が全銘柄ぶん貯まる。市場休止日 OHLCV=0 の crash（#58 OPEN）は直交課題。

## 4. グリッド（owner 確定 2026-06-16）— derived box-grow（persist しない）

- TTWR `compute_hakoniwa_box_size(n, min_tile, drag_height, default)` を pure 関数として port（`HakoniwaGridMath` に additive・`CellRects` と同じく AFK probe で検証可）。`box = max(default, cols×min_tile.x, rows×min_tile.y+drag_height)`、`cols=ceil(√n)`。
- **min tile = (280,180)**（TTWR `HAKONIWA_BOX_GROW_MIN_TILE_SIZE`。divider clamp の (320,150) とは別定数）。default = (700,450)（TTWR `HAKONIWA_DEFAULT_SIZE`）。
- **box 位置は固定・box サイズだけ universe 数から毎回再計算**（復元時も universe から導出するので安定）。TTWR が `HakoniwaSnapshot` に box_position/box_size を persist するのは **drag-handle で箱を動かせる（ADR 0016）から**であり、backcast はそれを ④ に deferred したので C では persist 不要。N が多くてもタイルは min size を保つ（固定 box だと 10 銘柄で ~175×100px となり実用性が落ちるため derived-grow を C に含める）。

## 5. 永続化（owner 確定 2026-06-16）— スキーマ追加 0

- **正本は slot 順だけ**。メンバー（どの chart が存在するか）は universe から導出する。
- 既存 `HakoniwaController.DeriveOrder`/`NormalizeOrder` の tolerance が**動的メンバーをそのまま処理**: doc にあって live(universe) に無い id は skip、live にあって doc に無い id は末尾へ append。旧 `"chart"` 固定 id を持つ doc も skip で**後方互換**。
- `#12` の `LayoutDocument`/`PanelLayout`（{id, slot, visible, rect}）を**そのまま再利用**（スキーマ追加 0）。rect は派生 snapshot（findings 0007 §3 の slot 正本/rect 派生を踏襲）。

## 6. durable 改造（owner 確定 2026-06-16）— C の本丸

- **唯一の本質的な durable 変更は `HakoniwaController` の実行時 tile add/remove 対応**。現状は構築時に静的 `tilesById` を受け取り runtime add/remove 経路が無い（`HakoniwaController.cs:33-41`・`BackcastWorkspaceRoot.cs:165` で `{startup, chart}` を構築時固定）。`AddTile(id, rt)` / `RemoveTile(id)` を足し、`_tilesById` と `_order` を変異させて `Rebuild` する。
- **grill round2 Q1（box-grow 所有 = membership orchestrator・Rebuild は box-size-free 維持）**: §0 の TTWR 構造どおり、`Rebuild`（= swap のたびに走る relayout 経路）は **`_root.sizeDelta` に触らない**（既存の "Rebuild は anchorMin/Max のみ・NEVER reads input/disk" 不変条件を守る）。box-grow（`_hakoniwaRoot.sizeDelta = HakoniwaGridMath.ComputeBoxSize(n, (280,180), dragHeight, (700,450))`）は **`BackcastWorkspaceRoot`（membership orchestrator）が spawn/despawn 直後に適用**する。**#60 は box drag handle が無いので `dragHeight=0`**（#63 が box drag handle＋content inset 導入時に実 height を供給）。これにより #62/#63 は box owner を additive 拡張するだけで Controller 改修ゼロ。
- `BackcastWorkspaceRoot` が `InstrumentRegistry.Changed` を購読し、chart tile chrome（`BuildTileChrome` 再利用・header drag swap）と `ChartView` を spawn/despawn して controller に add/remove を伝え、box-grow を適用する。teardown（`StopAndDispose`）で購読解除（orphan ハンドラなし・findings 0025 §12 の `ScenarioStartupTile` 同様）。

## 7. 射程外（C に含めない）

- モード別 base tile ＋ base パネルのタイル化（② B+M）／per-mode profile（③ P）／box 位置・サイズの永続化＋drag-handle 移動/リサイズ（④・ADR 0015/0016）。
- per-chart の Live subscribe（Live は ② 以降）。市場休止日 OHLCV=0 の Replay crash（#58）。chart chrome / volume / crosshair（#55/#56・各 chart が継承）。

## 8. 検証サーフェス（owner 確定 2026-06-16・backcast に FLOWS.md は無く AFK probe が正本ゲート）

### AFK probe（headless・Python-free・batchmode 可）

1. **box-grow 算術**: `compute_hakoniwa_box_size` が n に対し TTWR と一致（n=0→default、min tile 280×180 を下回らない、default を下回らない）。`CellRects`/`GridDims` の既存被覆・非重複は回帰。
2. **spawn/despawn on Changed（非トートロジー）**: `InstrumentRegistry` に add → chart:<id> tile が controller の order に出現し RectTransform が新 cell へ。remove → despawn。registry と controller の order が一致。
3. **per_id デコード**: `per_instrument[id].ohlc_points` を持つ実 state JSON から id ごとに正しい系列を抽出（dict 内 decoy 文字列に騙されない・depth ロケータの characterization に倣う）。absent id / per_instrument 欠落 → 空フレーム（no throw）。
4. **動的 id の slot 非空転 round-trip**: chart 複数を swap → Capture → save → 新規 instance load → slot 順生存 ∧ disk テキストに `chart:<id>` の slot 値が実在 ∧ universe から欠けた doc id は skip。
5. **#14/#59 回帰**: `HakoniwaProbe` / `BackcastWorkspaceProbe` / `ReplayLayoutProbe` ほか GREEN 継続。

### owner-run HITL

- sidebar で銘柄 add/remove → chart tile が即 spawn/despawn・grid が再レイアウト・box が grow。Run で各 chart に自銘柄の bar streaming。zoom 倍率を変えた状態の chart tile swap（findings 0025 §3 の懸念）。Save→再 Play で chart の並び順が復元（universe から membership 再導出）。

## 9. AC 達成方針（実装後に証跡を追記）

- **AC1（universe 銘柄ごとに chart tile が spawn）**: AFK = §8 S2（Changed→spawn/despawn・order 一致）。HITL = sidebar add/remove で即時反映。
- **AC2（N に応じた grid とサイズ）**: AFK = §8 S1（box-grow 算術・CellRects 被覆）。HITL = box grow 目視。
- **AC3（各 chart が自銘柄を描画）**: AFK = §8 S3（per_id デコード）。HITL = Run で各 chart に別系列。
- **AC4（並び順 persist・membership は universe 由来）**: AFK = §8 S4（動的 id round-trip・skip/ append tolerance）。

## 10. 関連・正本

- 移植元: TTWR `src/ui/hakoniwa.rs`（`hakoniwa_chart_tile_sync_system` / `compute_hakoniwa_box_size` / `hakoniwa_tile_kinds`）, ADR 0011 Update / 0013 / 0015 / 0016 / #169。
- backcast: CONTEXT.md「chart tile family / base tile」（本 slice で追加）/「tile / slot / tile swap」/「Hakoniwa」。findings 0007（split-grid）/ 0018（多銘柄 reader）/ 0023（ChartView）/ 0025（workspace root・§12 共有 universe SoT）。
- 重複調査（全 59 issue 本文走査・2026-06-16）: C / B+M / P / ④ いずれも既存 issue と重複なし。#55/#56 は単一 chart の装飾（枚数は対象外・各 chart が継承）、#11 は OnGUI panel（タイル化でない）、#59 は中央 chart 1 枚。

## 11. 実装証跡（stage① #60・2026-06-16・RED→GREEN）

実装ファイル:
- 新規: `Assets/Scripts/Live/PerInstrumentJsonLocator.cs`（DepthDecoder から抽出した共有構造認識ロケータ）、
  `Assets/Scripts/ReplayChart/InstrumentOhlcDecoder.cs`（per-id `ohlc_points` decode・`OhlcPoint[]` 再利用）、
  `Assets/Editor/HakoniwaChartTileProbe.cs`（新 AFK 必須ゲート）。
- 変更: `HakoniwaGridMath`（`ComputeBoxSize` pure fn 追加）、`HakoniwaController`（`AddTile`/`RemoveTile`・Rebuild は box-size-free 維持）、
  `DepthDecoder`（共有ロケータへ載せ替え・private スキャナ削除＝behavior-preserving）、
  `BackcastWorkspaceRoot`（membership orchestrator: `SyncChartTilesToUniverse`/`SpawnChartTile`/`DespawnChartTile`/`ApplyBoxGrow`・
  固定 chart 廃止・base=[startup]・per-id render・`Universe.Changed` 購読/teardown 解除）、
  `BackcastWorkspaceProbe`（Section10 chart tile family）。

AFK GREEN（Unity 6000.4.11f1 `-batchmode -nographics`・grill round2 の box-size-free / 共有ロケータ込み）:
- **`HakoniwaChartTileProbe`**（新・必須）: ① box-grow 算術（n=0 default・min floor・n=6→840×450・n=12→1120×540・dragHeight 加算）
  ② per-id ohlc decode（2 銘柄の別系列・string decoy 無視・null/absent→空・malformed→`FormatException`）
  ③ 動的 add/remove＋slot 非空転 round-trip（chart:<id> の disk 往復・stale `chart` skip・doc-absent live id append）→ `[HAKONIWA CHART TILE PASS]`。
- **`BackcastWorkspaceProbe.Section10`**（新）: 実 root を headless 合成し、`Universe.ReplaceAll`/`Remove` で chart:<id> が spawn/despawn・
  base=[startup]・旧 `chart` 退役・box-grow が `ComputeBoxSize(n)` 一致を assert。Section8/9 も回帰なし。
- **回帰 GREEN**: `DepthDecodeProbe`（共有ロケータ抽出の behavior-preserving＝#26 回帰ゲート）／`HakoniwaProbe`／`ScenarioStartupProbe`／
  `ReplayLayoutProbe`／`UniverseSidebarProbe`／`FloatingWindowProbe`／`InfiniteCanvasProbe`／`StrategyEditorProbe` 全 PASS。

**owner-run HITL（2026-06-16・全 pass）**: CP0（起動時に銘柄ごと chart tile）・CP1（sidebar add → chart tile 即 spawn・grid 再レイアウト・box grow）・
CP2（sidebar × → 即 despawn・box 縮小）・CP3（Run で各 chart が自銘柄の別系列を streaming＝per-id 描画実証・日付はデータ範囲内 2025-11-27〜2026-02-27）・
CP4（zoom 下の header-drag swap 成立）・CP5（swap→File Save→再 Play で並び順復元・membership は universe 再導出）・CP6（teardown clean・購読解除）を
owner が実機 Play で確認。**stage① #60 受入完了**。次は stage②（#61・モード別 base ＋ base パネルのタイル化）。
