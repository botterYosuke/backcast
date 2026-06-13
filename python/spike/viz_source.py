"""Viz spike — numpy ndarray producer for zero-copy GPU interop (issue #8, Phase 2).

The C# host (Phase 4) owns a CAS 4-state handshake and the GraphicsBuffer
upload; this module is the Python *producer* half. A worker thread generates,
under the GIL, a fresh ``np.sin`` ndarray every frame (float32, C-contiguous,
itemsize 4) and exposes its raw buffer (ptr / len / dtype) so C# can wrap it
with ``ConvertExistingDataToNativeArray`` and ``SetData`` it onto the GPU with
NO app-layer copy.

Lifetime: Python owns the ndarray. ``generate_frame()`` keeps a reference to the
just-produced slot array (so the buffer stays valid while C# reads it) and
``release_frame(generation)`` drops exactly one outstanding slot. C# releases a
slot only after ``SetData`` returns (Reading->Free) OR when latest-wins drops an
older Ready frame; both calls land on the worker thread under the GIL, so the
slot map is touched by a single thread and needs no lock. The CAS state machine
itself lives in C#; Python here only does per-frame generation + self-check.

Self-failing gate (mirrors python/spike/s0_backtest.py and s1_adapter_smoke.py):
every generated frame is self-checked — float32 / C-contiguous / itemsize==4 /
ptr non-zero & 4-byte aligned — and raises VizSourceError on any mismatch.
``run_gates()`` runs the check on the CURRENT interpreter without sys.exit, so
the in-process Mono host catches a bad frame as a Python exception on the same
interpreter (instead of a hard crash); ``main()`` is a headless smoke that loops
N frames, generating + releasing, and prints a PASS/FAIL line.
"""

from __future__ import annotations

import sys

import numpy as np

# ---------------------------------------------------------------------------
# Pins / constants
# ---------------------------------------------------------------------------

EXPECTED_DTYPE = np.float32
EXPECTED_ITEMSIZE = 4  # bytes; float32
ALIGN_BYTES = 4  # GraphicsBuffer(Structured, len, stride=4) wants 4-byte alignment

DEFAULT_POINTS = 4096
PHASE_STEP = 0.05  # radians advanced per frame, so the sine visibly animates

# Headless smoke loops this many frames (GREEN wants generated>=300).
SMOKE_FRAMES = 300


class VizSourceError(RuntimeError):
    """Raised by a self-failing viz gate on any frame/slot violation.

    Gate code raises this instead of calling sys.exit so an in-process host
    (Unity Mono / pythonnet) catches a malformed frame as a Python exception on
    the SAME interpreter that will keep generating frames. The headless main()
    catches it, prints the message and sys.exit(1)s.
    """


def _assert_frame(arr: np.ndarray) -> None:
    """Self-check one freshly produced frame. Raises VizSourceError on mismatch.

    Covers the Python-side half of the zero-copy proof (asserts 1-4): dtype is
    float32, the buffer is C-contiguous, itemsize is 4 bytes, and the data
    pointer is non-zero and 4-byte aligned. The C#-side asserts (ptr aliasing,
    setDataCalls accounting, byte totals) live in Phase 4.
    """
    failures: list[str] = []

    if arr.dtype != EXPECTED_DTYPE:
        failures.append(f"dtype={arr.dtype}, expected {np.dtype(EXPECTED_DTYPE)}")
    if not arr.flags["C_CONTIGUOUS"]:
        failures.append("buffer is not C-contiguous")
    if arr.itemsize != EXPECTED_ITEMSIZE:
        failures.append(f"itemsize={arr.itemsize}, expected {EXPECTED_ITEMSIZE}")

    ptr = arr.ctypes.data
    if ptr == 0:
        failures.append("data pointer is NULL (0)")
    elif ptr % ALIGN_BYTES != 0:
        failures.append(f"data pointer {ptr} is not {ALIGN_BYTES}-byte aligned")

    if failures:
        raise VizSourceError(
            "[VIZ SOURCE FAIL]\n" + "\n".join(f"  - {f}" for f in failures)
        )


