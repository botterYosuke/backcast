"""#112 S3 gates — marimo-forced live materialize (ADR-0025 D4).

The editor live path builds a marimo App → ``LiveCellBridge`` factory; a non-marimo file is
rejected with the dedicated ``NOT_A_MARIMO_NOTEBOOK`` code (no imperative dispatch — D4's
"唯一の道"). Pinned at three altitudes:

  * the loader itself (``build_live_marimo_loader``): marimo → (app, scenario, bridge_factory);
    non-marimo → ``StrategyLoadError(error_code=NOT_A_MARIMO_NOTEBOOK)``;
  * the ``StrategyRegistry`` (register-time validation) surfaces the code;
  * the ``LiveStrategyHost`` (start-time materialize) surfaces the code AND, for a marimo file,
    hands ``controller.attach`` a bridge factory (the live ``strategy_cls``).
"""
from __future__ import annotations

import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))

from engine.kernel.live.cell_bridge import LiveCellBridge  # noqa: E402
from engine.strategy_runtime.live_cell_runtime import (  # noqa: E402
    NOT_A_MARIMO_NOTEBOOK,
    build_live_marimo_loader,
)
from engine.strategy_runtime.strategy_loader import StrategyLoadError  # noqa: E402

_FIX = os.path.join(
    os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
    "spike", "fixtures", "strategies",
)
_MARIMO = os.path.join(_FIX, "kernel_spike_buy_sell_cell.py")        # marimo cell + sidecar
_IMPERATIVE = os.path.join(_FIX, "kernel_spike_buy_sell.py")          # imperative Strategy subclass


# ── loader ────────────────────────────────────────────────────────────────


def test_loader_marimo_returns_bridge_factory() -> None:
    app, scenario, factory = build_live_marimo_loader()(_MARIMO)
    assert scenario["instruments"] == ["8918.TSE"]
    assert scenario["granularity"] == "Daily"
    bridge = factory(instrument_id="8918.TSE")
    assert isinstance(bridge, LiveCellBridge)


def test_loader_non_marimo_raises_not_a_marimo_notebook() -> None:
    with pytest.raises(StrategyLoadError) as ei:
        build_live_marimo_loader()(_IMPERATIVE)
    assert ei.value.error_code == NOT_A_MARIMO_NOTEBOOK


def test_loader_accepts_original_path_anchor() -> None:
    # original_path becomes the cell's __file__ anchor (v19 artifacts loading / ADR-0021).
    app, _scenario, factory = build_live_marimo_loader()(
        _MARIMO, original_path=__import__("pathlib").Path(_MARIMO)
    )
    assert callable(factory)


# ── registry (register-time validation) ──────────────────────────────────────


def test_registry_marimo_registers_and_non_marimo_surfaces_code() -> None:
    from engine.live.strategy_registry import StrategyRegistry, StrategyRegistryError

    reg = StrategyRegistry(loader=build_live_marimo_loader())
    handle = reg.register(_MARIMO)
    assert handle.scenario["instruments"] == ["8918.TSE"]
    assert handle.display_name == "LiveCellBridgeFactory"

    with pytest.raises(StrategyRegistryError) as ei:
        reg.register(_IMPERATIVE)
    assert ei.value.error_code == NOT_A_MARIMO_NOTEBOOK


# ── host (start-time materialize) ────────────────────────────────────────────


class _Session:
    is_logged_in = True


class _RecordingController:
    def __init__(self) -> None:
        self.attached_strategy_cls = None

    def attach(self, *, strategy_cls, **_kw) -> None:
        self.attached_strategy_cls = strategy_cls

    def detach(self, **_kw) -> None:
        pass

    def cancel_inflight_orders(self, **_kw) -> None:
        pass


def _host(controller):
    from engine.live.run_registry import RunRegistry
    from engine.live.strategy_host import LiveStrategyHost

    return LiveStrategyHost(
        run_registry=RunRegistry(),
        session_provider=lambda: _Session(),
        engine_controller=controller,
        loader=build_live_marimo_loader(),
    )


def test_host_non_marimo_surfaces_not_a_marimo_notebook() -> None:
    from engine.live.strategy_host import LiveStrategyHostError, StartParams

    host = _host(_RecordingController())
    with pytest.raises(LiveStrategyHostError) as ei:
        host.start_run(
            StartParams(
                strategy_id="x",
                strategy_file=_IMPERATIVE,
                instrument_id="8918.TSE",
                venue="MOCK",
            )
        )
    assert ei.value.error_code == NOT_A_MARIMO_NOTEBOOK


def test_host_marimo_attaches_bridge_factory() -> None:
    from engine.live.strategy_host import StartParams

    controller = _RecordingController()
    host = _host(controller)
    record = host.start_run(
        StartParams(
            strategy_id="x",
            strategy_file=_MARIMO,
            instrument_id="8918.TSE",
            venue="MOCK",
        )
    )
    assert record.nautilus_strategy_id.startswith("LIVE-")
    # The live strategy_cls handed to the engine is the bridge factory; calling it yields a bridge.
    factory = controller.attached_strategy_cls
    assert factory is not None
    assert isinstance(factory(instrument_id="8918.TSE"), LiveCellBridge)
