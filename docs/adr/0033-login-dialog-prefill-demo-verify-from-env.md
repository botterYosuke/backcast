---
status: accepted
---

# ログインダイアログの資格情報プレフィル復活（demo/verify のみ・debug ビルド限定）

owner 依頼（2026-06-25）「立花も kabu も `.env` に値があるときは、ログインダイアログに初期値として
入力した状態で出してほしい」を受けた決定。`grill-with-docs` HITL（2026-06-25）で prod の扱い・適用
ビルド・実装層を確定した。

きっかけ: owner が /tachibana にログインできなかった。実機調査で原因は **資格情報の取り違え**だった
——v4r9 公開鍵認証ダイアログ（認証ID＋秘密鍵ファイル）に、廃止済みのパスワード時代の値
（`DEV_TACHIBANA_USER_ID`/`PASSWORD`）を入れて `AUTH_FAILED`。正しい demo 資格情報
（`DEV_TACHIBANA_AUTH_ID_DEMO` + `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO`）では実 demo サーバ
（`demo-kabuka.e-shiten.jp`）へのログインが成功することを確認済み。48 文字の認証IDや PEM パスを
毎回手で貼るのは現実的でなく、取り違えの温床になる。`.env` にある値を初期表示すればこの摩擦と
取り違えを同時に解消できる。

## なぜ新規 ADR か

ダイアログの prefill は **ADR-0027 D3 が意図的に廃止**した（「手動起動では資格情報を常にユーザーが
入力する。`DEV_*` env は `credentials_source="env"` の自動 E2E 専用に限定する」）。本決定はその D3 を
**正面から覆す**（hard-to-reverse=資格情報の扱い方針の反転・surprising=「なぜ手動ダイアログが env を
読むのか」を将来の読者が問う・real trade-off=利便性と「env を自動ログイン専用に閉じる」設計の喪失）。
ADR-0027 は自己保護条項（「覆す場合はこのファイルを編集せず、本 ADR を supersede する新規 ADR を
起こす」）を持つため **ADR-0027 は編集しない**。本 ADR が **ADR-0027 D3 のみ** を supersede する。
ADR-0027 D1/D2/D4（prod-allow ゲートの全層廃止・人間の選択が唯一の権威・E2E トリップワイヤ削除）は
**不変のまま**で、本 ADR はそれと整合する（下記 D2）。

関連: ADR-0027（prod-allow ゲート廃止・本 ADR が D3 のみ上書き）／ADR-0023（v4r9 公開鍵認証）／
`tachibana_credentials.resolve_credentials`（`credentials_source="env"` 自動 E2E 経路・本 ADR で不変）。

## Context

現状（ADR-0027 D3 後）、両ログインダイアログは資格情報欄が空欄で開く:

| venue | ダイアログ欄 | demo/verify の正しい env キー | prod の env キー |
|---|---|---|---|
| 立花 | 認証ID / 秘密鍵ファイル | `DEV_TACHIBANA_AUTH_ID_DEMO` / `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` | `DEV_TACHIBANA_AUTH_ID` / `DEV_TACHIBANA_PRIVATE_KEY_PATH` |
| kabu | API パスワード | `DEV_KABU_API_PASSWORD`（verify 18081） | `PROD_KABU_API_PASSWORD`（prod 18080・実弾） |

`.env` は `engine/paths.py:_load_dotenv_once()`（import 時に自動実行・`setdefault` なので実 process env が
優先）が `os.environ` へ流し込むので、埋め込み Python のダイアログは C# 配線なしで `DEV_*`/`PROD_*` を
読める。資格情報はモード固有（demo と prod で別キー）なので、Demo/Prod（kabu は Verify/Prod）ラジオと
prefill は不可分に連動する。

## Decision

- **D1（demo/verify を prefill）**: `IS_DEBUG_BUILD` かつ該当 `DEV_*` キーが `os.environ` にあるとき、
  ダイアログは **demo/verify モードの**資格情報欄を prefill して開く。
  - 立花 demo: 認証ID ← `DEV_TACHIBANA_AUTH_ID_DEMO`、秘密鍵ファイル ← `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO`
  - kabu verify: API パスワード ← `DEV_KABU_API_PASSWORD`
