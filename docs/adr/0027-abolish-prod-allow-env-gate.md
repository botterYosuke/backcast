---
status: accepted
---

# prod-allow env ゲート（KABU_ALLOW_PROD / TACHIBANA_ALLOW_PROD）の廃止

owner 依頼（2026-06-25）「手動でアプリを使おうとして『Connect kabuStation (Prod)』を押しても何も起きない。venue/prod の選択はユーザーがアプリ内で行うので、env フラグによる prod 解禁ゲートは『おせっかいな親切機能』。廃止する」を受けた決定。`grill-with-docs` HITL（2026-06-25）で 4 点を確定した。

報告された不具合の機序: prod 接続ボタンは `*_ALLOW_PROD == "1"` が **プロセス環境変数**に無いと `CanConnectEnv` が false を返し `interactable=false`（grey-out）になる。disabled な uGUI ボタンは onClick を発火しないため「押しても何も起きない」。`.env` に書いても効かない（`VenueMenuViewModel.DefaultProdAllowed` は `Environment.GetEnvironmentVariable` 直読みで `EnvConfig.Get` の `.env` フォールバックを使わない非対称）。

## なぜ新規 ADR か

prod-allow ゲートは **過去に意図的に固定された設計**である:
- **ADR-0021 D3**「prod は従来どおり `*_ALLOW_PROD` gate」（runtime-rebindable single live venue の保存条件として明記）
- **findings 0017 §6**（menu bar スライスの多層ガード設計）
- **`_env_guard` docstring**「Phase 8 §3.2 二重ガード」

本決定はこのゲートを **正面から廃止**する（hard-to-reverse=安全姿勢の反転・surprising=「取引アプリに本番ガードが無いのはなぜか」を将来の読者が必ず問う・real trade-off=マシン単位の安全フラグ喪失と引き換えに摩擦をなくす）。ADR-0021 は自己保護条項（「本 ADR の decision は固定。覆す場合はこのファイルを編集せず、本 ADR を supersede する新規 ADR を起こす」）を持つため **ADR-0021 は編集しない**。本 ADR が ADR-0021 D3 の prod-gate 部分を上書きする——ただし ADR-0021 の中核（実行時 venue 再バインド）は **不変のまま**。findings 0017 §6 と `_env_guard` の prod-gate 面も本 ADR が supersede する。

関連: ADR-0021（venue 再バインド・本 ADR が D3 の prod-gate 部分のみ上書き）／ADR-0026（venue 接続が Settings へ移設）／findings 0017 §6（multi-layer prod ガードの元設計）。

## Context

prod 解禁は env フラグ `KABU_ALLOW_PROD` / `TACHIBANA_ALLOW_PROD == "1"` を唯一の信号に、以下の多層で実装されていた:

| 層 | 場所 | 挙動 |
|---|---|---|
| C# grey-out | `VenueMenuViewModel.DefaultProdAllowed` / `CanConnectEnv` | フラグ無→Prod variant disabled |
| orchestrator front-stop | `live_orchestrator._handle_prompt_login` | prod かつフラグ無→`PROD_NOT_ALLOWED` |
| dialog | `kabusapi/tachibana_login_flow.run_dialog` | Prod ラジオ disabled＋`PROD_NOT_ALLOWED` |
| form_state | `kabusapi/tachibana_login_form_state.build_form_init` | `allow_prod`→ポート選択(kabu 18080/18081)・初期モード・credential suffix |
| URL builder（kabu のみ） | `kabusapi_url.base_url(env="prod")`→`require_prod_env` | トークンがあっても prod URL 生成時に raise |

同じフラグが **逆極性**で自動 E2E live runner（`KabuLiveE2ERunner` / `TachibanaLiveE2ERunner`）にも使われ、「フラグ==1 なら実行拒否」していた（verify/demo 専用テストが本番に触れない belt-and-suspenders）。ただし両 runner は `environment_hint` を `"verify"/"demo"` にハードコード固定しており、本番非接触はその固定が保証する。

このアプリは手動起動の取引アプリで、ユーザー（＝トレーダー本人）が「kabu / tachibana のどちらに、verify/demo/prod のどれで接続するか」を **アプリ内のメニューとログインダイアログで明示選択**する。env フラグはその選択の上に重なる冗長な過保護であり、しかも `.env` 経由で解禁できない非対称が「押しても無反応」の不具合を生んでいた。

## Decision

