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

- **core-kind 概念の波及**: かつて findings 0082 §2 が「core kinds = {startup, run_result}（FIXED）」と定めたが、
  **その core member / Hakoniwa group 概念は ADR-0024（D1）で既に退役済み**（startup / run_result を他 window と特別扱いしない・
  CONTEXT.md「Hakoniwa group」「core kind set」を SUPERSEDED と明記）。よって `DockShape.IsCoreKind` は **drag 判定からは参照されない
  dead helper**（全リポジトリで呼び出し元ゼロ）であり、ADR-0020 first-launch base 窓 ID 列挙の residual としてのみ存在しうる。
  ADR-0026 で startup が base から退役したため、この residual helper からも KIND_STARTUP を外し `{run_result}` に縮小した
  （ADR-0019/0024 ファイルは無改変・自己保護条項を尊重）。**注意: `IsCoreKind` は呼び出し元が無いので、その値を変えても
  PLANE/GROUP の AFK assert は RED にならない**（下の litmus ⑤ 参照）。factory base group の flush 化は kind 非依存。
- **`FloatingWindowE2ERunner` の KIND_STARTUP フィクスチャ 11 箇所を移行（削除ではなく契約の保持）**: startup を generic
  dock/core テストの fixture に使っていた。core が要る箇所（S23d/S26e/S27）→ `KIND_RUN_RESULT`、plain dock の箇所
  （S23f/S30/S40）→ `KIND_BUYING_POWER`、S12a の catalog-resolves チェックから startup を除去、S17 の startup-routes 行を削除
  （chart が dock-routing を既にカバー）、S18 は hidden window round-trip を run_result で保持。**S32（factory base group）は
  5→4 に書き換え＝DRAG-14 を新 4-window クラスタで再固定**（dock-count 5→4 の AFK 表現の一つ・旧名 GROUP-14 は ADR-0024 で
  DRAG-14 へ改名済み）。
- **ADR-0018 への波及（forward-pointer）**: ADR-0018 §dock プレーン列挙「元箱庭 6 種（…+startup）」は startup 退役で **5 種**へ。
  ADR-0018 ファイルは自己保護で無改変、本 findings に帰結を記録。`FloatingWindowE2ERunner.md` の DOCK-01/02・PLANE-01/03・
  DRAG-13/14 の startup/6 種記述も同 sweep で 5 種・4 base へ現行化（review 2026-06-25）。
- **棚卸し漏れだった startup base-id assert（review 2026-06-25 で発見・修正）**: 当初の移行は FloatingWindow/FooterMode/
  ScenarioStartup の 3 runner のみで、`ChartPlacementJourneyE2ERunner`（CP-S4-01）と `BackcastWorkspaceProbe`（chartwin）の
  `baseIds = {"startup", …}` を見落としていた。CP-S4-01 は back-plane に `dockWindows.Has("startup")` を assert するため
  **startup 退役後は必ず RED**（5 再走ゲートに本 runner が含まれず GREEN 確認をすり抜けていた）。CP-S4-01 を 4 窓へ修正。
  `BackcastWorkspaceProbe` chartwin は front plane `_windows` を読む pre-existing broken（startup 以前に buying_power で落ちる）
  ため別 issue 扱い。

## E2E ゲート（RED→GREEN・rollup タグ）

