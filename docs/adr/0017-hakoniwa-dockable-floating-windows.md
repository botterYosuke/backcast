---
status: accepted
---

# Hakoniwa サーフェスは「ドッキング可能な独立 floating window 群」（TTWR split-grid parity から意図的逸脱）

owner 依頼（2026-06-21）「Hakoniwa を tile/swap から **ドッキングスタイル**へ。1 つ 1 つのパネルを独立した
ウィンドウとし、それらをくっつけて Hakoniwa とする」を `grill-with-docs`（2026-06-21）で設計ロックした決定。

これは findings 0007（Hakoniwa split-grid・§1/§4 owner-locked）と CONTEXT.md glossary（`Hakoniwa＝split-grid`／
`tile/slot/tile swap`／`chart は floating window ではない`／`per-mode layout profile`）の **owner-lock を覆す**ため、
新規 ADR で明示記録する（非可逆・将来の読者が驚く・実トレードオフの 3 条件を満たす）。

関連: ADR-0003（capability parity・**本 ADR と矛盾しない**＝下記）／findings 0007（split-grid・本 ADR が supersede）／
findings 0008（floating window seam・本 ADR が再利用）。実装下位事実は findings 0075。

## Context

旧 Hakoniwa は `ceil(√n)` 等分グリッドに tile を並べ、唯一の mutation は 2 slot の **swap**、slot 順序が正本で
rect は派生——という TTWR `src/ui/hakoniwa.rs`（TTWR ADR 0011/0014）の **capability parity** だった。一方 #15 で
**floating window**（自由配置・独立移動・z-order・canvas 論理座標 x/y/w/h の永続化）が別系統として完成しており、
CONTEXT は両者を厳格に分離（`chart は floating window ではない＝Hakoniwa tile`／tile は free-float 不可）していた。

owner は「各パネルを**独立ウィンドウ**にして、近づけると辺が**吸着**してくっつき、離せば自由に浮く」という
**磁石スナップ型ドッキング**を望む。これは split-grid の中核前提（bounded box・slot 正本・swap・free-float 禁止）と
正面衝突する。同時に「独立ウィンドウ」は #15 floating window 概念そのものなので、**2 系統を 1 つに畳む**機会でもある。

「Bevy/TTWR 同等」は ADR-0003 で **capability parity（バイト互換ではない）** と定義済みで、capability surface の
具体項目は本 ADR ではなく findings に委譲されている。よって TTWR の split-grid を**そのまま写す義務はない**。

## Decision

1. **Hakoniwa = ドッキング可能な独立 floating window 群**。旧 base tile（startup / buying_power / orders /
   positions / run_result）と chart tile family（`chart:<id>`）は、すべて **floating window**（#15
   `FloatingWindowController` 管理・`FloatingWindowLayer` の子）になる。専用の bounded サーフェス GameObject
   （HakoniwaRoot）は持たない。「Hakoniwa」は**くっついた window クラスタを指す概念ラベル**になる。
2. **ドッキング＝磁石スナップ（結合なし）**。drag リリース時、近接した window の辺どうしが閾値内なら
   ピッタリ揃う（flush 隣接＋同辺整列）。**グループ化・一体移動・detach 状態は持たない**——常に各 window 独立で、
   隣を動かしても付いてこない。隙間・重なりは許容（タイリング強制でも resize 連動でもない）。
3. **split-grid 機構を退役**：`HakoniwaController`／`HakoniwaGridMath`／box-grow／`HakoniwaTileHeaderInput`（swap）／
   `HakoniwaLayoutProfiles`（per-mode）。snap は `FloatingWindowController`＋pure `FloatingWindowMath` に additive 実装。
4. **mode 別レイアウトを廃止**。配置は全 mode で**単一共有**（floating window の既存「flat 共有」へ統一）。mode 差は
   **startup window を Replay のときだけ表示**する show/hide のみ（base retile の縮退）。
5. **chart の universe 同期は維持**（銘柄 add/remove で chart window を spawn/despawn）。membership の所有は
   `BackcastWorkspaceRoot` のまま、actuation を HakoniwaController → FloatingWindowController に付け替える。
6. **永続化は ADR-0003 の枠内**（Unity 自前 versioned スキーマ）。base/chart は `LayoutDocument.floatingWindows`
   次元（x/y/w/h/zOrder/visible）で保存する。**旧 `panels`／`hakoniwaProfiles` は読まない**（dead schema・
   forward-evolution tolerance で温存はするが Hakoniwa の正本ではない）。既存保存レイアウトは **migrate せず
   デフォルトのタイル風配置にリセット**（owner 決定・ADR-0003 D4「作り直しでよい」と同精神）。

## Considered Options

- **採用：独立 window＋磁石スナップ（結合なし）／既存 floating window に合流**。owner の「独立ウィンドウ」言明に
  直結。完成済みの #15 seam（移動・z-order・永続化）を再利用し、snap だけ additive。永続スキーマ追加 0。
- **不採用：枠内タイリング・ドックツリー（IDE/Dear ImGui 風）**。bounded surface 維持・divider resize で隣連動・
  タブ。owner は「独立ウィンドウ・離せば自由に浮く」を選択（隙間/重なり許容）したため不採用。
- **不採用：ドック結合（群ごと一体移動・detach）**。永続にグループ所属が要りスキーマ追加。owner は「隣を動かしても
  付いてこない・各々独立」を明示選択したため不採用。
- **不採用：split-grid 維持（TTWR バイト写し）**。owner 要望と非互換。ADR-0003 が parity をバイト互換に縛らない。

## Consequences

- CONTEXT.md glossary の `Hakoniwa`／`tile / slot / tile swap`／`chart tile family`／`mode-conditional base tile`／
  `per-mode layout profile`／`floating window` の各項を本決定に整合させる（findings 0075 と同時）。
- findings 0007 は **SUPERSEDED**（split-grid は退役）。base retile / chart family の所有権分離の発想は
  show/hide・spawn/despawn として floating 側に引き継ぐ。
- AFK 正本の作り直し：`HakoniwaE2ERunner`（grid/swap）・`ReplayToHakoniwaE2ERunner` は新モデルへ rewrite/retire し、
  **snap 吸着の pure AFK probe** を新設する（実装着手前に `behavior-to-e2e` を formal invoke）。
- tile content（chart view / positions / orders / run_result / buying_power / scenario startup）は HakoniwaRoot の
  子から **floating window body（title bar + body）** へ rehome する（実装の最重量パート）。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
snap 閾値・揃える辺・デフォルト配置・catalog kind などの下位事実は本 ADR に書き戻さず、`docs/findings/0075` に
記録し本 ADR を「方針: ADR-0017」として参照する。
