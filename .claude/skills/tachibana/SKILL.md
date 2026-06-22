---
name: tachibana
description: 立花証券 e支店 API（v4r7/v4r8/v4r9、tachibana）でコードを書く・レビューする・計画書を検証するときの必読スキル。「立花」「e支店」「ｅ支店」「tachibana」「kabuka.e-shiten.jp」「demo-kabuka」「sUrlRequest」「sUrlEvent」「CLMAuthLoginRequest」「CLMKabuNewOrder」「sCLMID」「sResultCode」「p_errno」「sJsonOfmt」「公開鍵認証」「sAuthId」「秘密鍵」「RSA」「PKCS1_OAEP」「decrypt_sUrl」「secure_config.enc」「API_DECRYPT_KEY」「e_api_login_pubkey」「pubkey」「p_errno=9」「TachibanaSession」「PNoCounter」「p_no」「session_cache」「zyoutoeki_kazei_c」「fetch_instruments」「銘柄マスタ」「instrument master」「CLMEventDownload」「CLMIssueMstKabu」「CLMIssueSizyouMstKabu」「CLMYobine」「master download」「ListInstruments」「銘柄リスト取得」「universe fetch」「計画書レビュー」「plan document」「設計書」「--live-venue」「live-venue TACHIBANA」「launch.json」「tasks.json」「VS Code 起動設定」「.zed/debug.json」「Zed debug.json」「Zed 起動設定」「Zed デバッグ起動」「CodeLLDB」「envFile」「.env が読み込まれない」「env が渡らない」「環境変数が読まれない」「LIVE_VENUE」「デバッグ起動」「起動オプション追加」「build_instruments_from_master_records」「_extract_depth」「FdFrameProcessor」「sSizyouC」「sBaibaiTaniNumber」「sZyouzyouSizyou」「sBaibaiTani」「sSizyoubetuBaibaiTani」「market_to_suffix」「tachibana_market_to_suffix」「市場コード欠落」「売買単位欠落」「銘柄ビルド失敗」「銘柄が 0 件」「fetch_instruments が空」「picker に 2 件しか出ない」「No depth data」「板データが出ない」「depth data が表示されない」「volume 空」「bv=""」「float("")」「qty キー」「size キー」「if bp and bv」に触れたら必ず起動する（VS Code の launch.json / tasks.json や Zed の .zed/debug.json に立花 live 起動を足すときも、demo 環境デフォルト・.env 経由の DEV_TACHIBANA_USER_ID/PASSWORD・第二暗証番号は env に置かない・ポート 19876 衝突の落とし穴を参照すること。Zed では `.zed/debug.json` があると `.vscode/launch.json` は読まれず、CodeLLDB に `.env` を渡すには `envFile` フィールドが必須。autospawn 経路で立花 live にするには backcast が `LIVE_VENUE` env を読んで `--live-venue` を backend に渡す配線が必要）。CLMAuthLoginRequest によるログイン、公開鍵認証（sAuthId ログイン・RSA 暗号化仮想 URL の秘密鍵復号・移行期間の電話認証併用）、仮想 URL（sUrlRequest/Master/Price/Event/EventWebSocket）の取り扱い、`{virtual_url}?{JSON文字列}` 独自形式、p_no/p_sd_date/sJsonOfmt の必須化、p_errno と sResultCode の二段判定、Shift-JIS、空配列が "" で返る件、第二暗証番号の必須化、CLMKabuNewOrder のパラメータ、EVENT/WebSocket の ^A^B^C 区切り、CLMEventDownload マスタの特殊フロー、backcast（in-proc 単一バイナリ）ローカル起動時の debug/release・.env・セッションキャッシュ・ポート衝突の落とし穴を扱う。
---

# 立花証券・ｅ支店・ＡＰＩ スキル

立花 venue 統合は **Python 側 `python/engine/exchanges/tachibana*.py` にロジックを集約**する。Rust 側はチャート描画と IPC ライフサイクルイベントに責務を絞り、立花プロトコル固有のヘルパー（URL ビルド・Shift-JIS デコード・p_no 採番・WS 接続）は Rust に書かない（[architecture.md §1](../../../docs/✅tachibana/architecture.md)）。本スキルは Claude が API 仕様に正しく沿って Python / IPC コードを書くためのルール集である。

> **最新の進行ステータス・タスク完了状況は [docs/✅tachibana/implementation-plan.md](../../../docs/✅tachibana/implementation-plan.md) を参照**。本ファイルには腐りやすいマイルストン情報を書かない（不変ルールのみを書く）。

## 参照リソース

- **公式マニュアル（必読の一次資料）**
  - HTML リファレンス: [manual_files/mfds_json_api_ref_text.html](manual_files/mfds_json_api_ref_text.html)
    - `ComT1..ComT7` の章立てで共通説明・認証機能・業務（REQUEST）・マスタ・時価・EVENT・結果コード表を網羅
    - 共通説明は `ComP1..ComP7`（専用 URL・インタフェース概要・ブラウザ利用・共通項目/認証・マスタ・EXCEL VBA）
    - sCLMID の章タイトルがそのまま HTML の `id` 属性になっている（例: `#CLMKabuNewOrder`）。Claude は該当 `id` セクションを開いて仕様確認する
  - 同梱 PDF / Excel（`manual_files/` 配下に実ファイルあり）:
    - [e_api_interface_v4r9.pdf](manual_files/e_api_interface_v4r9.pdf) — **v4r9 インタフェース仕様（最新の一次資料）**
    - [e_api_web_access_v4r9.pdf](manual_files/e_api_web_access_v4r9.pdf) — v4r9 ブラウザ動作確認
    - [e_api_sample_feed_get_v4r9.xlsm](manual_files/e_api_sample_feed_get_v4r9.xlsm) — v4r9 フィード取得サンプル（Excel）
    - [api_request_if_v4r7.pdf](manual_files/api_request_if_v4r7.pdf) — REQUEST I/F 利用方法・データ仕様（v4r7、互換参照）
    - [api_request_if_master_v4r5.pdf](manual_files/api_request_if_master_v4r5.pdf) — マスタデータ利用方法
    - [api_web_access.xlsx](manual_files/api_web_access.xlsx) — ブラウザからの動作確認例
  - 外部参照のみ（`manual_files/` には同梱されていない）:
    - `api_overview_v4r7.pdf` — インタフェース概要（ComP2 からリンク）
    - `api_event_if_v4r7.pdf` / `api_event_if.xlsx` — EVENT I/F 利用方法・データ仕様（ComT6 からリンク、同内容の PDF/Excel 版）
    - これら外部資料を参照する場合はブラウザ側で e-shiten.jp の公開 URL を確認する。ローカルでは Python サンプルに抜粋コメントがあるのでそれを補助資料にする
