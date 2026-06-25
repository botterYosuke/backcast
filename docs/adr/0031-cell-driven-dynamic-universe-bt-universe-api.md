---
status: accepted
supersedes: ADR-0016 D6 (facet — "universe は cell に書かない" のみ)
---

# 戦略 cell が universe を動的に編集できる `bt.universe.*` API（ADR-0016 D6 の「universe は cell に書かない」facet を supersede）

`/grill-with-docs`（2026-06-25 owner セッション）で導出。owner telos:「python ソースから Instruments を双方向（読み/書き）に操作し、Unity アプリに反映される動線がほしい」。設計正本: 本 ADR ＋後続 slice の `docs/findings/`。

> **実装状態**: 本 ADR は **accepted**（docs + glossary のみ）。実 cell-facing API（`bt.universe.add/remove/clear/list`）／Python→C# registry ブリッジ／Replay stepper の mid-stream 銘柄合流／LiveAuto 購読配線の `Registry.Changed` 化は後続の named slice で順番に landing する。`status: accepted` は **decision が確定**したことを指す（ADR-0012 / ADR-0016 と同じく「accepted だが実装は段階的」）。

## Context

- **ADR-0016 D6** は「`universe / start / end / cash / granularity` は `ScenarioStartupTile` が所有し、**cell に書かない**」と固定し、cell に残すのは strategy ロジックだけとした。これは「config = startup panel / results = Hakoniwa」という責務分離の一環で、universe は `InstrumentRegistry`（C# SoT）＋ user 編集（sidebar / picker / startup tile）が唯一の populate 入口だった。
- **CONTEXT.md の所有権不変条件**（universe membership は「所有者はユーザーだけ・**システムは絶対に銘柄を足さない/減らさない/間引かない**」）も、autonomous なシステム操作（#253 の prune 回帰がその典型）を禁ずるために存在する。
- owner 問い:「戦略を書く Python コードから、扱う銘柄集合そのものを出し入れしたい。しかもバックテスト/自動売買が**走っている最中**にも。そして登録した universe は Python から**読み返せる**ようにしたい」。
- これは ADR-0016 D6 の「universe は cell に書かない」と正面衝突する。一方で CONTEXT.md の universe glossary は既に **populate 軸の入力に "strategy" を列挙**しており（「SoT は populate 入力（sidecar / strategy / picker click / user 編集）で埋まる」）、戦略由来の populate 自体は禁止されていない。衝突しているのは「**cell が config を書くか**」という ADR-0016 D6 の facet と、「**走行中の動的編集**」という新しい射程である。
- 既存配線の状況（2026-06-25 時点・grill でコード裏取り）:
  - universe SoT = C# `InstrumentRegistry`（`replace_all` / `Editable` / `Ids`）。永続化は `<strategy>.json` の `scenario.instruments`（`writeback_scenario_instruments_system`・Replay mode gate）。
  - **chart spawn/despawn は `InstrumentRegistry.Changed` 配線済み**（`BackcastWorkspaceRoot.cs:454` `_scenario.Universe.Changed += SyncChartWindowsToUniverse`）。誰が registry を変えても発火する。
  - **market-data 購読は UI アクション配線**（`LiveSubscribeHook = _subCoord.OnLiveRowSelected`・row-select / [+ Add] でのみ発火）。`Registry.Changed` には繋がっていない。unsubscribe-on-remove は**存在しない**（#107 の方針「購読は membership に従属・購読失敗でも membership から落とさない」）。
  - Replay の bars は `from_scenario` で `load_universe_bars(data_root, instrument_ids, start, end, granularity)` により **start 時に固定 instrument 集合**を読み込み、`KernelStepper(bars=, instrument_ids=)` が stream する（`backtester.py:138-156`）。
  - cell が `bt.replay()` / `bt.step()` を駆動するモデルは ADR-0016（B2/B3）＋ ADR-0025（mode-aware bt: Replay/LiveAuto を同一 cell が駆動）で確立済み。`bt` の公開 API は ADR-0016 で「locked」。

## Decision

### D1 — 戦略 cell は `bt.universe.*` で universe を編集できる（ADR-0016 D6 の universe facet を supersede）

ADR-0016 D6 のうち「**universe は cell に書かない**」facet **のみ**を supersede する。`start / end / cash / granularity` は引き続き `ScenarioStartupTile` 所有・cell に書かない（D6 の残りは踏襲）。universe については、戦略 cell が **`bt` ハンドル経由**で編集できる:

```python
bt.universe.add("9984.TSE")      # 1 銘柄を SoT に足す
bt.universe.remove("7203.TSE")   # 1 銘柄を SoT から外す
bt.universe.clear()              # SoT を空にする
ids = bt.universe.list()         # 現在の SoT を読み返す（list[str]）
```

**丸ごと置換 API（set/replace_all）は public に出さない**——owner は add / remove / clear の操作一式を要求した。「丸ごと入れ替え」は `clear()` ＋ `add()` で表現できる。

