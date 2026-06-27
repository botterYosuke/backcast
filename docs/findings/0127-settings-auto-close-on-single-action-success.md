# findings 0127 — Settings の単発アクション系セクションを確定成功で自動クローズ

**方針: ADR-0039**（ADR-0026 の「Venue 送信後も開いたまま」を部分 supersede）。本 findings は設計の木の下位決定・
実装 seam・E2E gate を固定する。grill-with-docs（2026-06-27, owner HITL）。

## 依頼

owner:「設定ダイアログでモードを切り替えて、その切替が成功したらダイアログを自動で閉じる。下記も『操作して成功
したら閉じる』: 外観テーマ切替 / Venue Connect, Disconnect」。

## 設計の木（owner 確定）

### D1 — 対象セクション
**単発アクション系のみ自動クローズ**: Venue 接続/切断・実行モード（Replay/LiveManual/LiveAuto）・外観テーマ
（Dark/Light）。**フォーム系（Scenario Startup・Data）は据え置き**——複数フィールド編集→Save As 型で単発の
成功境界が無い。

### D2 — 「確定成功」の判定（同期/非同期）
| アクション | 経路 | 成功判定 | 閉じるタイミング |
|---|---|---|---|
| 外観テーマ | host が `ThemeService.SetTheme`＋`AppearanceStore.Save`（同期・失敗しない） | 即時 | クリック直後 |
| モード Replay | `FooterModeViewModel.RequestMode` → `SwitchImmediate`（engine 拒否なし） | 即時 | クリック直後 |
| モード Manual/Auto | `SwitchLockedLive`（lock→`SetExecutionMode`→poll）／leaving LiveAuto は `StopRunThenSwitch` | poll の `execution_mode` が PendingTarget に到達し lock 解除 | 確定 poll 後 |
| Venue Connect | host login（second password 経由あり） | poll の `venue_state` が live（CONNECTED/SUBSCRIBED/RECONNECTING） | 確定 poll 後（secret 経由なら login 完了後） |
| Venue Disconnect | host logout | poll の `venue_state` が非 live | 確定 poll 後 |

### D3 — 失敗・取消・no-op は閉じない
- **失敗/拒否**（Live モード rejection＝`NotifyModeResult(false)`／login 失敗／secret password 取消）→ **閉じない**。
  lock 解除＋エラー表示のまま開いておく（閉じるとユーザーが失敗に気づけず開き直す）。
- **no-op**（既選択のモード/テーマを押し直す＝`RequestMode` が `Ignore`／テーマ無変化）→ **閉じない**。
  "成功"="実際に切り替わったとき"だけ（owner 案A）。Venue Disconnect は `CanDisconnect`（要 IsConnected）で
  no-op 不可。
- **auto-replay 巻き込み**: Live 切替の確定待ち中に venue 落ち→`ShouldAutoReplay` で display が Replay に
  戻った場合、意図した Live 目標に未到達＝**失敗扱いで閉じない**（pending latch は目標ちょうど一致でのみ発火）。

### D4 — z-order 契約は不変（ADR-0026 から継承）
Venue 接続の second password 入力中は secret modal(1000) が Settings(900) の上に重なり、Settings はその裏で
**生存し続ける**。本変更が触るのは「login 成功＋venue live 化した後」だけ（開いたまま → 自動クローズ）。

## 実装 seam（設計のみ・実装スライスで確定）

- **自動クローズ判定は pure C# のポリシー seam に置く**（host 直書きにしない）。AFK probe が headless で全分岐を
  駆動できる必要がある（repo 規律: brain は pure・view は thin）。
- **非同期アクションは pending を latch**: クリックが起こした「このアクションの目標」を記録し、その目標**ちょうど**に
  到達した poll でだけ閉じる。無関係な poll・別経路の状態変化・auto-replay では閉じない。
- 既存の解決点に hook:
  - モード: `BackcastWorkspaceRoot.OnFooterMode` の `req.Kind`／`SetExecutionMode` callback／`FooterModeViewModel`
    の lock 解除（poll 確定）と `NotifyModeResult(false)`。
  - Venue: `OnVenueConnect`/`OnVenueDisconnect` と `VenueConnectionViewModel` の poll 状態遷移。
  - テーマ: Appearance segment の `onSelect`（同期・即閉じ）。

## docs 整合タスク（実装スライスで処理）

- `SettingsVenueSectionView.cs` 先頭コメント「after submit Settings stays open」を ADR-0039 へ整合。
- `SettingsDialogE2ERunner` SETTINGS-08／関連コメントの「Settings stays open」前提を更新。

## E2E gate（実装スライスで著す）

`SettingsDialogE2ERunner` に新 Action-ID セクションを追加し `scripts/run-all-tests.ps1` rollup に載せる。
ポリシー seam に対する pure 駆動で、最低限:
- 同期アクション（テーマ・Replay）→ 即クローズ。
- 非同期アクション（Live モード・Venue connect/disconnect）→ 確定 poll でクローズ。
- 失敗/拒否/取消 → **開いたまま**（RED litmus: 失敗でも閉じる実装にすると fail）。
- no-op → **開いたまま**。
- auto-replay 巻き込み → **開いたまま**。

> 注: 採番は findings 0127 / ADR-0039（`ls docs/findings | sort` の次空き＝0127・ADR は 0039）。

## 実装着地（#171・2026-06-27）

設計の木どおり 3 スライス（基盤＋同期 / 非同期モード / 非同期 Venue＋docs）を 1 issue で実装。

