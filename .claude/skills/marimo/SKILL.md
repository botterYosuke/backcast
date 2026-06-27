---
name: marimo
description: >-
  Knowledge and source map for working with marimo (the reactive Python notebook) as it is
  EMBEDDED in the backcast trading app (The-Trader-Was-Replaced). Use this whenever a task touches
  marimo features, marimo internals, or the strategy-editor/notebook layer — even if "marimo" is
  not said explicitly. Triggers: marimo cells / notebooks, reactive cell DAG, `marimo.App`,
  `@app.cell`, `App.embed`, `mo.state` / `mo.ui` / `mo.output` / `mo.md`, the marimo `Kernel` /
  `Runner` / executor / dataflow graph, cell synthesis & decomposition (`generate_filecontents`,
  `load_app`), thin-drain / hot vs cold path, per-cell RUN, `HeadlessKernel`, `MarimoStrategy`,
  `NotebookSession`, `IMarimoSynthesizer`, Replay↔Auto cell driving, `bt` façade, `bt.universe.*`,
  the run-guard / cell-insert affordance, zero-cell / floor=1 persistence, the lazy-import seam,
  or any work under python/engine/strategy_runtime/* or Assets/Scripts/StrategyEditor/*. Also use
  when reading or modifying the upstream marimo source mirror checked out under this skill folder.
  Do NOT use for purely imperative (non-marimo) Strategy subclasses or for nautilus/engine bar
  internals — those belong to the nautilus-trader skill.
---

# marimo (embedded in backcast)

backcast embeds marimo as its **strategy authoring + execution** substrate: a strategy is a marimo
notebook (`marimo.App()` + `@app.cell` functions), edited in a Unity C# editor, synthesized to `.py`,
and run two ways — per-cell RUN (cold) and per-bar backtest (hot "thin-drain"). This skill is the map
for understanding and modifying that machinery without breaking its load-bearing invariants.

## Two source trees — know which you're reading

| Tree | What it is | Use it for |
|------|-----------|-----------|
| **Mirror** `.claude/skills/marimo/marimo/…` | upstream marimo source, **v0.23.11** | understanding concepts & structure; navigating internals |
| **Installed** `python/.venv/.../site-packages/marimo/` | what backcast actually runs, **v0.20.4** | exact private-API signatures backcast couples to |

> ⚠️ **The mirror is 3 minor versions ahead of what runs.** Private APIs drift. Read the mirror to
> learn how a thing works; verify the *exact* signature against the installed 0.20.4 copy before you
> depend on it. marimo is range-pinned `>=0.20.4`; `test_strategy_runtime_thin_drain.py` is the drift gate.

## Mental model: hot vs cold

Everything hinges on this split. Conflating the two paths is the #1 bug source.

- **cold path** — compile, edit, per-cell RUN, Open/Save. Full marimo `Kernel`/`Runner` orchestration;
  outputs and UI are visible; uses marimo **private APIs**.
- **hot path** — per-bar `on_bar` in a backtest. Host-owned **thin-drain**: a static cell list precomputed
  once at compile, then bare `exec`/`eval` per bar under a `with_cell_id` context. ~native speed
  (~680× faster than `Kernel.run` per bar). Minimal marimo coupling.

Within a bar, cells communicate by **variable dataflow** (a cell's returned vars). Across bars, by
**`mo.state`** (writer `set_x` / reader `get_x`) — because state passing creates no def/ref edge.

## How to use this skill

1. **Identify the surface.** Authoring/synthesis? per-cell RUN? thin-drain hot loop? live Replay↔Auto?
   C# editor seam? Each has a distinct owner file.
2. **Read the project seam** in [references/backcast-seams.md](references/backcast-seams.md) — the precise
   list of backcast files on each side of the boundary, what they call, and which marimo symbols they touch.
3. **Navigate marimo internals** via [references/marimo-internals.md](references/marimo-internals.md) — where
   the dataflow graph, kernel, executor, embed, state, UI, and outputs live in the source.
4. **Check the invariants** in [references/invariants-findings.md](references/invariants-findings.md) BEFORE
   editing — the rules that keep the embedding correct, plus the findings/ADR trail to read first.
5. **Verify exact signatures** against the installed 0.20.4 source (not the mirror) for anything load-bearing.
6. **Gate your change** with the relevant test (`test_strategy_runtime_thin_drain.py`,
   `test_strategy_runtime_offline.py`, `test_marimo_cell_synthesis_golden.py`, …).

## Invariants you must not break (full list + sources in references/invariants-findings.md)

- **Lazy-import discipline.** The runtime seam must not import marimo at module load — only the strategy
  layer does. (`test_strategy_runtime_offline.py` enforces this.)
- **Kernel/RuntimeContext are thread-local** — one kernel per notebook, one dedicated worker thread.
- **Private marimo APIs live in the cold path only**; the hot path is bare `exec`. Call marimo's
  `Runner.compute_cells_to_run(...)` rather than reimplementing topo/closure.
- **driver-state is host-declared, not auto-detected;** **fail-closed is structural** (reject a new cid).
- **per-bar cells install `with_cell_id` context** or `mo.output`/`mo.ui` silently no-op.
- **Open is marimo-only** (non-marimo `.py` → reject, distinct from broken-syntax); **persistence floor = 1 cell**.

## Common task → where to look

| Task | Start here |
|------|-----------|
| Change per-bar execution / perf | `thin_drain.py` (`_compile`, `_execute_hot_cell`); findings 0046 |
| Add/modify a cell-insert affordance | `cell_synthesis.py`; findings 0049 (run-guard anchor, `_`-skeleton) |
| Synthesis/decomposition round-trip | `cell_synthesis.py`; mirror `_ast/codegen.py`, `_ast/load.py`; test_…_golden |
| per-cell RUN behavior | `notebook_session.py`; mirror `_runtime/runner/cell_runner.py`; findings 0071 |
| Replay↔Auto live cell driving | `live_cell_runtime.py`; findings 0092 / ADR-0025 |
| Reactivity / dependency graph questions | mirror `_runtime/dataflow/graph.py` + `definitions.py` |
| `mo.state` / feedback across bars | mirror `_runtime/state.py`; ADR-0012 D1, findings 0046 D1 |
| C# editor ⇄ Python boundary | `Assets/Scripts/StrategyEditor/*`, `WorkspaceEngineHost.cs`; ADR-0013 |
| dynamic universe from a cell | `bt.universe.*`; findings 0115 / ADR-0031 |
| markdown cell (`mo.md`) detect/seed | NO cell type — a normal cell `mo.md(r"""…""")`. Detect: mirror `_convert/common/format.py::get_markdown_from_cell` (refs=={mo}, no defs). Canonical seed: `_ast/codegen.py::construct_markdown_call` (bare `mo`, prefix `r`). backcast [m] button + shared-import-cell: findings 0128 |

Tests run with `cd python && uv run pytest <file> -v`.
