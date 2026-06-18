// Cell.cs — issue #81 "cell-as-floating-window" (ADR-0013, PURE CORE)
//
// One logical marimo cell as the notebook aggregate (MarimoNotebookDocument) owns it: the raw
// cell BODY text plus the NAME and CONFIG carried OPAQUELY (ADR-0013 Decision 3 / findings 0050).
// S1 does NOT edit names/configs (the name UI is a later slice) — they are captured on Open
// (decompose) and written back verbatim on Save (synthesise) so a NAMED notebook (#76's
// `v19_morning_cell.py`, `def _config()` / `def _strategy()`) round-trips byte-identically instead
// of collapsing `def _config()` -> `def _()`. A bodies-only cell got that wrong (findings 0050).
//
// A window (StrategyEditorView) is a VIEW over a Cell (marimo `cellData.code` central store): the
// view edits the body via SetBody, which fires the aggregate's dirty hook. UnityEngine-FREE so the
// layer-1 AFK gate drives the model headless; `ConfigJson` is the marimo CellConfig as opaque JSON
// (the seam never interprets it — only marimo does, in cell_synthesis.py).

using System;

public sealed class Cell
{
    string _body;
    Action _onBodyChanged;   // the aggregate's dirty hook (rebound on decompose)

    // The cell NAME (`_` = marimo's anonymous default for a freshly added cell) and the marimo
    // CellConfig as opaque JSON ("{}" = default). Immutable in S1 — opaque carriage only.
    public string Name { get; }
    public string ConfigJson { get; }
    public string Body => _body;

    public Cell(string body = "", string name = "_", string configJson = "{}")
    {
        _body = body ?? string.Empty;
        Name = string.IsNullOrEmpty(name) ? "_" : name;
        ConfigJson = string.IsNullOrEmpty(configJson) ? "{}" : configJson;
    }

    // Edit the body from the view. Fires the aggregate's dirty hook ONLY on an actual change (a
    // no-op assignment must not flip a clean notebook to dirty — mirrors StrategyDocument.SetText).
    public void SetBody(string body)
    {
        body ??= string.Empty;
        if (body == _body) return;
        _body = body;
        _onBodyChanged?.Invoke();
    }

    // Wire the aggregate's dirty hook. Aggregate-only: the aggregate calls this when it adopts a
    // cell (AddCell / decompose) so a body edit marks the notebook dirty (findings 0050: SetBody is
    // not the ONLY dirty source — add/delete/reorder dirty too, owned by the aggregate).
    public void BindBodyChanged(Action onBodyChanged) => _onBodyChanged = onBodyChanged;
}
