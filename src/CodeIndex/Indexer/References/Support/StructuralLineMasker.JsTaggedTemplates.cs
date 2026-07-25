namespace CodeIndex.Indexer;

internal static partial class StructuralLineMasker
{
    private static void TryRecordJsTaggedTemplateHit(
        string[] lines, char[] masked, int lineIndex, int backtickPos, ref List<JsTaggedTemplateHit>? hits, bool allowGenericTag)
    {
        // Skip inter-token whitespace backward, crossing line boundaries when the tag
        // identifier lives on a prior line (multi-line forms like `tag\n\`hello\``).
        // Prior lines are already fully masked by the outer loop, so we can safely read
        // `lines[i]` for `i < lineIndex`.
        // トークン間空白を後方に辿る。`tag\n\`hello\`` のようにタグが前行にある形も扱うため、
        // 行境界を越えて走査する。先行行は外側ループで既にマスク済みなので `lines[i]` を
        // そのまま参照できる。
        int curLine = lineIndex;
        int k = backtickPos - 1;
        while (true)
        {
            if (curLine == lineIndex)
            {
                while (k >= 0 && IsJsInterTokenWhitespace(masked[k]))
                    k--;
                if (k >= 0) break;
            }
            else
            {
                var l = lines[curLine];
                while (k >= 0 && IsJsInterTokenWhitespace(l[k]))
                    k--;
                if (k >= 0) break;
            }
            curLine--;
            if (curLine < 0) return;
            k = (curLine == lineIndex ? masked.Length : lines[curLine].Length) - 1;
        }

        char CharAt(int li, int col)
            => li == lineIndex ? masked[col] : lines[li][col];
        int LineLen(int li)
            => li == lineIndex ? masked.Length : lines[li].Length;

        // Skip a balanced `<...>` (TypeScript generics) so `html<T>\`...\`` still sees `html`.
        // The generic-strip is TypeScript-only (`allowGenericTag`) because plain JavaScript has
        // no generics: `foo<bar>\`x\`` is always the chained comparison `(foo<bar)>\`x\``. Even
        // inside TypeScript we still require the `<` to directly abut an identifier so
        // whitespace-bearing comparison expressions like `foo < bar > \`plain\`` are rejected,
        // and we ignore `>` from `=>` (arrow-function type inside the generic range). The
        // generic-strip is same-line only; a generic argument list spanning line breaks is
        // extremely rare in practice.
        // `html<T>\`...\`` のジェネリクスを読み飛ばすため、同一行内で `<...>` が釣り合っている
        // 場合のみ括弧を剥がす。ジェネリクスは TypeScript 限定（`allowGenericTag`）。JavaScript
        // では `foo<bar>\`x\`` は常に連鎖比較式なので generic とは扱わない。TypeScript 側でも
        // `foo < bar > \`plain\`` のような比較式と区別するため `<` が識別子に隣接していることを
        // 要求し、`=>` 由来の `>` は関数型なので閉じ記号として数えない。ジェネリクス走査は
        // 同一行限定。行をまたぐジェネリクス引数リストは実運用で極めて稀。
        if (CharAt(curLine, k) == '>' && allowGenericTag)
        {
            int probe = k - 1;
            int depth = 1;
            while (probe >= 0 && depth > 0)
            {
                var ch = CharAt(curLine, probe);
                if (ch == '>' && probe > 0 && CharAt(curLine, probe - 1) == '=')
                {
                    probe -= 2;
                    continue;
                }
                if (ch == '>') depth++;
                else if (ch == '<') depth--;
                probe--;
            }
            if (depth != 0)
                return;
            if (probe < 0 || !IsJsIdentifierPart(CharAt(curLine, probe)))
                return;
            k = probe;
        }

        if (!IsJsIdentifierPart(CharAt(curLine, k)))
            return;

        // Identifier read stays within the current line — JS identifiers do not cross lines.
        // 識別子は行をまたがないため同一行内で読み切る。
        int end = k + 1;
        while (k >= 0 && IsJsIdentifierPart(CharAt(curLine, k)))
            k--;
        int start = k + 1;

        if (!IsJsIdentifierStart(CharAt(curLine, start)))
            return;

        string name = curLine == lineIndex
            ? new string(masked, start, end - start)
            : lines[curLine].Substring(start, end - start);

        // Member-access detection: look for a `.` (possibly after inter-token whitespace,
        // possibly across line breaks like `obj\n.default\`x\``) before the tag identifier.
        // Member-access tags bypass the keyword denylist downstream because any reserved
        // word — including `default`, `finally`, `in`, `instanceof`, `delete`, `void`,
        // `case` — is a legal property name in JavaScript/TypeScript.
        // メンバーアクセス判定: タグ識別子の前に空白（行境界含む）を挟んで `.` があれば
        // メンバーアクセス。JS/TS ではすべての予約語が property 名になりうるので、
        // メンバーアクセス扱いのタグは下流のキーワード除外リスト（`default` / `finally` /
        // `in` / `instanceof` / `delete` / `void` / `case`）の対象外にする。
        bool isMemberAccess = false;
        int mLine = curLine;
        int mk = start - 1;
        while (true)
        {
            if (mk < 0)
            {
                mLine--;
                if (mLine < 0) break;
                mk = LineLen(mLine) - 1;
                continue;
            }
            char pc = CharAt(mLine, mk);
            if (IsJsInterTokenWhitespace(pc))
            {
                mk--;
                continue;
            }
            if (pc == '.') isMemberAccess = true;
            break;
        }

        (hits ??= []).Add(new JsTaggedTemplateHit(curLine + 1, start + 1, name, isMemberAccess));
    }

