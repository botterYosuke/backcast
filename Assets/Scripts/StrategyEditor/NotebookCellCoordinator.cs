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
// 0050 trap 2), else spawns region_002+. The notebook is ALWAYS >=1 cell (the aggregate's >=1 guard).
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

    // The single-cell host-API placeholder hint (marimo showPlaceholder = hasOnlyOneCell). The
    // injected globals aren't in a generic marimo note, so backcast names them in the placeholder —
    // NEVER seeded into the body (findings 0050).
    public const string HostApiHint = "get_bar()\nget_portfolio()\nsubmit_market(qty)";

    readonly MarimoNotebookDocument _notebook;
    readonly FloatingWindowController _windows;
    readonly Func<string, StrategyEditorView> _viewFor;   // regionId -> its editor view (null-tolerant)
    readonly Func<Vector2> _anchorProvider;               // viewport-centre canvas-logical top-left
    readonly Vector2 _cellWindowSize;

    readonly Dictionary<string, Cell> _cellByRegion = new Dictionary<string, Cell>(StringComparer.Ordinal);
    readonly Dictionary<Cell, string> _regionByCell = new Dictionary<Cell, string>();
    bool _region001Dormant;   // region_001 exists but holds no cell (hidden) -> reuse it next

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
    public Cell AddCell()
    {
        Cell cell = _notebook.AddCell();

        string region;
        if (_region001Dormant)
        {
            region = AdoptedRegionId;
            _region001Dormant = false;
            RevealAt(region, NextSpawnTopLeft());   // shell reuse: NEW position, not old hidden coords
        }
        else
        {
            region = AllocRegion();
            _windows.SpawnAuto(FloatingWindowCatalog.KIND_STRATEGY_EDITOR, region,
                               _cellWindowSize.x, _cellWindowSize.y, _anchorProvider(), visible: true);
        }

        Track(region, cell);
        _viewFor(region)?.Bind(cell);
        _windows.Show(region);   // reveal + raise (front)
        UpdatePlaceholders();
        return cell;
    }

    // ---- title-bar X : delete the cell in `regionId` ----
    // >=1 guard (the aggregate refuses the last cell). region_001 -> hide (dormant); region_002+ ->
    // despawn. Positions are regenerated from live on the next Save, so nothing to splice here.
    public bool DeleteCell(string regionId)
    {
        if (string.IsNullOrEmpty(regionId)) return false;
        if (!_cellByRegion.TryGetValue(regionId, out var cell)) return false;
        if (!_notebook.RemoveCell(cell)) return false;   // last-cell guard

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
        UpdatePlaceholders();
        return true;
    }

    // ---- Open : decompose a `.py` into N cell windows ----
    // The aggregate replaces the cell list; this rebuilds the windows to match, applying the
    // sidecar positions (cell-order parallel; null/short -> auto-cascade). Returns false (and shows
    // nothing) when the aggregate's Open fails — the caller reads Notebook.LastError for the notice.
    // #87 slice 3: a caller that already obtained the user's discard consent (the SaveGuard "Discard"
    // verdict on File→Open) passes discardDirty:true to RELAX the aggregate's #86 F1 dirty-refuse. The
    // default false keeps F1 intact for every other caller (restore / probes / clean opens).
    public bool Open(string path, IReadOnlyList<Vector2> positions, bool discardDirty = false)
    {
        if (!_notebook.Open(path, discardDirty)) return false;
        SyncWindowsToNotebook(positions);
        return true;
    }

    // ---- File->New : unbound notebook with one empty cell ----
    public void New()
    {
        _notebook.ResetUnboundEmpty();
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
        UpdatePlaceholders();
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

    // Single-cell host-API hint on the only cell window; cleared otherwise (marimo hasOnlyOneCell).
    void UpdatePlaceholders()
    {
        bool single = _notebook.CellCount == 1;
        foreach (var kv in _cellByRegion)
            _viewFor(kv.Key)?.SetPlaceholderHint(single ? HostApiHint : null);
    }
}
