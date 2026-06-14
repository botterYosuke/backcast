// LiveBackendEventDecoder.cs — issue #20 "Live adapter tracer" (durable tier)
//
// Decodes the externally-tagged BackendEvent wire ({"OrderEvent":{...}}, ADR-0018
// A2) that LiveBackendEventSink receives, into typed consumer view-models. Unity's
// JsonUtility cannot bind the DYNAMIC outer tag key, so PeelTag() splits the
// single-key envelope into (tag, innerJson) by string scan, then each Decode* runs
// JsonUtility on the FIXED-key inner object. Mirrors ReplayPanelDecoder discipline:
// the parser is a hidden implementation detail; inner DTOs use VERBATIM snake_case
// (a name mismatch is silently zero-filled by JsonUtility, with no error); the
// consumer-facing value types are hand-mapped to PascalCase.
//
// This is backcast's first Live backend-event decoder; future Live panels reuse it.
// It is NOT ReplayPanelDecoder/ReplayBarDecoder (those read the Replay push_bar /
// EventSink projection wire, which is a different shape — see CONTEXT.md D2).
using System;
using System.Collections.Generic;
using UnityEngine;

// snake_case [Serializable] so JsonUtility binds it as an array element — do NOT
// rename to PascalCase (silent zero-fill on mismatch).
[System.Serializable]
public struct LivePosition
{
    public string symbol;
    public double qty;
    public double avg_price;
    public double unrealized_pnl;
}

// hand-mapped consumer value types -> PascalCase
public struct LiveOrderEvent
{
    public string OrderId;
    public string VenueOrderId;
    public string ClientOrderId;
    public string Status;
    public string StrategyId;
    public double FilledQty;
    public double AvgPrice;
    public long TsMs;
}

public struct LiveAccountEvent
{
    public double Cash;
    public double BuyingPower;
    public IReadOnlyList<LivePosition> Positions;
    public long TsMs;
}

public struct LiveLifecycleEvent
{
    public string RunId;
    public string StrategyId;
    public string Status;
    public long TsMs;
}

public struct LiveTelemetryEvent
{
    public string RunId;
    public string StrategyId;
    public double RealizedPnl;
    public double UnrealizedPnl;
    public long OrderCount;
    public long FillCount;
    public long TsMs;
}

// #21 D5: SecretRequired is pushed INSIDE the place_order RPC (tachibana second
// password). The native secret modal reacts to this and replies via submit_secret
// on the urgent-secret lane. Carries NO plaintext — only the request envelope.
public struct LiveSecretRequiredEvent
{
    public string RequestId;
    public string Venue;
    public string Kind;
    public string Purpose;
}

// #21 D6: VenueLogoutDetected is a health-watchdog NOTICE (prompt re-login). It is
// NOT the badge authority — the badge waits for the get_state_json poll to converge.
public struct LiveVenueLogoutEvent
{
    public string Venue;
}

public static class LiveBackendEventDecoder
{
    // wire = {"<Tag>":<innerObject>}. Returns the tag name and sets innerJson to
    // the inner object JSON. Returns "" (innerJson="") for null/empty/malformed.
    public static string PeelTag(string json, out string innerJson)
    {
        innerJson = "";
        if (string.IsNullOrWhiteSpace(json)) return "";
        int open = json.IndexOf('{');
        if (open < 0) return "";
        int q1 = json.IndexOf('"', open + 1);
        if (q1 < 0) return "";
        int q2 = json.IndexOf('"', q1 + 1);
        if (q2 < 0) return "";
        string tag = json.Substring(q1 + 1, q2 - q1 - 1);
        int colon = json.IndexOf(':', q2 + 1);
        if (colon < 0) return "";
        int lastBrace = json.LastIndexOf('}');
        if (lastBrace <= colon) return "";
        innerJson = json.Substring(colon + 1, lastBrace - colon - 1).Trim();
        return tag;
    }

