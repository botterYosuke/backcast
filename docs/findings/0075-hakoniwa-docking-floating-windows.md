# findings 0075 — Hakoniwa ドッキング化（split-grid → 独立 floating window＋磁石スナップ）

方針: **ADR-0017**（Hakoniwa = ドッキング可能な独立 floating window 群）。本 findings はその下位設計の木を固定する。
grill: `grill-with-docs`（2026-06-21・owner HITL）。supersede: findings 0007（split-grid）。再利用: findings 0008（floating window seam）。

> **SUPERSEDED（深さプレーン分離・またぎ吸着禁止／#103・ADR-0018・2026-06-21）**: ADR-0017 Decision 1 の
> 「全パネルを 1 枚の `FloatingWindowLayer`（1.2倍）に合流」と単一クラスタ cross-snap は **#103 が 2 点で覆す**。
> 元箱庭 6 種は新 **`DockLayer`（Content の子・1.0倍・奥・背面 sibling）** へ、`strategy_editor`/`order` は
> `FloatingWindowLayer`（1.2倍・手前）に残す。吸着は **同一プレーン内のみ**（controller がプレーンごとに分かれる結果、
> またぎ吸着は構造的に起きない）。下位事実は本 findings **§10** に固定。§3/§4/§6/§9 の「全部 `FloatingWindowLayer`」
> 前提の記述は dock 6 種について §10 に読み替える（base/chart の seam・既定配置・永続スキーマ自体は不変）。

## §0 owner が選んだ 4 つの分岐（HITL）

1. **Dock モデル = 独立ウィンドウ＋磁石スナップ**（枠内タイリングでも結合でもない）。
2. **置き場 = 既存 FloatingWindow に合流・grid 退役**（HakoniwaController/GridMath/box-grow を削除）。
3. **くっつき方 = 磁石スナップ・結合なし・リリース整列・各々独立**（隣を動かしても付いてこない／永続はグループ概念なし）。
4. **mode 別配置 = 単一共有に簡素化（drop）**（HakoniwaLayoutProfiles 削除・mode 差は startup の show/hide のみ）。
5. **既存保存レイアウト = デフォルト配置にリセット**（旧 panels/hakoniwaProfiles は読まない dead schema）。

## §1 snap 意味論（磁石スナップ・結合なし）

- **トリガ**: title-bar drag の **リリース時**（`OnEndDrag`）。drag 中は #15 のとおり `MoveByLogical` で自由追従、
  離した瞬間に 1 回だけ整列。live preview のガイド線は射程外（後続 additive）。
- **算出は pure**: `FloatingWindowMath` に `SnapOffset(draggedRect, otherRects, threshold)` を新設（headless・AFK 権威）。
  controller/title-input は RectTransform から rect を読んで渡し、戻りオフセットを `anchoredPosition` に加えるだけ。
- **揃える対象**: ① **flush 隣接**（dragged の右辺↔他の左辺、上辺↔下辺 など接触させる）＋② **同辺整列**
  （左辺↔左辺・上辺↔上辺 など並びを揃える）。x と y は**独立**に最近傍へ吸着（x は A に、y は B に揃ってよい）。
- **閾値**: canvas 論理 px の定数（推奨初期値 **12px**）。zoom 非依存（drag は既に論理座標）。
- **非対象**: resize 連動なし／隣の押し出しなし／グループ化なし／detach 状態なし。**隙間・重なりは許容**。
- **永続**: snap は関係を保存しない。結果の `x/y` が `floatingWindows` に乗るだけ（**スキーマ追加 0**）。

## §2 catalog kind の追加

`FloatingWindowCatalog.Default()` に base/chart の kind を追加（#15 は `strategy_editor`/`order` のみ）:
`chart`・`buying_power`・`orders`・`positions`・`run_result`・`startup`(scenario)。
- `chart` は **multi-instance**（id `chart:<instrument-id>`・kind は単一）＝既存 `strategy_editor` と同型。
- それ以外は概念上 **singleton**（1 枚）。
- unknown-kind tolerance（findings 0008 §3）は不変：旧 build が新 kind を読めなくても doc に温存。

