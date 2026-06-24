// PythonnetMarimoSynthesizer.cs — issue #81 cell-as-floating-window (ADR-0013 Decision 3, prod seam)
//
// The PRODUCTION IMarimoSynthesizer: synthesise/decompose the notebook by calling marimo's native
// generate_filecontents / load_app through WorkspaceEngineHost (the single Python owner, ADR-0009).
// It holds NO PythonEngine of its own — it forwards to the host's SynthesizeCells / DecomposeCells,
// which run on the host's GIL. The C#<->Python boundary is JSON: each Cell becomes
// {body, name, config} (config embedded as opaque JSON, not double-encoded), and decompose parses
// the JSON array back into Cells. The layer-1 fake mirrors THIS contract; the layer-2 (real
// pythonnet) and layer-3 (real marimo, pytest golden) gates assert the same scenarios so a drifting
// fake is caught mechanically (findings 0050).

using System.Collections.Generic;
using Newtonsoft.Json;

public sealed class PythonnetMarimoSynthesizer : IMarimoSynthesizer
{
    readonly WorkspaceEngineHost _host;

    public PythonnetMarimoSynthesizer(WorkspaceEngineHost host)
    {
        _host = host;
    }

    public string Synthesize(IReadOnlyList<Cell> cells)
    {
        if (_host == null) return null;
        return _host.SynthesizeCells(CellJson.ToArray(cells).ToString(Formatting.None));
    }

    public IReadOnlyList<Cell> Decompose(string py, out string error)
    {
        if (_host == null) { error = "python not ready"; return null; }
        // #113: the host distinguishes "not a marimo notebook" (decompose_json -> None) from a broken
        // -syntax parse error and hands back the reason; the aggregate surfaces it as the Open error
        // (no 1-cell wrap). A non-null JSON still means success even though `error` is threaded out.
        string json = _host.DecomposeCells(py, out error);
        if (json == null) return null;            // host already set `error` (not-marimo / syntax / engine fault)
        var cells = CellJson.TryParse(json);
        if (cells == null)
            // status=="ok" but the cells array failed to parse — a marshalling/seam fault, NOT a content
            // verdict. Give it a DISTINCT reason (mirrors WorkspaceEngineHost's "engine error" leg) so the
            // aggregate's `?? "not a marimo notebook"` fallback can't mislabel a valid notebook hit by a
            // transient marshalling glitch as "not a marimo notebook" (honours the IMarimoSynthesizer
            // contract: `error` is non-null whenever the result is null).
            error = "notebook decode failed (cell parse error)";
        return cells;
    }
}
