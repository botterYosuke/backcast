# findings 0079 — #102 Strategy Editor `print()` を per-cell console に出す + 出力領域の動的レイアウト（2 スライス）

方針: [ADR-0016](../adr/0016-notebook-equals-backtest-per-cell-run.md)（per-cell RUN を strategy 実行エントリーとし notebook = backtest に一本化）— 本 issue は ADR-0016 D11 / Phase 6（findings 0075）が「rich output（mimetype 契約）」までしか守備範囲に入れなかった結果、**console（stdout/stderr）が穴落ち**して `print('a')` がセル窓のどこにも出ない不具合の修正。**ADR は無改変**（D2/D4/D7 などの decision を一切覆さない）。

設計の参照点:
- Phase 6 設計の木: [findings 0075](./0075-issue95-phase6-run-state-ui-rich-output-sunset.md)（rich output `{mimetype, data}` 契約・S19 ＝ image/markdown/html/plain/unsupported routing）。
- marimo 正本: cell.ts L133 `collapseConsoleOutputs([...consoleOutputs, messageConsole])` / ConsoleOutput.tsx L172 `toReversed()` + `flex-col-reverse`（視覚上は元順） / collapseConsoleOutputs.tsx L10/L53 隣接同 channel の `text/plain` 結合 / Outputs.css L50 `.stderr { color: var(--amber-12); }`。

base ブランチ: `feat/#102`（`main` = `b174235`＝#100 fix の直後を起点）。

---

## 0. 何を出荷するか

1. **Slice 1（AFK・Python）**: per-cell の **stdout/stderr 捕捉**。`IncrementalNotebookSession._run` に `add_pre_execution` / `add_post_execution` hook を足し、各 cell の実行前後で `sys.stdout` / `sys.stderr` を per-cell な capture proxy に差し替え、書き込みを **arrival 順の順序付きセグメント列**として取る。`_backend_impl.run_cell` が `ran[i]` に `"console": [{stream, text}, ...]` を載せて C# へ素通し。`_format_output` の rich 経路は無改変（console は rich とは独立な並走 channel）。
2. **Slice 2（AFK・C#）**: セル窓の body を「**code editor（上・min 保証）+ rich output（中・自動・上限）+ console output（下・自動・上限）**」の 3 段に再構成。空ブロックは高さ0（DOM ベースで `gameObject.SetActive(false)`）で消え、内容超過は **ブロック内 ScrollRect**。stderr セグメントは **amber**。window 自体のサイズは layout 永続化を守るため不変。

---

## 1. 根本原因（2 層・コードで裏取り済み）

### L1（Python）— bare `Runner` 経路は marimo の console 配線を通らない

`IncrementalNotebookSession._run` (`notebook_session.py:438-459`) は marimo の bare `Runner` を直接駆動する設計。`Runner.run_all` は `_install_execution_context` を呼ぶが、`HeadlessKernel` は `stdout=None, stderr=None` を渡しているため、内部の `redirect_streams` (`marimo/_runtime/redirect_streams.py:72`) は **early return（no-op yield）** に分岐し、`sys.stdout` を一切いじらない:

```python
if stdout is None or stderr is None:
    try:
        yield
    finally:
        stream.cell_id = cell_id_old
    return
```