- **バージョン表記**: 現行は **v4r9**（`e_api_v4r9`、マニュアル v4.9-000 @2026.05.16）。最大の変更は**公開鍵認証の導入**（[references/pubkey_auth.md](references/pubkey_auth.md) 参照）。本格稼働までの移行期間は旧 **v4r8**（`e_api_v4r8`）も引き続き有効。v4r8/v4r7 ドキュメントはパラメータ仕様の互換参照として残す
- **Python サンプル（1 サンプル = 1 サブディレクトリ）**: `samples/e_api_*_{tel,pubkey}.py/`（v4r9 で主要サンプルが `_tel`→`_pubkey`＝公開鍵認証版にリネーム。一部の派生版は `_tel` のまま残存）
  - 各ディレクトリに `LICENSE` / `README.md` / `e_api_*.py` が同梱
  - ログイン（公開鍵認証）: `e_api_login_pubkey.py/e_api_login_pubkey.py`。実行時に復号済みログイン応答を `.auth/file_login_response.txt` へ生成する（旧 `e_api_login_response.txt` / `e_api_account_info.txt` の同梱実例 JSON は廃止）。同ディレクトリに `e_api_encode_auth.py`（暗号化設定 `secure_config.enc` 生成ツール）・`セットアップマニュアル_updated_20260609.html`・`認証ID・秘密鍵等の取得方法.pdf` が同梱。詳細は [references/pubkey_auth.md](references/pubkey_auth.md)
  - 新規注文（現物）: `e_api_order_genbutsu_buy_pubkey.py` / `e_api_order_genbutsu_sell_tel.py`（sell は tel のまま残存）
  - 新規注文（信用）: `e_api_order_shinyou_buy_shinki_pubkey.py` / `e_api_order_shinyou_sell_shinki_pubkey.py`
  - 返済注文（信用）: `e_api_order_shinyou_{buy,sell}_hensai_pubkey.py` / `e_api_order_shinyou_{buy,sell}_hensai_kobetsu_tel.py`（kobetsu＝建玉個別指定版は tel のまま残存）
  - 訂正/取消: `e_api_correct_order_tel.py` / `e_api_cancel_order_tel.py` / `e_api_cancel_order_all_tel.py`
  - 一覧取得: `e_api_get_orderlist_pubkey.py` / `e_api_get_orderlist_detail_tel.py`（detail 版は tel のまま）/ `e_api_get_genbutu_kabu_list_pubkey.py` / `e_api_get_shinyou_tategyoku_list_tel.py`
  - 余力: `e_api_get_kanougaku_genbutsu_pubkey.py` / `e_api_get_kanougaku_shinyou_pubkey.py`
  - マスタ: `e_api_get_master_tel.py`（全量ダウンロード）/ `e_api_get_master_kobetsu_tel.py`（個別列取得）
  - ニュース: `e_api_get_news_header_tel.py` / `e_api_get_news_body_tel.py`（本文は Base64）
  - 時価履歴: `e_api_get_histrical_price_daily.py` / `e_api_get_price_from_file_tel.py`
  - プッシュ: `e_api_event_receive_tel.py`（EVENT HTTP long-polling）/ `e_api_websocket_receive_tel.py`（WebSocket 版）
  - 総合例（スタンドアロン、直下に配置）: `samples/e_api_sample_v4r9.py` / `samples/e_api_sample_v4r9.txt`（旧 `e_api_sample_v4r8.py` / `.txt` も残存）
  - 参考（非 Python）: `samples/Excel_VBA_api_sample_tel.xlsm/`（VBA 版サンプル一式）/ `samples/e_api_test_compress_v4r2_js.py/`（レスポンス gzip 圧縮の動作確認）
- **計画文書**: [docs/✅tachibana/](../../../docs/✅tachibana/)（README / spec / architecture / data-mapping / implementation-plan / open-questions）

**原則**: 公式マニュアルが最優先。Python サンプルはマニュアル記載のパラメータを動作コードで示す参考実装。矛盾があればマニュアルに従う。

## サブリファレンス（必要時のみ読む）

- [references/sclmid.md](references/sclmid.md) — sCLMID 一覧（認証・業務・マスタ・時価・EVENT）。新しい機能追加時に該当行を引く
- [references/order_params.md](references/order_params.md) — `CLMKabuNewOrder` の入出力項目定石、訂正・取消との関係
- [references/event_protocol.md](references/event_protocol.md) — EVENT / WebSocket の `^A^B^C` 区切り規約、`p_evt_cmd` 種別、URL パラメータ
- [references/master_download.md](references/master_download.md) — `CLMEventDownload` ストリーム処理の特殊ルール、`sTargetCLMID` 一覧
- [references/pubkey_auth.md](references/pubkey_auth.md) — **v4r9 公開鍵認証**（`sAuthId` ログイン・RSA 暗号化仮想 URL の秘密鍵復号・移行期間運用・`p_errno="9"`・資格情報保護）

