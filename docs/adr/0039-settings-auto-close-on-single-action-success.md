---
status: accepted
---

# Settings の単発アクション系セクションは確定成功で自動クローズ — ADR-0026 の「Venue 送信後も開いたまま」を部分 supersede

`grill-with-docs`（2026-06-27, owner HITL）で導出。owner の依頼:「設定ダイアログでモードを切り替えて、その
切替が成功したらダイアログを自動で閉じる。外観テーマ切替・Venue Connect/Disconnect も同様に『操作して成功したら
閉じる』」。

[[Settings ダイアログ]]（ADR-0026）は **5 セクション**（Venue / Mode / Scenario / Data / Appearance）を 2 タブ
（実行 / 外観）に束ねる集約口。本 ADR は、そのうち**単発アクション系セクション**の操作が確定成功した時点で
Settings モーダルを**自動で閉じる**ことを決定する。

## Decision

**単発アクション系セクション**（**Venue 接続/切断**・**実行モード切替** Replay/LiveManual/LiveAuto・
**外観テーマ** Dark/Light）は、その操作が **確定成功した時点で Settings モーダルを自動クローズ**する。
**フォーム系セクション**（Scenario Startup・Data＝DuckDB root）は **据え置き**（編集→Save As 型で、単発の
「成功」境界が無いため自動クローズしない）。

「確定成功」の定義はアクションの同期/非同期で分かれる（下位決定の正本は findings 0127）:

- **同期アクション**（外観テーマ・実行モード Replay = engine が拒否しない `SwitchImmediate`）
  → **クリック直後に閉じる**。
- **非同期アクション**（実行モード Manual/Auto = `SwitchLockedLive`/`StopRunThenSwitch` の lock→poll 確定／
  Venue Connect = venue_state が live 化した poll／Venue Disconnect = venue_state が非 live 化した poll）
  → **確定 poll が来てから閉じる**。
- **失敗・拒否・取消は閉じない**（lock 解除＋エラー表示のまま開いておく）。
- **no-op**（既に選択中のモード/テーマを押し直す＝実際に変化しない）は **閉じない**（"成功"="実際に切り替わった
  とき"だけ）。
- Venue 接続が second password（secret modal）を要求する場合は、**ログイン完了＋venue live 化してから**閉じる
  （パスワード取消・ログイン失敗なら開いたまま）。
- Live 切替の確定待ち中に **venue が落ちて auto-replay**（[[FooterModeViewModel]] `ShouldAutoReplay`）した場合は、
  意図した Live 目標に到達していない＝**失敗扱いで閉じない**。

`[x]` ボタン・ESC トグルによる手動クローズは従来どおり（本 ADR は自動クローズの追加であって手動経路は不変）。

## Considered Options

- **採用：単発アクション系のみ確定成功で自動クローズ／フォーム系は据え置き**。owner の UX 選択。モード/テーマ/
  venue は「選んで離脱」する commit-and-go 操作で、確定したら chrome を消すのが速い。フォーム系は複数フィールドを
  編集して Save As で確定するため単発の成功境界が無く、自動クローズすると編集中に消える。
- **不採用：全セクション一律で自動クローズ**。Scenario/Data の編集途中で閉じてしまい破綻。
- **不採用：自動クローズしない（現状維持）**。owner の明示依頼に反する。モード/venue/テーマを変えるたびに手で
  閉じる手間が残る。
- **不採用：RPC を送った瞬間に閉じる**（非同期アクション）。Live モードや venue 接続は engine/venue に拒否され
  得るため、送信＝成功ではない。閉じてから失敗が判明するとユーザーが気づけず開き直す羽目になる。

## Scope of supersession（重要）

本 ADR は **ADR-0026** を **「Settings 内 Venue 接続で…送信後は Settings が開いたまま venue 状態更新を映す」の
一点に限って** supersede する。ADR-0026 の残りの決定（5 セクションの集約・z-order = secret(1000)/save-guard より
下・ESC 開閉トグルと guard・brain 不変による移設）は**そのまま有効**。

特に **z-order 契約は不変**——Venue 接続の second password 入力中は secret modal が Settings の上に重なり、
Settings はその裏で生存し続ける。本 ADR が変えるのは「**送信が成功し venue が live 化した後**」の挙動だけ
（開いたまま → 自動クローズ）。

ADR-0026 は自己保護条項により**無改変**——本 ADR がそれを部分的に上書きする関係をここに記録するのみ
（ADR-0026 ファイルへは書き戻さない）。

## Consequences

- `SettingsVenueSectionView.cs` 先頭コメントの「after submit Settings stays open」、および
  `SettingsDialogE2ERunner` SETTINGS-08 まわりの「Settings stays open」前提の記述を、本 ADR に整合させる
  （実装スライスで更新）。
- 自動クローズの判定（どの解決が「このアクションの確定成功か」）は **pure C# のポリシー seam** に置き、AFK probe が
  headless で駆動できるようにする（host 直書きにしない）。非同期アクションは「このクリックが起こした pending を
  latch し、その目標に到達した poll でだけ閉じる」必要があり、無関係な poll/別経路の状態変化で閉じてはならない。
- 下位の実装事実（採用 seam・close ポリシークラス・E2E gate の Action-ID）は本 ADR に書き戻さず
  `docs/findings/0127-*.md` に記録し、本 ADR を「方針: ADR-0039」として参照する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
