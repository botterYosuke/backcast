// NotebookCellCoordinator.cs — issue #81 cell-as-floating-window (ADR-0013, orchestration brain)
//
// The調整役 that turns notebook-model operations (add / delete / open / save) into window lifecycle
// on the infinite canvas, and keeps the region_id <-> Cell binding. A PLAIN C# class (NOT a
// MonoBehaviour), DI-driven — the same altitude as WorkspaceEngineHost (ADR-0009/0010: the root is
// NOT the place for orchestration; drive it from a host). The root WIRES it (adopt region_001 /
// viewport->canvas anchor / X-button callback) and delegates OnAddCell / Open / Save to it. Keeping
// it MonoBehaviour-free lets the layer-1/2 AFK gate drive AddCell/DeleteCell/Open/Save WITHOUT a full
// scene (fake synthesizer, bare-RT FloatingWindowController, null/real viewFor).
//
// PHYSICAL WINDOW != LOGICAL CELL (ADR-0013 Decision 4): region_001 is a never-Destroy adopted shell.
// Deleting its cell HIDES it (dormant); deleting a region_002+ cell DESPAWNS it. A new cell reuses a
// dormant region_001 first (shell only — new cascade position, NOT the old hidden coords, findings
// 0050 trap 2), else spawns region_002+. #146 (ADR-0033 supersedes ADR-0013 D5): the notebook MAY reach
// 0 cells in a session — deleting the last cell leaves region_001 a dormant shell + the [+] Add Cell
// button only; the next AddCell reuses that dormant region_001 (the same 0→1 path as File→New).
//
// TWO SERIALISATION PATHS, NEVER RECOMBINED: cell content+order -> `.py` (the aggregate, via the
// synthesizer); spatial position -> the layout sidecar as a cell-order-parallel list (CapturePositions),
// regenerated FROM LIVE on each Save (findings 0050 trap 1 — never splice an index-parallel array).

