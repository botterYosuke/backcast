from engine.kernel.orders import OrderSide
from engine.kernel.strategy import Strategy


class ThresholdStrategy(Strategy):
    """付録: imperative 形式の最小戦略（marimo 版 01_threshold と同じルール）。

    marimo の符号付き submit_market(qty) と違い、imperative では OrderSide と
    正の数量で発注する。新規作成は marimo 形式を推奨。
    """

    def on_bar(self, bar) -> None:
        signal = 1.0 if bar.close > 1010.0 else (-1.0 if bar.close < 990.0 else 0.0)
        qty = signal * 10.0
        if qty > 0.0:
            self.submit_market(self.instrument_id, OrderSide.BUY, qty)
        elif qty < 0.0:
            self.submit_market(self.instrument_id, OrderSide.SELL, abs(qty))
