// DepthDecoder.cs — issue #26 "S9a orderbook 先行" (durable tier)
//
// The typed, consumer-facing decode API for the order-book 板 (depth). It takes the
// FULL get_state_json() string (TradingState.model_dump_json) plus a target instrument
// id, and returns that instrument's latest DepthSnapshot as a bid/ask ladder view.
//
// WHY THIS IS NOT A PLAIN JsonUtility DTO (the central #26 problem):
//   get_state_json's `per_instrument` is a dict KEYED BY instrument id
//   ({ "8918.TSE": { price, ohlc_points, depth }, ... }). Unity's JsonUtility canNOT
//   model a JSON object whose keys are dynamic data. So the dynamic outer shell is peeled
//   off by PerInstrumentJsonLocator (the shared structure-aware locator extracted in #60,
//   findings 0027 §3) — find per_instrument → the target id → its `depth` value — and the
//   FIXED-shape depth object substring is handed to JsonUtility, which handles the
//   bids/asks arrays-of-objects natively. No Newtonsoft; no backend contract change.
//
// ORDERING IS FAITHFULLY PRESERVED: DepthSnapshot's "bids 降順 / asks 昇順" is a PRODUCER
// contract (engine.models / DepthCache do NOT enforce a sort). This decoder restores the
// wire order verbatim — it does NOT defensively re-sort, because a defensive sort would
// hide a producer contract violation. The characterization probe asserts the mock emits
// in the expected order instead (CONTEXT.md 板/depth, bid/ask ladder).
//
// Decode CONTRACT (mirrors ReplayBarDecoder/ReplayPanelDecoder discipline):
//   * null / empty / whitespace / "null" stateJson        -> Empty (HasDepth=false, no throw).
//   * per_instrument absent / instrument absent            -> Empty (no throw).
//   * instrument present but `depth` absent or `depth:null` (Replay) -> Empty (no throw).
//   * depth object present                                 -> HasDepth=true, ladder mapped.
//   * MALFORMED json encountered while navigating          -> NOT swallowed; throws
//     FormatException. The grounded payload is always valid model_dump_json output, so a
//     structural parse failure is a real bug we want surfaced, not silently emptied.
//
// The locator navigates STRUCTURALLY (member keys only, strings skipped via escape-aware
// scanning), so a decoy occurrence of "per_instrument"/the id/"depth" inside a string
// value (e.g. live_last_error) does NOT fool it — that is a probe characterization case.

using System;
using System.Collections.Generic;

// One ladder level. Consumer-facing value type → PascalCase (mirrors #10 ReplayBarFrame).
public struct DepthLevelView
{
    public double Price;
    public double Size;
}

// The typed, consumer-facing decode result. Bids/Asks are IReadOnlyList so consumers
// can't mutate the decoded ladder; arrays satisfy it. HasDepth distinguishes "no board"
// (Replay / not-yet-subscribed) from "empty board" (depth present, both sides empty).
public struct DepthSnapshotView
{
    public bool HasDepth;
    public IReadOnlyList<DepthLevelView> Bids;
    public IReadOnlyList<DepthLevelView> Asks;
    public long TimestampMs;

    public static DepthSnapshotView Empty => new DepthSnapshotView
    {
        HasDepth = false,
        Bids = Array.Empty<DepthLevelView>(),
        Asks = Array.Empty<DepthLevelView>(),
        TimestampMs = 0,
    };
}

public static class DepthDecoder
{
    // Fixed-shape DTOs for the depth object only (JsonUtility handles these natively).
    // Field names are VERBATIM JSON keys (snake_case) — a mismatch silently zero-fills.
    [Serializable] class LevelDto { public double price; public double size; }
    [Serializable] class DepthDto { public LevelDto[] bids; public LevelDto[] asks; public long timestamp_ms; }

    public static DepthSnapshotView Decode(string stateJson, string instrumentId)
    {
        if (string.IsNullOrWhiteSpace(stateJson) || string.IsNullOrEmpty(instrumentId))
            return DepthSnapshotView.Empty;

        // Structure-aware locate of the (fixed-shape) depth object inside the dict-keyed
        // per_instrument. Returns null when any link of per_instrument→id→depth is absent
        // or depth is JSON null; throws FormatException on malformed structure.
        string depthObject = LocateDepthObject(stateJson, instrumentId);
        if (depthObject == null) return DepthSnapshotView.Empty;

        var dto = UnityEngine.JsonUtility.FromJson<DepthDto>(depthObject);
        if (dto == null) return DepthSnapshotView.Empty;

        return new DepthSnapshotView
        {
            HasDepth = true,
            Bids = MapLevels(dto.bids),
            Asks = MapLevels(dto.asks),
            TimestampMs = dto.timestamp_ms,
        };
    }

    static IReadOnlyList<DepthLevelView> MapLevels(LevelDto[] arr)
    {
        if (arr == null || arr.Length == 0) return Array.Empty<DepthLevelView>();
        var outv = new DepthLevelView[arr.Length];
        for (int k = 0; k < arr.Length; k++)
            outv[k] = new DepthLevelView { Price = arr[k].price, Size = arr[k].size };
        return outv;
    }

    // Locate the `depth` OBJECT inside per_instrument[id] via the shared locator (#60). The
    // intermediate-hop navigation + null/absent/malformed contract lives in the locator; here we
    // only assert depth is an OBJECT and slice its verbatim span for JsonUtility.
    static string LocateDepthObject(string s, string instrumentId)
    {
        int v = PerInstrumentJsonLocator.LocateMember(s, instrumentId, "depth");
        if (v < 0) return null;
        PerInstrumentJsonLocator.RequireObjectStart(s, v, "depth");
        int end = PerInstrumentJsonLocator.ScanContainer(s, v);
        return s.Substring(v, end - v);
    }
}
