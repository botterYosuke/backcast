// CellJson.cs — issue #81 cell-as-floating-window (ADR-0013, shared seam marshaling)
//
// The ONE place the {body, name, config} JSON shape lives for IMarimoSynthesizer implementations.
// The real (PythonnetMarimoSynthesizer, across pythonnet) and the AFK fake (FakeMarimoSynthesizer)
// MUST agree on this shape — the cell seam carries body+name+config opaquely (ADR-0013 Decision 3 /
// findings 0050), so a divergence between the two synthesizers' marshaling would let the fake pass
// while the real seam disagrees. Extracting it here makes the shape a single edit. config is embedded
// as a nested JToken (NOT a JSON-string-inside-a-string); a malformed/empty config falls to {}.

using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public static class CellJson
{
    // Ordered cells -> a JSON array of {body,name,config} (cell order = .py cell order).
    public static JArray ToArray(IReadOnlyList<Cell> cells)
    {
        var arr = new JArray();
        if (cells != null)
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
        return arr;
    }

    // A JSON array of {body,name,config} -> ordered cells. Returns null on a non-array / malformed
    // string (the seam's fail-soft signal — the aggregate keeps its buffer and shows a notice).
    public static List<Cell> TryParse(string json)
    {
        if (json == null) return null;
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

    static JToken ParseConfig(string configJson)
    {
        if (string.IsNullOrEmpty(configJson)) return new JObject();
        try { return JToken.Parse(configJson); }
        catch { return new JObject(); }
    }
}
