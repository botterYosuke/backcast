# findings 0114 — ログインダイアログの資格情報 prefill 復活（demo/verify・debug 限定）

方針: **ADR-0033**（ADR-0027 D3 を supersede）。本 findings は反転した assert・新設した prefill
ロジック・run_dialog の toggle 再導出・RED→GREEN・再走手順・実機検証を固定する。ADR には書き戻さない。

関連: ADR-0033（正本）／ADR-0027（D1/D2/D4 は不変・本 finding は D3 のみ反転）／ADR-0023（v4r9 公開鍵認証）。

## きっかけ（実機調査）

owner が /tachibana にログインできなかった。実機調査で原因は **資格情報の取り違え**:
v4r9 公開鍵認証ダイアログ（認証ID＋秘密鍵ファイル）に、廃止済みのパスワード時代の値
（`DEV_TACHIBANA_USER_ID`=`uxf05882` / `DEV_TACHIBANA_PASSWORD`=`vw20sr9h`）を入れて `AUTH_FAILED`
（しかも認証ID欄に PASSWORD、秘密鍵欄に USER_ID と取り違え）。正しい demo 資格情報
（`DEV_TACHIBANA_AUTH_ID_DEMO` 48 文字 + `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO`）で実 demo サーバ
（`demo-kabuka.e-shiten.jp`）へのログインは成功することを `tachibana_auth.login` 直叩きで確認。
48 文字認証IDや PEM パスを毎回手入力するのは非現実的で取り違えの温床——`.env` の値を初期表示する
（owner 依頼 2026-06-25）。

## 設計（grill-with-docs HITL 2026-06-25）

- prod は prefill **しない**（demo/verify のみ）。kabu prod は実弾なので OK 一押しで auth が飛ぶのを避ける。
- release ビルド（`IS_DEBUG_BUILD` False）は一切 prefill しない（`DEV_*` を debug 限定で読む既存規律 R10/S1）。
- env 読みは **pure presenter `build_form_init`** に閉じ、`run_dialog`（tkinter widget）は `os.environ` を
  直接読まない → ADR-0027 の PRODGATE-08 不変条件を維持。

## env キー対応

| venue | mode | ダイアログ欄 | env キー |
|---|---|---|---|
| 立花 | demo | 認証ID / 秘密鍵ファイル | `DEV_TACHIBANA_AUTH_ID_DEMO` / `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` |
| 立花 | prod | （prefill しない・手入力） | — |
| kabu | verify | API パスワード | `DEV_KABU_API_PASSWORD` |
| kabu | prod | （prefill しない・手入力） | — |

`os.environ` は `engine/paths.py:_load_dotenv_once()`（import 時に自動・`setdefault`）が `.env` から流し込む。

## 実装

- `tachibana_login_form_state.build_form_init`: `FormInit` に `auth_id_prefill` / `key_path_prefill` を追加。
  `_demo_prefill()` が `IS_DEBUG_BUILD` かつ demo のとき `DEV_TACHIBANA_AUTH_ID_DEMO` /
  `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` を返す（prod / release は `("","")`）。
- `kabusapi_login_form_state.build_form_init`: `FormInit` に `api_password_prefill` を追加。
  `_verify_prefill()` が `IS_DEBUG_BUILD` かつ verify のとき `DEV_KABU_API_PASSWORD` を返す。
- `tachibana_login_flow` / `kabusapi_login_flow`: StringVar 初期値を presenter の prefill から取る。
  **モード切替は `Radiobutton.config(command=_on_mode_change)`** で配線し、`build_form_init(mode)` を呼び直して
  prefill を再導出（demo/verify→値 / prod→空）。
  - ⚠️ **`StringVar.trace_add` は使わない**: trace コールバックが `#133` の gc.collect() teardown を
    生き残る参照を作り、`test_login_dialog_tk_teardown` の「3 root が creating thread で finalize される」を
    破る（実測: finalized=0／root が生存）。widget `command=` は teardown_tk が `_tclCommands` を掃くので安全。

## 回帰ゲート（RED→GREEN）

新 `python/tests/test_login_prefill.py`（`@pytest.mark.scenario` で rollup へ）:

| Action-ID | 不変条件 | RED litmus |
|---|---|---|
| PREFILL-01 | 立花 demo は debug で `DEV_TACHIBANA_AUTH_ID_DEMO`/PATH を prefill | prefill 配線を消すと空 |
| PREFILL-02 | 立花 prod は prefill しない（demo/prod 両 env set でも空） | mode 分岐を消し常時 `_demo_prefill()` にすると **RED 実測**（prod に demo 値が出る） |
| PREFILL-03 | kabu verify は debug で `DEV_KABU_API_PASSWORD` を prefill | prefill 配線を消すと空 |
| PREFILL-04 | kabu prod は prefill しない（実弾は手入力） | mode 分岐を消すと RED |
| PREFILL-05 | release（`IS_DEBUG_BUILD` False）は demo/verify でも prefill しない | `IS_DEBUG_BUILD` ゲートを消すと RED |

維持: **PRODGATE-08**（`*_login_flow.py` は `environ`/`getenv` を読まない・source-scan）。env 読みは
presenter に閉じたので GREEN のまま。

撤去: 旧 **PRODGATE-05/06**（「prefill しない」を assert）は ADR-0033 で反転したため `test_prod_gate_abolished.py`
から撤去。`test_tachibana_secret.py::test_login_form_ignores_second_password_env` は「auth_id は prefill しない」を
誤って混在 assert していたので、**第二暗証番号（F-H5）の非 surface のみ**を assert するよう修正（auth_id/key_path の
prefill は ADR-0033 で許容・第二暗証番号は prefill 対象外）。

## 実機検証

1. **実 demo サーバ login**: 正しい demo 資格情報で `tachibana_auth.login(is_demo=True)` が session URL 5 本を取得（成功）。
2. **prefill→login の端から端**: `build_form_init("demo")` が返す `auth_id_prefill`（48 文字）/ `key_path_prefill`
   （実在 PEM）で `load_private_key_from_file` → `login` が `demo-kabuka.e-shiten.jp` に成功。ダイアログが初期表示
   する値がそのまま認証に通ることを確認。
   - ⚠️ owner が screenshot で `AUTH_FAILED` だったのは `USER_ID`/`PASSWORD`（パスワード時代の廃止キー）を欄に
     入れていたため。v4r9 は `AUTH_ID` + 秘密鍵。prefill 復活でこの取り違えが構造的に起きなくなる。

## 再走手順

```
cd python && ./.venv/Scripts/python.exe -m pytest tests/test_login_prefill.py \
  tests/test_prod_gate_abolished.py tests/test_tachibana_secret.py \
  tests/test_login_dialog_tk_teardown.py tests/test_inproc_prompt_login.py -q
```

Unity AFK は不要（prefill は埋め込み Python presenter の挙動・tkinter teardown は display-bound で
`_display_available()` skip ガード付き）。実 venue login は HITL/手動（demo サーバ・上記実機検証）。