using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class NotebookCellCoordinator
{
    public const string AdoptedRegionId = "strategy_editor:region_001";

    // #169 (ADR-0036 D2, supersedes findings 0050's "本体 seed 却下＝空セル＋placeholder"): the body seeded
    // into the SINGLE cell of a fresh File→New / no-resume boot notebook. An observe-only replay loop (zero
    // trades — never calls bt.submit_market) so the workspace lands on "トヨタを眺めて replay 観察できる、すぐ
    // Run 可能な雛形". `cell_synthesis` wraps it with `@app.cell / def _(bt): / return` (bt is an undefined
    // ref → captured as the def arg). New(seedBody) lands it; the per-cell RUN runs the unsaved buffer (untitled
    // scratch, D6). The host-API placeholder hint mechanism is RETIRED here (D3) — a non-empty seed makes it
    // moot, and keeping it would resurrect a hint that disagrees with the seed once the cell is emptied.
    // No trailing newline: a cell body is canonically stored without one (mirrors the golden gate's
    // _BODIES), which keeps synth→decompose→synth byte-idempotent and matches the owner-spec synth output
    // (no blank line before the bare `return`). Verified by test_marimo_cell_synthesis_golden NEWSEED-05.
    public const string ObserveSeedBody =
        "for bar in bt.replay(bars_per_second=2):\n    pass  # 観察のみ。bt.submit_market() を呼ばない＝売買ゼロ";

    readonly MarimoNotebookDocument _notebook;
    readonly FloatingWindowController _windows;
    readonly Func<string, StrategyEditorView> _viewFor;   // regionId -> its editor view (null-tolerant)
    readonly Func<Vector2> _anchorProvider;               // viewport-centre canvas-logical top-left
    readonly Vector2 _cellWindowSize;

    readonly Dictionary<string, Cell> _cellByRegion = new Dictionary<string, Cell>(StringComparer.Ordinal);
    readonly Dictionary<Cell, string> _regionByCell = new Dictionary<Cell, string>();
    bool _region001Dormant;   // region_001 exists but holds no cell (hidden) -> reuse it next

    // #102 findings 0079 §6 D7: fired AFTER every structural mutation of the cell list (AddCell /
    // DeleteCell / SyncWindowsToNotebook).  Wire to NotebookRunController.Invalidate so an in-flight
    // per-cell RUN whose pressed-index frame predates the mutation does NOT paint onto the rebound
    // window (the dormant region_001 reuse race: press cell A → DeleteCell(A) → AddCell(B) reuses
    // dormant R1 → drain would otherwise paint A's stdout onto B's window).  generation-based
    // mechanism, identical to how Open/New already drops stale results.
    public event Action ListMutated;

    public NotebookCellCoordinator(
        MarimoNotebookDocument notebook,
        FloatingWindowController windows,
        Func<string, StrategyEditorView> viewFor,
        Func<Vector2> anchorProvider,
        Vector2 cellWindowSize)
    {
        _notebook = notebook ?? throw new ArgumentNullException(nameof(notebook));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _viewFor = viewFor ?? (_ => null);
        _anchorProvider = anchorProvider ?? (() => Vector2.zero);
        _cellWindowSize = cellWindowSize;
    }

    public MarimoNotebookDocument Notebook => _notebook;
    public string RegionOf(Cell cell) => cell != null && _regionByCell.TryGetValue(cell, out var r) ? r : null;
    public Cell CellOf(string regionId) => regionId != null && _cellByRegion.TryGetValue(regionId, out var c) ? c : null;

    // ---- [+] : append a new empty cell and show its window ----
    // marimo createNewCell {type:"__end__"}: body="", anonymous, default config. Reuses a dormant
    // region_001 shell first (new cascade position), else spawns region_002+. Returns the new cell.
    public Cell AddCell() => AddCell("");

    // #179 (findings 0128): the shared `import marimo as mo` cell body that an [m] markdown cell
    // depends on, and the `mo.md(...)` seed the [m] button lands. marimo has no markdown CELL TYPE —
    // a markdown cell is a normal cell whose body is `mo.md(r"""…""")` (owner: bare `mo` for本家 parity,
    // NOT a cell-local `_mo`), so it REFS `mo` and needs a sibling cell that DEFS `mo`. The seed is
    // column-0 / no trailing newline (the canonical cell-body form, mirrors ObserveSeedBody) so
    // synth→decompose stays byte-idempotent; synthesis adds the `def _():` wrapper + indent and
    // `mo.md` dedents it for rendering. Pressing ▶ on the md cell runs its STALE upstream ancestor
    // (the import cell) first (IncrementalNotebookSession autorun = pressed + stale ancestors +
    // reactive descendants), so bare `mo` resolves WITHOUT a NameError on the first press.
    public const string MoImportBody = "import marimo as mo";
    public const string MarkdownSeedBody =
        "mo.md(r\"\"\"\n# 見出し\n\n本文をここに書く。\n\"\"\")";

    // ---- [m] : append a markdown cell (mo.md template), ensuring `import marimo as mo` exists ----
    // #179 (findings 0128): the sibling of AddCell that seeds a MARKDOWN cell. It first guarantees a
    // single shared `import marimo as mo` cell (idempotent — reuse if present, else add ONE) so the
    // bare-`mo` seed resolves, then appends the `mo.md(...)` cell exactly like AddCell (window + front +
    // ListMutated). The import cell is NOT bundled INTO the md cell on purpose: that would re-define
    // `mo` on every [m] press (marimo treats cell-level imports as global defs → duplicate-definition
    // error). Returns the new markdown cell. Created idle — the user presses ▶ to render (no auto-run,
    // mirrors [+]).
    public Cell AddMarkdownCell()
    {
        EnsureMoImportCell();
        return AddCell(MarkdownSeedBody);
    }

    // Idempotently ensure ONE cell binds `mo` via an `import marimo as mo`. C# does NOT re-implement
    // marimo's def/ref analysis (its job over the seam — [[ttwr-parity-first]]); a text scan of the
    // existing cell bodies is enough to avoid a SECOND cell defining `mo` (a marimo MultipleDefinitionError).
    // The import cell is added through the WINDOWED AddCell (not _notebook.AddCell) so it gets its own
    // window/▶/✕ and is tracked — every cell↔window bijection path (AddCell / SyncWindowsToNotebook / Open)
    // holds, so CapturePositions never sees a windowless cell. marimo likewise shows the import as a normal cell.
    void EnsureMoImportCell()
    {
        foreach (var c in _notebook.Cells)
            if (DefinesMoImport(c.Body)) return;
        AddCell(MoImportBody);
    }

    // Matches a TOP-LEVEL `import marimo as mo`, tolerating whitespace variants, a trailing comment, and a
    // combined import (`import marimo as mo, pandas as pd`) — so a user's / loaded-.py's non-canonical `mo`
    // import is REUSED, not duplicated into a 2nd defining cell (the duplicate-definition failure D2 forbids).
    // `\bmo\b` keeps `import marimo as molecule` from matching. This stays a text predicate by design (not
    // def/ref) — see the [[ttwr-parity-first]] note above.
    static readonly System.Text.RegularExpressions.Regex MoImportLine =
        new System.Text.RegularExpressions.Regex(@"^import\s+marimo\s+as\s+mo\b");

    static bool DefinesMoImport(string body)
    {
        if (string.IsNullOrEmpty(body)) return false;
        // A markdown cell (body is `mo.md(r"""…""")`) REFS `mo`, it never DEFS it — yet its prose / a code
        // fence inside it can literally contain the line `import marimo as mo` as TEXT. Treating that string
        // content as the import would suppress the real import cell → NameError on ▶. Skip markdown-seed cells;
        // only a code cell defines the import. (A code cell mixing `import marimo as mo` + `mo.md(...)` still
        // STARTS with the import line, so it is not skipped — its first non-whitespace token is `import`.)
        if (body.TrimStart().StartsWith("mo.md(")) return false;
        foreach (var line in body.Split('\n'))
            if (MoImportLine.IsMatch(line.Trim())) return true;
        return false;
    }

    // ---- [+] (overload) : append a new cell carrying `seedBody` and show its window ----
    // #179 (findings 0128): the seeded form of AddCell, factored so AddMarkdownCell (and any future
    // seeded-cell affordance) shares the window-lifecycle path. AddCell() is the seedBody="" case.
    public Cell AddCell(string seedBody)
    {
        Cell cell = _notebook.AddCell(seedBody);

        string region;
        if (_region001Dormant)
        {
            region = AdoptedRegionId;
            _region001Dormant = false;
            RevealAt(region, NextSpawnTopLeft());
        }
        else
        {
            region = AllocRegion();
            _windows.SpawnAuto(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, region,
                               _cellWindowSize.x, _cellWindowSize.y, _anchorProvider(), visible: true);
        }

        Track(region, cell);
        _viewFor(region)?.Bind(cell);
        _windows.Show(region);
        ListMutated?.Invoke();
        return cell;
    }

    // ---- title-bar X : delete the cell in `regionId` ----
    // #146 (ADR-0033): the last cell CAN be deleted (0-cell floor lifted). region_001 -> hide (dormant);
    // region_002+ -> despawn. Deleting the last cell empties the canvas (region_001 dormant + [+] only).
    // Positions are regenerated from live on the next Save, so nothing to splice here.
    public bool DeleteCell(string regionId)
    {
        if (string.IsNullOrEmpty(regionId)) return false;
        if (!_cellByRegion.TryGetValue(regionId, out var cell)) return false;
        if (!_notebook.RemoveCell(cell)) return false;   // #146: false only on a null/unknown cell (no last-cell floor)

        Untrack(regionId, cell);
        if (regionId == AdoptedRegionId)
        {
            _windows.Hide(regionId);     // never-Destroy shell -> dormant
            _region001Dormant = true;
            _viewFor(regionId)?.Bind(null);
        }
        else
        {
            _windows.Close(regionId);    // despawn + deregister
        }
        ListMutated?.Invoke();   // #102 findings 0079 §6 D7: drop any in-flight run against the prior list
        return true;
    }

    // ---- Open : decompose a `.py` into N cell windows ----
    // The aggregate replaces the cell list; this rebuilds the windows to match, applying the
    // sidecar positions (cell-order parallel; null/short -> auto-cascade). Returns false (and shows
    // nothing) when the aggregate's Open fails — the caller reads Notebook.LastError for the notice.
    // #113: the aggregate's Open is now "marimo or error" — a non-marimo / broken `.py` fails WITHOUT
    // touching the buffer (no 1-cell wrap), so the dirty buffer is intrinsically safe and the old
    // #86 F1 dirty-refuse / #87 discardDirty discard-authorization seam is gone (the SaveGuard modal
    // still gives the Save/Discard/Cancel choice before a valid-marimo switch — that protection is at
    // the root, not here).
    public bool Open(string path, IReadOnlyList<Vector2> positions)
    {
        if (!_notebook.Open(path)) return false;
        SyncWindowsToNotebook(positions);
        return true;
    }

    // ---- File->New : unbound notebook with one cell ----
    // #169 (ADR-0036 D2): `seedBody` lands in the single cell (default "" = the pre-#169 empty-cell New, kept
    // for the AFK gates that drive the coordinator primitive directly). The root passes ObserveSeedBody on the
    // fresh File→New / no-resume boot paths so the workspace lands on the observe-replay 雛形.
    public void New(string seedBody = "")
    {
        _notebook.ResetUnboundSeeded(seedBody);
        SyncWindowsToNotebook(null);
    }

    // ---- Save : synthesise the `.py`; positions are captured separately by the root ----
    public bool Save() => _notebook.Save();
    public bool SaveAs(string newPath) => _notebook.SaveAs(newPath);

    // Rebuild the windows so exactly the notebook's cells are shown (used by Open / New and the
    // initial bind). region_001 hosts cell 0 (revealed); cells 1.. spawn region_002+. Existing
    // region_002+ windows are despawned first so a re-open never leaves orphans.
    public void SyncWindowsToNotebook(IReadOnlyList<Vector2> positions)
    {
        // tear down current cell windows: despawn region_002+, hide region_001.
        foreach (var region in new List<string>(_cellByRegion.Keys))
        {
            if (region == AdoptedRegionId) { _windows.Hide(region); _viewFor(region)?.Bind(null); }
            else _windows.Close(region);
        }
        _cellByRegion.Clear();
        _regionByCell.Clear();
        _region001Dormant = true;

        var cells = _notebook.Cells;
        for (int i = 0; i < cells.Count; i++)
        {
            string region = (i == 0) ? AdoptedRegionId : AllocRegion();
            bool hasPos = positions != null && i < positions.Count;
            Vector2 pos = hasPos ? positions[i] : NextSpawnTopLeft();

            if (region == AdoptedRegionId)
            {
                _region001Dormant = false;
                if (hasPos) RevealAt(region, pos);   // restore saved position
                else _windows.Show(region);          // keep the authored position
            }
            else
            {
                _windows.Spawn(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, region,
                               pos.x, pos.y, _cellWindowSize.x, _cellWindowSize.y, visible: true);
            }

            Track(region, cells[i]);
            _viewFor(region)?.Bind(cells[i]);
        }
        ListMutated?.Invoke();   // #102 findings 0079 §6 D7: drop any in-flight run against the prior list
    }

    // Spatial positions in CELL ORDER (position[i] <-> cell[i]), regenerated FROM LIVE — the layout
    // sidecar's cellPositions. A cell with no live window (shouldn't happen) contributes (0,0).
    public List<Vector2> CapturePositions()
    {
        var list = new List<Vector2>(_notebook.CellCount);
        foreach (var cell in _notebook.Cells)
        {
            Vector2 p = Vector2.zero;
            if (_regionByCell.TryGetValue(cell, out var region))
            {
                var rt = _windows.RectOf(region);
                if (rt != null) p = rt.anchoredPosition;
            }
            list.Add(p);
        }
        return list;
    }

    // ---- helpers ----

    void Track(string region, Cell cell) { _cellByRegion[region] = cell; _regionByCell[cell] = region; }
    void Untrack(string region, Cell cell) { _cellByRegion.Remove(region); _regionByCell.Remove(cell); }

    // Lowest unused region_00N (N>=2); region_001 is the adopted shell.
    string AllocRegion()
    {
        int n = 2;
        while (_cellByRegion.ContainsKey(RegionId(n))) n++;
        return RegionId(n);
    }

    static string RegionId(int n) => "strategy_editor:region_" + n.ToString("D3");

    // The cascade position for the NEXT window, off every live window (findings 0050: collision母集合
    // = all windows). Used for region_002+ new spawns AND dormant region_001 reuse.
    Vector2 NextSpawnTopLeft()
        => SpawnPlacement.Next(_windows.CaptureTopLefts(), _anchorProvider(), SpawnPlacement.DefaultOffset);

    // Reveal an existing window AT a position (dormant region_001 reuse / restore): move it then show.
    void RevealAt(string region, Vector2 topLeft)
    {
        _windows.ApplyGeometry(new FloatingWindowLayout(
            region, FloatingWindowCatalog.KIND_STRATEGY_EDITOR,
            topLeft.x, topLeft.y, _cellWindowSize.x, _cellWindowSize.y, 0, true));
        _windows.Show(region);
    }
}
