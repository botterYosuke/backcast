---
status: accepted
---

# run_result は dock panel を退役し、run 結果は screen-anchored な右上ポップアップで出す（ADR-0017 D1 / ADR-0018 / ADR-0020 を点 supersede）

owner 依頼（2026-06-27）「run_result パネルを廃止。表示することがある期間だけ右側にポップアップ表示（3D空間から除外）」を
`grill-with-docs`（2026-06-27・owner HITL）で設計ロックした決定。issue は別途。

これは **ADR-0017 Decision 1**（旧 base tile と chart family は **すべて** floating window になる＝run_result も floating
window）と **ADR-0018**（元箱庭 6 種は奥プレーン `DockLayer`（1.0倍）に乗る＝run_result も `DockLayer`）を、**run_result に
ついてだけ** 点 supersede する。さらに **ADR-0020**（first-launch base cluster = `{buying_power, orders, positions,
run_result}` の 4 窓 group）の base 集合を 3 窓へ縮める。これら 3 ADR はいずれも自己保護条項（「覆す場合はこのファイルを
編集せず、本 ADR を supersede する新規 ADR を起こす」）を持つため、**3 ADR は無改変**で本 ADR を起こす。

関連: [[ADR-0017]]（Hakoniwa ドッキング化・本 ADR が D1 を run_result について supersede）／[[ADR-0018]]（2 深さプレーン・
本 ADR が run_result を両プレーンから外す）／[[ADR-0020]]（factory base cluster・本 ADR が base 集合を 4→3 に縮小）／
ADR-0024（Hakoniwa core special 退役＝`IsCoreKind` は既に dead）／findings 0075（ドッキング下位設計）／findings 0110 §7
（#138 DriveRunResult の LiveManual-hide・本 ADR が subsume）。実装下位事実は findings 0125。

## Context

run_result は現在、奥プレーン `DockLayer`（1.0倍）の **non-closable base-dock singleton** であり、(a) パンすると canvas
（Content）と一緒に動く＝**パララックス「3D空間」に乗っている**、(b) `CaptureLayout` に x/y/w/h/visible が乗り永続化される、
(c) factory base group（ADR-0020）の member かつ ADR-0026 以降の唯一の「promoting core」（`DockShape.IsCoreKind`）、
(d) LiveManual のときだけ `DriveRunResult` が `SetActive(false)` で隠す特例（#138・findings 0110 §7）を持つ。

owner は「run_result を常設パネルとして 3D 空間（パララックス canvas）に置くのをやめ、**run の結果が出ている期間だけ**
画面右上にポップアップで見せたい」と要望した。これは「常時 dock に居る base singleton」という現モデルと、「**content が
あるときだけ・画面固定で出る ephemeral overlay**」という別モデルの対立であり、深さ（どのプレーンか）と seam（どの
controller が動かす/保存するか）の両方を run_result について畳む決定になる。

`DockShape.IsCoreKind` は ADR-0024 で Hakoniwa core special が退役して以降 **production の挙動経路から参照されていない**
（定義 1 箇所と comment 1 箇所だけ＝diagnostic/dead）。よって run_result を core 集合から外しても挙動影響は無い。factory
base group は `FormGroup`／`DissolveIfShrunkTo(2)` の **≥2 閾値** で成立するため、run_result を外した 3 窓
`{buying_power, orders, positions}` でも group は成立する。

## Decision

ADR-0017 D1・ADR-0018・ADR-0020 を以下の点で supersede する（run_result 以外は不変）。

1. **run_result は dock window を退役**（ADR-0017 D1 / ADR-0018 を run_result について覆す）。`DockLayer` から外れ、
   floating window seam（移動 / z-order / snap / window group / `CaptureLayout`）から完全に抜ける。`FloatingWindowCatalog`
   の `KIND_RUN_RESULT`・`DockShape.IsDockKind` の run_result 分岐・`BaseDockWindowIds`・`SpawnBaseDockWindows` の
   run_result spawn を退役。dock plane は **chart + 3 base singleton（buying_power / orders / positions）** に縮退。

2. **run 結果は screen-anchored な右上ポップアップで出す**（「3D空間から除外」）。`ScreenSpaceOverlay` Canvas の直下
   （`Content` の子では **ない**）に固定 anchor で置き、**パンしても動かない**（パララックス層に乗らない）。canvas に
   **重ねて浮く**（ガター予約なし・レイアウトを押しのけない）。固定サイズの card で **drag / resize 不可**、title ＋ ×
   close のみ。

