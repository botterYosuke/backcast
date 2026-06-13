// PythonHighlighter.cs — issue #16 "Strategy Editor" (DURABLE tier, PURE CORE)
//
// The AUTHORITATIVE, headless LEXICAL Python tokenizer — the AFK gate proves THIS
// (no Canvas, no mesh, no input), exactly as #15's FloatingWindowMath / #13's
// CanvasViewMath are the authoritative math. NOTHING here touches a RectTransform,
// a render, or a Text; PythonSyntaxMeshEffect is the thin Unity boundary that turns
// these spans into UIVertex colours.
//
// CM6 capability parity (findings 0010 header): we match the VISUAL capability of
// CodeMirror 6's defaultHighlightStyle over Python, NOT its implementation — Lezer
// is a real parser; this is a single-pass LEXER. So function-name vs variable is
// NOT distinguished (that needs a parse); only def/class definition names are, via
// a trivial look-behind. That is sufficient for the AC ("Python syntax highlight").
//
// EMITTED CLASSES: Keyword / String / Comment / Number / Decorator / Definition.
// Default (operators, punctuation, identifiers, whitespace) is the IMPLICIT gap
// class and is NEVER emitted. Output spans are ascending, non-overlapping, in-range
// (findings 0010 §2). No token is ever produced inside a string or comment.
//
// LOCKED LEXER RULES (findings 0010 §2):
//   * UTF-16 (start,length) matching C# string indices.
//   * string prefixes case-insensitive, VALID combos only (r/b/u/f, rb/br/rf/fr);
//     f-string is highlighted as ONE String (no nested-expr highlighting).
//   * triple-quote (""" / ''') crosses newlines; UNTERMINATED -> String to EOF.
//   * `# comment` runs to EOL.
//   * decorator = leading-whitespace `@` at LOGICAL LINE START -> dotted name.
//   * def/class name: whitespace/newlines allowed between, but NOT across a
//     comment/string (those clear the pending-definition state).
//   * `\` line-continuation is Default (no special state).
//   * numbers: bin/oct/hex, exponent, imaginary suffix, underscores.

using System.Collections.Generic;

public static class PythonHighlighter
{
    static readonly HashSet<string> Keywords = new HashSet<string>
    {
        "False", "None", "True", "and", "as", "assert", "async", "await",
        "break", "class", "continue", "def", "del", "elif", "else", "except",
        "finally", "for", "from", "global", "if", "import", "in", "is",
        "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try",
        "while", "with", "yield", "match", "case",
    };

    // Valid string prefixes (lowercased). u cannot combine; r/b/f combine per the grammar.
    static readonly HashSet<string> StringPrefixes = new HashSet<string>
    {
        "r", "b", "u", "f", "rb", "br", "rf", "fr",
    };

    public static List<PythonToken> Tokenize(string src)
    {
        var tokens = new List<PythonToken>();
        if (string.IsNullOrEmpty(src)) return tokens;

        int n = src.Length;
        int i = 0;
        bool atLineStart = true;       // only whitespace seen since last newline (for decorators)
        bool expectDefName = false;    // previous significant token was def/class

        while (i < n)
        {
            char c = src[i];

            // --- newline: resets logical-line-start; continuation `\` is plain Default. ---
            if (c == '\n')
            {
                i++;
                atLineStart = true;
                // a bare newline does NOT clear expectDefName (whitespace/newlines allowed
                // between def/class and the name).
                continue;
            }

            // --- whitespace (not newline): preserves atLineStart and expectDefName. ---
            if (c == ' ' || c == '\t' || c == '\r' || c == '\f' || c == '\v')
            {
                i++;
                continue;
            }

            // --- comment: clears a pending def-name (cannot span a comment). ---
            if (c == '#')
            {
                int start = i;
                while (i < n && src[i] != '\n') i++;
                tokens.Add(new PythonToken(start, i - start, PythonTokenClass.Comment));
                atLineStart = false;
                expectDefName = false;
                continue;
            }

            // --- decorator: `@` at logical line start -> @ + dotted name. ---
            if (c == '@' && atLineStart)
            {
                int start = i;
                i++;                                   // consume '@'
                while (i < n && (src[i] == ' ' || src[i] == '\t')) i++;   // `@ foo` is legal
                // dotted name: identifier (. identifier)*
                while (i < n && IsIdentStart(src[i]))
                {
                    while (i < n && IsIdentPart(src[i])) i++;
                    if (i < n && src[i] == '.') { i++; continue; }
                    break;
                }
                tokens.Add(new PythonToken(start, i - start, PythonTokenClass.Decorator));
                atLineStart = false;
                expectDefName = false;
                continue;
            }

            // --- string (with optional prefix): clears a pending def-name. ---
            if (c == '"' || c == '\'')
            {
                i = ScanString(src, i, i, tokens);
                atLineStart = false;
                expectDefName = false;
                continue;
            }

            // --- identifier / keyword / definition / prefixed string. ---
            if (IsIdentStart(c))
            {
                int start = i;
                while (i < n && IsIdentPart(src[i])) i++;
                string word = src.Substring(start, i - start);

                // prefixed string? a valid prefix immediately followed by a quote.
                if (i < n && (src[i] == '"' || src[i] == '\'') && StringPrefixes.Contains(word.ToLowerInvariant()))
                {
                    i = ScanString(src, start, i, tokens);
                    atLineStart = false;
                    expectDefName = false;
                    continue;
                }

                if (Keywords.Contains(word))
                {
                    tokens.Add(new PythonToken(start, i - start, PythonTokenClass.Keyword));
                    // `def`/`class` arm the next identifier as a Definition.
                    expectDefName = (word == "def" || word == "class");
                }
                else if (expectDefName)
                {
                    tokens.Add(new PythonToken(start, i - start, PythonTokenClass.Definition));
                    expectDefName = false;
                }
                // else: a plain identifier -> Default (no span emitted); clears the pending name.
                else
                {
                    expectDefName = false;
                }
                atLineStart = false;
                continue;
            }

            // --- number: digit, or `.` followed by a digit. ---
            if (IsDigit(c) || (c == '.' && i + 1 < n && IsDigit(src[i + 1])))
            {
                int start = i;
                i = ScanNumber(src, i);
                tokens.Add(new PythonToken(start, i - start, PythonTokenClass.Number));
                atLineStart = false;
                expectDefName = false;
                continue;
            }

            // --- everything else (operators, punctuation, `@` mid-line, `\`): Default. ---
            i++;
            atLineStart = false;
            expectDefName = false;
        }

        return tokens;
    }

