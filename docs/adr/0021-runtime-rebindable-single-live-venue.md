---
status: accepted
---

# 実行時に再バインドできる単一 live venue（起動時 LIVE_VENUE ロックの撤去）

owner 依頼（2026-06-22）「Venue メニューで Tachibana(Demo) をクリックすると `login failed: VENUE_MISMATCH` になる。『起動時に 1 venue へ固定される仕様を廃止する』」を受けた決定。grill HITL（2026-06-22）で 4 点を確定した:

1. **scope = 単一 venue・実行時再バインド**。同時に接続するのは常に 1 venue。複数 venue 同時接続（engine の `venue_sm` / `mode_manager` / portfolio を全面 venue 多重化する大規模リアーキ）は不採用——報告された不具合の解消に不要で、engine 全体が単一 venue 前提。
2. **LIVE_VENUE は残す（任意の初期選択）**。未設定＝起動時に既定 MOCK でサーバを建て、メニューで任意 venue を選べる。設定済み＝その venue を初期 factory にし、メニューをその venue だけに絞る。ロックはしない。
3. **起動時の自動接続はしない**。LIVE_VENUE は初期 factory とメニュー絞り込みを決めるだけ。接続はメニュー / HITL harness の Connect で開始（回帰ゼロ）。
4. **MOCK は据え置き**。credential-less な E2E / probe（`OrderTicketE2ERunner` 等多数）の土台なので削除しない。

## なぜ新規 ADR か

「1 backend = 1 venue・起動時バインド」は **D26**（`live_orchestrator.py` のコメント＋ findings 0014 RH5）で決まった不変条件だが、専用の番号付き ADR は無い。本決定はその不変条件を **正面から反転**する（hard-to-reverse・他読者には surprising・実トレードオフあり）ので、findings への追記ではなく ADR に固定する。D26 は ADR ではないため supersede 対象の ADR ファイルは無い——本 ADR が D26 の startup-lock 面を置換し、コード／findings の D26 参照は本 ADR を指すよう更新する。

関連: findings 0014（D26 one-per-server の元実装・本 ADR が startup-lock 面を撤去）／findings 0085（下位事実・RED→GREEN・AFK 再走手順）／findings 0027（mainline Venue メニュー cutover・本 ADR がメニュー絞り込みを足す）。

## Context

venue は `InprocLiveServer(engine, venue)` 構築時に `build_live_adapter_factory(venue)` で **adapter factory 1 個**として束ねられ、`live_orchestrator.venue_login` が `configured_venue != venue_id` を **VENUE_MISMATCH** で拒否していた（D26）。`BackcastWorkspaceRoot.Awake` は `LIVE_VENUE` env（既定 MOCK）で venue を解決し `InitializePython(_venue)` でサーバを建てる。

ところが mainline の Venue メニュー（`MenuBarView.BuildVenueMenu`）は構成 venue に関係なく `ConnectVariants` 4 つ（Tachibana/kabu × demo/prod）を常に表示する。既定（LIVE_VENUE 未設定＝MOCK サーバ）で「Connect Tachibana (Demo)」を押すと、MOCK サーバへ TACHIBANA login → VENUE_MISMATCH。これが報告された不具合。venue 固有の状態は **factory のみ**（`venue_sm` / `mode_manager` / portfolio は venue 非依存）なので、login 時に factory を作り直せば実行時 venue 切替は技術的に可能。さらにメニューは接続中は全 Connect variant を grey-out（`CanConnect => !IsConnected`）するため、**venue 切替は必ず切断済み状態からのみ**発生し、接続中ホットスワップの危険ケースは UI が既に防いでいる。

## Decision

