# 0123 — marimo リッチ output サンプル notebook + 目視代替自動テスト（#165）

**状態**: 実装完了（2026-06-26）。著者: behavior-to-e2e + grill-with-docs（issue #165 の grill 済み設計を着地）。
**関連**: findings 0075（Phase 6 rich output `(mimetype,data)` 契約）/ findings 0071（per-cell RUN 土台）/
`test_notebook_rich_output.py`（合成 producer での seam gate）/ ADR-0012（marimo prod dep）。
ADR は不要（容易に可逆・自明・トレードオフ無し＝issue 明記）。

## 何をするか

marimo **特有のリッチ output**（`mo.md` / DataFrame テーブル / matplotlib チャート / `mo.ui` widget）を、
Unity Strategy Editor で **per-cell RUN** したときに各型が正しく描画されることを、**目視（HITL）に頼らず
自動テストで担保する**。そのための ① 出荷サンプル戦略 notebook と ② 目視を代替する 2 ゲートを揃える。

- ① `docs/samples/code/07_rich_output.py` — catalog の **per-cell RUN デモ**（既存 00–06 は全て backtest 戦略・
  リッチ output デモが 1 つも無かった穴を埋める）。4 セル（md / table / chart / ui）・bt 非依存・各セル自己完結。
- ② Python gate（`test_rich_output_sample.py`）＋ AFK gate（`StrategyEditorNotebookE2ERunner` Section31・STRATEGY-63/64）。

## 目視代替の射程（重要な制約）

headless `-batchmode -nographics` では **画素レンダ**（image の `Texture2D.LoadImage` GPU decode・TMP SDF mesh）は
**検証不能**。自動テストが担保するのは **①各セルが本物のリッチ payload を産出 ＋ ②それが正しい pane へ routing** の 2 点。
**実画素が出る**ことだけは HITL に残す（下記 §HITL）。

## 設計の木（凍結済み下位決定）

### D1 — 置き場とセル構成
- `docs/samples/code/07_rich_output.py`。セル: Markdown(`mo.md`) / テーブル(DataFrame) / チャート(matplotlib Agg) /
  `mo.ui.slider` の 4 セル。**各セルは `_`-prefix の cell-local import で自己完結**（`import marimo as _mo` 等＝
  `_` 始まりは marimo の cell ローカルなので multi-define 衝突を避ける）。各セルの**最後の式**がリッチ値。

### D2 — リッチ output の描画パイプラインは既存（gate 済み・再利用）
- Python: `notebook_session._format_output` が `(mimetype, data)` を産出（marimo `try_format` 経由・matplotlib は
  `_maybe_matplotlib_png` が自己完結 PNG data URL 化）。`_backend_impl.run_cell` が `output = text_projection(mimetype, data)`
  を補完して C# へ渡す。
- C#: `StrategyEditorView.SetOutput(output, mimetype, data)` が mimetype で分岐（image/png・image/jpeg→RawImage /
  text/markdown・text/html→`RichToUnity` rich-text / その他→`[mimetype]` labelled plain fallback）。
- 既存 AFK `Section19_RichOutput`（STRATEGY-32/33）は **合成 payload** で routing を型ジェネリックに gate 済み。
  本 finding が足すのは **実サンプルから採取した本物の marimo markup** での routing 確認（合成より強い）。

### D3 — セル別の決定性（実測 2026-06-26）と fixture freshness の射程
`IncrementalNotebookSession` で各セルを 2 回走らせて byte 一致を実測:

| セル | mimetype | byte 決定的か | fixture freshness |
|---|---|---|---|
| markdown | `text/markdown`（rendered HTML・`<strong>` 等） | ✔ | **byte 一致** |
| table | `text/html`（pandas `<table>`＋`<style>`） | ✔ | **byte 一致** |
| chart | `image/png`（`data:image/png;base64,…`） | この環境では ✔ だが matplotlib は **version/platform 依存** | **構造的**（valid PNG data URL のみ） |
| ui | `text/html`（`<marimo-slider>`） | ✘（`random-id='<UUID>'` が毎回変わる） | **random-id 正規化後に byte 一致** |

- chart を byte 一致にすると別 matplotlib/フォント/プラットフォームで脆くなるので**意図的に構造的**に留める
  （`data:image/png;base64,` 接頭＋PNG magic `89504e470d0a1a0a` のみ pin）。
- ui の唯一の非決定トークンは `random-id`（per-render UUID）。capture/test の `normalize_volatile` が
  `random-id='NORMALIZED'`（**山括弧なし**＝projection の tag-strip を壊さない）へ畳んでから比較・保存する。

### D4 — committed fixture は実サンプルから採取（合成しない）
`Assets/Tests/E2E/Editor/Fixtures/RichOutputSample.json` は `python -m tests.capture_rich_output_sample` が
**実サンプルを per-cell RUN して採取**（golden doctrine #24 と同じ「正本経路から記録」）。Python gate と AFK gate の
**両方が同一 fixture を pin**＝1 回の capture で両レッグが整合する。test は書かない（capture script だけが書く）。

## ゲートと RED→GREEN

