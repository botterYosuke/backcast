---
status: accepted
---

# 立花 e支店 API を v4r9 公開鍵認証へ完全カットオーバーする

## 文脈

立花 demo の EVENT WebSocket が、ログインに成功した直後でも `ST p_errno=2 / "session inactive."`
で 100% 拒否される現象（issue #85→#92）を、standalone probe と公式資料 v4r8→v4r9 diff から
追い込んだ結果、真因は **立花サーバが v4r9（`e_api_v4r9`）へ移行し、v4r8 ログインが返す旧式
（非暗号化）の EVENT WS 仮想 URL のセッションを無効扱いしている** ことと確定した。URL の形・
handshake 不備ではなく **セッション無効判定**で、v4r9 README 7.(3)「セッション無効化時」の逐語
（`p_errno:[2] / "session inactive."`）と一致する。

v4r9 の認証は v4r8 と互換でない:
- ログインは `sUserId`+`sPassword` → **`sAuthId`（認証ID）単独**（パスワードを送らない）。
- 応答の仮想 URL 5 本（`sUrlRequest`/`sUrlMaster`/`sUrlPrice`/`sUrlEvent`/`sUrlEventWebSocket`）が
  **RSA 公開鍵で暗号化されて返り、クライアントが秘密鍵で復号必須**（base64 → PKCS1_OAEP/SHA256 →
  `utf-8-sig`）。本人性は「秘密鍵で復号できること」で証明する。
- 利用設定画面で **API 利用宣言＋公開鍵登録**（対の秘密鍵を保管）が前提。本番とデモは別セット。
- 未読書面判定が旧 `sKinsyouhouMidokuFlg=="1"` フラグ → **`sUrlRequest` 空文字検出**へ置換。
- 新エラー `p_errno="9"`（システム・サービス停止中＝利用時間外）。

## 決定

backcast を **v4r9 公開鍵認証へ完全カットオーバー**する。v4r8 の併存（base URL 混在・tel-auth
フォールバック）は採らない:

1. **base URL を `e_api_v4r9` 一本**にする（`tachibana_url.py` の `BASE_URL_PROD`/`BASE_URL_DEMO`）。
2. **ログインは `sAuthId` 方式のみ**。`tachibana_auth.login()` の引数は `user_id`/`password` を捨て
   `auth_id` + 秘密鍵オブジェクトを取る。
3. **仮想 URL 5 本の RSA 復号を Python 側に集約**（pycryptodome `Cryptodome`）。Rust に秘密鍵・復号を
   持たせない（architecture.md §1）。復号後の仮想 URL もセッション秘密としてマスク（R3/R10）。
4. **認証情報の供給は 2 系統**（解決優先順）:
   - ① 本番級 = 立花サンプル互換の **Fernet `secure_config.enc` ＋ `API_DECRYPT_KEY`**
     （`{auth_id, private_key(PEM)}` を 1 ファイルに封入、復号鍵だけ env に分離）。
   - ② 開発 = **`DEV_TACHIBANA_AUTH_ID*`（値）＋ `DEV_TACHIBANA_PRIVATE_KEY_PATH*`（PEM パス）**。
     既存 `DEV_TACHIBANA_*` debug-env 流儀と一貫。秘密鍵 PEM はリポジトリ外・600。
   どちらも `is_debug_build`/secret 取り扱い規約（R10）を満たす。Fernet は release でも有効、dev env は
   debug build 限定。両方欠落なら明示エラー。
   - **demo/prod は別セット**: 立花は認証ID・秘密鍵・公開鍵が demo/prod で別物（pubkey_auth.md・公式サンプル
     `e_api_login_pubkey.py:48-51` で確認）。`resolve_credentials(is_demo=...)` が env を切り替える。
     **owner 規約: 本番=無印、デモ=`_DEMO` サフィックス**:
     - prod（無印）= `API_DECRYPT_KEY` / `secure_config.enc` / `DEV_TACHIBANA_AUTH_ID` / `DEV_TACHIBANA_PRIVATE_KEY_PATH`
     - demo（`_DEMO`）= `API_DECRYPT_KEY_DEMO` / `secure_config_demo.enc` / `DEV_TACHIBANA_AUTH_ID_DEMO` / `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO`
     demo の認証情報を prod へ送る取り違えを構造的に防ぐ。prod live は従来どおり `TACHIBANA_ALLOW_PROD=1`
     ゲート（R1）配下で、本 ADR では未実走。
5. **未読書面判定**を `sUrlRequest` 空文字検出へ、**`p_errno="9"`** を `ServiceOutOfHoursError` 相当へ。
6. session_cache の on-disk スキーマ（復号済み URL 文字列）は不変＝復元経路は無改変。

## 却下した選択肢

- **v4r8 を残して EVENT WS だけ v4r9 に差す**: ログイン応答（暗号化 URL）が v4r9 でしか返らないため、
  EVENT WS の有効セッションは v4r9 ログインからしか生まれない。部分移行は不可能。
- **Fernet 一本（公式サンプルそのまま）**: dev で `e_api_encode_auth.py` を毎回回す摩擦が大きく、
  既存 `DEV_TACHIBANA_*` 直接 env 流儀と乖離する。本番級として併存はさせるが必須にはしない。
- **秘密鍵を Rust 側で扱う**: architecture.md §1「立花プロトコル固有ヘルパーは Rust に書かない」に反する。

## 帰結

- 新依存: **pycryptodome**（import 名 `Cryptodome`、RSA-OAEP/SHA256）と **cryptography**（Fernet）。
- prompt（tkinter）経路はパスワード欄を廃し、秘密鍵ファイル選択＋認証ID 入力へ作り替える。
- 移行期間は「API 用電話認証 → 3 分以内にログイン」が必要（移行終了後は不要・終了時期は e支店 HP）。
- 旧 `DEV_TACHIBANA_USER_ID`/`DEV_TACHIBANA_PASSWORD` はログインで不要化。`DEV_TACHIBANA_SECOND`
  （第二暗証＝発注時）は不変。

設計の木の下位決定は [docs/findings/0087-tachibana-v4r9-pubkey-migration.md](../findings/0087-tachibana-v4r9-pubkey-migration.md)。
真因の追い込みは [docs/findings/0053](../findings/0053-e2e-tachibana-live-login-order-fill.md) §issue #92。

---
本 ADR の決定は確定である。再考は本 ADR を supersede する新 ADR を要し、本ファイルの編集では行わない。
