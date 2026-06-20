# findings 0075 — Hakoniwa ドッキング化（split-grid → 独立 floating window＋磁石スナップ）

方針: **ADR-0017**（Hakoniwa = ドッキング可能な独立 floating window 群）。本 findings はその下位設計の木を固定する。
grill: `grill-with-docs`（2026-06-21・owner HITL）。supersede: findings 0007（split-grid）。再利用: findings 0008（floating window seam）。

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

`BackcastWorkspaceRoot` が membership を所有する構造は不変。actuation だけ HakoniwaController → FloatingWindowController へ:
- **chart universe 同期**: `SyncChartTilesToUniverse` を `_hako.AddTile/RemoveTile` → `_windows.Spawn/Close` に。
  banner: 銘柄 add で chart window spawn（初回はデフォルト配置・以後は保存座標）、remove で Close。membership 正本は
  `InstrumentRegistry`（#60 不変・doc は並び/座標のミラー）。
- **startup show/hide（base retile の縮退）**: mode poll（`IsLiveShape(DisplayMode)`）が Replay→startup window を Show、
  Live→Hide。**despawn/respawn ではなく可視性トグル**（#15 `Show`/`Hide` を流用・dormant 温存）。chart は mode をまたいで
  identity 保持（spawn したまま）。

## §4 デフォルト配置（初回起動・リセット時）

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
