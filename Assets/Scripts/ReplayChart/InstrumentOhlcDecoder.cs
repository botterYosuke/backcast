// InstrumentOhlcDecoder.cs — issue #60 "Hakoniwa chart tile family" (per-instrument OHLC decode)
//
// The typed decode API for ONE instrument's bar series out of get_state_json's dict-keyed
// `per_instrument` ({ "8918.TSE": { price, ohlc_points, depth }, ... }). The dynamic chart tile
// family (#60) renders N charts — one per universe instrument — each from its OWN
// per_instrument[id].ohlc_points, NOT the aggregate top-level ohlc_points (which is primary-only;
// reducer.py is_primary gate). ReplayBarDecoder.Decode(json) still serves that aggregate/primary
// series; this serves the per-id series.
//
// Reuses the SAME structure-aware locator DepthDecoder uses (PerInstrumentJsonLocator, findings
// 0027 §3) — extracted, not mirrored, so depth and ohlc navigation can't drift. JsonUtility cannot
// model the dict-keyed per_instrument NOR bind a top-level JSON array, so the located ohlc_points
// ARRAY span is wrapped as { "ohlc_points": <span> } and bound to the existing OhlcPoint[] shape.
//
// Decode CONTRACT (mirrors DepthDecoder / ReplayBarDecoder):
//   * null / empty / whitespace / "null" stateJson         -> Empty (no throw).
//   * per_instrument absent / instrument absent             -> Empty (no throw).
//   * instrument present but `ohlc_points` absent or null   -> Empty (no throw).
//   * ohlc_points array present                             -> mapped series (HasSeries=true).
//   * MALFORMED json encountered while navigating           -> NOT swallowed; FormatException.

using System;
using System.Collections.Generic;
using UnityEngine;

// The typed, consumer-facing decode result for one instrument's bar series. HasSeries
// distinguishes "no series for this id" (absent / not-yet-warmed) from an empty array.
public struct InstrumentOhlcFrame
{
    public bool HasSeries;
    public IReadOnlyList<OhlcPoint> Ohlc;

    public static InstrumentOhlcFrame Empty => new InstrumentOhlcFrame
    {
        HasSeries = false,
        Ohlc = Array.Empty<OhlcPoint>(),
    };
}

public static class InstrumentOhlcDecoder
{
    // JsonUtility can't bind a top-level array, so the located ohlc_points span is wrapped in this
    // single-field object. OhlcPoint (snake_case fields) is reused verbatim from ReplayBarDecoder.
    [Serializable] class OhlcWrapDto { public OhlcPoint[] ohlc_points; }

    public static InstrumentOhlcFrame Decode(string stateJson, string instrumentId)
    {
        if (string.IsNullOrWhiteSpace(stateJson) || string.IsNullOrEmpty(instrumentId))
            return InstrumentOhlcFrame.Empty;

        int v = PerInstrumentJsonLocator.LocateMember(stateJson, instrumentId, "ohlc_points");
        if (v < 0) return InstrumentOhlcFrame.Empty;

        // The member is an ARRAY (the caller's container-type assertion, per the locator contract).
        PerInstrumentJsonLocator.RequireArrayStart(stateJson, v, "ohlc_points");
        int end = PerInstrumentJsonLocator.ScanContainer(stateJson, v);
        string arraySpan = stateJson.Substring(v, end - v);

        var dto = JsonUtility.FromJson<OhlcWrapDto>("{\"ohlc_points\":" + arraySpan + "}");
        return new InstrumentOhlcFrame
        {
            HasSeries = true,
            Ohlc = dto?.ohlc_points ?? Array.Empty<OhlcPoint>(),
        };
    }
}
