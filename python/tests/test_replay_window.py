"""ReplayWindow unit tests — exercise the watchable-playback concept (#182) directly through
its own interface (the test surface) instead of by driving the whole engine.

The end-to-end witness that this clip reaches the real poll path stays in
test_replay_chart_watchable_playback.py (REPLAY-PLAY-01..06); here we pin the gate/clip/exempt
rules in isolation so a regression points straight at ReplayWindow."""

from dataclasses import dataclass

import pytest

from engine.replay_window import ReplayWindow


@dataclass
class _Pt:
    open_time_ms: int


def _pts(*ts: int) -> list[_Pt]:
    return [_Pt(t) for t in ts]


# ---- visible_cursor_ms: the gate ----

def test_replay_running_clips_to_cursor():
    w = ReplayWindow()
    assert w.visible_cursor_ms(
        mode="replay", replay_state="RUNNING", streamed=False, timestamp_ms=500
    ) == 500


def test_replay_streamed_after_run_still_clips():
    # Host reverts RUNNING->IDLE at run end; an already-streamed series must stay clipped.
    w = ReplayWindow()
    assert w.visible_cursor_ms(
        mode="replay", replay_state="IDLE", streamed=True, timestamp_ms=900
    ) == 900


def test_replay_pre_run_shows_full_series():
    # LOADED/IDLE pre-run, nothing streamed yet → no clip (fit-all preview preserved).
    w = ReplayWindow()
    assert w.visible_cursor_ms(
        mode="replay", replay_state="IDLE", streamed=False, timestamp_ms=1
    ) is None


@pytest.mark.parametrize("state", ["IDLE", "RUNNING"])
def test_live_never_clips(state):
    # Live never cold-seeds; the mode guard makes "Replay only" explicit.
    w = ReplayWindow()
    assert w.visible_cursor_ms(
        mode="static", replay_state=state, streamed=True, timestamp_ms=900
    ) is None


# ---- clip + preview exemption ----

def test_clip_trims_points_at_or_under_cursor():
    w = ReplayWindow()
    out = w.clip("8918.TSE", _pts(10, 20, 30, 40), cursor_ms=25)
    assert [p.open_time_ms for p in out] == [10, 20]


def test_clip_none_cursor_returns_full_series():
    w = ReplayWindow()
    out = w.clip("8918.TSE", _pts(10, 20, 30), cursor_ms=None)
    assert [p.open_time_ms for p in out] == [10, 20, 30]


def test_marked_preview_iid_is_exempt_from_clip():
    w = ReplayWindow()
    w.mark_preview("8918.TSE")
    out = w.clip("8918.TSE", _pts(10, 20, 30, 40), cursor_ms=25)
    assert [p.open_time_ms for p in out] == [10, 20, 30, 40]  # full series despite cursor


def test_forget_drops_exemption():
    w = ReplayWindow()
    w.mark_preview("8918.TSE")
    w.forget("8918.TSE")
    out = w.clip("8918.TSE", _pts(10, 20, 30, 40), cursor_ms=25)
    assert [p.open_time_ms for p in out] == [10, 20]


def test_rearm_clears_all_exemptions():
    w = ReplayWindow()
    w.mark_preview("A")
    w.mark_preview("B")
    w.rearm()
    # After rearm neither A nor B is exempt → both clip at the cursor (vs. literal, not self).
    assert [p.open_time_ms for p in w.clip("A", _pts(10, 20, 30), cursor_ms=15)] == [10]
    assert [p.open_time_ms for p in w.clip("B", _pts(10, 20, 30), cursor_ms=15)] == [10]
