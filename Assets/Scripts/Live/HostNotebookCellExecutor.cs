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
        string json = _host.InvokeRunCell(source, pressedIndex, scenarioJson);
        var result = Parse(json);
        // A bt-driven run returns a "run_summary" key — set the Hakoniwa run_result tile to it so it
        // fills like a title-bar Run (the live portfolio/positions already stream via the poll lane).
        // The value may be null (the run stopped/crashed before finalize): set it anyway so a STALE
        // prior summary is cleared. A pure-compute press OMITS the key → leave the tile untouched.
        if (TryGetRunSummary(json, out string summary)) _host.SetReplayRunSummary(summary);
        return result;
    }

    // Returns true iff the backend reported a backtest drive (the "run_summary" key is present);
    // `summary` is its finalized JSON, or null when the driven run produced no summary.
    static bool TryGetRunSummary(string json, out string summary)
    {
        summary = null;
        if (string.IsNullOrEmpty(json)) return false;
        try
        {
            var token = JObject.Parse(json)["run_summary"];
            if (token == null) return false;   // key absent: a pure-compute press, not a backtest
            summary = token.Type == JTokenType.Null ? null : token.ToString(Newtonsoft.Json.Formatting.None);
            return true;
        }
        catch (Exception) { return false; }
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
                    });
                }
            }
            return new NotebookRunResult
            {
                Ok = o.Value<bool?>("ok") ?? false,
                Ran = ran.ToArray(),
                Error = o.Value<string>("error"),
            };
        }
        catch (Exception e)
        {
            return NotebookRunResult.Failure("bad backend JSON: " + e.Message);
        }
    }
}
