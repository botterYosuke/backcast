// ReplayBarDecoder.cs — issue #10 "Replay chart" (M1, durable tier)
//
// The typed decode API for the replay push_bar JSON stream. This is the DURABLE
// half of the chart slice (mirrors S1's two-tier split): a typed model
// (OhlcPoint / ReplayBarFrame) + a single decode entry point
// (ReplayBarDecoder.Decode) that #11/#5/#7 reuse. The throwaway halves (M2 AFK
// decode probe, M3 HITL playmode widget) live elsewhere and consume this API.
//
// PARSER IS HIDDEN: Decode uses Unity's built-in JsonUtility internally, but no
// caller sees that — the parser is an implementation detail behind Decode, so it
// can be swapped (e.g. to a dict-capable parser) at POINT-B without touching
// consumers. No Newtonsoft is added (not in the manifest; premature here).
//
// PAYLOAD (grounded in engine/live/gui_bridge_actor.py:103-143, owner-verified):
//   push_bar top-level object = { price, timestamp, timestamp_ms, history,
//   ohlc_points, per_instrument }. ohlc_points is CUMULATIVE — each bar carries
//   the FULL series so far, so a consumer renders the latest frame's full
//   ohlc_points (no C#-side accumulation). Each ohlc_points element =
//   { timestamp_ms, open_time_ms, open, high, low, close, volume }.
//
// JsonUtility binding rules this file depends on:
//   * Binds by VERBATIM field name — OhlcPoint's fields are snake_case to match
//     the JSON keys EXACTLY. A name mismatch is silently zero-filled (no error),
//     which is why M2's gate asserts decoded VALUES, not just non-emptiness.
//   * Supports a top-level [Serializable] OBJECT with array/List<T> fields, but
//     NOT a top-level JSON array — the payload top level is an object, so the DTO
//     is an object with an `ohlc_points` array field. Good.
//   * Silently ignores JSON keys with no matching field — so the DTO declares
//     ONLY price/timestamp_ms/ohlc_points; history and per_instrument (the latter
//     a dict JsonUtility could not model anyway) are simply not declared.
//
// Decode CONTRACT:
//   * null / empty / whitespace json  -> empty frame (Ohlc == empty, no throw).
//   * json == "null"                  -> empty frame (no throw).
//   * valid json with no ohlc_points  -> frame with empty Ohlc (no throw).
//   * MALFORMED json                  -> Decode does NOT swallow it; JsonUtility
//     throws. The grounded payload is always valid json.dumps output, so a parse
//     failure is a real bug we want surfaced, not silently zero-filled (mirrors
//     S1's "no try/catch, don't mask spike bugs" discipline).
//
// INTERMEDIATE STATE: this file compiles but is UNUSED until the M2 probe drains
// the JSON and calls Decode. No .meta is authored here — Unity generates
// ReplayBarDecoder.cs.meta + the ReplayChart folder .meta on the next import.

using System.Collections.Generic;
using UnityEngine;

// One cumulative OHLC sample. Field names are VERBATIM JSON keys (JsonUtility
// binds by exact name; snake_case is required — do NOT rename to PascalCase).
[System.Serializable]
public struct OhlcPoint
{
    public long open_time_ms;
    public double open;
    public double high;
    public double low;
    public double close;
    public double volume;
}

// The typed, consumer-facing decode result. Ohlc is exposed as IReadOnlyList so
// consumers can't mutate the decoded series; an OhlcPoint[] satisfies it.
public struct ReplayBarFrame
{
    public double Price;
    public long TimestampMs;
    public IReadOnlyList<OhlcPoint> Ohlc;
}

public static class ReplayBarDecoder
{
    // Private JsonUtility DTO: declares ONLY the fields we consume. Field names
    // are VERBATIM JSON keys. Undeclared payload keys (history, per_instrument)
    // are silently ignored by JsonUtility.
    [System.Serializable]
    class PushBarDto
    {
        public double price;
        public long timestamp_ms;
        public OhlcPoint[] ohlc_points;
    }

    public static ReplayBarFrame Decode(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ReplayBarFrame { Ohlc = System.Array.Empty<OhlcPoint>() };
        }

        var dto = JsonUtility.FromJson<PushBarDto>(json);
        if (dto == null)
        {
            return new ReplayBarFrame { Ohlc = System.Array.Empty<OhlcPoint>() };
        }

        return new ReplayBarFrame
        {
            Price = dto.price,
            TimestampMs = dto.timestamp_ms,
            Ohlc = dto.ohlc_points ?? System.Array.Empty<OhlcPoint>(),
        };
    }
}
