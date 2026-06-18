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
using Newtonsoft.Json.Linq;

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
        var arr = new JArray();
        if (cells != null)
        {
            foreach (var c in cells)
            {
                if (c == null) continue;
                arr.Add(new JObject
                {
                    ["body"] = c.Body ?? string.Empty,
                    ["name"] = string.IsNullOrEmpty(c.Name) ? "_" : c.Name,
                    ["config"] = ParseConfig(c.ConfigJson),
                });
            }
        }
        return _host.SynthesizeCells(arr.ToString(Formatting.None));
    }

    public IReadOnlyList<Cell> Decompose(string py)
    {
        if (_host == null) return null;
        string json = _host.DecomposeCells(py);
        if (json == null) return null;   // fail-soft (broken/non-marimo .py)

        JArray arr;
        try { arr = JArray.Parse(json); }
        catch { return null; }

        var cells = new List<Cell>(arr.Count);
        foreach (var tok in arr)
        {
            string body = (string)tok["body"] ?? string.Empty;
            string name = (string)tok["name"] ?? "_";
            string cfg = tok["config"] != null ? tok["config"].ToString(Formatting.None) : "{}";
            cells.Add(new Cell(body, name, cfg));
        }
        return cells;
    }

    // The Cell's opaque config JSON re-embedded as a JToken so the cells array carries it as a
    // nested object (NOT a JSON-string-inside-a-string). A malformed/empty config falls to {}.
    static JToken ParseConfig(string configJson)
    {
        if (string.IsNullOrEmpty(configJson)) return new JObject();
        try { return JToken.Parse(configJson); }
        catch { return new JObject(); }
    }
}