帰結: cell の `print('a')` は埋め込み Unity プロセスの素の `sys.stdout`＝ Editor.log / Unity console に漏れ、セル窓には届かない。`_format_output` は `RunResult.output is None` ＋ `accumulated_output is None`（`mo.output` 未公開）なので `("text/plain", "")` を返す＝空ペイロード。Phase 6 (#95) の `{mimetype, data}` 契約は **rich** を運ぶ専用 channel として設計され、**console は最初から守備範囲外**だった（findings 0075 §1 P6-2 は 5 mimetype に閉じている）。

### L2（C#）— 出力ペインが固定 26% を常時予約

`StrategyEditorContentBuilder.Build` (`StrategyEditorContentBuilder.cs:31`) は `OutputFrac = 0.26f` のハードコード比率で body 下部 26% を出力ペインに **常時** 予約する。`StrategyEditorView.SetOutput(null)` は Text/RawImage を **inactive** にするが、**RectTransform の anchor は body の下 26% を保持**したまま — その分の縦領域が編集領域から削られ、見た目には「コード下に常時無駄な空白」が残る。

帰結: たとえ Slice 1 で console を運べるようになっても、L2 のままだと「動的に縮む / 動的に伸びる」が成立しない。両層を同じ slice 群でまとめて直すのが妥当。

### 設計の方針が ADR を覆さないことの確認

- ADR-0016 D2/D4 = 単一 Run 撤去・per-cell RUN: 影響なし（本 issue は per-cell の出力経路の補強で、トリガー UX は不変）。
- ADR-0016 D7 = 走行中 #65 sink 経路: console は **#65 を通らない並走 channel**（cell 実行時の stdout だけ・走行スナップショットや fill には触れない）。新 sink ではない。
- ADR-0016 D8/D9 = 速度・GIL: console capture は cell の execute() スコープのみで stdin/stdout swap、worker thread 上で完結（marimo Kernel/RuntimeContext と同じスレッド = thread-local 規律を破らない）。
- ADR-0012 §3/§4 = marimo 依存 pin / lazy-import 規律: 本 slice は marimo 既存の `marimo._runtime.runner.hooks.Priority` を import するだけで追加依存ゼロ。`_backend_impl` は引き続き marimo-free（offline import-purity gate `test_strategy_runtime_offline` GREEN 維持）。
- findings 0075 P6-2 = rich output `{mimetype, data}` 契約: console は rich とは別 channel として追加。既存 rich 経路は一切無改変（5 mimetype routing / image decode HITL 降格 / `text_projection` interim 全て継承）。

---

## 2. owner-locked 下位決定

### D1 — console 表現は **順序付きセグメント列**（marimo 正本に忠実）

owner 決定（Q1・2026-06-21）: stdout と stderr を **arrival 順で混ぜたセグメント列**として運ぶ。`stdout: str` / `stderr: str` の 2 フィールド分離は採用しない — `print()` と `print(file=sys.stderr)` が交互に出た瞬間に時系列を失うため、issue #102 の「理想形」としては不採用。

**Python 側**: per-cell に `[{"stream": "stdout"|"stderr", "text": str}, ...]` を返す。 marimo `collapseConsoleOutputs` (cell.ts:133 / collapseConsoleOutputs.tsx:10,53) の挙動に倣い、**隣接する同 stream は結合**（capture proxy が走る順序で merge する＝1 cell 内では真の chronological 順を保ち、無駄な細断は出さない）。空セグメントは入れない。

**C# 側**: 配列順に `<color>` run を積む（stdout 通常色 / stderr amber）— 1 つの Text コンポーネントに `supportRichText = true` で `<color=#xxxxxx>...</color>` を挿入する単純な join で十分。改行はテキストに含まれるので追加加工は不要。

### D2 — 出力レイアウトは **VerticalLayoutGroup ＋ ContentSizeFitter** で auto-collapse / auto-grow

body subtree を以下の 3 ブロックに再構成:

```
[Body] (VerticalLayoutGroup, spacing = 4)
├── [CellInput] (LayoutElement: minHeight=editorMin, flexibleHeight=1)
├── [RichOutput] (LayoutElement: preferredHeight=measured, flexibleHeight=0)
│      └── [Viewport] (ScrollRect; content = Text + RawImage 兄弟・既存と同 anchor)
└── [ConsoleOutput] (LayoutElement: preferredHeight=measured, flexibleHeight=0)
       └── [Viewport] (ScrollRect; content = Console Text（supportRichText=true）)
```

- **editorMin** = `max(80, bodyHeight * 0.30)` — body が 200px しかない極端な縮小時でも 60px は確保し、それ以下は LayoutGroup の minHeight ルールで保護される。
- **per-block max** = `bodyHeight * 0.45` — rich + console が両方上限まで埋まっても editor min 30% は残る配置。marimo の `max-height:610px` は web pixel 絶対値（無限スクロール親を前提）だが、Unity 側は窓固定のため相対比に置き換える。
- **空ブロック**: `gameObject.SetActive(false)` で LayoutGroup から消える（spacing も消える）。`SetOutput(null,…)` / `SetConsole([])` が同じ縮退規律を保つ。
- **overflow**: 各ブロックは ScrollRect を内蔵し、Text の preferredHeight が `LayoutElement.preferredHeight` を超えるときだけ縦スクロールバーが現れる（ScrollRect は viewport ＞ content では何もしない）。window 全体は伸びない。
- **rich の image (RawImage) 経路**: 既存 `OutputIsImage` 不変条件を保つため、ScrollRect content には Text と RawImage を **兄弟**として置き、SetOutput の per-mimetype 分岐を継承。image のとき RawImage active + Text inactive、text のとき逆 — `OutputIsImage` の AFK assert（S19）は不変。
- **layout 永続化**: window 自体の rect は触らない（findings 0075 §1.5 / ADR-0017 hakoniwa docking は窓 anchor を `_windows.RectOf(region)` で永続化しており、subtree だけ再構成すれば永続化スキーマは壊れない）。

### D3 — capture の境界と clear セマンティクス

- **press ごとに clear**: `_run` 開始時に `_consoles` dict を空にする（findings 0075 §1 P6-1 の incremental graph 下で「stale ancestor も走る」runner と整合 — 走ったセルは clear+accumulate、走らなかった旧 console は **保持される**＝marimo の `clear_console=False on cells that ran` と同じ semantics）。
- **per-cell 帰属**: pre/post hook の `cell.cell_id` で確定。pressed cell の reactive descendant が `print` したら、その descendant 自身の console に積まれる（pressed cell のと混ざらない）。
- **複数 write は accumulate**: `StringIO`-like proxy が単一 buffer に append し、`getvalue()` で 1 セグメントにまとめる（adjacent-same-stream collapse の自然帰結）。
- **stderr↔stdout 切替**: 各 write call 時点でセグメントを切る。同 stream の連続 write は内部 buffer に積み、stream が変わる瞬間に既存 buffer を flush して新セグメントを開く。
- **例外時**: cell が raise したら post hook は **必ず**走る（marimo cell_runner.py:786 が `with execution_context: ...; for post_hook in ...` を try で囲んでいる）— restore は post の `EARLY` priority で保証。Runner 全体が crash した場合は `_run` の finally で defensive restore。
- **hook priority**:
  - `pre_execution`: 既定（NORMAL）。marimo 既存の pre hooks の **後** で swap すれば、他 hook が stdout に書いたものを cell の console に漏らさない（既存 pre hooks は stdout に書かない＝chunked 検証済みだが defense in depth）。
  - `post_execution`: `EARLY` (0)。marimo 既存の `attempt_pytest` 等が `sys.stdout.write` する経路があるため、capture を **最初に取り終えて restore** してから他 post hook に委ねる。

### D4 — Python→C# JSON 契約の追加

```json
{
  "ok": true,
  "ran": [
    {
      "index": 0,
      "mimetype": "text/plain",
      "data": "",
      "output": "",
      "console": [
        {"stream": "stdout", "text": "a\n"},
        {"stream": "stderr", "text": "boom\n"}
      ],
      "ok": true
    }
  ],
  "stale": [],
  "error": null
}
```

`console` が **無いとき** = legacy / 旧 paint で AFK fake が console を返さなかったとき。C# は `console == null || console.Length == 0` を console pane の auto-collapse 判定として使う（rich の `mimetype == ""` 判定と同じ規律）。

---

## 3. 実装サブ決定（owner 質問を使わず確定・明確なデフォルト）

- **AFK 既存 section との関係**: S19 までは rich output / image routing / mimetype 5 種 / glyph 状態 / document badge を網羅済み（findings 0075 §3b）。本 issue は **S20** を追加し、`Section20_ConsoleAndDynamicLayout` で 6 AC（console 表示・空で高さ0・editor 最低保証・rich + console 同時表示・overflow scroll・stderr amber + cell rebind 時の console クリア）を pin する。AFK の Section 命名規約（findings 0061）と Action ID `STRATEGY-34..38` を新規割当（5 つ程度）。
- **`_RichExecutor` / `_FakeCellExecutor` 拡張**: console を返せる新 fake `_ConsoleExecutor`（Set(stdoutText, stderrText, ...) ベース）を Section20 内に追加。既存 fake は引き続き console=null（legacy 互換）。
- **pytest gate**: `python/tests/test_notebook_console.py` を新規作成（7 ケース：plain print / multi print accumulate / stderr / mixed interleave / descendant attribution / press-clear / mo.output 共存）。`test_notebook_rich_output.py` は **無改変**（console と rich は別 channel）。
- **offline gate**: `test_strategy_runtime_offline.py` は `_backend_impl` の marimo-free を assert。`notebook_session._capture_console` も `_backend_impl` から lazy import のままなので変化なし。確認のため一度 run する。
- **golden gate**: per-cell run は #24 golden を経由しない（ADR-0006 / D5）— 本 issue は backtest 経路に一切触れず、`KernelRunner` も `bt` も触らない。byte-identical 不変条件は自動で保たれる。

---

## 4. done-gate

### Slice 1（Python）

1. `pytest python/tests/test_notebook_console.py` 7 ケース GREEN
2. `pytest python/tests/test_notebook_rich_output.py` 8 ケース GREEN（rich 不退行）
3. `pytest python/tests/test_strategy_runtime_offline.py` GREEN（lazy-import 規律維持）
4. `pytest python/tests` 全体 GREEN（#100 / Phase 5/6 既存全部不退行）

### Slice 2（C#）

5. Unity compile gate: `-batchmode -quit` ＋ `error CS\d+` 0 件
6. AFK S20 GREEN: `-executeMethod StrategyEditorNotebookE2ERunner.Run` PASS + `Found no leaked weakptrs`
7. AFK S1–S19 不退行 GREEN

### 共通

8. CONTEXT.md / E2E-INDEX.md 加筆（console mimetype / S20 セクション登録）
9. `code-review(simplify)` Medium+ 0（CLAUDE.md 必須・`/pair-relay` で潰す）
10. `post-impl-skill-update` 発動

---

## 5. 範囲外

- **stdin の埋め込み（`input()`）**: 別 issue（埋め込み環境では blocking read = Unity main を凍らせる）。
- **OS 級 stream redirect（`os.dup2`）**: `redirect_streams.py` の edit-mode path にあるが、C/C++ extension が直接 fd=1 へ書く稀ケース対応。本 issue は Python レベルの `sys.stdout` swap のみで AC を満たす。
- **console virtual file（`@file/...`）**: marimo の mo.image は `text/html` で `<img src="@file/...">` を出す（findings 0075 P6-2 注記）。これは rich の話で console には来ない。本 issue は console（stdout/stderr）のみ。

これらは ADR-0016 の方針下で別 issue / 将来 finding に委譲（ADR は無改変＝自己保護条項）。

---

## 6. 監査（E2E coverage audit & bug hunt・2026-06-21 second-pass）

Slice 1/2 landing 後の post-merge 監査。owner directive「実装コストは度外視して理想的な完成形を目指せ」を踏まえ、handoff（`handoff-102-e2e-coverage-audit.md`）が列挙した 9 件のバグ候補をコードで裏取りし、本 §6 で **D5–D10** として下位決定を凍結する。ADR-0016 は無改変。

### D5 — 鏡像 RectMask2D を捨て **本物の ScrollRect** へ昇格（Bug 5 = §0 で凍結済み D2 の実装乖離）

owner 決定（Q1・2026-06-21）: §2 D2 が指定する **ScrollRect-by-block** へ実装を整合させる。commit `b789480` の `RectMask2D` クリップは「読めない部分が隠れる」だけで、issue #102 AC「利用可能高を超えたらブロック内スクロール」を満たさない。これは設計の変更ではなく **D2 の実装履行**。

新サブツリー（`StrategyEditorContentBuilder.BuildOutputBlock`）:

```
[Block]   RectTransform, Image(透明), LayoutElement, ScrollRect
  ├── [Viewport]   RectTransform(Stretch), Image(マスク), RectMask2D
  │     └── [Content]   RectTransform(top-pivot), VerticalLayoutGroup, ContentSizeFitter(vertical=PreferredSize)
  │           ├── Text  (Stretch横; preferredHeight が VLG 経由で Content 高さに反映)
  │           └── RawImage  (Stretch横; LayoutElement.preferredHeight = texture.height・rich のみ)
  └── [VerticalScrollbar]   ScrollRect.verticalScrollbar wire
```

- **Block.LayoutElement.preferredHeight** = `min(Content.preferredHeight, body * OutputBlockMaxFractionOfBody)`。Content が cap 未満なら Block = Content 高さ（編集領域が残余を吸う）／ Content > cap なら Block = cap で Content が viewport を溢れる → ScrollRect が縦スクロール解決。
- **ScrollRect.verticalScrollbarVisibility = AutoHide**: cap 内に収まるときスクロールバーは出さず視覚クリーン。
- **rich block の Text↔RawImage 切替**: 非活性側 `SetActive(false)` で VerticalLayoutGroup から除外される（前後どちらが Content 高さを駆動するか曖昧にならない）。
- **既存 STRATEGY-37 cap assert は不変**: `LayoutElement.preferredHeight ≤ body * 0.45 + 1px` の式は ScrollRect 化後も保持（cap が viewport の上限になる）。
- **新 assert（STRATEGY-43）**: 大量行 payload（cap 超過確実）で `scrollRect.content.rect.height > scrollRect.viewport.rect.height` ＋ `verticalNormalizedPosition` 操作で値が動く（0↔1）ことを pin。実 mesh 行送りは HITL（findings 0010 §9 と同型）。

`StrategyEditorView` の `ApplyBlockSize` は ScrollRect 化後も「Content の preferredHeight を測って Block.LayoutElement.preferredHeight をクランプする」役割を保つ。ScrollRect 自体が content の高さを管理するため、ApplyBlockSize は viewport の cap を決めるだけ。

### D6 — `EscapeForUguiRichText` から **`&` の二重エスケープを削除**（Bug 1 = HIGH 確定）

owner 決定（暗黙・実コード読解で確定）: UGUI legacy Text は HTML entity を**デコードしない**ので、`s.Replace("&", "&amp;")` は user の `print("a & b")` を `a &amp; b` と**文字どおりに**画面に出す純粋なリグレッション。リテラル `&` がタグ trigger にはならない（タグ trigger は `<` のみ）。

修正方針:
- `EscapeForUguiRichText` は `s.Replace("<", "&lt;")` のみとする。`&` は無加工で通す。
- 帰結: user の `print("a & b")` は `a & b` と正しく見える。`print("&lt;")` のような文字列リテラルを書いたユーザーは `&lt;` を見ることになる（UGUI が decode しないため）が、これは「`<` を画面に出すには `<noparse>` か entity を見せる」UGUI legacy Text の根本制約で本 issue のスコープ外。将来 TextMeshPro に migrate するときに同時解消する。
- **新 assert（STRATEGY-39）**: `print("a & b")` 相当の segment を流し、`CurrentConsoleText` がリテラル `a & b` を含み `a &amp; b` を**含まない**ことを pin。

### D7 — `view.BoundCell` ガードで Bind 中の stale result drain を遮断（Bug 8 = LOW・defensive）

owner 決定（暗黙）: `NotebookRunController._generation` は notebook 置換時のみ bump（File→Open/New）。`AddCell`/`DeleteCell`（dormant region_001 reuse）では generation 不変だが、coordinator が `view.Bind(newCell)` を呼ぶことで view の bound cell が press 時点と乖離するレースが存在する。

修正方針:
- `NotebookRunController.ApplyResult` の per-ran ループに **`view.BoundCell == cells[co.Index]` 等値ガード**を追加。違っていたら skip（前回 press の出力が新 cell の窓に滲まない）。
- これは generation の補完で、generation 不要のときも cheap（参照等値比較）に走る。
- **新 assert（STRATEGY-46）**: 押下→drain 前に `view.Bind(otherCell)`→drain、で `SetConsole` が走らず（view が新 cell の状態を保持）を pin。

### D8 — Section21 を新設して 8 件の網羅 gap を埋める

Section20 は単一 `region_001` のみ駆動するため、`NotebookRunController.ApplyResult` の **multi-cell routing**・**re-press clear semantics**・**bodyH==0 first-frame race**・**injection-resistance**・**Bind race** が未網羅。新 `Section21_ConsoleAuditGaps` を増設し STRATEGY-39..46（**8 leaf**）を採番。

| Action ID | 目的 | 関連バグ候補 |
|---|---|---|
| STRATEGY-39 | `&` リテラル非二重エスケープ | Bug 1 |
| STRATEGY-40 | multi-cell routing（pressed R1 / descendant R2 / 互いに滲まない） | Bug 2 |
| STRATEGY-41 | 同 cell 再 press（非空→非空・前回 text が混ざらない＝置換セマンティクス） | Bug 3 |
| STRATEGY-42 | 同 cell 再 press（非空→空・ブロックは隠れる） | Bug 3 |
| STRATEGY-43 | overflow → ScrollRect が真に縦スクロール可能 | Bug 5 |
| STRATEGY-44 | bodyH==0 first-frame race（`ForceRebuildLayoutImmediate` をスキップした初期描画） | Bug 6 |
| STRATEGY-45 | `</color>` 注入耐性（amber wrapper を閉じない） | Bug 7 |
| STRATEGY-46 | Bind が in-flight press の stale drain を遮断 | Bug 8 |

**棄却**:
- **Bug 4（multi-print 蓄積）**: STRATEGY-34 の 3 segment `o1<e1<o2` 順序検査が既にこれを assert している。重複 pin はしない。
- **Bug 9（empty SetConsole で last-text 失う）**: view-level semantics の関心事で、AC でも findings 0076 でも `CurrentConsoleText` を「最後の paint を覚える」性質として要求していない。`ConsoleBlockVisible==false` を pin した既存 assert で十分。

### D9 — Bug 1 修正で削除する `&` ↔ E2E 既存 assert の互換確認

STRATEGY-34 line 1848 は `Contains("&lt;EOF")` を assert していて、`&` 削除と無関係（`<EOF>` → `&lt;EOF>` の鎖は維持）。STRATEGY-34 は不変、STRATEGY-39 が `&` リテラル経路を追加する。

### D10 — STRATEGY-39..46 採番 / Section21 命名 / E2E-INDEX 反映

- Action ID は STRATEGY-39..46（次の空き番号 ＝ 8 連番）。
- Section 名は `Section21_ConsoleAuditGaps`。
- `StrategyEditorNotebookE2ERunner.md` の操作一覧表に 8 行追記＋litmus 一覧 8 ブロック。
- `E2E-INDEX.md` line 20 の `STRATEGY-01..38` → `STRATEGY-01..46` ＋ 行数 38→46 ＋ 自動(E2E済) 34→42。
- line 43 の集計「12 本 ＝ 182 行 ／ 総計 232 行」→「12 本 ＝ 190 行 ／ 総計 240 行」。

### Done-gate（§4 の延長）

- `pytest python/tests/test_notebook_console.py` 7 GREEN（不退行）
- Unity AFK S20 不退行 ＋ **S21 GREEN**
- compile gate 0 CS error
- `code-review(simplify)` Medium+ 0
- `post-impl-skill-update` 発動
