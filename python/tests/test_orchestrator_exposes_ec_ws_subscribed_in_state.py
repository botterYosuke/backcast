"""issue #85 Q1 (A'): get_state_json は ``ec_ws_subscribed`` / ``last_event_ws_recv_ts_ms``
を top-level に露出する。Unity 側 VenueConnectionViewModel は SUBSCRIBED badge
(= market-data 購読成立) ではなく、この 2 つで EC WS gate を判定する
(findings 0053 §issue#85 改訂)。

SUBSCRIBED は live_orchestrator.subscribe_market_data 成立時のみ遷移するため、
本 E2E Runner のように market-data を購読せず発注だけする経路では永遠に CONNECTED
止まり → 額面 SUBSCRIBED gate は常時 RED。EC WS の生フレーム受信を独立シグナルに
することで、SSL 失敗時は確実に 30s 内に検出できる。
"""
from __future__ import annotations

import json
import types

from engine.core import DataEngine
from engine.live.state_machine import VenueStateMachine
from engine.mode_manager import ModeManager
from engine._backend_impl import DataEngineBackend


def _backend_with_fake_session(adapter_recv_ts_ms):
    """`_live_mgr._session.runner.adapter` 経由で ec ts を読みに行く配線を fake で
    用意する (LiveSession 実体は重いので minimal namespace で代替)。"""
    eng = DataEngine()
    venue_sm = VenueStateMachine()
    eng.state_machine = venue_sm
    mode_manager = ModeManager(venue_sm=venue_sm, replay_engine=eng)
    eng.attach_mode_manager(mode_manager)
    backend = DataEngineBackend(engine=eng, mode_manager=mode_manager, venue_sm=venue_sm)

    fake_adapter = types.SimpleNamespace(
        ec_ws_first_recv_ts_ms=adapter_recv_ts_ms,
        ec_ws_last_recv_ts_ms=adapter_recv_ts_ms,
    )
    # `_resolve_live_last_error` reads runner.last_error → fake には None で足りる。
    fake_runner = types.SimpleNamespace(adapter=fake_adapter, last_error=None)
    fake_session = backend._make_test_session(runner=fake_runner)
    backend._live_mgr._session = fake_session
    return backend


def test_state_exposes_ec_ws_subscribed_true_when_adapter_has_first_recv_ts() -> None:
    backend = _backend_with_fake_session(adapter_recv_ts_ms=1_700_000_000_000)
    payload = json.loads(backend.get_state_json())

    assert payload["ec_ws_subscribed"] is True, (
        "first_recv_ts_ms != None ⇒ ec_ws_subscribed must be True (SSL handshake 済)"
    )
    assert payload["last_event_ws_recv_ts_ms"] == 1_700_000_000_000


def test_state_exposes_ec_ws_subscribed_false_when_adapter_has_no_recv_ts() -> None:
    backend = _backend_with_fake_session(adapter_recv_ts_ms=None)
    payload = json.loads(backend.get_state_json())

    assert payload["ec_ws_subscribed"] is False, (
        "first_recv_ts_ms == None ⇒ ec_ws_subscribed must be False "
        "(SSL ハンドシェイク前 / 失敗中 — 発注 gate は False で止める)"
    )
    # #85 code-review G#3: Unity JsonUtility は long フィールドの null 入力で例外を投げ得るため、
    # Python 側は 0 sentinel で常に int を emit する (None → 0 に coerce)。
    assert payload["last_event_ws_recv_ts_ms"] == 0
