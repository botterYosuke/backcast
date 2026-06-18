# 01 はじめての戦略

最初のゴールは「戦略がロードされ、毎バー呼ばれている」ことを確認することです。発注はまだ
しません。

## 概念

Backcast の marimo 戦略は、`@app.cell` で書いた処理を **1 バーごとに 1 回** 走らせます。
そのバーの情報は `get_bar()` で読めます。返ってくるのは OHLCV を持つオブジェクトです。

| フィールド | 意味 |
|---|---|
| `instrument_id` | 銘柄コード（例 `7203.T`） |
| `ts_event_ns` | バーの時刻（UTC ナノ秒） |
| `open` / `high` / `low` / `close` | 始値 / 高値 / 安値 / 終値 |
| `volume` | 出来高 |

**最低条件**: 1 つ以上の cell が `get_bar()`（または `get_portfolio()`）を読むこと。何も
読まない戦略は「毎バー何もしない」とみなされ、ロード時に弾かれます。

## コード

```python
import marimo

app = marimo.App()


@app.cell
def _observe():
    bar = get_bar()
    note = f"{bar.instrument_id} close={bar.close}"
    return (note,)
```

完成形（コピー用）: [`00_observe.py`](../samples/index.md#observe)

## 確認

Strategy Editor でこのファイルを開き、Replay を Run します。エラーなく完走し、注文が 0 件で
あれば成功です。次のレッスンで発注を足します。
