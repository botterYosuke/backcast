# findings 0132 — kabu ログインの「パスワード入力欄が無い」を本物の InputField で解消（ADR-0042）

> 番号注記: 0131 は 2 ファイルが採番済み（`0131-replay-watchable-playback-cursor.md` /
> `0131-venue-login-ugui-modal-headless-auth.md`）。本 slice は次の空き番号 0132 で採番する。
> 方針の正本は **ADR-0042**（ADR-0040 §D2 を supersede）。本 findings は下位事実（撤去シンボル・gate・
> RED→GREEN・再走手順）を固定し ADR-0042 を参照する（ADR には書き戻さない）。

## 症状（owner 報告 2026-06-30）

#181 で venue ログインを Unity uGUI モーダル化したが、**kabu モーダルにパスワードの入力欄が無い**。

## 真因

ADR-0040 §D2 の char[] 無バッファ secret 規律のため、`VenueLoginModalOverlay` は kabu パスワードを
**本物の InputField ではなく背景なしの `Text` ラベル**（`_maskedPw` = `MakeLabel`）で描き、入力はモーダル
表示中にグローバルな `Keyboard.onTextInput` を hook して masked dot（`•`）を出していた。結果、パスワード部は
**枠も背景も無い透明領域**として描画され、ユーザーには「入力欄が存在しない」ように見えた（クリックしても
フォーカス概念が無い・`raycastTarget=false`）。機能（タイプすれば `•` が出て送信できる）は生きていた。

**なぜ gate を素通りしたか**: AFK probe（VLOGIN-MODAL-01..09）は `VenueLoginModalController` だけを駆動し
（`AppendSecretChar` / `MaskedPassword` / `CanSubmit`）、**overlay の見た目（入力欄が描画されているか）を一切
assert しなかった**。だから「機能 GREEN・見た目欠落」が成立し、owner HITL 目視で初めて露見した。

## 決定（ADR-0042）

kabu パスワードを Tachibana の認証ID/秘密鍵パスと同じ本物の uGUI `InputField`（`contentType=Password`・
legacy `UnityEngine.UI.InputField`）にし、ADR-0040 §D2 の char[] 無バッファ規律を**撤回**する。
代償＝平文が零化不能な managed string として GC ヒープに滞留する（owner が discoverability を優先して受容）。
C#↔Python 境界（`WorkspaceEngineHost.SubmitVenueLogin(char[])`）は不変（blast radius 最小化）。

## 実装（撤去・追加）

**`VenueLoginModalController.cs`**
- 撤去: `const MaxSecretLen` / `char[] _secret` / `int _secretLen` / `AppendSecretChar` / `BackspaceSecret` /
  `MaskedPassword` / `SecretLength` / `ZeroizeSecret` / `SecretIsZeroed` / `SeedSecretPrefill` / `TakeSecretTransient`(char[] 版)。
- 追加: `string Password`（managed・零化不能） / `SetPassword(string)`。
- 変更: `Open` / `ApplyModeRefresh` は `Password = prefill ?? ""`（kabu のみ）。`CanSubmit`(kabu) = `StationRunning && Password.Length>0`。
  `Close` = `Password=""`。`TakeSecretTransient()` = `IsKabu ? Password.ToCharArray() : Array.Empty<char>()`（host が char[] を zeroize）。
  `SecretIsZeroed()` → **`PasswordCleared()`**（honest rename: 真の零化は不能・`Password==""` を検査）。

**`VenueLoginModalOverlay.cs`**
- 撤去: `event CharTyped` / `event BackspacePressed` / `Keyboard _subscribedKb` / `Subscribe` / `Unsubscribe` /
  `OnTextInput` / `Update`(backspace poll) / `OnDisable`/`OnDestroy`(unsubscribe) / `using UnityEngine.InputSystem` /
  `Text _maskedPw`。
- 追加: `InputField _pwField`（`MakeField(..., password:true, placeholder:"パスワードを入力")`） / `PasswordText` / `SetPasswordText` /
  **`KabuPasswordFieldIsLivePasswordInput()`**（gate contract）。`MakeField` に `password`/`placeholder` 引数。