---

## 立花 venue 利用の前提条件

立花証券 venue を使うには以下が**すべて**必要:

1. **電話認証済みの立花証券 e支店 口座** — e支店 API は電話認証（電話番号照合）完了後でないとプログラマチックログインが成立しない。電話認証未済のアカウントでは tkinter ログインダイアログが弾かれる。
2. **e支店 API サービスの申込み** — API 利用は口座開設とは別途申込みが必要な場合がある（立花証券営業窓口に確認すること）。
3. **デモ環境デフォルト** — デモ環境（`demo-kabuka.e-shiten.jp`）に接続する。本番接続には `TACHIBANA_ALLOW_PROD=1` env が必要（設定しても demo/prod 選択は tkinter ダイアログで都度行う）。
4. **CI demo ジョブは `workflow_dispatch` のみ** — `pytest -m demo_tachibana` を回す CI ジョブ（`.github/workflows/tachibana-demo.yml`）は手動トリガ限定。PR/push への組み込みは閉局帯ヒットによる偽陰性を防ぐため禁止（open-questions.md Q21）。

---

## いつこのスキルを発動するか

- 立花証券 API に対する新規エンドポイント・新しい `sCLMID` を追加するとき
- 立花 Python モジュール（`tachibana.py` / `tachibana_auth.py` / `tachibana_codec.py` / `tachibana_url.py` / `tachibana_ws.py` / `tachibana_master.py` 等）のリクエスト/レスポンス型を追加・修正するとき
- Python 側の EVENT / WebSocket 受信パース（`tachibana_codec.py` / `tachibana_ws.py`）を触るとき
- 注文入力・訂正・取消のパラメータを扱うとき
- `sResultCode` / `p_errno` のハンドリングを設計するとき
- ユーザーが「立花」「e支店」「ｅ支店」「tachibana」に触れたとき
- `backcast` をローカル起動して立花セッションを必要とする検証を行うとき（下記「運用クイックスタート」を参照）

---

## 運用クイックスタート（ローカル起動で立花セッションを作る）

E2E 検証やエージェント体験検証で **`backcast`（Bevy フロントエンド + in-proc PyO3 Python エンジン、単一バイナリ）** を起動し、立花セッションを使いたい場合の手順。**コードを書く前にまずこの節を読むこと。** ここに書かれた含意を見落とすと「env 設定したのにログイン画面が空のまま」「catalog パスが壊れる」等で数十分単位の時間を失う。

> **現行の正は in-proc 単一バイナリ**（`BACKEND_TRANSPORT=inproc`）。gRPC backend を別プロセス（`python -m engine` @ `:19876`）で起動する旧経路もコードには残るが、立花 live は in-proc を既定とする。Zed の F5 から起動する具体手順は **§S7**。

### S0. Unity フロントエンド（現行）で立花 live を完全自動化する（C# E2E）

> ⚠️ 下記 **S1–S7 は Rust 期の旧経路**（`target/debug/backcast` / Zed `.zed/debug.json` / cargo build / ポート 19876）。本リポジトリの現行フロントエンドは **Unity + pythonnet（同一プロセス埋め込み、ADR-0001）** で、立花 live を batchmode で完全自動化する E2E は `Assets/Tests/E2E/Editor/TachibanaLiveE2ERunner.{cs,md}`（設計: `docs/findings/0053`）を雛形にする。要点:

- **ログイン**: `WorkspaceEngineHost.InitializePython("TACHIBANA")` → `host.VenueLogin("TACHIBANA","env","demo",cb)`。`credentials_source="env"` は tkinter を spawn せず `os.environ` の `DEV_TACHIBANA_USER_ID/PASSWORD` を直読み（`tachibana.py`）。接続確認は poll の `venue_state`（`VenueConnectionViewModel.IsConnected`）。
- **資格情報**: C# は **`EnvConfig.Get`**（process env 優先 → `<repo>/.env` → `<repo>/python/.env`）で解決する。⚠️ **`PythonRuntimeLocator.ProjectRoot` は `<repo>/python`（repo root ではない）**。`ProjectRoot` で `.env` パスを組むと `python/.env` を見て creds を取りこぼす（gate が常に FAIL）。`USER_ID/PASSWORD` は login 前に `os.environ` へ設定（`os.environ.SetItem` を Py.GIL 内で）。
- **第二暗証（完全自動化の肝）**: env / `os.environ` に載せない。発注中に push される `SecretRequired`（`panel.SecretRequiredCount` の edge）を `host.Lanes.SubmitSecret(requestId, char[])`（urgent-secret lane、別スレッド）で応答する。`DEV_TACHIBANA_SECOND` は `char[]` で保持し clone を渡す（`SubmitSecret` が payload を zeroize）。
- **約定観測**: `place_order` の ack は `status="ACCEPTED"`、FILLED は EC(WS) push → `OrderEvent` → `panel.FilledOrderCount`。成行は**場中（前場 09:00–11:30 / 後場 12:30–15:30 JST）のみ約定**。
- **本番ガード**: `environment_hint="demo"` 固定 + `TACHIBANA_ALLOW_PROD=1` 検出で発注前に拒否（R1）。
- **batchmode 補足**: `pythonnet PythonEngine.Exec + __main__ 読み戻し`は効かない（Exec が `__main__` 名前空間で走らない）。Python 関数（例 `is_market_open`）は **`Py.Import` → `InvokeMethod` で直接呼ぶ**。

### S1. ビルドは **debug** を使う（release では自動ログイン不可）

