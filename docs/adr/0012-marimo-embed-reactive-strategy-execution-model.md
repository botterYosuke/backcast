---
status: accepted
---

# marimo embed を reactive strategy 実行モデルとする（ADR-0005-cutover の strategy-authoring 表面のみを supersede）

> **実装状態（2026-06-18）**: 本 ADR は #76 S3 スライスで **accepted**。S3 が landing させるのは ① 本 decision の記録
> ② marimo の `[project.dependencies]` 昇格（範囲 pin）③ `test_strategy_runtime_offline` の intent を「dormancy」→
> 「lazy-import 規律」へ昇格、の **3 点（=docs + manifest + gate-intent）**。**実際の per-bar 配線（KernelRunner adapter /
> dispatch）は S6、cell へ `submit_market`/`portfolio` を inject するのは S4** で、いずれも本 ADR の後続 named スライス。
> 命令型 strategy の sunset はさらに後の named スライス。**誤って「marimo 戦略が既に走る」と読まないこと**（thin_drain は
> findings 0046 のとおり dormant・未配線）。
>
> `status: accepted` は **decision が確定した**ことを指す（ADR-0005-cutover と同じく「accepted だが実装は段階的」。0% 未実装ゆえ
> `proposed` の ADR-0011 とは異なる）。execution-model の配線（S4/S6）が未実装なだけで、decision そのものは AC4 決定ゲートで確定済み。

`/grill-with-docs`（2026-06-18・#76 S3）で導出。設計正本: [Discussion #64](https://github.com/botterYosuke/backcast/discussions/64)
（本文＋R7–R14）＋ [findings 0046](../findings/0046-marimo-embed-thin-drain-runtime.md)。#76 の 3 spike（AC1 per-tick perf /
AC2 `mo.state` reactivity / AC3 async×GIL）と D6（per-cell execution-context 均一化）が PASS し、AC4 決定ゲートで owner が
**「marimo embed 実行モデルへ進む」**と判断したことを受け、その実行モデルを ADR として固定する。

## Context

- 現行の strategy は命令型 `engine.kernel.strategy.Strategy` サブクラス（`on_start`/`on_bar`/`on_stop`/`on_order` ＋
  `ctx.submit_market`）で、`engine.strategy_runtime.strategy_loader.load` が `.py` から単一 Strategy サブクラスを取り出す。
  **KernelRunner（`engine/kernel/runner.py`）は per-bar に `self._strategy.on_bar(bar)` を呼び `self._ctx.pending`/`denials`
  を読むだけで、戦略の計算方法から完全に分離**している（runner.py:222–336）。equity_curve は FROZEN #24 golden に供給される
  （ADR-0006 immutable）。
- #64 のビジョンは「アプリ全体で 1 つの再生ボタン」＋「Strategy Editor = テキスト」を **reactive cell-DAG（"Strategy
  Editor = cell"）へ置き換える**こと。これは ADR-0005-cutover（1:1 TTWR 表面 parity）が定めた **Strategy Editor 表面と
  単一 run ボタン表面**に反する。
- host-owned 痩せ drain runtime（`engine/strategy_runtime/thin_drain.py`・findings 0046・S1+D6）は、marimo App を cold
  compile し per-bar に execution-context 付きで drain する core を既に持つ（**dormant・未配線**）。これを runtime seam
  （KernelRunner）へ配線するのが S6。
- marimo は現在 **spike-only 依存**（`[dependency-groups] spike`・local editable fork）で、runtime seam は marimo を
  import しない不変条件（`tests/test_strategy_runtime_offline.py`・常時走る）を持つ。

## Decision

1. **marimo cell-DAG を今後の唯一の target authored strategy モデルとする。** 命令型 `Strategy.on_bar` / `strategy_loader`
   は **移行期のみ supported**（target ではない frozen surface）。**新規の命令型専用 kernel 機能は足さない**（機能追加は
   marimo 側のみ）。命令型の sunset は将来の named スライス（S6 の後）に委譲し、本 ADR は**方向（marimo 一本化・命令型は
   移行期 only）のみ記録**する。退役の実行時期は別 decision。
2. **kernel の per-bar 契約（`on_start`/`on_bar`/`on_stop` ＋ `ctx.submit_market`/`ctx.pending`/`ctx.denials`）を不変の
   adaptation 境界とする。** marimo は **App→compile→drain でこの契約へ adapt** する（adapter 機構＝S4 injected globals ＋
   S6 KernelRunner dispatch、詳細は各 slice の findings）。kernel を変えないため、命令型経路の equity_curve は #24 golden に
   対し byte-identical のまま保たれる。
3. **marimo を `[project.dependencies]` へ昇格する**（範囲 pin `marimo>=0.20.4`、PyPI、spike-0 検証版）。editable local
   fork は破棄する（embed 経路は `marimo._server` を引かないため fork の lazy-import 実験は不要・#76 で 不採用 済み）。
   thin_drain は marimo private API（`compute_cells_to_run` / `_find_cells_for_state` / `with_cell_id` / `get_executor` 等）
   に強依存するため、**`uv.lock` が解決版を固定し、`tests/test_strategy_runtime_thin_drain.py` gate が private-API drift を
   検出する**ことを範囲 pin の安全網とする。**marimo を upgrade するときは必ずこの gate を回す**。
