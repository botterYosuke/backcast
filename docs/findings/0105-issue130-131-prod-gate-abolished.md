# findings 0105 — prod-allow env ゲート廃止 + DEV_* prefill 廃止（#130 / #131）

方針: **ADR-0027**（prod-allow env ゲートの廃止）。本 findings は撤去した各 call site・反転した
probe assert・新設した回帰ゲートの RED→GREEN・再走手順を固定する。ADR には書き戻さない。

関連: ADR-0027（正本）／ADR-0021（venue 再バインド・本 ADR が D3 の prod-gate 部分のみ上書き）／
findings 0017 §6（multi-layer prod ガードの元設計・本スライスが supersede し stale-marker を付与）。

## 何を直したか（不具合の機序）

「Connect kabuStation (Prod) を押しても何も起きない」: prod 接続ボタンは `*_ALLOW_PROD == "1"` が
**プロセス環境変数**に無いと `CanConnectEnv` が false → `interactable=false`（grey-out）になり、disabled な
uGUI ボタンは onClick を発火しない。`.env` に書いても効かない（`DefaultProdAllowed` は
`Environment.GetEnvironmentVariable` 直読みで `EnvConfig.Get` の `.env` フォールバックを使わない非対称）。

ADR-0027 D1/D2 に従い、env フラグによる prod 解禁ゲートを **全層から撤去**。本番接続の可否は「ユーザーが
ダイアログで prod を選ぶ＋本物の prod 資格情報＋prod 本体の起動」の 3 点だけで決まる（マシン単位フラグは持たない）。
D3 で DEV_* prefill も撤去（手動起動では常にユーザーが入力）。

## 撤去した call site（#130）

| 層 | 場所 | before | after |
|---|---|---|---|
| Python guard module | `engine/exchanges/_env_guard.py` | `require_prod_env(var)` | **ファイルごと削除**（参照 0 件） |
| kabu URL builder | `kabusapi_url.base_url(env="prod")` | `require_prod_env("KABU_ALLOW_PROD")` で raise | フラグ無しで prod URL を返す |
| orchestrator front-stop | `live_orchestrator._handle_prompt_login` | prod かつフラグ無→`PROD_NOT_ALLOWED` | prod もダイアログ経路へ進む（INVALID_ENV は残す） |
| kabu form_state | `kabusapi_login_form_state.build_form_init` | `allow_prod`・`is_debug_build`・`dev_api_password` フィールド／env 連動ポート | `FormInit(env_hint, station_port)` のみ。ポートは env_hint だけで決定（prod=18080 / verify=18081） |
| tachibana form_state | `tachibana_login_form_state.build_form_init` | `allow_prod`・`is_debug_build`・`dev_*` フィールド／env 連動モード | `FormInit(env_hint, initial_mode)` のみ。モードは env_hint だけ（prod / それ以外 demo） |
| kabu dialog | `kabusapi_login_flow.run_dialog` | front-stop・Prod ラジオ disabled・`KABU_ALLOW_PROD=1 が必要`・dev prefill | 撤去。Prod ラジオ常時 enable・パスワード欄空欄 |
| tachibana dialog | `tachibana_login_flow.run_dialog` | front-stop・Prod ラジオ disabled・`TACHIBANA_ALLOW_PROD=1 が必要`・dev prefill・debug focus | 撤去。Prod ラジオ常時 enable・認証ID/秘密鍵欄空欄・focus は認証ID欄固定 |
| tachibana adapter env-path | `tachibana.set_execution_hooks` | `require_prod_env("TACHIBANA_ALLOW_PROD")` | 撤去（自動 E2E は environment_hint を demo/verify 固定で本番非接触） |
| C# VM | `VenueMenuViewModel` | `prodAllowed` 注入・`DefaultProdAllowed`・`CanConnectEnv` の prod 分岐 | `CanConnectEnv` は `CanConnect` に収斂（prod 特別扱い消滅） |
| E2E トリップワイヤ | `KabuLiveE2ERunner` / `TachibanaLiveE2ERunner` | `*_ALLOW_PROD==1 なら refuse` | 単純削除（死にコード）。本番非接触は environment_hint 固定が保証 |

## 反転した probe / 台本（#130）

- `MenuBarVerify.cs`: prod grey-out（deny/allow 2VM）→ **prod 常時 enable**（切断中は prod も connectable・接続中は
  全 disable＝CanConnect 追従の delete-litmus 付き）。Action-ID `PRODGATE-07` を成功マイルストンで吐く。
