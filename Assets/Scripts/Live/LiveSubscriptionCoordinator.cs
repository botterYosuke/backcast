// LiveSubscriptionCoordinator.cs — issue #107 "LiveManual market-data 購読の本番配線"
//
// The brain that turns user/mode events into live market-data subscriptions (方針: ADR-0022,
// findings 0086). A PLAIN C# class (UnityEngine-free, pythonnet-free) so the AFK probe drives it
// headlessly — the composition root (BackcastWorkspaceRoot) wires it to the real LiveRpcLanes via
// ISubscribeSink, feeds it the poll's execution_mode, and points UniverseSidebarController's
// row-select / [+ Add] paths at it.
//
// This closes the #107 death-zone: the subscribe CHAIN (SubmitSubscribeMarketData → orchestrator →
// runner → adapter → venue WS) was complete, but NOTHING in production STARTED it — UniverseSidebar
// Controller.LiveSubscribeHook was the #31 DEFERRED seam, never assigned, and the only caller of the
// RPC was the E2E runner itself. This coordinator is the production trigger.
//
// Two independent, load-bearing triggers (Live mode only; Replay is a no-op — the engine
// precondition-rejects subscribe there):
//   (1) rising edge into a Live mode  → bulk-subscribe the WHOLE universe at entry (LiveManual 突入
//       / universe 復元-before-entry / AC#1, AC#5). One batch RPC (engine gathers sequentially).
//   (2) LiveSubscribeHook            → subscribe ONE instrument on row-select OR [+ Add] (AC#2). This
//       is the per-instrument trigger for ids added after entry; it is NOT made redundant by (1)
//       because (1) only fires on the entry edge, not on later adds (there is deliberately no
//       universe-Changed auto-subscribe — that would make this hook redundant and break the AC#6
//       delete-to-RED litmus).
//
// INVARIANT (ADR-0022 D3 — membership 不可侵): this NEVER writes the universe. It only READS
// InstrumentRegistry.Ids and subscribes. Subscription is subordinate to membership; the system never
// adds/removes/prunes instruments because of subscription (that would repeat the #253 prune incident).
// Venue hard limits surface as typed errors from the engine (SUBSCRIPTION_LIMIT_EXCEEDED) — we do not
// silently drop ids here. No artificial count cap (撤去済み・ADR-0022 D2).

using System;
using System.Collections.Generic;

// The subscribe egress. Production = LaneSubscribeSink over LiveRpcLanes; tests can record calls.
public interface ISubscribeSink
{
    void Subscribe(string instrumentId);                          // single (row select / one add)
    void SubscribeBatch(IReadOnlyList<string> instrumentIds);     // bulk (entry / restore)
}

public sealed class LiveSubscriptionCoordinator
{
    readonly ISubscribeSink _sink;
    readonly InstrumentRegistry _universe;
    // C#-side dedup mirror so a bulk-entry that already covered an id doesn't re-emit when the user
    // then selects it. The engine runner.subscribe is also idempotent, so this is best-effort chatter
    // reduction, not a correctness gate.
    readonly HashSet<string> _subscribed = new HashSet<string>();
    string _lastMode = "Replay";

    public LiveSubscriptionCoordinator(ISubscribeSink sink, InstrumentRegistry universe)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _universe = universe ?? throw new ArgumentNullException(nameof(universe));
    }

    static bool IsLive(string mode) => mode == "LiveManual" || mode == "LiveAuto";

    // Assigned to UniverseSidebarController.LiveSubscribeHook by the host, and invoked by the controller
    // on a Live row-select AND on a Live [+ Add] (the controller gates both on UniverseSourceMode.Live),
    // so this is always a Live single-instrument subscribe.
    public void OnLiveRowSelected(string instrumentId)
    {
        if (string.IsNullOrEmpty(instrumentId)) return;
        if (!_subscribed.Add(instrumentId)) return;   // already subscribed → no-op
        _sink.Subscribe(instrumentId);
    }

    // Fed from the poll loop's execution_mode (FooterModeViewModel.DisplayMode). A rising edge into a
    // Live mode bulk-subscribes the universe; leaving Live resets the dedup mirror so re-entry re-subscribes
    // (idempotent on the engine side). LiveManual⇄LiveAuto is not an edge (both Live) → no re-bulk.
    public void OnModePoll(string mode)
    {
        bool wasLive = IsLive(_lastMode);
        bool nowLive = IsLive(mode);
        _lastMode = mode;
        if (nowLive && !wasLive) BulkSubscribeUniverse();          // rising edge → bulk subscribe
        else if (!nowLive && wasLive) _subscribed.Clear();         // falling edge → reset dedup
    }

    void BulkSubscribeUniverse()
    {
        var fresh = new List<string>();
        foreach (string id in _universe.Ids)
            if (!string.IsNullOrEmpty(id) && _subscribed.Add(id)) fresh.Add(id);
        if (fresh.Count == 1) _sink.Subscribe(fresh[0]);
        else if (fresh.Count > 1) _sink.SubscribeBatch(fresh);
    }
}
