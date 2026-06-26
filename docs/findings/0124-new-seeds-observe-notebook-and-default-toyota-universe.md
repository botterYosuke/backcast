# findings 0124 — New/初期化が観察ノート＋トヨタ universe を種付けし untitled でも Run 可

**方針: ADR-0036**（findings 0050 / #76 を supersede）。実装 issue: **#169**（S1–S3 節立て・着手可能）。本 finding は設計の木と codebase 裏取りを固定する slice 記録。

> 番号注記: issue 本文で別番号が名指されていた場合も本 finding は `ls docs/findings/ | sort` の次空き番号 0124 で採番。

## 要求（owner・2026-06-26）

1. New / 初期化 / 最初の Strategy Editor spawn 時に placeholder ウィンドウではなく、`bt.replay` 観察のみ（売買ゼロ）の marimo コードが種付けされた editor を spawn する。
2. New / 初期化時、universe にデフォルトでトヨタ（`7203.TSE`）が登録済みであること。
3. （grill で派生）起動直後から Run 可。

種コード（owner 指定・synth 出力形）:
```python
import marimo
app = marimo.App()

@app.cell
def _(bt):
    for bar in bt.replay(bars_per_second=2):
        pass          # 観察のみ。bt.submit_market() を呼ばない＝売買ゼロ
    return
```

## codebase 裏取り（grill F1–F5）

- **F1 `bt.replay(bars_per_second=2)` は現行 API。** `engine/strategy_runtime/backtester.py:288` `def replay(self, *, bars_per_second=None) -> Iterator[Bar]`。`bars_per_second` は stream 開始時キャプチャ・正値以外は `ValueError`。正本サンプル `python/strategies/v19/v19_morning_cell.py:122`。懸念だった「削除済み v19 API」ではない。
- **F2 placeholder は findings 0050 の slice 決定で、ADR-0013 本体には無い。** 機構: `NotebookCellCoordinator.HostApiHint`（const `get_bar()\nget_portfolio()\nsubmit_market(qty)`）/ `StrategyEditorContentBuilder` の Placeholder GameObject / `StrategyEditorView.SetPlaceholderHint` / `NotebookCellCoordinator.UpdatePlaceholders`（`CellCount == 1` 時のみ表示）。ADR-0013 末尾は「Slice record + spike evidence: findings 0050」とだけ書き、placeholder 決定を ADR 本体に持たない → ADR-0013 編集不要。
- **F3 セルが持つのは関数本体のみ。** `MarimoNotebookDocument.ResetUnboundEmpty()`（line ~189）が `_cells.Add(NewCell("", "_", "{}"))`。synth は `engine/strategy_runtime/cell_synthesis.py:34` `synthesize_json` → marimo `generate_filecontents` が `@app.cell / def _(refs): / return` を自動付与。`bt` は cell 内で未定義参照 → ref 検出され `def _(bt):` になる。よって種セルの本文 = 関数本体の 2 行のみ。
- **F4 トヨタ id = `7203.TSE`。** `ScenarioStartupTile.cs:87` のデフォルト UI 文言 `"9984.TSE, 7203.TSE"`、`InstrumentRegistry.cs:37` コメント「canonical e.g. "1301.TSE"」、`test_replay_instrument_picker_supply.py` が `"7203.TSE"` を使用。venue suffix 付きフル id（[[銘柄表示ラベル]] 規約と一致）。
- **F5 Run 実行系は既にバッファ synth。** `NotebookRunController.cs:117` `_coordinator.Notebook.SynthesizeLiveSource()`（ディスク非読込）。Run ブロックは**ゲートのみ**: `RunReadinessViewModel.Evaluate()` の `if (!strategyReady) return NoStrategy`、`strategyReady` の源 `MarimoNotebookDocument.TryGetStrategyFile` が `_path == null`（untitled）で false。`__file__`/cwd 用に保存パスを要求するのが理由（ADR-0011・PROPOSED）。

## 設計の木（D1–D8・ADR-0036 で凍結）

