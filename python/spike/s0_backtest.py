"""S0 spike — nautilus_trader self-contained backtest gate (issue #2).

This script is intentionally self-contained: it imports only nautilus_trader,
pyarrow and the stdlib. It does NOT import the `engine` package, so it can run
against a bare staged fixture catalog before any of the project's runtime code
exists.

It is also a *self-failing reproducible gate* (behavior-to-e2e substitute):
  1. assert_pin()        — hard pin on nautilus build (PRECISION_BYTES / version / py).
  2. assert_footer_widths() — every fixture parquet stores OHLC as fixed_size_binary[8].
Both raise S0GateError on mismatch (run_gates() runs them on the host
interpreter; main() catches and sys.exit(1)s) so a wrong nautilus build /
catalog precision fails loudly and reproducibly instead of aborting deep
inside Rust (SIGABRT).

  3. run_backtest()      — query the staged ParquetDataCatalog and run a trivial
                           counting strategy through BacktestEngine. Loopable: the
                           C# host reruns it across its >=300-frame window.
"""

from __future__ import annotations

import glob
import sys
from decimal import Decimal
from pathlib import Path

import pyarrow as pa
import pyarrow.parquet as pq

# ---------------------------------------------------------------------------
# Pins / constants
# ---------------------------------------------------------------------------

EXPECTED_PRECISION_BYTES = 8
EXPECTED_NAUTILUS_VERSION = "1.226.0"
EXPECTED_PY = (3, 13)

VENUE = "TSE"
SYMBOLS = ["8918", "6740", "3823"]
INITIAL_CASH_JPY = 10_000_000

FIXTURE_ROOT = Path(__file__).resolve().parent / "fixtures" / "jquants-catalog"
OHLC_COLUMNS = ("open", "high", "low", "close")


def bar_type_str(symbol: str) -> str:
    """'8918' -> '8918.TSE-1-DAY-LAST-EXTERNAL' (== catalog dir name == BarType str)."""
    return f"{symbol}.{VENUE}-1-DAY-LAST-EXTERNAL"


# ---------------------------------------------------------------------------
# Gate 1: hard pin on the running nautilus build (self-failing)
# ---------------------------------------------------------------------------


class S0GateError(RuntimeError):
    """Raised by a self-failing S0 gate (pin / footer) on any mismatch.

    Gate functions raise this instead of calling sys.exit, so an in-process
    host (Unity Mono / pythonnet) catches a wrong nautilus build as a Python
    exception on the SAME interpreter — instead of SIGABRT'ing deep in Rust.
    The headless main() catches it, prints the message and sys.exit(1).
    """


def assert_pin() -> None:
    """Hard-pin the nautilus build. Raises S0GateError on any mismatch.

    A 16-byte (high-precision) wheel would .unwrap() a PrecisionMismatch deep in
    Rust and SIGABRT the process when it hits the 8-byte fixtures. We refuse to
    even start instead, with a reproducible message (issue #2 / GH #34).
    """
    import nautilus_trader
    from nautilus_trader.core import nautilus_pyo3

    failures: list[str] = []

    precision = int(nautilus_pyo3.PRECISION_BYTES)
    if precision != EXPECTED_PRECISION_BYTES:
        failures.append(
            f"PRECISION_BYTES={precision}, expected {EXPECTED_PRECISION_BYTES} "
            f"(rebuild nautilus HIGH_PRECISION=false to match the 8-byte catalog)"
        )

    version = nautilus_trader.__version__
    if version != EXPECTED_NAUTILUS_VERSION:
        failures.append(
            f"nautilus_trader.__version__={version!r}, expected {EXPECTED_NAUTILUS_VERSION!r}"
        )

    py = sys.version_info[:2]
    if py != EXPECTED_PY:
        failures.append(f"python {py[0]}.{py[1]}, expected {EXPECTED_PY[0]}.{EXPECTED_PY[1]}")

    if failures:
        raise S0GateError("[S0 PIN FAIL]\n" + "\n".join(f"  - {f}" for f in failures))

    print(
        f"[S0 PIN OK] PRECISION_BYTES={precision} | nautilus {version} | "
        f"py {py[0]}.{py[1]}"
    )


# ---------------------------------------------------------------------------
# Gate 2: fixture parquet OHLC footer widths (self-failing)
# ---------------------------------------------------------------------------


def assert_footer_widths() -> None:
    """Every staged fixture parquet must store OHLC as fixed_size_binary[8].

    read_schema reads only the parquet footer (cheap) — this exercises the real
    pyarrow path on this machine before catalog.query() touches the same files.
    Raises S0GateError on any missing fixture or wrong width.
    """
    failures: list[str] = []
    checked = 0

    for symbol in SYMBOLS:
        id_dir = FIXTURE_ROOT / "data" / "bar" / bar_type_str(symbol)
        files = sorted(glob.glob(str(id_dir / "*.parquet")))
        if not files:
            failures.append(f"no parquet under {id_dir}")
            continue
        for f in files:
            schema = pq.read_schema(f)
            for col in OHLC_COLUMNS:
                if col not in schema.names:
                    failures.append(f"{f}: missing column {col!r}")
                    continue
                field_type = schema.field(col).type
                if not pa.types.is_fixed_size_binary(field_type):
                    failures.append(f"{f}: column {col!r} is {field_type}, not fixed_size_binary")
                    continue
                width = field_type.byte_width
                if width != EXPECTED_PRECISION_BYTES:
                    failures.append(
                        f"{f}: column {col!r} fixed_size_binary[{width}], "
                        f"expected [{EXPECTED_PRECISION_BYTES}]"
                    )
            checked += 1

    if failures:
        raise S0GateError("[S0 FOOTER FAIL]\n" + "\n".join(f"  - {f}" for f in failures))

    print(f"[S0 FOOTER OK] {checked} parquet file(s), OHLC=fixed_size_binary[8]")


