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
// marker — e.g. a real strategy `.py` a SEEDING probe opens) leniently wraps the whole text as ONE
// anonymous cell, so the notebook binds (this is a TEST-DOUBLE seeding convenience, NOT production
// policy — the seeding probes only need a bound document, they do not assert marimo detection).
//
// #146 (ADR-0033 D2): the persistence floor = 1 is marimo-derived. A 0-cell notebook synthesises to a
// header-only blob (marker + "[]"), but real marimo's load_app inflates an empty valid-marimo file back
// into ONE empty cell — so this fake's Decompose inflates an empty marker blob to one empty cell too,
// keeping the AFK X1 gate faithful to marimo (findings 0114 §2).
//
// #113: the production aggregate is now "marimo or error" at Open time (the 1-cell auto-wrap of
// findings 0054 was retired). The two PRODUCTION decompose-failure legs are modelled explicitly so
// the #113 AFK gates can drive them: FailDecompose => the non-marimo leg (Decompose -> null,
// error="not a marimo notebook"); SyntaxErrorDetail != null => the broken-syntax leg (Decompose ->
// null, error="syntax error: <detail>"). The aggregate surfaces `error` as its Open LastError.

using System.Collections.Generic;
using Newtonsoft.Json;

public sealed class FakeMarimoSynthesizer : IMarimoSynthesizer
{
    const string Marker = "# fake-marimo-notebook";

    // Production non-marimo leg: when true, Decompose returns null with "not a marimo notebook"
    // (PythonnetMarimoSynthesizer's decompose_json -> None case).
    public bool FailDecompose;

    // Production broken-syntax leg (#113 AC#2): when non-null, Decompose returns null with
    // "syntax error: <SyntaxErrorDetail>" (decompose_json raising SyntaxError). DISTINCT from the
    // non-marimo leg so a parse error is never masked. Takes precedence over FailDecompose.
    public string SyntaxErrorDetail;

    public string Synthesize(IReadOnlyList<Cell> cells)
        => Marker + "\n" + CellJson.ToArray(cells).ToString(Formatting.None);

    public IReadOnlyList<Cell> Decompose(string py, out string error)
    {
        if (SyntaxErrorDetail != null) { error = "syntax error: " + SyntaxErrorDetail; return null; }
        if (FailDecompose) { error = "not a marimo notebook"; return null; }
        if (py == null) { error = "not a marimo notebook"; return null; }

        error = null;
        int nl = py.IndexOf('\n');
        if (nl >= 0 && py.Substring(0, nl).Trim() == Marker)
        {
            var cells = CellJson.TryParse(py.Substring(nl + 1));   // null on a corrupt fake blob
            // #146 (ADR-0033 D2 / findings 0114 §2): mirror marimo's PERSISTENCE FLOOR = 1. A 0-cell
            // notebook synthesises to a header-only `.py` (here: marker + "[]"), but real marimo's
            // load_app inflates an empty valid-marimo file back to ONE empty cell — so the fake must
            // too, else the shared-golden discipline drifts (the AFK X1 gate would see 0, real marimo 1).
            if (cells != null && cells.Count == 0) cells.Add(new Cell("", "_", "{}"));
            return cells;
        }

        // arbitrary real `.py` (no marker): wrap the whole text as one anonymous cell so the notebook
        // can bind to it (the seeding probes read the file off disk, independent of cell decomposition).
        return new List<Cell> { new Cell(py, "_", "{}") };
    }
}
