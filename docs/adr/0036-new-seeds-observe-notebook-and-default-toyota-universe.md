# New/初期化は観察用 marimo ノートとトヨタ universe を種付けし、untitled でも Run 可にする

**Status:** accepted (2026-06-26)。**findings 0050 の「本体 seed 却下＝空セル＋placeholder」決定**と、**#76 の「空白 New＋Save まで Run ブロック（WYSIWYR）」決定**を supersede する。ADR-0013（cell-as-floating-window 集約モデル D1–D4）・ADR-0016（per-cell RUN 実行モデル）・ADR-0011（run cwd=戦略 dir）は**不変**で、本 ADR は参照のみ（編集しない）。

## Context

#76 / findings 0050 は、新規ノート（File→New・no-resume boot・最初の editor spawn）を **空セル 1 枚＋placeholder ヒント**（`get_bar()` / `get_portfolio()` / `submit_market(qty)` を CodeMirror placeholder に表示・本体は空）に着地させ、本体への seed 焼き込みを **却下**した。理由は ①marimo 非忠実 ②毎回「消すべき例」が本文に付く、の 2 点（findings 0050 §placeholder）。同時に #76 は「no-resume boot と File→New は同一の空白状態に着地し、Run は Save するまでブロック（WYSIWYR）」を凍結した（`BackcastWorkspaceRoot.OpenFileNewDefault`・`RunReadinessViewModel` の `NoStrategy` ゲート）。

owner はこの 2 つを覆し、起動直後に「**トヨタを眺めて replay 観察できる、すぐ Run 可能な雛形**」へ着地させたいと要求（2026-06-26）:

- 最初の Strategy Editor は placeholder ではなく、`bt.replay(bars_per_second=2)` で全 bar を観察する（売買ゼロの）コードが**本文に種付け**された状態で spawn する。
- New / 初期化時、universe にデフォルトでトヨタ（`7203.TSE`）が登録済みであること。

`bt.replay(bars_per_second=2)` は現行 API として実在・実行可能（`python/strategies/v19/v19_morning_cell.py` が同形の正本・`engine/strategy_runtime/backtester.py:288`）であることをコードで裏取り済み。よって技術的障壁は無く、論点は **2 つの凍結決定の逆転を正面から記録すること**だけである。設計の木と裏取り証跡は findings 0124。

## Decision

1. **New/初期化は観察用ノートを種付けする。** 空セル＋placeholder ではなく、唯一のセルに以下の本文を種付けして spawn する（セルが持つのは関数本体のみ・`cell_synthesis.py` が `@app.cell / def _(bt): / return` を自動付与）:
   ```
   for bar in bt.replay(bars_per_second=2):
       pass  # 観察のみ。bt.submit_market() を呼ばない＝売買ゼロ
   ```
   findings 0050 の「本体 seed 却下」を **supersede**。種は marimo 忠実な唯一セルとして展開され、synth 出力は owner 指定の `import marimo / app = marimo.App() / @app.cell def _(bt): ... return` と一致する。

2. **placeholder（host-API hint）機構を撤去する。** `get_bar/get_portfolio/submit_market` の placeholder ヒント（const・`SetPlaceholderHint`・単一セル判定）を廃止。種セルは非空なので元々表示されないが、機構を残すとセルを空にしたとき **種と異なる API のヒント**が蘇り不整合になるため完全に撤去する。

3. **デフォルト universe = トヨタ（`7203.TSE`）。** New/初期化（sidecar 無しの fresh 経路）で universe SoT（`InstrumentRegistry`）を空ではなくトヨタ 1 銘柄で満たす。既存配線 `InstrumentRegistry.Changed → SyncChartWindowsToUniverse` により、トヨタの chart tile も同時に spawn する（universe↔chart の不変条件「chart ⊆ universe」をそのまま honor・追加制御なし）。

4. **untitled でも Run 可（WYSIWYR ゲートの逆転）。** Run の実行系は既にバッファ synth（`SynthesizeLiveSource`）であり、ディスクのファイルは読んでいない。ゲート（`RunReadinessViewModel.strategyReady`）を **「named doc なら従来 WYSIWYR / untitled なら非空セルあり」** に拡張し、種付き untitled を起動直後から Run 可能にする。#76 の「Save まで Run ブロック」を **untitled に限り supersede**（named doc の dirty ゲートは無改変）。

5. **untitled Run は遅延 scratch 方式。** untitled の Run 押下時に、現バッファを synth した `.py` を scratch に書き出し、それを実行する（cwd = scratch dir＝ADR-0011 の「実行する `.py` のディレクトリ」方針に**追従**し、ADR-0011 §4「未保存時 cwd 未定義」を本 ADR が解消する）。scratch は New 時点では生成せず（eager 不採用）、`_path=null` を保つことで menu badge の「Untitled」表示と未 Run 時のディスク非汚染を維持する。Save As で実パスへ束ね直すと以降は通常の named WYSIWYR に戻り、scratch は破棄してよい。