# ---------------------------------------------------------------------------
# Self-contained Equity factory (TTWR instrument_factory.py port)
# ---------------------------------------------------------------------------


def _make_equity_instrument(symbol: str, venue: str):
    from nautilus_trader.model.currencies import JPY
    from nautilus_trader.model.identifiers import InstrumentId, Symbol, Venue
    from nautilus_trader.model.instruments import Equity
    from nautilus_trader.model.objects import Price, Quantity

    return Equity(
        instrument_id=InstrumentId(Symbol(symbol), Venue(venue)),
        raw_symbol=Symbol(symbol),
        currency=JPY,
        price_precision=1,
        price_increment=Price(Decimal("0.1"), precision=1),
        lot_size=Quantity(100, precision=0),
        isin=None,
        ts_event=0,
        ts_init=0,
    )


def _make_counting_strategy_cls():
    """Build the trivial strategy class lazily (after imports are known good)."""
    from nautilus_trader.trading.strategy import Strategy

    class CountingStrategy(Strategy):
        """Subscribes to each bar_type and counts bars. Never trades."""

        def __init__(self, bar_types):
            super().__init__()
            self._bar_types = list(bar_types)
            self.n_bars = 0

        def on_start(self):
            for bar_type in self._bar_types:
                self.subscribe_bars(bar_type)

        def on_bar(self, bar):
            self.n_bars += 1

    return CountingStrategy


# ---------------------------------------------------------------------------
# Backtest body — loopable
# ---------------------------------------------------------------------------


def run_backtest() -> dict:
    """Query the staged catalog and run one BacktestEngine pass.

    Builds a fresh engine each call so it can be looped by the C# host across its
    >=300-frame window. Returns {"bars": int, "fills": int, "final_equity": float}.
    """
    from nautilus_trader.backtest.engine import BacktestEngine
    from nautilus_trader.config import BacktestEngineConfig, LoggingConfig
    from nautilus_trader.model.currencies import JPY
    from nautilus_trader.model.data import Bar, BarType
    from nautilus_trader.model.enums import AccountType, OmsType
    from nautilus_trader.model.identifiers import Venue
    from nautilus_trader.model.objects import Money
    from nautilus_trader.persistence.catalog import ParquetDataCatalog

    catalog = ParquetDataCatalog(str(FIXTURE_ROOT))

    all_bars: list = []
    bar_types: list = []
    for symbol in SYMBOLS:
        bt_str = bar_type_str(symbol)
        bars = catalog.query(data_cls=Bar, identifiers=[bt_str])
        all_bars.extend(bars)
        bar_types.append(BarType.from_str(bt_str))
    all_bars.sort(key=lambda b: b.ts_init)

    cfg = BacktestEngineConfig(
        trader_id="S0SPIKE-001",
        logging=LoggingConfig(bypass_logging=True),
    )
    engine = BacktestEngine(config=cfg)

    fills: list = []
    try:
        engine.add_venue(
            venue=Venue(VENUE),
            oms_type=OmsType.NETTING,
            account_type=AccountType.CASH,
            base_currency=JPY,
            starting_balances=[Money(INITIAL_CASH_JPY, JPY)],
        )
        for symbol in SYMBOLS:
            engine.add_instrument(_make_equity_instrument(symbol, VENUE))

        engine.kernel.msgbus.subscribe(topic="events.fills.*", handler=fills.append)

        strategy = _make_counting_strategy_cls()(bar_types)
        engine.add_strategy(strategy)
        engine.add_data(all_bars)
        engine.run()

        account = engine.portfolio.account(Venue(VENUE))
        if account is not None:
            balance = account.balance_total(JPY)
            final_equity = (
                float(str(balance.as_decimal()))
                if balance is not None
                else float(INITIAL_CASH_JPY)
            )
        else:
            final_equity = float(INITIAL_CASH_JPY)

        return {
            "bars": int(strategy.n_bars),
            "fills": len(fills),
            "final_equity": final_equity,
        }
    finally:
        try:
            engine.dispose()
        except Exception:
            pass


def run_gates() -> None:
    """Run both self-failing gates on the CURRENT interpreter (no sys.exit).

    Public entry point for the in-process host (Unity Mono / pythonnet): it runs
    the pin + footer gates so a wrong (e.g. 16-byte) nautilus wheel is caught
    here as an S0GateError on the same interpreter that will run the backtest,
    instead of aborting deep inside Rust (SIGABRT). Raises S0GateError on any
    mismatch; returns None on success.
    """
    assert_pin()
    assert_footer_widths()


def main() -> None:
    try:
        run_gates()
    except S0GateError as e:
        print(e)
        sys.exit(1)
    result = run_backtest()
    print(
        f"[S0 BACKTEST OK] bars={result['bars']} | fills={result['fills']} | "
        f"final_equity={result['final_equity']:.0f} JPY"
    )


if __name__ == "__main__":
    main()
