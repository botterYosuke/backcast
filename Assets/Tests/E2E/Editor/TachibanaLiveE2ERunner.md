# TachibanaLiveE2ERunner — 台本（LIVE E2E 仕様 / 観測点 / 合格条件）

`TachibanaLiveE2ERunner.cs` が自動検証する **LIVE** E2E の台本。実装者は `.cs` と本 `.md` をセットで読む。
設計の木・配線の裏取りは `docs/findings/0053-e2e-tachibana-live-login-order-fill.md`、ドメイン不変条件は
[`/tachibana` SKILL.md](../../../.claude/skills/tachibana/SKILL.md) を参照。

> ⚠️ これは MOCK ではなく **実 venue（立花 demo `demo-kabuka.e-shiten.jp`）** を叩く。成行 BUY が約定すると
> demo 口座に**実建玉が残る**（owner 決定で flatten しない）。本番（実弾）には接続しない。

## 対象ストーリー

1. アプリ起動相当（batchmode で `WorkspaceEngineHost` を venue=TACHIBANA 固定で構築）
2. 立花 demo へログイン（`venue_login("env","demo")`、tkinter なし）
3. 成行 BUY を発注（7203.TSE / 100 株 / MARKET / TIF=DAY）
4. 発注中に要求される第二暗証番号を自動応答（urgent-secret lane）
5. 約定（FILLED）を確認する

## アーキテクチャ前提

- C#↔Python 境界は **pythonnet（同一プロセス直接呼び出し）**。`WorkspaceEngineHost` を**単体 new** し、render
  経路（scene / ChartView）は組まない — venue 往復が主眼で、host が `_sink/_panel/_lanes` を自己完結で所有する。
- batchmode の `WorkspaceOwnership.ShouldClaim` は Python 初期化をスキップするので、Runner は
  `host.InitializePython("TACHIBANA")` を**直接呼んで**所有権の門を迂回する（`ReplayToHakoniwaE2ERunner` /
  `KernelTeardownProbe` と同じ正当手）。
- **完全自動化の肝＝第二暗証番号**。第二暗証は env に載らない（R10、Python は `DEV_TACHIBANA_SECOND` を読まない）。
  Runner が `.env` から `char[]` で読み、発注中に sink へ push される `SecretRequired` を main の
  `DrainLiveEvents()` で拾い、`Lanes.SubmitSecret`（別スレッドの urgent-secret lane）で応答する。GUI secret
  modal の画素描画は引き続き **owner HITL**。

## 自動検証する範囲（この Runner がゲートする）

- **step 2**: `venue_login("TACHIBANA","env","demo")` が tkinter を spawn せず v4r9 公開鍵認証
  （demo 用 `DEV_TACHIBANA_AUTH_ID_DEMO` + `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` を `resolve_credentials(is_demo=True)` で解決し `sAuthId`
  ログイン + 仮想URL秘密鍵復号）で `_auth_login` 成立。poll の `venue_state` が `CONNECTED` に収束。
- **step 3-4**: `Lanes.SubmitPlaceOrder` の production lane で `CLMKabuNewOrder` が demo に飛び、発注中の
  `SecretRequired` を第二暗証で応答して ack（`status="ACCEPTED"`、`Success==true`）。
- **step 5（縫い目）**: 約定通知 EC（WS push）が `OrderEvent(status="FILLED")` として sink → `LivePanelViewModel`
  に届き、`FilledOrderCount>0` ＆ `LatestOrder.Status=="FILLED"` ＆ `FilledQty>=100`。

## 自動検証しない範囲

- **GUI secret modal の画素**（マスク表示・入力 UI）。`SecretModalE2ERunner`（旧 `SecretModalM2Probe`）が unit でカバー。Runner は
  modal を介さず urgent-secret lane を直接叩く。
- **チャート/箱庭の描画**（`ReplayToHakoniwaE2ERunner` がカバー）。
- **建玉の後始末**（flatten しない。demo の建玉積み上がりは許容＝owner 決定）。
- **板/歩み値の購読**（depth subscription）。本 Runner は発注→約定のみ。

## 観測点

| step | 観測 | 合否の意味 |
|---|---|---|
| 1 | `host.ServerReady`、ログ `[WorkspaceEngineHost] live-configured server built; …` | server 構築済み |
| 2 | `VenueLogin` onResult ok==true ＋ `VenueConnectionViewModel.IsConnected`（venue_state CONNECTED/SUBSCRIBED/RECONNECTING） | demo へログイン成立 |
| 3-4 | `SubmitPlaceOrder` onResult `Success==true`（ack `ACCEPTED`）、`SecretRequiredCount` の edge を 1 回応答 | 発注が venue に受理 |
| 5 | `panel.FilledOrderCount>0` ＆ `LatestOrder.Status=="FILLED"` ＆ `FilledQty>=100` | 成行が約定 |

