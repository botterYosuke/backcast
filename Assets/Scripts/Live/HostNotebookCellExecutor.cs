// HostNotebookCellExecutor.cs — #95 Phase 2 土台 (findings 0071 P2-3/P2-4)
//
// The production INotebookCellExecutor: crosses pythonnet (WorkspaceEngineHost.InvokeRunCell ->
// engine.inproc_server.run_cell) on the NotebookRunLane's worker thread and decodes the backend's
// JSON ({"ok","ran":[{"index","output","ok"}...],"error"}) into a NotebookRunResult. A null/unparsable
// response degrades to a run-level failure (the window shows nothing rather than throwing). The AFK
// gate substitutes a Python-FREE fake for this class.

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

public sealed class HostNotebookCellExecutor : INotebookCellExecutor
{
    readonly WorkspaceEngineHost _host;

    public HostNotebookCellExecutor(WorkspaceEngineHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
    }

    public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson)
    {
        // GIL acquired inside; worker thread. scenarioJson (#95 Phase 4) lets the backend build a bt
        // handle when the notebook drives a backtest.
        //
        // #100 slice①: the run_result tile is NO LONGER set from this return value. The completion
        // summary now follows the same poll-symmetric lifecycle as the portfolio snapshot — the
        // backend publishes engine.last_run_summary (cleared at run-begin, set at finalize) and the
        // poll lane (get_run_summary_json) carries it to RunSummaryJson. Setting it here at run-return
        // is what left the tile showing the PREVIOUS run's stats during a re-run's streaming (the
        // set lagged the whole synchronous run, with no run-begin clear). The backend still emits the
        // "run_summary" key in the result for the Python e2e contract; C# just doesn't consume it.
        string json = _host.InvokeRunCell(source, pressedIndex, scenarioJson);
        return Parse(json);
    }

    // #95 Phase 6 Slice 4 (findings 0075 P6-1): edit-time stale projection. Crosses pythonnet
    // (WorkspaceEngineHost.InvokeNotebookRestage -> engine.inproc_server.notebook_restage) on the
    // NotebookRunLane worker thread (same thread discipline as Run — the IncrementalNotebookSession is
    // thread-guarded to the lane worker). Returns the cell-order indices still stale; a null/unparsable
    // response degrades to an empty set (an edit must never crash the lane).
    public int[] Restage(string source)
    {
        string json = _host.InvokeNotebookRestage(source);
        return ParseStale(json);
    }

    // Decode the backend restage JSON ({"stale":[indices],"error"}). Defensive: a missing/typed-off
    // field never throws; an unparsable payload becomes an empty stale set.
    public static int[] ParseStale(string json)
    {
        if (string.IsNullOrEmpty(json)) return Array.Empty<int>();
        try
        {
            return ExtractStale(JObject.Parse(json));
        }
        catch (Exception)
        {
            return Array.Empty<int>();
        }
    }

    // Extract the `stale` JArray (cell-order indices) from an already-parsed object. A missing/typed-off
    // field yields an empty set; a non-int entry is skipped defensively. Shared by ParseStale (restage)
    // and Parse (run-result) so the two paths agree on stale semantics.
    static int[] ExtractStale(JObject o)
    {
        var stale = new List<int>();
        if (o["stale"] is JArray arr)
        {
            foreach (var s in arr)
            {
                int? v = s.Value<int?>();
                if (v.HasValue) stale.Add(v.Value);
            }
        }
        return stale.ToArray();
    }

    // Decode the backend JSON. Defensive: a missing/typed-off field never throws (a per-cell run must
    // not crash the lane); an unparsable payload becomes a run-level failure.
    public static NotebookRunResult Parse(string json)
    {
        if (string.IsNullOrEmpty(json))
            return NotebookRunResult.Failure("no response from backend");
        try
        {
            var o = JObject.Parse(json);
            var ran = new List<NotebookCellOutput>();
            if (o["ran"] is JArray arr)
            {
                foreach (var item in arr)
                {
                    ran.Add(new NotebookCellOutput
                    {
                        Index = item.Value<int?>("index") ?? -1,
                        Output = item.Value<string>("output") ?? string.Empty,
                        Ok = item.Value<bool?>("ok") ?? true,
                        // #95 Phase 6 Slice 2: rich output (absent on a legacy text-only payload → empty).
                        Mimetype = item.Value<string>("mimetype") ?? string.Empty,
                        Data = item.Value<string>("data") ?? string.Empty,
                    });
                }
            }
            return new NotebookRunResult
            {
                Ok = o.Value<bool?>("ok") ?? false,
                Ran = ran.ToArray(),
                Error = o.Value<string>("error"),
                // #95 Phase 6 Slice 2: top-level `stale` = cell-order indices still needing a press
                // (absent on a legacy payload → empty).
                Stale = ExtractStale(o),
            };
        }
        catch (Exception e)
        {
            return NotebookRunResult.Failure("bad backend JSON: " + e.Message);
        }
    }
}