## Considered Options

- **採用：本文に観察コードを種付け＋placeholder 撤去（D1–D2）。** owner の明示要求。起動直後に「動かして眺められる」雛形を与えオンボーディング摩擦を下げる。トレードオフ：marimo 汎用ノートとの非忠実性（findings 0050 が避けた領域）を**意図的に許容**する——backcast は「即観察できる雛形」を忠実性より優先する。
- **不採用：placeholder 維持（本体空のまま）。** findings 0050 の現状。marimo 忠実だが「注入グローバルの存在を示す」以上の onboarding 価値が無く、owner が雛形種付けを要求したため却下。
- **採用：untitled 遅延 scratch（D5）。** `_path=null` を保ち Untitled badge とディスク非汚染を両立。起動直後 Run 可はゲート述語（非空セル）で満たす。
- **不採用：New 時点で scratch を即生成・バインド（eager）。** badge が scratch 名を出す不整合＋未 Run でもファイルが残るため却下。owner 可視挙動は遅延と同一。
- **不採用：buffer 実行・ファイル一切作らず cwd フォールバック定義。** ADR-0011 の「実行する `.py` のディレクトリで実行」直感と乖離し、未保存 run に実 cwd を与えられない。owner が scratch 方式を選択したため却下。

## Consequences

- **findings 0050 / #76 の 2 決定が逆転される。** 本 ADR が正本。findings 0050（slice 記録）と #76 由来のコメント/findings には dangling とならないよう「supersede: ADR-0036」の stale-marker を該当 slice 側に足す（ADR ファイル自体は無編集）。
- **既存 probe / golden / test の更新が要る**（regression net）: 「New→空セル 1 枚」「Untitled blank」「placeholder 表示」を pin する assertion（`StrategyEditorProbe` placeholder 系・New 系 golden・`RunReadinessViewModel` の `NoStrategy` ゲート）。種付き・placeholder 撤去・untitled Run 可へ更新する。ADR-0033（0 セル許容）は不変（種は 1 セル）・menu badge「Untitled」も不変（遅延 scratch なので `_path=null` 維持）。
- **universe↔chart 不変条件は無改変**で honor される（トヨタ chart は既存 `SyncChartWindowsToUniverse` で spawn）。デフォルト universe は in-memory（sidecar 無し）で、Save As 時に既存 `scenario.instruments` writeback に自然に乗る（追加実装ゼロ）。
- **トヨタの replay 可否は DuckDB の `7203.TSE` データ有無に依存**（実行時）。データが無ければ replay は 0 bar で即終了するだけで crash しない（findings 0097 no-trade-day 経路）。種コード自体は `__file__` も相対 I/O も使わないので cwd に非依存。
- 下位の実装事実（種定数の置き場・ゲート述語の正確な配線・scratch パスの location と書き出し seam・probe 更新の正確な section）は本 ADR に書き戻さず **findings 0124** に記録し、本 ADR を「方針: ADR-0036」として参照する。
- **D 番号の対応表（cross-doc 注記）**: 本 ADR の Decision は **D1–D5**、findings 0124 の「設計の木」は **D1–D8**（D1=トリガ統一を別立てにするため 1 つズレる）。コード/台本コメントは findings 0124 の D 番号で「ADR-0036 DN」と表記しているので、ADR 単体を読む際は次の対応で解決すること: **ADR D1（観察ノート種付け）＝findings D2**／**ADR D2（placeholder 撤去）＝findings D3**／**ADR D3（universe トヨタ）＝findings D4**／**ADR D4（untitled Run 可ゲート）＝findings D5**／**ADR D5（遅延 scratch）＝findings D6**。よってソース中の「ADR-0036 D2＝種セル」「D4＝universe」「D6＝遅延 scratch」は findings 0124 番号での参照（正本は method/Covers・[[action-id-renumber-drift]] 同型）。

## 自己保護

本 ADR の decision は固定。覆す場合はこのファイルを編集せず、**本 ADR を supersede する新規 ADR** を起こす。supersede した findings 0050 / #76 の旧決定は履歴として残し、本 ADR が明示 supersede した facet（本体 seed 却下・空白 New・Save まで Run ブロック）を除き、ADR-0013 / ADR-0016 / ADR-0011 / ADR-0033 は別 decision の固定 oracle として踏襲し編集しない。下位の実装事実は findings 0124 に記録し、本 ADR を参照する。
