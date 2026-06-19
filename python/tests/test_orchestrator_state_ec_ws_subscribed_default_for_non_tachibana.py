"""issue #85 Q4 #6: ec_ws_subscribed / last_event_ws_recv_ts_ms は venue-agnostic に
emit される。kabu や mock など TachibanaAdapter 固有 property を持たない adapter でも
AttributeError で落ちず、ec_ws_subscribed=False / last_event_ws_recv_ts_ms=None を返す
(findings 0053 §issue#85 改訂)。

実装は _backend_impl.get_state_json の model_copy(update=...) 経路で
``getattr(session.runner.adapter, "_ec_ws_first_recv_ts_ms", None) is not None`` のように
defensive read することで venue 非依存にする。
"""
from __future__ import annotations

import json
import types

from engine.core import DataEngine
from engine.live.state_machine import VenueStateMachine
from engine.mode_manager import ModeManager
from engine._backend_impl import DataEngineBackend


def test_non_tachibana_adapter_defaults_ec_ws_subscribed_to_false() -> None:
    """kabu 等の adapter は _ec_ws_first_recv_ts_ms / _ec_ws_last_recv_ts_ms を持たないが、
    get_state_json はそれでも AttributeError を出さず False / None を emit する。"""
    eng = DataEngine()
    venue_sm = VenueStateMachine()
    eng.state_machine = venue_sm
    mode_manager = ModeManager(venue_sm=venue_sm, replay_engine=eng)
    eng.attach_mode_manager(mode_manager)
    backend = DataEngineBackend(engine=eng, mode_manager=mode_manager, venue_sm=venue_sm)

    # 立花固有 attr を持たない minimal adapter。
    fake_adapter = types.SimpleNamespace()
    fake_runner = types.SimpleNamespace(adapter=fake_adapter, last_error=None)
    backend._live_mgr._session = backend._make_test_session(runner=fake_runner)

    payload = json.loads(backend.get_state_json())
    assert payload["ec_ws_subscribed"] is False
    # #85 code-review G#3: 0 sentinel (Unity JsonUtility long の null 例外回避)。
    assert payload["last_event_ws_recv_ts_ms"] == 0


def test_runner_without_adapter_attr_does_not_crash() -> None:
    """#85 code-review A#4 / C#1: runner.adapter が無い (kabu / mock の minimal SimpleNamespace)
    でも get_state_json は AttributeError で落ちず ec_ws_subscribed=False を emit する。"""
    eng = DataEngine()
    venue_sm = VenueStateMachine()
    eng.state_machine = venue_sm
    mode_manager = ModeManager(venue_sm=venue_sm, replay_engine=eng)
    eng.attach_mode_manager(mode_manager)
    backend = DataEngineBackend(engine=eng, mode_manager=mode_manager, venue_sm=venue_sm)

    # adapter 属性自体が無い runner (kabu の LiveRunner ラッパで .adapter が export されない場合)。
    fake_runner = types.SimpleNamespace(last_error=None)
    backend._live_mgr._session = backend._make_test_session(runner=fake_runner)

    payload = json.loads(backend.get_state_json())
    assert payload["ec_ws_subscribed"] is False
    assert payload["last_event_ws_recv_ts_ms"] == 0


def test_no_session_defaults_ec_ws_subscribed_to_false() -> None:
    """Replay only / pre-login の状態でも ec_ws_subscribed が emit され False になる。"""
    eng = DataEngine()
    venue_sm = VenueStateMachine()
    eng.state_machine = venue_sm
    mode_manager = ModeManager(venue_sm=venue_sm, replay_engine=eng)
    eng.attach_mode_manager(mode_manager)
    backend = DataEngineBackend(engine=eng, mode_manager=mode_manager, venue_sm=venue_sm)
    # _live_mgr._session は None のまま (init default)。

    payload = json.loads(backend.get_state_json())
    assert payload["ec_ws_subscribed"] is False
    assert payload["last_event_ws_recv_ts_ms"] == 0
