# 04 先読み禁止とバー契約

戦略を正しく書くうえで一番大事な「いつ何が起きるか」の約束ごとです。

## 1 バーの流れ

各バーで Backcast は次の順に処理します。

1. そのバーの `get_bar()` / `get_portfolio()` を用意する（**fill 前**の状態）。
2. cell-DAG を 1 回走らせる（あなたの `submit_market` がここで積まれる）。
3. 積まれた成行注文を **そのバーの終値で約定**させる。
4. 約定を反映して次のバーへ。

## 先読み禁止（no look-ahead）

`get_portfolio()` のスナップショットは **このバーの約定が起きる前**＝前のバー終了時点の
ポジションです。だから「目標 − 現在ポジション」で安全にサイジングできます。約定後の値が
見えてしまうと、自分の注文結果を先読みして発注することになり、検証が破綻します。

!!! note "fill 前で固定される"
    同じバーの中で何度 `get_portfolio()` を読んでも値は変わりません。スナップショットは
    バー入口で凍結されます。

## バーをまたいで状態を持つ

cell は毎バー走り直すので、ローカル変数はバーをまたいで残りません。終値履歴や旗のような
**バー越しの状態**は `mo.state` で持ちます。状態を作る cell が `get_bar()` を読まなければ、
その cell は毎バー再実行されず、状態は走行中ずっと保持されます。

```python
import marimo

app = marimo.App()


@app.cell
def _state():
    import marimo as mo

    closes_get, closes_set = mo.state([])  # バー越しに残る履歴
    return closes_get, closes_set


@app.cell
def _use(closes_get, closes_set):
    bar = get_bar()
    if bar.close > 0.0:
        closes_set((closes_get() + [bar.close])[-20:])  # 直近 20 本を保持
    return ()
```

完成形（コピー用）: [`04_sma_cross.py`](../samples/index.md#sma) / [`05_momentum.py`](../samples/index.md#momentum)

!!! warning "ロード時の中立バーに注意"
    戦略をロードするとき、エンジンは各 cell を **中立バー（`close=0`）で 1 度だけ試走**して
    依存関係を解析します。履歴へ無条件に積むとこの `0` が混入します。`if bar.close > 0.0:` の
    ように実バーだけ積むのが安全です（同じ理由で「何本目か」を数えるカウンタも 1 ずれます）。

## 確認

`mo.state` で履歴を貯める戦略を Replay し、十分なバー数が貯まってからシグナルが立つことを
確認します。
