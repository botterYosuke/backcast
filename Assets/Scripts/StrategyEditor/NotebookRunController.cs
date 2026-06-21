// NotebookRunController.cs — #95 Phase 2 土台 (ADR-0016 D2 / findings 0071 P2-3), orchestration brain
//
// Turns a per-cell RUN press into: (1) the LIVE notebook source + the pressed cell's index, (2) a
// queued run on the NotebookRunLane, (3) routing each ran cell's output text back to ITS window. A
// PLAIN C# class (NOT a MonoBehaviour), DI-driven — same altitude as NotebookCellCoordinator: the
// root WIRES it (per-window RUN button -> RunCell; per-frame DrainAndRoute) and injects the lane +
// view lookup. MonoBehaviour-free so the layer-1/2 AFK gate drives RunCell/DrainAndRoute with a
// synchronous lane + Python-FREE fake executor, asserting the index->window routing directly.
//
// Routing is by ABSOLUTE cell index (the lane result carries it), resolved against the CURRENT cell
// list at drain time — so a cell that was deleted between submit and drain is simply skipped.

using System;
using System.Collections.Generic;

public sealed class NotebookRunController
{
    readonly NotebookCellCoordinator _coordinator;
    readonly Func<string, StrategyEditorView> _viewFor;
    readonly NotebookRunLane _lane;
    readonly Action<string> _onError;   // optional run-level error surfacer (null-tolerant)
    readonly Func<string> _scenarioJsonProvider;     // #95 P4: committed scenario JSON (null-tolerant)
    readonly Action _onStop;                         // #95 P4: ■ press → force-stop the running backtest
    readonly Action<string, bool> _onRunningChanged;  // #95 P4: (region, running) → swap ▶/■ on the cell
    readonly Action<IReadOnlyList<string>> _onStaleRegionsChanged;  // #95 P6 S3: regions still stale → amber ▶
    int _generation;                    // notebook epoch; bumped by Invalidate to drop stale in-flight runs

    int _runSeq;                        // monotonic run id source
    bool _btRunActive;                  // a backtest-driving run is in flight (running guard, ADR-0016 D3)
    int _btRunId = -1;                  // the in-flight backtest's run id (correlates its result)
    string _runningRegion;              // the cell whose ▶ is currently showing ■

    public bool IsBacktestRunning => _btRunActive;

