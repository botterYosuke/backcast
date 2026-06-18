"""THROWAWAY spike (#81 Slice 1) — lock the C#=spatial / Python(marimo)=synthesis seam.

Owner froze the design on calling marimo's native codegen directly in-proc:
  C# holds raw cell *body* strings (one per window) + window positions;
  Python composes/decomposes via generate_filecontents / load_app.

This spike proves the load-bearing claims BEFORE the seam is frozen:
  (a) generate_filecontents([body,body,body]) -> a .py that load_app collects as 3 cells;
  (b) load_app gives the *bodies* back (the window content), wrapper hidden;
  (c) round-trip is byte-idempotent: bodies -> .py -> bodies -> .py == first .py;
  (d) observe the canonical-form divergence (__generated_with version line + run-guard footer)
      vs backcast's current footer-less form, and whether the version line lands in output.

Run: uv run --group spike python -m spike.marimo_cell_synthesis_spike
"""
from __future__ import annotations

import tempfile
from pathlib import Path

from marimo._ast.cell import CellConfig
from marimo._ast.codegen import generate_filecontents
from marimo._ast.load import load_app

# 3 raw cell bodies as a C# window would hold them — NO @app.cell / def _ / return.
# Cell 2 references `px` defined by cell 1 (a real DAG edge: refs={px}); cell 3 submits.
BODIES = [
    "px = get_bar().close  # noqa: F821",
    "signal = 1.0 if px > 1010.0 else -1.0\nqty = signal * 10.0",
    "submit_market(qty)  # noqa: F821",
]
NAMES = ["_", "_", "_"]
CONFIGS = [CellConfig(), CellConfig(), CellConfig()]


def synth(codes, names, configs):
    return generate_filecontents(codes=list(codes), names=list(names),
                                 cell_configs=list(configs), config=None)


def main() -> None:
    ok = True

    gen1 = synth(BODIES, NAMES, CONFIGS)
    print("===== generate_filecontents OUTPUT =====")
    print(gen1)
    print("========================================")

    has_version = "__generated_with" in gen1
    has_runguard = 'if __name__ == "__main__":' in gen1
    print(f"[OBS] __generated_with present: {has_version}")
    print(f"[OBS] run-guard footer present: {has_runguard}")
    # The DAG: cell 2 must take px as a hidden arg, cell 1 must return px.
    print(f"[OBS] cell2 takes hidden ref (def _(px)): {'def _(px):' in gen1}")
    print(f"[OBS] cell1 returns def (return (px,)): {'return (px,)' in gen1}")

    with tempfile.TemporaryDirectory() as d:
        p = Path(d) / "nb.py"
        p.write_text(gen1, encoding="utf-8")

        app = load_app(p)
        if app is None:
            print("[FAIL] load_app returned None"); return
        codes = list(app._cell_manager.codes())
        names = list(app._cell_manager.names())
        configs = list(app._cell_manager.configs())

        print(f"[CHK a] load_app collected {len(codes)} cells (want 3): "
              f"{'PASS' if len(codes) == 3 else 'FAIL'}")
        ok &= len(codes) == 3

        print("[CHK b] recovered bodies (wrapper hidden?):")
        for i, c in enumerate(codes):
            print(f"  --- cell {i} ---\n{c}")
        bodies_match = [c.strip() == BODIES[i].strip() for i, c in enumerate(codes)]
        print(f"[CHK b] bodies match input (stripped): {bodies_match} -> "
              f"{'PASS' if all(bodies_match) else 'FAIL'}")
        ok &= all(bodies_match)

        # (c) idempotency: re-synthesize from the recovered bodies/names/configs.
        gen2 = synth(codes, names, configs)
        idem = gen2 == gen1
        print(f"[CHK c] byte-idempotent round-trip (bodies->py->bodies->py): "
              f"{'PASS' if idem else 'FAIL'}")
        if not idem:
            # show first divergence
            for i, (a, b) in enumerate(zip(gen1.splitlines(), gen2.splitlines())):
                if a != b:
                    print(f"  first diff @ line {i}:\n    gen1: {a!r}\n    gen2: {b!r}")
                    break
            if len(gen1.splitlines()) != len(gen2.splitlines()):
                print(f"  line count: gen1={len(gen1.splitlines())} gen2={len(gen2.splitlines())}")
        ok &= idem

    print(f"\n[SPIKE #81 seam] {'ALL PASS' if ok else 'FAIL'}")


if __name__ == "__main__":
    main()