    // Decide whether `/` at the current scan position starts a regex literal rather
    // than a division operator. Division follows numeric / string / regex / template literals,
    // `)`, `]`, and non-keyword identifiers. Everything else (operators, `{`, `(`, `[`, `,`,
    // `;`, `=`, `?`, `:`, leading None) puts us in an expression-prefix context where `/`
    // begins a regex. Regex-prefix keywords such as `return`, `throw`, `typeof` re-enable
    // regex mode even though they are identifier-shaped.
    // `/` が division ではなく regex literal の開始かを判定する。数値 / 文字列 / regex /
    // template 等のリテラル、`)`、`]`、および非 keyword な識別子の後は division。
    // それ以外（演算子、`(` / `[` / `=` / `?` / `:` / `,` / `;` や行頭 None）は式の
    // 先頭コンテキストで `/` は regex。`return` / `throw` / `typeof` など regex-prefix
    // keyword は識別子形でも regex を許す。
    private static bool CanStartJsRegexLiteral(JsLexState lexState)
    {
        switch (lexState.PrevTokenKind)
        {
            case JsPrevTokenKind.None:
                return true;
            case JsPrevTokenKind.CloseParen:
            case JsPrevTokenKind.CloseBracket:
            case JsPrevTokenKind.CloseBrace:
            case JsPrevTokenKind.Numeric:
            case JsPrevTokenKind.Literal:
                return false;
            case JsPrevTokenKind.Identifier:
                return IsJsRegexPrefixKeyword(lexState.PrevIdentifier);
            case JsPrevTokenKind.StatementHeadCloseParen:
            case JsPrevTokenKind.Arrow:
            case JsPrevTokenKind.Other:
            default:
                return true;
        }
    }

