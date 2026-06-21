---
status: accepted
---

# ドックの奥行きは「2 つの深さプレーン」で出す（元箱庭は奥プレーン・エディタ/発注は手前プレーン）／プレーンをまたぐ吸着は禁止

owner 報告（2026-06-21）「Hakoniwa ドッキング化（#99）後、元箱庭パネルと floating window の間にあった
**視覚的な奥行きの差が消えた**」を `grill-with-docs`（2026-06-21・owner HITL）で設計ロックした決定。issue #103。

この決定は **ADR-0017 の Decision 1（「旧 base tile と chart family は **すべて** `FloatingWindowLayer` の子に合流し、
専用 bounded サーフェス（HakoniwaRoot）は持たない」）** と、同 ADR が単一クラスタ内で許した cross-snap を、
**2 点だけ部分的に覆す**。ADR-0017 は自己保護条項（「覆す場合はこのファイルを編集せず、本 ADR を supersede する
新規 ADR を起こす」）を持つため、**ADR-0017 は無改変**で本 ADR を起こす。ADR-0017 のその他の決定（磁石スナップ・
結合なし・bounded surface なし・split-grid 退役・per-mode profile 廃止・永続スキーマ追加 0）は**不変**。

関連: ADR-0017（Hakoniwa ドッキング化・本 ADR が 2 点 supersede）／ADR-0003（capability parity・無矛盾）／
findings 0006（無限キャンバスのパララックス座標モデル `ParallaxLayerOffset`・再利用）／findings 0075（ドッキング化の
下位設計の木・本決定の下位事実を追記）。実装下位事実は findings 0075（追記）。

## Context

この画面の「奥行き」は z 値ではなく **パララックス（視差）** で出す。Canvas は ScreenSpaceOverlay なので z は描画順に
効かず、奥行きの錯覚はパンしたときの**速度差**で生む（findings 0006 §2 のパララックス座標モデル）。

- **#99 以前**: 箱庭タイルは `HakoniwaRoot`（Content の直子＝**1.0倍**プレーン）に、floating window（エディタ／発注）は
  `FloatingWindowLayer`（**1.2倍**プレーン）にいた。パンすると 1.2倍プレーンが速く動き、floating window が
  「手前に浮く」＝**奥行きの差**が見えた。
- **#99 以後（ADR-0017）**: `HakoniwaRoot` を退役し、元箱庭 6 種（chart / orders / positions / run_result /
  buying_power / startup）も**全部 `FloatingWindowLayer`（1.2倍）に合流**。全ウィンドウが同速で動くため
  **奥行きの差が消失**した（回帰）。

ADR-0017 の「2 系統を 1 つに畳む」判断は **移動/z-order/永続化の seam 統一**としては正しかったが、**深さの統一**まで
巻き込んだのが回帰の原因。深さ（どのプレーンに乗るか）と seam（どの controller が動かす/保存するか）は直交する軸で、
seam は 1 本に畳みつつ深さは 2 枚に分けられる。

さらに owner は「**奥のパネルと手前のエディタはくっつかない**」を明示決定した。#99 の磁石スナップは「近接した辺どうしが
揃う」挙動だが、奥（1.0倍）と手前（1.2倍）はパンのたびに相対位置がずれるため、またいで吸着させると「揃えたのに離れる」
不整合が起きる。よって**吸着は同一プレーン内のみ**に限定する。

## Decision

ADR-0017 Decision 1 と単一クラスタ cross-snap を、以下の **2 点**で supersede する（他は不変）。

1. **2 つの深さプレーンに分ける**（ADR-0017 Decision 1 の「全パネルを 1 枚の `FloatingWindowLayer` に合流」を覆す）。
   - **奥プレーン = 新 `DockLayer`**: `Content` の直子・**1.0倍**（パララックスなし＝Content をそのまま乗るだけ）・
     `FloatingWindowLayer` より**前の sibling ＝常に背面描画**・**identity 全面レイヤー**（bounded box でも split-grid でも
     ない）。元箱庭 6 種（chart / orders / positions / run_result / buying_power / startup）はここに spawn する。
   - **手前プレーン = 既存 `FloatingWindowLayer`**: **1.2倍**・常に前面。`strategy_editor`（セル）＋ `order`（発注
     チケット）はここに残す。
   - 各プレーンは独立した `FloatingWindowController` が所有する（同じ catalog を共有・layer だけ別）。移動/z-order/snap/
     永続化の **seam は ADR-0017 のまま 1 種類**で、プレーンは「どの layer にぶら下げるか」だけが違う。

