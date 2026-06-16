// InstrumentPriceDecoder.cs — issue #57 "orderbook: DepthLadderView を本線 scene に載せ替え"
//
// The typed reader for per_instrument[id].price — the per-instrument LAST trade price that feeds the
// depth ladder's LAST row (TTWR overlays_ladder.rs LastPrices, findings 0028 D4). It takes the FULL
// get_state_json() string + a target instrument id and returns that id's `price`, or null when the
// price is absent/JSON-null (→ "LAST ---").
//
// Like DepthDecoder (#26) / InstrumentOhlcDecoder (#60), it peels the dynamic per_instrument outer
// shell via the SHARED PerInstrumentJsonLocator (one structure-aware scanner — extract, do not mirror)
// and reads ONLY the located scalar. No Newtonsoft; no backend contract change.
//
// Decode CONTRACT (mirrors the sibling decoders):
//   * null / empty / whitespace / "null" stateJson           -> null (no throw).
//   * per_instrument absent / id absent / price absent/null   -> null (no throw).
//   * price present but NOT a number (string / object / array) -> null (no throw — a typed-but-wrong
//     value is surfaced as "no last", not a crash; the LAST row simply shows "LAST ---").
//   * MALFORMED json while navigating to the id               -> FormatException (NOT swallowed):
//     the grounded payload is always valid model_dump_json, so a structural parse failure is a real
//     bug to surface, exactly as the locator/DepthDecoder do.

using System;
using System.Globalization;

public static class InstrumentPriceDecoder
{
    public static double? Decode(string stateJson, string instrumentId)
    {
        if (string.IsNullOrWhiteSpace(stateJson) || string.IsNullOrEmpty(instrumentId))
            return null;

        // Locate per_instrument[id].price's value (first char), or -1 if absent / JSON null.
        // Throws FormatException on malformed structure while navigating.
        int v = PerInstrumentJsonLocator.LocateMember(stateJson, instrumentId, "price");
        if (v < 0) return null;                                         // absent / JSON null
        // Located-but-out-of-bounds means the payload was truncated right after `"price":` — malformed,
        // surfaced (not swallowed as "no last"), consistent with DepthDecoder/the locator contract.
        if (v >= stateJson.Length) throw new FormatException("price value truncated");

        // price must be a JSON number. A container/string value is a typed mismatch, not malformed
        // JSON — return null ("LAST ---") rather than mis-scanning it as a scalar.
        char c0 = stateJson[v];
        if (c0 == '{' || c0 == '[' || c0 == '"') return null;

        int end = PerInstrumentJsonLocator.ScanScalarEnd(stateJson, v);
        string tok = stateJson.Substring(v, end - v);
        return double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
            ? d
            : (double?)null;
    }
}