class VizSource:
    """Per-frame np.sin producer that hands raw buffers to the C# consumer.

    Single-threaded under the GIL: the worker thread calls generate_frame() and
    release_frame() (and nothing else does), so the outstanding-slot map needs
    no lock.
    """

    def __init__(self, n_points: int = DEFAULT_POINTS) -> None:
        n = int(n_points)
        if n <= 0:
            raise VizSourceError(f"[VIZ SOURCE FAIL] n_points={n}, expected > 0")
        self._n = n
        self._phase = 0.0
        # generation -> ndarray. Holds a reference to every outstanding slot so
        # its buffer stays valid until C# explicitly releases it.
        self._slots: dict[int, np.ndarray] = {}
        self.generated = 0

    @property
    def n_points(self) -> int:
        return self._n

    def live_slots(self) -> int:
        """Number of outstanding (generated but not yet released) frames."""
        return len(self._slots)

    def generate_frame(self) -> dict:
        """Produce a fresh np.sin frame, self-check it, retain it, return its meta.

        np.sin of a float32 C-contiguous input yields a float32 C-contiguous
        output in a single allocation (no app-layer copy). The returned dict is
        what the C# side wraps: ptr is the raw data address C# must see aliased
        to its NativeArray (assert 5), length/itemsize feed the GraphicsBuffer
        (the C# side derives the byte total as length*itemsize).
        """
        n = self._n
        x = np.arange(n, dtype=EXPECTED_DTYPE) * (2.0 * np.pi / n) + np.float32(
            self._phase
        )
        arr = np.sin(x)  # float32 in -> float32 out, C-contiguous, fresh alloc

        _assert_frame(arr)

        self.generated += 1
        generation = self.generated
        self._slots[generation] = arr  # keep alive until release_frame()
        self._phase += PHASE_STEP

        return {
            "generation": generation,
            "ptr": int(arr.ctypes.data),
            "length": int(arr.shape[0]),
            "itemsize": int(arr.itemsize),
            "dtype": str(arr.dtype),
            "c_contiguous": bool(arr.flags["C_CONTIGUOUS"]),
        }

    def release_frame(self, generation: int) -> None:
        """Drop the retained slot for ``generation`` (worker thread, under GIL).

        Called by C# after SetData returns, or when latest-wins drops an older
        Ready frame. Releasing an unknown/already-released generation is a bug in
        the handshake and fails the gate loudly.
        """
        if self._slots.pop(int(generation), None) is None:
            raise VizSourceError(
                f"[VIZ SOURCE FAIL] release of unknown/duplicate generation "
                f"{generation} (live={sorted(self._slots)})"
            )


def run_gates(n_points: int = DEFAULT_POINTS) -> None:
    """Run the producer self-check on the CURRENT interpreter (no sys.exit).

    Public entry for the in-process host (Unity Mono / pythonnet): generates a
    couple of frames, proving each is float32 / C-contiguous / itemsize==4 with a
    non-zero 4-byte-aligned ptr (via generate_frame's _assert_frame), that
    successive frames are FRESH allocations (distinct ptrs => "new ndarray every
    frame"), and that release_frame drains the outstanding slot map back to zero.
    Raises VizSourceError on any mismatch; returns None on success.
    """
    src = VizSource(n_points)

    f0 = src.generate_frame()
    f1 = src.generate_frame()

    if f0["ptr"] == f1["ptr"]:
        raise VizSourceError(
            "[VIZ SOURCE FAIL] two consecutive frames share a data pointer "
            f"({f0['ptr']}) — frames are not freshly allocated per frame"
        )
    if src.live_slots() != 2:
        raise VizSourceError(
            f"[VIZ SOURCE FAIL] live_slots={src.live_slots()} after 2 generates, "
            "expected 2"
        )

    src.release_frame(f0["generation"])
    src.release_frame(f1["generation"])
    if src.live_slots() != 0:
        raise VizSourceError(
            f"[VIZ SOURCE FAIL] live_slots={src.live_slots()} after releasing all, "
            "expected 0"
        )

    print(
        f"[VIZ SOURCE OK] points={src.n_points} | float32 C-contig itemsize=4 | "
        f"ptr aligned/4 nonzero | fresh-per-frame | release drains slots"
    )


def main() -> None:
    """Headless smoke: loop SMOKE_FRAMES generate+release passes, print PASS/FAIL."""
    try:
        run_gates()

        src = VizSource(DEFAULT_POINTS)
        for _ in range(SMOKE_FRAMES):
            meta = src.generate_frame()
            # Model the C# consumer: read happens here, then release under GIL.
            src.release_frame(meta["generation"])

        if src.generated != SMOKE_FRAMES:
            raise VizSourceError(
                f"[VIZ SOURCE FAIL] generated={src.generated}, expected {SMOKE_FRAMES}"
            )
        if src.live_slots() != 0:
            raise VizSourceError(
                f"[VIZ SOURCE FAIL] {src.live_slots()} slot(s) leaked after smoke loop"
            )
    except VizSourceError as exc:
        print(exc)
        sys.exit(1)
    except Exception as exc:  # noqa: BLE001 — any unexpected error fails the gate
        print(f"[VIZ SOURCE FAIL] unexpected error: {exc!r}")
        sys.exit(1)

    print(
        f"[VIZ SOURCE PASS] generated={src.generated} points={src.n_points} "
        f"numpy={np.__version__}"
    )
    sys.exit(0)


if __name__ == "__main__":
    main()
