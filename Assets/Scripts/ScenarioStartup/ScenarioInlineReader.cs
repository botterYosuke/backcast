// ScenarioInlineReader.cs — issue #66 "本線 Populate に inline-.py SCENARIO fallback を配線"
//
// The Python-FREE reader for a strategy `.py`'s module-level `SCENARIO = {...}` dict literal,
// projected into a ScenarioSnapshot (the same 5 panel-owned keys ScenarioSidecarStore reads).
// This is the `fallback` ScenarioStartupController.Populate has always carried but production
// never wired: when a strategy has NO scenario sidecar, the universe must be seeded from the
// inline SCENARIO or footer LiveAuto ▶ is BlockedNoInstrument (findings 0043 / 0027 §3(d)).
//
// WHY PURE C# (findings 0043 D1): the canonical SoT at run time is the Python
// engine.strategy_runtime.scenario.load_scenario, but it cannot be used at populate time —
// (1) ResolvePaths/Populate runs in Awake BEFORE InitializePython, (2) a non-owner root never
// inits Python yet still builds the startup tile, (3) ScenarioStartupController /
// ScenarioSidecarStore are deliberately Python-free + AFK-probe-testable. So we mirror the
// LITERAL SUBSET that scenario.extract() guarantees (SCENARIO is a literal Dict — DictComp /
// non-literal / multiple definitions are rejected by the Python side) with a small
// recursive-descent parser, and pin C#↔Python faithfulness with a golden gate (findings 0043 §2).
//
// NEVER THROWS (Awake-safe; findings 0043 D3). `status` distinguishes Absent (no SCENARIO node —
// a legitimate blank/new strategy, silently falls to SeedDefaults) from Unparseable (a SCENARIO
// node we could not parse — a capability gap; LogWarning here as the dev floor, and the caller
// raises a user-facing menu notice so #66's silent no-op never recurs — findings 0027 silent-drop).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

public enum ScenarioReadStatus { Found, Absent, Unparseable }

public static class ScenarioInlineReader
{
    // Read the inline SCENARIO and project the 5 panel-owned keys. Returns null on Absent /
    // Unparseable (see `status`); never throws.
    public static ScenarioSnapshot Read(string pyPath, out ScenarioReadStatus status)
    {
        status = ScenarioReadStatus.Absent;
        if (string.IsNullOrEmpty(pyPath) || !File.Exists(pyPath)) return null;

        string src;
        try { src = File.ReadAllText(pyPath); }
        catch (Exception e)
        {
            status = ScenarioReadStatus.Unparseable;
            Debug.LogWarning("[ScenarioInlineReader] cannot read '" + pyPath + "': " + e.Message);
            return null;
        }

        ScenarioLocate located = LocateScenarioDict(src, out string dictLiteral);
        if (located == ScenarioLocate.NotFound)
            return null;   // Absent: no module-level SCENARIO assignment (silent SeedDefaults)
        if (located == ScenarioLocate.Malformed)
        {
            // a module-level SCENARIO dict is present but its braces don't balance (truncated /
            // mid-edit file). This is present-but-unreadable, NOT absent — surface it loudly so #66's
            // silent no-op never recurs (findings 0043 D3).
            status = ScenarioReadStatus.Unparseable;
            Debug.LogWarning("[ScenarioInlineReader] inline SCENARIO in '" + pyPath +
                "' has an unbalanced dict literal (truncated?); save a scenario sidecar.");
            return null;
        }

        try
        {
            var parser = new LiteralParser(dictLiteral);
            object value = parser.ParseValue();
            parser.SkipTrivia();
            if (!parser.AtEnd) throw new FormatException("trailing tokens after the SCENARIO dict literal");
            if (!(value is Dictionary<string, object> map))
                throw new FormatException("SCENARIO is not a dict literal");

            status = ScenarioReadStatus.Found;
            return BuildSnapshot(map);
        }
        catch (Exception e)
        {
            status = ScenarioReadStatus.Unparseable;
            Debug.LogWarning("[ScenarioInlineReader] inline SCENARIO in '" + pyPath +
                "' is present but unreadable (" + e.Message + "); save a scenario sidecar.");
            return null;
        }
    }

    enum ScenarioLocate { NotFound, Found, Malformed }