- **D1 (ゲート全層を削除)**: `KABU_ALLOW_PROD` / `TACHIBANA_ALLOW_PROD` を参照する全コードを削除する——C# `DefaultProdAllowed`／`CanConnectEnv` の prod 分岐、Python `_handle_prompt_login` の `PROD_NOT_ALLOWED` front-stop、両 `run_dialog` の Prod ラジオ disabled・`PROD_NOT_ALLOWED`・"…ALLOW_PROD=1 が必要" メッセージ、`build_form_init` の `allow_prod` フィールドと派生（ポート/初期モードは `env_hint` だけで決める）、`kabusapi_url` の `require_prod_env` 呼び出し。`_env_guard.require_prod_env` が他に参照されなくなれば関数ごと撤去。
- **D2 (人間の選択が唯一の権威)**: 本番接続の可否を決めるのは「ユーザーがダイアログで Prod を選ぶ＋本物の prod 資格情報を入れる＋prod 本体（kabu 18080 等）が起動している」の 3 点だけ。マシン単位の「この端末は prod 可」フラグは持たない。Prod ラジオは常時選択可能。
- **D3 (DEV_* prefill も廃止)**: ログインダイアログは `DEV_TACHIBANA*` / `DEV_KABU*` を **prefill しない**（debug ビルドでも空欄）。手動起動では資格情報を常にユーザーが入力する。`DEV_*` env は `credentials_source="env"` の自動 E2E（demo/verify ログイン）専用に限定する。`build_form_init` の `is_debug_build` 連動 prefill ロジックは撤去。
- **D4 (E2E トリップワイヤは単純削除)**: `KabuLiveE2ERunner` / `TachibanaLiveE2ERunner` の「`*_ALLOW_PROD==1` なら refuse」行は、見るべきフラグが消えるため死にコードになる。単純削除する。本番非接触は `environment_hint` の verify/demo ハードコード固定が引き続き保証する（assert への置換はしない＝grill で owner が選択）。

## 不採用

- **不採用：env フラグは残し既定で prod 許可**。「過保護を消す」意図に対し、無効化された機構を温存するのは死にコード。フラグ自体を撤去する（D1）。
- **不採用：別形式の最終確認ゲート（prod 選択時の確認ダイアログ等）を新設**。owner が「人間の選択を唯一の権威に」を選択（grill Q1）。新しい安全装置は作らない。
- **不採用：debug ビルドの DEV_* prefill を温存**。owner が「prefill も廃止・常にユーザー入力」を選択（grill Q2）。
- **不採用：E2E トリップワイヤを `environment_hint` の assert に置換**。runner は接続先を verify/demo にハードコードしており、本番化するにはソース改変が要る（その際 assert も改変され得る）ため実効性が薄い。単純削除（grill Q3＝A）。

## Consequences

- **不具合解消**: 「Connect kabuStation (Prod) を押しても何も起きない」は解消。prod variant は切断中かつサーバ ready なら常時クリック可能になり、ダイアログが開く。
- **C#**: `VenueMenuViewModel` の `prodAllowed` 注入と `DefaultProdAllowed` を撤去。`CanConnectEnv(venue, env)` は prod 特別扱いを失い実質 `CanConnect` に収斂（呼び出し側の整理は /simplify altitude）。grey-out を assert していた probe（`MenuBarHitlHarness` A3「prod enabled when KABU_ALLOW_PROD set」・`VenueMenuM3Probe`・`SettingsDialogE2ERunner`・`MenuBarVerify`）は「prod は常時 enable」へ反転して更新する。
- **Python**: `_env_guard.py`／`require_prod_env`／`base_url` の prod 分岐／両 login flow の prod ガード／`build_form_init` の `allow_prod`・prefill を撤去。`environment_hint` の `_ENV_PER_VENUE` 検証（未知 env を `INVALID_ENV`）は残す——prod は valid env として通る。
- **back-compat（安全）**: 本番接続の実行経路は「人間が prod を選んだとき」だけ到達する。verify/demo の既存経路・自動 E2E（verify/demo 固定）は不変。
- **.env**: `KABU_ALLOW_PROD` / `TACHIBANA_ALLOW_PROD` は不要キーになる（`.env` には元々未記載）。`.env.example` 等にあれば削除。
- **下位事実は findings に固定**: 撤去した各 call site・反転した probe の assert・RED→GREEN・AFK 再走手順は本スライスの `docs/findings/NNNN-*.md` に記録し、本 ADR を「方針: ADR-0027」として参照する（本 ADR には書き戻さない）。

## 自己保護

本 ADR の decision は固定。覆す（prod ガードを再導入する）場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。下位事実（撤去した個々の call site・probe assert の反転・ダイアログのポート/モード決定の細部）は本 ADR に書き戻さず slice の findings に記録し本 ADR を参照する。ADR-0021 は自己保護条項により編集しない——本 ADR が D3 の prod-gate 部分を上書きする旨は本 ADR 側にのみ記す。
