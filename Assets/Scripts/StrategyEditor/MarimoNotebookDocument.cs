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
// >=1 INVARIANT (marimo canDelete=!hasOnlyOneCell): the notebook is ALWAYS >=1 cell; RemoveCell
// refuses the last cell. The 0-cell transient (File->New / opening an empty `.py`) is resolved by
// bootstrapping one empty cell. UnityEngine-FREE so the layer-1 AFK gate drives the whole model.

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
    string _lastError;            // last Open fail-soft reason (the caller surfaces it)

    public MarimoNotebookDocument(IMarimoSynthesizer synthesizer)
    {
        _synth = synthesizer ?? throw new ArgumentNullException(nameof(synthesizer));
        _cells.Add(NewCell("", "_", "{}"));   // a fresh notebook starts unbound with one empty cell (>=1)
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

    // Remove a cell, REFUSING the last one (>=1 invariant, marimo canDelete=!hasOnlyOneCell). A
    // structural change -> dirty. Position re-packing is the caller's regenerate-from-live job
    // (findings 0050 trap 1: never splice an index-parallel array).
    public bool RemoveCell(Cell cell)
    {
        if (cell == null) return false;
        if (_cells.Count <= 1) return false;   // >=1 guard: the last cell cannot be deleted
        if (!_cells.Remove(cell)) return false;
        _dirty = true;
        return true;
    }

    // Synthesise the ordered cells and write them to the bound path via an atomic temp+replace.
    public bool Save()
    {
        if (_path == null) return false;
        string py = _synth.Synthesize(_cells);
        if (py == null) return false;                       // unexpected seam failure: retain dirty/path
        if (!AtomicPyFile.Write(_path, py)) return false;   // replace-failure preserves on-disk
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

        string py = _synth.Synthesize(_cells);
        if (py == null) return false;
        if (!AtomicPyFile.Write(full, py)) return false;

        _path = full;
        _dirty = false;
        _openedOrSaved = true;
        return true;
    }

    // Open an existing `.py`: read -> decompose -> REPLACE the cell list. The notebook is unchanged
    // ONLY for path/IO failures (bad path / wrong extension / missing file / read error); those set
    // LastError and the caller shows a notice. The synthesiser's fail-soft null (non-marimo or broken
    // source) is NOT an Open failure: per #86 the file content is BOOTSTRAPPED as a single anonymous
    // cell (body = the raw file text verbatim, name = "_", default config) so any `.py` File->Open
    // picks opens. This makes the editor a general Python editor at Open time; Save then synthesises
    // through `generate_filecontents` (owner: destructive overwrite into marimo form is OK), so a
    // non-marimo `.py` opened + saved is a ONE-WAY migration into the cell-DAG model. A VALID but
    // empty/0-cell marimo `.py` opens with one bootstrapped empty cell (>=1 invariant).
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

        // #86: a non-marimo / unparseable `.py` (Decompose -> null) wraps the WHOLE file text as ONE
        // anonymous cell so the editor opens it as-is. The synthesiser seam still says "null = not a
        // marimo notebook" (the Python contract is unchanged); the wrap is the aggregate's policy.
        // Route through NewCell so the wrap cell shares the same factory as every other constructor
        // in this file — the foreach below re-binds the decomposed-list cells too, but going through
        // NewCell keeps the wrap path on the file's own MarkDirty wiring (invariant locality).
        IReadOnlyList<Cell> decomposed = _synth.Decompose(content)
            ?? new List<Cell> { NewCell(content, "_", "{}") };

        _cells.Clear();
        foreach (var c in decomposed)
        {
            c.BindBodyChanged(MarkDirty);
            _cells.Add(c);
        }
        // Reachable only when Decompose returns a non-null but empty list — a VALID marimo header
        // (`app = marimo.App()`) with zero `@app.cell` defs. The non-marimo / unparseable case takes
        // the wrap above (always >=1 cell); a 0-byte non-marimo `.py` becomes a 1-cell wrap of body="".
        if (_cells.Count == 0) _cells.Add(NewCell("", "_", "{}"));

        _path = full;
        _dirty = false;
        _openedOrSaved = true;
        return true;
    }

    // Restore-boundary / File->New reset to unbound with ONE empty cell (marimo File->New = one
    // empty cell). NOT a normal Open failure (which leaves the notebook unchanged).
    public void ResetUnboundEmpty()
    {
        _cells.Clear();
        _cells.Add(NewCell("", "_", "{}"));
        _path = null;
        _dirty = false;
        _openedOrSaved = false;
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
