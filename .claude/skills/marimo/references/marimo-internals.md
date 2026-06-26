# marimo internals — source navigation map

This maps the **upstream marimo source mirror** checked out at
`/Users/sasac/backcast/.claude/skills/marimo/marimo/` so you can jump to the right file fast.

> ⚠️ **Version skew.** This mirror is **v0.23.11**. The project actually runs **v0.20.4**
> (`python/.venv/.../site-packages/marimo/`, `uv.lock`, `__generated_with`). For anything
> load-bearing (private API signatures, embed semantics, executor internals) **cross-check the
> installed 0.20.4 source**, because private APIs drift between minor versions. Use the mirror to
> understand *concepts and structure*; trust the installed copy for *exact signatures backcast
> couples to*. See [invariants-findings.md](invariants-findings.md) §version-skew.

Paths below are relative to `.claude/skills/marimo/marimo/`.

## 1. App / notebook API (defining & embedding)
- `_ast/app.py` — `App`, `InternalApp`, `App.embed()` (→ `AppEmbedResult`), `@app.cell` decorator,
  `_get_kernel_runner()`, `AppKernelRunnerRegistry` (thread-local kernel runners for isolated embeds).
- `_ast/cell.py` — `Cell`, `CellImpl` (frozen: code/defs/refs/body/last_expr/config/language),
  `CellConfig`, `RuntimeState`, `RunResultStatus`.
- `_ast/cell_manager.py` — `CellManager`: cell registration, IDs, `.codes()`/`.names()`/`.configs()`.
- `_ast/codegen.py` — `generate_filecontents()` (cells → `.py` source). backcast's `synthesize_json` calls this.
- `_ast/load.py` — `load_app()` (`.py` → `App`). backcast's `decompose_for_open` calls this.

`App.embed()` shape (conceptual — verify against 0.20.4):
```python
async def embed(self, defs=None) -> AppEmbedResult:
    runner = self._get_kernel_runner()                              # thread-isolated
    cells = dataflow.prune_cells_for_overrides(self._graph, self._execution_order, defs or {})
    outputs, glbls = await runner.run(cells)                        # nested kernel, child context
```

## 2. Reactive dataflow engine (the heart)
- `_runtime/dataflow/graph.py` — `DirectedGraph` (thread-safe coordinator). **Start here** to understand reactivity.
- `_runtime/dataflow/topology.py` — `GraphTopology` / `MutableGraphTopology`: cells, parent/child edges, ancestry.
- `_runtime/dataflow/definitions.py` — `DefinitionRegistry`: which cell defines which variable; name conflicts.
- `_runtime/dataflow/edges.py` — edge computation from var refs/defs.
- `_runtime/dataflow/cycles.py` — `CycleTracker`.
- `_ast/visitor.py` — `ScopedVisitor`, `VariableData`, `ImportData`: AST extraction of defs/refs/imports.
- `_ast/variables.py` — local-variable mangling (`_x` → cell-prefixed) so cell-locals don't leak / collide.
  This is why findings 0049 uses `_`-prefixed skeleton cells to avoid multiple-definition errors.

## 3. Kernel & execution
- `_runtime/runtime.py` — `Kernel`: graph + globals + callbacks + request routing. `_find_cells_for_state()`.
- `_runtime/app/kernel_runner.py` — `AppKernelRunner`: runs an app in an isolated `KernelRuntimeContext`.
- `_runtime/runner/cell_runner.py` — `Runner` (a.k.a. `CellRunner`), `RunResult`.
  `Runner.compute_cells_to_run(graph, roots, excluded, "autorun")` (**@staticmethod**) → dirty-set + topo order.
  This is the exact call backcast's thin-drain uses to compute its hot cell list.
- `_runtime/runner/scheduler.py` — `SequentialScheduler`: FIFO queue, cancellation, nested-run stack.
- `_runtime/executor/executor.py` — `DefaultExecutor` / `Executor`. `execute_cell(body)` = `exec` statements
  then `eval` last expr; strips marimo frames from tracebacks.
- `_runtime/executor/__init__.py` — `get_executor(ExecutionConfig())`.
- `_runtime/context/types.py` — `get_context()`, `ExecutionContext`, `RuntimeContext`, `with_cell_id()`,
  `runtime_context_installed()`, `ContextNotInitializedError`.
- `_runtime/context/kernel_context.py` — `KernelRuntimeContext`, `create_kernel_context()` (parent-child linking).

Cell execution core (conceptual):
```python
# DefaultExecutor.execute_cell
exec(cell.body, glbls)          # statements
return eval(cell.last_expr, glbls)   # last expression is the cell's value
```

## 4. State & reactivity primitives
- `_runtime/state.py` — `State`, `StateRegistry`. `marimo.state(initial)` → `(getter, setter)`. Setting state
  marks reader cells dirty. backcast uses this for **bar-crossing feedback** (writer `set_x` / reader `get_x`)
  because passing values via state does **not** create a dataflow edge (no shared def/ref).
- `_runtime/watch/__init__.py` — `watch()` reactive dependency.

## 5. UI elements (`mo.ui.*`)
- `_plugins/ui/_core/ui_element.py` — `UIElement(Html, Generic[S, T])`: `_value_frontend: S` (JSON) vs
  `_value: T` (Python); `_convert_value()` transforms frontend→Python. Changing a UI value enqueues an
  update command that marks dependent cells dirty.
- `_plugins/ui/_core/registry.py` — `UIElementRegistry`. `_plugins/ui/_impl/` — 40+ concrete widgets.

## 6. Outputs & markdown
- `_output/md.py` — `md()` (math, iconify, pycon detection).
- `_output/formatting.py` — `try_format()`, `as_html()`, `get_formatter()`, `FormatterRegistry`.
  `_output/formatters/` — 30+ type formatters (DataFrame, matplotlib → mimebundle, plotly, …).
- `_runtime/output/_output.py` — `replace()` / `append()` / `replace_at_index()` (mo.output.*).
  **Footgun:** these silently no-op without a runtime context (see invariants 0046).
- `_output/hypertext.py` — `Html` (base for UI elements + display).

## 7. Public API surface
- `marimo/__init__.py` — what `import marimo as mo` exposes: `App`, `Cell`, `ui`, `md`, `output`, `state`,
  `watch`, `cache`, `sql`, layout helpers, AI integrations.

## Three execution paths to keep straight
1. **Edit mode:** `App → Kernel → SequentialScheduler → CellRunner → Executor → graph → frontend`.
2. **Embedded:** `App.embed() → AppKernelRunner → prune_cells_for_overrides → nested Kernel + child context`.
3. **UI reactivity:** `UIElement value change → UpdateUIElementCommand → graph marks dependents → re-run`.

backcast's thin-drain is a **fourth, host-built path** that reuses #1's *components* (graph, executor,
`compute_cells_to_run`) but skips the scheduler/Kernel.run orchestration for native per-bar speed.