**`BackcastWorkspaceRoot.cs`**
- 撤去: `_venueLoginOverlay.CharTyped/BackspacePressed` 購読 / `OnVenueLoginChar` / `OnVenueLoginBackspace`。
- 追加: open/mode 切替で kabu の `SetPasswordText(controller.Password)`。per-frame の `if(open)` で kabu は
  `controller.SetPassword(overlay.PasswordText)` を同期（tachibana の AuthId/KeyPath と対称）。

## gate（VLOGIN-MODAL-12 新設・RED→GREEN）

`VenueLoginModalE2ERunner.KabuPasswordIsRealInputField()`: overlay を `Build`→`SetVisible(true)`→`Reflect(kabu)` し、
`KabuPasswordFieldIsLivePasswordInput()`（kabu group 表示中・`_pwField` が `contentType=Password` の生きた InputField）を
assert。`SetPasswordText("hunter2")`→`SetPassword(PasswordText)` で controller への round-trip も assert。tachibana で
`Reflect`→当述語が false（kabu group 非表示）。**litmus**: `MakeField` の `contentType=Password` を外す／ラベルへ戻すと
当 section が RED——controller のみ駆動の旧 gate では捕捉不能だった「入力欄が透明」回帰を直接捕捉する。

既存 section も string 規律へ更新: 01（`Password`/`PasswordCleared`）/ 04（transient char[] コピー＋成功で clear）/
06（mode 切替で `PasswordCleared`）。`Type` ヘルパは `SetPassword(Password + s)`（末尾追記）。

Action-ID 採番: VLOGIN-MODAL-**10/11 は HITL（実 venue 認証・実表示）で既存**ゆえ、新 automated gate は **-12** で採番
（-10 衝突回避）。`EmitPerIdTags` は 01..09＋12 を機械 emit、10/11 は owner HITL で非 emit。

## 再走手順

- C# compile-only: `pwsh scripts/run-live-e2e.ps1 -CompileOnly`（`error CS\d+` 0 件）。
- AFK gate: `pwsh scripts/run-live-e2e.ps1 -Method VenueLoginModalE2ERunner.Run`（`[E2E VENUE LOGIN MODAL PASS]`・
  `[E2E VLOGIN-MODAL-01..09 PASS]`＋`[E2E VLOGIN-MODAL-12 PASS]`・exit 0）。
- Python: 本 slice は C# のみ・headless 認証半分（`test_venue_login_headless.py`）は無改変。

## code-review（high・simplify）で潰した指摘

- **F1（CONFIRMED・correctness）**: `SetPassword` が `_retryBlocked` を無条件 clear していたため、root の per-frame
  `SetPassword(PasswordText)` 同期が毎フレーム allow_retry=false の据え置きを解いてしまい、do-not-retry venue
  （KABU_API_DISABLED）でも翌フレームに OK が再有効化する回帰。修正＝**値が変わったときだけ** block を解く
  （`if (Password != s) _retryBlocked = false;`）。char[] 時代は keystroke でのみ clear していた挙動と一致。
- **F2（gate gap）**: VLOGIN-MODAL-05 は controller を直接駆動し per-frame sync を通らないため F1 に対し vacuous。
  → 「`SetPassword(unchanged)` 後も `!CanSubmit()`」の litmus を追加（per-frame sync 経路を gate）。
- **F3（hygiene）**: `Close()` は `Password=""` にするが overlay の `_pwField.text` は残り、成功/キャンセル後も平文が
  live InputField に滞留していた。→ `SetVisible(false)` で `_pwField.text=""`（平文 surface をモーダル表示中に限定）。
- **doc**: VLOGIN-MODAL-12 round-trip leg の tautology を「accessor が同一 field を指す」確認へ honest 化／header
  コメントの Action-ID 誤記（-10→-12）修正。

## HITL（据え置き）

VLOGIN-MODAL-10/11: 実 venue 認証（kabu verify / tachibana demo）・実モーダル表示・実キーボード入力は display/venue-bound。
owner が実アプリ Settings ▸ Connect で「kabu パスワード欄が入力欄として見え、タイプ→送信→CONNECTED」を目視確認する。