- **D2（prod は prefill しない＝手入力）**: prod 資格情報（kabu `PROD_KABU_API_PASSWORD`・立花
  `DEV_TACHIBANA_AUTH_ID`/`PRIVATE_KEY_PATH`）は**決して prefill しない**。実弾/本番ログインは常にユーザー
  手入力を要求する。ラジオを **Prod に切替えると資格情報欄をクリア**し、demo/verify へ戻すと prefill を
  再導出する。これは ADR-0027 D2（本番接続の権威は「人間が prod を選ぶ＋本物の prod 資格情報＋prod 本体
  起動」の 3 点）と整合する——prefill は demo/verify の摩擦だけを下げ、real-money の安全姿勢は変えない。
- **D3（release は prefill しない）**: `IS_DEBUG_BUILD == False`（release ビルド）では一切 prefill せず空欄で
  開く。`resolve_credentials` が `DEV_*` を debug ビルドでのみ読む既存規律（R10 / S1）と一致させる。
- **D4（env 読みは pure presenter に閉じる）**: env を読むのは pure presenter（`*_login_form_state.py` の
  `build_form_init`）だけにし、`run_dialog`（tkinter widget 側）は **presenter が返す prefill 文字列**を
  StringVar 初期値に使う。`run_dialog` は `os.environ`/`getenv` を直接読まない。これにより
  ADR-0027 で導入した **PRODGATE-08 不変条件（login_flow widget は process env を読まない）を維持**でき、
  prefill ロジックは tkinter 無しで単体テスト可能なまま（PRODGATE-05/06 が presenter を直接 assert）。

## 不採用

- **不採用：prod も含め全モード prefill**。owner が「demo/verify のみ prefill・prod は手入力」を選択
  （grill 2026-06-25）。real-money の prod を OK 一押しで送れる状態は避ける。
- **不採用：prefill を `run_dialog` の `os.environ.get(...)` で直接行う**。PRODGATE-08 不変条件を壊し、
  tkinter 無しで単体テストできなくなる。env 読みは presenter に閉じる（D4）。
- **不採用：release ビルドでも prefill**。`DEV_*` を debug 限定で読む既存規律に反する（D3）。
- **不採用：手編集の保持（モード切替で欄を温存）**。モード切替は当該モードの正準値へ欄をリセットする
  単純モデルにする（demo/verify=env 値、prod=空）。切替は稀で、予測可能性を優先。

## Consequences

- **回帰ゲートの反転**: `test_prod_gate_abolished.py` の PRODGATE-05/06（「prefill しない」を assert）は
  新契約へ反転——`build_form_init("demo"/"verify")` は debug ビルドで `DEV_*` 値を載せ、
  `build_form_init("prod")` は載せず、`IS_DEBUG_BUILD==False` では載せない、を assert する。
- **PRODGATE-08 は維持**: `*_login_flow.py` が `environ`/`getenv` を読まない source-scan は GREEN のまま
  （prefill は presenter 経由・D4）。
- **新規挙動ゲート（behavior-to-e2e）**: demo/verify prefill 有・prod 空・Prod 切替でクリア・debug 限定を
  pytest で固定。Action-ID は本スライスの findings に採番し rollup へ載せる。
- **`os.environ` 依存**: prefill は `engine.paths` の dotenv 自動ロードに依存する。presenter は env 読みの
  前に必要なら明示ロードを保証する（実装側 finding に記録）。
- **下位事実は findings に固定**: 反転した assert・追加した presenter prefill・run_dialog の toggle 再導出・
  RED→GREEN・再走手順は本スライスの `docs/findings/NNNN-*.md` に記録し、本 ADR を「方針: ADR-0033」と
  して参照する（本 ADR には書き戻さない）。

## 自己保護

本 ADR の decision は固定。覆す（prefill を再び廃止する・prod も prefill する等）場合はこのファイルを
編集せず、**本 ADR を supersede する新規 ADR** を起こす。下位事実（個々の env キー対応・toggle の挙動細部・
反転した probe assert）は本 ADR に書き戻さず slice の findings に記録し本 ADR を参照する。ADR-0027 は
自己保護条項により編集しない——本 ADR が D3 を上書きする旨は本 ADR 側にのみ記す。
