---
status: accepted
---

# カットオーバー（#5）の done = TTWR `src/ui` との 1:1 表面 parity（機能 parity に留めない）

`grill-with-docs`（2026-06-14）で導出。backcast を本線化し TTWR を廃止する #5 カットオーバーの
**完了契約**を「TTWR `src/ui` の各 UI 表面を Unity に再現する 1:1 surface parity」と定める。
epic #5 の文言「Replay/Live/Auto 全モードで機能 parity」を *機能* に閉じず、**TTWR が持つ UI 表面の集合
（reconcile_modal / settings / theme / menu_bar / instruments_universe_prune 等の二次的サーフェスを含む）を
網羅すること**を done の判定基準とする。

## Context

`The-Trader-Was-Replaced/src/ui/` を backcast の `Assets/Scripts/` と grep で突き合わせた結果、closed issue
（#9–#28）＋既存 OPEN（#23, #29–#37）でも以下が未起票だった：
**LiveAuto 実配線（engine の "Phase 10"・現状 `NoopLiveEngineController` placeholder）/ footer `StartLiveAuto` /
reconcile_modal / instruments_universe_prune / menu_bar(全体) / settings / theme**。
「#29–#37 を実装すれば TTWR を再現できるか？」への答えは **No**。本 ADR はその差分を埋める作業の射程を固定する。

## Considered Options

- **採用：1:1 表面 parity**。TTWR が廃止される以上、ユーザーが現に使う UI 表面は欠落なく移植する。
  欠けたサーフェスは即「機能後退」になるため、surface 集合そのものを契約にする。
- **不採用：#5 AC の機能 parity だけ（二次 UI は任意）**。settings/theme 等を「無くても回る」と判断して落とすと、
  カットオーバー後に TTWR が無く戻れない（廃止）ため、後から欠落が顕在化したとき fallback が無い。
- **不採用：Replay parity のみで cutover**。Live/Auto を別フェーズに切り離す案。`LiveAuto` は
  "The Trader Was Replaced" の本丸であり、本線化の意味を成さないため却下。

## Consequences

- 未起票差分は vertical slice で新規 issue 化する（to-issues, 2026-06-14）。`LiveAuto` 実配線は #4 配下の最優先スライス。
- `component`/`panel_specs`/`systems`/`traits`/`testing` 等の TTWR 内部 Bevy infra は 1:1 表面要素ではないため、
  surface parity 契約の対象外（既存 spec 駆動基盤に吸収する）。
- 本 ADR は **layout の ADR-0003 と矛盾しない**：ADR-0003 は layout 永続化「形式」を capability parity（Bevy 形式
  非互換）に留める決定であり、本 ADR は UI「表面の網羅範囲」を 1:1 にする決定。format は非互換のまま、surface は網羅する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
個別 surface の実装事実（採用ウィジェット・seam 等）は本 ADR に書き戻さず各スライスの `docs/findings/` に記録し、
本 ADR を「方針: ADR-0005」として参照する。
