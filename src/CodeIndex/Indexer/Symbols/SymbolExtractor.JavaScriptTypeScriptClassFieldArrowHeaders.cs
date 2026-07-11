namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    // Class-field arrow like `handleClick = () => { ... }` is not matched by the method-header
    // parser because the identifier is followed by `=` instead of `(`. This parser handles that
    // shape (with optional TS modifiers, field type annotation, generics, and return type).
    // 正規表現や method-header パーサは `name = ... =>` 形式のクラスフィールド矢印関数を拾えないため、
    // 専用パーサでそのシェイプだけ（修飾子・フィールド型注釈・ジェネリクス・戻り値型を含む）をパースする。
    private static bool TryParseJavaScriptTypeScriptClassFieldArrowHeader(
        string sanitizedHeader,
        int startColumn,
        string? lang,
        out JavaScriptTypeScriptMethodHeaderInfo arrowInfo)
    {
        arrowInfo = default;
        var index = Math.Max(0, startColumn);
        string? visibility = null;

        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        TrySkipJavaScriptTypeScriptDecorators(sanitizedHeader, ref index);

        string? candidateName = null;
        while (index < sanitizedHeader.Length)
        {
            if (!TryReadJavaScriptTypeScriptMethodToken(sanitizedHeader, ref index, out var token))
                return false;

            if (token == "*")
                return false;

            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;

            if (TypeScriptBareMethodModifiers.Contains(token)
                && CanTreatJavaScriptTypeScriptMethodTokenAsModifier(sanitizedHeader, index))
            {
                // `get`/`set`/`async`/`abstract` as leading modifier here would turn the construct
                // back into a method (not an arrow field); bail so the method-header parser owns it.
                // `get`/`set`/`async`/`abstract` が先頭修飾子に来るケースは arrow field ではなく
                // method なので、method-header パーサ側に委ねるためここで諦める。
                if (token is "get" or "set" or "async" or "abstract")
                    return false;
                if (token is "public" or "private" or "protected")
                    visibility = token;
                continue;
            }

            candidateName = token;
            break;
        }

        if (candidateName == null)
            return false;

        if (index < sanitizedHeader.Length && (sanitizedHeader[index] == '?' || sanitizedHeader[index] == '!'))
        {
            index++;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == ':')
        {
            if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilFieldEquals(sanitizedHeader, ref index))
                return false;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != '=')
            return false;
        if (index + 1 < sanitizedHeader.Length && (sanitizedHeader[index + 1] == '=' || sanitizedHeader[index + 1] == '>'))
            return false;
        index++;
        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        if (index + 5 <= sanitizedHeader.Length
            && string.CompareOrdinal(sanitizedHeader, index, "async", 0, 5) == 0
            && (index + 5 == sanitizedHeader.Length || !IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[index + 5])))
        {
            index += 5;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        int? genericStartColumn = null;
        int? genericEndColumn = null;
        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == '<')
        {
            genericStartColumn = index;
            var angleDepth = 0;
            while (index < sanitizedHeader.Length)
            {
                var ch = sanitizedHeader[index];
                if (ch == '<')
                {
                    angleDepth++;
                }
                else if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
                {
                    index += 2;
                    continue;
                }
                else if (ch == '>')
                {
                    angleDepth--;
                    if (angleDepth == 0)
                    {
                        genericEndColumn = index;
                        index++;
                        break;
                    }
                }
                index++;
            }
            if (genericEndColumn == null)
                return false;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index >= sanitizedHeader.Length)
            return false;

        if (sanitizedHeader[index] == '(')
        {
            var parenDepth = 0;
            while (index < sanitizedHeader.Length)
            {
                var ch = sanitizedHeader[index];
                if (ch == '(')
                {
                    parenDepth++;
                }
                else if (ch == ')')
                {
                    parenDepth--;
                    if (parenDepth == 0)
                    {
                        index++;
                        break;
                    }
                }
                index++;
            }
            if (parenDepth != 0)
                return false;
        }
        else if (IsJavaScriptTypeScriptIdentifierStart(sanitizedHeader[index]))
        {
            index++;
            while (index < sanitizedHeader.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[index]))
                index++;
        }
        else
        {
            return false;
        }

        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        int? returnTypeStartColumn = null;
        int? returnTypeEndColumn = null;
        if (lang == "typescript" && index < sanitizedHeader.Length && sanitizedHeader[index] == ':')
        {
            returnTypeStartColumn = index;
            if (!TrySkipJavaScriptTypeScriptTypeAnnotationUntilArrow(sanitizedHeader, ref index, out var rtEnd))
                return false;
            returnTypeEndColumn = rtEnd;
            while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
                index++;
        }

        if (index + 1 >= sanitizedHeader.Length
            || sanitizedHeader[index] != '='
            || sanitizedHeader[index + 1] != '>')
            return false;

        index += 2;
        while (index < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[index]))
            index++;

        if (index >= sanitizedHeader.Length)
            return false;

        // Block-body arrow (`=> { ... }`). HeaderEndColumn == BodyStartColumn, both point at `{`.
        // ブロック本体矢印 (`=> { ... }`)。header 終端と body 開始は同じ `{` を指す。
        if (sanitizedHeader[index] == '{')
        {
            arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                candidateName,
                index,
                visibility,
                genericStartColumn,
                genericEndColumn,
                returnTypeStartColumn,
                returnTypeEndColumn,
                index);
            return true;
        }

        // Expression-body arrow (`=> expr;`). Walk until a class-field terminator at depth 0.
        // Explicit `;` always terminates; implicit ASI also terminates when we hit the enclosing
        // class body `}` or a newline followed by a new class-member start (identifier+`=`/`(`,
        // `#private`, `*name`, decorator, or modifier keyword). `[` is treated as continuation
        // here because a bare `[` is ambiguous between computed-member access and a computed
        // method name; see StartsJavaScriptTypeScriptClassMemberAt for the full rationale.
        // `{}` / `()` / `[]` stay balanced; strings / comments are already masked by the upstream
        // lexer. If the accumulated header ends at depth 0 with expression tokens but no visible
        // terminator, return false so TryCapture pulls another line and retries.
        // 式本体矢印 (`=> expr;`)。深さ 0 でのクラスフィールド終端まで歩く。明示的な `;` は常に終端し、
        // 暗黙の ASI は囲みクラス body の `}` か、改行直後に新しいクラスメンバの開始 (identifier+`=`/`(`、
        // `#private`、`*name`、decorator、修飾子キーワード) が来た場合にも終端する。`[` は computed
        // member access の継続と computed method 名の両方になり得るためここでは継続扱いとする
        // (詳細は StartsJavaScriptTypeScriptClassMemberAt のコメント参照)。
        // 括弧類はバランスを取り、文字列・コメントは上流の lexer でマスク済み。終端が見えないまま
        // 蓄積ヘッダの末尾に達したら false を返し、TryCapture に次の行を積ませる。
        var expressionStart = index;
        var parenDepth2 = 0;
        var bracketDepth2 = 0;
        var braceDepth2 = 0;
        int? lastNonWhitespace = null;
        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == ';' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0)
            {
                if (lastNonWhitespace == null)
                    return false;
                arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                    candidateName,
                    expressionStart,
                    visibility,
                    genericStartColumn,
                    genericEndColumn,
                    returnTypeStartColumn,
                    returnTypeEndColumn,
                    expressionStart,
                    HasBody: true,
                    ExpressionBodyEndColumn: lastNonWhitespace);
                return true;
            }

            if (ch == '}' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0)
            {
                // Enclosing class body `}` at depth 0. If we already have expression tokens that
                // can validly end a statement (identifier/number/`)`/`]`/`}`), treat it as ASI and
                // emit. Otherwise bail so the class scanner handles the closer.
                // 囲みクラス body の `}` (深さ 0)。識別子/数値/`)`/`]`/`}` のように文末になり得るトークンが
                // 既に見えていれば ASI として終端扱いで emit する。無ければクラススキャナに委ねるため false。
                if (lastNonWhitespace != null
                    && CanJavaScriptTypeScriptExpressionEndAt(sanitizedHeader[lastNonWhitespace.Value]))
                {
                    arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                        candidateName,
                        expressionStart,
                        visibility,
                        genericStartColumn,
                        genericEndColumn,
                        returnTypeStartColumn,
                        returnTypeEndColumn,
                        expressionStart,
                        HasBody: true,
                        ExpressionBodyEndColumn: lastNonWhitespace);
                    return true;
                }
                return false;
            }

            if (ch == '\n' && parenDepth2 == 0 && bracketDepth2 == 0 && braceDepth2 == 0
                && lastNonWhitespace != null
                && CanJavaScriptTypeScriptExpressionEndAt(sanitizedHeader[lastNonWhitespace.Value]))
            {
                var peek = index + 1;
                while (peek < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[peek]))
                    peek++;
                // peek == sanitizedHeader.Length means we exhausted the accumulated header after
                // this newline — need more input from TryCapture. Break out of the heuristic and
                // fall through to the normal end-of-input `return false` path.
                // peek が末尾に達した場合は、この改行以降に蓄積ヘッダ上の文字が尽きたということなので
                // TryCapture に次の行を積ませる必要がある。ヒューリスティックは停止し、ループ末尾の
                // end-of-input `return false` に任せる。
                if (peek < sanitizedHeader.Length
                    && StartsJavaScriptTypeScriptClassMemberAt(sanitizedHeader, peek))
                {
                    arrowInfo = new JavaScriptTypeScriptMethodHeaderInfo(
                        candidateName,
                        expressionStart,
                        visibility,
                        genericStartColumn,
                        genericEndColumn,
                        returnTypeStartColumn,
                        returnTypeEndColumn,
                        expressionStart,
                        HasBody: true,
                        ExpressionBodyEndColumn: lastNonWhitespace);
                    return true;
                }
            }

            if (ch == '(') parenDepth2++;
            else if (ch == ')' && parenDepth2 > 0) parenDepth2--;
            else if (ch == '[') bracketDepth2++;
            else if (ch == ']' && bracketDepth2 > 0) bracketDepth2--;
            else if (ch == '{') braceDepth2++;
            else if (ch == '}' && braceDepth2 > 0) braceDepth2--;

            if (!char.IsWhiteSpace(ch))
                lastNonWhitespace = index;
            index++;
        }

        return false;
    }

    // Returns true when `ch` is a token that can validly end a JavaScript / TypeScript expression
    // (identifier/digit tail, closing bracket, `$`/`_`, or the closing delimiter of a string /
    // template literal). The upstream lexer preserves the opening and closing `"`/`'`/`` ` `` in
    // the sanitized header (only the body content is blanked to spaces), so a string-returning
    // arrow such as `only = () => "x"` ends with a visible quote character here.
    // Operator-like characters (`+`, `.`, `,`, etc.) return false so multi-line expression
    // continuations are not accidentally cut off by the ASI heuristic.
    // `ch` が JavaScript / TypeScript の式を終端できるトークン (識別子/数字末尾、閉じ括弧、`$`/`_`、
    // 文字列・テンプレートリテラルの閉じデリミタ) なら true。上流の lexer は sanitized header 上で
    // `"` / `'` / `` ` `` の開き/閉じ文字は残し、リテラル本体だけをスペースに blank する。
    // そのため `only = () => "x"` のような文字列を返す式は、ここでは閉じクォートが lastNonWhitespace と
    // して可視のまま残る。演算子類 (`+`、`.`、`,` 等) は false を返すことで、複数行の式継続が ASI
    // ヒューリスティックで誤って途中終端されないようにする。
    private static bool CanJavaScriptTypeScriptExpressionEndAt(char ch)
    {
        if (char.IsLetterOrDigit(ch))
            return true;
        return ch is '_' or '$' or ')' or ']' or '}' or '"' or '\'' or '`';
    }

    // Returns true when the position starts a new class-body member declaration: `}` (class body
    // close), `;` (stray empty statement), `#` / `@` / `*<name>` lead tokens, or an identifier that
    // is either a well-known class-member modifier keyword or is followed by a class-field /
    // method-shorthand syntactic marker (`=`, `(`, `<`, `?`, `!`, `:`, `;`).
    // Note: `[` is intentionally NOT a member-start signal here. A bare `[` after a newline is
    // ambiguous between a computed method name (`[Symbol.iterator]()`) and a computed member
    // access continuation (`foo\n  [bar]`). JavaScript's ASI rule explicitly forbids inserting a
    // `;` before a line that starts with `[`, so any source file that wants the computed-method
    // reading must write an explicit `;` — which the outer loop's `;` branch already handles. That
    // makes "treat `[` as continuation" the safe default for this heuristic.
    // Feed a sanitized (lex-masked) header string; strings/comments must already be blanked.
    // 指定位置がクラスボディの新しいメンバ宣言を始めるかを判定する: `}` (クラス body 閉じ)、
    // `;` (空文)、`#` / `@` / `*<name>` の先頭トークン、あるいは識別子で「クラスメンバ修飾キーワード」
    // または直後が `=` / `(` / `<` / `?` / `!` / `:` / `;` の場合。
    // 注意: `[` はあえて member-start として扱わない。改行直後の素の `[` は computed method name
    // (`[Symbol.iterator]()`) と computed member access の継続 (`foo\n  [bar]`) の両方に見えてしまう。
    // JavaScript の ASI 規則は `[` で始まる行の前に自動で `;` を挿入しないため、計算メンバ名を意図する
    // ソースは明示的に `;` を書く必要があり、そのケースは外側ループの `;` 分岐で既に拾える。よって
    // この ASI ヒューリスティックでは `[` を継続として扱うのが安全な既定。
    // 呼び出し側は lexer でマスク済み (文字列/コメントが blanked) の sanitizedHeader を渡すこと。
    private static bool StartsJavaScriptTypeScriptClassMemberAt(string sanitizedHeader, int index)
    {
        if (index < 0 || index >= sanitizedHeader.Length)
            return false;
        var ch = sanitizedHeader[index];
        if (ch is '}' or ';' or '#' or '@')
            return true;
        if (ch == '*')
        {
            var j = index + 1;
            while (j < sanitizedHeader.Length && char.IsWhiteSpace(sanitizedHeader[j]))
                j++;
            if (j >= sanitizedHeader.Length)
                return false;
            var next = sanitizedHeader[j];
            return IsJavaScriptTypeScriptIdentifierStart(next) || next is '#' or '[';
        }
        if (!IsJavaScriptTypeScriptIdentifierStart(ch))
            return false;

        var end = index + 1;
        while (end < sanitizedHeader.Length && IsJavaScriptTypeScriptIdentifierPart(sanitizedHeader[end]))
            end++;
        var word = sanitizedHeader[index..end];
        if (word is "async" or "static" or "get" or "set" or "public" or "private" or "protected"
            or "readonly" or "override" or "abstract" or "declare" or "accessor" or "constructor")
        {
            return true;
        }

        var after = end;
        while (after < sanitizedHeader.Length && sanitizedHeader[after] != '\n' && char.IsWhiteSpace(sanitizedHeader[after]))
            after++;
        if (after >= sanitizedHeader.Length)
            return false;
        var follow = sanitizedHeader[after];
        return follow is '=' or '(' or '<' or '?' or '!' or ':' or ';';
    }

    // Walks a TypeScript type annotation starting at ':' through to the outer '=' that terminates
    // it (i.e., the class-field assignment operator). `=>` inside the type (arrow types) is
    // treated as a two-char token and skipped; `==` is likewise skipped so we do not terminate on
    // a stray comparison.
    // 型注釈 `:` から、フィールド代入の外側 `=` までを歩く。型内部の `=>` (arrow type) は 2 文字ひと組で
    // 読み飛ばし、`==` も比較演算子として読み飛ばして誤終端しないようにする。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilFieldEquals(string sanitizedHeader, ref int index)
    {
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                index += 2;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
            {
                if (ch == '=')
                {
                    if (index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '=')
                    {
                        index += 2;
                        continue;
                    }
                    return true;
                }
                if (ch == ';' || ch == ',')
                    return false;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            index++;
        }

        return false;
    }

    // Walks a TypeScript member-property type annotation from `:` to the terminating `;`.
    // Arrow types inside nested parens / angles / brackets are skipped as two-char tokens so
    // `=>` in function types does not terminate the walk early.
    // TypeScript の member-property 型注釈を `:` から終端 `;` まで歩く。入れ子の
    // 括弧 / 山括弧 / 角括弧内の arrow type は 2 文字トークンとして読み飛ばし、
    // function type 内の `=>` で早期終了しないようにする。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilSemicolon(string sanitizedHeader, ref int index, out int typeEndColumn)
    {
        typeEndColumn = -1;
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        var lastNonWs = index;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                    lastNonWs = index + 1;
                index += 2;
                continue;
            }

            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
            {
                if (ch == ';')
                {
                    typeEndColumn = lastNonWs;
                    return true;
                }
                if (!char.IsWhiteSpace(ch))
                    lastNonWs = index;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            index++;
        }

        return false;
    }

    // Walks a TypeScript return-type annotation from ':' to the terminating '=>'. Inner arrow
    // types inside parens/angles/brackets are skipped as two-char tokens without decrementing
    // depth. Returns the inclusive column of the last non-whitespace character of the type.
    // 戻り値型 `:` から最外殻の `=>` までを歩く。括弧/角括弧/山括弧内の arrow type は 2 文字単位で
    // 読み飛ばし深さを下げない。型末尾の非空白位置 (inclusive) を返す。
    private static bool TrySkipJavaScriptTypeScriptTypeAnnotationUntilArrow(
        string sanitizedHeader,
        ref int index,
        out int typeEndColumn)
    {
        typeEndColumn = -1;
        if (index >= sanitizedHeader.Length || sanitizedHeader[index] != ':')
            return false;
        var lastNonWs = index;
        index++;

        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var angleDepth = 0;

        while (index < sanitizedHeader.Length)
        {
            var ch = sanitizedHeader[index];

            if (ch == '=' && index + 1 < sanitizedHeader.Length && sanitizedHeader[index + 1] == '>')
            {
                if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && angleDepth == 0)
                {
                    typeEndColumn = lastNonWs;
                    return true;
                }
                lastNonWs = index + 1;
                index += 2;
                continue;
            }

            if (ch == '(') parenDepth++;
            else if (ch == ')' && parenDepth > 0) parenDepth--;
            else if (ch == '[') bracketDepth++;
            else if (ch == ']' && bracketDepth > 0) bracketDepth--;
            else if (ch == '{') braceDepth++;
            else if (ch == '}' && braceDepth > 0) braceDepth--;
            else if (ch == '<') angleDepth++;
            else if (ch == '>' && angleDepth > 0) angleDepth--;

            if (!char.IsWhiteSpace(ch))
                lastNonWs = index;
            index++;
        }

        return false;
    }
}
