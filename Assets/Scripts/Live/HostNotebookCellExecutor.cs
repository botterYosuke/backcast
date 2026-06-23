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

    public NotebookRunResult Run(string source, int pressedIndex, string scenarioJson, string strategyPath)
    {
        // GIL acquired inside; worker thread. scenarioJson (#95 Phase 4) lets the backend build a bt
        // handle when the notebook drives a backtest. strategyPath (the document's canonical .py path,
        // #78 provider) gives the marimo cell globals the right __file__ for artifact resolution.
        //
        // #100 Slice ① (findings 0077): the run_summary key returned by run_cell is no longer
        // surfaced into a C# field — Python's _finalize_run wrote it to engine.last_run_summary
        // before returning, and LiveRpcLanes is polling get_run_summary_json, so the Hakoniwa
        // RunResult tile sees the finalize via the SAME poll-symmetric path as portfolio (LatestRunSummary).
        // Single source = Python; the set-at-return path that previously lived here was the gap
        // that caused #100 ① (a re-press carried run1's stats through run2's running view because
        // the C# field was set per-press but never cleared at run start).
        string json = _host.InvokeRunCell(source, pressedIndex, scenarioJson, strategyPath);
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

    // #102 Slice 2 (findings 0079): pull the `console` JArray of {stream, text} segments off a ran row.
    // Defensive: a missing/typed-off field yields an empty array; a malformed entry is skipped (a stale
    // executor or future-format payload must never crash routing).
    static ConsoleSegment[] ExtractConsole(JToken item)
    {
        if (!(item is JObject o) || !(o["console"] is JArray arr)) return Array.Empty<ConsoleSegment>();
        var segs = new List<ConsoleSegment>(arr.Count);
        foreach (var s in arr)
        {
            if (!(s is JObject seg)) continue;
            segs.Add(new ConsoleSegment
            {
                Stream = seg.Value<string>("stream") ?? "stdout",
                Text = seg.Value<string>("text") ?? string.Empty,
            });
        }
        return segs.ToArray();
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
                        // #102 Slice 2 (findings 0079): per-cell stdout/stderr segments in arrival order
                        // (adjacent same-stream already collapsed on the Python side).
                        Console = ExtractConsole(item),
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
