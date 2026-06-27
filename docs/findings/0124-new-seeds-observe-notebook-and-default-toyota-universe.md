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
- **F5 Run 実行系は既にバッファ synth。** `NotebookRunController.cs:117` `_coordinator.Notebook.SynthesizeLiveSource()`（ディスク非読込）。

## 実装裏取り訂正（grill F6–F8・2026-06-26・slice 記録なので in-place 修正）

> ADR-0036 の **決定 D1–D6 は不変**。以下は findings 初稿（issue 本文ベース）が実コードと食い違っていた**実装事実の訂正**で、ADR の decision を覆すものではない（ADR 本体は無編集／本 finding が seam 正本・ADR-0036 Consequences が「ゲート述語の正確な配線・scratch location は findings 0124」と明示委譲）。

- **F6（F5 を訂正）live untitled Run を実際にブロックしているゲートは無い。** 初稿 F5 が挙げた `RunReadinessViewModel.Evaluate()` は **production で呼ばれていない死にコード**、`ScenarioStartupController.TryStartRun(provider)`（`NoStrategy` 文言を使う）も **probe/E2E 専用**（`BackcastWorkspaceProbe` / `AuthorToRunJourneyE2ERunner` / `ScenarioStartupE2ERunner` のみ）で、`BackcastWorkspaceRoot` の本番経路から呼ばれない（`OnRun` は #95 Phase 6 で sunset）。**live の per-cell ▶ Run** は `BackcastWorkspaceRoot.WireCellRunButton`（L1092/1126）で `if (_isOwner && _host.ServerReady)` だけを見て `RunCell` を呼び、`RunCell`（`NotebookRunController.cs:98`）は保存パスを要求せず `strategyPath==null` を許容する（backend `_backend_impl.py:805` `if strategy_path:` ＝ null なら `__file__` を据え置くだけ）。**∴「起動直後 Run 可」は S1+S2 が届ける**：S1 で universe にトヨタ→`BuildNotebookScenarioJson`（L1150）が非 null→`bt.replay` が実 bt を build、S2 でセル本文が非空。今 untitled が観察 run を駆動できないのは F7 の通り universe が空で `BuildNotebookScenarioJson==null`→`NoScenarioBacktester`（fail-closed）になるからで、ゲート述語のせいではない。
- **F7（F5 の理由を訂正）untitled で universe が空になる経路。** unbound editor では `SeedScenarioFromEditor`（L360）の `EditorFileProvider.TryGetStrategyFile` が false→**何も seed しない**（`#78 未ロード→走らない`）→universe 空。S1 がこの空 universe をトヨタで満たすことで replay が駆動する。
- **F8（D4 の seam を訂正）fresh 種付け箇所は 2 つで、`PopulateFrom(null)` は対象外。** 初稿 D4 が挙げた「`PopulateFrom` の sidecar 無し else 枝＝boot fresh」は**誤り**：`PopulateFrom(ScenarioStartupController.cs:59)` の null 枝は `SeedScenarioFromEditor`（**bound** editor の破損 sidecar＋inline 不能 ＝ findings 0051 D3 劣化経路）から到達する経路で、boot fresh ではない。ここにトヨタを注ぐと「Open/復元には種付けしない」を破る。**真の fresh 経路は ①`DoFileNew`（L2416）の `_scenario.Clear()`（ScenarioStartupController.cs:90）＝File→New、②`OpenFileNewDefault`（L2708）＝no-resume boot** の 2 つだけ（どちらも文書を持たない）。restore 経路（`Boot` L2688 `_coordinator.Open`＋`ReseedFromEditor`）と corrupt-Open（`PopulateFrom(null)`）は無種のまま。
- **F9（D6 の cwd 注記）ADR-0011 の `os.chdir`（cwd=戦略dir）は今も proposed/未実装（#79）。** run 経路は `__file__` のみ設定（findings 0089）。scratch `.py` の主目的は untitled に実体ある `__file__`/path を与えること。完全な cwd=scratch-dir は #79 着地時。種コードは `__file__`/相対 I/O とも不使用なので実害ゼロ（ADR-0036 Consequences も明記）。

## 設計の木（D1–D8・ADR-0036 で凍結）

