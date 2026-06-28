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
> **不可侵**: kabu API パスワードを env/log/managed string に残さない（char[] 無バッファ・[[secret modal]] と同規律）。

## 対象サーフェス

venue ログインモーダル（頭脳 `VenueLoginModalController`〔plain C#・Python 非依存〕＋ chrome `VenueLoginModalOverlay`
〔ScreenSpaceOverlay sort 1000〕＋ root 連携 `BackcastWorkspaceRoot.OnVenueConnect/OpenVenueLoginModal/
DriveVenueLoginModal/OnVenueLogin*`）。RPC は `WorkspaceEngineHost.VenueLoginFormInit/VenueLoginProbeStation/
SubmitVenueLogin` → `submit_venue_login` 等（headless 認証）。kabu API パスワードは `SecretModalOverlay` と同じ
char[] 無バッファ方式（onTextInput で 1 文字ずつ・masked dot・managed string を作らない）。

## 対象ユーザー行動

Tachibana: 認証ID 入力 / 秘密鍵 PEM パス入力＋「参照…」ピッカー / demo・prod ラジオ。kabu: API パスワード入力
（char[]）/ verify・prod ラジオ / 本体ポート表示 / 「再確認」/ 本体未起動なら OK 無効。共通: OK（送信→headless 認証）/
キャンセル / 失敗時はモーダル内に日本語エラー赤字＋閉じずに再試行。

## 操作一覧表（網羅台帳）

| Action ID | ユーザー行動 / 状態遷移 | 入口（file:line） | 観測点 | 自動判定 | カバー状態 | 既存 Probe |
|---|---|---|---|---|---|---|
| VLOGIN-MODAL-01 | kabu パスワード 1 文字ずつ入力（char[]・managed string 不使用）・backspace・masked | `VenueLoginModalController.cs`(AppendSecretChar/BackspaceSecret/MaskedPassword)／`VenueLoginModalOverlay.cs`(OnTextInput) | char[] へ蓄積・`•` dot 数＝入力長・close で `SecretIsZeroed()` | Open→type→backspace→`SecretLength`/`MaskedPassword`、Close→`SecretIsZeroed` | 自動(E2E済) | `VenueLoginModalE2ERunner`(KabuSecretDrainAndMask) |
| VLOGIN-MODAL-02 | OK 有効判定 | `VenueLoginModalController.cs`(CanSubmit) | kabu=本体起動＋PW 非空 / tachibana=認証ID＋鍵パス非空 / busy 中は不可 | 各状態で `CanSubmit()` を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(SubmitGating) |
| VLOGIN-MODAL-03 | fields JSON 整形 | `VenueLoginModalController.cs`(BuildFieldsJson) | tachibana=`{"auth_id","key_path"}`・JSON escape / kabu=`{}`（secret は別経路） | escape 含む JSON を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(FieldsJsonShape) |
| VLOGIN-MODAL-04 | secret transient 渡し＋成功で zeroize | `VenueLoginModalController.cs`(TakeSecretTransient/ApplyResult/Close) | transient は入力 PW のコピー・controller バッファは結果まで保持・成功 close で zeroize | type→Take→success→`SecretIsZeroed` | 自動(E2E済) | `VenueLoginModalE2ERunner`(SecretTransientAndZeroize) |
| VLOGIN-MODAL-05 | 結果表示（成功=閉じる / 失敗=閉じず赤字＋再試行 / allow_retry=false は OK 据え置き） | `VenueLoginModalController.cs`(ApplyResult/CanSubmit) | 成功→`!IsOpen` / 失敗→`IsOpen`＋`StatusText` / allow_retry=false→OK 不可・再確認で解除 | 各結果で状態を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(ResultRenderingAndRetry) |
| VLOGIN-MODAL-06 | モード切替で prefill/ポート再導出 | `VenueLoginModalController.cs`(ApplyModeRefresh)／`BackcastWorkspaceRoot.cs`(OnVenueLoginMode) | kabu=port 再導出＋secret zeroize / tachibana=ID/鍵 prefill（prod は空・ADR-0033） | mode 切替→port/prefill/zeroize を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(ModeRefreshRederives) |
| VLOGIN-MODAL-07 | kabu 本体起動確認が OK を gate | `VenueLoginModalController.cs`(SetStationProbe/CanSubmit)／`BackcastWorkspaceRoot.cs`(RequestVenueProbe) | 未起動=OK 不可 / 再確認で起動=可 / tachibana は probe 非依存 | probe false/true で `CanSubmit` を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(StationProbeGatesOk) |
| VLOGIN-MODAL-08 | 秘密鍵 PEM ピッカー seam | `Win32FileDialog.cs`/`MacFileDialog.cs`(OpenPrivateKey)／`BackcastWorkspaceRoot.cs`(OnVenueLoginBrowse) | `IFileDialog.OpenPrivateKey` が .pem パスを返す→`SetKeyPath`→`CanSubmit`、cancel=null は無変更 | Stub で path/cancel を assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(PemPickerSeam) |
| VLOGIN-MODAL-09 | 入力→送信→結果 roundtrip（Python-FREE fake executor） | `BackcastWorkspaceRoot.cs`(OnVenueLoginSubmit/DriveVenueLoginModal)／`WorkspaceEngineHost.cs`(SubmitVenueLogin) | submit が secret を executor へ渡し結果を `ApplyResult`・誤 PW=閉じず / 正 PW=閉じる＋zeroize | fake executor で 2 往復 assert | 自動(E2E済) | `VenueLoginModalE2ERunner`(SubmitRoundtripViaFakeExecutor) |
| VLOGIN-MODAL-10 | 実 venue 認証（kabu verify / tachibana demo） | `WorkspaceEngineHost.cs`(SubmitVenueLogin) 実経路 | 実フォーム入力→`submit_venue_login`→実取引所認証→CONNECTED・成功でモーダル閉じ平文 zeroize | — | HITL専用（実 venue 接続・外部認証/秘密情報依存） | owner HITL（実アプリ Settings ▸ Connect） |
| VLOGIN-MODAL-11 | 実モーダル表示・実キーボード drain | `VenueLoginModalOverlay.cs`(Build/OnTextInput) | アプリ内モーダルが別 OS ウィンドウ無しで開く・実キーが char[] に入る・赤字エラー表示 | — | HITL専用（display/実キーボード device・headless では device 無し） | owner HITL（実アプリ Settings ▸ Connect） |