- **新 `SettingsDialogE2ERunner`（SETTINGS-01..09・AFK は 01..08／09=HITL）** — modal shell open/close・ESC guard 3 分岐・
  `[x]`+SetVisible・z-order・chrome+3 section 非空虚・Venue section gating＋menu Venue 退役（`OpenMenu` enum に Venue 無し）。
  per-Action-ID タグ `[E2E SETTINGS-0N PASS]`（rollup の単一トークン規約）。GREEN・exit 0。SETTINGS-09（second password で secret が
  Settings の上に実描画で重なり・送信後 Settings 残存）は実 overlay 重畳が要るため HITL 専用。
  **AFK カバレッジ（review 2026-06-25 で強化）**: 当初 SETTINGS-08 は Venue gating（interactable）のみで button `onClick`→
  `_onConnect/_onDisconnect` を Invoke していなかった（配線が外れても緑）。→ **修正済み**: SETTINGS-08(c) で connect/Disconnect の
  `onClick.Invoke()` を回し、capturing ラムダで `_onConnect(venue,env)`/`_onDisconnect` 発火を assert（`[x]`=SETTINGS-05 と同手法・
  plain C# view の onClick は EventSystem 不要）。**ESC 優先順位**も SETTINGS-02 に `OnEscape(true,true)==DeferToDrag`（drag>blocking）を
  追加し段間順位を pin。**残る follow-up**: ① mode segment の onClick→`_onMode`→`OnFooterMode`（FOOTER-06/07 は表示反映のみ）、
  ② menu Settings 項目→`OpenSettings`→`controller.Open` と host `DriveSettings` の drag/blocking 集約＋`IsOpen→SetVisible` 鏡映は
  なお AFK 未検証（host を直接駆動する runner が無い・menu→open は HITL 専用）。
- **`FooterModeE2ERunner`** — FOOTER-06/07 の view section を `SettingsModeSegmentView` に retarget（footer の `_modeSegs` 退役）。
  **FOOTER-13 を反転**: 旧「footer に 3 segment ＋ transport 無し」→ 新「footer は **ボタン 0**（segment は Settings へ移設）」。
  非空虚化: Settings mode view が segment を持つことを先に assert。37 pass / 0 fail。
- **`ScenarioStartupE2ERunner`** — SCENARIO-16（`BaseDockWindowIds.Length==4` ＆ no "startup"）/ SCENARIO-17（catalog が
  "startup" を resolve しない＝forward-compat skip の litmus）を追加。tile ロジック（SCENARIO-01..15）は不変。
- RED→GREEN litmus: ① `SETTINGS_SORT` を ≥1000 にすると SETTINGS-06 RED（secret が前面でなくなる）。② startup spec を
  catalog に戻すと SCENARIO-17 RED。③ `BaseDockWindowIds` に startup を戻すと SCENARIO-16 RED（**正本はこれ**。S32 は
  クラスタ数をリテラル 4 で持つので production の `BaseDockWindowIds` とは結合しておらず 5→4 を守らない＝旧記述「S32 RED」は誤り）。
  ④ footer に AddModeSeg を戻すと FOOTER-13 RED。⑤ `IsDockKind` を壊すと FloatingWindow PLANE/GROUP RED（**`IsCoreKind` は呼び出し元
  ゼロの dead helper なので litmus にならない**＝旧記述「IsCoreKind から run_result を外すと RED」は誤り。修正済み）。

## 再走手順

```
pwsh scripts/run-live-e2e.ps1 -CompileOnly                         # error CS 0 件（warning CS も 0）
pwsh scripts/run-live-e2e.ps1 -Method SettingsDialogE2ERunner.Run  # [E2E SETTINGS-01..08 PASS]
pwsh scripts/run-live-e2e.ps1 -Method FooterModeE2ERunner.Run      # [E2E FOOTER MODE PASS] 37/0
pwsh scripts/run-live-e2e.ps1 -Method ScenarioStartupE2ERunner.Run # [E2E SCENARIO STARTUP PASS] + 16/17
pwsh scripts/run-live-e2e.ps1 -Method FloatingWindowE2ERunner.Run  # [E2E FLOATING WINDOW PASS]
pwsh scripts/run-live-e2e.ps1 -Method ChartPlacementJourneyE2ERunner.Run # CP-S4-01 base=4（review 2026-06-25 で startup 退役を追補）
```
（この dev 機は pwsh 不在＝PowerShell 5.1 で `& .\scripts\run-live-e2e.ps1 -Method <X>` 直叩き。確認は Bash `grep -a`。）

2026-06-24 実走: 5 ゲートすべて GREEN・exit 0・`error CS` 0 件・`warning CS` 0 件（pre-existing の G_FRONT を en-passant 除去）。
```