`DEV_TACHIBANA_USER_ID` / `DEV_TACHIBANA_PASSWORD` / `DEV_TACHIBANA_DEMO`（既定 `true`）による自動ログインは **Python 側 [`tachibana_login_form_state.build_form_init()`](../../../python/engine/exchanges/tachibana_login_form_state.py)** が `is_debug_build=True` のときだけ読む。release ビルドでは `is_debug_build=False` となり `DEV_TACHIBANA_*` を一切読まず常にユーザー入力を要求する。Rust 側に env 取込みコードは追加しない（経路が Python に閉じる）。

| ビルド | 自動ログイン | デモトグル自動化 | 用途 |
| :--- | :--- | :--- | :--- |
| `target/debug/backcast` | ✅（`is_debug_build=True` で env を読む） | ✅（`DEV_TACHIBANA_DEMO`） | E2E・検証・開発 |
| `target/release/backcast` | ❌（env を**完全無視**） | ❌ | 本番配布のみ |

**禁止**: release で起動してログイン画面が空なのを「env 未設定」と誤診断すること。release は env を読まない。

### S2. `.env` は `backcast` 本体が**読まない**。シェルで export するか Zed envFile で渡す

`backcast` は `dotenv` 系クレートを使っていない。起動前に自前でロードする（Zed なら §S7 の `envFile` が等価）:

```bash
# macOS / bash — in-proc 単一バイナリ起動（issue #84 の正準コマンド）
set -a; source .env; set +a
BACKEND_ENABLED=true BACKEND_TRANSPORT=inproc PYTHON_ENGINE_PATH=python \
  BEVY_ASSET_ROOT=$PWD PYTHONPATH=$PWD/.venv/lib/python3.14/site-packages \
  RUST_LOG=info LIVE_VENUE=TACHIBANA ./target/debug/backcast
```

- `PYTHONPATH` の `python3.14` は **venv の実バージョンに合わせる**（`ls .venv/lib/` で確認）。
- `LIVE_VENUE=TACHIBANA` は `src/backend_supervisor.rs` / `src/trading.rs` が読み、in-proc backend を立花 live モードで起動する。未設定なら replay/backtest。

`.env` の想定キー（`DEV_TACHIBANA_*` は debug 専用、**Python 側のみが読む**）:

```
DEV_TACHIBANA_USER_ID=...          # 立花ユーザーID
DEV_TACHIBANA_PASSWORD=...         # ログインパスワード
DEV_TACHIBANA_DEMO=true            # demo 環境フラグ（未設定でも demo 既定）
```

⚠️ **`.env` のパス値はクオートを付けない**（`ARTIFACTS_PATH=/path`、`"/path"` は不可）。`src/trading.rs` は `ARTIFACTS_PATH` を生のまま `Path::join("jquants-catalog")` するため、Zed envFile 等クオートを剥がさないパーサだと `"/path"/jquants-catalog` になり catalog エラー。スペース無しパスにクオートは不要。

**第二暗証番号は env に置かない**（F-H5）。ログイン時には収集せず、発注時に GUI の secret modal で取得しメモリのみ保持する（idle forget タイマーで自動消去）。`.env` に `DEV_TACHIBANA_SECOND_PASSWORD` を書いても Python は読まない。

**`DEV_TACHIBANA_DEMO` 既定値は `true`**。未設定でも demo URL のみを叩く（F-Default-Demo）。**本番接続は `TACHIBANA_ALLOW_PROD=1` を併用したときに限り解禁**（Q7、`build_form_init` の `allow_prod`）。`DEV_IS_DEMO` / `TACHIBANA_USER_ID` / `TACHIBANA_PASSWORD` といった旧名は採用しない。

### S3. 2 回目以降の起動は **セッションファイルキャッシュ** が利用される

初回ログイン成功後、セッション（仮想 URL 一式）は Python が JSON ファイルに保存する（[`tachibana_file_store.py`](../../../python/engine/exchanges/tachibana_file_store.py)、`session_file_path()`）。keyring は使用しない。保存先:

- 既定: `$LOCALAPPDATA`（Windows）または `~/.cache`（macOS/Linux） / `the-trader-was-replaced` / `tachibana` / `tachibana_session.json`
  - macOS 実パス: `~/.cache/the-trader-was-replaced/tachibana/tachibana_session.json`
- override: `TACHIBANA_SESSION_PATH` env でファイルパスを直接指定

**次回起動時は JST 当日付のキャッシュなら自動復元を試みる**。ログ順序:

```
INFO -- Attempting to restore tachibana session from file cache
INFO -- Session file loaded, validating...
INFO -- Tachibana session validated successfully, restoring
```

- **初回だけ**: `DEV_TACHIBANA_USER_ID` / `DEV_TACHIBANA_PASSWORD` が必要（電話認証は別途ユーザーが済ませている前提）。
- **2 回目以降（JST 当日限り）**: キャッシュが有効なら env 未設定でも起動できる。
- **セッションをリセット** したいときは上記 JSON ファイルを削除する。

セッションが切れている（`p_errno="2"` / 夜間閉局越え / 翌日）と起動時検証が失敗するので、env を設定して再ログインする。

### S4. `backcast` GUI は CLI 引数（`--ticker` 等）を**読まない**

`backcast` に銘柄・期間を渡す clap CLI は無い。replay 既定銘柄・期間は `.env` の `REPLAY_INSTRUMENT_ID` / `REPLAY_START_DATE` / `REPLAY_END_DATE` / `REPLAY_GRANULARITY` と、GUI 内の Strategy Editor / instrument picker から指定する。起動後の操作はすべて GUI（Bevy UI）で行う。

### S5. ポート衝突（:19876）は **gRPC subprocess モードのみ**の問題

`:19876` は **gRPC backend（`python -m engine --port 19876`）を別プロセス起動する旧経路** の bind 先。**in-proc（`BACKEND_TRANSPORT=inproc`）では TCP サーバを立てない**ので衝突は起きない。gRPC モードを使う場合だけ、起動前に占有を確認:

