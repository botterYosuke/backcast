# backcast ⇄ marimo integration seams

This is the **project side** of the boundary — backcast's own code that calls into marimo.
For the marimo source itself, see [marimo-internals.md](marimo-internals.md). For the *why*
behind these seams, see [invariants-findings.md](invariants-findings.md).

All paths are under `/Users/sasac/backcast`. Read the file before editing — these summaries
go stale; the code is the source of truth.

## The two execution paths

marimo strategies run through **two different code paths** depending on whether you're
compiling/editing (cold) or backtesting per-bar (hot). Conflating them is the #1 source of bugs.

| Path | When | Mechanism | marimo coupling |
|------|------|-----------|-----------------|
| **cold** | compile, edit, per-cell RUN, Open/Save | full marimo `Kernel`/`Runner` orchestration, outputs + UI visible | heavy — private APIs |
| **hot** | per-bar `on_bar` in a backtest | host-owned "thin-drain": precomputed static cell list, bare `exec`/`eval` per bar | minimal — `with_cell_id` context only |

## Python seams

### Detection & dispatch
- `python/engine/strategy_runtime/strategy_kind.py` — `is_marimo_app_file()` / `is_marimo_app_source()`.
  AST scan for module-level `app = marimo.App()`. **No marimo import** (pure AST). This is the
  branch point between the marimo path and the imperative `Strategy` path.
- `python/engine/_backend_impl.py` — `_select_replay_strategy()`, `run_cell()`. Lazy conditional
  dispatch: marimo branch → `MarimoStrategy`; otherwise imperative `Strategy`. marimo is imported
  **only inside the marimo branch** (lazy-import discipline, ADR-0012 D4).

### Hot path (thin-drain)
- `python/engine/strategy_runtime/thin_drain.py` — the core. `HeadlessKernel`, `CompiledStrategy`,
  `StrategyRuntime`, `open_runtime()`, `_compile()`, `_execute_hot_cell()`.
  - **Cold compile (once):** stands up `HeadlessKernel` (= `Kernel` + thread-local `RuntimeContext`),
    runs all cells once, then asks marimo which cells are reactive roots of the driver state via
    `kernel._find_cells_for_state(state, "__external__")`, and computes the static hot list via
    `Runner.compute_cells_to_run(graph, roots, set(), "autorun")` (marimo @staticmethod — do **not**
    reimplement topo/closure yourself; call marimo's function so behavior tracks the version).
  - **Hot drain (per bar):** `setters[name](value)` writes driver `mo.state`, then for each precomputed
    hot cell `_execute_hot_cell()` runs the cell body under `get_context().with_cell_id(cid)` and bare
    `executor.execute_cell(body)`. ~0.6–3µs/bar (native), vs ~4.4ms/bar for full `Kernel.run`.
- `python/engine/strategy_runtime/marimo_strategy.py` — `MarimoStrategy` adapter. `on_start()` lazily
  imports `thin_drain.open_runtime()`; `on_bar()` calls `rt.drain(...)`. **Top-level stays marimo-free.**
- `python/engine/strategy_runtime/cell_api.py` — `make_submit_market()`. Marimo-free seam that adapts
  the kernel's submit contract to plain Python callables injected into the cell namespace.

### Cold path (per-cell RUN, live cells)
- `python/engine/strategy_runtime/notebook_session.py` — `NotebookSession`, `IncrementalNotebookSession`,
  `_BridgedConsoleHook`, `_ConsoleCapture`. Drives a pressed cell + its reactive downstream and captures
  output. Uses `ExecuteCellCommand`, `Runner`, `create_default_hooks()`, `try_format`. **One kernel per
  notebook, lived on one dedicated worker thread** (marimo Kernel/RuntimeContext are thread-local).
- `python/engine/strategy_runtime/live_cell_runtime.py` — `build_live_marimo_loader()`,
  `_make_cell_runner()`, `_make_bridge_factory()`. Replay↔Auto live driving of cells (lock-step
  rendezvous; see findings 0092 / ADR-0025).

### Synthesis / decomposition (C# cells ⇄ `.py`)
- `python/engine/strategy_runtime/cell_synthesis.py` — `synthesize_json()`, `decompose_json()`,
  `decompose_for_open()`, `load_app_from_text()`. Uses `marimo._ast.codegen.generate_filecontents`,
  `marimo._ast.load.load_app`, `marimo._ast.cell.CellConfig`.
  - `synthesize_json(cells_json)` → `marimo.App()` header + `@app.cell` defs + run-guard footer.
  - `decompose_for_open(py_text)` → `load_app` (temp-file dance) → cells via `app._cell_manager`.
  - **`raise_syntax_error=True`** distinguishes non-marimo (`None`) from broken-syntax (raises) — see 0098.

## C# (Unity) seams

- `Assets/Scripts/StrategyEditor/IMarimoSynthesizer.cs` — interface `Synthesize()` / `Decompose()`.
  C# never implements def/ref analysis — that's marimo's job over the seam (ADR-0013 D3).
- `Assets/Scripts/StrategyEditor/PythonnetMarimoSynthesizer.cs` — production impl, calls
  `cell_synthesis.py` through `WorkspaceEngineHost`'s single GIL.
- `Assets/Editor/FakeMarimoSynthesizer.cs` — AFK test double (Python-free round-trip, mirrors marimo's
  `floor=1` empty-notebook behavior).
- `Assets/Scripts/StrategyEditor/MarimoNotebookDocument.cs` — notebook aggregate (cell model, dirty,
  Save/Open).
- `Assets/Scripts/Live/WorkspaceEngineHost.cs` — single Python owner / GIL; `SynthesizeCells()`,
  `DecomposeCells()`, `InvokeRunCell()`.

## Example strategy
- `python/strategies/v19/v19_morning_cell.py` — a full marimo cell-DAG strategy
  (`__generated_with = "0.20.4"`). Good reference for what an authored marimo strategy looks like:
  `marimo.App()`, `@app.cell` functions, free-ref reads of `bt` / `submit_market` / service callables.

## Test anchors (run these on any marimo change)
- `python/tests/test_strategy_runtime_thin_drain.py` — AC1 perf, D2 precompute, D6 context, mo.state reactivity.
- `python/tests/test_strategy_runtime_offline.py` — **lazy-import discipline**: module load must NOT import marimo.
- `python/tests/test_notebook_interactive_run.py` — per-cell RUN downstream recompute.
- `python/tests/test_marimo_cell_synthesis_golden.py` — `decompose(synthesize(cells))` round-trip idempotence.
- `python/tests/test_marimo_strategy_adapter.py` — `MarimoStrategy` integration.

Run: `cd python && uv run pytest tests/test_strategy_runtime_thin_drain.py -v`
