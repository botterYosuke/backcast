// PerInstrumentJsonLocator.cs — issue #60 "Hakoniwa chart tile family" (shared structure-aware locator)
//
// Extracted from DepthDecoder (#26) so depth AND per-instrument ohlc_points share ONE scanner
// (findings 0027 §3, grill round2 Q2 — extract, do NOT mirror, so a fix in one never silently
// drifts from the other). It navigates get_state_json's `per_instrument` — a dict KEYED BY
// instrument id ({ "8918.TSE": { price, ohlc_points, depth }, ... }) that Unity's JsonUtility
// cannot model — peeling off the dynamic outer shell (per_instrument → the target id → the
// named member) WITHOUT a full parser and WITHOUT being fooled by braces/keys inside string
// values (escape-aware). The caller asserts the member's container TYPE (RequireObjectStart for
// depth, RequireArrayStart for ohlc_points) and binds the verbatim span with JsonUtility.
//
// LocateMember CONTRACT (mirrors the DepthDecoder discipline it was extracted from):
//   * any link absent (per_instrument / id / member missing)  -> -1.
//   * member value is JSON null (e.g. depth:null in Replay)   -> -1.
//   * MALFORMED json while navigating                          -> FormatException (NOT swallowed):
//     the grounded payload is always valid model_dump_json, so a structural parse failure is a
//     real bug to surface, not silently empty.
// The navigation REQUIRES the intermediate hops (top-level / per_instrument / entry) to be
// objects (throws otherwise) — only the FINAL member's container type is left to the caller.

using System;

public static class PerInstrumentJsonLocator
{
    // Returns the ws-skipped index of per_instrument[instrumentId].<member>'s value (first char),
    // or -1 if any link is absent or that value is JSON null. Throws FormatException on malformed
    // structure. The caller asserts the container type and scans the span via ScanContainer.
    public static int LocateMember(string s, string instrumentId, string member)
    {
        if (string.IsNullOrEmpty(s) || string.IsNullOrEmpty(instrumentId) || string.IsNullOrEmpty(member))
            return -1;

        int i = SkipWs(s, 0);
        if (i >= s.Length) return -1;
        if (IsJsonNull(s, i)) return -1;                 // stateJson == "null"
        RequireObjectStart(s, i, "state json top-level");

        int pi = FindMember(s, i, "per_instrument");
        if (pi < 0) return -1;
        if (IsJsonNull(s, pi)) return -1;
        RequireObjectStart(s, pi, "per_instrument");

        int inst = FindMember(s, pi, instrumentId);
        if (inst < 0) return -1;
        if (IsJsonNull(s, inst)) return -1;
        RequireObjectStart(s, inst, "per_instrument entry");

        int mem = FindMember(s, inst, member);
        if (mem < 0) return -1;
        if (IsJsonNull(s, mem)) return -1;               // member value is JSON null
        return mem;
    }

    public static void RequireObjectStart(string s, int pos, string what)
    {
        if (pos >= s.Length || s[pos] != '{')
            throw new FormatException(what + " is not an object");
    }

    public static void RequireArrayStart(string s, int pos, string what)
    {
        if (pos >= s.Length || s[pos] != '[')
            throw new FormatException(what + " is not an array");
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
    // (per_instrument / depth / ohlc_points) and instrument ids carry no JSON escapes, so a raw
    // compare is correct: an escaped key in the wire is a DIFFERENT key and rightly won't match.
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

    // i at '{' or '['. Returns index just past the matching close bracket. String contents are
    // skipped via ScanString so brackets inside strings don't affect nesting. Bracket TYPE is
    // checked (a '{' closed by ']' is malformed → throw), not just the count.
    public static int ScanContainer(string s, int i)
    {
        var stack = new System.Collections.Generic.Stack<char>();
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

    // True only for a complete JSON `null` token — the 4 chars AND a value-terminator (or EOF)
    // after them, so a malformed `nullish` is NOT mistaken for null (it falls through to the
    // caller's RequireObjectStart/RequireArrayStart → FormatException).
    static bool IsJsonNull(string s, int i)
    {
        if (i + 4 > s.Length) return false;
        if (!(s[i] == 'n' && s[i + 1] == 'u' && s[i + 2] == 'l' && s[i + 3] == 'l')) return false;
        if (i + 4 == s.Length) return true;
        char c = s[i + 4];
        return c == ',' || c == '}' || c == ']' || c == ' ' || c == '\t' || c == '\n' || c == '\r';
    }
}
