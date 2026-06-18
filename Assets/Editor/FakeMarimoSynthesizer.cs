// FakeMarimoSynthesizer.cs — issue #81 cell-as-floating-window (ADR-0013, AFK test double)
//
// The Python-FREE IMarimoSynthesizer the layer-1/2 AFK gates and the root probes inject (the root's
// SetSynthesizer seam) so the cell model (synthesise / decompose / Save / Open / dirty / >=1 guard /
// dormant reuse) runs headless WITHOUT pythonnet. It satisfies the SAME contract the real
// PythonnetMarimoSynthesizer does — round-trip faithfulness (Decompose(Synthesize(cells)) preserves
// body+name+config) — which is exactly the property layer 2 (real pythonnet, once) and layer 3 (real
// marimo, the pytest golden) re-assert on the SAME scenarios, so a fake that drifts from marimo is
// caught mechanically (the shared-golden discipline, findings 0050).
//
// Encoding: a marker line + a JSON array of {body,name,config}. The "py" it writes is NOT real Python
// (it never runs through marimo here) — it is a reversible blob. Decompose of ARBITRARY text (no
// marker — e.g. a real strategy `.py` a seeding probe opens) leniently wraps the whole text as ONE
// anonymous cell, so the notebook binds. FailDecompose forces the fail-soft null (the broken-`.py` leg).

using System.Collections.Generic;
using Newtonsoft.Json;

public sealed class FakeMarimoSynthesizer : IMarimoSynthesizer
{
    const string Marker = "# fake-marimo-notebook";

    // Layer-1 fail-soft leg: when true, Decompose returns null (a broken / non-marimo `.py`).
    public bool FailDecompose;

    public string Synthesize(IReadOnlyList<Cell> cells)
        => Marker + "\n" + CellJson.ToArray(cells).ToString(Formatting.None);

    public IReadOnlyList<Cell> Decompose(string py)
    {
        if (FailDecompose) return null;
        if (py == null) return null;

        int nl = py.IndexOf('\n');
        if (nl >= 0 && py.Substring(0, nl).Trim() == Marker)
            return CellJson.TryParse(py.Substring(nl + 1));   // null on a corrupt fake blob -> fail-soft

        // arbitrary real `.py` (no marker): wrap the whole text as one anonymous cell so the notebook
        // can bind to it (the seeding probes read the file off disk, independent of cell decomposition).
        return new List<Cell> { new Cell(py, "_", "{}") };
    }
}