- **D1 トリガ統一**: New / no-resume boot / 最初の spawn は #76 で既に同一状態（`BackcastWorkspaceRoot.OpenFileNewDefault` / `DoFileNew` → `NotebookCellCoordinator.New()`）。1 seam で 3 トリガ全部に効く。
- **D2 セル本文の種**:
  ```
  for bar in bt.replay(bars_per_second=2):
      pass  # 観察のみ。bt.submit_market() を呼ばない＝売買ゼロ
  ```
  種定数を導入し、空セル生成箇所（`ResetUnboundEmpty` 由来の New 経路）で空文字の代わりに種文字列を入れる。`ResetUnboundEmpty` は他 caller 影響を grep 確認した上で、種は New 経路専用とする（pure document メソッドは無汚染に保ち、種注入は coordinator/workspace 層 or 種付き reset の別メソッドで）。
- **D3 placeholder 撤去**: F2 の機構（HostApiHint / Placeholder GameObject / SetPlaceholderHint / UpdatePlaceholders）を撤去。種セルは非空で元々非表示だが、撤去しないとセルを空にしたとき**種と異なる API** のヒントが蘇る。
- **D4 デフォルト universe = `7203.TSE`**（F8 訂正済 seam）: fresh 経路 **①`DoFileNew` の `_scenario.Clear()`（File→New）②`OpenFileNewDefault`（no-resume boot）** の 2 箇所でトヨタを seed する。`Clear()` は文書を持たない真の fresh なので `Universe.ReplaceAll(Array.Empty<string>())`→トヨタ seed を直接入れてよい（または coordinator/root 層で seed）。`OpenFileNewDefault` は `_coordinator.New()`＋`ReseedFromEditor`（unbound→無 seed）なので、ここで明示的にトヨタを registry へ。`InstrumentRegistry.Changed → SyncChartWindowsToUniverse` でトヨタ chart も spawn（universe↔chart 不変条件 honor・追加制御なし）。**`PopulateFrom(null)`（破損 sidecar Open の劣化）と restore 経路（`_coordinator.Open`）には種付けしない**（自前 universe を honor）。
- **D5 untitled でも Run 可**（F6 訂正：契約/probe レベル）: live per-cell ▶ は元々ゲート述語を読まない（F6）。本 D5 は**文書化された WYSIWYR 契約と regression probe を正直に保つ**ため、`RunReadinessViewModel.Reason`/`ScenarioStartupController.TryStartRun` の `strategyReady` を「named → 従来 `TryGetStrategyFile` / untitled → 非空セルあり」に拡張する（dirty named は従来どおりブロック）。live の「起動直後 Run 可」は S1+S2 が満たす（F6）。
- **D6 untitled Run は遅延 scratch（eager 不採用）**: seam = `BuildNotebookStrategyPath`（L1173・`_strategyPathProvider`）。現状 untitled は `EditorFileProvider.TryGetStrategyFile`→null を返す。これを「named → 従来 path / untitled かつ非空セル → 現バッファ synth を scratch `.py` へ atomic 書き出し（`AtomicPyFile.Write`）し、その path を返す」に拡張。`RunCell` は毎押下で `_strategyPathProvider()` を呼ぶので「バッファ synth を毎回 scratch へ反映」が自然に満たされる。New 時点では `BuildNotebookStrategyPath` を呼ばないので scratch 非生成・`_path=null`・menu badge「Untitled」（CONTEXT L600）・ディスク非汚染を維持。Save As で実パスへ束ね直すと以降 named WYSIWYR（dirty ゲート復活）・scratch 破棄可。scratch location = OS temp 配下の固定 backcast scratch dir。**cwd 注記（F9）**: 渡した path は backend で `__file__` に入る（findings 0089）。完全な cwd=scratch-dir（`os.chdir`）は ADR-0011 §1-2 proposed/#79 待ちで本 slice では実装しない。種コードは `__file__`/相対 I/O 不使用なので実害なし。
- **D7 永続化**: untitled 中は sidecar 無し・トヨタは `InstrumentRegistry` in-memory。Save As 時に既存 `scenario.instruments` writeback（Replay mode gate）に自然に乗る＝追加実装ゼロ。
- **D8 逆転対象 / ADR 不編集**: ①findings 0050「本体 seed 却下＝空セル＋placeholder」 ②#76「空白 New＋Save まで Run ブロック」を ADR-0036 が supersede。ADR-0013（placeholder は本体に無い）/ ADR-0016（per-cell run）/ ADR-0011（cwd・scratch で自然解決）/ ADR-0033（0 セル・種は 1 セルで不変）は参照のみ・無編集。

