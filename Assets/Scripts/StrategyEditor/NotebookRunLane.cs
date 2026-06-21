// NotebookRunLane.cs — #95 Phase 2 土台 (ADR-0016 D2 / findings 0070 F5 / findings 0071 P2-2/P2-3)
//
// The dedicated worker lane for per-cell RUN (pure computation; engine NOT connected). marimo's
// RuntimeContext is thread-local (thin_drain.py:152), so the persistent in-proc kernel must be
// built AND run from ONE consistent thread — this lane owns that single background thread, so the
// press returns immediately (the click never blocks on the run) and the kernel is never touched
// from Unity main or the Replay launcher. RUNs QUEUE (one engine, one thread — F5): a second press
// while one run is in flight waits its turn. NOTE: Python work still serialises on the GIL, so a
// long pure-compute cell can still make the main thread's NEXT GIL op wait until the run yields —
// 土台 cells are expected to be light; cooperative pacing is a later phase, not Phase 2.
//
// The work itself is delegated to an INotebookCellExecutor so the AFK gate
// (StrategyEditorNotebookE2ERunner) can inject a Python-FREE fake and assert the C# wiring
// (button presence + index->window output routing) without standing up the embedded interpreter;
// the real reactive correctness is the Python pytest gate (test_notebook_interactive_run.py).
//
// Lifecycle: Submit(req) from main -> queued -> worker calls executor.Run -> result enqueued ->
// the root drains TryDrainResult() each frame and routes each cell's output to its window. The
// executor result carries ABSOLUTE cell indices, so routing needs no request<->result correlation.

using System;
using System.Collections.Concurrent;
using System.Threading;
using UnityEngine;

// One RUN press: the LIVE synthesised marimo source + the pressed cell's order index. Generation is
// the notebook epoch at submit time — a result whose generation no longer matches (the notebook was
// replaced by File→Open/New mid-flight) is dropped instead of painted into a different document.
public struct NotebookRunRequest
{
    public string Source;
    public int PressedIndex;
    public int Generation;
    // #95 Phase 4: the committed startup-panel scenario (JSON). Non-null lets the backend build a
    // bt handle when the notebook drives a backtest (ADR-0016 D4). Null = pure-compute 土台 press.
    public string ScenarioJson;
    // Monotonic per-run id so the controller's running guard can correlate THIS run's result and
    // clear the busy flag for the right run (not a faster pure-compute run that drained first).
    public int RunId;
    // #95 Phase 6 Slice 4 (findings 0075 P6-1): an edit/blur RESTAGE rather than a RUN. The worker
    // calls executor.Restage(Source) instead of Run, and the result carries ONLY the post-edit stale
    // set (no ran cells). Routed on the SAME lane so the incremental session's thread guard is honoured.
    public bool IsRestage;
}

// One ordered console segment carried back from a cell's stdout/stderr (#102 Slice 2, findings 0079).
// Adjacent same-stream writes are already collapsed on the Python side (marimo cell.ts:133 /
// collapseConsoleOutputs.tsx parity), so the array reflects arrival order with stream switches as
// segment boundaries.  C# concatenates with per-stream colour to paint the console pane.
public struct ConsoleSegment
{
    public string Stream;   // "stdout" | "stderr"
    public string Text;
}

// One ran cell's result (pressed cell or a reactive descendant), by cell-order index.
public struct NotebookCellOutput
{
    public int Index;
    public string Output;
    public bool Ok;
    // #95 Phase 6 Slice 2 (findings 0075 P6-2): the cell's REAL rich output — `Mimetype` (image/png,
    // text/markdown, text/plain, …) and `Data` (base64 for images, text otherwise). `Output` stays the
    // interim plain-text projection the current Text renderer paints; Slice 5 renders image/markdown/
    // table natively from these. Absent on a legacy/text-only payload → empty (backward compatible).
    public string Mimetype;
    public string Data;
    // #102 Slice 2 (findings 0079): the cell's stdout/stderr in arrival order. Null/empty for a cell
    // that produced no console output OR for a legacy/test fake that did not populate it.
    public ConsoleSegment[] Console;
}