## §3 membership の所有・actuation 付け替え

> **SUPERSEDED（chart の spawn rect のみ・#101 / findings 0078）**: 下記「初回はデフォルト配置（`ComputeRects(N)`）」は
> chart について **#101 が supersede**。chart は **spec 固定サイズ（520×360・総数 N 非依存）で spawn し、フォーカス窓の辺へ
> flush 吸着**（`FloatingWindowController.SpawnDockedToFocus` + `DockSnapPlacement`）。§9 実装着地の
> `SpawnChartWindow(iid, rect)`（rect = `ComputeRects(N)[i]`）も同様に supersede。base 5 枚・既存保存 geometry は不変。

`BackcastWorkspaceRoot` が membership を所有する構造は不変。actuation だけ HakoniwaController → FloatingWindowController へ:
- **chart universe 同期**: `SyncChartTilesToUniverse` を `_hako.AddTile/RemoveTile` → `_windows.Spawn/Close` に。
  banner: 銘柄 add で chart window spawn（初回はデフォルト配置・以後は保存座標）、remove で Close。membership 正本は
  `InstrumentRegistry`（#60 不変・doc は並び/座標のミラー）。
- **startup show/hide（base retile の縮退）**: mode poll（`IsLiveShape(DisplayMode)`）が Replay→startup window を Show、
  Live→Hide。**despawn/respawn ではなく可視性トグル**（#15 `Show`/`Hide` を流用・dormant 温存）。chart は mode をまたいで
  identity 保持（spawn したまま）。

## §4 デフォルト配置（初回起動・リセット時）

> **SUPERSEDED（chart のみ・#101 / findings 0078）**: 下記グリッド初期配置は **base 5 枚の初回配置にのみ**適用される。
> chart は `[+Add]`/universe add のたびに **spec 固定サイズ（520×360）＋フォーカス窓への flush 吸着**（`DockSnapPlacement`）で
> spawn し、`ComputeRects(N)` のグリッドマスを使わない（#99 では総数 N でサイズが変動するバグだった）。base 5 枚は不変。

保存が無い／旧 schema のみのとき、base＋chart を **grid 風の初期タイル配置**で spawn する（いきなり対角カスケードだと
多窓で乱雑なため）。pure helper（推奨 `DockDefaultPlacement` ＝ headless・AFK 権威）が n 窓の絶対 canvas 論理 rect を返す
（旧 `HakoniwaGridMath` の grid-dims 発想を**配置初期値**として転生・live レイアウトの正本ではない）。spawn 後は自由移動・snap 可。

## §5 退役一覧（削除）

`HakoniwaController.cs`／`HakoniwaGridMath.cs`／`HakoniwaLayoutProfiles.cs`／`HakoniwaTileHeaderInput.cs`／
`HakoniwaBaseTiles.cs`（startup-only 判定だけ `IsLiveShape`/`IsChartId` を小ヘルパへ残すか floating 側へ移管）／
`BackcastWorkspaceRoot` の box-grow（`ComputeBoxSize`）と `_profiles`／HakoniwaRoot 関連の chrome paint。
`LayoutDocument.panels`/`hakoniwaProfiles` は **read を Hakoniwa から外す**（schema は forward-tolerance で残置）。

## §6 永続化（ADR-0003 枠内）

base/chart は `floatingWindows`（x/y/w/h/zOrder/visible）で round-trip。`startup` は visible=false でも **registered のまま
hidden**（mode で復帰）。capture/apply は #15 `FloatingWindowController.Capture/Apply` をそのまま使う（full-replacement・
unknown-kind skip）。**旧 panels/hakoniwaProfiles は無視**（migrate しない・owner 決定）。

## §7 AFK 正本（実装前に `behavior-to-e2e` を formal invoke）

- 新設: **snap pure probe**（`FloatingWindowMath.SnapOffset` の flush/同辺/閾値外/x-y 独立を headless 検証）。
- rewrite/retire: `HakoniwaE2ERunner`（grid/swap 前提）・`ReplayToHakoniwaE2ERunner`（grid 前提）を新モデルへ。
- 維持/拡張: `FloatingWindowE2ERunner` に base/chart kind の spawn・startup show/hide・デフォルト配置・persist round-trip を追加。
- content rehome（tile content → window body）は最重量。実装は `parallel-agent-dev`/`pair-relay` で多ファイル横断。

