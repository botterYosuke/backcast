// DepthDecoder.cs — issue #26 "S9a orderbook 先行" (durable tier)
//
// The typed, consumer-facing decode API for the order-book 板 (depth). It takes the
// FULL get_state_json() string (TradingState.model_dump_json) plus a target instrument
// id, and returns that instrument's latest DepthSnapshot as a bid/ask ladder view.
//
// WHY THIS IS NOT A PLAIN JsonUtility DTO (the central #26 problem):
//   get_state_json's `per_instrument` is a dict KEYED BY instrument id
//   ({ "8918.TSE": { price, ohlc_points, depth }, ... }). Unity's JsonUtility canNOT
//   model a JSON object whose keys are dynamic data (ReplayBarDecoder.cs:29 states this
//   is exactly why per_instrument has never been decoded durably). So we use the same
//   HYBRID shape LiveBackendEventDecoder.PeelTag uses: a small structure-aware locator
//   peels off the dynamic outer shell (find per_instrument → the target id member → its
//   `depth` value) and hands the FIXED-shape depth object substring to JsonUtility, which
//   handles the bids/asks arrays-of-objects natively. No Newtonsoft; no backend contract
//   change (depth is already merged into the snapshot at _backend_impl.py get_state_json).
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
using UnityEngine;

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

        var dto = JsonUtility.FromJson<DepthDto>(depthObject);
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

    // ---- structure-aware locator -----------------------------------------------------
    // A minimal JSON value-span scanner: enough to navigate object members and skip values
    // (objects/arrays/strings/numbers/literals) WITHOUT a full parser and WITHOUT being
    // fooled by braces/keys that appear inside string values. Returns the raw substring of
    // the `depth` object value, or null if the path is absent / depth is null.

    static string LocateDepthObject(string s, string instrumentId)
    {
        // FindMember returns an already-ws-skipped value index, so no re-SkipWs is needed
        // between hops. RequireObjectStart bounds-checks before indexing, so a payload that
        // truncates right after a `key:` throws FormatException (the contract) rather than
        // IndexOutOfRangeException.
        int i = SkipWs(s, 0);
        if (i >= s.Length) return null;
        if (IsJsonNull(s, i)) return null;               // stateJson == "null"
        RequireObjectStart(s, i, "state json top-level");

        int pi = FindMember(s, i, "per_instrument");
        if (pi < 0) return null;
        if (IsJsonNull(s, pi)) return null;
        RequireObjectStart(s, pi, "per_instrument");

        int inst = FindMember(s, pi, instrumentId);
        if (inst < 0) return null;
        if (IsJsonNull(s, inst)) return null;
        RequireObjectStart(s, inst, "per_instrument entry");

        int depth = FindMember(s, inst, "depth");
        if (depth < 0) return null;
        if (IsJsonNull(s, depth)) return null;           // Replay / no board
        RequireObjectStart(s, depth, "depth");

        int end = ScanContainer(s, depth);
        return s.Substring(depth, end - depth);
    }

    static void RequireObjectStart(string s, int pos, string what)
    {
        if (pos >= s.Length || s[pos] != '{')
            throw new FormatException(what + " is not an object");
    }

    // objStart points at '{'. Returns the index of the matched member's VALUE (first char),
    // or -1 if `key` is absent in this object. Throws on malformed object structure.
    static int FindMember(string s, int objStart, string key)
    {
        int i = SkipWs(s, objStart + 1);
        if (i < s.Length && s[i] == '}') return -1;      // empty object
        while (i < s.Length)
        {
            i = SkipWs(s, i);
            if (i >= s.Length || s[i] != '"') throw new FormatException("expected object key string");
            int keyStart = i + 1;
            int keyEnd = ScanString(s, i);               // index after closing quote
            bool match = MatchKey(s, keyStart, keyEnd - 1, key);

            i = SkipWs(s, keyEnd);
            if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' after key");
            int valStart = SkipWs(s, i + 1);
            if (match) return valStart;

            int valEnd = ScanValue(s, valStart);
            i = SkipWs(s, valEnd);
            if (i >= s.Length) throw new FormatException("unterminated object");
            if (s[i] == ',') { i++; continue; }
            if (s[i] == '}') return -1;
            throw new FormatException("expected ',' or '}' in object");
        }
        throw new FormatException("unterminated object");
    }

    // Compares the raw key inner span s[start,end) to `key` (ordinal). Our target keys
    // (per_instrument / depth) and instrument ids carry no JSON escapes, so a raw compare
    // is correct: an escaped key in the wire is a DIFFERENT key and rightly won't match.
    static bool MatchKey(string s, int start, int end, string key)
    {
        int len = end - start;
        if (len != key.Length) return false;
        return string.CompareOrdinal(s, start, key, 0, len) == 0;
    }

    // i at opening '"'. Returns index just past the closing '"'. Escape-aware so that
    // quotes/braces inside string values never derail navigation. Throws if unterminated.
    static int ScanString(string s, int i)
    {
        i++; // past opening quote
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\\') { i += 2; continue; }         // skip escaped char (\" \\ \uXXXX...)
            if (c == '"') return i + 1;
            i++;
        }
        throw new FormatException("unterminated string");
    }

    // i at first char of a value (post-ws). Returns end index (exclusive).
    static int ScanValue(string s, int i)
    {
        if (i >= s.Length) throw new FormatException("expected value");
        char c = s[i];
        if (c == '{' || c == '[') return ScanContainer(s, i);
        if (c == '"') return ScanString(s, i);
        int start = i;                                   // number / true / false / null
        while (i < s.Length)
        {
            char d = s[i];
            if (d == ',' || d == '}' || d == ']' ||
                d == ' ' || d == '\t' || d == '\n' || d == '\r') break;
            i++;
        }
        if (i == start) throw new FormatException("empty value");
        return i;
    }

    // i at '{' or '['. Returns index just past the matching close bracket. String contents
    // are skipped via ScanString so brackets inside strings don't affect nesting. Bracket
    // TYPE is checked (a '{' closed by ']' is malformed → throw), not just the count, so a
    // count-balanced-but-mispaired container is surfaced instead of silently extracted.
    // The stack depth tracks JSON nesting only (≤ a few here), independent of array length.
    static int ScanContainer(string s, int i)
    {
        var stack = new Stack<char>();
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '"') { i = ScanString(s, i); continue; }
            if (c == '{') { stack.Push('}'); i++; continue; }
            if (c == '[') { stack.Push(']'); i++; continue; }
            if (c == '}' || c == ']')
            {
                if (stack.Count == 0 || stack.Pop() != c)
                    throw new FormatException("mismatched bracket in container");
                i++;
                if (stack.Count == 0) return i;
                continue;
            }
            i++;
        }
        throw new FormatException("unterminated container");
    }

    static int SkipWs(string s, int i)
    {
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ' ' || c == '\t' || c == '\n' || c == '\r') i++;
            else break;
        }
        return i;
    }

    // True only for a complete JSON `null` token — the 4 chars AND a value-terminator (or
    // EOF) after them, so a malformed `nullish` is NOT mistaken for null (it falls through
    // to RequireObjectStart → FormatException).
    static bool IsJsonNull(string s, int i)
    {
        if (i + 4 > s.Length) return false;
        if (!(s[i] == 'n' && s[i + 1] == 'u' && s[i + 2] == 'l' && s[i + 3] == 'l')) return false;
        if (i + 4 == s.Length) return true;
        char c = s[i + 4];
        return c == ',' || c == '}' || c == ']' || c == ' ' || c == '\t' || c == '\n' || c == '\r';
    }
}