### 採用 seam — pure C# `SettingsAutoCloseController`（`Assets/Scripts/Live/SettingsAutoCloseController.cs`）
`SettingsModalController` と同型の MonoBehaviour-free brain。**判定だけ**を持ち host は decision に従って `_settings.Close()` を呼ぶだけ
（host 直書きにしない＝ADR-0039 の要件）。API:
- `OnThemeSelected(bool changed) → SettingsCloseDecision`：同期。変化あり＝`CloseNow`・無し（no-op）＝`Stay`。
- `OnModeRequest(FooterModeRequestKind, target) → SettingsCloseDecision`：`SwitchImmediate`(Replay)＝`CloseNow`・`SwitchLockedLive`/
  `StopRunThenSwitch`＝`Wait`（`Goal.Mode` latch）・`Ignore`/`BlockedVenueNotLive`＝`Stay`。
- `ArmVenueConnect()`／`ArmVenueDisconnect()`：`Goal.VenueLive`／`Goal.VenueDown` を latch。
- `NotifyFailed()`：latch を落とす（失敗・拒否・取消・auto-replay）。
- `OnPoll(displayMode, modeLocked, venueLive) → bool`：latch した目標**ちょうど**に到達した poll でだけ true。**Live target は
  `venueLive` も要求**——venue 落ち poll は DisplayMode==target・lock 解除でも閉じない（auto-replay 巻き込み対策）。

### host 配線（`BackcastWorkspaceRoot`）
- 生成: `BuildWorkspace`（`_settings` の隣）。
- 同期 close: `ApplyAppearance`（theme・apply 前に `changed` を捕捉）／`OnFooterMode`（req.Kind の decision）。
- async arm: `OnVenueConnect`→`ArmVenueConnect`／`OnVenueDisconnect`→`ArmVenueDisconnect`。
- 確定 poll close: `DriveFooter` 末尾で `OnPoll(_footerMode.DisplayMode, _footerMode.Locked, _host.Conn.IsConnected)`。`OnPoll` は
  `_settings.IsOpen` で短絡 gate される（`_settings.IsOpen && _settingsAutoClose.OnPoll(...)`）——Settings が閉じている間は呼ばれない。
  ESC/手動クローズで mid-flight に abandon した latch が後の再 open を誤閉じしない安全性は、この gate **ではなく** 下の disarm 経路
  （`DriveSettings` の open→close 遷移で `NotifyFailed`）が担う。
- disarm: 拒否（`_footerModeRejected`）／login 失敗（`lr==2`）／logout 失敗（`_venueLogoutFailed`）／secret 取消（`CancelSecret`）／
  **Settings の手動クローズ**（`DriveSettings` の open→close 遷移）の 5 経路で `NotifyFailed`。**auto-replay（`ShouldAutoReplay` 消費時）だけは
  `NotifyLiveModeAbandoned`**（live-mode goal だけ落とす surgical 版）——venue 落ちは Disconnect-from-Live の `VenueDown` goal を**成就**させ、
  Replay-target の `Goal.Mode` は fallback で Replay に届くので、これらを blanket `NotifyFailed` で消すと「Live 中の切断が閉じない／LiveAuto→Replay
  離脱が閉じない」回帰になる（findings review fix）。手動クローズ時の disarm は「mid-flight で abandon した latch が後の再 open を誤閉じ」防止。

### docs 整合
`SettingsVenueSectionView.cs` 先頭コメントの「after submit Settings stays open」を「z-order 契約は不変・login 完了＋venue live 化で
auto-close」へ更新。`SettingsDialogE2ERunner.md` の HITL SETTINGS-12「送信後 Settings 残存」を ADR-0039 へ整合（入力中は z-order 不変・
確定後は auto-close）。

### E2E gate（`SettingsDialogE2ERunner` SETTINGS-14..18・rollup tag `[E2E SETTINGS-NN PASS]`）
pure seam を headless 全分岐駆動。SETTINGS-14（同期テーマ/Replay 即クローズ＋no-op 据置）・15（非同期モード確定 poll）・16（拒否＋
auto-replay 巻き込みは開いたまま＋`NotifyLiveModeAbandoned` の surgical 性：Replay-target は落とさない）・17（Venue connect/disconnect 確定 poll
＋**Disconnect-from-Live で `NotifyLiveModeAbandoned` が `VenueDown` を消さない**回帰ガード）・18（Venue 失敗/取消/idle は開いたまま）。SETTINGS-19＝
フォーム系は seam 非配線で対象外。SETTINGS-12 HITL に自動クローズの実挙動確認を追加。**host 結線（どの分岐がどの disarm を呼ぶか・OnPoll 極性・
`_settings.Close()`）は thin glue＝コード読解＋live HITL SETTINGS-12 で担保**（pure seam の全分岐は SETTINGS-14..18 が gate）。

### RED→GREEN（実走で実証・2026-06-27）
`NotifyFailed` を no-op に潰して `SettingsDialogE2ERunner.Run` を batchmode 実走 → **SETTINGS-16「rejection did not drop the latch」/
SETTINGS-18「connect failure/cancel did not drop the latch」が RED**（失敗経路で latch が残り、確定 poll で誤って閉じる）、他 section は
GREEN・`[E2E SETTINGS DIALOG FAIL]`。`NotifyFailed=>Disarm()` に戻して再走 → **SETTINGS-01..18 全 GREEN・`[E2E SETTINGS DIALOG PASS]`・
exit 0・error CS 0 件**。delete-the-production-logic litmus は台本 `.md` の自動判定節に固定（OnThemeSelected の changed 無視 / OnPoll の
modeLocked・venueLive ガード撤去 / ArmVenueConnect の goal 取り違え 等で各 section RED）。

実行: `pwsh scripts/run-live-e2e.ps1 -Method SettingsDialogE2ERunner.Run`（rollup 合流は `run-all-tests.ps1 -Method …`）。
