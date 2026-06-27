// MarimoNotebookDocument.cs — issue #81 "cell-as-floating-window" (ADR-0013, PURE CORE)
//
// The NOTEBOOK AGGREGATE: in the cell-as-floating-window model there is ONE `.py` for the whole
// notebook but N windows, so the file<->window correspondence inverts from the pre-#81 model
// (1 window = 1 StrategyDocument = 1 `.py`). This aggregate owns the single `.py` path, the dirty
// flag, the ordered cell list, Save, and Open, and IS the `IStrategyFileProvider` (EditorFileProvider
// points to it; the run/supply path is unchanged — RegistryStrategyFileProvider -> registry ->
// IStrategyFileProvider, so it registers under one id and no run-path edits are needed).
//
// It SUPERSEDES StrategyDocument (ADR-0013 Consequences): StrategyDocument's proven I/O contracts
// MOVE here — atomic temp+replace Save (replace-failure preserves on-disk), SaveAs writes a NEW
// `.py` and forks, Open rejects a vanished/non-`.py` path, the supplyable 5-condition contract, and
// the WYSIWYR "no path while dirty" rule (findings 0044). The body text splits OUT to the per-cell
// window (Cell); this aggregate owns file I/O + the ordered cell model.
//
// BODY OWNERSHIP (model A, mirrors marimo's central store): the ordered cell list IS the source of
// truth for body text; a window is a view that reads/edits its Cell. Serialisation splits on TWO
// paths that are never recombined: cell content (body + name + config) + order -> `.py` via the
// injected IMarimoSynthesizer; spatial position -> the layout sidecar (owned by the coordinator/root,
// NOT here). DIRTY sources: a body edit (Cell.SetBody -> MarkDirty) AND add/delete (structural change
// also changes the `.py`), so "added a window but didn't save" correctly falls to not-supplyable.
//
// ZERO-CELL FLOOR LIFTED (#146 / ADR-0033 supersedes ADR-0013 D5): during a session the notebook MAY
// reach 0 cells — RemoveCell no longer refuses the last cell, so [✕] can delete the canvas down to an
// empty state ([+] Add Cell only). A FRESH notebook still starts with one empty cell (constructor /
// ResetUnboundEmpty = File->New), because a blank canvas has no typing start point. PERSISTENCE FLOOR = 1
// (marimo-derived, ADR-0033 D2): a 0-cell notebook saves to a header-only `.py`, but marimo's load_app
// inflates an empty valid-marimo file back to ONE empty cell, so Save→Open round-trips 0 → 1 (NOT 0) —
// backcast adds no machinery to keep 0 across the disk hop (X1). UnityEngine-FREE so the layer-1 AFK gate
// drives the whole model.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public sealed class MarimoNotebookDocument : IStrategyFileProvider
{
    readonly IMarimoSynthesizer _synth;
    readonly List<Cell> _cells = new List<Cell>();
    string _path;                 // null = unbound; otherwise canonical absolute .py
    bool _dirty;
    bool _openedOrSaved;          // last Open OR Save succeeded (provider condition 3)
    string _lastError;            // last Open failure reason (the caller surfaces it)

    public MarimoNotebookDocument(IMarimoSynthesizer synthesizer)
    {
        _synth = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _cells.Add(NewCell("", "_", "{}"));   // a fresh notebook starts unbound with one empty cell (File→New floor=1)
    }

    public IReadOnlyList<Cell> Cells => _cells;
    public int CellCount => _cells.Count;
    public string CurrentPath => _path;
    public bool IsDirty => _dirty;
    public bool IsBound => _path != null;
    public string LastError => _lastError;
    public int IndexOf(Cell cell) => _cells.IndexOf(cell);

    void MarkDirty() => _dirty = true;

    Cell NewCell(string body, string name, string configJson)
    {
        var c = new Cell(body, name, configJson);
        c.BindBodyChanged(MarkDirty);
        return c;
    }

    // Append a new (empty, anonymous, default-config) cell at the END — marimo createNewCell
    // {type:"__end__"}. A structural change, so the notebook goes dirty (the `.py` would change).
    public Cell AddCell(string body = "", string name = "_", string configJson = "{}")
    {
        var c = NewCell(body, name, configJson);
        _cells.Add(c);
        _dirty = true;
        return c;
    }

    // Remove a cell. #146 (ADR-0033 supersedes ADR-0013 D5): the >=1 floor is LIFTED — the last cell
    // CAN be removed, reaching 0 cells (the empty-canvas state; the only authoring affordance left is
    // the screen-fixed [+] Add Cell button). The down-stream (SyncWindowsToNotebook / CapturePositions /
    // run routing) is all 0-cell-safe (findings 0114 §3), so the floor removal does
    // NOT crash. false is now returned ONLY for a genuine anomaly (a null or unknown cell). A structural
    // change -> dirty. Position re-packing is the caller's regenerate-from-live job (findings 0050 trap 1:
    // never splice an index-parallel array).
    public bool RemoveCell(Cell cell)
    {
        if (cell == null) return false;
        if (!_cells.Remove(cell)) return false;
        _dirty = true;
        return true;
    }

    // #95 Phase 2 土台: the LIVE notebook source as one canonical marimo `.py` text, WITHOUT
    // writing to disk and WITHOUT requiring not-dirty (owner HITL: per-cell RUN runs the unsaved
    // buffer). This is the pure-compute run input — distinct from the engine-run path, which still
    // takes the saved file via TryGetStrategyFile (golden reproducibility). Returns null on a
    // synthesiser seam failure (the caller skips the run + surfaces a notice).
    public string SynthesizeLiveSource() => _synth.Synthesize(_cells);

    // Synthesise the ordered cells and atomic temp+replace them onto `path`. ONE place for the
    // "synth the live buffer → atomic write" shape (Save / SaveAs / untitled scratch all share it), so
    // the on-disk byte contract can never drift between them. No state change — the caller owns dirty/path.
    bool WriteSynthesized(string path)
    {
        string py = _synth.Synthesize(_cells);
        if (py == null) return false;                       // unexpected seam failure
        return AtomicPyFile.Write(path, py);                // replace-failure preserves on-disk
    }

    // Synthesise the ordered cells and write them to the bound path via an atomic temp+replace.
    public bool Save()
    {
        if (_path == null) return false;
        if (!WriteSynthesized(_path)) return false;   // seam failure / replace-failure: retain dirty/path
        _dirty = false;
        _openedOrSaved = true;
        return true;
    }

    // Save As (findings 0048 D6): synthesise + write the WHOLE notebook to a NEW `.py` and REBIND
    // (N windows -> 1 `.py`; not a per-window file swap). On any failure the notebook is UNCHANGED.
    public bool SaveAs(string newPath)
    {
        if (string.IsNullOrEmpty(newPath)) return false;
        string full;
        try { full = Path.GetFullPath(newPath); }
        catch { return false; }
        if (!string.Equals(Path.GetExtension(full), ".py", StringComparison.OrdinalIgnoreCase)) return false;

        if (!WriteSynthesized(full)) return false;

        _path = full;
        _dirty = false;
        _openedOrSaved = true;
        return true;
    }

    // Open an existing `.py`: read -> decompose -> REPLACE the cell list. The notebook is UNCHANGED
    // on EVERY failure (path/IO error OR a non-marimo / broken source): the failing leg sets LastError
    // and returns false WITHOUT touching `_cells` / `_path` / `_dirty`, so an in-flight buffer (even a
    // dirty one) is never wiped by a failed Open. The caller surfaces LastError.
    //
    // #113 (reverses findings 0054 §D1): the editor is a MARIMO notebook editor — Open is "marimo or
    // error". A non-marimo `.py` (`_synth.Decompose` -> null with "not a marimo notebook") is NO
    // LONGER bootstrapped into a 1-cell wrap; it is an explicit Open failure. A broken-syntax source
    // surfaces as a DISTINCT "syntax error: ..." (AC#2), never masked. This makes the Open layer
    // consistent with the run/materialize contract (#112 ADR-0025 D4 NOT_A_MARIMO_NOTEBOOK). A VALID
    // marimo `.py` opens unchanged. #146 (ADR-0033 D2): a VALID-but-EMPTY marimo header (no `@app.cell`
    // defs) is NOT rejected — marimo's load_app inflates it back to ONE empty cell, so Open succeeds with
    // 1 cell (the X1 persistence floor; Save→Open round-trips 0 → 1). See the empty-list guard below.
    public bool Open(string path)
    {
        _lastError = null;
        if (string.IsNullOrEmpty(path)) return Fail("no path");

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return Fail("bad path"); }

        if (!string.Equals(Path.GetExtension(full), ".py", StringComparison.OrdinalIgnoreCase)) return Fail("not a .py");
        if (!File.Exists(full)) return Fail("file missing");

        string content;
        try { content = File.ReadAllText(full, Encoding.UTF8); }
        catch { return Fail("read failed"); }

        // #113: Decompose returns the ordered cells for a valid marimo notebook, or null with a
        // user-facing reason ("not a marimo notebook" / "syntax error: ...") otherwise. Reject the
        // non-marimo / broken source as an Open failure — no 1-cell wrap, buffer untouched.
        IReadOnlyList<Cell> decomposed = _synth.Decompose(content, out string decErr);
        if (decomposed == null) return Fail(decErr ?? "not a marimo notebook");
        // #146 (ADR-0033 D2 / findings 0114 §2): on real marimo, load_app INFLATES a valid marimo
        // header (`app = marimo.App()`) with zero `@app.cell` defs back into ONE empty cell, so a valid
        // marimo file never decomposes to an empty list (and a non-marimo file already returned null
        // above). This guard is therefore defensive dead code — kept as belt-and-suspenders so a
        // hypothetical seam returning a non-null-but-empty list can't bind a windowless 0-cell doc.
        if (decomposed.Count == 0) return Fail("not a marimo notebook");

        _cells.Clear();
        foreach (var c in decomposed)
        {
            c.BindBodyChanged(MarkDirty);
            _cells.Add(c);
        }

        _path = full;
        _dirty = false;
        _openedOrSaved = true;
        return true;
    }

    // Restore-boundary / File->New reset to unbound with ONE empty cell (marimo File->New = one
    // empty cell). NOT a normal Open failure (which leaves the notebook unchanged).
    public void ResetUnboundEmpty() => ResetUnboundSeeded("");

    // #169 (ADR-0036 D2): File->New reset to unbound with ONE cell carrying `seedBody`. The seed STRING
    // is a product decision owned by the caller (NotebookCellCoordinator.ObserveSeedBody) — this method
    // stays PURE (no policy): it just lands one cell with the given body, unbound and NOT dirty (a fresh
    // New is clean, so the Untitled badge and the WYSIWYR floor hold). `ResetUnboundEmpty` is the seedBody=""
    // case (kept so the pure-document AFK gate still pins the empty-cell floor).
    public void ResetUnboundSeeded(string seedBody)
    {
        _cells.Clear();
        _cells.Add(NewCell(seedBody ?? "", "_", "{}"));
        _path = null;
        _dirty = false;
        _openedOrSaved = false;
    }

    // #169 (ADR-0036 D5): true iff at least one cell carries a non-whitespace body. The untitled-Run
    // gate predicate — an untitled notebook is runnable iff it has authored content (the seeded observe
    // cell qualifies; a 0-cell or all-blank buffer does not). Distinct from the supplyable 5-condition
    // (TryGetStrategyFile), which is named-doc WYSIWYR only.
    public bool HasNonEmptyCell
    {
        get
        {
            foreach (var c in _cells)
                if (!string.IsNullOrWhiteSpace(c.Body)) return true;
            return false;
        }
    }

    // #169 (ADR-0036 D5/D6): the file an UNTITLED notebook's per-cell RUN executes. The document owns the whole
    // run-file decision (it already owns the named supplyable 5-condition via TryGetStrategyFile): an untitled
    // notebook (_path == null) with a non-empty cell synthesises the LIVE buffer to `scratchPath` and returns it,
    // so the run gets a real `.py` on disk → the backend sets `__file__` to it (findings 0089) and a cell's
    // Path(__file__).parent artifact resolution works without a Save (ADR-0011 §4 解消; the os.chdir cwd policy
    // itself stays #79). A BOUND (named) notebook returns false here — its run-file is TryGetStrategyFile (named
    // WYSIWYR), so a named+dirty doc stays blocked, NOT silently run from a scratch. An empty / 0-cell untitled
    // buffer returns false. Writing the scratch does NOT bind the notebook (_path stays null → Untitled badge /
    // WYSIWYR floor intact). Returns false on a synth or write failure (the caller leaves the run path null).
    public bool TryGetUntitledScratch(string scratchPath, out string path)
    {
        path = null;
        if (_path != null) return false;                    // bound (named) → its run-file is TryGetStrategyFile
        if (!HasNonEmptyCell) return false;                 // empty / 0-cell buffer → not runnable
        if (string.IsNullOrEmpty(scratchPath)) return false;
        if (!WriteSynthesized(scratchPath)) return false;
        path = scratchPath;
        return true;
    }

    // IStrategyFileProvider — supplyable iff ALL 5 conditions hold (findings 0010 §5, moved up here):
    // bound, not dirty, last Open/Save ok, canonical absolute `.py`, still a file now.
    public bool TryGetStrategyFile(out string path)
    {
        path = null;
        if (_path == null) return false;
        if (_dirty) return false;
        if (!_openedOrSaved) return false;
        if (!Path.IsPathRooted(_path)) return false;
        if (!string.Equals(Path.GetExtension(_path), ".py", StringComparison.OrdinalIgnoreCase)) return false;
        if (!File.Exists(_path)) return false;
        path = _path;
        return true;
    }

    // Set the fail-soft reason and return false in one statement (Open's uniform failure exit).
    bool Fail(string reason) { _lastError = reason; return false; }
}
