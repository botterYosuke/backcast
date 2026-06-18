# 1 cell = 1 floating window; notebook aggregate owns the single `.py`

**Status:** accepted (2026-06-18)

## Context

ADR-0012 made the target authored model the marimo cell-DAG. #81 implements the authoring
surface as **marimo 3D mode**: each cell is an independent floating window on the infinite
canvas (`strategy_editor:region_NNN`) showing **only the cell body** — `@app.cell` /
`def _(refs)` / `return defs` are never on screen. The hidden wrapper and the reactive
dependency edges are computed by marimo, not C#.

This collides with the pre-#81 editor model, where **1 window = 1 `StrategyDocument` =
1 `.py` file = its own `IStrategyFileProvider`** (#16/#78). In the cell model there is **one
`.py` for the whole notebook** but **N windows**, so the file↔window correspondence inverts.

## Decision

1. **Notebook aggregate owns the one `.py`.** A new `MarimoNotebookDocument` owns the single
   `.py` path, the dirty flag, the ordered cell list, Save, and Open. It IS the
   `IStrategyFileProvider`; `EditorFileProvider` points to it. The engine supply seam stays
   "one path" (improvement is local). The "supplyable" 5-condition contract (path-bound ∧
   not dirty ∧ last Open/Save ok ∧ canonical `.py` ∧ exists) and the WYSIWYR "no path while
   dirty" rule (findings 0044) move UP from `StrategyDocument` to the aggregate.
   **Body ownership (model A, mirrors marimo's central store):** the aggregate's ordered cell
   list IS the source of truth for cell *body text*; a window is a view that reads/edits its
   cell (marimo: `cellData.code` + `inOrderIds` central store, `cell-flow-node.tsx` reads
   `useCellData().code`). The two concerns serialize on **separate paths and are never
   recombined**: cell content (**body + name + config**) + order → `.py` via
   `generate_filecontents`; spatial position → layout
   sidecar as a list index-parallel to `.py` cell order (no id key, findings 0050). Window id
   is demoted to a runtime host handle, not a persistence key; a dormant `region_001` shell
   holds neither body nor position.

2. **A cell window is a body fragment, not a file.** Each window reuses the editor core
   (text / `EditHistory` undo / `PythonSyntaxMeshEffect` highlight) but edits a **cell body
   fragment** — it has no path, no per-window Open/Save.

3. **C# = spatial UI; Python(marimo) = synthesis/decomposition + DAG.** Save synthesizes the
   ordered cells via `marimo._ast.codegen.generate_filecontents(codes, names, cell_configs)`;
   Open decomposes via `marimo._ast.load.load_app` + `app._cell_manager.codes()/names()/configs()`.
   The seam carries **body + name + config** (not body-only): cell *names* (`def _config()`)
   and configs are preserved opaquely so re-saving a named notebook (e.g. #76's
   `v19_morning_cell.py`) does not collapse `def _config()` → `def _()`. S1 does not *edit*
   names/configs (name UI is a later slice); it captures them on Open and writes them back on
   Save. C# never reimplements def/ref/return analysis ([[ttwr-parity-first]]). Frozen GREEN by
   spike (findings 0050): round-trip is byte-idempotent (the spike round-tripped
   codes+names+configs) and the output runs order-for-order under host-seeding even though host
   APIs become args (`def _(get_bar):`). The on-disk canonical form becomes
   `generate_filecontents` output (`__generated_with` line + run-guard footer), superseding
   the older footer-less hand-authored form.

4. **Physical window ≠ logical cell; adopt = hide-not-destroy.** Cell identity is NOT pinned
   1:1 to a window GameObject. The scene-authored `region_001` is a never-`Destroy` shell
   (adopt invariant, findings 0025 §8). Deleting a cell: a `region_002+` window is
   `Despawn`ed (destroyed); the `region_001` window is **hidden** (`SetActive(false)`) and
   goes dormant. New cells reuse a dormant `region_001` first, else spawn `region_002…`.
   Spatial positions stay bound to the cell, so windows don't jump.

5. **Notebook is always ≥1 cell** (marimo `canDelete={!hasOnlyOneCell}`). Delete never
   reaches 0 cells; the 0-cell state is a transient (File→New / opening an empty `.py`)
   resolved by bootstrapping cell 1. (Dormant `region_001` is a separate physical concept
   from logical cell count.)

## Considered options

- **Keep per-window `.py` files (the pre-#81 model).** Rejected: marimo is one notebook =
  one `.py`; N independent files would need a cross-window synthesis layer bolted onto the
  engine seam and would diverge from the port source.
- **Vertical stack of editor boxes in one window (marimo non-3D `cell-array`).** Considered
  during grill, rejected by owner: the target is marimo **3D mode** (`CellFlowNode`), spatial
  windows with drag / z-order / arrows.
- **Pin cell identity to the scene-authored window / forbid deleting the first cell.**
  Rejected: users delete any cell (owner); `canDelete={!hasOnlyOneCell}` guards the last cell,
  not the first — so the shell must survive via hide-not-destroy, not via a delete ban.

## Consequences

- The `#16/#78` provider seam is restructured (the reason this ADR exists). `StrategyDocument`
  splits: file-I/O + supplyable → aggregate; text buffer → per-cell window (a lightweight
  `Cell`). The run/supply path is unchanged: it is interface-decoupled
  (`RegistryStrategyFileProvider` → registry → `IStrategyFileProvider`), so the aggregate
  implements `IStrategyFileProvider` and registers under the same id; no run-path edits.
- **`StrategyDocument` retirement = move contracts, not delete.** Its proven I/O contracts
  (atomic temp+replace, `SaveAs` writes a NEW `.py` and leaves the old file independent,
  Open rejects a vanished `.py`, stale-picker guard) move UP to the aggregate, and the probes
  that pin them (StrategyEditorProbe §3, MultiDocLayoutProbe §4, StrategyPickerProbe §5,
  BackcastWorkspaceProbe) migrate to target the aggregate — dropping them would erase the
  file-model regression net. **Semantic shift:** `SaveAs` rebinds the *whole notebook* to a
  new path (N windows → 1 `.py`), not a per-window file swap. The per-window `filePath`
  layout state retires with it (id-key → cell-order index-parallel list); the
  MultiDocLayoutProbe / `StrategyEditorRestore` migration is therefore the *same work* as the
  layout-schema migration, not a separate task.
- Existing goldens / `test_marimo_strategy_adapter.py` (footer-less hand-authored form) update
  to the `generate_filecontents` canonical form. Goldens written version-independent
  (`__generated_with` masked) to avoid churn on marimo upgrades.
- Slices: S1 windows + synthesis + Open/Save + delete + position persistence (no arrows);
  S2 dependency arrows (pure viz of refs/defs); S3 explicit ordering UI / cell names.

ADR-0012 referenced only (not written back). Slice record + spike evidence: findings 0050.
