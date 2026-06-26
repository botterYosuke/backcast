"""engine.strategy_runtime.summary — aggregate metrics for a strategy run-buffer."""
from __future__ import annotations

import json
import logging
from collections import deque
import os
import tempfile
from pathlib import Path
from typing import Optional

from engine.strategy_runtime.run_buffer_reader import EquityPoint, Fill

log = logging.getLogger(__name__)


def _realised_pnl_fifo(fills: list[Fill]) -> list[float]:
    """Compute per-trade realised PnL using FIFO, instrument-scoped."""
    open_lots: dict[str, deque[tuple[str, float, float]]] = {}
    realised: list[float] = []
    for fill in fills:
        lots = open_lots.setdefault(fill.instrument_id, deque())
        opposite = "SELL" if fill.side == "BUY" else "BUY"
        remaining = fill.qty
        while remaining > 0 and lots and lots[0][0] == opposite:
            entry_side, entry_qty, entry_price = lots[0]
            close_qty = min(remaining, entry_qty)
            sign = 1.0 if entry_side == "BUY" else -1.0
            realised.append((fill.price - entry_price) * sign * close_qty)
            entry_qty -= close_qty
            remaining -= close_qty
            if entry_qty <= 0:
                lots.popleft()
            else:
                lots[0] = (entry_side, entry_qty, entry_price)
        if remaining > 0:
            lots.append((fill.side, remaining, fill.price))
    return realised


def compute_summary(fills: list[Fill] | str | Path, equity_points: list[EquityPoint] | None = None) -> dict:
    """Compute aggregate metrics from typed Fill / EquityPoint records, or a path to run_dir."""
    if isinstance(fills, (str, Path)):
        from engine.strategy_runtime.run_buffer_reader import RunBufferReader
        reader = RunBufferReader(Path(fills))
        fills_list = reader.fills
        equity_list = reader.equity_points
    else:
        fills_list = fills
        equity_list = equity_points if equity_points is not None else []

    equity_values = [ep.equity for ep in equity_list]

    if equity_values:
        total_pnl = equity_values[-1] - equity_values[0]
        peak = equity_values[0]
        max_dd = 0.0
        for v in equity_values:
            if v > peak:
                peak = v
            dd = peak - v
            if dd > max_dd:
                max_dd = dd
    else:
        total_pnl = 0.0
        max_dd = 0.0

    fee_total = sum(f.commission for f in fills_list if f.commission is not None)

    realised = _realised_pnl_fifo(fills_list)
    trade_count = len(realised)
    win_rate: Optional[float] = (
        sum(1 for r in realised if r > 0) / trade_count if trade_count > 0 else None
    )

    return {
        "total_pnl": total_pnl,
        "max_drawdown": max_dd,
        "trade_count": trade_count,
        "win_rate": win_rate,
        "fee_total": fee_total,
        "equity_points": len(equity_values),
        "fills_count": len(fills_list),
    }


def equity_curve_stats(equity_values: list) -> dict:
    """Compute max_drawdown / sharpe / sortino from an in-memory equity curve."""
    import math

    n = len(equity_values)
    max_drawdown = 0.0
    sharpe = 0.0
    sortino = 0.0
    if n >= 2:
        peak = equity_values[0]
        for eq in equity_values:
            if eq > peak:
                peak = eq
            dd = peak - eq
            if dd > max_drawdown:
                max_drawdown = dd
        returns = [
            (equity_values[i] - equity_values[i - 1]) / equity_values[i - 1]
            for i in range(1, n)
            if equity_values[i - 1] != 0.0
        ]
        if returns:
            mean_r = sum(returns) / len(returns)
            variance = sum((r - mean_r) ** 2 for r in returns) / len(returns)
            std_r = math.sqrt(variance)
            sharpe = (mean_r / std_r) * math.sqrt(252) if std_r != 0.0 else 0.0
            neg_returns = [r for r in returns if r < 0.0]
            if neg_returns:
                neg_var = sum(r ** 2 for r in neg_returns) / len(neg_returns)
                downside_std = math.sqrt(neg_var)
                sortino = (mean_r / downside_std) * math.sqrt(252) if downside_std != 0.0 else 0.0
    return {"max_drawdown": max_drawdown, "sharpe": sharpe, "sortino": sortino}


def write_summary_json(target_dir: Path, summary: dict) -> Path:
    """Persist summary as summary.json under target_dir atomically."""
    target_dir = Path(target_dir)
    target_dir.mkdir(parents=True, exist_ok=True)
    target = target_dir / "summary.json"
    fd, tmp_path = tempfile.mkstemp(prefix="summary.", suffix=".json", dir=str(target_dir))
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            json.dump(summary, fh, ensure_ascii=False, indent=2)
        os.replace(tmp_path, target)
    except Exception:
        try:
            Path(tmp_path).unlink(missing_ok=True)
        except OSError:
            pass
        raise
    return target
