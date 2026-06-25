# findings 0102 — Settings ダイアログ集約カットオーバー（#125–#128 / ADR-0026）

方針の正本は **[ADR-0026](../adr/0026-settings-dialog-consolidates-venue-mode-scenario.md)**（immutable）と
**CONTEXT.md「Settings ダイアログ」/「footer」/「Replay 実行設定（scenario startup）」**。本 findings は ADR-0026 が
findings に委ねた**下位決定**・**RED→GREEN**・**sibling 契約の移行**を 1 スライス群（#125→#128）でまとめて記録する。

Help→Settings の deferred stub を実体化し、**3 つの既存機能を単一の screen-fixed モーダルへ集約**した。機能ロジック
（`VenueMenuViewModel` / `FooterModeViewModel` / `ScenarioStartupController`）は**不変**——ビュー層を同じ brain に対して
作り直しただけ（engine 経路は無改変）。

## 下位決定（設計の木）

- **D1 — ESC ディスパッチは pure controller に集約**。backcast には ESC のグローバルハンドラが無く、ESC は
  per-component（`FloatingWindowTitleInput.Update` が drag 中のみ poll）だった。secret/save-guard は ESC を poll しない。
  → 新 `SettingsModalController.OnEscape(dragInProgress, blockingModalOpen)` を pure（MonoBehaviour-free）で起こし、
  優先順位を **drag-revert > secret/save-guard > Settings トグル** に固定。host（`BackcastWorkspaceRoot.DriveSettings`）が
  毎フレーム `Keyboard.current.escapeKey.wasPressedThisFrame` を読み、guard 入力を fresh に渡す。AFK は controller を
  headless 駆動（SETTINGS-02/03/04）。
- **D2 — drag 中の ESC が Settings を開かない不変条件は `FloatingWindowController.IsDragging` で担保**。`IsDragging => _drag != null`
  と定義（**`canceled` でも null にしない**）。理由: ESC-cancel（`CancelDrag`）は `_drag.canceled=true` にするが `_drag` は
  mouse-up の `ReleaseDrag` まで非 null。同一フレームで `FloatingWindowTitleInput.Update` と `DriveSettings` の Update 順序が
  不定でも、drag 中 ESC は常に `IsDragging==true` を読む → `DeferToDrag`。front(`_windows`)/back(`_dockWindows`) 両 controller の OR。
- **D3 — z-order = `SETTINGS_SORT = 900`**。menu(600) < footer(550 は下) < **settings(900)** < secret/save-guard(1000)。
  Venue 接続で second password を要求されたら secret(1000) が Settings(900) の**上**に重なり、送信後も Settings は開いたまま
  （`SetVisible` は controller.IsOpen の鏡）。SETTINGS-06 が relation を assert（secret は const ではなく 1000 リテラルなので
  `< 1000` で固定）。
- **D4 — Settings は ScreenSpaceOverlay chrome（floating window ではない）**。secret/save-guard と同型（自前 canvas・全画面
  backdrop・`[x]`）。3 セクション container（Venue/Mode/Scenario）を公開し、host が**既存ビューを built** する：Scenario=
  `ScenarioStartupTile`（`_scenario` SoT 共有）、Mode=新 `SettingsModeSegmentView`（`FooterModeViewModel`＋`OnFooterMode` 再利用）、
  Venue=新 `SettingsVenueSectionView`（`VenueMenuViewModel`＋`OnVenueConnect/Disconnect` 再利用）。
- **D5 — footer は mode ステータス表示専用に縮退**（ADR-0005 の footer 廃止ではない）。segment（3 ボタン）を撤去し status 行のみ。
  `WorkspaceFooterView` の ctor から `onMode` を落とし、`_modeSegs`/`RefreshModeSegments`/`AddModeSeg` を削除。

## sibling 契約の移行（KIND_STARTUP 退役の波及）

ADR-0026 は **`KIND_STARTUP` を catalog `Default()` から除去**するよう要求する（これが saved layout の `"startup"` を
forward-compat で skip させる唯一の手＝`catalog.TryGet("startup")=false`）。これにより 2 つの sibling 契約が動く：