- `MenuBarHitlHarness.cs`: A1/A3 を prod-enable へ反転（`_menu` を直接駆動）。
- `SettingsDialogE2ERunner.cs`: S08 を「非 prod のみ enable（≥1）」→「**全 connect（prod 含む）enable**＝
  connectEnabled==connectCount」へ強化（prod を再グレーアウトすると RED）。
- 台本 `.md`: MenuBarE2ERunner（MENU-12）／SettingsDialogE2ERunner（SETTINGS-08）／Kabu・Tachibana LiveE2ERunner の
  prod 記述を ADR-0027 へ反転。findings 0017 §6 に SUPERSEDED stale-marker。

## 新設/反転した pytest ゲート（#130 / #131）と Action-ID

| Action-ID | 不変条件 | 場所 |
|---|---|---|
| PRODGATE-01 | prod env_hint はフラグ無しでもダイアログ経路へ進む（`PROD_NOT_ALLOWED` を返さない） | `test_inproc_prompt_login.py::test_dispatcher_prod_reaches_dialog_without_allow_flag`（旧 `test_dispatcher_prod_not_allowed` を反転） |
| PRODGATE-02 | `kabusapi_url.base_url("prod")` はフラグ無しで prod URL を返す・未知 env は ValueError | `test_prod_gate_abolished.py` |
| PRODGATE-03 | kabu `build_form_init("prod")` はフラグ無しで port 18080・FormInit に allow_prod/dev_/is_debug_build 無し | 〃 |
| PRODGATE-04 | tachibana `build_form_init("prod")` はフラグ無しで mode "prod"・FormInit に allow_prod/dev_/is_debug_build 無し | 〃 |
| PRODGATE-05 | kabu form_state は DEV_KABU_API_PASSWORD を env に置いても surface しない（空欄で開く / #131） | 〃 |
| PRODGATE-06 | tachibana form_state は DEV_TACHIBANA_* を env に置いても surface しない（空欄で開く / #131） | 〃 |
| PRODGATE-07 | C# `CanConnectEnv(*,"prod")` がフラグ無しで true・接続中は false（CanConnect 収斂） | `MenuBarVerify.cs`（Unity rollup） |

タグは conftest が `@pytest.mark.scenario` を実 outcome から `[E2E <id> PASS/FAIL/SKIP]` に翻訳（PRODGATE-01..06）／
`MenuBarVerify` が成功点で `[E2E PRODGATE-07 PASS]`（rollup-visible 単一トークン）。

## RED→GREEN（delete-the-production-logic litmus）

各ゲートは「撤去したガードを再導入すると RED」になるよう設計:
- PRODGATE-01: front-stop を戻すと `(False, "PROD_NOT_ALLOWED", None)` を返し assert（success==True）が RED。
- PRODGATE-02: `require_prod_env` を戻すと `base_url("prod")` が RuntimeError を上げ RED。
- PRODGATE-03/04: env 連動ポート/モードを戻すとフラグ無しで 18081 / "demo" になり assert が RED。allow_prod/dev_ フィールドを
  戻すと「フィールド非存在」assert が RED。
- PRODGATE-05/06: prefill を戻すと sentinel が `repr(init)` に現れ assert が RED。
- PRODGATE-07: prod を再グレーアウト（`CanConnectEnv` に prod 分岐を戻す）と「prod connectable when disconnected」が RED。
- SETTINGS-08（強化）: prod を再グレーアウトすると connectEnabled<connectCount で RED。

## 検証結果（2026-06-25）

- `cd python && uv run pytest`: **559 passed**（PRODGATE-01..06 を含む。`resolve_credentials` の `is_debug_build` は
  `credentials_source="env"` 自動 E2E 用の別関数で D3 保持対象＝回帰なし）。
- compile gate（`pwsh scripts/run-live-e2e.ps1 -CompileOnly`）: `[COMPILE PASS]` error CS 0 / exit 0。
- `MenuBarVerify.Run`: 17 pass / 0 fail・`[E2E PRODGATE-07 PASS]`・rollup `[PASS] PRODGATE-07`。
- `SettingsDialogE2ERunner.Run`: SETTINGS-01..08 PASS（強化 S08 含む）・8 PASS / 0 FAIL。
- merged rollup（`run-all-tests.ps1`）: PRODGATE-01..06 が `[PASS]`・`Summary 6 PASS / 0 FAIL`。

## 再走手順

```
cd python && ./.venv/Scripts/python.exe -m pytest tests/test_prod_gate_abolished.py tests/test_inproc_prompt_login.py tests/test_tachibana_secret.py -v
pwsh scripts/run-live-e2e.ps1 -CompileOnly
pwsh scripts/run-live-e2e.ps1 -Method MenuBarVerify.Run
pwsh scripts/run-live-e2e.ps1 -Method SettingsDialogE2ERunner.Run
pwsh scripts/run-all-tests.ps1 -PytestArgs 'tests/test_prod_gate_abolished.py'
```