    // Scan a (possibly prefixed, possibly triple-quoted) string starting at the quote at
    // `quoteIdx`; the emitted span begins at `tokenStart` (the prefix, if any). Returns the
    // index just past the string. Unterminated -> runs to EOF (still one String span).
    static int ScanString(string src, int tokenStart, int quoteIdx, List<PythonToken> tokens)
    {
        int n = src.Length;
        char q = src[quoteIdx];
        int i = quoteIdx;

        bool triple = i + 2 < n && src[i + 1] == q && src[i + 2] == q;
        if (triple)
        {
            i += 3;
            while (i < n)
            {
                if (src[i] == '\\') { i += 2; continue; }   // escaped char (incl. escaped quote/newline)
                // a closing triple must fit before EOF, i.e. i+2 <= n-1 == i+2 < n; that single
                // test covers a close at the very end of the buffer too. No match -> advance;
                // if we reach EOF first the string is UNTERMINATED and runs to EOF (one String).
                if (i + 2 < n && src[i] == q && src[i + 1] == q && src[i + 2] == q) { i += 3; break; }
                i++;
            }
        }
        else
        {
            i++;   // opening quote
            while (i < n)
            {
                char ch = src[i];
                if (ch == '\\') { i += 2; continue; }       // escape (line continuation / escaped quote)
                if (ch == '\n') break;                       // single-line string ends at newline (unterminated)
                if (ch == q) { i++; break; }
                i++;
            }
        }
        if (i > n) i = n;
        tokens.Add(new PythonToken(tokenStart, i - tokenStart, PythonTokenClass.String));
        return i;
    }

    // Scan a numeric literal: 0x/0o/0b prefixed, or decimal with optional fraction, exponent,
    // imaginary suffix, and underscore digit separators.
    static int ScanNumber(string src, int i)
    {
        int n = src.Length;
        if (src[i] == '0' && i + 1 < n && (src[i + 1] == 'x' || src[i + 1] == 'X'
                                        || src[i + 1] == 'o' || src[i + 1] == 'O'
                                        || src[i + 1] == 'b' || src[i + 1] == 'B'))
        {
            i += 2;
            while (i < n && (IsHexDigit(src[i]) || src[i] == '_')) i++;
            return i;
        }

        while (i < n && (IsDigit(src[i]) || src[i] == '_')) i++;
        if (i < n && src[i] == '.')
        {
            i++;
            while (i < n && (IsDigit(src[i]) || src[i] == '_')) i++;
        }
        if (i < n && (src[i] == 'e' || src[i] == 'E'))
        {
            int save = i;
            i++;
            if (i < n && (src[i] == '+' || src[i] == '-')) i++;
            if (i < n && IsDigit(src[i]))
            {
                while (i < n && (IsDigit(src[i]) || src[i] == '_')) i++;
            }
            else
            {
                i = save;   // not an exponent after all
            }
        }
        if (i < n && (src[i] == 'j' || src[i] == 'J')) i++;   // imaginary
        return i;
    }

    static bool IsDigit(char c) => c >= '0' && c <= '9';
    static bool IsHexDigit(char c) => IsDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    // Identifier chars: ASCII letters / `_` / digits (continuation), PLUS any non-ASCII code
    // unit (>=128). Treating every non-ASCII unit as an identifier part keeps UTF-16 offsets
    // intact across BMP unicode AND surrogate pairs (findings 0010 §2: offset must not break
    // around a unicode identifier) without a full Unicode category table.
    static bool IsIdentStart(char c)
        => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || c >= 128;
    static bool IsIdentPart(char c)
        => IsIdentStart(c) || IsDigit(c);
}
