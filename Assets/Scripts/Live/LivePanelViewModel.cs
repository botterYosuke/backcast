// LivePanelViewModel.cs — issue #20 "Live adapter tracer" (durable tier)
//
// Minimal Live panel view-model: the single Apply() entry drains one wire string
// from LiveBackendEventSink, peels the tag + decodes it via LiveBackendEventDecoder,
// and stores the latest typed order/account/lifecycle/telemetry values a real Live
// panel would bind to (plus the counters the AFK gate asserts on). Keeping the
// decode→view-model mapping here (durable) is what the throwaway probe asserts
// against per findings 0011 D3 ("sink drain → decoder → view-model 値 → 実値 assert").
using System;

public class LivePanelViewModel
{
    public long AppliedCount { get; private set; }
    public long UnknownTagCount { get; private set; }

    public long FilledOrderCount { get; private set; }
    public bool HasOrder { get; private set; }
    public LiveOrderEvent LatestOrder { get; private set; }

    public bool HasAccount { get; private set; }
    public bool SawAccountPosition { get; private set; }
    public LiveAccountEvent LatestAccount { get; private set; }

    public long LifecycleCount { get; private set; }
    public bool HasLifecycle { get; private set; }
    public LiveLifecycleEvent LatestLifecycle { get; private set; }

    public long TelemetryCount { get; private set; }
    public bool HasTelemetry { get; private set; }
    public LiveTelemetryEvent LatestTelemetry { get; private set; }

    // #21 D5: SecretRequired arrives on the sink WHILE place_order blocks the
    // order-write lane. The native secret modal observes SecretRequiredCount (a
    // monotonic edge) and opens for LatestSecretRequired.RequestId, then replies
    // via submit_secret on the urgent-secret lane. The view-model holds only the
    // request envelope — never a secret.
    public long SecretRequiredCount { get; private set; }
    public bool HasSecretRequired { get; private set; }
    public LiveSecretRequiredEvent LatestSecretRequired { get; private set; }

    // #21 D6: VenueLogoutDetected is a NOTICE only (prompt re-login). It does NOT
    // flip the connection badge — VenueConnectionViewModel keeps the badge on the
    // get_state_json poll, which is the sole continuous canonical (CONTEXT.md
    // "venue 接続状態"). We record the notice so the UI can surface a re-login hint.
    public long VenueLogoutNoticeCount { get; private set; }
    public bool HasVenueLogoutNotice { get; private set; }
    public LiveVenueLogoutEvent LatestVenueLogoutNotice { get; private set; }

    public void Apply(string wireJson)
    {
        string inner;
        string tag = LiveBackendEventDecoder.PeelTag(wireJson, out inner);
        switch (tag)
        {
            case "OrderEvent":
                LatestOrder = LiveBackendEventDecoder.DecodeOrder(inner);
                HasOrder = true;
                if (LatestOrder.Status == "FILLED") FilledOrderCount++;
                break;
            case "AccountEvent":
                LatestAccount = LiveBackendEventDecoder.DecodeAccount(inner);
                HasAccount = true;
                if (LatestAccount.Positions != null && LatestAccount.Positions.Count > 0)
                    SawAccountPosition = true;
                break;
            case "LiveStrategyEvent":
                LatestLifecycle = LiveBackendEventDecoder.DecodeLifecycle(inner);
                HasLifecycle = true;
                LifecycleCount++;
                break;
            case "LiveStrategyTelemetry":
                LatestTelemetry = LiveBackendEventDecoder.DecodeTelemetry(inner);
                HasTelemetry = true;
                TelemetryCount++;
                break;
            case "SecretRequired":
                LatestSecretRequired = LiveBackendEventDecoder.DecodeSecretRequired(inner);
                HasSecretRequired = true;
                SecretRequiredCount++;
                break;
            case "VenueLogoutDetected":
                LatestVenueLogoutNotice = LiveBackendEventDecoder.DecodeVenueLogout(inner);
                HasVenueLogoutNotice = true;
                VenueLogoutNoticeCount++;
                break;
            default:
                // StrategyLogMessage / BackendError / SafetyRailViolation / etc. —
                // received but not panel-critical for this slice.
                UnknownTagCount++;
                break;
        }
        AppliedCount++;
    }
}