- **ADR-0019 §2「core kinds = {startup, run_result}（FIXED）」の波及**: startup が dock window でなくなったので core 集合は
  **{run_result} に縮小**。これは ADR-0019 の decision の逆転ではなく**配置 supersession（ADR-0026）の帰結**——ADR-0019
  ファイルは無改変。`DockShape.IsCoreKind` から KIND_STARTUP を外し、本 findings に帰結を記録（ADR-0019 自己保護条項を尊重）。
  factory group の promoting core は run_result が継承。
- **`FloatingWindowE2ERunner` の KIND_STARTUP フィクスチャ 11 箇所を移行（削除ではなく契約の保持）**: startup を generic
  dock/core テストの fixture に使っていた。core が要る箇所（S23d/S26e/S27）→ `KIND_RUN_RESULT`、plain dock の箇所
  （S23f/S30/S40）→ `KIND_BUYING_POWER`、S12a の catalog-resolves チェックから startup を除去、S17 の startup-routes 行を削除
  （chart が dock-routing を既にカバー）、S18 は hidden window round-trip を run_result で保持。**S32（factory base group）は
  5→4 に書き換え＝GROUP-14/DRAG-14 を新 4-window クラスタで再固定**（dock-count 5→4 の AFK 表現の一つ）。

## E2E ゲート（RED→GREEN・rollup タグ）

- **新 `SettingsDialogE2ERunner`（SETTINGS-01..08）** — modal shell open/close・ESC guard 3 分岐・`[x]`+SetVisible・z-order・
  chrome+3 section 非空虚・Venue section gating＋menu Venue 退役（`OpenMenu` enum に Venue 無し）。per-Action-ID タグ
  `[E2E SETTINGS-0N PASS]`（rollup の単一トークン規約）。GREEN・exit 0。
- **`FooterModeE2ERunner`** — FOOTER-06/07 の view section を `SettingsModeSegmentView` に retarget（footer の `_modeSegs` 退役）。
  **FOOTER-13 を反転**: 旧「footer に 3 segment ＋ transport 無し」→ 新「footer は **ボタン 0**（segment は Settings へ移設）」。
  非空虚化: Settings mode view が segment を持つことを先に assert。37 pass / 0 fail。
- **`ScenarioStartupE2ERunner`** — SCENARIO-16（`BaseDockWindowIds.Length==4` ＆ no "startup"）/ SCENARIO-17（catalog が
  "startup" を resolve しない＝forward-compat skip の litmus）を追加。tile ロジック（SCENARIO-01..15）は不変。
- RED→GREEN litmus: ① `SETTINGS_SORT` を ≥1000 にすると SETTINGS-06 RED（secret が前面でなくなる）。② startup spec を
  catalog に戻すと SCENARIO-17 RED。③ `BaseDockWindowIds` に startup を戻すと SCENARIO-16 / S32 RED。④ footer に AddModeSeg
  を戻すと FOOTER-13 RED。⑤ `IsCoreKind` から run_result を外す/`IsDockKind` を壊すと FloatingWindow PLANE/GROUP RED。

## 再走手順

```
pwsh scripts/run-live-e2e.ps1 -CompileOnly                         # error CS 0 件（warning CS も 0）
pwsh scripts/run-live-e2e.ps1 -Method SettingsDialogE2ERunner.Run  # [E2E SETTINGS-01..08 PASS]
pwsh scripts/run-live-e2e.ps1 -Method FooterModeE2ERunner.Run      # [E2E FOOTER MODE PASS] 37/0
pwsh scripts/run-live-e2e.ps1 -Method ScenarioStartupE2ERunner.Run # [E2E SCENARIO STARTUP PASS] + 16/17
pwsh scripts/run-live-e2e.ps1 -Method FloatingWindowE2ERunner.Run  # [E2E FLOATING WINDOW PASS]
```
（この dev 機は pwsh 不在＝PowerShell 5.1 で `& .\scripts\run-live-e2e.ps1 -Method <X>` 直叩き。確認は Bash `grep -a`。）

2026-06-24 実走: 5 ゲートすべて GREEN・exit 0・`error CS` 0 件・`warning CS` 0 件（pre-existing の G_FRONT を en-passant 除去）。
```
