// LaneSubscribeSink.cs — issue #107 production egress for LiveSubscriptionCoordinator.
//
// Bridges the (UnityEngine-free, pythonnet-free) LiveSubscriptionCoordinator to the real
// LiveRpcLanes write lane. Holds the host (not the lanes directly) so it resolves host.Lanes
// LAZILY at call time — the coordinator is constructed at BuildWorkspace before InitializePython
// builds the lanes, but Subscribe only ever fires on a Live-mode edge / row-select, which can only
// happen AFTER InitializePython + login/connect (the lanes are built by InitializePython and the
// engine must be in a Live mode before the coordinator's rising edge fires). So host.Lanes is always
// ready by the time Subscribe is called. The null guard is pure defense-in-depth, not a recovery path:
// the coordinator has no universe-Changed re-fire, so do NOT rely on a later trigger to retry a drop.
//
// Results are fire-and-forget here: venue hard limits surface as typed SUBSCRIPTION_LIMIT_EXCEEDED
// error codes in the OrderRpcResult, which we log but do not act on (ADR-0022 D3 — never drop ids /
// touch membership because of a subscribe outcome). Order operations are unaffected (shared write
// lane keeps them serialized).

using System.Collections.Generic;
using UnityEngine;

public sealed class LaneSubscribeSink : ISubscribeSink
{
    readonly WorkspaceEngineHost _host;

    public LaneSubscribeSink(WorkspaceEngineHost host) { _host = host; }

    public void Subscribe(string instrumentId)
    {
        var lanes = _host != null ? _host.Lanes : null;
        if (lanes == null) return;
        lanes.SubmitSubscribeMarketData(instrumentId, OnResult);
    }

    public void SubscribeBatch(IReadOnlyList<string> instrumentIds)
    {
        var lanes = _host != null ? _host.Lanes : null;
        if (lanes == null || instrumentIds == null || instrumentIds.Count == 0) return;
        lanes.SubmitSubscribeMarketDataBatch(instrumentIds, OnResult);
    }

    // ADR-0031 S5 (#145): remove → venue unsubscribe (add↔remove symmetry). Fire-and-forget like
    // Subscribe; an UNSUBSCRIBE_FAILED is logged but never touches membership (ADR-0022 D3 — the
    // registry owns membership, not the venue).
    public void Unsubscribe(string instrumentId)
    {
        var lanes = _host != null ? _host.Lanes : null;
        if (lanes == null) return;
        lanes.SubmitUnsubscribeMarketData(instrumentId, OnResult);
    }

    static void OnResult(OrderRpcResult r)
    {
        if (!r.Success && !string.IsNullOrEmpty(r.ErrorCode))
            Debug.LogWarning("[live-subscribe] subscribe returned " + r.ErrorCode);
    }
}
