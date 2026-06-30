"""ReplayWindow — the slice of each per-instrument series that is currently visible in Replay.

Replay COLD-SEEDS the full scenario window into ``per_id_ohlc_points`` at load (#156 /
findings 0119 D-5) and the reducer dedupes-by-ts while streaming, so without clipping the
chart shows every bar from the first poll and ``bars_per_second`` only slows the Python loop
(the streamed top-level ``ohlc_points`` advances, but C# ChartView ignores it). While a run is
in flight (or has already streamed >=1 primary bar), each per-id series is clipped to the
streamed replay cursor — the latest streamed primary bar's time (``rs.timestamp_ms``, the
global replay clock; multi-instrument charts follow the same cursor). Before any bar streams
(LOADED pre-run / IDLE preview / cold-seed) the FULL series shows so the fit-all preview
(#156) is preserved.

A freshly cold-previewed instrument (``mark_preview``) is EXEMPT from the clip: a post-run
preview (scenario Commit / chart spawn / layout restore) re-seeds the full catalog while the
engine sits IDLE with a stale cursor (``force_stop_replay`` keeps ``_mode='replay'`` and the
streamed series), so the clip would otherwise leak in and drop the tail.

This is the SINGLE home for the watchable-playback concept (#182): the cursor gate, the clip,
and the preview-exemption set. ``engine.core`` calls it; it imports nothing from the host and
reads only plain scalars, so it is exercised directly through its own interface (the test
surface) instead of by driving the whole engine — see ``tests/test_replay_window.py``.
"""

from __future__ import annotations

from typing import Iterable, List, Optional, Protocol


class _HasOpenTime(Protocol):
    open_time_ms: int


class ReplayWindow:
    def __init__(self) -> None:
        # Instruments whose per-id series was (re)seeded by an IDLE cold preview and must show
        # their full period even when the run-end clip is otherwise armed.
        self._preview_iids: set[str] = set()

    def rearm(self) -> None:
        """A new scenario (``load_replay_data``) re-arms the run path; drop all exemptions so the
        fresh cold-seed streams and clips normally."""
        self._preview_iids.clear()

    def mark_preview(self, iid: str) -> None:
        """Exempt ``iid``: its series was (re)seeded by an IDLE cold preview (full period)."""
        self._preview_iids.add(iid)

    def forget(self, iid: str) -> None:
        """Drop ``iid``'s exemption when the instrument is unsubscribed/forgotten."""
        self._preview_iids.discard(iid)

    def visible_cursor_ms(
        self, *, mode: str, replay_state: str, streamed: bool, timestamp_ms: int
    ) -> Optional[int]:
        """The latest visible bar time, or ``None`` to show the full series.

        Clip only in Replay once a run is RUNNING or has already streamed >=1 bar. The gate is
        "RUNNING or already-streamed", NOT RUNNING-only: the host reverts RUNNING->IDLE at run
        end (``force_stop_replay``) and an observation-only cell that ``break``s early must keep
        showing only the bars it streamed — not snap back to the full series.

        Gated on ``mode=='replay'`` because clipping is a Replay concept (cold-seed + streamed
        cursor). Live never cold-seeds, so the mode guard makes "Replay only" explicit and
        protects Live from silent bar loss.
        """
        if mode == "replay" and (replay_state == "RUNNING" or streamed):
            return timestamp_ms
        return None

    def clip(
        self, iid: str, points: Iterable[_HasOpenTime], cursor_ms: Optional[int]
    ) -> List[_HasOpenTime]:
        """Trim ``points`` to those at/under ``cursor_ms``. ALWAYS returns a fresh list (callers
        may store it without re-copying), so it never aliases the engine's per-id buffer. The full
        series is returned when there is no cursor (full series) or ``iid`` is preview-exempt."""
        if cursor_ms is None or iid in self._preview_iids:
            return list(points)
        return [p for p in points if p.open_time_ms <= cursor_ms]
