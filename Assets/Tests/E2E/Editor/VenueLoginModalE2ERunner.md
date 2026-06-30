# VenueLoginModalE2ERunner — 台本（Surface E2E / 操作網羅台帳）

`VenueLoginModalE2ERunner.cs` が自動検証する **venue ログイン uGUI モーダル サーフェス**（#181 / ADR-0040）の台本。
実装者は `.cs` と本 `.md` をセットで読む。これは調査メモではなく、**このサーフェスでユーザーができる行動すべての
網羅台帳と、E2E の観測点・合格条件を定義する正本**。Action ID 採番・カバー状態の語彙・セクション構成・責務境界の
共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md)（命名は [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。

> **二層 E2E の位置づけ**: 本台本は *Surface E2E* の **C# 半分**（モーダルの入力→送信→結果表示）。旧 tkinter ダイアログ
> （別 OS ウィンドウ・findings 0130 の macOS crash）を Unity uGUI モーダルへ置換した（オーケストレーションの反転＝
> C# が GUI を持ち Python は headless 認証だけ）。**headless 認証半分**（`submit_venue_login` → `authenticate_*` の
> 実 RPC・error_code/status_text/allow_retry の出し分け・CONNECTED 到達）は **pytest が正本**
> （`python/tests/test_venue_login_headless.py`）。**実 venue 認証・実キーボード drain・実モーダル表示**は
> display/venue-bound＝*HITL*（findings 0131 §HITL）。
> **不可侵**: kabu API パスワードを env/log に残さない・close/mode 切替/成功で `Password=""` にクリアする。
> ADR-0042 で kabu パスワードは本物の InputField(contentType=Password) になった（ADR-0040 §D2 の char[] 無バッファを撤回・
> 代償＝平文は零化不能 managed string）。[[secret modal]] 自身の char[] 規律は kabu ログインには適用しない。

## 対象サーフェス

venue ログインモーダル（頭脳 `VenueLoginModalController`〔plain C#・Python 非依存〕＋ chrome `VenueLoginModalOverlay`
〔ScreenSpaceOverlay sort 1000〕＋ root 連携 `BackcastWorkspaceRoot.OnVenueConnect/OpenVenueLoginModal/
DriveVenueLoginModal/OnVenueLogin*`）。RPC は `WorkspaceEngineHost.VenueLoginFormInit/VenueLoginProbeStation/
SubmitVenueLogin` → `submit_venue_login` 等（headless 認証）。kabu API パスワードは本物の uGUI InputField
（contentType=Password・ADR-0042）。Tachibana の認証ID・秘密鍵パスと同じ legacy InputField。

## 対象ユーザー行動

Tachibana: 認証ID 入力 / 秘密鍵 PEM パス入力＋「参照…」ピッカー / demo・prod ラジオ。kabu: API パスワード入力
（InputField(Password)）/ verify・prod ラジオ / 本体ポート表示 / 「再確認」/ 本体未起動なら OK 無効。共通: OK（送信→headless 認証）/
キャンセル / 失敗時はモーダル内に日本語エラー赤字＋閉じずに再試行。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 / 状態遷移 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| VLOGIN-MODAL-01 | kabu パスワード入力（managed string・ADR-0042）・close で clear | `VenueLoginModalController.cs`(SetPassword/Password/Close/PasswordCleared) | `Password` に保持・close で `PasswordCleared()` | Open→SetPassword→`Password`、Close→`PasswordCleared` | 自動(E2E済) | `VenueLoginModalE2ERunner`(KabuPasswordAndClear) |
| VLOGIN-MODAL-02 | OK 有効判定 | `VenueLoginModalController.cs`(CanSubmit) | kabu=本体起動＋PW 非空 / tachibana=認証ID＋鍵パス非空 / busy 中は不可 | 各状態で `CanSubmit()` を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(SubmitGating) |
| VLOGIN-MODAL-03 | fields JSON 整形 | `VenueLoginModalController.cs`(BuildFieldsJson) | tachibana=`{"auth_id","key_path"}`・JSON escape / kabu=`{}`（secret は別経路） | escape 含む JSON を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(FieldsJsonShape) |
| VLOGIN-MODAL-04 | secret transient 渡し＋成功で clear | `VenueLoginModalController.cs`(TakeSecretTransient/ApplyResult/Close) | transient char[] は入力 PW のコピー（host が zeroize）・controller の `Password` は結果まで保持・成功 close で clear | type→Take→success→`PasswordCleared` | 自動(E2E済) | `VenueLoginModalE2ERunner`(SecretTransientAndClear) |
| VLOGIN-MODAL-05 | 結果表示（成功=閉じる / 失敗=閉じず赤字＋再試行 / allow_retry=false は OK 据え置き） | `VenueLoginModalController.cs`(ApplyResult/CanSubmit) | 成功→`!IsOpen` / 失敗→`IsOpen`＋`StatusText` / allow_retry=false→OK 不可・再確認で解除 | 各結果で状態を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(ResultRenderingAndRetry) |
| VLOGIN-MODAL-06 | モード切替で prefill/ポート再導出 | `VenueLoginModalController.cs`(ApplyModeRefresh)／`BackcastWorkspaceRoot.cs`(OnVenueLoginMode) | kabu=port 再導出＋password clear / tachibana=ID/鍵 prefill（prod は空・ADR-0033） | mode 切替→port/prefill/`PasswordCleared` を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(ModeRefreshRederives) |
| VLOGIN-MODAL-07 | kabu 本体起動確認が OK を gate | `VenueLoginModalController.cs`(SetStationProbe/CanSubmit)／`BackcastWorkspaceRoot.cs`(RequestVenueProbe) | 未起動=OK 不可 / 再確認で起動=可 / tachibana は probe 非依存 | probe false/true で `CanSubmit` を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(StationProbeGatesOk) |
| VLOGIN-MODAL-08 | 秘密鍵 PEM ピッカー seam | `Win32FileDialog.cs`/`MacFileDialog.cs`(OpenPrivateKey)／`BackcastWorkspaceRoot.cs`(OnVenueLoginBrowse) | `IFileDialog.OpenPrivateKey` が .pem パスを返す→`SetKeyPath`→`CanSubmit`、cancel=null は無変更 | Stub で path/cancel を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(PemPickerSeam) |
| VLOGIN-MODAL-09 | 入力→送信→結果 roundtrip（Python-FREE fake executor） | `BackcastWorkspaceRoot.cs`(OnVenueLoginSubmit/DriveVenueLoginModal)／`WorkspaceEngineHost.cs`(SubmitVenueLogin) | submit が secret を executor へ渡し結果を `ApplyResult`・誤 PW=閉じず / 正 PW=閉じる＋zeroize | fake executor で 2 往復 assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(SubmitRoundtripViaFakeExecutor) |
| VLOGIN-MODAL-10 | 実 venue 認証（kabu verify / tachibana demo） | `WorkspaceEngineHost.cs`(SubmitVenueLogin) 実経路 | 実フォーム入力→`submit_venue_login`→実取引所認証→CONNECTED・成功でモーダル閉じ平文 zeroize | — | HITL専用（実 venue 接続・外部認証/秘密情報依存） | owner HITL（実アプリ Settings ▸ Connect） |
| VLOGIN-MODAL-11 | 実モーダル表示・実キーボード入力 | `VenueLoginModalOverlay.cs`(Build) | アプリ内モーダルが別 OS ウィンドウ無しで開く・実キーが InputField に入る・赤字エラー表示 | — | HITL専用（display/実キーボード device・headless では device 無し） | owner HITL（実アプリ Settings ▸ Connect） |
| VLOGIN-MODAL-12 | kabu パスワードが本物の入力欄として描画される（ADR-0042・「入力欄が無い」回帰の的） | `VenueLoginModalOverlay.cs`(Build/MakeField/Reflect/KabuPasswordFieldIsLivePasswordInput) | kabu 表示時に `_pwField` が `contentType=Password` の生きた InputField・PasswordText が controller へ round-trip・tachibana では kabu group 非表示 | overlay を Build→SetVisible→Reflect(kabu/tachibana)→`KabuPasswordFieldIsLivePasswordInput()` を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(KabuPasswordIsRealInputField) |

> VLOGIN-MODAL-10/11 は display/venue-bound＝HITL。owner が macOS 実機で「クリック→アプリ内モーダル→実約定」と
> 「crash しない」を目視確認する（findings 0131 §HITL）。VLOGIN-MODAL-12 はその affordance を EditMode で先取り gate する
> （controller のみを駆動していた旧 gate は overlay の見た目を見ず「入力欄が透明ラベル」を見逃した・findings 0132）。

## headless 認証半分（pytest 正本）

モーダルが OK で叩く `submit_venue_login` の中身（validate → `authenticate_tachibana`/`authenticate_kabu` →
finalize → CONNECTED、失敗時の error_code/status_text/allow_retry の出し分け）は **`test_venue_login_headless.py`**
が正本（`VLOGIN-HEADLESS` 相当・`PRODGATE-01` も移管）。本 C# 台本は「C# が入力を集め secret を char[] で渡し、
結果を描画する」配線だけを担う（2 ゲート分割）。

## 自動判定（合格条件）

- ログに `[E2E VENUE LOGIN MODAL PASS] <要約>` ＋ per-Action-ID 単一トークンタグ `[E2E VLOGIN-MODAL-NN PASS]`、
  プロセス exit 0（`-quit` 併用・self-failing gate）、`error CS\d+` 0 件。
- いずれかの観測点を落としたら `[E2E VENUE LOGIN MODAL FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus（`.cs` 各 section 末尾）: `Close` の `Password=""` / `CanSubmit` の StationRunning 依存 /
  `ApplyResult` の allow_retry / submit の secret 受け渡し / `MakeField` の `contentType=Password`（VLOGIN-MODAL-12）を壊すと対応 assert が必ず落ちる。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `VenueLoginModalE2ERunner` | EditMode・pure C#（fake executor）＋ overlay Build | VLOGIN-MODAL-01〜09・12 の正本 |
| `test_venue_login_headless.py` | pytest | headless 認証半分（submit/probe/form_init・PRODGATE-01）の正本 |
| 実アプリ（Settings ▸ Connect） | owner HITL（playmode/実機） | VLOGIN-MODAL-10/11（実 venue 認証・実表示）は実アプリのモーダルで記録。`VenueLoginSecretHitlHarness` は #181 で退役（tkinter 廃止で自前 credential form を持てず・実モーダルが上位互換） |