## レビュー pass（orchestrated multi-agent review・2026-06-25）

dead-code / simplify / regression / behavior-to-e2e の 4 専門 agent で精査し、Medium 指摘を 0 に落とした。

- **dead-code**: load-bearing は CLEAN（`_env_guard.py` の importer 0・unused param/import/field 無し・`.env` に
  ALLOW_PROD キー無し）。`CanConnectEnv(venue,env)` は param を使わないが **意図的 retained**（per-(venue,env)
  enablement の唯一の seam＝PRODGATE-07 が pin する場所）。残りは stale doc コメントのみ。
- **simplify（Med・修正済）**: `SettingsVenueSectionView.Build` の `if (venueId=="MOCK") … else …` は
  `CanConnectEnv("MOCK","")⇒CanConnect` と恒等で **dead branch**。全 variant を `CanConnectEnv(venueId,envId)`
  一本に収斂（seam は温存）。`CanConnectEnv` 自体の撤去は seam/litmus を弱めるため不採用（ADR-0027 が
  signature 据え置きを許容・dead-code agent も retained-OK 判定）。
- **regression**: NO-REGRESSION（560 passed）。dialog-prefill 撤去（#131）と `credentials_source="env"` 自動 E2E の
  DEV_* 解決（`tachibana_credentials.resolve_credentials` の `is_debug_build`・ADR-0027 D3 保持）の境界が無傷で
  あることを確認。
- **behavior-to-e2e（Med・修正済）**: PRODGATE-05/06 は presenter（`build_form_init` の repr）のみを見るため、
  歴史的に prefill が実在した **`run_dialog` の widget**（`pw_var=tk.StringVar(value=...)`）に env 由来 prefill を
  戻しても GREEN を素通りする穴があった。**PRODGATE-08**（login flow 2 モジュールが `environ`/`getenv` を
  一切持たない source-scan）を新設して塞いだ。また「enabled な prod ボタンが onClick→`onConnect(venue,"prod")`
  を実発火する」（不具合の直接の面）を **SETTINGS-08** に dispatch litmus として追加（従来は no-op onConnect で
  interactable しか見ていなかった）。

| Action-ID | 不変条件 | 場所 | delete-litmus |
|---|---|---|---|
| PRODGATE-08 | login ダイアログ flow が process env を読まない（widget-prefill 回帰 RED） | `test_prod_gate_abolished.py::test_login_dialog_modules_read_no_process_env` | `value=os.environ.get("DEV_*")` を戻すと `environ` 検出で RED（RED→GREEN 実機確認済） |
| SETTINGS-08（追加面） | enabled prod ボタンが `onConnect(venue,"prod")` を発火 | `SettingsDialogE2ERunner.cs` S08 | prod を再グレーアウト→`!interactable`／dispatch venue/env 不一致で RED |

stale doc コメント修正: `tachibana_url.py` 冒頭 docstring と `tachibana_credentials.py` `_DEMO_KEYS` 直前コメントの
「TACHIBANA_ALLOW_PROD ゲートが後で landする / R1 配下」記述を ADR-0027 反映（廃止済・DEV_* は env-E2E 専用）へ更新。
未修正の follow-up（非ブロッキング）: `.claude/skills/{tachibana,kabusapi}/SKILL.md` と `docs/findings/0053`・
`docs/adr/0023` に残る ALLOW_PROD 記述（knowledge/history 面・別途整理）。

検証（2026-06-25・全 GREEN）: `uv run pytest` **560 passed**（PRODGATE-08 含む・litmus RED→GREEN 確認）／
compile gate `[COMPILE PASS]` exit 0／`SettingsDialogE2ERunner.Run` **8 PASS / 0 FAIL**（強化 S08）／
`MenuBarVerify.Run` **17 pass**・`[E2E PRODGATE-07 PASS]`。

## HITL（owner 専用・headless 不可）

- 実ダイアログの目視: kabu/tachibana ログインダイアログが **資格情報欄が空欄**で開き、**Prod ラジオが常時選択可能**で
  あること（tkinter 表示が要るため headless では走らない・findings 0093 §HITL と同区分）。
- 「Connect kabuStation (Prod)」を押下 → ダイアログが prod 選択可能な状態で開くこと（不具合の直接再現確認）。
- prod 本接続（実 kabu 18080 / 実 tachibana 本番）は本スライスの対象外（実弾リスクゲートは別 issue）。
