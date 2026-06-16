---
status: accepted
supersedes: ADR-0009 Decision 4
---

# 本番 root が venue+replay 兼用の永続単一 `WorkspaceEngineHost` を所有する（per-run `ReplayEngineHost` を廃する）

`grill-with-docs`（2026-06-15〜16・#42 cutover / #39→#59 統合）で導出。production の **engine 所有形態**を固定する。
本 ADR は **ADR-0009 Decision 4 のみ**を狭く supersede する（ADR-0009 の他の decision = scene-authored
composition root / single Play-owner / `RuntimeInitializeOnLoadMethod` の HITL 限定 はそのまま有効）。
上位方針として **ADR-0001（in-proc 埋め込み・orphan 不在）/ ADR-0005（1:1 表面 parity）/ ADR-0009（残り decision）**
を参照する（いずれも supersede しない）。

## Context

ADR-0009 D4 は「Replay orchestration は durable な `ReplayEngineHost` が所有」と固定した。`ReplayEngineHost` は
**run ごとに inproc server を構築**する Replay 専用 host だった。一方 #42（venue 統合 menu bar）の完成版は
`ProductionLiveShell` の**永続 live server**上で既に稼働しており（findings 0017 §8 で HITL 14/14 PASS）、#59 が
`BackcastWorkspaceRoot` を本番 scene の単一エントリにした結果、**完成版 live UI が本番 scene に載っていない**
ねじれが生じた（sidebar item2 の `VenueConnectionViewModel` 二重インスタンス配線漏れはこの症状）。

裏取り（一次コード読み）: `InprocLiveServer` / `BackendService` は **venue+replay を 1 クラスに保持**し、`_replay_state`
は `IDLE↔LOADED↔RUNNING` で再 run 可、`venue_state` と `replay_state` は独立フィールドで共存できる。よって
「永続単一 server を起動時に1本建て、Replay も Live も同じ server で回す」は技術的に成立する。per-run 構築モデル
（ADR-0009 D4）では、本番 scene に常時 venue 接続を保持できず、venue/live と replay を別 server に割らざるを得ない。

## Decision

1. **本番 composition root `BackcastWorkspaceRoot` は、起動時（`Awake`）に live 構成の永続単一 server を1本建てる**
   （`DataEngine` + `set_rust_event_sink` + `InprocLiveServer(de, venue)`）。Replay も Live も**同じ server**で回す。
2. **その server / engine lifecycle / launcher / poll / transport RPC / live seam を、durable な `WorkspaceEngineHost`
   が所有**する（`ReplayEngineHost` を一般化・改名した型）。host は Replay API（`load_replay_data`/`start_engine`/
   `pause`/`resume`/`step`/`set_speed`/`force_stop`）に加え live RPC（`VenueLogin` / `SetExecutionMode` /
   `RegisterAndStartLiveAuto` / `Pause|Resume|StopLiveStrategy` / `StopLiveThenSetMode` / **`VenueLogout`**）を公開する。
   poll は `LiveRpcLanes` に一本化し、main は GIL-free で render する。
3. **`BackcastWorkspaceRoot` は View/VM と Host の結線のみ**を担い、orchestration 本体は抱えない（ADR-0009 D2/D4 の
   single-owner・orchestration 分離は維持。所有先が `ReplayEngineHost`→`WorkspaceEngineHost` に変わる）。
4. **`ProductionLiveShell` は即時削除しない**。#23（manual order ticket / live panels / tachibana live-demo roundtrip
   HITL）の現 home であり、その re-home が済むまで HITL harness として温存する。退役は **4 スライスの tracer-bullet**
   で進める：①engine 所有統合（**完了**）②venue 統合 menu bar を本線へ（venue submenu + File 操作 mode 副作用 +
   host `VenueLogout`・本スライス）③発注/建玉/Auto パネル移設（#23/#39/#57）④`ProductionLiveShell` 全面退役＋parity 仕上げ。

## Considered Options

- **採用：永続単一 `WorkspaceEngineHost`**。本番 scene が常時 venue 接続を保持でき、Replay/Live が同一 server で
  一貫。単一 `VenueConnectionViewModel` で item2 の二重インスタンスを構造的に解消。代償：server を起動時 eager
  構築するため startup コストと依存（duckdb 欠落で起動時に engine が死ぬ）に敏感（[[venv-uv-duckdb-gotcha]]）。
- **不採用：ADR-0009 D4 の per-run `ReplayEngineHost` 踏襲**。本番 scene に live UI を載せられず、venue/live を別 server
  に割る必要があり、完成版 live UI が `ProductionLiveShell` に取り残されたままになる。
- **不採用：本番 root に live server、replay は別 per-run server の二本立て**。`InprocLiveServer` が両 RPC を1 façade に
  持つのに二重所有となり、poll/teardown 経路が二系統化して GIL 規律と単一所有を崩す。

## Consequences

- **再 run 時の stale フレーム**：永続 server 上で replay を再 load+start するため、前 run の最終フレームが一瞬残り得る
  （slice 1 の既知 Low・HITL 検証項目）。
- **startup 依存**：起動時 eager 構築のため `duckdb`（ADR-0006・`pyproject.toml` 宣言依存）未導入だと起動で engine が
  死ぬ。`python/` で `uv sync`（uv 管理 venv・pip 無し）で解消（[[venv-uv-duckdb-gotcha]]）。
- **退役の段階性**：`ProductionLiveShell` と本線 root が当面**併存**する（前者=#23 harness、後者=production）。venue を
  両方が持つ重複は slice 4 まで許容する。
- 詳細な実装事実・スライス境界・検証項目は **`docs/findings/0026-footer-liveauto-launch.md`（#39→#59 統合節）/
  `docs/findings/0027-mainline-menu-bar-venue-cutover.md`（slice 2）/ `docs/findings/0017-menu-bar-global.md`（menu bar
  oracle / Model A）** を参照（本 ADR には重複させない）。

## Status note

`status: accepted`（2026-06-16）。slice 1（engine 所有統合）の AFK ゲート（`test_live_configured_server_replay_intact.py`
/ `test_live_auto_lifecycle_inproc_server.py`）GREEN ＋ owner-run HITL（`WorkspaceEngineHost` 実機 bring-up GREEN・
findings 0026 末尾）をもって、`WorkspaceEngineHost` 所有モデルを accepted とする。slice 2〜4 は本 ADR を方針として参照する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。
下位の実装事実は本 ADR に書き戻さず findings（0026/0027/0017）に記録し、本 ADR を「方針: ADR-0010」として参照する。