    // ---- locate the module-level `SCENARIO` assignment's dict literal ----
    // extract() honors ONLY module-level SCENARIO, so require the identifier at column 0 (no
    // indent — never `self.SCENARIO` / an indented re-bind), and NOT inside a triple-quoted
    // string (a module docstring that contains a line `SCENARIO = {...}` is prose, not an
    // assignment — ast sees no node). Allow an optional `: annotation`, then brace-match the first
    // `{ ... }` after it. A SCENARIO line with a `{` whose braces don't balance is Malformed
    // (→ Unparseable, loud); a SCENARIO line with no `{` (annotation-only / prose) is skipped.
    static ScenarioLocate LocateScenarioDict(string src, out string dictLiteral)
    {
        dictLiteral = null;
        int i = 0;
        while (i < src.Length)
        {
            bool lineStart = i == 0 || src[i - 1] == '\n';
            if (lineStart && Matches(src, i, "SCENARIO") &&
                (i + 8 >= src.Length || !IsIdentChar(src[i + 8])) &&
                !IsInsideTripleString(src, i))
            {
                // scan to the first '{' on the same logical assignment (the ": dict =" prefix
                // carries no '{'); a newline before any '{' means this wasn't the dict assignment.
                int j = i + 8;
                while (j < src.Length && src[j] != '{' && src[j] != '\n') j++;
                if (j < src.Length && src[j] == '{')
                    return TryExtractBraced(src, j, out dictLiteral) ? ScenarioLocate.Found : ScenarioLocate.Malformed;
            }
            int nl = src.IndexOf('\n', i);
            if (nl < 0) break;
            i = nl + 1;
        }
        return ScenarioLocate.NotFound;
    }

    // Is `pos` inside a triple-quoted (`"""` / `'''`) string? Scans [0, pos) tracking triple-quote
    // blocks, `#` comments, and single-line strings (so a quote/# inside them doesn't mislead). Only
    // triple-quotes can span newlines to swallow a column-0 SCENARIO, so that's all this needs to catch.
    static bool IsInsideTripleString(string src, int pos)
    {
        int i = 0;
        bool inTriple = false;
        char tripleQuote = ' ';
        while (i < pos)
        {
            if (inTriple)
            {
                if (i + 2 < src.Length && src[i] == tripleQuote && src[i + 1] == tripleQuote && src[i + 2] == tripleQuote)
                { inTriple = false; i += 3; continue; }
                i++; continue;
            }
            char c = src[i];
            if (c == '#') { int nl = src.IndexOf('\n', i); if (nl < 0) return false; i = nl + 1; continue; }
            if (c == '"' || c == '\'')
            {
                if (i + 2 < src.Length && src[i + 1] == c && src[i + 2] == c)
                { inTriple = true; tripleQuote = c; i += 3; continue; }
                // single-line string: skip to its (unescaped) closing quote.
                i++;
                while (i < src.Length && src[i] != c) { if (src[i] == '\\') i++; i++; }
                i++; continue;
            }
            i++;
        }
        return inTriple;
    }