```bash
# macOS / bash
lsof -nP -iTCP:19876 -sTCP:LISTEN
kill <pid>
```

### S6. 起動時ログで拾うべきサイン

| ログ | 意味 | 対処 |
| :--- | :--- | :--- |
| `Attempting to restore tachibana session from file cache` → `Session file loaded, validating...` → `validated successfully` | 正常（ファイルキャッシュ復元） | そのまま利用可 |
| `Catalog path does not exist: .../jquants-catalog` | `ARTIFACTS_PATH` 未設定/誤り、または値にクオートが残っている（S2） | `.env` の `ARTIFACTS_PATH`（クオート無し）を確認 |
| `engine WS bind failed on 127.0.0.1:19876` | gRPC subprocess モードのポート衝突（S5）。in-proc では出ない | 既存プロセスを kill、または in-proc に切替 |
| `Tachibana daily history fetch failed: ... code=6 ... p_no ... エラー` | 起動時の p_no 競合（R4）。セッション復元と並行の history fetch が逆転 | 機能影響は軽微（既知の軽微バグ）。`next_p_no()` 呼び出しパスを見直す価値あり |

### S7. Zed の F5 で `backcast` を立花 live(demo) 起動する（in-proc / macOS）

**`.zed/debug.json`**（プロジェクト直下。`.gitignore` 対象外なのでコミット可・機密なし）:

```jsonc
[{
  "label": "backcast (TACHIBANA live / demo, in-proc)",
  "adapter": "CodeLLDB",
  "request": "launch",
  "program": "$ZED_WORKTREE_ROOT/target/debug/backcast",
  "cwd": "$ZED_WORKTREE_ROOT",
  "build": { "command": "cargo", "args": ["build", "--bin", "backcast"] },
  "envFile": "$ZED_WORKTREE_ROOT/.env",            // DEV_TACHIBANA_* / ARTIFACTS_PATH を供給
  "env": {
    "BACKEND_ENABLED": "true",
    "BACKEND_TRANSPORT": "inproc",                  // ← gRPC 別起動しない
    "PYTHON_ENGINE_PATH": "python",
    "BEVY_ASSET_ROOT": "$ZED_WORKTREE_ROOT",
    "PYTHONPATH": "$ZED_WORKTREE_ROOT/.venv/lib/python3.14/site-packages",  // venv の実 python バージョンに合わせる
    "RUST_LOG": "info",
    "LIVE_VENUE": "TACHIBANA"                        // 配線: src/backend_supervisor.rs / src/trading.rs が読む
    // 本番(実弾)接続は別 issue のゲートまで設定しない: "TACHIBANA_ALLOW_PROD": "1"
  }
}]
```

**F5 割当**: Zed は VSCode と違い F5 が既定で未割当のことがある。`~/.config/zed/keymap.json`（macOS の Zed 設定は `~/.config/zed/`）に:

```json
[{ "bindings": { "f5": "debugger::Start" } }]
```

落とし穴・要点:
- **`LIVE_VENUE=TACHIBANA` だけ＝ demo**（`demo-kabuka.e-shiten.jp`）。本番は `TACHIBANA_ALLOW_PROD=1` を併用したときのみ解禁（R1 / Q7）。リスクゲートに入るまで付けない。
- **`.env` の値は引用符を付けない**。消費側（`src/trading.rs` の `ARTIFACTS_PATH`）は値を生のまま `Path::join` するため、`ARTIFACTS_PATH="/path"` のクオートを envFile パーサが剥がさないと `"/path"/jquants-catalog` になり catalog エラー。スペース無しパスはクオート不要。
- **debug ビルド限定**で自動ログイン（`DEV_TACHIBANA_*`）が効く。release は env を完全無視（S1）。`build` で `--bin backcast` を debug ビルドさせる。
- **CodeLLDB** アダプタは初回デバッグ時に Zed が自動取得（`~/Library/Application Support/Zed/debug_adapters/CodeLLDB`）。
- **PyO3 リンク先 Python は `.cargo/config.toml` で venv に固定する（F5 ビルド失敗の最頻原因）**: Zed の `build` は `/bin/zsh -i -c 'cargo build'` を venv 未 activate で走らせる。`PYO3_PYTHON` 未指定だと PyO3 が PATH の `python3`（macOS なら `/usr/bin/python3`＝システム 3.9）を拾い、`ld: library not found for -lpython3.9` でリンク失敗 → F5 が CodeLLDB 起動前に中断する（症状は「F5 で error: linking with cc failed」）。シェルで動くのは venv activate で `python` が 3.14 に解決されるため。修正は `.cargo/config.toml` の `[env]` に `PYO3_PYTHON = { value = ".venv/bin/python", relative = true }` を置く（Zed・素のシェル・autofix headless すべてのビルドが venv python3.14 を使う）。`debug.json` の `env`/`envFile` は**実行時**にしか効かず、PyO3 の**ビルド時**リンクには効かないので debug.json 側では直せない。
- 第二暗証番号は env / `.env` に置かない。発注時に GUI modal でプロンプト（R10）。

---

## 絶対に守るべきルール

### R1. 本番環境では実弾が飛ぶ／URL リテラルは 1 箇所限定

- **本番 URL** `https://kabuka.e-shiten.jp/e_api_v4r9/` に接続すると、発注関連 API は**実際に市場へ注文が出る**。約定は取り消せない（移行期間は旧 `e_api_v4r8` も有効）
- **開発・テストはデモ環境** `https://demo-kabuka.e-shiten.jp/e_api_v4r9/` を使う
- **URL リテラルの所在は 1 箇所限定（F-L1）**: `BASE_URL_PROD` / `BASE_URL_DEMO` を持てるのは **`python/engine/exchanges/tachibana_url.py` の冒頭定義 1 箇所のみ**。Rust 側に本番 URL リテラルを書かず、Python から venue 設定経由で受け取る（architecture.md §1）
- Python 側のテストでは `BASE_URL_DEMO` またはテスト用モック URL のみを使う（`HTTPXMock` 既定）

