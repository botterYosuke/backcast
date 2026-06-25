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
//     dirty / delete-to-0 / dormant reuse) is driven WITHOUT pythonnet. The fake satisfies the
//     SAME contract (Decompose(Synthesize(cells)) preserves body+name+config + #146/ADR-0033 D2's
//     persistence floor=1: an empty notebook decomposes back to ONE empty cell, marimo's load_app
//     behaviour); layer 2 (real pythonnet, once) and layer 3 (real marimo, the pytest golden) assert
//     the same scenarios so a fake that drifts from marimo is caught mechanically.
//
// The seam carries body + name + config (NOT body-only): see Cell. UnityEngine-FREE.

using System.Collections.Generic;

public interface IMarimoSynthesizer
{
    // Synthesise the ordered cells (= the `.py` cell order, the authoritative ordering) into one
    // canonical marimo `.py`. NEVER throws on a malformed body (marimo's safe_serialize_cell emits
    // an unparsable-cell marker); returns null only on an unexpected seam failure.
    string Synthesize(IReadOnlyList<Cell> cells);

    // Decompose a `.py` back into ordered cells (body + name + config). Returns null when the source
    // is NOT a marimo notebook (#113: the editor is "marimo or error" at Open time — the 1-cell
    // auto-wrap of findings 0054 was retired), setting `error` to a user-facing reason:
    //   * "not a marimo notebook"  — a loadable non-marimo `.py` (no `app = marimo.App()`);
    //   * "syntax error: <detail>" — a broken-syntax source (a DISTINCT failure, #113 AC#2);
    //   * a seam-unavailable reason — the Python owner is not ready (pre-init).
    // `error` is null on success. The aggregate surfaces `error` as its Open LastError (no wrap).
    IReadOnlyList<Cell> Decompose(string py, out string error);
}