    // Classify a nested `{` opened inside a template-literal hole as an expression
    // brace (object literal or `() => ({})` body, follows `=`, `(`, `[`, `,`, `:`,
    // `?`, operator, regex-prefix keyword) vs. a statement block (arrow-function
    // body, `if`/`while`/`for`/`function` block body — typically follows `)` or
    // `=>`). Expression braces classify the matching `}` as division-context; block
    // braces keep regex-legal classification so `{} /regex/` still parses.
    // テンプレートホール内でネストした `{` が expression brace（object literal /
    // `() => ({})` 本体）か statement block（arrow body / `if/while/for/function`
    // ブロック）かを判定する。expression は `=`、`(`、`[`、`,`、`:`、`?`、演算子、
    // regex-prefix keyword の直後。block は `)` や `=>` の直後。
    private static bool IsJsExpressionBraceContext(JsLexState lexState)
    {
        switch (lexState.PrevTokenKind)
        {
            case JsPrevTokenKind.CloseParen:
            case JsPrevTokenKind.StatementHeadCloseParen:
            case JsPrevTokenKind.Arrow:
                return false;
            case JsPrevTokenKind.Identifier:
                // Keywords that open a statement block follow the same rule as `)`.
                // `else { ... }`, `do { ... }`, `try { ... }`, `finally { ... }`,
                // and the optional-binding `catch { ... }` (ES2019).
                // block を開く keyword は `)` と同じ扱い。ES2019 の optional
                // binding 付き `catch { ... }` も block として扱う。
                return lexState.PrevIdentifier is not ("else" or "do" or "try" or "finally" or "catch");
            default:
                return true;
        }
    }

    private static bool IsJsRegexPrefixKeyword(string word) =>
        word is "return" or "throw" or "case" or "delete" or "typeof" or "void"
            or "new" or "in" or "of" or "instanceof" or "yield" or "await"
            or "else" or "do" or "finally";

    private static int SkipJsRegexLiteral(string line, int startIndex)
    {
        var p = startIndex + 1;
        var inCharClass = false;

        while (p < line.Length)
        {
            var ch = line[p];
            if (ch == '\\')
            {
                if (p + 1 < line.Length)
                {
                    p += 2;
                    continue;
                }

                return line.Length;
            }

            if (ch == '[')
            {
                inCharClass = true;
                p++;
                continue;
            }

            if (ch == ']' && inCharClass)
            {
                inCharClass = false;
                p++;
                continue;
            }

            if (ch == '/' && !inCharClass)
            {
                p++;
                while (p < line.Length && char.IsLetter(line[p]))
                    p++;
                return p;
            }

            p++;
        }

        return line.Length;
    }

    private static int MaskJsTemplateHoleString(string line, int startIndex, char[] masked, char quote, bool startsInsideString, out bool continuesOnNextLine)
    {
        var p = startIndex;
        if (!startsInsideString)
        {
            masked[p] = ' ';
            p++;
        }

        while (p < line.Length)
        {
            var ch = line[p];
            masked[p] = ' ';

            if (ch == '\\')
            {
                if (p + 1 == line.Length)
                {
                    continuesOnNextLine = true;
                    return p + 1;
                }

                if (p + 2 == line.Length && line[p + 1] == '\r')
                {
                    masked[p + 1] = ' ';
                    continuesOnNextLine = true;
                    return line.Length;
                }

                if (p + 1 < line.Length)
                {
                    masked[p + 1] = ' ';
                    p += 2;
                    continue;
                }
            }

            p++;
            if (ch == quote)
            {
                continuesOnNextLine = false;
                return p;
            }
        }

        continuesOnNextLine = false;
        return p;
    }

    // Mask a single-line Swift extended raw string `<N>#"..."<N>#` while preserving
    // any matching `\<N>#(...)` interpolation hole bodies so real call edges inside
    // the holes still reach the reference graph. Returns the position immediately
    // after the closing delimiter (or end of line if the source is malformed).
    // Callers must have already verified that `line[startIndex .. startIndex + hashCount]`
    // is `<N>#"`. Closes #1001.
    // Swift の単行 `<N>#"..."<N>#` 拡張 raw 文字列をマスクしつつ、内側の hash 数一致 `\<N>#(...)`
    // 補間ホール本文だけは残し、ホール内の本物の call が reference graph に届くようにする。
}