### Python gate — `python/tests/test_rich_output_sample.py`（11 tests・GREEN）
- 実サンプルが valid な 4 セル marimo App であること。
- 各セルの `(mimetype, data)`＋中身＋`text_projection`（md 見出し / table 値 / chart `[image/png]` / ui 空）。
- committed fixture freshness（md/table/ui = 正規化後 byte 一致・chart = 構造的）。
- **delete-the-production-logic litmus**: `test_litmus_plainified_cell_loses_its_rich_mimetype` — セルをプレーン化
  （`mo.md` を外して bare 値に）すると `text/markdown` から `<pre>`-wrapped `text/html` バケットへ落ちる＝per-cell
  mimetype assert は本物の rich producer に依存している（非空虚）。サンプルをプレーン化すれば fixture の mimetype が
  変わり Python gate が RED になり、再 capture がここへ流れる。

### AFK gate — `StrategyEditorNotebookE2ERunner` Section31（STRATEGY-63/64）
fixture の各 payload を **実 `NotebookRunController` → `StrategyEditorView.SetOutput`** に流す（Section19 と同じ
`_RichExecutor`→controller 経路＝passthrough も同時に通る）:
- **STRATEGY-63**（md/table/ui）: markdown の `<strong>`→`<b>` / pandas `<table>`→pipe 行（実値 `7203.TSE` が生存）/
  `mo.ui.slider`→html rich-text fallback（image でない・静的テキスト無し＝interactive 境界を正直に）。
- **STRATEGY-64**（chart）: `image/png` data URL が image codepath へ routing。**decode 可能環境＝RawImage 活性 /
  headless batch＝`[image/png]` ラベルで mimetype 伝播を pin（GPU 画素 decode は HITL 降格・Section19 と同型）**。
- **RED litmus（AFK）**: 全 mimetype を Text へ collapse→chart の image routing 破綻（RawImage 非活性 / ラベル消失）で
  STRATEGY-64 RED。controller の Mimetype passthrough を外す→md/table が plain バケットへ（`<b>`/pipe 消失）で
  STRATEGY-63 RED。
- 成功マイルストンで `[E2E STRATEGY-63 PASS]` / `[E2E STRATEGY-64 PASS]` を吐き rollup に合流。

## 実装中に判明・修正した潜在バグ（`HtmlToUnity` の emphasis デッドコード）

issue の AFK 設計（S1）は「md payload が `<b>` rich-text へ変換され」を期待していたが、**実サンプルを流すと
`<b>` が出ず RED**（`S31/STRATEGY-63: real marimo markdown not rich-converted (no <b> from <strong>)`）。
真因＝`StrategyEditorView.HtmlToUnity` が `<strong>`→`<b>`・`<em>`→`<i>` に変換した**直後**、最終の汎用タグ
strip（`_tagRe = <[^>]+>`）が**変換済みの `<b>`/`<i>` も剥がしていた**＝emphasis 変換 4 行が**デッドコード**。

- 既存 `Section19`（STRATEGY-32）が露出させなかった理由: 合成 payload は **生 markdown**（`# Title\n**bold**`・タグ無し）
  なので `RichToUnity` が **`MarkdownToUnity` 経路**（最終 strip 無し）を取り `<b>` が生存していた。一方 **実 marimo の
  `mo.md` は rendered HTML を出す**ので `looksHtml=true`→`HtmlToUnity` 経路に入り、このバグを踏む。
- 修正: 最終 strip の正規表現を `<(?!/?[bi]>)[^>]+>` に変更＝**`<b>`/`</b>`/`<i>`/`</i>` 以外の**タグだけを剥がす
  （変換済み Unity emphasis を保護）。table（emphasis 無し）・mo.image/ui の html fallback（`<b>` 無し）には無影響、
  pipe 行も不変。`StrategyEditorView.cs` の `_tagRe` 定義 + コメントのみ（1 箇所）。
- これにより issue の AFK 設計どおり実サンプルの **markdown bold が実際に bold で描画される**（デモが本物のリッチに）。
  STRATEGY-63 の `<b>` assert が**この修正の delete-the-production-logic litmus**（strip を blanket に戻すと RED）。
- AFK 全 section 再走（Section19 含む 12 PASS・exit 0）で回帰ゼロを確認。

## HITL 残（自動化不能・理由付き）

- **実画素レンダ**: image の GPU decode→RawImage 表示（`Texture2D.LoadImage` は headless で decode 不可）・
  markdown/table の TMP SDF mesh の実描画。STRATEGY-18（Strategy Editor HITL）に cross-ref。
- AFK は「本物のリッチ payload 産出（Python）＋正しい pane への routing（C#）」までを決定論で pin、**実画素が出る**ことは
  owner HITL（per-cell RUN して md 見出し・テーブル・チャート画像・スライダーが見えること）。

## 再走手順

```
# Python gate
cd python && uv run pytest tests/test_rich_output_sample.py -v

# fixture の再採取（サンプルを編集したら）— レビュー対象の差分を確認してからコミット
cd python && python -m tests.capture_rich_output_sample

# AFK gate（Unity batchmode・recompile-skip に注意＝.cs 編集直後は 2 回目で実走）
pwsh scripts/run-live-e2e.ps1 -Method StrategyEditorNotebookE2ERunner.Run
#   expect: [E2E STRATEGY-63 PASS] / [E2E STRATEGY-64 PASS] / exit=0 / error CS\d+ 0 件
#   確認は Bash: grep -a "STRATEGY-6" <log>
```