> VLOGIN-MODAL-10/11 は display/venue-bound＝HITL。owner が macOS 実機で「クリック→アプリ内モーダル→実約定」と
> 「crash しない」を目視確認する（findings 0131 §HITL）。

## headless 認証半分（pytest 正本）

モーダルが OK で叩く `submit_venue_login` の中身（validate → `authenticate_tachibana`/`authenticate_kabu` →
finalize → CONNECTED、失敗時の error_code/status_text/allow_retry の出し分け）は **`test_venue_login_headless.py`**
が正本（`VLOGIN-HEADLESS` 相当・`PRODGATE-01` も移管）。本 C# 台本は「C# が入力を集め secret を char[] で渡し、
結果を描画する」配線だけを担う（2 ゲート分割）。

## 自動判定（合格条件）

- ログに `[E2E VENUE LOGIN MODAL PASS] <要約>` ＋ per-Action-ID 単一トークンタグ `[E2E VLOGIN-MODAL-NN PASS]`、
  プロセス exit 0（`-quit` 併用・self-failing gate）、`error CS\d+` 0 件。
- いずれかの観測点を落としたら `[E2E VENUE LOGIN MODAL FAIL] <msg>` で exit 1。
- delete-the-production-logic litmus（`.cs` 各 section 末尾）: `Close` の zeroize / `CanSubmit` の StationRunning 依存 /
  `ApplyResult` の allow_retry / submit の secret 受け渡し を壊すと対応 assert が必ず落ちる。

## 既存 Probe との対応

| Probe | 種別 | 本台本での扱い |
|---|---|---|
| `VenueLoginModalE2ERunner` | EditMode・pure C#（fake executor） | VLOGIN-MODAL-01〜09 の正本 |
| `test_venue_login_headless.py` | pytest | headless 認証半分（submit/probe/form_init・PRODGATE-01）の正本 |
| 実アプリ（Settings ▸ Connect） | owner HITL（playmode/実機） | VLOGIN-MODAL-10/11（実 venue 認証・実表示）は実アプリのモーダルで記録。`VenueLoginSecretHitlHarness` は #181 で退役（tkinter 廃止で自前 credential form を持てず・実モーダルが上位互換） |
