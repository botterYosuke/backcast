# findings 0087 — 立花 e支店 API v4r8→v4r9 公開鍵認証 移行（issue #92 修正）

方針: [ADR-0023](../adr/0023-tachibana-v4r9-pubkey-auth-cutover.md)。真因の追い込みは
[findings 0053](0053-e2e-tachibana-live-login-order-fill.md) §issue #92。ドメイン不変条件は
[`/tachibana` SKILL.md](../../.claude/skills/tachibana/SKILL.md) / [references/pubkey_auth.md](../../.claude/skills/tachibana/references/pubkey_auth.md)。

## スコープ（owner と grill で確定 2026-06-22）

issue #92 の受け入れ条件は (1) `[E2E TACHIBANA-LIVE PASS]` の場中 clean run、(2) root cause を
findings に追記。root cause は v4r9 cutover 未了と確定済み（0053）。本スライスは **コードを v4r9 へ
完全カットオーバー**し、live 緑取得を owner HITL に渡せる状態にする。owner の指示は「実装コストは
度外視・理想形・手を抜くな」＝全経路（env / prompt / session_cache / probe / LiveE2ERunner）を v4r9 化。

owner は demo の v4r9 登録を完了済み（`e_api_authid.txt` / `e_api_public_key.pem` /
`e_api_private_key.pem` を取得）。

## 設計の木（確定した下位決定）

| # | 決定 | 値 | 根拠 |
|---|---|---|---|
| D1 | base URL | `e_api_v4r9` 一本（demo/prod とも） | ADR-0023。部分移行は不可（暗号化 URL は v9 ログインのみ） |
| D2 | ログイン項目 | `sAuthId` 単独（`sUserId`/`sPassword` 廃止） | pubkey_auth.md、`e_api_login_pubkey.py:427` |
| D3 | 仮想 URL 復号 | base64 → `PKCS1_OAEP(SHA256)` → `utf-8-sig`.strip()、5 本とも | sample `decrypt_sUrl`、Python 集約 |
| D4 | 認証情報供給 | ① Fernet ② dev env。① 優先、両欠落でエラー。**demo/prod 別セット — 本番=無印 / デモ=`_DEMO`**（demo: `DEV_TACHIBANA_AUTH_ID_DEMO` / `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` / `secure_config_demo.enc` / `API_DECRYPT_KEY_DEMO`） | ADR-0023 D4 |
| D5 | 秘密鍵オブジェクト | `RSA.import_key(pem_text)`。PEM は `utf-8-sig`+strip 読み | sample `load_api_credentials` |
| D6 | 未読書面判定 | `sUrlRequest` 空文字 → `UnreadNoticesError`（`sKinsyouhouMidokuFlg` 廃止） | pubkey_auth.md §未読 |
| D7 | 新エラー | `p_errno="9"` → 利用時間外。`check_response` で分類 | R6 / pubkey_auth.md |
| D8 | login 時 p_no | サンプルは `1` 固定だが backcast は `PNoCounter` 単調増加を維持（R4 / 既存 resume 契約と整合） | R4。サンプルの 1 固定は単一プロセス前提 |
| D9 | session_cache schema | 復号済み URL 文字列のまま不変。復元経路無改変 | 既存 `tachibana_file_store` |
| D10 | prompt(tkinter) | パスワード欄廃止。秘密鍵ファイル選択＋認証ID 入力（または configured creds で確認のみ） | pubkey にパスワード概念なし |
| D11 | 第二暗証 | 不変（発注時 GUI modal / env 不可、`DEV_TACHIBANA_SECOND`） | R10 / findings 0053 |
| D12 | 依存追加 | `pycryptodome`（`Cryptodome`）+ `cryptography`（Fernet） | D3/D4 |

## crypto 不変条件（実装で守る）

- RSA 復号は **OAEP パディング＋内部ハッシュ SHA-256** 固定。base64 デコード前に `strip().replace('"','')`
  でクレンジング、復号後は `utf-8-sig`（BOM 対応）＋strip。
- 秘密鍵 PEM・認証ID・復号後仮想 URL は全てセッション秘密。ログ/テレメトリは `mask_secrets` 経由。
  秘密鍵オブジェクトを repr/log しない。
- 認証情報の解決は `is_debug_build` ガード下（release は env を読まない）。

## 実装（2026-06-22 完了）