    public NotebookRunController(
        NotebookCellCoordinator coordinator,
        Func<string, StrategyEditorView> viewFor,
        NotebookRunLane lane,
        Action<string> onError = null,
        Func<string> scenarioJsonProvider = null,
        Action onStop = null,
        Action<string, bool> onRunningChanged = null,
        Action<IReadOnlyList<string>> onStaleRegionsChanged = null)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _viewFor = viewFor ?? (_ => null);
        _lane = lane ?? throw new ArgumentNullException(nameof(lane));
        _onError = onError;
        _scenarioJsonProvider = scenarioJsonProvider;
        _onStop = onStop;
        _onRunningChanged = onRunningChanged;
        _onStaleRegionsChanged = onStaleRegionsChanged;
    }

    // A RUN press on the cell in `regionId`: synthesise the LIVE source (unsaved buffer ok — owner
    // HITL) and queue {source, pressed index}. No-op for an unknown region or a synthesiser failure.
    //
    // #95 Phase 4: when the notebook drives a backtest (bt.replay/bt.step) AND a scenario is
    // committed, the committed scenario rides along (the backend builds the bt handle) and the cell
    // enters the RUNNING state (▶→■). A second RUN while a backtest is in flight is REJECTED, not
    // queued (ADR-0016 D3) — the running cell's ■ stops it first.
    //
    // #95 Phase 5 (findings 0074): a STEP-only cell (`bt.step` in source, no `bt.replay`) does NOT
    // activate the running guard / ▶→■ toggle.  Step is an instant single-bar operation and is
    // INTENTIONALLY stateful across presses (the cell author wrote `bar = bt.step()` and expects
    // each press to advance the pointer by 1), so showing ■ between presses would be misleading
    // and the guard would reject the very next press the user expects to fire.  Only `bt.replay`
    // (a multi-bar transaction with potentially long pacing) activates the guard.
    public void RunCell(string regionId)
    {
        if (_btRunActive)
        {
            _onError?.Invoke("a backtest is already running — press ■ to stop it before running another cell");
            return;
        }
        var cell = _coordinator.CellOf(regionId);
        if (cell == null) return;
        int index = _coordinator.Notebook.IndexOf(cell);
        if (index < 0) return;
        string source = _coordinator.Notebook.SynthesizeLiveSource();
        if (source == null) { _onError?.Invoke("could not synthesise the notebook source"); return; }

        bool drivesReplay = source.Contains("bt.replay");
        bool drivesStep = source.Contains("bt.step");
        bool drivesBacktest = drivesReplay || drivesStep;
        string scenarioJson = drivesBacktest ? _scenarioJsonProvider?.Invoke() : null;
        int runId = ++_runSeq;
        _lane.Submit(new NotebookRunRequest
        {
            Source = source,
            PressedIndex = index,
            Generation = _generation,
            ScenarioJson = scenarioJson,
            RunId = runId,
        });

        // #95 Phase 5: only `bt.replay` activates the running guard / ▶→■ toggle.  `bt.step` is
        // instant per press and persistent across presses, so guarding it would block the very
        // next press the user expects to fire.
        if (drivesReplay && !string.IsNullOrEmpty(scenarioJson))
        {
            _btRunActive = true;
            _btRunId = runId;
            _runningRegion = regionId;
            _onRunningChanged?.Invoke(regionId, true);   // ▶ → ■ on this cell
        }
    }

    // #95 Phase 6 Slice 4 (findings 0075 P6-1): an edit/blur RESTAGE. Synthesise the LIVE notebook
    // source (unsaved buffer ok) and queue a restage on the SAME worker lane as RunCell — the
    // IncrementalNotebookSession is thread-guarded to the lane worker, so a direct host call from Unity
    // main (the onEndEdit handler) would be rejected. The result carries ONLY the post-edit stale set
    // (no ran cells) and drains through the SAME ApplyResult path, which routes it to the amber ▶ badges
    // exactly like a run's residual stale set (the just-edited cells light up; re-pressing clears them).
    // No-op on a synthesiser failure (leave the badges untouched). The notebook epoch rides along so a
    // restage queued against a notebook that is then replaced (File→Open/New) is dropped at drain time.
    public void Restage()
    {
        string source = _coordinator.Notebook.SynthesizeLiveSource();
        if (source == null) return;
        _lane.Submit(new NotebookRunRequest
        {
            Source = source,
            PressedIndex = -1,
            Generation = _generation,
            ScenarioJson = null,
            RunId = ++_runSeq,
            IsRestage = true,
        });
    }

    // The running cell's ■ press: ask the engine to stop the in-flight backtest. The run still
    // completes (the stepper returns STOPPED) and its result drains through ApplyResult, which
    // clears the busy flag and restores ▶. No-op when nothing is running.
    public void StopRunning()
    {
        if (_btRunActive) _onStop?.Invoke();
    }

    // Bump the notebook epoch so any in-flight run's result is dropped at drain time — call when the
    // cell list is structurally REPLACED (File→Open / File→New / restore), so a run queued against the
    // OLD document never paints its output into the same-index cell of the NEW one.
    public void Invalidate() => _generation++;

    // Drain every completed run and route each ran cell's output to its window. Called per frame by
    // the root (and directly by the AFK gate after a synchronous Submit).
    public void DrainAndRoute()
    {
        while (_lane.TryDrainResult(out var result))
            ApplyResult(result);
    }

    void ApplyResult(NotebookRunResult result)
    {
        if (result == null) return;
        // Release the running guard the moment the in-flight backtest's result arrives — BEFORE the
        // generation check, so a notebook replaced mid-run still clears the flag and restores ▶
        // (the routing below is still skipped for the stale result).
        if (_btRunActive && result.RunId == _btRunId)
        {
            _btRunActive = false;
            _btRunId = -1;
            string region = _runningRegion;
            _runningRegion = null;
            _onRunningChanged?.Invoke(region, false);   // ■ → ▶
        }
        if (result.Generation != _generation) return;   // stale: the notebook was replaced mid-flight
        if (!result.Ok && !string.IsNullOrEmpty(result.Error)) _onError?.Invoke(result.Error);

        var cells = _coordinator.Notebook.Cells;

        // #95 Phase 6 Slice 3 (findings 0075 P6-1): project the post-run stale set (cell-order indices
        // still needing a press) to their regions and hand them to the badge driver — the root paints
        // amber ▶ on stale windows and green ▶ elsewhere. Mapped here (the controller owns the cell
        // list); the root just paints. The just-pressed cell ran, so it is no longer stale → re-pressing
        // a stale cell is how the user clears its amber.
        if (_onStaleRegionsChanged != null)
        {
            var staleRegions = new List<string>();
            if (result.Stale != null)
            {
                foreach (var idx in result.Stale)
                {
                    if (idx < 0 || idx >= cells.Count) continue;
                    string r = _coordinator.RegionOf(cells[idx]);
                    if (r != null) staleRegions.Add(r);
                }
            }
            _onStaleRegionsChanged(staleRegions);
        }

        if (result.Ran == null) return;
        foreach (var co in result.Ran)
        {
            if (co.Index < 0 || co.Index >= cells.Count) continue;   // stale index (cell deleted) — skip
            string region = _coordinator.RegionOf(cells[co.Index]);
            if (region == null) continue;
            var view = _viewFor(region);
            if (view == null) continue;
            view.SetOutput(co.Output, co.Mimetype, co.Data);
            // #102 Slice 2 (findings 0079): a cell that ran this press also has its console replaced
            // (an empty segment list hides the console block — auto-collapse).  Cells that did NOT run
            // this press are simply absent from result.Ran, so their console pane is untouched
            // (marimo `clear_console=False on cells that ran` parity is at the Python boundary; here
            // we just paint what each ran cell carried back).
            view.SetConsole(co.Console);
        }
    }
}