### D2 — マスター（SoT）は C# `InstrumentRegistry` のまま。`bt.universe.*` は「プログラムによるユーザー編集」

universe の正本は今後も **C# `InstrumentRegistry`**。`bt.universe.*` は Python を SoT に昇格させず、**ユーザーが sidebar を手編集したのと同型の registry mutation** として作用する。Python は双方向の**クライアント**（書き＝registry を変える／読み＝registry の現在値を受け取る）。

帰結として下流カスケードは**既存の registry-change 機構をそのまま流用**する:

- **chart**: `InstrumentRegistry.Changed` 配線済みなので add→spawn / remove→despawn は**タダで反映**（新規配線不要）。
- **永続化**: registry 変更で dirty になり、**既存の Save タイミングでのみ** sidecar に書く（D4）。
- これにより「Unity に反映される動線」は chart については追加配線ゼロで成立する。

### D3 — 対象モードは Replay ＋ LiveAuto（LiveManual 対象外）。走行中の動的編集を許す

`bt.universe.*` が意味を持つのは **戦略（cell）が運転席に座るモード** = **Replay** と **LiveAuto**。**LiveManual は対象外**（人間が order ticket で手動取引するモードで、universe はユーザーが直接所有する）。編集は **stop 前の setup 時に限らず、走行中（mid-run）にも**有効とする。

### D4 — 保存は「既存 Save タイミング」のみ。独自タイミングで保存しない

`bt.universe.*` の編集は registry を即時に変え（＝即反映＋dirty）、**永続化は既存の Save 経路（`writeback_scenario_instruments_system` が発火する従来のタイミング）でのみ**行う。`bt.universe.add` 自体が新しい sidecar 書き込みトリガを引くことは**しない**——ユーザーの手編集と同じ「dirty にして、いつもの Save で落ちる」挙動に揃える。

### D5 — Replay 走行中の銘柄追加は「追加時点から」データを合流させる

Replay 走行中に `bt.universe.add(X)` した場合、X のデータは**追加した replay 時刻以降**を流す（巻き戻して最初から再生はしない／追加を禁止もしない）。実装は現在の replay 時刻〜`scenario.end` の X の bars を DuckDB から読み、`KernelStepper` の残ストリームへ ts 順に merge し、`instrument_ids` を動的に更新する。「途中参加（mid-stream join）」の自然な振る舞いとする。

### D6 — LiveAuto では add→subscribe / remove→unsubscribe（対称）。購読を `Registry.Changed` 起動へ拡張する

LiveAuto で:

- **add → venue へ subscribe**（板/価格 feed 開始）。これを成立させるため、現状 UI クリック配線（`LiveSubscribeHook`）の購読起動を **`Registry.Changed` 起動へ拡張**し、誰が（UI でも Python でも）registry に銘柄を足しても新規分が購読されるようにする。
- **remove → venue へ unsubscribe**（feed 停止）。**unsubscribe-on-remove は新規配線**（今は存在しない）。使わない feed を残さず venue の実購読枠を節約する。

membership と購読の従属関係（#107: 購読は membership に従属・購読失敗で membership を落とさない）は維持する——本 ADR が足すのは「membership 変化 → 購読の対称追従」であって、購読失敗が membership を変える経路ではない。

### D7 — 所有権不変条件との両立（populate であって system prune ではない）

CONTEXT.md の「システムは絶対に銘柄を足さない/減らさない/間引かない」は **autonomous なシステム操作**（#253 prune 回帰が典型）を禁ずるもの。`bt.universe.*` は **ユーザーが自分で書いた戦略を、自分で実行した結果**の編集＝**populate 軸（user-triggered）**であり、glossary が既に列挙する "strategy" populate 入力の **動的・双方向への拡張**である。よって禁止対象の「自律的システム prune」には当たらない。この線引きを CONTEXT.md glossary に明記する（本セッションで更新）。

## Considered Options