- **D1 (実行時再バインド)**: `venue_login(venue)` は、**切断中**かつ要求 venue ≠ 現 bind venue（または factory 未構築）のとき、`build_live_adapter_factory(venue)` で factory を作り直し `_live_venue_id` を更新してログイン続行する。起動時バインドは「初期 factory」であって「ロック」ではない。
- **D2 (接続中の防御)**: **別 venue へ接続中**（`venue_sm.current ∈ {AUTHENTICATING, CONNECTED, SUBSCRIBED, RECONNECTING}`）に別 venue の login が来たら **VENUE_MISMATCH を維持**。ホットスワップはしない。UI が既に防ぐので通常到達しない defense-in-depth。同一 venue 接続中は従来どおり idempotent success。
- **D3 (LIVE_VENUE = 初期選択＋メニュー絞り込み)**: `LIVE_VENUE` は (a) 起動時の初期 factory venue（`ResolveLiveVenue`・既定 MOCK）と、(b) メニュー絞り込み（`ResolveExplicitLiveVenue`・未設定=null）を決める。**未設定 → 全 variant 表示**（＋editor 限定 MOCK dev）。**明示設定 → その venue の variant のみ表示**（他 venue・MOCK dev を隠す）。prod は従来どおり `*_ALLOW_PROD` gate。
- **D4 (自動接続なし)**: LIVE_VENUE 設定でも起動時に自動ログインしない。接続はユーザー / harness 起点（回帰ゼロ）。

## 不採用

- **不採用：複数 venue 同時接続**。engine の `venue_sm` / `mode_manager` / portfolio / orders がすべて単一 venue 前提で、多重化は全面再設計。報告された不具合の解消に不要（grill Q1 で owner が単一 venue を選択）。
- **不採用：接続中のホットスワップ（自動 teardown→再接続）**。`OnVenueDisconnect` の stop-then-logout 順序（active LiveAuto の graceful-stop）を venue_login 内で再現することになり脆弱。UI が接続中の他 venue クリックを既に防ぐので不要。
- **不採用：LIVE_VENUE 完全削除**。HITL harness（`ConnectConfigured`）・Tachibana live E2E・autospawn live driver が依存。owner が「ロックの撤去」を望んだのであって env の削除ではない（grill Q2）。
- **不採用：起動時自動接続**。実 venue だと Play と同時に tkinter 認証プロンプトが無人で立ち上がり HITL/E2E の邪魔（grill Q3）。

## Consequences

- **engine**: `live_orchestrator.venue_login` の `configured_venue != venue_id → VENUE_MISMATCH` ブロックを D1/D2 の再バインド＋接続中防御に置換。`_KNOWN_VENUES` 検証は前段に残るので未知 venue は UNKNOWN_VENUE で先に弾かれる。
- **C# メニュー**: 絞り込みロジックを純 `VenueMenuViewModel.VisibleConnectItems(filterVenue, isEditor)` に抽出（AFK が uGUI 生成なしで駆動できる）。`MenuBarView.Bind` は `devVenue` を `filterVenue`（明示 LIVE_VENUE or null）に置換。`BackcastWorkspaceRoot` に `ResolveExplicitLiveVenue()` を新設。
- **back-compat**: 既存 E2E（`InitializePython("MOCK"/"TACHIBANA")` → 同 venue login）は bound==requested で再バインドを踏まず不変。`LiveDemoRoundtripMenu` の「set LIVE_VENUE and re-Play / VENUE_MISMATCH」warn は harness が `ConnectConfigured`（=`_venue`）を使う事実に即して正確化（メニュー経由は再バインドで再 Play 不要と追記）。
- **AFK 正本の拡張**: `VenueMenuM3Probe` に `VenueMenuFilterByLiveVenue` section を追加（未設定=5項目・player=4項目・pinned=2項目・MOCK pin=1項目）。Python seam は `test_venue_mismatch_inproc_server.py` を反転（切断中再バインド GREEN／接続中 VENUE_MISMATCH）。実装着手前に `behavior-to-e2e` を formal invoke 済み。
- **下位事実は findings 0085 に固定**（venue_sm 状態集合・rebind 条件・RED→GREEN・AFK 再走手順・.env.example 更新）。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。下位事実（rebind を発火させる venue_sm 状態集合・factory 再構築の細部・メニュー絞り込みの項目数）は本 ADR に書き戻さず findings 0085 に記録し本 ADR を「方針: ADR-0021」として参照する。