> **#107（2026-06-22）**: step 2.4 の EC carrier 購読を**本番トリガに置換**した。以前は runner が
> `host.Lanes.SubmitSubscribeMarketData` を *自分で* 叩いていた（＝本番 UI の購読配線をゲートできない
> production-binding の死角）。現在は本番と同じ `LiveSubscriptionCoordinator` + `LaneSubscribeSink` を
> universe 投入 → `OnModePoll(LiveManual)` の一括購読として駆動し、テスト自身は購読 RPC を呼ばない。
> 購読が走った観測は step 2.5 の EC WS gate（実 demo の per-ticker FD WS 確立）が担う。方針: ADR-0022。

## 合格条件

- ログに `[E2E TACHIBANA-LIVE PASS] ...`、プロセス exit 0（`-quit` 併用、self-failing gate）。`error CS\d+` 0 件。
- **厳密ゲート**: 約定（FILLED）が来なければ FAIL。成行は**場中**（前場 09:00–11:30 / 後場 12:30–15:30 JST）でないと
  約定しないため、本ゲートは場中に実行する運用が前提。閉局時は FAIL メッセージに `is_market_open` 診断が付く。

## 前提条件（満たさないと FAIL）

- `DEV_TACHIBANA_AUTH_ID_DEMO` / `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` / `DEV_TACHIBANA_SECOND` が **process env か
  `.env`** にある（`EnvConfig.Get` が process env 優先 → `<repo>/.env` → `<repo>/python/.env` の順で解決。本番=無印 /
  デモ=`_DEMO` サフィックス、この demo ゲートは `_DEMO` を読む）。秘密鍵 PEM は
  リポジトリ外・パーミッション 600 推奨。Fernet 方式（`secure_config.enc` + `API_DECRYPT_KEY`）も可（ADR-0023）。
- demo 口座が **v4r9 登録済み**（API 利用設定で認証ID取得・公開鍵登録）＋ 移行期間は **電話認証→3分以内ログイン**
  （API 前提条件、SKILL.md「立花 venue 利用の前提条件」/ references/pubkey_auth.md）。
- **debug ビルド**（`IS_DEBUG_BUILD=True`）。editor batchmode は True。release は env を読まない。
- **ADR-0027**: `TACHIBANA_ALLOW_PROD` トリップワイヤは廃止（死にコード化したため削除）。実弾防止は runner が
  `environment_hint = "demo"` をハードコード固定することで保証する（端末フラグには依存しない）。

## 実行コマンド

```text
<Unity> -batchmode -nographics -quit -projectPath . -executeMethod TachibanaLiveE2ERunner.Run -logFile <log>
```

このマシンの Unity: `Unity 6000.4.11f1`（`/Applications/Unity/Hub/Editor/6000.4.11f1/...` または Windows の同版）。
compile だけ先に通すゲート: `-executeMethod` を外して同コマンド（`error CS\d+` 0 件＋return code 0）。
**CI 非組込**: 立花 demo は `workflow_dispatch` 限定（閉局による偽陰性回避、open-questions Q21）。PR/push に載せない。

## 失敗時に確認するログ・代表的な原因

- **`venue_login failed: <ec>`**: v4r9 登録未了 / 電話認証未済 / 閉局（demo サービス時間 平日 8:00–18:00 JST 外）
  / creds 誤り（認証ID・秘密鍵）。`DEV_TACHIBANA_AUTH_ID_DEMO` / `DEV_TACHIBANA_PRIVATE_KEY_PATH_DEMO` と demo 口座の
  v4r9 登録・電話認証を確認。`SERVICE_OUT_OF_HOURS`（p_errno=9）は利用時間外。
- **`venue_state never reached CONNECTED`**: ログインは ack したが poll が収束せず。WS 接続失敗 / 閉局を疑う。
- **`place_order did not return …（second password not answered?）`**: SecretRequired を拾えていない。
  `DrainLiveEvents` pump と `DEV_TACHIBANA_SECOND` の有無、urgent-secret lane の起動を確認。
- **`NO FILLED OrderEvent within Ns`**: ack は通ったが約定通知が来ない。**市場閉局中**が最有力（場中に実行せよ）。
  EC（WS）購読が確立しているか、対象銘柄が demo で取引可能かも確認。
- **segfault / GIL stall**: pythonnet boundary。`SubmitSecret` の payload は GIL 内で構築（`LiveRpcLanes` の
  CallSubmitSecret）。`host.Stop()` の lanes/launcher join 規律を確認。

## 命名規約

E2E 回帰ゲートは `Assets/Tests/E2E/Editor/<ScenarioName>E2ERunner.{cs,md}`（[ADR-0015](../../../../docs/adr/0015-e2e-runner-layout-and-script-convention.md)）。
