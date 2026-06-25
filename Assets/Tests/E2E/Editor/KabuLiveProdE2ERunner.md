# KabuLiveProdE2ERunner — 台本（kabu 本番 prod 実ログイン / owner HITL）

`KabuLiveProdE2ERunner.cs` が駆動する **kabu（kabuステーション）本番（prod 18080）の実ログイン leg**。
方針: [ADR-0027](../../../../docs/adr/0027-abolish-prod-allow-env-gate.md)、下位事実:
[findings 0106](../../../../docs/findings/0106-kabu-prod-login-auth-rejected-message-and-test.md)。
共通規約は [E2E-CONVENTIONS.md](./E2E-CONVENTIONS.md) / [ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)。

> **位置づけ**: owner 報告（2026-06-25)「Connect kabuStation (Prod) のログインがエラー」を**本番経路で**回帰ゲートする
> runner。`KabuLiveE2ERunner`（verify 固定）の prod 版だが、**本番口座で発注/購読を走らせない**ため検証は
> login→CONNECTED の確立までに限定する（板/depth は verify leg が担う）。

## ⚠️ 前提（owner HITL・Windows 限定・本番接触）

- **kabuステーション本体を本番モードで起動・ログイン済み**、設定→API→「APIを利用する」有効、API パスワード設定済み。
- **本番ポート localhost:18080** で listen していること。
- `PROD_KABU_API_PASSWORD` を process env / `.env` で供給（**verify の `DEV_KABU_API_PASSWORD` とは別パスワード**・findings 0106 / D2）。
- macOS / Linux / CI では走らない。前提未充足（env 未設定 / 本体未起動）は **SKIP** として exit 0（rollup 中立）。

## ADR-0027 との整合

ADR-0027 D4 は「自動 runner は verify/demo 固定で本番非接触」とする。本 runner は **意図的に prod を叩く唯一の例外**で、
owner HITL 専用・`PROD_KABU_API_PASSWORD` 未設定や本体未起動では走らない二重ガードで守る。新たな端末フラグは足さない
（ADR-0027 の中核＝prod-allow env ゲート廃止に矛盾しない）。詳細は findings 0106 を参照（ADR-0027 には書き戻さない）。

## 操作一覧表

| Action ID | 行動 | 入口 | 観測点 | 自動判定 | カバー状態 |
|---|---|---|---|---|---|
| KABU-LIVE-PROD-01 | ログイン（env / prod・`PROD_KABU_API_PASSWORD`） | `WorkspaceEngineHost.VenueLogin("KABU","env","prod")` → `KabuStationAdapter.login(env=prod)` → `fetch_token` | `venue_state=CONNECTED` | poll 収束を assert | HITL専用（本体・本番モード） |

> 同じ Action-ID `KABU-LIVE-PROD-01` を **pytest** [`test_kabu_prod_login_live.py`](../../../../python/tests/test_kabu_prod_login_live.py)
> も持つ（本体不要時は skip）。rollup は FAIL-wins dedup で両者を畳む。

## 実行

```
<Unity> -batchmode -nographics -quit -projectPath . \
        -executeMethod KabuLiveProdE2ERunner.Run -logFile <abs log>
# expect: [E2E KABU-LIVE-PROD-01 PASS] / exit=0  （確認は Bash `grep -a "E2E KABU-LIVE-PROD"`）
# または: pwsh scripts/run-live-e2e.ps1 -Method KabuLiveProdE2ERunner.Run
```