## §8 未決の低リスク項目（実装時に確定）

- snap 閾値の最終値（初期 12px）／同辺整列を corner まで広げるか／snap 中の視覚ガイド（後続 additive）。
- `startup` window の close 可否（mode 所有なので close 不可＝visibility のみが妥当）。
- content rehome の title bar 表示文字・accent（theme PlayerColors 既存規約に従う）。

## §9 実装着地（2026-06-21）

設計の木（§1〜§7）を以下のコード seam に固定した。slice 単位の実装でなく、設計上 binding な事実のみ記録：

- **snap 算術 (§1)**: `FloatingWindowMath.SnapOffset(DockRect dragged, IList<DockRect> others, float threshold)` ＋ `DockRect{topLeft,size}` 補助型。`FloatingWindowController.SnapOnRelease(id [,threshold])` ／ `FloatingWindowTitleInput.OnEndDrag` で配線。閾値定数 `FloatingWindowController.DEFAULT_SNAP_THRESHOLD = 12f`。AFK gate: `FloatingWindowE2ERunner` S10（pure 算術: flush/同辺/閾値外/x-y 独立/threshold≤0 guard/nearest-wins）＋ S11（controller wiring: 自分は除外・hidden 無視・dragged のみ移動 = group なし）。
- **kind 拡張 (§2)**: `FloatingWindowCatalog` に `KIND_CHART` / `KIND_BUYING_POWER` / `KIND_ORDERS` / `KIND_POSITIONS` / `KIND_RUN_RESULT` / `KIND_STARTUP` の 6 種。accent は PlayerColors の 8 スロットを使い切る配色。`closeable=false`（workspace 所有 = ユーザの X で消失しない）。chart のみ multi-instance（id `chart:<iid>`）、他は singleton（id = kind verbatim）。
- **初回配置 (§4)**: `DockDefaultPlacement` (pure helper)。`ComputeRects(n, anchor, box, gap)` が grid-style 絶対 canvas 論理 rect 列を返す。Default は 1200×640 box・gap 12px・anchor は box を canvas 原点に centred したときの top-left = (-600, 320)。AFK gate: `FloatingWindowE2ERunner` S13。
- **frame chrome (§7)**: `DockWindowFrame.Build(id, title, accent, font, …)` — 6 dock kind 用の共通 frame builder（StrategyEditorWindowFrame / OrderTicketWindowFrame と並ぶ第三者）。title bar 色は spec accent。
- **dispatch (§3/§7)**: `BackcastWorkspaceRoot.BuildWindowFrame(spec, id)`（旧 `BuildEditorWindowFrame` の昇格名）が strategy_editor / order / dock kinds / strategy_editor cell を全て分岐。dock kind は frame＋body content（`BuildDockContent`）を SPAWN 中に一括注入。
- **chart family の universe 連動 (§3)**: `_scenario.Universe.Changed → SyncChartWindowsToUniverse()`（旧 `SyncChartTilesToUniverse` の置換）。orphan は `DespawnChartWindow(iid)`（`_windows.Close + dict 掃除`）、欠落は `SpawnChartWindow(iid, rect)`（rect は `DockDefaultPlacement.ComputeRects(N)[i]`）。
- **startup show/hide (§3)**: `SyncStartupVisibilityToMode(bool live)` — `_windows.Hide("startup")` ／ `Show("startup")` だけ（despawn しない）。DriveFooter で `live != _lastLiveShape` のとき 1 回。
- **永続化 (§6)**: `CaptureLayout` は `panels = empty list`／`hakoniwaProfiles = null` を書く（dead schema 温存・forward-tolerance）。`floatingWindows` のみ正本。`ApplyLayout` は canvas → `RestoreFloating` の 2 段（per-mode profile 読みは削除）。pre-#99 doc の `panels`／`hakoniwaProfiles` は読まない＝デフォルト配置にリセット（ADR-0017 §6）。
- **退役 (§5)**: `Assets/Scripts/Hakoniwa/HakoniwaController.cs` ／ `HakoniwaGridMath.cs` ／ `HakoniwaTileHeaderInput.cs` ／ `HakoniwaBaseTiles.cs` ／ `HakoniwaHitlHarness.cs` ／ `Assets/Editor/HakoniwaHitlMenu.cs` ／ `HakoniwaBaseModeProbe.cs` ／ `HakoniwaChartTileProbe.cs` を削除。`HakoniwaLayoutProfiles.cs` は `HakoniwaProfile`／`HakoniwaLayoutProfiles` の [Serializable] POCO のみに減量（logic 全削除、`HakoniwaBaseTiles` 依存も消失）— `LayoutDocument` の forward-tolerance のためだけに温存。`BackcastWorkspaceSceneBuilder` の `HakoniwaRoot` ／ 5 タイル GameObject 生成と serialized 参照配線を削除（scene 再ビルドが必要）。
- **E2E 正本の入替**: 旧 `HakoniwaE2ERunner.cs` / `ReplayToHakoniwaE2ERunner.cs`（split-grid / box-grow 前提）を削除。`FloatingWindowE2ERunner` を Section10/11（snap）＋ Section12/13（dock catalog & placement）で拡張し新 SoT 化。`ThemeProbe.Section5_ChromeSubscriptionKill` は Hakoniwa chrome の subscription 試験だったので削除（ChartView / DepthLadderView は個別 probe で theme 追従を保証）。
- **DockShape helper**: `Assets/Scripts/FloatingWindow/DockShape.cs` に `IsLiveShape(displayMode)` / `IsChartId(id)` / `ChartId(iid)` / `InstrumentOfChartId(id)` を集約（旧 `HakoniwaBaseTiles.IsLiveShape` / `IsChartId` の移管先）。production code（`BackcastWorkspaceRoot`）は両方を `DockShape` から呼ぶ。