    // Brace-match from the opening '{' to its closing '}', respecting string literals and
    // `#` comments so braces inside them don't count. Returns the inclusive `{...}` slice.
    static bool TryExtractBraced(string src, int braceStart, out string literal)
    {
        literal = null;
        int depth = 0;
        bool inStr = false;
        char quote = ' ';
        for (int i = braceStart; i < src.Length; i++)
        {
            char c = src[i];
            if (inStr)
            {
                if (c == '\\') { i++; continue; }   // skip the escaped char
                if (c == quote) inStr = false;
                continue;
            }
            if (c == '#') { int nl = src.IndexOf('\n', i); if (nl < 0) return false; i = nl; continue; }
            if (c == '\'' || c == '"') { inStr = true; quote = c; continue; }
            if (c == '{' || c == '[' || c == '(') depth++;
            else if (c == '}' || c == ']' || c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    if (c != '}') return false;   // mismatched closer
                    literal = src.Substring(braceStart, i - braceStart + 1);
                    return true;
                }
            }
        }
        return false;   // unbalanced
    }

    // ---- project the parsed dict into the 5 panel-owned fields (mirrors ScenarioSnapshot.FromJObject) ----
    static ScenarioSnapshot BuildSnapshot(Dictionary<string, object> map)
    {
        var s = new ScenarioSnapshot
        {
            Start = AsString(map, "start"),
            End = AsString(map, "end"),
            Granularity = AsString(map, "granularity"),
            InitialCash = AsCash(map, "initial_cash"),
        };
        // v2/v3 "instruments" (list) is canonical; tolerate v1 legacy "instrument" (single).
        if (map.TryGetValue("instruments", out object instr) && instr is List<object> list)
        {
            foreach (object o in list) if (o is string str) s.Instruments.Add(str);
        }
        else if (map.TryGetValue("instrument", out object single) && single is string one)
        {
            s.Instruments.Add(one);
        }
        return s;
    }

    static string AsString(Dictionary<string, object> map, string key)
        => map.TryGetValue(key, out object v) && v is string s ? s : null;

    static long? AsCash(Dictionary<string, object> map, string key)
    {
        if (!map.TryGetValue(key, out object v)) return null;
        if (v is long l) return l;
        if (v is double d) return (long)Math.Round(d);
        return null;
    }

    static bool Matches(string src, int at, string token)
    {
        if (at + token.Length > src.Length) return false;
        for (int k = 0; k < token.Length; k++) if (src[at + k] != token[k]) return false;
        return true;
    }

    static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ---- recursive-descent parser for the Python literal subset (str/int/float/list/tuple/
    // dict/True/False/None). Throws FormatException on anything outside the subset → Unparseable. ----
    sealed class LiteralParser
    {
        readonly string _s;
        int _p;

        public LiteralParser(string s) { _s = s; _p = 0; }

        public bool AtEnd => _p >= _s.Length;

        public void SkipTrivia()
        {
            while (_p < _s.Length)
            {
                char c = _s[_p];
                if (char.IsWhiteSpace(c)) { _p++; continue; }
                if (c == '#') { int nl = _s.IndexOf('\n', _p); _p = nl < 0 ? _s.Length : nl + 1; continue; }
                break;
            }
        }

        public object ParseValue()
        {
            SkipTrivia();
            if (AtEnd) throw new FormatException("unexpected end of input");
            char c = _s[_p];
            if (c == '{') return ParseDict();
            if (c == '[') return ParseSeq('[', ']');
            if (c == '(') return ParseSeq('(', ')');
            if (c == '\'' || c == '"') return ParseString();
            if (c == '-' || c == '+' || c == '.' || char.IsDigit(c)) return ParseNumber();
            if (Matches(_s, _p, "True")) { _p += 4; return true; }
            if (Matches(_s, _p, "False")) { _p += 5; return false; }
            if (Matches(_s, _p, "None")) { _p += 4; return null; }
            throw new FormatException("unexpected token at offset " + _p + ": '" + c + "'");
        }

        Dictionary<string, object> ParseDict()
        {
            var map = new Dictionary<string, object>();
            Expect('{');
            SkipTrivia();
            while (!AtEnd && _s[_p] != '}')
            {
                object key = ParseValue();
                SkipTrivia();
                Expect(':');
                object val = ParseValue();
                if (key is string ks) map[ks] = val;   // only string keys matter to the projection
                SkipTrivia();
                if (!AtEnd && _s[_p] == ',') { _p++; SkipTrivia(); continue; }
                break;
            }
            Expect('}');
            return map;
        }

        List<object> ParseSeq(char open, char close)
        {
            var list = new List<object>();
            Expect(open);
            SkipTrivia();
            while (!AtEnd && _s[_p] != close)
            {
                list.Add(ParseValue());
                SkipTrivia();
                if (!AtEnd && _s[_p] == ',') { _p++; SkipTrivia(); continue; }
                break;
            }
            Expect(close);
            return list;
        }

        string ParseString()
        {
            char quote = _s[_p++];
            var sb = new StringBuilder();
            while (_p < _s.Length)
            {
                char c = _s[_p++];
                if (c == '\\')
                {
                    if (_p >= _s.Length) throw new FormatException("dangling escape in string");
                    char e = _s[_p++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '0': sb.Append('\0'); break;
                        case '\\': sb.Append('\\'); break;
                        case '\'': sb.Append('\''); break;
                        case '"': sb.Append('"'); break;
                        // unknown escape (\x.. / \u.. / \N{..} / \0NN octal): the reference
                        // ast.literal_eval would decode these, but silently keeping the char would
                        // CORRUPT a panel-owned value. Refuse instead → Unparseable (loud safety net).
                        default: throw new FormatException("unsupported string escape '\\" + e + "'");
                    }
                    continue;
                }
                if (c == quote) return sb.ToString();
                sb.Append(c);
            }
            throw new FormatException("unterminated string literal");
        }

        object ParseNumber()
        {
            int start = _p;
            bool isFloat = false;
            while (_p < _s.Length)
            {
                char c = _s[_p];
                if (char.IsDigit(c) || c == '_') { _p++; continue; }
                if (c == '.' || c == 'e' || c == 'E') { isFloat = true; _p++; continue; }
                if ((c == '+' || c == '-') && _p > start && (_s[_p - 1] == 'e' || _s[_p - 1] == 'E')) { _p++; continue; }
                if ((c == '+' || c == '-') && _p == start) { _p++; continue; }
                break;
            }
            string tok = _s.Substring(start, _p - start).Replace("_", "");
            if (tok.Length == 0 || tok == "+" || tok == "-") throw new FormatException("malformed number");
            if (isFloat)
            {
                if (double.TryParse(tok, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            }
            else if (long.TryParse(tok, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)) return l;
            throw new FormatException("malformed number: '" + tok + "'");
        }

        void Expect(char c)
        {
            SkipTrivia();
            if (AtEnd || _s[_p] != c) throw new FormatException("expected '" + c + "' at offset " + _p);
            _p++;
        }
    }
}
