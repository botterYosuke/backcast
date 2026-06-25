# 0109 — kabu prod login: 「トークン期限切れ」誤表示の修正 + PROD_KABU_API_PASSWORD 本番ログインテスト

方針: ADR-0027（prod-allow env ゲート廃止 / DEV_* prefill 廃止）を参照する。本 ADR は編集しない
（自己保護条項）。本スライスの下位決定をここに固定する。

## 不具合（owner 報告 2026-06-25）

手動で「Connect kabuStation (Prod)」→ API パスワードを入力してログインすると、
「**トークン期限切れ。API パスワードを確認して再試行してください**」が表示される。kabuステーション本体は
起動・ログイン済み。

### 機序（コード裏取り）

1. この文言は `kabusapi_login_form_state.auth_failure_view` が `KABU_TOKEN_EXPIRED` のときだけ返す。
2. `KABU_TOKEN_EXPIRED` は `kabusapi_login_flow._map_exception` が `KabuTokenExpiredError` を受けたときだけ返す。
3. `KabuTokenExpiredError` は `kabusapi_auth.check_response` が **HTTP 401 のときだけ** raise する。
4. login ダイアログの唯一の auth 呼び出しは `fetch_token`（`/token` エンドポイント）。
5. kabu 公式エラー表（`ptal/error.html`）: `/token` の 401 実体は **code 4001013
   「トークン取得失敗：kabuステーションがログインしている状態で、APIパスワードが不正」**。

→ **login ダイアログで 401 が出る原因は常に「API パスワード不正」**であり、トークン失効ではない
（トークン発行リクエスト時点で失効すべき既存トークンは存在しない）。`KABU_TOKEN_EXPIRED` は
login flow + form_state 以外に consumer が無い（C#・watchdog・他 Python から不参照を grep 確認）。
従って文言の再テキスト化は安全。

### 真因（owner の env 設定が効かない件）

owner は `.env` に `PROD_KABU_API_PASSWORD` を置いたが**どのコードも読まない**（repo 全体 grep で read 0 件）。
手動ダイアログは ADR-0027 D3 + PRODGATE-08 により process env を一切読まない（空欄で開きユーザー入力のみ）。
→ env var は手動ログインには無効。**自動 `credentials_source="env"` テスト専用の fixture** としてのみ意味を持つ。

## 設計の木（grill HITL 2026-06-25）

- **D1（メッセージ修正）**: presenter の error code `KABU_TOKEN_EXPIRED` を廃し、401 の body code で
  **2 つに分岐**する（実 prod 応答の empirical 発見で grill の当初仮説を訂正・下記参照）:
  - 4001007 / 4001017（本体が口座へ未ログイン）→ **`KABU_STATION_NOT_LOGGED_IN`**
    「kabuステーション本体が口座にログインしていません。本体でログインしてから再試行してください」
  - 4001013 ほか（ログイン済みだが API パスワード不正）→ **`KABU_AUTH_REJECTED`**
    「API パスワードが正しくありません。確認して再試行してください」
  どちらも allow_retry=True。旧実装は両者を一律「トークン期限切れ。API パスワードを確認」と誤表示していた。
  401 の body code は `KabuTokenExpiredError.body_code` に温存（`.code` は契約どおり 401 のまま）。
  transport 例外クラス `KabuTokenExpiredError`（HTTP 401 の汎用名）は改名しない。

  ### empirical 発見（grill 仮説の訂正・2026-06-25）

  grill の当初仮説は「login の 401 は常に 4001013＝パスワード不正」だった。だが **KABU-LIVE-PROD-01
  pytest を owner の実 prod 本体（18080・起動済み）に対して走らせたところ、HTTP 401 + body
  `Code: 4001007「ログイン認証エラー」` が返った**＝本体は *起動* されているが *口座へ未ログイン*。
  「API パスワード不正」一律表示も誤りになるため、body code で分岐する D1 に訂正した。
  → owner への含意: 今回のログイン不能の実体は（少なくとも検査時点では）**本体がブローカー口座へ
  未ログイン**。kabuステーション本体でログイン（早朝強制ログアウト後の再ログイン等）してから再試行する。
- **D2（env var 配線）**: `credentials_source="env"` は env により var を出し分ける —
  `env="prod"` → **`PROD_KABU_API_PASSWORD`** / `env="verify"` → `DEV_KABU_API_PASSWORD`。
  prod と verify は kabuステーション本体で**別パスワード**なので分離が安全（単一 var 流用は今回の 401 を再発させる）。
  読むのは adapter `kabusapi.py`（既に env を読む層）であって login ダイアログではない → **PRODGATE-08 順守**
  （source-scan ゲートは `kabusapi_login_flow.py` のみを対象にする）。
- **D3（テスト 3 本）**:
  - **KABU-AUTH-REJECT-01**（CI unit・`tests/test_kabu_login_auth_rejected.py`）: バグ修正の RED→GREEN。
    `_map_exception(KabuTokenExpiredError(401, ...)) == KABU_AUTH_REJECTED` と view 文言・allow_retry を固定。
    本体不要で CI 常時走行。
  - **KABU-LIVE-PROD-01**（owner HITL pytest・`tests/test_kabu_prod_login_live.py`）: `PROD_KABU_API_PASSWORD` と
    prod 本体 18080 が揃ったときだけ走り、無ければ `skip`（conftest が SKIP タグを rollup に中立計上）。
    `KabuStationAdapter(environment="prod")` を `credentials_source="env"` でログインし `is_logged_in()` を assert。
  - **KabuLiveProdE2ERunner.cs**（owner HITL Unity batchmode）: 既存 `KabuLiveE2ERunner` の prod 版。
    `PROD_KABU_API_PASSWORD` を読み `VenueLogin("KABU","env","prod")` → CONNECTED を gate。タグ `[E2E KABU-LIVE-PROD-01 …]`。

### ADR-0027 との整合（surface）

ADR-0027 D4 の Consequence は「自動 E2E runner は environment_hint を verify/demo にハードコード固定し
本番非接触」とする。本スライスの **KABU-LIVE-PROD-01 / KabuLiveProdE2ERunner は意図的に prod を叩く唯一の例外**で、
owner HITL 専用・`PROD_KABU_API_PASSWORD` 未設定や本体未起動では走らない二重ガードで守る。これは ADR-0027 の
中核決定（prod-allow env ゲートの廃止）に矛盾しない（新たな machine フラグを足さない）ため新規 ADR は不要。
本 findings に記録し ADR-0027 を参照する（書き戻さない）。

## 再走手順

- unit: `cd python && uv run pytest tests/test_kabu_login_auth_rejected.py -v`
- prod live (owner HITL・prod 本体 18080 起動＋ログイン＋API 有効＋`.env` に `PROD_KABU_API_PASSWORD`):
  `cd python && uv run pytest tests/test_kabu_prod_login_live.py -v`
  または Unity: `pwsh scripts/run-live-e2e.ps1 -Method KabuLiveProdE2ERunner.Run`
</content>
</invoke>
