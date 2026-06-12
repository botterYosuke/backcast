// ReplayEventSink.cs — issue #9 S1 "Replay seam tracer" (M2 move 3)
//
// The C# end of the replay sink contract. A single instance is constructed in
// C# and handed to the Python worker as cfg["rust_sink"]; the Nautilus replay
// engine (engine.live.gui_bridge_actor / nautilus_backtest_runner) duck-calls
// its 5 push_* methods FROM the Python daemon backtest thread while that thread
// holds Py.GIL(). This is the Python->C# (reverse) callback under Mono that S0
// did NOT exercise.
//
// pythonnet method exposure: pythonnet exposes CLR members to Python under their
// VERBATIM names (no snake_case<->PascalCase translation by default). Python
// calls obj.push_bar(str), so the C# methods MUST be named push_bar/push_order/
// push_portfolio/push_run_complete/push_run_failed exactly (lowercase+underscore
// are legal C# identifiers). Renaming any to PascalCase would break the duck call.
//
// Threading contract (mirrors S0's Interlocked/Volatile discipline):
//   * push_* run on the Python worker thread UNDER the GIL. Each only does a
//     ConcurrentQueue.Enqueue + an Interlocked counter bump and returns at once —
//     NO Unity main-thread API is touched under the GIL.
//   * The move-4 headless probe drains the queues + reads counters/flags from the
//     main thread WITHOUT the GIL; ConcurrentQueue + Interlocked + volatile are
//     the GIL-free channels across the boundary.
//   * push_* must NEVER throw: the engine swallows sink exceptions as warnings
//     (GuiBridgeActor._on_bar), which would silently drop bars. Every body here
//     is provably non-throwing (Enqueue / Interlocked / reference assignment), so
//     no try/catch is needed — and none is added, to avoid masking real spike bugs.
//
// Counting policy: push_bar bumps a producer-side _pushed counter, but the move-4
// gate stays AUTHORITATIVE on the drain side (count TryDequeueBar successes and
// assert pushed == drained && pushed > 0 && every payload parses), mirroring the
// CPython gate in python/spike/s1_adapter_smoke.py.
//
// INTERMEDIATE STATE: this class compiles but is UNUSED until move 4 wires the
// headless probe that constructs it and drives the backtest. No .meta is authored
// here — Unity generates ReplayEventSink.cs.meta (and the S1Spike folder .meta) on
// the next import/batchmode run.

using System.Collections.Concurrent;
using System.Threading;

public class ReplayEventSink
{
    // --- GIL-free channels: Python worker enqueues, main-thread probe drains ---
    readonly ConcurrentQueue<string> _bars = new ConcurrentQueue<string>();
    readonly ConcurrentQueue<string> _orders = new ConcurrentQueue<string>();
    readonly ConcurrentQueue<string> _portfolios = new ConcurrentQueue<string>();

    // Producer-side counters (Interlocked write on worker, Interlocked read on main).
    long _pushed;
    long _ordersPushed;
    long _portfoliosPushed;

    // Completion signalling (volatile: worker writes once, main polls).
    volatile bool _completed;
    volatile bool _failed;
    volatile string _runId;
    volatile string _summary;
    volatile string _error;

    // ======================================================================
    // 5-method sink contract — called from the Python worker thread UNDER the
    // GIL. Names are VERBATIM snake_case so pythonnet duck-typing resolves them.
    // ======================================================================

    public void push_bar(string json)
    {
        _bars.Enqueue(json);
        Interlocked.Increment(ref _pushed);
    }

    public void push_order(string json)
    {
        _orders.Enqueue(json);
        Interlocked.Increment(ref _ordersPushed);
    }

    public void push_portfolio(string json)
    {
        _portfolios.Enqueue(json);
        Interlocked.Increment(ref _portfoliosPushed);
    }

    public void push_run_complete(string run_id, string summary)
    {
        _runId = run_id;
        _summary = summary;
        _completed = true;
    }

    public void push_run_failed(string err)
    {
        _error = err;
        _failed = true;
    }

    // ======================================================================
    // Drain / observation surface — called from the main (probe) thread,
    // GIL-free. ConcurrentQueue.TryDequeue + Interlocked.Read + volatile reads.
    // ======================================================================

    public bool TryDequeueBar(out string json) => _bars.TryDequeue(out json);
    public bool TryDequeueOrder(out string json) => _orders.TryDequeue(out json);
    public bool TryDequeuePortfolio(out string json) => _portfolios.TryDequeue(out json);

    public long Pushed => Interlocked.Read(ref _pushed);
    public long OrdersPushed => Interlocked.Read(ref _ordersPushed);
    public long PortfoliosPushed => Interlocked.Read(ref _portfoliosPushed);

    public bool Completed => _completed;
    public bool Failed => _failed;
    public string RunId => _runId;
    public string Summary => _summary;
    public string Error => _error;
}