4. **runtime seam の不変条件を「marimo を一切 import しない（dormancy）」から「module-load 時に marimo（top-level import が
   引く `_ai.llm`/`_plugins.ui.chat`/altair/`_server` の重い鎖）を import しない＝marimo 戦略を走らせるときだけ lazy import
   する（lazy-import 規律）」へ昇格する。** `test_strategy_runtime_offline` の機構（clean interpreter で seam を import →
   marimo が `sys.modules` に漏れないことを assert）は**据え置き、intent（docstring）だけ昇格**する。gate が
   `engine.kernel.runner` を import する以上、S6 が module-top に `marimo`/`thin_drain` を置けば即 RED ＝ **gate 据え置きが
   S6 に lazy import を構造的に強制**する。S6 の lazy import は `import marimo` ではなく thin_drain と同じ narrow-submodule
   流儀（`marimo._runtime.*`/`._ast.*` を名指しし `initialize_mimetypes`/`marimo._server` を引かない）で行う。

## ADR-0005-cutover との関係（部分 supersede）

本 ADR は **ADR-0005-cutover（1:1 TTWR 表面 parity）を Strategy Editor／単一 run ボタンの strategy-authoring 表面に関して
のみ supersede** する。他の全表面（menu_bar / settings / theme / reconcile_modal / instruments_universe_prune）の 1:1 TTWR
parity 契約は**踏襲する**。これは [ADR-0011](0011-strategy-run-cwd-is-strategy-file-directory.md) が TTWR ADR-0021 の cwd
ファセットだけを上書きし identity 決定を踏襲した **facet-scoped supersede と同型**。ADR-0005-cutover 本体は編集しない
（同 ADR 自己保護条項）。

## Considered Options

- **supersede 範囲**: 採用＝**部分 supersede（strategy-authoring 面のみ）**。embed が触らない 5 面の parity 決定まで書き直す
  全面 supersede は射程過大で既決事項を再オープンし rationale を散逸させる。後ろ倒し（ADR-0005 に触れず将来 supersede）は
  「ADR-0005 が 1:1 Strategy Editor parity を主張したまま production がそれに反する」未記録矛盾を残す。findings 0046 が S3
  での supersede を予告済みで、ADR-0011 の前例もあるため部分 supersede が筋。
- **dep の形**: 採用＝**PyPI 範囲 pin `marimo>=0.20.4`**。editable fork 維持は reproducible でなく（fork が repo 隣に必要）
  永続 fork 保守を負う。exact pin は drift を完全に防ぐが、owner は範囲 pin（lock＋gate を安全網とする運用）を選択。
- **offline gate の運命**: 採用＝**S3 で昇格＋gate 機構据え置き・intent を lazy-import 規律へ**。gate 退役は軽起動/orphan-free
  （非 marimo 経路）の保護と S6 への lazy 強制を捨てる愚策（top-level `import marimo` は ~500ms ＋ `_ai.llm`/altair/`_server`
  鎖を毎回背負う実測）。promotion を S6 まで遅延する案は findings 0046 の「昇格=S3」順序とずれ、ADR↔manifest 乖離を生む。
- **execution-model 立場**: 採用＝**marimo=target / 命令型=移行期共存**。命令型 即退役は (a) Q3 の lazy-import 規律の正当化
  （marimo 無し命令型 Replay の存続）を自壊させ (b) FROZEN #24 golden ＋ 既存 Strategy/テストの S6 一括書換で blast radius 大。
  恒久共存は 2 モデル永久保守＝Strategy Editor 表面が 2 つ残り #64 の「置き換え」ビジョンに逆行する。

## Consequences

- 命令型と marimo の **2 モデルが移行期に共存**する（Strategy Editor 表面が一時的に 2 つ）。恒久共存ではなく、Decision §1 の
  非対称（marimo=target / 命令型=暫定・命令型に新機能を足さない）で恒久共存への漂流を防ぐ。
- marimo が prod 依存になり、embedded Python runtime（ADR-0002）に marimo＋transitive deps が入る（install 容量増）。単一
  アプリ運用のため runtime/security 上の問題にはしない。
- **範囲 pin のため将来 marimo が private API を壊すと thin_drain gate が RED になり得る**。その時点で upper bound 追加 or
  thin_drain 適応を判断する（gate がトリガー）。
- `tests/test_strategy_runtime_thin_drain.py` gate は marimo が prod 依存になることで **default test run でも走る**（spike
  group 限定でなくなる）。`pytest.importorskip("marimo")` は冗長化するが防御として残してよい。
- 下位の実装事実（adapter の形・loader の marimo App 検出・lazy import の正確な submodule 集合・S4 injected globals の API）は
  本 ADR に書き戻さず **S4/S6 の `docs/findings/` に記録**し、本 ADR を「方針: ADR-0012」として参照する。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。ADR-0005-cutover
は別 decision の固定 oracle として（strategy-authoring 表面を除き）踏襲し、編集しない。下位の実装事実は各 slice の
`docs/findings/` に記録し、本 ADR を「方針: ADR-0012」として参照する。
