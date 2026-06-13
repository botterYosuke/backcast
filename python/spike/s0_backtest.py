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
import time
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


def _make_synthetic_bars(bt_str: str, n: int = 64) -> list:
    """In-memory bars (no catalog/parquet) for the #18 Windows-leg synthetic diag.
    Matches the equity instrument precision (price precision=1, volume precision=0)."""
    from nautilus_trader.model.data import Bar, BarType
    from nautilus_trader.model.objects import Price, Quantity

    bar_type = BarType.from_str(bt_str)
    day_ns = 86_400_000_000_000
    base_ts = 1_700_000_000_000_000_000
    bars = []
    for i in range(n):
        px = Price(100.0 + i * 0.1, precision=1)
        ts = base_ts + i * day_ns
        bars.append(
            Bar(
                bar_type=bar_type,
                open=px,
                high=px,
                low=px,
                close=px,
                volume=Quantity(100, precision=0),
                ts_event=ts,
                ts_init=ts,
            )
        )
    return bars


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

    # #18 Windows-leg diagnostic B (stepwise exclusion via env, no C# change):
    #   S0_SYNTHETIC=1   -> build bars in-memory, skip catalog/parquet entirely
    #   S0_NO_STRATEGY=1 -> run engine with data but NO Python strategy (no on_bar
    #                       Rust->Python callback). Narrows whether the segfault is
    #                       the strategy callback vs data marshal vs core run.
    import os as _os
    synthetic   = _os.environ.get("S0_SYNTHETIC") == "1"
    no_strategy = _os.environ.get("S0_NO_STRATEGY") == "1"

    all_bars: list = []
    bar_types: list = []
    if synthetic:
        for symbol in SYMBOLS:
            bt_str = bar_type_str(symbol)
            bar_types.append(BarType.from_str(bt_str))
            all_bars.extend(_make_synthetic_bars(bt_str, n=64))
    else:
        catalog = ParquetDataCatalog(str(FIXTURE_ROOT))
        for symbol in SYMBOLS:
            bt_str = bar_type_str(symbol)
            bars = catalog.query(data_cls=Bar, identifiers=[bt_str])
            all_bars.extend(bars)
            bar_types.append(BarType.from_str(bt_str))
    all_bars.sort(key=lambda b: b.ts_init)

    # Logging mode (env-selectable for #18 diagnostics). Default bypass_logging=True:
    # Nautilus's logger is a PROCESS-GLOBAL singleton, so a 2nd BacktestEngine in the
    # same process panics ("attempted to set a logger after ... already initialized").
    # The long-lived-engine spike runs multiple backtests per process, so the global
    # logger must not be (re)init'd. S0_LOG_MODE=error selects the Live-style config
    # (log_level=ERROR/log_level_file=OFF) for a SINGLE-run confound check.
    if _os.environ.get("S0_LOG_MODE") == "error":
        logging_cfg = LoggingConfig(log_level="ERROR", log_level_file="OFF")
    else:
        logging_cfg = LoggingConfig(bypass_logging=True)

    cfg = BacktestEngineConfig(trader_id="S0SPIKE-001", logging=logging_cfg)
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

        strategy = None
        if not no_strategy:
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
            "bars": int(strategy.n_bars) if strategy is not None else len(all_bars),
            "fills": len(fills),
            "final_equity": final_equity,
        }
    finally:
        try:
            engine.dispose()
        except Exception:
            pass


class S0EngineSeam:
    """#18 Windows-leg spike: run the S0 backtest on a PYTHON-OWNED thread.

    Root cause isolated by the #18 diagnostic ladder: BacktestEngine.run segfaults
    when driven on a C#-created (`new Thread`) host thread under Windows-Mono +
    pythonnet (the foreign thread's state is mishandled), but runs fine on the
    Unity main thread and under Mac-Mono. This seam runs the *identical* existing
    backtest on a CPython-owned ``threading.Thread`` instead — the same ownership
    model S2-spike's engine-owned asyncio loop uses (GREEN on Windows-Mono).

    The host (a C# worker under ``Py.GIL()``) calls ``start()`` then ``join()``;
    ``threading.Thread.join`` releases the GIL while blocking, so the engine thread
    acquires it and runs Nautilus while the Unity MAIN thread stays GIL-free.
    Results cross back as C# primitives only (no PyObject escapes the boundary).
    """

    def __init__(self) -> None:
        self._thread = None
        self._ok = False
        self._bars = 0
        self._fills = 0
        self._equity = 0.0
        self._error = ""

    def start(self) -> None:
        import threading

        def _run() -> None:
            print("[S0 PYTHREAD] python-owned engine thread: nautilus run start", flush=True)
            try:
                r = run_backtest()
                self._bars = int(r["bars"])
                self._fills = int(r["fills"])
                self._equity = float(r["final_equity"])
                self._ok = True
                print(f"[S0 PYTHREAD] python-owned engine thread: run done bars={self._bars}", flush=True)
            except BaseException as e:  # noqa: BLE001 — surfaced to host as error()
                self._error = repr(e)
                print(f"[S0 PYTHREAD] python-owned engine thread: run ERROR {self._error}", flush=True)

        self._thread = threading.Thread(target=_run, name="s0-py-engine-thread", daemon=True)
        self._thread.start()
        print("[S0 PYTHREAD] python-owned engine thread spawned (daemon)", flush=True)

    def join(self, timeout: float = 90.0) -> bool:
        """Join the engine thread; returns True iff it terminated within timeout.
        ``join`` releases the GIL while waiting (host stays GIL-free / main renders)."""
        if self._thread is None:
            return False
        self._thread.join(timeout)
        terminated = not self._thread.is_alive()
        print(f"[S0 PYTHREAD] engine thread join: terminated={terminated}", flush=True)
        return terminated

    def ok(self) -> bool:
        return bool(self._ok)

    def bars(self) -> int:
        return int(self._bars)

    def fills(self) -> int:
        return int(self._fills)

    def equity(self) -> float:
        return float(self._equity)

    def error(self) -> str:
        return str(self._error)


