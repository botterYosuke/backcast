// PythonSyntaxToken.cs — issue #16 "Strategy Editor" (DURABLE tier, PURE CORE)
//
// The token vocabulary the lexical PythonHighlighter emits (findings 0010 §2,
// owner-locked). UnityEngine-FREE so the AFK gate tokenizes headless and the
// PythonSyntaxMeshEffect maps spans -> UIVertex.color separately.
//
// Token CLASSES are LEXICAL, not semantic (no LSP / parser): Keyword / String /
// Comment / Number / Decorator / Definition (the identifier right after def/class).
// `Default` is the IMPLICIT class of every gap NOT covered by a token (operators,
// punctuation, whitespace, identifiers) — the highlighter never EMITS a Default
// span; the mesh effect leaves uncovered glyphs at the base colour. Builtins are
// deliberately NOT a class (print/len are shadowable; a fixed list is pseudo-
// semantic, not syntax).
//
// SPAN CONTRACT (findings 0010 §2): (start, length) are UTF-16 code-unit offsets
// matching C# string / InputField indices; spans are ascending, non-overlapping,
// in-range, and never produced INSIDE a string or comment.

public enum PythonTokenClass
{
    Default = 0,   // implicit gap class (never emitted) — operators/punctuation/identifiers/whitespace
    Keyword,
    String,
    Comment,
    Number,
    Decorator,
    Definition,    // identifier immediately following `def`/`class`
}

public readonly struct PythonToken
{
    public readonly int start;   // UTF-16 offset of first char
    public readonly int length;  // UTF-16 code-unit count (> 0)
    public readonly PythonTokenClass cls;

    public PythonToken(int start, int length, PythonTokenClass cls)
    {
        this.start = start;
        this.length = length;
        this.cls = cls;
    }

    public int End => start + length;   // exclusive
}
