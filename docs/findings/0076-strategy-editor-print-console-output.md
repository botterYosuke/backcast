# 0076 — Strategy Editor の `print()` / console 出力（stdout/stderr 捕捉 + 出力領域の動的レイアウト）

方針: **ADR-0016**（notebook = backtest / per-cell RUN）の下位実装事実。ADR は無改変（自己保護条項）。
前提スライス: findings **0071**（per-cell run 土台）, **0075-issue95-phase6**（rich output = mimetype/data）。

> ⚠️ findings 番号 `0075` は既存 2 ファイル（`0075-hakoniwa-docking-floating-windows.md` /
> `0075-issue95-phase6-...`）で重複採番済み。本スライスは衝突回避で **0076** を採番。

## 症状（owner 報告 2026-06-21）

Strategy Editor のセルに `print('a')` と入力し ▶ を押しても **どこにも何も表示されない**。

## 根本原因（コードで裏取り済み）

`print('a')` は戻り値を持たないため marimo の `RunResult.output is None`（`marimo/_runtime/runner/cell_runner.py:86`）。
`mo.output` も使っていないので `accumulated_output` も None。よって
`notebook_session.py:103 _format_output` が `("text/plain", "")`（空）を返し、
`text_projection` → 空 → セル窓に何も出ない。

`print` の文字列は **stdout** へ流れるが、埋め込み経路は bare `Runner.run_all()` を直接駆動しており
（`IncrementalNotebookSession._run` `notebook_session.py:438-459`）、marimo フル runtime の
`redirect_streams`（`marimo/_runtime/runtime.py:781`）を通らない。よって stdout は素の `sys.stdout`
（Unity コンソール / Editor.log）へ漏れ、ストラテジエディタ窓には届かない。

= Phase 6（findings 0075）の rich-output 設計が「最後の式の値 / `mo.output` / 画像」だけを対象にし、
**console（stdout/stderr）を最初から守備範囲外にしていた**ことの帰結。意図的に却下した決定は docs に無い（grep 済み）。

## 確認した marimo の正本仕様（`/Users/sasac/marimo/frontend`）

owner 要望「marimo と同じように出して」に対し実装を直接確認:

| 領域 | 空のとき | 中身があるとき | スクロール | 出典 |
|---|---|---|---|---|
| **rich output**（`OutputArea`） | `data===""`/null で `null` 返し＝**DOM に出ない（高さ0）** | **中身に高さ追従**、`max-height:610px` で頭打ち | 610px 超で `overflow:auto` | `Output.tsx:368-372` / `Cell.css:93-97` |
| **console**（`ConsoleOutput`） | 中身なしで実質非表示 | **中身に高さ追従**、`max-height:610px` で頭打ち | 610px 超で `overflow:auto` | `ConsoleOutput.tsx` / `Cell.css:93-97` |

- marimo は **rich と console を同じ規則**で扱う：「中身に高さ追従 → 上限で頭打ち → 超えたらスクロール → 空は非表示」。
- 配置: **rich output が上・console が下**（`notebook-cell.tsx`）。stderr は amber（`Outputs.css:39-52 .stderr{color:var(--amber-12)}`）。
- 両方に展開トグル（cap 解除）あり。console は実行開始でクリア・実行中に蓄積・stdout/stderr は順序を保って interleave。
- **両方あれば両方表示**（either/or ではない）。

## 設計の木（owner HITL で確定）

### D1. 捕捉スコープ — stdout **と** stderr の両方
marimo 同様。pre/post execution hook（`add_pre_execution` 確認済み `hooks.py:108`）で
cell 実行の前後に `sys.stdout`/`sys.stderr` をセル単位の `StringIO` へ差し替え→読み戻す。
複数 cell（pressed + reactive descendants）が走るため **per-cell 帰属**にする。

### D2. 表示位置 — rich output（上）+ console（下）の **別ブロック**（marimo 配置に一致）
owner: 「console: ＋スクロール / rich output: 出力に高さを合わせる」。rich と console で
サイズ規則を別々に効かせたいので別ブロックが素直（A=単一ペイン混在は不採用）。

### D3. サイズ / レイアウト（固定 floating window 内）
backcast の cell は **サイズが layout 保存される固定 floating window**（ADR-0013 / findings 0050）。
marimo のようにページ全体は伸びない → 「中身に追従」には window 内の利用可能高が上限。

- **空 → 高さ0・非表示**（rich・console とも。owner 明言「出力が何も無い時は高さゼロ＝非表示」）。
  現状は `OutputFrac=0.26` で **空でも下部 26% を常時予約**（`StrategyEditorContentBuilder.cs:31,36,78`）。
  `SetOutput` は空で Text を `SetActive(false)` するが **枠は予約されたまま**＝コード下に空白が残る。これを是正。
- **code エディタは最低保証高**（owner 明言「code は最低保証」）。
- **rich output**: 中身に高さ追従、利用可能高で頭打ち→超過でスクロール。
- **console**: 中身に高さ追従、上限つき→超過でスクロール。
- window 自体は不変（layout 保存 model を壊さない）。出力は code の最低保証を引いた残りを rich→console の順で使う。

### D4. stderr スタイル
marimo parity の **amber 色**を target とする（`supportRichText` の color タグで安価）。
interim で plain でも可。stdout/stderr の厳密な interleave 再現は follow-up（interim は
stdout 塊・stderr 塊で可）。

### D5. クロス言語コントラクト
per-cell 結果 dict（`notebook_session.py:473` `{cell_id, mimetype, data, ok}`）に **console を追加**。
形は `stdout` / `stderr`（蓄積済み文字列）を持たせ、C# が console ブロックを描画。
`_backend_impl.py:1016-1026`（`text_projection` 経路）も console を素通しするよう拡張。

### D6. 蓄積 / 前提
console は **press ごとにクリア**し実行中に蓄積（marimo 準拠）。rich と console は**両方あれば両方表示**。
rebind（別 cell バインド）で console もクリア（`StrategyEditorView.Bind` の `SetOutput(null)` 同様）。

## ゲート（実装は behavior-to-e2e + tdd で RED から）

- **Python（pytest）**: `print('a')` → console に `a` が出る／rich output は空のまま。
  `print` + 戻り値の両立。stderr 捕捉。複数 print 蓄積。reactive descendant の per-cell 帰属。
  既存 `python/tests/test_notebook_rich_output.py` 隣に追加（print テスト欠落を補完）。
- **C# AFK probe / E2E**: 既存 `Assets/Tests/E2E/Editor/StrategyEditorNotebookE2ERunner.cs` を拡張。
  console ブロックに print 文字列が出る／空 cell は出力領域 **高さ0**／code 最低保証高／出力時に rich・console が現れる。

## 影響ファイル（実装時の見取り図）

- `python/engine/strategy_runtime/notebook_session.py` — pre/post hook で stdout/stderr 捕捉、`_run` の ran dict に console 追加。
- `python/engine/_backend_impl.py` — console を C# へ素通し（`text_projection` 経路の拡張）。
- `Assets/Scripts/StrategyEditor/StrategyEditorContentBuilder.cs` — 出力領域を固定 26% から動的レイアウトへ（code 最低保証・空で高さ0・console ブロック新設）。
- `Assets/Scripts/StrategyEditor/StrategyEditorView.cs` — `SetOutput` に console 引数追加、ブロック表示制御。
- gate: `python/tests/test_notebook_console*.py`（新）, `StrategyEditorNotebookE2ERunner.cs`（拡張）。