// The result of one RUN: the cells that ran (pressed + reactive downstream) + a run-level error.
// Generation is copied from the originating request so a stale in-flight result can be discarded.
public sealed class NotebookRunResult
{
    public bool Ok;
    public NotebookCellOutput[] Ran;
    public string Error;
    public int Generation;
    public int RunId;   // copied from the request so the controller can match its busy-flag run (#95 P4)
    // #95 Phase 6 Slice 2 (findings 0075 P6-1): cell-order indices still STALE after this run (cells
    // edited but not yet re-pressed). The controller routes these to amber ▶ badges (Slice 3). Empty
    // on a legacy payload that omits the key (backward compatible).
    public int[] Stale = Array.Empty<int>();

    public static NotebookRunResult Failure(string error)
        => new NotebookRunResult { Ok = false, Ran = Array.Empty<NotebookCellOutput>(), Error = error };
}

// Runs one press. Real impl crosses pythonnet (HostNotebookCellExecutor); the AFK gate injects a fake.
// scenarioJson (#95 Phase 4) is the committed scenario (null for a pure-compute press).
public interface INotebookCellExecutor
{
    NotebookRunResult Run(string source, int pressedIndex, string scenarioJson);
    // #95 Phase 6 Slice 4 (findings 0075 P6-1): edit-time stale projection — diff-register the live
    // source WITHOUT running any cell. Returns the cell-order indices still stale (empty when nothing
    // is stale). Runs on the lane worker thread (same thread discipline as Run).
    int[] Restage(string source);
}

public sealed class NotebookRunLane : IDisposable
{
    readonly INotebookCellExecutor _executor;
    readonly BlockingCollection<NotebookRunRequest> _requests;
    readonly ConcurrentQueue<NotebookRunResult> _results = new ConcurrentQueue<NotebookRunResult>();
    readonly Thread _worker;
    volatile bool _disposed;

    // startWorker:false = synchronous mode: Submit runs the executor inline on the caller thread (the
    // AFK gate drives it deterministically without the background thread / per-frame pump).
    public NotebookRunLane(INotebookCellExecutor executor, bool startWorker = true)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        if (startWorker)
        {
            _requests = new BlockingCollection<NotebookRunRequest>();
            _worker = new Thread(Loop) { IsBackground = true, Name = "NotebookRunLane" };
            _worker.Start();
        }
    }

    // Queue a press (worker mode) or run it inline (synchronous mode). Never throws.
    public void Submit(NotebookRunRequest req)
    {
        if (_disposed) return;
        if (_requests != null)
        {
            try { _requests.Add(req); } catch (InvalidOperationException) { /* completed/disposed */ }
        }
        else
        {
            ProcessOne(req);
        }
    }

    // Main-thread drain: pull one completed result to route into the windows. Returns false when empty.
    public bool TryDrainResult(out NotebookRunResult result) => _results.TryDequeue(out result);

    void Loop()
    {
        try
        {
            foreach (var req in _requests.GetConsumingEnumerable())
                ProcessOne(req);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[NotebookRunLane] worker loop ended: " + e.Message);
        }
    }

    void ProcessOne(NotebookRunRequest req)
    {
        NotebookRunResult r;
        try
        {
            if (req.IsRestage)
            {
                // #95 Phase 6 Slice 4 (findings 0075 P6-1): an edit/blur restage — diff-register the live
                // source WITHOUT running any cell and carry back ONLY the post-edit stale set (no ran
                // cells). Same worker thread as Run, so the incremental session's thread guard is honoured.
                int[] stale = _executor.Restage(req.Source) ?? Array.Empty<int>();
                r = new NotebookRunResult { Ok = true, Ran = Array.Empty<NotebookCellOutput>(), Stale = stale };
            }
            else
            {
                r = _executor.Run(req.Source, req.PressedIndex, req.ScenarioJson)
                    ?? NotebookRunResult.Failure("executor returned null");
            }
        }
        catch (Exception e)
        {
            r = NotebookRunResult.Failure(e.Message);
        }
        r.Generation = req.Generation;   // carry the epoch so the router can drop a stale in-flight result
        r.RunId = req.RunId;             // carry the run id so the controller clears the right busy flag
        _results.Enqueue(r);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_requests != null)
        {
            _requests.CompleteAdding();
            try { _worker?.Join(500); } catch { }
            _requests.Dispose();
        }
    }
}