class S0LongLivedEngine:
    """#18 Windows-leg spike (2): a PROCESS-LIFETIME daemon engine thread.

    The §1.2 spike showed a python-owned thread *runs* the backtest fine (bars=204)
    but segfaults while the per-run thread *terminates*. This variant uses the
    production / S2-spike ownership model instead: ONE long-lived daemon thread
    that owns a command queue, runs backtests on the SAME thread, and is NEVER
    terminated/joined/recreated per-run — work is marshalled IN, run-scoped engine
    resources are disposed, and the thread returns to its wait state. The OS
    reclaims the thread + interpreter at process exit (no explicit finalize), so
    the crashing per-run-termination code path is never entered.

    The host (a C# worker under ``Py.GIL()``) calls ``start()`` once, then
    ``submit_backtest()`` repeatedly; ``queue.get`` releases the GIL while blocking
    so the engine thread runs while the Unity MAIN thread stays GIL-free.
    """

    def __init__(self) -> None:
        import queue
        self._cmd_q: "queue.Queue" = queue.Queue()
        self._res_q: "queue.Queue" = queue.Queue()
        self._thread = None
        self._thread_id = 0
        self._last_ok = False
        self._last_bars = 0
        self._last_fills = 0
        self._last_equity = 0.0
        self._last_err = ""

    def start(self) -> int:
        """Spawn the long-lived engine thread ONCE. Idempotent: a live thread is
        never duplicated (Play-stop/re-Play guard). Returns the engine thread id."""
        import threading

        if self._thread is not None and self._thread.is_alive():
            return int(self._thread_id)  # never create a second engine thread

        def _loop() -> None:
            self._thread_id = threading.get_ident()
            print(f"[S0 LONGTHREAD] engine thread up id={self._thread_id} (waiting)", flush=True)
            while True:
                cmd = self._cmd_q.get()        # blocks here -> GIL released (wait state)
                if cmd == "STOP":
                    print("[S0 LONGTHREAD] engine thread STOP (explicit)", flush=True)
                    return
                if cmd == "BACKTEST":
                    print("[S0 LONGTHREAD] backtest start (same long-lived thread)", flush=True)
                    try:
                        r = run_backtest()      # run-scoped engine is created + disposed inside
                        self._res_q.put((True, int(r["bars"]), int(r["fills"]), float(r["final_equity"]), ""))
                        print(f"[S0 LONGTHREAD] backtest done bars={r['bars']}; back to wait", flush=True)
                    except BaseException as e:   # noqa: BLE001 — surfaced to host
                        self._res_q.put((False, 0, 0, 0.0, repr(e)))
                        print(f"[S0 LONGTHREAD] backtest ERROR {e!r}; back to wait", flush=True)

        self._thread = threading.Thread(target=_loop, name="s0-longlived-engine", daemon=True)
        self._thread.start()
        # Wait until the loop published its thread id (so thread_id() is valid).
        deadline = time.perf_counter() + 5.0
        while self._thread_id == 0 and time.perf_counter() < deadline:
            time.sleep(0.001)
        return int(self._thread_id)

    def submit_backtest(self, timeout: float = 90.0) -> bool:
        """Marshal ONE backtest onto the long-lived thread; block (GIL released in
        ``res_q.get``) until its result. Stores result as primitives; returns ok."""
        import queue

        self._cmd_q.put("BACKTEST")
        try:
            ok, bars, fills, equity, err = self._res_q.get(timeout=timeout)
        except queue.Empty:
            self._last_ok, self._last_err = False, "submit_backtest timeout"
            return False
        self._last_ok, self._last_bars = ok, bars
        self._last_fills, self._last_equity, self._last_err = fills, equity, err
        return bool(ok)

    def thread_id(self) -> int:
        return int(self._thread_id)

    def is_waiting(self) -> bool:
        """True iff the engine thread is alive and idle (no pending command)."""
        return (self._thread is not None and self._thread.is_alive()
                and self._cmd_q.empty() and self._res_q.empty())

    def last_bars(self) -> int:
        return int(self._last_bars)

    def last_fills(self) -> int:
        return int(self._last_fills)

    def last_equity(self) -> float:
        return float(self._last_equity)

    def last_error(self) -> str:
        return str(self._last_err)


# Process-lifetime singleton so a Play-stop/re-Play (interpreter persists) reuses
# the SAME engine thread instead of spawning a second one (#18 spike condition).
_LONG_LIVED_ENGINE: "S0LongLivedEngine | None" = None


def get_long_lived_engine() -> "S0LongLivedEngine":
    global _LONG_LIVED_ENGINE
    if _LONG_LIVED_ENGINE is None:
        _LONG_LIVED_ENGINE = S0LongLivedEngine()
    return _LONG_LIVED_ENGINE


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
