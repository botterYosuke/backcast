// IMarimoSynthesizer.cs — issue #81 "cell-as-floating-window" (ADR-0013 Decision 3, seam)
//
// The injectable seam between the C# notebook aggregate (MarimoNotebookDocument) and marimo's
// native code generation. C# = spatial UI; Python(marimo) = synthesis/decomposition + DAG: C#
// NEVER reimplements def/ref/return analysis ([[ttwr-parity-first]]). Save synthesises the ordered
// cells into one `.py`; Open decomposes a `.py` back into ordered cells.
//
// Two implementations (the same shared-golden discipline as IStrategyFileProvider, findings 0050):
//   * PythonnetMarimoSynthesizer — production, calls cell_synthesis.py
//     (generate_filecontents / load_app) through WorkspaceEngineHost under the single Python
//     owner's GIL (ADR-0009).
//   * a fake — injected in the layer-1 AFK gate so the aggregate's model logic (synthesis order /
//     dirty / >=1 delete guard / dormant reuse) is driven WITHOUT pythonnet. The fake satisfies the
//     SAME contract (Decompose(Synthesize(cells)) preserves body+name+config + the >=1 invariant);
//     layer 2 (real pythonnet, once) and layer 3 (real marimo, the pytest golden) assert the same
//     scenarios so a fake that drifts from marimo is caught mechanically.
//
// The seam carries body + name + config (NOT body-only): see Cell. UnityEngine-FREE.

using System.Collections.Generic;

public interface IMarimoSynthesizer
{
    // Synthesise the ordered cells (= the `.py` cell order, the authoritative ordering) into one
    // canonical marimo `.py`. NEVER throws on a malformed body (marimo's safe_serialize_cell emits
    // an unparsable-cell marker); returns null only on an unexpected seam failure.
    string Synthesize(IReadOnlyList<Cell> cells);

    // Decompose a `.py` back into ordered cells (body + name + config). Returns null on a broken /
    // non-marimo `.py` (FAIL-SOFT, findings 0044): the aggregate keeps the live buffer untouched and
    // the caller shows a notice — a parse failure never wipes the editor.
    IReadOnlyList<Cell> Decompose(string py);
}