    [System.Serializable] class OrderDto
    {
        public string order_id;
        public string venue_order_id;
        public string client_order_id;
        public string status;
        public string strategy_id;
        public double filled_qty;
        public double avg_price;
        public long ts_ms;
    }

    [System.Serializable] class AccountDto
    {
        public double cash;
        public double buying_power;
        public LivePosition[] positions;
        public long ts_ms;
    }

    [System.Serializable] class LifecycleDto
    {
        public string run_id;
        public string strategy_id;
        public string status;
        public long ts_ms;
    }

    [System.Serializable] class TelemetryDto
    {
        public string run_id;
        public string strategy_id;
        public double realized_pnl;
        public double unrealized_pnl;
        public long order_count;
        public long fill_count;
        public long ts_ms;
    }

    [System.Serializable] class SecretRequiredDto
    {
        public string request_id;
        public string venue;
        public string kind;
        public string purpose;
    }

    [System.Serializable] class VenueLogoutDto
    {
        public string venue;
    }

    public static LiveOrderEvent DecodeOrder(string innerJson)
    {
        if (string.IsNullOrWhiteSpace(innerJson)) return default;
        var d = JsonUtility.FromJson<OrderDto>(innerJson);
        if (d == null) return default;
        return new LiveOrderEvent
        {
            OrderId = d.order_id,
            VenueOrderId = d.venue_order_id,
            ClientOrderId = d.client_order_id,
            Status = d.status,
            StrategyId = d.strategy_id,
            FilledQty = d.filled_qty,
            AvgPrice = d.avg_price,
            TsMs = d.ts_ms,
        };
    }

    public static LiveAccountEvent DecodeAccount(string innerJson)
    {
        if (string.IsNullOrWhiteSpace(innerJson))
            return new LiveAccountEvent { Positions = Array.Empty<LivePosition>() };
        var d = JsonUtility.FromJson<AccountDto>(innerJson);
        if (d == null)
            return new LiveAccountEvent { Positions = Array.Empty<LivePosition>() };
        return new LiveAccountEvent
        {
            Cash = d.cash,
            BuyingPower = d.buying_power,
            Positions = d.positions ?? Array.Empty<LivePosition>(),
            TsMs = d.ts_ms,
        };
    }

    public static LiveLifecycleEvent DecodeLifecycle(string innerJson)
    {
        if (string.IsNullOrWhiteSpace(innerJson)) return default;
        var d = JsonUtility.FromJson<LifecycleDto>(innerJson);
        if (d == null) return default;
        return new LiveLifecycleEvent
        {
            RunId = d.run_id,
            StrategyId = d.strategy_id,
            Status = d.status,
            TsMs = d.ts_ms,
        };
    }

    public static LiveTelemetryEvent DecodeTelemetry(string innerJson)
    {
        if (string.IsNullOrWhiteSpace(innerJson)) return default;
        var d = JsonUtility.FromJson<TelemetryDto>(innerJson);
        if (d == null) return default;
        return new LiveTelemetryEvent
        {
            RunId = d.run_id,
            StrategyId = d.strategy_id,
            RealizedPnl = d.realized_pnl,
            UnrealizedPnl = d.unrealized_pnl,
            OrderCount = d.order_count,
            FillCount = d.fill_count,
            TsMs = d.ts_ms,
        };
    }

    public static LiveSecretRequiredEvent DecodeSecretRequired(string innerJson)
    {
        if (string.IsNullOrWhiteSpace(innerJson)) return default;
        var d = JsonUtility.FromJson<SecretRequiredDto>(innerJson);
        if (d == null) return default;
        return new LiveSecretRequiredEvent
        {
            RequestId = d.request_id,
            Venue = d.venue,
            Kind = d.kind,
            Purpose = d.purpose,
        };
    }

    public static LiveVenueLogoutEvent DecodeVenueLogout(string innerJson)
    {
        if (string.IsNullOrWhiteSpace(innerJson)) return default;
        var d = JsonUtility.FromJson<VenueLogoutDto>(innerJson);
        if (d == null) return default;
        return new LiveVenueLogoutEvent { Venue = d.venue };
    }
}
