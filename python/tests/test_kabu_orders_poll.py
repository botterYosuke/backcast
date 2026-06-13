"""INV-K3-POLL — kabu 約定通知 GET /orders 1 秒 polling の契約 (findings/0009)。

skill: kabu に約定 PUSH は無く、約定通知は GET /orders を 1 秒間隔で polling
して取得する。idle (追跡注文ゼロ) で自己終了し、連続失敗時は指数バックオフ。

決定論テスト: _rate_limit_sleep と _poll_orders_once を差し替え、待機列で
間隔・自己終了・バックオフを検証する (実 HTTP / 実時間に依存しない)。
"""
from __future__ import annotations

from engine.exchanges.kabusapi_execution import (
    _ORDERS_POLL_INTERVAL_S,
    _POLL_MAX_BACKOFF_S,
    KabuExecutionEngine,
)


def _engine() -> KabuExecutionEngine:
    eng = KabuExecutionEngine(
        client=object(),  # poll を stub するため未使用
        rl=object(),
        env="verify",
        time_source=lambda: 0.0,
    )
    eng._on_order_event = lambda _e: None
    return eng


def test_poll_interval_is_1s() -> None:
    assert _ORDERS_POLL_INTERVAL_S == 1.0


async def test_success_polls_at_1s_interval() -> None:
    """成功し続ける限り 1 秒間隔で polling する。"""
    eng = _engine()
    eng._orders_ref = {"c1": object()}  # type: ignore[dict-item]
    sleeps: list[float] = []

    async def fake_sleep(d: float) -> None:
        sleeps.append(d)
        if len(sleeps) >= 3:
            eng._orders_ref.clear()  # 3 周で追跡対象を空にして停止

    async def fake_poll() -> None:
        return None

    eng._rate_limit_sleep = fake_sleep
    eng._poll_orders_once = fake_poll  # type: ignore[method-assign]
    await eng._run_orders_poll()
    assert sleeps == [1.0, 1.0, 1.0]


async def test_self_terminates_when_no_orders() -> None:
    """追跡注文がゼロなら 1 回も sleep/poll せず即終了する (idle ループを畳む)。"""
    eng = _engine()
    eng._orders_ref = {}
    polled: list[int] = []

    async def fake_poll() -> None:
        polled.append(1)

    eng._poll_orders_once = fake_poll  # type: ignore[method-assign]
    await eng._run_orders_poll()
    assert polled == []


async def test_failure_backs_off_exponentially_and_caps() -> None:
    """連続失敗で待機が 1→2→4... と倍増し _POLL_MAX_BACKOFF_S で頭打ち。"""
    eng = _engine()
    eng._orders_ref = {"c1": object()}  # type: ignore[dict-item]
    sleeps: list[float] = []

    async def fake_sleep(d: float) -> None:
        sleeps.append(d)
        if len(sleeps) >= 8:
            eng._orders_ref.clear()

    async def failing_poll() -> None:
        raise RuntimeError("kabu body logged out")

    eng._rate_limit_sleep = fake_sleep
    eng._poll_orders_once = failing_poll  # type: ignore[method-assign]
    await eng._run_orders_poll()
    # 初回は backoff=0→interval(1.0)、以降 fail ごとに *2 で倍増し 30 で cap。
    # 中間列まで完全固定し、倍率改変 (例 1.5x) がすり抜けないようにする。
    assert sleeps == [1.0, 2.0, 4.0, 8.0, 16.0, 30.0, 30.0, 30.0]
    assert sleeps[-1] == _POLL_MAX_BACKOFF_S
