# 06 複数銘柄を扱う

ユニバースに複数銘柄を入れると、戦略は複数銘柄を同時に扱えます。

## バーは銘柄ごとに流れてくる

複数銘柄でも `on_bar` は **1 バーずつ** 呼ばれます。`get_bar()` が返すのは「いま処理中の
1 銘柄のバー」です。どの銘柄かは `get_bar().instrument_id` で分かります。各銘柄のバーが
時刻順に交互に流れてくる、とイメージしてください。

## 全銘柄のポジションを読む

`get_portfolio()` は全銘柄分の情報を持っています。

- `pf.positions` — `{銘柄: 保有数}` の辞書。**建玉のある銘柄だけ**で、建玉ゼロの銘柄は含まれません。
- `pf.net_qty(iid)` — 指定銘柄の保有数（建玉ゼロや未保有なら 0）。全銘柄を確実に見たいときはこちら。

## 銘柄を指定して発注する

`submit_market(qty, instrument_id=iid)` で発注先の銘柄を指定できます（省略時は主銘柄）。

```python
import marimo

app = marimo.App()


@app.cell
def _equal_weight():
    bar = get_bar()
    pf = get_portfolio()
    iid = bar.instrument_id          # いま流れている銘柄
    target_qty = 5.0                 # 各銘柄を 5 株ずつ
    delta = target_qty - pf.net_qty(iid)
    submit_market(delta, instrument_id=iid)
    return (delta,)
```

完成形（コピー用）: [`06_equal_weight.py`](../samples/index.md#equal-weight)

!!! warning "発注は『いま流れている銘柄』に対して行う"
    成行注文はその銘柄の**最後に判明している終値**で約定します。`get_bar()` で来ている銘柄に
    発注すれば、それはこのバーの終値です。一方、別の銘柄へ発注すると**古い終値**で約定し得ます。
    さらに、まだ一度もバーが来ていない銘柄への発注は**拒否**されます。`instrument_id` を
    省略すると主銘柄宛てになるため、非主銘柄のバーを処理中に省略すると意図せず主銘柄へ発注します。
    基本は上記のように `bar.instrument_id` 宛てに発注してください。

!!! tip "銘柄ごとの状態"
    銘柄ごとに履歴やフラグを持ちたいときは、`mo.state` に `{銘柄: 値}` の辞書を入れ、
    `get_bar().instrument_id` をキーに更新します（[04 のフィードバック状態](04-bar-contract.md) の応用）。

## 確認

ユニバースに 2 銘柄入れて Run すると、両方の銘柄に約定が出て、それぞれ目標株数で
止まることを確認します。
