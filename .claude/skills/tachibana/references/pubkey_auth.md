# v4r9 公開鍵認証（RSA 暗号化仮想 URL）

v4r9 で立花 e支店 API のログインが **電話認証単独 → 公開鍵（RSA）認証**へ移行した。一次ソースは [`samples/e_api_login_pubkey.py/e_api_login_pubkey.py`](../samples/e_api_login_pubkey.py/e_api_login_pubkey.py) と同ディレクトリ README。

## 概要

- 認証は **認証ID（`sAuthId`）＋秘密鍵**で行う。パスワードはログインリクエストに送らない。本人性は「応答で返る暗号化仮想 URL を秘密鍵で復号できること」で証明する設計。
- **本格稼働までは移行期間**: 公開鍵認証＋電話認証の併用。公開鍵を設定し、API 用電話認証を済ませてから **3 分以内にログイン**する。移行終了後は電話認証不要・公開鍵認証のみ。終了時期は e支店 HP 参照。
- **本番とデモは別セット**: 認証ID・秘密鍵・公開鍵は本番環境とデモ環境で各々別物。デモ環境で v4r9 を使うにはデモ標準 web 画面にログインしてデモ専用セットを取得する。

## ログインリクエスト（`CLMAuthLoginRequest`）

リクエスト項目は **`sAuthId` ベース**で、`sUserId` / `sPassword` は無い:

| 項目 | 内容 |
| :--- | :--- |
| `p_no` | リクエスト通番（ログイン時は **`1` 固定**、`./file_info_p_no.txt` に保存） |
| `p_sd_date` | 送信日時 `YYYY.MM.DD-hh:mm:ss.sss`（JST） |
| `sCLMID` | `"CLMAuthLoginRequest"` |
| `sAuthId` | 認証ID |
| `sJsonOfmt` | `"5"`（R5） |

送信先は `{BASE_URL}/auth/?{JSON}`（R2）。

## 応答仮想 URL の RSA 復号

応答の 5 本の仮想 URL（`sUrlRequest` / `sUrlMaster` / `sUrlPrice` / `sUrlEvent` / `sUrlEventWebSocket`）は **RSA 暗号化されて返る**。クライアントが秘密鍵で復号する:

```python
import base64
from Cryptodome.Cipher import PKCS1_OAEP
from Cryptodome.Hash import SHA256
from Cryptodome.PublicKey import RSA   # pycryptodome（import 名は Cryptodome）

def decrypt_sUrl(encoded_encrypted_sUrl: str, private_key_obj) -> str:
    decryptor = PKCS1_OAEP.new(private_key_obj, hashAlgo=SHA256)
    clean = encoded_encrypted_sUrl.strip().replace('"', '')
    decoded = base64.b64decode(clean)
    return decryptor.decrypt(decoded).decode("utf-8-sig").strip()  # BOM 対応
```

- パディングは **OAEP**、内部ハッシュは **SHA-256**。
- 復号前に **base64 デコード**、復号後は **`utf-8-sig`**（BOM 対応）でデコード。
- 依存は **pycryptodome**（import 名 `Cryptodome`）。秘密鍵は `RSA.import_key(pem_text)` でオブジェクト化する。

## 未読書面判定（`sUrlRequest` 空）

`p_errno=0 && sResultCode=0` でも **`sUrlRequest` が空文字**なら契約締結前書面が未読で API 利用不可（ブラウザの標準 web 画面で書面確認が必要）。v4r9 でこの判定は旧 `sKinsyouhouMidokuFlg=="1"` フラグから **空 URL 検出**に置き換わった。Python 側は `UnreadNoticesError` で早期脱出する（R3 / R6）。

## 新エラーコード・新応答フィールド

- **`p_errno="9"`** = システム・サービス停止中（利用時間外）。デモ環境の利用時間はデモ案内ページ参照。
- 新応答フィールド:
  - `sUpdateInformWebDocument` — 交付書面更新予定日
  - `sUpdateInformAPISpecFunction` — API リリース予定日
  - `sSecondPasswordOmit` — 第二暗証番号省略区分

## 資格情報保護（サンプル流儀・必須ではない）

サンプルは認証ID・秘密鍵を平文保存せず、共通鍵暗号 **Fernet**（`cryptography` パッケージ）で `secure_config.enc` に暗号化する:

- 復号鍵は env **`API_DECRYPT_KEY`** に分離保管（暗号化ファイルと復号鍵を分けることで、片方だけの漏洩では認証情報を復元させない）。
- `e_api_encode_auth.py` が暗号化設定ファイル生成ツール。
- 秘密鍵・第二暗証・ログイン応答ファイルの流出に備え、**接続元の固定 IP 制限を強く推奨**。
- 第二暗証番号は従来どおり env / ファイルに置かない（R10）。

Fernet・固定 IP は公開鍵認証自体の必須要件ではなく、漏洩リスク低減の実装例。OS 資格情報ストア / DPAPI / Secret Service / HSM 等の同等以上の方式に置き換えてよい。

## 出力ファイル（サンプル）

- `./file_info_p_no.txt` — 現在の p_no（ログイン時は 1）。
- `./.auth/file_login_response.txt` — **復号済み**ログイン応答（`utf-8-sig`、パーミッション 600）。

## backcast 実装上の含意

- **復号は Python 側に集約する**（`tachibana_auth.py` 想定）。Rust に秘密鍵・RSA 復号を持たせない（architecture.md §1「立花プロトコル固有ヘルパーは Rust に書かない」を踏襲）。
- 復号後の仮想 URL もセッション秘密（R3 / R10）。ログ・テレメトリでマスクする。
- pycryptodome（`Cryptodome`）依存を公開鍵復号の実装時点で `python/` の依存に加える。
- 一次ソースは [`samples/e_api_login_pubkey.py/`](../samples/e_api_login_pubkey.py/)。