3. **表示は content-derived ＋ manual-close latch**。run データがあるとき出現する（Replay: running view → 完了で full
   stats / LiveAuto: telemetry）。× で閉じると **次の run まで再出現しない**。latch の **再 arm は新しい run の rising
   edge**——Replay は run 間で portfolio が honest-empty に落ち再投入される自然な falling→rising edge、LiveAuto は
   telemetry flag が sticky（`LivePanelViewModel.Apply` は flag を reset しない）なので **新しい `run_id`** を rising edge
   として使う（boolean flag では 2 回目の LiveAuto run で再 arm しない）。LiveManual は telemetry が無く構造的に出ない。

4. **両モード（Replay ＋ LiveAuto）**。content の有無だけで決まり mode 分岐しない。**#138 の `DriveRunResult`
   LiveManual-hide 特例は本決定が subsume** して退役する（LiveManual は content 無し＝自然に非表示）。

5. **永続化ゼロ**。固定 anchor（座標を持たない）・visibility は run state から派生・dismiss latch は session 内（毎 run
   再 arm）なので **何も保存しない**。run_result は `floatingWindows` 次元から外れる。既存保存レイアウトに残る run_result
   geometry は **migrate せず無視**（ADR-0003 D4「作り直しでよい」と同精神・スキーマ追加 0）。

6. **base factory group を 4→3 に縮小**（ADR-0020 の base 集合を覆う）。`{buying_power, orders, positions}` で group は
   ≥2 閾値により成立を維持。`DockShape.IsCoreKind` は **空集合**（全 kind に false）へ畳む（dead-code simplify・挙動影響
   無し）。content drive（`PushReplayTiles` / `PushLiveTiles` → `FormatReplayRunResultRunning/Complete` / `FormatRunResult`）
   は **そのまま再利用**し、ターゲットを dock tile view からポップアップ view へ付け替えるだけ。

## Considered Options

- **採用：screen-anchored 右上ポップアップ・content-derived・manual-close latch・両モード・永続ゼロ**。owner の「3D空間
  から除外」「表示することがある期間だけ」「右側」「手動で閉じるまで残る」言明に直結。content drive を再利用し removal は
  純減（catalog kind / dock 分岐 / base 窓 / DriveRunResult / 永続次元）。
- **不採用：dock panel のまま位置だけ右に固定**。パララックス Content に乗ったままなので **パンで動く＝「3D空間から除外」に
  ならない**。owner 要望と非互換。
- **不採用：完了後 N 秒で自動消滅するトースト**。owner は「出たら手動で閉じるまで残る」を明示選択（§Decision 3）。
- **不採用：Replay 成績専用**。owner は「Replay と LiveAuto の両方」を明示選択（§Decision 4）。LiveAuto telemetry が surface
  を失う。
- **不採用：右端の縦ストリップ（ガター予約）**。canvas を左に狭める。owner は「重ねる・ガターなし」を選択（§Decision 2）。

## Consequences

- `ScreenSpaceOverlay` Canvas 直下に run-result ポップアップ GameObject を追加（`Content` 配下では **ない**＝奥行き層に
  乗らない）。scene 再ビルドの要否は findings 0125 に記録。
- `BackcastWorkspaceRoot`：`SpawnBaseDockWindows`／`BaseDockWindowIds`（4→3）／`DriveRunResult`（退役）／`PushReplayTiles`
  ・`PushLiveTiles` の run_result drive ターゲット付け替え／manual-close latch ＋ 新-run rising-edge 再 arm を実装。
- `FloatingWindowCatalog.KIND_RUN_RESULT` と `DockShape.IsDockKind` の run_result 分岐を退役。`DockShape.IsCoreKind` は
  空集合へ。dock kind は chart + 3 base singleton。
- 永続（`CaptureLayout` / restore）は run_result を **書かない・読まない**。既存 doc の run_result geometry は無視。
- ADR-0017 D1 / ADR-0018 / ADR-0020 / ADR-0026-era core set を **無改変**のまま点 supersede（本 ADR が差分を持つ）。
- CONTEXT.md glossary の `Replay portfolio projection`／`Hakoniwa`／`floating window`／`window group`／`core member` /
  `IsCoreKind` の各項を run_result 退役に整合（findings 0125 と同時）。
- AFK 正本：run-result ポップアップの probe を新設（content があると出る／× で閉じると次 run まで出ない／新 run の rising
  edge で再 arm（Replay portfolio 再投入・LiveAuto 新 run_id の両方）／両モードで出る／**pan で動かない（screen-anchored）**
  ／**永続しない**（save→boot で位置/可視を復元しない）／base group は 3 窓で成立）。実装着手前に `behavior-to-e2e` を
  formal invoke する。実 pan の奥行き目視は owner HITL。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。ポップアップの
固定 anchor 位置・card サイズ・latch 再 arm の実装シグナル（portfolio empty 検出 / run_id 比較）・除去する正確なシンボル
などの下位事実は本 ADR に書き戻さず、`docs/findings/0125` に記録し本 ADR を「方針: ADR-0037」として参照する。