## 実装スライス（後続 behavior-to-e2e で RED-first 化）

- **S1**: デフォルト universe トヨタ種付け（D4）。chart 自動 spawn の AFK 確認。
- **S2**: セル本文の種付け＋placeholder 機構撤去（D2/D3）。synth 出力が owner 指定形と一致する golden。
- **S3**: untitled Run ゲート拡張＋遅延 scratch 実行（D5/D6）。起動直後 Run 可・編集後も Run 可・Save As で named 復帰の AFK。

## probe/test 影響（regression net）

- 更新: 「New→空セル 1 枚」「placeholder 表示」「`NoStrategy` で Run ブロック」を pin する assertion（`StrategyEditorProbe` placeholder 系・New 系 golden・`RunReadinessViewModel` テスト）。
- 不変: ADR-0033（0 セル削除可）・menu badge「Untitled」（`_path=null` 維持）・universe↔chart 同期（既存 `SyncChartWindowsToUniverse`）。

## 実装着地（2026-06-26）

**production（C#）**:
- `NotebookCellCoordinator.ObserveSeedBody`（const・末尾改行なし＝golden 冪等）＋ `New(string seedBody="")`（既定空＝AFK の coordinator 直駆動は不変／root が seed を渡す）。`MarimoNotebookDocument.ResetUnboundSeeded(body)`（`ResetUnboundEmpty` は `=""` 委譲＝pure document・S10 line 890 不変）。`HostApiHint`/`UpdatePlaceholders` 撤去（D3）。
- `StrategyEditorView._placeholder`/`SetPlaceholderHint`/`StrategyEditorContentBuilder` の Placeholder GO・`input.placeholder` 撤去（D3）。
- `BackcastWorkspaceRoot`: `DoFileNew`/`OpenFileNewDefault` で `_coordinator.New(ObserveSeedBody)`＋`SeedFreshDocumentDefaults()`（→`ScenarioStartupController.SeedFreshDefaults`・const `DefaultSeedInstrument="7203.TSE"`→`Universe.ReplaceAll`・F8 の 2 fresh seam だけ）。`BuildNotebookStrategyPath` を named WYSIWYR → untitled 遅延 scratch（`MarimoNotebookDocument.HasNonEmptyCell` ＋ `TryGetUntitledScratch(UntitledScratchPath())`）へ拡張（D5/D6）。

> 訂正注記（2026-06-26 post-review）: 上記実名は **`SeedFreshDocumentDefaults`**（root の seed wrapper）/ **`TryGetUntitledScratch`**（document の run-file 集約）。code-review で導入された統合後の名前で、初稿の `SeedDefaultUniverse` / `TryWriteScratch` は実装に存在しない（grep 確認）。`.md` litmus も同名へ訂正済み。
- `ScenarioStartupController.TryStartRun(IStrategyFileProvider)` → `TryStartRun(Func<string> resolver)`（gate を altitude-agnostic に・production 呼び出し元は無し＝probe のみ更新／resolver = `BuildNotebookStrategyPath`）。

**gate（RED→GREEN）**:
- AFK `NewSeedsObserveNotebookE2ERunner`（実 root 反射・Python-FREE・OpenScene 1 回）= **NEWSEED-01..09**（05 除く 8 section）。`run-live-e2e.ps1 -Method ...Run` で **8 PASS ＋ SLICE PASS / exit 0**。litmus は各 section の delete-the-production-logic を `.md` に明記。RED 実証: 初版で NEWSEED-04 が root の tile-sync dirty で `PopulateFrom` early-return → fresh `ScenarioStartupController` 直駆動へ修正し GREEN。
- pytest `test_marimo_cell_synthesis_golden.py`（実 marimo）= **NEWSEED-05**（観察セル synth が owner 指定形と byte 一致〔version masked〕＋ round-trip 冪等）。RED 実証: 種本文に末尾改行を残すと synth に `return` 前の空行が入り round-trip 非冪等で RED → 末尾改行を落として GREEN（const も同形）。
- 既存 regression net 更新で GREEN 維持: `StrategyEditorNotebookE2ERunner` Section20（placeholder hint → **RETIRED** 撤去 pin・STRATEGY-11）、`AuthorToRunJourneyE2ERunner` JOURNEY-AUTHOR-02（種付き New へ・SECOND を 7203 衝突回避で 6758 に）＋ `TryStartRun(Resolve(provider))`、`ScenarioStartupE2ERunner`/`BackcastWorkspaceProbe`（gate resolver 化）。全 4 runner ＋ pytest 663 passed/1 skipped GREEN。
- **en-passant**: `AuthorToRunJourneyE2ERunner.ComposeRoot` が 2 回 compose する 2nd-OpenScene で `ThemeService.Changed` の stale handler（destroyed 1st-root の `ApplyViewportTheme`→`SettingsModeSegmentView`）が `MissingReferenceException`（memory `e2e-single-openscene-per-runner` の hazard）→ compose 冒頭で `ThemeService.ResetForTests()`（domain-reload の無い batchmode 用に既設）を呼んで解消。本番は per-Play domain reload で隔離＝production bug ではない test-harness fix。