## §10 深さプレーン分離・またぎ吸着禁止（2026-06-21）

方針: **ADR-0018**（ドックの奥行きは 2 つの深さプレーンで出す／プレーンをまたぐ吸着は禁止）。issue #103。ADR-0017
Decision 1 と単一クラスタ cross-snap を 2 点だけ supersede（他は §1〜§9 のまま不変）。grill: `grill-with-docs`
（2026-06-21・owner HITL）。AFK: 実装着手前に `behavior-to-e2e` を formal invoke 済み。

- **2 プレーン（§4 の「全部 FloatingWindowLayer」を覆す）**: 奥 = 新 `DockLayer`（`Content` 直子・**1.0倍**＝
  パララックスなし・`FloatingWindowLayer` より**前の sibling**＝背面・identity 全面）に元箱庭 6 種（chart / orders /
  positions / run_result / buying_power / startup）。手前 = `FloatingWindowLayer`（**1.2倍**）に `strategy_editor`（セル）
  ＋ `order`。パンの速度差（1.0 vs 1.2）が奥行き＝findings 0006 §2 の `CanvasViewMath.ParallaxLayerOffset(v, factor)`
  をそのまま再利用（**新規パララックス機構なし**。`DockLayer` は factor 1.0 ＝ offset 0 なので controller 配線不要・
  Content の子として乗るだけ）。
- **2 controller（seam は ADR-0017 のまま 1 種類）**: `BackcastWorkspaceRoot._windows`（floating layer・order/editor）に
  加え `_dockWindows = new FloatingWindowController(_dockLayer, _catalog, BuildDockWindowFrame)`。catalog は共有・layer
  だけ別。dock factory `BuildDockWindowFrame` は dock title input を **`_dockWindows`** で Initialize（snap/focus が
  dock プレーン内に閉じる）。floating factory `BuildFloatingWindowFrame` は order/editor/cell を担当。
- **またぎ吸着禁止（§1/§3 の単一クラスタ cross-snap を覆す）**: `SnapOnRelease` / `SpawnDockedToFocus` は各 controller の
  `_windows` dict しか母集合に持たないため、**プレーンをまたぐ吸着は構造的に起きない**（禁止コードは不要）。同一プレーン
  内吸着（奥どうし・手前どうし）は §1 のまま。