| ファイル | 変更 |
|---|---|
| `python/pyproject.toml` | `pycryptodomex>=3.20`（import 名 `Cryptodome`）+ `cryptography>=42.0`（Fernet）追加 |
| `tachibana_url.py` | `BASE_URL_PROD`/`DEMO` を `e_api_v4r9` へ |
| `tachibana_pubkey.py`（新） | `load_private_key` / `decrypt_s_url`（base64→PKCS1_OAEP(SHA256)→utf-8-sig）。鍵素材を含まない `PubkeyCryptoError` |
| `tachibana_credentials.py`（新） | `resolve_credentials(is_demo, is_debug_build)`：Fernet 優先→dev env（is_debug_build 限定）。**demo/prod 別セット — owner 規約 本番=無印 / デモ=`_DEMO`**（demo: `DEV_TACHIBANA_AUTH_ID_DEMO` / `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` / `secure_config_demo.enc` / `API_DECRYPT_KEY_DEMO`、prod: 無印）。取り違え防止 |
| `tachibana_auth.py` | `login(auth_id, private_key, ...)` へ。sAuthId 送信・5 本復号・未読=sUrlRequest 空→`UnreadNoticesError`。`check_response` に `p_errno=9`→`ServiceOutOfHoursError`、`sKinsyouhouMidokuFlg` 撤去 |
| `tachibana.py` | env 経路を `resolve_credentials`→`_auth_login(auth_id, private_key)` へ。`_ENV_USER_ID`/`_ENV_PASSWORD` 撤去 |
| `tachibana_login_form_state.py` / `tachibana_login_flow.py` | tkinter からパスワード欄廃止、認証ID＋秘密鍵ファイル選択（参照ボタン）へ |
| `tachibana_login_messages.py` | `p_errno=9` を service-out-of-hours バナーへ、文言を認証ID/秘密鍵へ |
| `scripts/tachibana_ws_probe.py` | `resolve_credentials`→v4r9 ログインで再走可能に |
| `TachibanaLiveE2ERunner.{cs,md}` | demo 専用ゲートなので env を `DEV_TACHIBANA_AUTH_ID_DEMO`/`DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` へ |

## RED→GREEN（回帰ゲート: `python/tests/test_tachibana_login_pubkey.py`）

テスト用 RSA 鍵を生成し、公開鍵で仮想 URL 5 本を暗号化→base64→shift_jis JSON 応答を mock し、
`login()` が秘密鍵で復号して https/wss を取り戻すことを assert（5 ケース: sAuthId 送信・復号・未読・
p_errno=9・wrong-key）。

- **RED**: `_decrypt_virtual_urls` の復号呼び出しを no-op 化すると、復号されない base64 が
  `_validate_virtual_urls` の `https://` 判定で弾かれ `LoginError("virtual URL invalid")`
  → happy-path / wrong-key の 2 ケースが FAIL（delete-the-production-logic litmus 成立）。
- **GREEN**: 復号を戻すと 5/5 PASS。tachibana スイート全体 69 passed、回帰なし。

## 受け入れ（本スライス）

- [x] base URL / login / 復号 / 未読 / p_errno=9 を v4r9 化（コード）
- [x] オフライン回帰（pytest, mock）GREEN — 公開鍵認証応答 mock を含む（`test_tachibana_login_pubkey.py` 5/5、tachibana 69/69）
- [x] probe を v4r9 ログインで再走可能に
- [x] LiveE2ERunner / env 経路が v4r9 creds で動く（コード）。**[E2E TACHIBANA-LIVE PASS] は owner 場中 HITL（下記留保）**

## 実ログイン probe（2026-06-22 19:48 JST, demo・閉局後）

owner 依頼で `tachibana_ws_probe.py` 相当を実走（demo・実弾なし）。**v4r9 コード経路は end-to-end で健全**と確認:
認証情報解決（`auth_id` 48 char alnum・`e_api_authid.txt` と byte 一致／秘密鍵 PEM ロード）→ `sAuthId`
リクエスト送信 → TLS/Shift-JIS デコード/JSON parse → `check_response` 分類、すべて正常動作。コードのバグは出ず。

サーバ応答（初回試行）: **`p_errno=-1`, `p_err="引数（sAuthId:[…]）エラー。"`**（sAuthId 引数エラー）。
- ⚠️ **真因は当初の AuthID 値が誤り**だった（その時 unsuffixed に入っていた `TWp***`。後に owner が正しい
  demo AuthID `T0R***` を `_DEMO` に設定したら成功）。**demo は電話認証不要・サービス時間外でもログイン可**
  （owner 指摘どおり。当初の「電話認証/時間外」推測は誤りで、純粋に AuthID 値の不一致だった）。
  教訓: `p_errno=-1 "引数(sAuthId)エラー"` ＝ **AuthID 値そのものが無効/不一致**のサイン。

## 実ログイン成功（2026-06-22 19:57 JST, owner HITL・demo）

owner が **API 用電話認証 → 3 分以内**に v4r9 ログインを実走し **成功**:

```
LIVE_LOGIN_OK
url_request_scheme=https / url_master_scheme=https / url_price_scheme=https / url_event_scheme=https
url_event_ws_scheme=wss
zyoutoeki_kazei_c_present=True
```

