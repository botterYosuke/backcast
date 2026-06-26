import marimo

app = marimo.App()


@app.cell
def _markdown():
    # Markdown 出力。mo.md(...) を cell の最後の式に置くと text/markdown として
    # 描画される。見出し・太字・箇条書きが Strategy Editor の rich-text pane に出る。
    # _ 始まりの別名 import は cell ローカルなので、他 cell の import と衝突しない。
    import marimo as _mo

    _mo.md(
        """
        # リッチ output デモ

        marimo の **per-cell RUN** は、戦略バックテスト（`get_bar` / Replay）とは別に、
        各セルを単独実行してリッチな表示を確認するための経路です。このサンプルは
        bt 非依存で、4 種類の出力を 1 セルずつ示します。

        - **Markdown** … この見出し・太字・箇条書き
        - **テーブル** … pandas DataFrame
        - **チャート** … matplotlib の図
        - **UI** … `mo.ui` ウィジェット
        """
    )
    return


@app.cell
def _table():
    # テーブル出力。pandas DataFrame を最後の式に置くと text/html の <table> として
    # 描画される。Strategy Editor は table を pipe 行（| 区切り）に射影して表示する。
    import pandas as _pd

    _pd.DataFrame(
        {
            "銘柄": ["7203.TSE", "6758.TSE", "9984.TSE"],
            "終値": [2500, 1800, 9200],
            "数量": [100, 200, 50],
        }
    )
    return


@app.cell
def _chart():
    # チャート出力。matplotlib(Agg) の Figure を最後の式に置くと、自己完結した
    # image/png（data:image/png;base64,... の data URL）として描画される。
    # Agg backend は GUI を必要とせず headless でも PNG を生成できる。
    import matplotlib as _matplotlib

    _matplotlib.use("Agg")
    import matplotlib.pyplot as _plt

    _fig, _ax = _plt.subplots(figsize=(4, 2.5))
    _ax.plot([0, 1, 2, 3, 4], [0, 1, 4, 9, 16], marker="o")
    _ax.set_title("y = x^2")
    _ax.set_xlabel("x")
    _ax.set_ylabel("y")
    _fig
    return


@app.cell
def _ui():
    # UI ウィジェット境界。mo.ui.slider(...) を最後の式に置くと、interactive な
    # ウィジェットが marimo の web フロントエンド向けに html/plain へ畳まれて出る。
    # Strategy Editor は interactive UI を描けないので、これは「境界を正直に示す」
    # サンプル（実際に操作できるのは marimo web 版・Strategy Editor では静的表示）。
    import marimo as _mo

    _mo.ui.slider(start=1, stop=10, value=5, label="サンプルスライダー")
    return


if __name__ == "__main__":
    app.run()