### R2. URL 形式は独自仕様（クエリ構造ではない）

- マニュアル根拠: `mfds_json_api_ref_text.html#ComP1`「【アクセス方法】」
- REQUEST I/F はすべて `{virtual_url}?{JSON 文字列}` の形で送る
  - `?` 以降に **JSON オブジェクトの文字列をそのまま**付ける（`key=value&...` 形式ではない）
  - `httpx` の `params=` / `requests` の `params=` / `urllib` の `params=` は**使えない**
  - URL 構築は **Python 側 `tachibana_url.py`** に集約。`build_request_url(base, json_obj)`（REQUEST 用、JSON 文字列パス）と `build_event_url(base, params)`（EVENT 用、key=value 形式）を別関数として実装する
- EVENT I/F だけは例外で **通常の `key=value&key=value` 形式**（`p_rid`, `p_board_no`, `p_gyou_no`, `p_issue_code`, `p_mkt_code`, `p_eno`, `p_evt_cmd`）。REQUEST と混同しない
- 認証は `{BASE_URL}/auth/?{JSON}` と `/auth/` セグメントを挟む。それ以外は仮想 URL に直接付ける（仮想 URL 自体の末尾に `/` が含まれている）

### R3. 認証フローと仮想 URL の寿命

1. **公開鍵認証**（v4r9〜）: 事前に認証ID・秘密鍵・公開鍵を取得し公開鍵を登録する。**移行期間は公開鍵認証＋電話認証の併用**で、API 用電話認証を済ませてから **3 分以内にログイン**する（移行終了後は電話認証不要。終了時期は e支店 HP。詳細は [references/pubkey_auth.md](references/pubkey_auth.md)）
2. `CLMAuthLoginRequest` で **`sAuthId`（認証ID）ベース**にログインする（パスワードは送らない）。応答の以下 5 個の**仮想 URL**（= セッション固有、1 日券）は **RSA 暗号化されて返り、秘密鍵で復号必須**（`PKCS1_OAEP` / `SHA256` / base64 デコード / `utf-8-sig`。[references/pubkey_auth.md](references/pubkey_auth.md)）:
   - `sUrlRequest` — 業務機能（REQUEST I/F）
   - `sUrlMaster` — マスタ機能（REQUEST I/F）
   - `sUrlPrice` — 時価情報機能（REQUEST I/F）
   - `sUrlEvent` — 注文約定通知（EVENT I/F, HTTP long-polling）
   - `sUrlEventWebSocket` — EVENT I/F WebSocket 版（スキームは `wss://`）
   - 応答には他に `sZyoutoekiKazeiC`（譲渡益課税区分）などが含まれる。発注時の同名フィールドにはこの値をそのまま使うのが定石（実行時に復号して `samples/e_api_login_pubkey.py/.auth/file_login_response.txt` に保存される応答を参照）
3. 夜間の閉局まで仮想 URL は有効。閉局後は電話認証からやり直し
4. **仮想 URL はセッション秘密**。ログ出力・テレメトリ送信時はマスクすること
5. 永続化は **Python 側 [`tachibana_file_store.py`](../../../python/engine/exchanges/tachibana_file_store.py)** が `tachibana_session.json` ファイルキャッシュで行う（JST 当日付で有効判定）。keyring は使用しない
6. ログイン応答パース → セッション変換は **Python 側 `tachibana_auth.py`** で実装する。`p_errno` → `sResultCode` → **未読書面判定（`sUrlRequest` が空文字なら契約締結前書面 未読で利用不可）** の 3 段チェックを強制し、途中のいずれかが NG なら `LoginError` / `UnreadNoticesError` で早期脱出する（v4r9 で未読判定は旧 `sKinsyouhouMidokuFlg` フラグから **空 URL 検出**に置き換わった）

### R4. `p_no` と `p_sd_date` は全リクエストに必須

- `p_no` — リクエスト通番。**リクエストごとに単調増加**する整数（最大 10 桁）。セッション復元後も必ず前回より大きい値を使う
  - Python 側 `tachibana_auth.next_p_no()` を使う（`asyncio` 単一スレッド前提の単調増加カウンタ、Unix 秒で初期化）。**自前採番禁止**
- `p_sd_date` — 送信日時 `YYYY.MM.DD-hh:mm:ss.sss`（JST）。UTC で送らない
  - Python 側 `tachibana_auth.current_p_sd_date()` を使う（JST 固定）

### R5. `sJsonOfmt`="5" を必ず指定する

- "5" = bit1 ON（ブラウザで見やすい形式）+ bit3 ON（引数項目名称での応答）
- 指定しないとレスポンスのキーが数値 ID になりデシリアライズできない
- マスタダウンロード（`CLMEventDownload`）は "4" を使う（一行 1 データで保存しやすい — [references/master_download.md](references/master_download.md) 参照）

### R6. エラーは 2 段階で判定する

```
if p_errno != "0"       → API 共通エラー（認証・接続レベル）
if sResultCode != "0"   → 業務処理エラー（パラメータ不正・残高不足など）
```

