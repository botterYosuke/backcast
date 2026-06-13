"""Import-purity gate for the LiveAuto path (#25・AC: nautilus_trader* 非ロード・D5).

#24 の `test_gate_import_purity` は kernel **module の import** が pure であることだけを見る。
#25 の権威ゲートは **full mock LiveAuto lifecycle を fresh subprocess で完走**させ、
attach→fill→stop/detach の **後** に `nautilus_trader*` が sys.modules に載っていないことを検査する
（controller 単体テストでは `live_orchestrator.py` のような上流 import リークを検出できない・D5 layer 2）。

`spike.kernel_live.run_mock_live` が本番と同じ seam で roundtrip を回し、purity を検査して
"[KERNEL LIVE PURITY PASS]" を print し exit 0 する。Mono 版 full Live probe（D5 layer 3）も同 main() を使う。

Runnable directly (`python tests/test_kernel_live_purity.py`) or via pytest.
"""
from __future__ import annotations

import sys


def _run_live_purity_child():
    from spike.kernel_golden.subprocess_util import run_python

    # full LiveAuto roundtrip（async loop thread + 40 bar 注入 + teardown）。15s で十分。
    return run_python(["-m", "spike.kernel_live.run_mock_live"], timeout=60)


# register_live_strategy（StrategyRegistry）も kernel loader を使い Rust core を引かないことを
# fresh subprocess で確認する。full LiveAuto harness（run_mock_live）は controller を直接叩いて
# register 経路を通らないため、ここが register-path purity の唯一のゲート（codex review #25 finding 2）。
_REGISTER_CHILD = r"""
import functools, sys
from engine.kernel.strategy import Strategy as KernelStrategy
from engine.live.strategy_registry import StrategyRegistry
from engine.strategy_runtime import strategy_loader

reg = StrategyRegistry(
    loader=functools.partial(strategy_loader.load, base_cls=KernelStrategy)
)
handle = reg.register("spike/fixtures/strategies/kernel_spike_buy_sell.py")
assert handle.strategy_id, "register did not issue a strategy_id"

from spike.kernel_golden.purity import leaked_nautilus_modules
leaked = leaked_nautilus_modules(sys.modules)
if leaked:
    print("LEAKED:" + ",".join(leaked)); sys.exit(1)
print("REGISTER PURE")
"""


def test_full_liveauto_is_rust_core_free() -> None:
    result = _run_live_purity_child()
    assert result.returncode == 0, (
        "full mock LiveAuto roundtrip failed or loaded nautilus into a clean interpreter — "
        "AC requires the LiveAuto path to stay Rust-core-free (Live でも Rust core 非ロード).\n"
        f"stdout={result.stdout!r}\nstderr={result.stderr!r}"
    )
    assert "[KERNEL LIVE PURITY PASS]" in result.stdout
    assert "nautilus_leaked=0" in result.stdout


def test_register_live_strategy_loads_kernel_twin_without_nautilus() -> None:
    from spike.kernel_golden.subprocess_util import run_python

    result = run_python(["-c", _REGISTER_CHILD], timeout=60)
    assert result.returncode == 0, (
        "register_live_strategy's StrategyRegistry loaded nautilus or failed to register the "
        "kernel strategy — the register seam must use the kernel loader and stay Rust-core-free.\n"
        f"stdout={result.stdout!r}\nstderr={result.stderr!r}"
    )
    assert "REGISTER PURE" in result.stdout


if __name__ == "__main__":
    res = _run_live_purity_child()
    if res.returncode != 0:
        print(f"[KERNEL LIVE PURITY FAIL] {res.stdout.strip()} {res.stderr.strip()}")
        sys.exit(1)
    print(res.stdout.strip())