- **chart/startup/既定配置 (§3/§4)**: `SyncChartWindowsToUniverse` / `SpawnChartWindow`（`SpawnDockedToFocus`）/
  `DespawnChartWindow` / `SyncStartupVisibilityToMode`（Hide/Show "startup"）/ `SpawnBaseDockWindows`
  （`DockDefaultPlacement.ComputeRects`）は **`_dockWindows`** へ付け替え（chart の #101 fixed-size + focus 吸着も dock
  プレーン内で不変）。chart spawn anchor は `SpawnAnchorTopLeftIn(_dockLayer)`（dock layer は offset 0 なので viewport
  中心の Content 論理点そのもの）。cell は従来どおり `SpawnAnchorTopLeftIn(_floatingLayer)`。
- **永続化 (§6 の kind ルーティング化)**: `CaptureLayout` は `_windows.Capture()`（非 cell）∪ `_dockWindows.Capture()` を
  1 つの `floatingWindows` に union。`RestoreFloating` は各 window を **`DockShape.IsDockKind(kind)` で**プレーンへ
  ルーティング（dock 6 種→`_dockWindows`・order→`_windows`・strategy_editor cell は coordinator 所有で skip）。
  **スキーマ追加 0**（§6 不変）。`DockShape.IsDockKind` を新設し root の private `IsDockKind` を置換（root と AFK が
  同一述語を共有）。
- **scene (§9 退役の続き)**: `BackcastWorkspaceSceneBuilder` が `Content` の子として `DockLayer` を **floatingLayer より
  先に**生成（背面 sibling）＝identity・`_dockLayer` 参照を配線。**シーン再ビルドが必要**（Tools > Backcast > Build
  Workspace Scene）。
- **AFK 正本の拡張**: `FloatingWindowE2ERunner` に Section16（2 プレーン parallax 速度差 1.0 vs 1.2 + 背面 sibling・
  PLANE-01）/ Section17（またぎ吸着なし・プレーン内吸着あり・dock focus は dock プレーン内・`IsDockKind` ルーティング
  parity・PLANE-02）/ Section18（2 controller capture∪ → disk → kind ルーティング restore round-trip・PLANE-03）を追加。
  ID は `PLANE-` 採番（`DEPTH-` は `DepthLadderE2ERunner` 所有のため衝突回避）。さらに **Section19** が実シーン
  `BackcastWorkspace.unity` を editmode で開き、DockLayer が FloatingWindowLayer の背面 sibling であること・
  `_dockLayer`/`_floatingLayer` serialized 参照を構造検証（S16a の合成スタック back-sibling は test 自身の生成順の
  tautology のため、実 scene-builder 出力を S19 で pin。code-review 指摘の対応）。パン時の実奥行き目視は owner HITL（PLANE-04）。
- **code-review(simplify) 対応（2026-06-21）**: ① `_dockLayer` 未配線時の `?? _floatingLayer` フォールバックを撤去＝
  両プレーン collapse（#99 回帰）を silent に再導入せず ctor で **fail-loud**。② `BuildFloatingWindowFrame`/
  `BuildDockWindowFrame` に kind→plane の mis-route guard（`DockShape.IsDockKind`）を追加＝単一チョークポイントで
  loud fail。③ CaptureLayout の zOrder が per-plane-relative である旨を明記。④ S19 で back-sibling を実 scene に bind。
- **RED→GREEN（2026-06-21・実 Unity AFK）**: compile-only gate `error CS` 0 → シーン再ビルド（`DockLayer` 配線確認）→
  `FloatingWindowE2ERunner.Run` **GREEN/exit 0**（PASS tail `…PLANE-01,02,03]`）。非空虚性 litmus: 本番述語
  `DockShape.IsDockKind` から `KIND_CHART` を外すと S17 が `chart must route to the dock plane` で **RED/exit 1** →
  復帰で GREEN。Unity: `6000.4.11f1`。