- **両方**をチェックする。片方だけではエラーを見逃す
- `p_errno` はレスポンスで**空文字列のことがある**ため、`"0" または空文字 = 正常` として扱う（公式 Python サンプルで観測される挙動。マニュアル `ComT7` の結果コード表は `"0"=正常` のみ規定し、空文字の取り扱いはサンプル準拠）
- `sResultCode` 一覧は `ComT7`（[`#sResultCode`](manual_files/mfds_json_api_ref_text.html#sResultCode)）参照。警告コード `sWarningCode` / `sWarningText` も同セクションに一覧あり
- `p_errno="2"` は**仮想 URL 無効**（セッション切れ or 営業時間外） → 再ログインが必要
- `p_errno="9"` は**システム・サービス停止中（利用時間外）**（v4r9〜）。デモ環境の利用時間はデモ案内ページ参照
- ログインで `p_errno=0 && sResultCode=0` でも **`sUrlRequest` が空文字**なら契約締結前書面 未読で仮想 URL が使えない → `UnreadNoticesError`（v4r9〜。旧 `sKinsyouhouMidokuFlg=="1"` 判定の置き換え）
- 共通判定は Python 側 `tachibana_auth.check_response(payload)` に集約し、例外型 `ApiError(code, message)` / `LoginError` / `UnreadNoticesError` / `SessionExpiredError` で原因切り分けする

### R7. レスポンスは Shift-JIS

- 日本語テキスト（銘柄名・エラーメッセージ）は Shift-JIS エンコード
- Python 側 `tachibana_codec.decode_response_body(bytes)` を経由。`bytes.decode("utf-8")` 直叩きは文字化けする
- **`errors="ignore"` を本番経路で使わない**。サイレントにバイトを落とすと銘柄名・エラーメッセージが部分破損したまま処理継続してしまう。既定は `errors="strict"`、失敗を許容したいログ出力経路だけ `errors="replace"`（`?` 化＋警告ログ）に切り替える。Python 公式サンプルが `errors="ignore"` を使っているのは教育用の簡略化であり、規範ではない

### R8. 空配列は `""` で返る

- 注文ゼロ件などの場合、本来配列のフィールドが空文字列 `""` で返る
- Python 側 `tachibana_codec.deserialize_tachibana_list(value)` で `""` → `[]` 正規化する
- 新しい List 応答型を追加する際は必ずこのヘルパー（または同等のバリデータ）を通す

### R9. URL エンコードの非標準文字

JSON 文字列を `?` 以降に貼り付けた後、含まれる記号文字をパーセントエンコードする。旧 Python サンプル（`_tel.py` 系）の `func_replace_urlecnode`（綴りママ）が置換対象 30 文字を列挙していた。v4r9 の新サンプル `e_api_login_pubkey.py` は綴りを `func_replace_urlencode` に修正し、実装も `urllib.parse.quote(s, safe='')` へ変更したが、**backcast 側 `tachibana_url.py` は従来どおり下表 30 文字の置換テーブル直書き方針を維持する**（関数名 `func_replace_urlecnode` も変えない）。代表的なもの:

```
' ' → '%20'    '!' → '%21'    '"' → '%22'    '#' → '%23'    '$' → '%24'
'%' → '%25'    '&' → '%26'    "'" → '%27'    '(' → '%28'    ')' → '%29'
'*' → '%2A'    '+' → '%2B'    ',' → '%2C'    '/' → '%2F'    ':' → '%3A'
';' → '%3B'    '<' → '%3C'    '=' → '%3D'    '>' → '%3E'    '?' → '%3F'
'@' → '%40'    '[' → '%5B'    ']' → '%5D'    '^' → '%5E'    '`' → '%60'
'{' → '%7B'    '|' → '%7C'    '}' → '%7D'    '~' → '%7E'
```

JSON 構造の `{` `}` `"` `:` `,` も**置換対象に含まれる**。つまり「生 JSON 文字列をそのまま全体エンコード」してから貼る運用ではなく、**key/value を個別にエンコードしつつ JSON 構造文字（`{` `}` `"` `:` `,`）はクエリにそのまま埋める**のが正しい:

```python
# 誤: httpx.get(url, params=payload)                       # 標準クエリエンコードされ立花仕様外
# 誤: url + "?" + urllib.parse.quote(json.dumps(payload))  # 構造文字までエンコードされ JSON にならない
# 正: url + "?" + tachibana_url.func_replace_urlecnode(json.dumps(payload))
#     # ↑ key/value 内の 30 文字を %xx 化、JSON 構造は保持
```

- パスワードに記号が含まれる場合は必ずエンコード。`func_replace_urlecnode` をそのまま `tachibana_url.py` に移植する（標準ライブラリの `urllib.parse.quote` の `safe` 引数チューニングでは立花仕様を再現しにくいので、置換テーブル直書きが安全）
- マルチバイト（日本語）は Shift-JIS エンコード後に `%xx` 化が公式流儀だが、現状マルチバイト送信は発生していないため未検証。拡張時は `api_web_access.xlsx` の事例に従う

### R10. シークレットは**絶対に**ハードコードしない

- `sUserId` / `sPassword` / `sSecondPassword` / 仮想 URL はすべて機密情報
- 運用時は Python の [`tachibana_file_store.py`](../../../python/engine/exchanges/tachibana_file_store.py)（ファイルキャッシュ）経由でのみ扱う。keyring は使用しない
- `DEV_TACHIBANA_USER_ID` / `DEV_TACHIBANA_PASSWORD` 環境変数による自動ログインは **Python 側 `tachibana_login_flow.py` の fast path** で扱い、release ビルドでは env を読まないようガードする
- 第二暗証番号は env / ファイルに保存しない。発注時に GUI（Bevy）の secret modal で取得しメモリのみ保持、idle forget タイマーで自動消去
- `.env` を使う場合は `.gitignore` に入れ、PR/コミットにも載せない
- ログ（`logger.info` / `log::info!`）に仮想 URL・パスワード・第二暗証番号を含めない（`debug` レベルですら生で流さず、`***` にマスク）。テストコード内でも同じ
- ⚠️ **HTTP クライアントライブラリの request ログに注意（#19 / findings 0009 で発見した実漏洩）**: 立花は R2 により `{virtual_url}?{JSON}` 形式で送るため、URL に session-secret な仮想 URL（`ND=` token）と発注時の `sSecondPassword` が乗る。`httpx` は **INFO で `HTTP Request: GET <full-url>`**、`httpcore` は DEBUG で低層 I/O を出すので、既定 INFO 運用だと R10 に反して平文資格情報がログ漏洩する。`mask_secrets` はこちらが組む payload にしか効かず**ライブラリ自身の request ログには届かない**。対策は [`engine/live/logging.py`](../../../python/engine/live/logging.py) の `suppress_third_party_http_logs()`（`httpx` / `httpcore` を WARNING へ）で、**venue adapter の `__init__` から呼んで login・発注などあらゆる secret-bearing request の前に効かせる**。新しい httpx 経路 / 新 venue adapter を足すときは必ずこの抑制が効いているか確認する

---

## Python 実装ヘルパー（`python/engine/exchanges/tachibana*.py`）

立花 venue の I/O は Python 側に集約する。新しい sCLMID を追加する際は下記ヘルパーを踏襲する（未実装のものはこの規約に沿って新設する）。Rust 側に同等ヘルパーを実装してはいけない。

- `tachibana_url.build_request_url(base, json_obj)` — REQUEST 用 `{base}?{JSON文字列}` を組み立て（R2）
- `tachibana_url.build_event_url(base, params: dict)` — EVENT 用 `{base}?key=value&...` を組み立て（R2 例外）
- `tachibana_url.func_replace_urlecnode(s)` — 30 文字置換（R9）
- `tachibana_codec.decode_response_body(bytes)` — Shift-JIS デコード（R7）
- `tachibana_codec.parse_event_frame(data: str) -> list[tuple[str, str]]` — `^A^B^C` 区切り分解
- `tachibana_codec.deserialize_tachibana_list(value)` — 空配列 `""` → `[]` 正規化（R8）
- ⚠️ **FD 板/歩み値の段は codec で「有限数のみ採用」に絞る（#27 で発見・実装）**: `_extract_depth` の段フィルタは `if bp and bv`（空欄 skip）だけでは不十分。`"—"` 等の **truthy だが非数値な特別気配マーカや `nan`/`inf`** を通すと、adapter `_cb`（`tachibana.py`）の `float(lv["price"])` が `ValueError` を投げる。**`tachibana_ws.py` の recv loop は `await callback(...)` を try で包まない**ため、この例外は recv_task を落とし再 raise → **接続断・再接続churn**（kabu は `kabusapi_ws.py` の `on_message` を `try/except BaseException: continue` で包むので落ちない＝venue 間の resilience 非対称）。対策は `_extract_depth` で `_is_finite_quote`（後段 `_cb` と同じ `float(v)` でパースし `math.isfinite` で NaN/Inf を弾く＝『guard True ⟹ float() は raise しない』を保証する。`Decimal(v).is_finite()` だと `"1_"` 等 Decimal が受理し float が `ValueError` にする underscore を取りこぼす）により非数値・非有限段を段ごと skip すること（`price<=0` の最終弾きは `DepthCache` の `gt=0` が担う二段防御）。**同 `_cb` の trade 側 `float(trade["price"])` も同じ無防備クラスを共有する**が、EC（注文約定）フレームが同じ callback dispatch を流れるため blanket な try/except 化は約定取りこぼしリスクがあり、WS callback 全体の resilience は trades/orders slice（#23）で EC 失敗ポリシーと併せて設計する（findings 0014 §2.2）。
- `tachibana_auth.next_p_no()` — `asyncio` 単一スレッド前提の単調増加カウンタ（R4、自前採番禁止）
- `tachibana_auth.current_p_sd_date()` — JST 固定の送信日時（R4）
- `tachibana_auth.check_response(payload)` — `p_errno` → `sResultCode` の二段判定（R6、`p_errno` 空文字 = 正常）
- 例外階層: `LoginError` / `UnreadNoticesError` / `SessionExpiredError` / `ApiError` を `tachibana_auth.py` で定義
- テストは [`pytest-httpx`](https://pypi.org/project/pytest-httpx/) の `HTTPXMock` フィクスチャでモック（既存 [`python/tests/test_binance_rest.py`](../../../python/tests/test_binance_rest.py) パターン踏襲）。本番 URL を絶対に踏まない（R1）。ログイン応答のモックは公開鍵認証サンプル [`e_api_login_pubkey.py`](samples/e_api_login_pubkey.py/e_api_login_pubkey.py)（実行時に `.auth/file_login_response.txt` を生成）の応答フィールド構成を参照する

## Rust 側に置くもの／置かないもの

**置くもの**（IPC ライフサイクル＋ enum 定義のみ）:

- `engine-client/src/dto.rs` — IPC コマンド `RequestVenueLogin` / venue ライフサイクルイベント `VenueReady` / `VenueError` / `VenueLoginStarted` / `VenueLoginCancelled`
- `engine-client/src/capabilities.rs` — `Ready.capabilities.venue_capabilities[<venue>][<key>]` の型付き抽出
- `exchange/src/adapter.rs` — `Venue::Tachibana` / `MarketKind::Stock` / `Exchange::TachibanaStock` 列挙子

**置かないもの**（Python に集約）:

- `exchange/src/adapter/tachibana.rs` — venue adapter は新設しない
- `data/src/config/tachibana.rs` — Python autonomous 方針によりセッション永続化は Python `tachibana_file_store.py` 側
- `src/connector/auth.rs` / `src/replay_api.rs` の立花拡張 — 不要
- `src/screen/login.rs` の立花用ログイン UI — Python tkinter ヘルパー subprocess で開く（フィールド名・ラベル・順序を Rust 側に書かない）
- `#[cfg(debug_assertions)]` の env 取込みコード — env は Python `tachibana_login_flow.py` のみが読む
- 立花 WebSocket クライアント — Python `tachibana_ws.py` に集約

---

詳細は [docs/✅tachibana/](../../../docs/✅tachibana/) を参照。
