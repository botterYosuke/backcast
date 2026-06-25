---
status: accepted
---

# Settings ダイアログへの集約（Venue 接続 / 実行モード切替 / Scenario Startup）— ADR-0005 の表面配置を部分 supersede

`grill-with-docs`（2026-06-24, owner HITL）で導出。Help→Settings の deferred stub
（`MenuBarView.cs` の `Settings (deferred slice)`）を実装するにあたり、**3 つの既存機能を単一の
Settings モーダルへ集約**し、それぞれの旧表面を再配置する：

- **Venue 接続/切断** — menu bar の Venue dropdown を**退役**し Settings の Venue セクションへ。
- **実行モード切替セグメント**（Replay / LiveManual / LiveAuto）— footer から Settings へ**移設**。
- **Scenario Startup**（universe/期間/granularity/cash）— dock クラスタの base window
  `KIND_STARTUP` を**完全退役**し、`ScenarioStartupTile` を Settings 内で再構築。

機能ロジック（`VenueMenuViewModel` / `FooterModeViewModel` / `ScenarioStartupController`）は
**不変**——再配置のみ。Settings はビュー層を同じ VM に対して作り直すだけで、engine 経路は触らない。

## Decision の細目（下位決定の正本は本 ADR ではなく findings）

- **footer 表面は残す**（ADR-0005 の footer 廃止ではない）。footer は **mode ステータス表示専用**に縮退
  し、現在モード / `switching…` / `LiveAuto:<runId>` を従来どおり `FooterModeViewModel` から映す。
  モード切替セグメント（3 ボタン）だけが Settings へ移る。「今どのモードか」は menu-bar の
  `mode: <X>` バッジでも従来どおり常時可視。
- **起動口** = Help→Settings（deferred stub を実装に置換）。**ショートカット ESC で開閉トグル**、guard 付き：
  ① ウィンドウ drag 中の ESC は従来どおり drag-revert を優先（ADR-0024 §8）、② secret/save-guard
  モーダルが開いている間は ESC はそちらが消費（Settings は裏で開かない）。`[x]` ボタンでも閉じる。
- **モーダル z-order** = Settings は `SecretModalOverlay`(1000)/`SaveGuardOverlay` より**下**に置く。
  Settings 内 Venue 接続で second password を要求されたら secret モーダルが Settings の**上に重なり**、
  送信後は Settings が開いたまま venue 状態更新を映す。
- Manual/Auto セグメントの venue-gated 可視性・lock 中の dim は VM 再利用でそのまま継承。

## Considered Options

- **採用：単一 Settings へ集約**。owner の UX 選択。設定系（venue・mode・scenario）を 1 箇所に束ね、
  常時 chrome（footer の mode セグメント・menu の Venue dropdown）を減らす。機能 parity は保持。
- **不採用：ADR-0005 の 1:1 配置のまま Settings は別口で複製**。footer/menu に同じ control を残したまま
  Settings にも複製＝冗長・真実源二重化。
- **不採用：footer ごと廃止**（grill 途中で owner が取り下げ）。mode ステータスの常時可視を失うため。

## Scope of supersession（重要）

本 ADR は ADR-0005 を **settings / footer の mode 切替 / venue メニュー の「表面配置」に限って** supersede
する。ADR-0005 の残りの 1:1 表面 parity 契約（sidebar / menu の File=Layout / theme / reconcile 等）は
**そのまま有効**。ADR-0005 は自己保護条項により**無改変**——本 ADR がそれを部分的に上書きする関係を
ここに記録するのみ（ADR-0005 ファイルへは書き戻さない）。

## Consequences

- `KIND_STARTUP` を `FloatingWindowCatalog.Default()` / `BaseDockWindowIds` / `SpawnBaseDockWindows` /
  `FormFactoryBaseGroup` から除去。dock クラスタは 4 base window に。`_lastLiveShape`→startup の
  show/hide トグルは dead code 化するため除去。
- 旧保存 layout が `"startup"` を含む場合は既存の forward-evolution 規律（catalog TryGet=false →
  controller が spawn を skip しつつ layout エントリは保持）でそのまま無害に skip される。
- `WorkspaceFooterView` から mode セグメント（ボタン/可視性/lock-dim）を除去し status 行のみ残す。
- 下位の実装事実（採用ウィジェット・seam・E2E gate）は本 ADR に書き戻さず当該スライスの
  `docs/findings/` に記録し、本 ADR を「方針: ADR-0026」として参照する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