2. **吸着（磁石スナップ）は同一プレーン内のみ**（ADR-0017 の単一クラスタ cross-snap を覆す）。
   - controller がプレーンごとに分かれている結果、`SnapOnRelease` / `SpawnDockedToFocus` は**自分のプレーンの
     ウィンドウしか母集合に持たない**ため、プレーンをまたぐ吸着は**構造的に起きない**（特別な禁止コードは要らない）。
   - 同一プレーン内の吸着（奥どうし・手前どうし）は ADR-0017 のまま不変。

それ以外（#99 のドッキング自体＝自由移動＋同一プレーン内吸着、`DockLayer` が identity 全面で bounded surface を
復活させないこと、永続スキーマ追加 0、chart universe 同期、startup の mode 別 show/hide）は ADR-0017 のまま。

## Considered Options

- **採用：2 プレーン分離（奥 1.0倍 DockLayer ／手前 1.2倍 FloatingWindowLayer）＋プレーン内吸着のみ**。
  #99 以前の奥行きを最小変更で復元。seam は 1 本のまま（controller を 2 個 instantiate するだけ）、永続は kind で
  ルーティングしてスキーマ追加 0、cross-snap 禁止は controller 分離で構造的に保証。owner の「奥と手前はくっつかない」
  明示決定に直結。
- **不採用：単一レイヤーのまま per-window でパララックス係数を変える**。window ごとに layer-local offset を別管理する
  必要があり、#15 の「layer が 1 枚・window は layer-local 座標」という seam を壊す。z-order・snap・永続の母集合が
  プレーン混在になり cross-snap 禁止も別途コードが要る。複雑さに見合わない。
- **不採用：HakoniwaRoot（bounded box）を復活**。ADR-0017 が退役を決めた split-grid/bounded surface に逆戻り。
  owner は「identity 全面レイヤー」を選択（issue #103 設計ロック §4）。
- **不採用：奥行きを z で出す**。Canvas は ScreenSpaceOverlay で z が描画順に効かない（Context）。

## Consequences

- シーンに `DockLayer`（`Content` の子・`FloatingWindowLayer` より前の sibling）を追加し serialized 参照を配線 →
  **シーン再ビルド**が必要（Tools > Backcast > Build Workspace Scene）。
- `BackcastWorkspaceRoot` は floating controller（`_windows`）に加え dock controller（`_dockWindows`）を持つ。
  元箱庭 6 種の spawn / chart universe 同期 / startup show-hide / 既定配置は dock controller 側へ移る。
- 永続化（`CaptureLayout` / `RestoreFloating`）は両プレーンのウィンドウを `floatingWindows` 次元に書き、復元時に
  **kind でプレーンへルーティング**する（`DockShape.IsDockKind`）。**スキーマ追加は 0**（ADR-0017 §6 不変）。
- AFK 正本: `FloatingWindowE2ERunner` に「またぎ吸着なし／プレーン内吸着あり／2 プレーンの parallax 速度差（1.0 vs
  1.2）／奥は背面 sibling／2 コントローラ persist round-trip」のセクションを追加（実装着手前に `behavior-to-e2e` を
  formal invoke 済み）。パン時の実奥行きの目視は owner HITL。
- 下位事実（プレーン分離・cross-snap 禁止）は findings 0075 に追記し、本 ADR を「方針: ADR-0018」として参照する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
プレーン数・パララックス係数（1.0 / 1.2）・どの kind がどちらのプレーンか・sibling 順などの下位事実は本 ADR に
書き戻さず、`docs/findings/0075` に記録し本 ADR を「方針: ADR-0018」として参照する。
