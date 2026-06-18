# チュートリアル

Backcast の戦略は **Python（marimo の cell-DAG 形式）** で書きます。このチュートリアルは、
Python が一通り書ける人が「Backcast の戦略モデル」を順に身につけるための入口です。

各レッスンは **概念 → コード → 確認** の順で進みます。完成形のコードは
[サンプル戦略](../samples/index.md) にまとまっているので、手を動かしながら読んでください。

## 学ぶ順番

1. [はじめての戦略](01-first-strategy.md) — `marimo.App` / `@app.cell` / `get_bar()`
2. [発注する](02-submitting-orders.md) — `submit_market(qty)` の符号付き数量
3. [ポートフォリオを読む](03-reading-portfolio.md) — `get_portfolio()` と目標との差分発注
4. [先読み禁止とバー契約](04-bar-contract.md) — fill 前スナップショット / 1 バー 1 回 / 状態保持
5. [シナリオと銘柄](05-scenario-universe.md) — 期間・足種・銘柄・初期資金の決め方
6. [複数銘柄を扱う](06-multi-instrument.md) — `positions` / `net_qty()` / 銘柄指定発注
7. [付録: imperative 戦略](99-imperative.md) — `Strategy` サブクラス（命令型）

## marimo 戦略の最小構成

すべてのレッスンはこの形から始まります。`marimo` を import し、`app = marimo.App()` を作り、
`@app.cell` で処理を書く。これだけで Backcast は marimo 戦略として認識します。

```python
import marimo

app = marimo.App()


@app.cell
def _():
    bar = get_bar()  # いまのバー（host が毎バー注入する）
    return (bar,)
```

!!! note "なぜ `get_bar` は import していないのに使えるのか"
    `get_bar` / `get_portfolio` / `submit_market` は、戦略を走らせるときに Backcast が
    cell へ**注入**します。組み込み関数のように、定義せずそのまま呼べます。