## code-review（high）反映（2026-06-26・Medium+ ゼロまで）

- **[HIGH] fresh New の scenario params が空で run が壊れる＋Save As で universe 消滅**（1st pass review）: 初版は universe だけ seed し params（dates/granularity/cash）が空のままだった。`bt.replay` 種セルを ▶ すると `load_replay_data(start="",end="")` が `RuntimeError`（0 bar ではなく error）、かつ最初の Save As で `Commit` が無効 scenario で失敗→sidecar に scenario 不書き→post-save `ReseedFromEditor`→`PopulateFrom(null)` が universe を wipe。ADR の「すぐ Run 可能」「Save As rides the writeback」自体が成立していなかった。**fix**: `ScenarioStartupController.SeedFreshDefaults(today, instruments)` を新設し fresh seam で **valid な default params（`SeedDefaults` は Granularity=None を返すので `Daily` を field-set で補完・Dirty 不変）＋ universe** を一括 seed。`DoFileNew`/`OpenFileNewDefault` は `SeedFreshDocumentDefaults()`（共有）に統一（旧 `Clear()`＋`SeedDefaultUniverse()` を置換・seed pair の二重定義も解消）。
- **[MED] 2nd-round: `SeedDefaults` の Granularity=None で依然 invalid**（2nd pass review）: 上記の `SeedDefaults` だけでは granularity 未設定で Validate 失敗。`Params.Granularity = GranularityChoice.Daily` を明示補完して valid 化。NEWSEED-01/03 に `scenario.Validate().Any==false`、NEWSEED-09 に「Save As 後 universe==[7203.TSE]」を追加して**この 2 つの masked bug をゲート化**（litmus: Granularity を None に戻すと両方 RED）。
- **[MED] no-resume boot で tile が blank**（2nd pass）: `OpenFileNewDefault` は `ReseedFromEditor`（内部 tile-sync は seed 前）後に seed するため、tile が seed 済 params を映さず blank（findings 0025 §12 の WYSIWYR 破れ）。`SeedFreshDocumentDefaults()` 後に `_tile?.SyncFieldsFromController()` を追加（DoFileNew と鏡像）。`SyncFieldsFromController` は `SetTextWithoutNotify` なので spurious dirty なし。
- **[MED] altitude: run-file 判定が root/document に分散**（1st pass）: `BuildNotebookStrategyPath` の untitled 分岐ロジック（IsBound/HasNonEmptyCell/scratch）を `MarimoNotebookDocument.TryGetUntitledScratch(scratchPath, out path)` に集約（document が supplyable 5-condition も所有しているので run-file 決定は document に置くのが正）。`Synthesize→AtomicPyFile.Write` 重複は private `WriteSynthesized(path)` に括り出し Save/SaveAs/scratch で共有。
- **[MED] `StrategyEditorZoomCrispnessE2ERunner` ZOOM-02/03 が placeholder 撤去で RED**（cross-file tracer）: placeholder assertion を撤去し ZOOM-03 の TMP surface 数を 4→3 に修正。
- doc staleness（`ScenarioStartupController`/`StrategyEditorContentBuilder` ヘッダの provider/placeholder 記述）も更新。**3rd pass review = `[]`（収束）**。全 5 Unity runner＋pytest GREEN を再確認。
