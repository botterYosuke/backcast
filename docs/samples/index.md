# サンプル戦略

すぐコピーして動かせる marimo 戦略を、簡単なものから順に並べました。各サンプルは
**実際に Replay エンジンで走らせて動作を確認済み**です（コード行右上のボタンでコピーできます）。

考え方の解説は [チュートリアル](../tutorial/index.md) にあります。ここは「動く完成形」の見本帳です。

!!! tip "使い方"
    1. 下のコードをコピーして `.py` として保存します。
    2. Strategy Editor で開き、Replay の期間・足種・銘柄・初期資金を設定します。
    3. Run で実行し、「確認ポイント」の挙動が出るか見ます。

---

## 1. 観察だけ（発注なし） { #observe }

バーを読むだけで一切発注しない、最小の戦略。まず「戦略がロードされ、毎バー呼ばれる」
ことを確認するのに使います。`get_bar()` を読む cell が 1 つでもあれば有効な戦略です。

```python title="00_observe.py"
--8<-- "samples/code/00_observe.py"
```

**確認ポイント**: 実行は完走し、注文は 0 件。

---

## 2. 閾値で売買する { #threshold }

`close` が上のバンドを超えたら買い、下のバンドを割ったら売る。`submit_market(qty)` の
**符号付き数量**（`+`=買い / `-`=売り / `0`=何もしない）を使う最小の発注例。

```python title="01_threshold.py"
--8<-- "samples/code/01_threshold.py"
```

**確認ポイント**: 価格がバンドを上下するたびに BUY と SELL の両方の約定が出る。

---

## 3. 目標ポジションへリバランス { #rebalance }

「いくら売買するか」ではなく「最終的に何株持ちたいか（目標）」を決め、現在ポジションとの
**差分だけ**発注します。`get_portfolio().position` は bar 入口時点（fill 前）の値なので、
先読みになりません。

```python title="02_rebalance.py"
--8<-- "samples/code/02_rebalance.py"
```

**確認ポイント**: 同じ目標が続く間は追加発注されず、目標が変わったバーだけ約定する。

---

## 4. 買付余力ゲート付きサイジング { #cash-gate }

買付余力（`buying_power`）が 1 株分を賄える間だけ 1 株ずつ買う。現金を使うほど余力が
縮むので、資金が尽きると自動的に発注が止まります。

```python title="03_cash_gate.py"
--8<-- "samples/code/03_cash_gate.py"
```

**確認ポイント**: 初期資金を小さくすると、数回 BUY したあと余力切れで止まる（毎バーは買わない）。

---

## 5. 移動平均（SMA）クロス { #sma }

`close` が現在を含む直近 N 本の平均を上回れば建玉、割れば手仕舞い。バーをまたいで終値履歴を覚える
ため、`mo.state` の**フィードバック状態**を使います（[チュートリアル 04](../tutorial/04-bar-contract.md) 参照）。

```python title="04_sma_cross.py"
--8<-- "samples/code/04_sma_cross.py"
```

**確認ポイント**: 価格が上昇トレンドに入ると建玉、下落に転じると手仕舞いの約定が出る。

!!! warning "フィードバック状態の落とし穴"
    戦略ロード時、エンジンは各 cell を **中立バー（`close=0`）で 1 度だけ試走**します。
    無条件に履歴へ積むとこの `0` が混入するため、`if bar.close > 0.0:` で実バーだけ
    積むのが安全です。

---

## 6. モメンタム { #momentum }

`lookback` 本前と比べて上昇していれば建玉、下落していれば手仕舞い。SMA と同じく
フィードバック状態で終値履歴を保持します。

```python title="05_momentum.py"
--8<-- "samples/code/05_momentum.py"
```

**確認ポイント**: 上昇局面で建玉し、下落局面で手仕舞う。

---

## 7. 複数銘柄を等しく持つ { #equal-weight }

バーは銘柄ごとに順番に流れてきます。`get_bar().instrument_id` で「いまどの銘柄か」を見て、
その銘柄の現在ポジション `net_qty(iid)` との差分を `submit_market(qty, instrument_id=iid)` で
発注します。

```python title="06_equal_weight.py"
--8<-- "samples/code/06_equal_weight.py"
```

**確認ポイント**: ユニバースの各銘柄に約定が出て、それぞれ目標株数で止まる。

---

## 8. リッチ output（per-cell RUN デモ） { #rich-output }

ここまでの 1–7 は **Replay エンジンで毎バー走らせる戦略**でしたが、これは毛色が違います。
marimo の **per-cell RUN**（セル右上の ▶ ボタン、または**フォーカス中のセルで Shift+Return** ―― Jupyter/marimo
流のショートカット。Ctrl+Return / Cmd+Return / テンキー Enter も同一。plain Return は改行のまま）は、バックテスト
（`get_bar` / Replay）とは別に **各セルを単独実行**して、Markdown・テーブル・チャート・UI ウィジェットといった
**リッチな表示**を確認するための経路です。このサンプルは `bt` 非依存で、4 種類の出力を 1 セルずつ示します。

```python title="07_rich_output.py"
--8<-- "samples/code/07_rich_output.py"
```

**確認ポイント**（各セルを ▶ で実行）:

- **Markdown** … 見出し・太字・箇条書きが整形されて出る（`mo.md`）。
- **テーブル** … pandas DataFrame が表として出る。
- **チャート** … matplotlib の図が**画像**として出る。
- **UI** … `mo.ui.slider` が出る（ただし Strategy Editor では**静的表示**＝操作はできない。
  interactive 操作は marimo web 版の境界）。

!!! note "これは戦略ではありません"
    このサンプルは Replay で走らせる戦略ではなく、**per-cell RUN の表示デモ**です。
    Replay の期間設定や発注は関係ありません。各セルの ▶ を押して、その型が描画されるかを見ます。

!!! warning "実画素の確認は目視で"
    各セルが**本物のリッチ payload を産出し、正しい表示 pane へ振り分けられる**ことは自動テストで
    担保しています（`test_rich_output_sample.py` ＋ AFK `StrategyEditorNotebookE2ERunner` STRATEGY-63/64・
    findings 0123）。ただし**画像が実際に画素として描かれる**ことだけは headless 環境で検証できないため、
    最終確認は目視（HITL）です。

---

## 付録: imperative 形式 { #imperative }

新規作成は marimo 形式を推奨しますが、`Strategy` サブクラス（命令型）も使えます。同じ
閾値ルールを imperative で書くとこうなります（`OrderSide` と正の数量で発注）。詳しくは
[チュートリアル付録](../tutorial/99-imperative.md)。

```python title="99_imperative.py"
--8<-- "samples/code/99_imperative.py"
```
