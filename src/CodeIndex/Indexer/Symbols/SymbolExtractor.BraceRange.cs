namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static (int EndLine, int? BodyStartLine, int? BodyEndLine) FindBraceRange(string[] lines, int startIndex, int startColumn = 0, string? lang = null)
    {
        int depth = 0;
        bool opened = false;
        int? bodyStartLine = null;
        // In languages where `'...'` is a regular string literal (PHP) rather than a char
        // literal (Java/Kotlin/Scala/Swift/Go/C/C++/Dart) or a lifetime annotation (Rust / OCaml),
        // we must scan to the next unescaped `'` regardless of length so that unbalanced `(`,
        // `[`, `{`, `}` tokens inside long single-quoted strings do not leak into brace-depth
        // counters and collapse the enclosing body range.
        // PHP のように `'...'` が通常の文字列リテラルである言語では、閉じ `'` まで距離を制限せず
        // スキップしないと、文字列内の `(` / `[` / `{` / `}` で body 範囲が壊れる。
        bool singleQuoteIsString = lang == "php";
        // Track () and [] depth so `{` / `}` inside annotation arguments, function-default lambdas,
        // and similar paren/bracket-delimited contexts do not advance the body brace counter.
        // Without this, Java headers like `class Leaf extends @Ann({A.class, B.class}) Root {`
        // count the annotation-arg `{A.class, B.class}` as the body open/close pair, flip
        // `opened=true` on the inner `{`, close depth to 0 on the inner `}`, and return a 1-line
        // body range that stops before the real class body opens. Subsequent ctor-chain emission
        // then loses the enclosing type, silently dropping `super(...)` edges for annotated Java
        // hierarchies. Same issue applies to Kotlin / Scala default-argument lambdas inside `()`.
        // Comments and string/char literals are also skipped so that unbalanced `(` `)` `[` `]`
        // `{` `}` inside them (e.g. `class Leaf extends Root /* ( */ { ... }`, Kotlin docstrings,
        // or Rust attribute comment bodies) do not leave depth counters stuck above zero and
        // silently collapse the body range. This mirrors the C# path which already routes through
        // LexCSharpLine before counting braces.
        // アノテーション引数内の `{` / `}` を本物の本体ブレースと誤認しないよう `(` / `[` 深度を追い、
        // コメント・文字列・文字リテラル内の不均衡な括弧やブレースを無視する。
        int parenDepth = 0;
        int bracketDepth = 0;
        bool inBlockComment = false;
        bool inString = false;
        var allowKrParameterDeclarations = lang is "c" or "cpp";
        bool sawTopLevelClosingParen = false;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var scanLine = i == startIndex && startColumn > 0 && startColumn < lines[i].Length
                ? lines[i][startColumn..]
                : i == startIndex && startColumn >= lines[i].Length
                    ? string.Empty
                    : lines[i];

            int sawTerminator = -1;
            for (int j = 0; j < scanLine.Length; j++)
            {
                char c = scanLine[j];

                if (inBlockComment)
                {
                    if (c == '*' && j + 1 < scanLine.Length && scanLine[j + 1] == '/')
                    {
                        inBlockComment = false;
                        j++;
                    }
                    continue;
                }

                if (inString)
                {
                    if (c == '\\' && j + 1 < scanLine.Length)
                    {
                        j++;
                        continue;
                    }
                    if (c == '"')
                        inString = false;
                    continue;
                }

                if (c == '/' && j + 1 < scanLine.Length)
                {
                    if (scanLine[j + 1] == '/')
                        break;
                    if (scanLine[j + 1] == '*')
                    {
                        inBlockComment = true;
                        j++;
                        continue;
                    }
                }

                if (c == '"')
                {
                    inString = true;
                    continue;
                }

                if (c == '\'')
                {
                    if (singleQuoteIsString)
                    {
                        // PHP-style: `'...'` is a full string literal. Scan to the next
                        // unescaped `'` on this line regardless of length so long strings
                        // with `[` / `{` / `(` inside cannot leak into brace-depth counters.
                        // PHP の `'...'` はフルの文字列リテラル。閉じ `'` まで距離制限なく走査する。
                        var closeIdx = -1;
                        for (int k = j + 1; k < scanLine.Length; k++)
                        {
                            if (scanLine[k] == '\\' && k + 1 < scanLine.Length)
                            {
                                k++;
                                continue;
                            }
                            if (scanLine[k] == '\'')
                            {
                                closeIdx = k;
                                break;
                            }
                        }
                        // If no close on this line, swallow the rest of the line so multi-line
                        // PHP single-quoted strings do not corrupt brace depth mid-scan.
                        // その行に閉じが無ければ行末までスキップする（PHP の複数行 '...' 文字列対応）。
                        j = closeIdx > 0 ? closeIdx : scanLine.Length;
                        continue;
                    }

                    // Distinguish char literals (`'x'`, `'\n'`, `'\u{1}'`) from Rust / OCaml
                    // lifetime annotations (`'a`, `'static`, `'_`) and from possessive text
                    // in comments/strings we already skipped. A char literal has a closing
                    // `'` within a short distance; a lifetime does not. If we cannot locate
                    // a matching close within ~12 chars on this line, treat the `'` as a
                    // regular character so `Holder<'a>` does not swallow the `{` that follows.
                    // Rust の lifetime (`'a`) と char literal (`'x'`) を区別する。対応する閉じ `'`
                    // が近傍に無ければ lifetime として `'` を普通の文字扱いで読み飛ばす。
                    {
                        var closeIdx = -1;
                        var limit = Math.Min(scanLine.Length, j + 12);
                        for (int k = j + 1; k < limit; k++)
                        {
                            if (scanLine[k] == '\\' && k + 1 < scanLine.Length)
                            {
                                k++;
                                continue;
                            }
                            if (scanLine[k] == '\'')
                            {
                                closeIdx = k;
                                break;
                            }
                        }
                        if (closeIdx > 0)
                        {
                            j = closeIdx;
                        }
                    }
                    continue;
                }

                if (c == '(')
                    parenDepth++;
                else if (c == ')' && parenDepth > 0)
                {
                    parenDepth--;
                    if (allowKrParameterDeclarations && parenDepth == 0)
                        sawTopLevelClosingParen = true;
                }
                else if (c == '[')
                    bracketDepth++;
                else if (c == ']' && bracketDepth > 0)
                    bracketDepth--;
                else if (c == ';' && !opened)
                {
                    if (allowKrParameterDeclarations && sawTopLevelClosingParen && i > startIndex)
                        continue;
                    return (startIndex + 1, null, null);
                }
                else if (c == '{')
                {
                    if (parenDepth > 0 || bracketDepth > 0)
                        continue;
                    depth++;
                    if (!opened)
                    {
                        opened = true;
                        bodyStartLine = i + 1;
                    }
                }
                else if (c == '}' && opened)
                {
                    if (parenDepth > 0 || bracketDepth > 0)
                        continue;
                    depth--;
                    if (depth == 0)
                    {
                        sawTerminator = j;
                        break;
                    }
                }
            }

            if (sawTerminator >= 0)
                return (i + 1, bodyStartLine, i + 1);
            // Line comments reset at end of line (handled by the break above).
        }

        return opened
            ? (lines.Length, bodyStartLine, lines.Length)
            : (startIndex + 1, null, null);
    }
}