- **SoT の所在**: 採用＝**C# `InstrumentRegistry` のまま**（Python は双方向クライアント）。**却下**＝SoT を Python へ移す——chart / sidebar / sidecar 永続化の消費側を全面的に作り直す射程過大で、owner も「Unity のままでいい」と明示。
- **API の操作セット**: 採用＝**add / remove / clear / list**。**却下**＝丸ごと置換（replace_all/set）を public に出す——owner は粒度のある出し入れを要求、置換は clear+add で表現可能。
- **API の置き場**: 採用＝**`bt` ハンドル**（`bt.universe.*`）——既存の `for bar in bt.replay(): bt.submit_market(...)` と同一世界。**却下**＝module 関数（`register_universe([...])`）——run コンテキスト取得に別配線が要り、cell-drives-bt モデルから外れる。
- **対象モード**: 採用＝**Replay ＋ LiveAuto**。**却下**＝Replay のみ（owner は LiveAuto も要求）／LiveManual を含める（人間所有モードに strategy 編集を持ち込むのは所有権が混線）。
- **編集タイミング**: 採用＝**走行中も**（動的 universe）。**却下**＝start 前固定のみ（owner は mid-run を明示要求）。
- **Replay 途中追加のデータ起点**: 採用＝**追加時点から合流**。**却下**＝最初から再生やり直し（run 中断・再走で重い）／Replay では途中追加禁止（owner は途中追加を要求）。
- **保存タイミング**: 採用＝**既存 Save タイミングのみ**（独自トリガを引かない）。**却下**＝編集ごとに即永続化（owner が「勝手なタイミングで保存しない」と明示）／その回だけで一切残さない（owner は「残す」と明示）。
- **LiveAuto remove の購読**: 採用＝**unsubscribe する**（add↔remove 対称）。**却下**＝購読は残す（使わない feed が venue 上限を食う）。
- **ADR-0016 の supersede 範囲**: 採用＝**facet-scoped supersede**（D6 の「universe は cell に書かない」facet のみ。D1–D5 / D7–D11 および D6 の `start/end/cash/granularity` 所有は踏襲）。**却下**＝ADR-0016 編集（自己保護条項に反する）／全面 supersede（射程過大）。**ADR-0016 が ADR-0012 を facet-scoped supersede した文型と同型**。

## ADR-0016 / ADR-0025 / ADR-0006 / ADR-0007 との関係

- **ADR-0016 D6 facet supersede**: 「universe は cell に書かない」**のみ** supersede。`start/end/cash/granularity = startup panel 所有・cell に書かない` と results = Hakoniwa は**踏襲**。ADR-0016 本文は**無改変**（自己保護条項）。
- **ADR-0016 の他 decision 踏襲**: D1（notebook=backtest 一本化）／D3（`bt` lifecycle）／D5（kernel per-bar 契約＝不変の adaptation 境界）／D8-D9（速度・GIL）は本 ADR でも前提。`bt.universe.*` は `bt` の公開 API を**拡張**する（ADR-0016 が「locked」とした surface への追加＝本 ADR が明示的に解禁する）。
- **ADR-0025 踏襲＋拡張**: mode-aware bt（同一 cell が Replay/LiveAuto を駆動）を前提に、`bt.universe.*` の挙動を mode 別に定義（D3/D5/D6）。
- **ADR-0006 踏襲**: #24 golden は byte-identical。universe を**編集しない**従来 cell の per-bar order/fill/equity は不変。`bt.universe.*` を**呼ばない**run は挙動が変わらないこと（既存 golden 影響ゼロ）を実装 slice の gate で pin する。
- **ADR-0007 踏襲**: Replay portfolio projection の権威は不変。本 ADR は universe membership の編集経路を足すだけで、portfolio 計算経路には触れない。

## Consequences

- **新規 cell-facing API `bt.universe`**（`add/remove/clear/list`）が `backtester.py` 系に landing する。`bt` 自身の marimo-free 規律（lazy-import）は維持。
- **Python(worker thread)→C# `InstrumentRegistry` のブリッジ**を新設する必要がある（`bt.universe.*` の registry mutation を Unity 側へ届ける push 経路）。#65 の `ReplayKernelObserver → engine.last_portfolio → poll lane → Hakoniwa` が Python→Unity push の先例。正確な seam は実装 slice の `docs/findings/` に記録。
- **LiveAuto 購読配線の `Registry.Changed` 化＋unsubscribe-on-remove 新設**（D6）。`LiveSubscriptionCoordinator` / `BackcastWorkspaceRoot` の購読フックを拡張。
- **`KernelStepper` の mid-stream 銘柄合流**（D5）。現在 replay 時刻以降の bars を読んで残ストリームへ ts 順 merge し `instrument_ids` を更新する経路を追加。
- **chart 反映は追加配線ゼロ**（`Registry.Changed` 既配線）。
- **golden 影響ゼロの担保**: `bt.universe.*` 不使用 run の byte-identical を実装 slice の parity gate で確認。
- **下位の実装事実**（registry ブリッジの正確な class／purge 経路／stepper の merge 実装／購読フックの配置／clear の chart 一括 despawn 挙動）は本 ADR に書き戻さず実装 slice の `docs/findings/` に記録し、本 ADR を「方針: ADR-0031」として参照する。
- 関連 issue（既存の universe/registry 系 #29 / #31 / #41 / #107 / #253）の不変条件は本 ADR の populate/prune 線引き（D7）と整合させて読む。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。ADR-0016 / ADR-0025 / ADR-0006 / ADR-0007 / ADR-0012 は別 decision の固定 oracle として（本 ADR が明示 supersede した ADR-0016 D6 の「universe は cell に書かない」facet を除き）踏襲し、編集しない。下位の実装事実は各 slice の `docs/findings/` に記録し、本 ADR を「方針: ADR-0031」として参照する。
