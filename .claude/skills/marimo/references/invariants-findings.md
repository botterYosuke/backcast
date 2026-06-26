# marimo invariants, gotchas & decision index

The load-bearing rules that make backcast's marimo embedding correct, plus the findings/ADR trail.
Violating one of these is how marimo work silently breaks. Read the cited finding before changing
anything it governs.

## §version-skew (read first)
The source mirror at `.claude/skills/marimo/marimo/` is **v0.23.11**; the project runs **v0.20.4**
(`python/.venv/.../site-packages/marimo/`, `uv.lock`, `__generated_with` in strategies). Private
APIs backcast couples to (`_find_cells_for_state`, `Runner.compute_cells_to_run` signature,
`get_executor`, `App.embed`, `generate_filecontents`) can differ between these. **For exact
signatures, grep the installed 0.20.4 copy, not the mirror.** marimo is range-pinned `>=0.20.4`
(ADR-0012 D3) with `uv.lock` + the thin_drain tests as the drift safety net — re-run
`test_strategy_runtime_thin_drain.py` after any marimo upgrade.

## Load-bearing invariants

| Invariant | Source | Why it matters |
|-----------|--------|----------------|
| **Lazy-import discipline.** The runtime seam (`cell_api.py`, `marimo_strategy.py` top-level, `_backend_impl.py` outside the marimo branch) must NOT import marimo at module load. | ADR-0012 D4; `test_strategy_runtime_offline.py` | The engine/kernel must run without marimo installed; only the strategy layer pulls it in. |
| **marimo Kernel/RuntimeContext are thread-local.** One kernel per notebook, built+driven on ONE dedicated worker thread. | findings 0071:29-33; 0080 | Touching the kernel from another thread corrupts state. Per-cell RUN serializes on its worker thread. |
| **hot path uses bare `exec`; private marimo APIs stay in the cold path.** | findings 0046:100-102 | Confines version-fragile private-API use to compile/edit; per-bar drain is plain Python = stable + fast. |
| **Call marimo's `compute_cells_to_run`; don't reimplement topo/closure.** | findings 0046:32-41 (D2) | Hand-rolled dirty-set/topo silently diverges from marimo's reactive semantics across versions. |
| **bar-crossing feedback only via `mo.state`** (writer `set_x`/reader `get_x`); within-bar via variable dataflow. | ADR-0012 D1; findings 0046:24-31 | State passing creates no def/ref edge, so it carries values across bars without wiring the graph. |
| **driver-state must be host-declared explicitly** — no auto-detecting all `mo.state` as drivers. | ADR-0012 D5; findings 0046:64-69 | Auto-detect sweeps forward-unconnected consumers into the hot list (D4 would reject them). |
| **fail-closed is structural:** if a cell-id outside the precomputed hot list fires, reject — judged by new cid, not output. | ADR-0012 D3; findings 0046:43-48 | Detects a strategy whose reactive shape changed under us, instead of silently running unvetted cells. |
| **per-bar cells still install an execution context** (`with_cell_id`) so every cell behaves like a normal marimo cell. | findings 0046:176-187 (D6) | Without it, `mo.output`/`mo.ui` in a per-bar cell silently no-op (footgun 0046:81-96). |
| **`mo.output`/`mo.ui` silently no-op without a runtime context.** | findings 0046:81-96 | Explains "my cell produced nothing" — the context wasn't installed. |
| **Open is marimo-only; non-marimo `.py` is rejected at Open, not auto-wrapped.** | findings 0098 (reverses 0054); ADR-0025 D4 | `decompose_json(..., raise_syntax_error=True)`: non-marimo → `None`; broken syntax → raises. Distinct errors. |
| **Empty notebook persistence floor = 1 cell.** `load_app` inflates a header-only `.py` into one empty cell. | findings 0114; ADR-0033 | 0 cells allowed in-session, but Save→Open round-trips 0→1 (marimo's own behavior, not a bug to "fix"). |
| **run-guard anchoring:** `@app.cell` after `if __name__ == "__main__": app.run()` is silently dropped. | findings 0049:29-32 | Cell-insert must anchor at marimo's collection terminus or appended cells vanish. |
| **`__file__` is not anchored in per-cell RUN** — source arrives as text with no disk path. | findings 0089 | `_artifacts`-style cells using `Path(__file__).parent` break; backend must wire `strategy_path` and inject `__file__`. |
| **no GIL-yield floor in the hot loop** (no per-bar sleep). Replay↔Auto relies on the 5ms auto-switch. | findings 0070:79-103 | A per-bar sleep to free the GIL was owner-rejected ("worst — binding the engine to Hakoniwa's convenience"). |
| **cold-run does not populate `mo.output`/`ui` cells** — `load_app(all)` doesn't populate globals; contract gates call the bare executor directly. | findings 0046:135 | Don't assert cold-run output for output/ui cells. |

## Cell-skeleton convention
Inserted skeleton cells use `def _():` with all-`_` locals and a `_qty = 0.0` no-op default
(findings 0049:34-40). The `_` prefix makes them cell-local (marimo mangles locals by cell id),
so repeated insertion never trips multiple-definition.

## Findings index (read the file before touching its area)
| Finding | Topic |
|---------|-------|
| `docs/findings/0010-strategy-editor.md` | Editor buffer / syntax highlight / undo-redo architecture |
| `docs/findings/0046-marimo-embed-thin-drain-runtime.md` | thin-drain hot path, AC1 perf, D1–D6 (the core runtime doc) |
| `docs/findings/0049-marimo-cell-add-affordance.md` | cell [+] insert UI, run-guard anchor, skeleton convention |
| `docs/findings/0070-notebook-equals-backtest-grill.md` | notebook = backtest design tree (B2+B3) |
| `docs/findings/0071-notebook-foundation-per-cell-run.md` | per-cell RUN foundation, thread-locality |
| `docs/findings/0089-v19-marimo-cell-file-anchor.md` | `__file__` anchoring in per-cell run |
| `docs/findings/0092-issue112-marimo-cell-live-mode-aware-bt.md` | Replay/Auto live cell driving, rendezvous |
| `docs/findings/0098-issue113-open-layer-marimo-only.md` | Open = marimo-only (reverses 0054) |
| `docs/findings/0114-strategy-notebook-zero-cells.md` | 0-cell allowed, floor=1 persistence |
| `docs/findings/0115-issue141-145-cell-driven-dynamic-universe.md` | `bt.universe.*` dynamic universe API |
| `docs/findings/0054-...` | **SUPERSEDED by 0098** (do not follow) |

## ADR index
| ADR | Topic |
|-----|-------|
| ADR-0012 | marimo-embed reactive strategy execution model (D1 dataflow/state, D2 precompute, D3 fail-closed, D4 lazy-import, D5 host-declared drivers) |
| ADR-0013 | cell-as-floating-window; `IMarimoSynthesizer` seam (D3 C# never does def/ref) |
| ADR-0016 | notebook = backtest, per-cell RUN as execution entry |
| ADR-0025 | marimo cell drives both Replay and live mode-aware `bt` (D4 marimo-or-error) |
| ADR-0031 | cell-driven dynamic universe `bt.universe.*` |
| ADR-0033 | strategy notebook allows zero cells (floor=1 persist; supersedes ADR-0013 D5) |