これは `login()` が TachibanaSession を返した（=check_response 通過・sUrlRequest 非空・**5 本とも復号成功**・
`_validate_virtual_urls` 通過）ことを意味する。**復号が no-op なら scheme は base64 のままで `_validate_virtual_urls`
が `LoginError("virtual URL invalid")` を投げる**（litmus の RED 状態）ので、4×https + 1×wss が揃った時点で
**RSA 復号が実機で全 5 本成功**したことの証左。offline gate（6/6）と合わせ mock/実機の両方で v4r9 認証＋復号を実証。
→ **issue #92 の認証・セッション root cause（v4r8 旧式セッション）は解消**。

## EVENT WS leg 実証 — #92 本丸 解消（2026-06-22 閉局後・demo・電話認証なし）

`tachibana_ws_probe.py` を demo（`_DEMO` creds・電話認証なし）で実走:

```
login (demo) auth_id=T0R*** ... login OK. url_event_ws = 'wss://demo-kabuka.e-shiten.jp/e_api_v4r9/event_ws/<token>/'
TCP/TLS/WS handshake OK
<KP> <FD>(69 fields) <SS> <US>×7 …   frame counts: {'KP':4,'FD':1,'SS':1,'US':7}
```

- **旧症状 `ST p_errno=2 "session inactive."` が一切来ず**、代わりに **実 EVENT フレーム（FD=板/歩み 69 fields・
  KP・SS・US）が stream**された＝**EVENT WS が v4r9 セッションを受理**。#92 の 100% 拒否（issue #85/#92 で 4 回連続
  再現）は**解消**。閉局後でも snapshot/status frame が流れ、WS 受理を確認できた。
- probe の verdict を更新: 旧ロジックは ST フレームのみ見て「INCONCLUSIVE」と誤判定したため、**「ST 拒否なし＋
  実フレーム stream ＝ PASS（#92 RESOLVED）」**を判定軸に追加（captured frames だと PASS branch に入る）。

## PROD(本番) でも実証 — #92 解消（2026-06-22・read-only probe）

owner が **本番の電話認証 → 3 分以内**に prod probe（`tachibana_ws_probe.py 7203 prod`・READ-ONLY＝発注なし）を実走:

```
login (PROD) auth_id=TWp*** ... login OK. url_event_ws = 'wss://price-kabuka.e-shiten.jp/e_api_v4r9/event_ws/<token>/'
TCP/TLS/WS handshake OK
frame counts: {'KP':4,'FD':1,'SS':1,'US':25}
[WS-PROBE PASS] no ST rejection and EVENT frames streamed — v4r9 session ACCEPTED by EVENT WS. #92 is RESOLVED.
```

- 本番（host `price-kabuka.e-shiten.jp`）でも **EVENT WS が v4r9 セッションを受理・実フレーム stream・`ST p_errno=2` なし**。
- 途中の `sResultCode=10089`（電話認証エラー＝「登録電話番号への着信が時間内に無い」）は #92 と無関係の運用ゲートで、
  正しい本番認証電話 → 3 分以内ログインで解消。prod の AuthID/復号は健全（10089 まで到達＝引数エラーにならない）。
- probe verdict 修正（ST 拒否なし＋実フレーム＝PASS）も **live で PASS を正しく出力**＝修正の動作確認。

## 受け入れ最終

- [x] base URL / login / 復号 / 未読 / p_errno=9 を v4r9 化（コード）— offline 71/71・実機 login 成功
- [x] **EVENT WS が v4r9 セッションを受理（`ST p_errno=2` 消滅・実フレーム stream）＝#92 root cause 実証解消（demo ＋ prod 両方）**
- [x] root cause を findings に記録
- [ ] `[E2E TACHIBANA-LIVE PASS]`（login→**発注→約定**の統合ゲート）は **場中（9:00–11:30 / 12:30–15:30 JST）**＋
  第二暗証で owner HITL 実走（#92 のブロッカーは demo/prod とも除去済み・残りは成行約定の場中確認のみ）。
  owner 手順（demo）: `e_api_private_key.pem`（デモ用）をリポジトリ外（600）に置き、`.env` に
  `DEV_TACHIBANA_AUTH_ID_DEMO`（demo `e_api_authid.txt` の中身）＋`DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` を
  設定（本番は無印キー）。旧 `DEV_TACHIBANA_USER_ID`/
  `PASSWORD` は不要。`DEV_TACHIBANA_SECOND`（第二暗証）は維持。
- 全 venv 共通の data-dependent kernel/duckdb テスト 15 件は本変更と無関係（`S:\jp\stocks_daily\*.duckdb`
  欠落・本機ローカルデータ不在で pre-existing FAIL）。