- **D1 トリガ統一**: New / no-resume boot / 最初の spawn は #76 で既に同一状態（`BackcastWorkspaceRoot.OpenFileNewDefault` / `DoFileNew` → `NotebookCellCoordinator.New()`）。1 seam で 3 トリガ全部に効く。
- **D2 セル本文の種**:
  ```
  for bar in bt.replay(bars_per_second=2):
      pass  # 観察のみ。bt.submit_market() を呼ばない＝売買ゼロ
  ```
  種定数を導入し、空セル生成箇所（`ResetUnboundEmpty` 由来の New 経路）で空文字の代わりに種文字列を入れる。`ResetUnboundEmpty` は他 caller 影響を grep 確認した上で、種は New 経路専用とする（pure document メソッドは無汚染に保ち、種注入は coordinator/workspace 層 or 種付き reset の別メソッドで）。
- **D3 placeholder 撤去**: F2 の機構（HostApiHint / Placeholder GameObject / SetPlaceholderHint / UpdatePlaceholders）を撤去。種セルは非空で元々非表示だが、撤去しないとセルを空にしたとき**種と異なる API** のヒントが蘇る。
- **D4 デフォルト universe = `7203.TSE`**: New/初期化の fresh 経路（`ScenarioStartupController.Clear` ＝ File→New、`PopulateFrom` の sidecar 無し else 枝 ＝ boot fresh）の `Universe.ReplaceAll(Array.Empty<string>())` を `ReplaceAll(new[]{"7203.TSE"})` に。`InstrumentRegistry.Changed → SyncChartWindowsToUniverse` でトヨタ chart も spawn（universe↔chart 不変条件 honor・追加制御なし）。**saved layout を復元する経路では種付けしない**（復元 doc は自前の universe を持つ）。
- **D5 untitled でも Run 可**: `strategyReady` を「named → 従来 `TryGetStrategyFile` / untitled → 非空セルあり」に拡張。Run ボタンは起動直後に有効（種セルが非空）。
- **D6 untitled Run は遅延 scratch（eager 不採用）**: untitled の Run 押下時に現バッファ synth を scratch `.py` へ atomic 書き出し（`AtomicPyFile` 流用可）→ それを `strategyPath` として実行（cwd = scratch dir＝ADR-0011 追従・§4 解消）。New 時点では scratch 生成せず `_path=null` を保ち、menu badge「Untitled」（CONTEXT L600）と未 Run 時のディスク非汚染を維持。Save As で実パスへ束ね直すと以降は named WYSIWYR（dirty ゲート復活）・scratch 破棄可。scratch location は実装時に確定（OS temp 配下の固定 backcast scratch dir を想定・to_csv 等の相対出力先になる点だけ留意）。
- **D7 永続化**: untitled 中は sidecar 無し・トヨタは `InstrumentRegistry` in-memory。Save As 時に既存 `scenario.instruments` writeback（Replay mode gate）に自然に乗る＝追加実装ゼロ。
- **D8 逆転対象 / ADR 不編集**: ①findings 0050「本体 seed 却下＝空セル＋placeholder」 ②#76「空白 New＋Save まで Run ブロック」を ADR-0036 が supersede。ADR-0013（placeholder は本体に無い）/ ADR-0016（per-cell run）/ ADR-0011（cwd・scratch で自然解決）/ ADR-0033（0 セル・種は 1 セルで不変）は参照のみ・無編集。

## 実装スライス（後続 behavior-to-e2e で RED-first 化）

- **S1**: デフォルト universe トヨタ種付け（D4）。chart 自動 spawn の AFK 確認。
- **S2**: セル本文の種付け＋placeholder 機構撤去（D2/D3）。synth 出力が owner 指定形と一致する golden。
- **S3**: untitled Run ゲート拡張＋遅延 scratch 実行（D5/D6）。起動直後 Run 可・編集後も Run 可・Save As で named 復帰の AFK。

## probe/test 影響（regression net）

- 更新: 「New→空セル 1 枚」「placeholder 表示」「`NoStrategy` で Run ブロック」を pin する assertion（`StrategyEditorProbe` placeholder 系・New 系 golden・`RunReadinessViewModel` テスト）。
- 不変: ADR-0033（0 セル削除可）・menu badge「Untitled」（`_path=null` 維持）・universe↔chart 同期（既存 `SyncChartWindowsToUniverse`）。
