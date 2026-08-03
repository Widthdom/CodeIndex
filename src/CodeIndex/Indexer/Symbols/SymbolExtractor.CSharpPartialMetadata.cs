using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private const int CSharpLeadingDeclarationLookbackLines = 64;
    private static readonly HashSet<string> CSharpStandaloneDeclarationModifiers = new(StringComparer.Ordinal)
    {
        "public", "protected", "internal", "private", "file", "new", "static", "abstract",
        "sealed", "virtual", "override", "readonly", "unsafe", "extern", "partial", "async",
        "ref", "required",
    };
    private static readonly string[] CSharpTypeDeclarationKeywords =
        ["class", "struct", "interface", "record", "enum", "delegate"];

    private static void PopulateCSharpPartialDeclarationMetadata(
        IReadOnlyList<string> lines,
        IReadOnlyList<SymbolRecord> symbols,
        Func<CSharpLexState[]>? getCSharpLineStartStates)
    {
        var lineStartStates = getCSharpLineStartStates?.Invoke();
        var firstDeclarationColumns = GetFirstCSharpDeclarationColumns(
            lines,
            symbols,
            lineStartStates);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("function" or "test.method" or "class" or "struct" or "interface" or "record" or "enum" or "delegate"))
                continue;

            var signature = symbol.Signature ?? string.Empty;
            var sanitizedSignature = SanitizeCSharpDeclarationEvidence(signature);
            var declarationHeader = ExtractCSharpDeclarationHeader(sanitizedSignature);
            var declarationModifierPrefix = ExtractCSharpDeclarationModifierPrefix(
                declarationHeader,
                symbol);
            var leading = ReadCSharpLeadingDeclarationEvidence(
                lines,
                symbol,
                lineStartStates,
                IsFirstCSharpDeclarationOnLine(
                    lines,
                    symbol,
                    lineStartStates,
                    firstDeclarationColumns));
            var supportsPartialDeclaration = symbol.Kind is
                "function" or "test.method" or "class" or "struct" or "interface" or "record";
            symbol.IsPartialDeclaration = supportsPartialDeclaration
                && (ContainsCSharpLeadingModifier(
                        declarationModifierPrefix,
                        "partial",
                        requireTrailingDeclarationType: symbol.Kind is "function" or "test.method")
                    || leading.HasPartialModifier);
            symbol.IsExplicitFileLocalDeclaration =
                symbol.Kind is "class" or "struct" or "interface" or "record" or "enum" or "delegate"
                && (ContainsCSharpLeadingModifier(declarationModifierPrefix, "file")
                    || leading.HasFileModifier);
            symbol.IsFileLocalDeclaration = symbol.IsExplicitFileLocalDeclaration == true;
            if (symbol.IsPartialDeclaration == true)
            {
                symbol.IdentifierStartColumn = FindCSharpDeclarationIdentifierColumn(
                    lines,
                    symbol,
                    lineStartStates);
            }

            var semanticScore = 0;
            if (ContainsCSharpAttributeEvidence(declarationHeader) || leading.HasAttribute)
                semanticScore += 2;
            if (leading.HasDocumentation)
                semanticScore += 1;
            if (symbol.Kind is "class" or "struct" or "interface" or "record"
                && ContainsCSharpTypeBaseList(declarationHeader))
            {
                semanticScore += 4;
            }
            if (ContainsCSharpWhereConstraint(declarationHeader))
                semanticScore += 1;
            symbol.DeclarationSemanticScore = semanticScore;
        }
    }

    internal static void RefreshCSharpPartialDeclarationMetadataFromHookSignature(
        SymbolRecord symbol)
    {
        symbol.IsPartialDeclaration = null;
        symbol.IsFileLocalDeclaration = false;
        symbol.IsExplicitFileLocalDeclaration = null;
        symbol.DeclarationSemanticScore = null;
        symbol.IdentifierStartColumn = null;

        if (symbol.Kind is not ("function" or "test.method" or "class" or "struct" or "interface" or "record" or "enum" or "delegate"))
            return;

        var sanitizedSignature = SanitizeCSharpDeclarationEvidence(symbol.Signature ?? string.Empty);
        var declarationHeader = ExtractCSharpDeclarationHeader(sanitizedSignature);
        var declarationModifierPrefix = ExtractCSharpDeclarationModifierPrefix(
            declarationHeader,
            symbol);
        var supportsPartialDeclaration = symbol.Kind is
            "function" or "test.method" or "class" or "struct" or "interface" or "record";
        symbol.IsPartialDeclaration = supportsPartialDeclaration
            && ContainsCSharpLeadingModifier(
                declarationModifierPrefix,
                "partial",
                requireTrailingDeclarationType: symbol.Kind is "function" or "test.method");
        symbol.IsExplicitFileLocalDeclaration =
            symbol.Kind is "class" or "struct" or "interface" or "record" or "enum" or "delegate"
            && ContainsCSharpLeadingModifier(declarationModifierPrefix, "file");
        symbol.IsFileLocalDeclaration = symbol.IsExplicitFileLocalDeclaration == true;

        var semanticScore = 0;
        if (ContainsCSharpAttributeEvidence(declarationHeader))
            semanticScore += 2;
        if (symbol.Kind is "class" or "struct" or "interface" or "record"
            && ContainsCSharpTypeBaseList(declarationHeader))
        {
            semanticScore += 4;
        }
        if (ContainsCSharpWhereConstraint(declarationHeader))
            semanticScore += 1;
        symbol.DeclarationSemanticScore = semanticScore;
    }

    private static Dictionary<int, int> GetFirstCSharpDeclarationColumns(
        IReadOnlyList<string> lines,
        IReadOnlyList<SymbolRecord> symbols,
        IReadOnlyList<CSharpLexState>? lineStartStates)
    {
        var firstColumns = new Dictionary<int, int>();
        foreach (var symbol in symbols)
        {
            var declarationLine = symbol.Line > 0 ? symbol.Line : symbol.StartLine;
            if (declarationLine <= 0 || declarationLine > lines.Count)
                continue;

            var lineIndex = declarationLine - 1;
            var lineStartState = lineStartStates != null && lineIndex < lineStartStates.Count
                ? lineStartStates[lineIndex]
                : new CSharpLexState();
            var declarationColumn = FindCSharpDeclarationOccurrenceStartColumn(
                lines[lineIndex],
                symbol,
                lineStartState);
            if (declarationColumn < 0)
                declarationColumn = symbol.StartColumn ?? int.MaxValue;

            if (!firstColumns.TryGetValue(declarationLine, out var firstColumn)
                || declarationColumn < firstColumn)
            {
                firstColumns[declarationLine] = declarationColumn;
            }
        }

        return firstColumns;
    }

    private static bool IsFirstCSharpDeclarationOnLine(
        IReadOnlyList<string> lines,
        SymbolRecord symbol,
        IReadOnlyList<CSharpLexState>? lineStartStates,
        IReadOnlyDictionary<int, int> firstDeclarationColumns)
    {
        var declarationLine = symbol.Line > 0 ? symbol.Line : symbol.StartLine;
        if (declarationLine <= 0
            || declarationLine > lines.Count
            || !firstDeclarationColumns.TryGetValue(declarationLine, out var firstColumn))
        {
            return true;
        }

        var lineIndex = declarationLine - 1;
        var lineStartState = lineStartStates != null && lineIndex < lineStartStates.Count
            ? lineStartStates[lineIndex]
            : new CSharpLexState();
        var declarationColumn = FindCSharpDeclarationOccurrenceStartColumn(
            lines[lineIndex],
            symbol,
            lineStartState);
        if (declarationColumn < 0)
            declarationColumn = symbol.StartColumn ?? int.MaxValue;
        return declarationColumn <= firstColumn;
    }

    private static string SanitizeCSharpDeclarationEvidence(string signature)
    {
        if (string.IsNullOrEmpty(signature))
            return string.Empty;

        var sanitized = new System.Text.StringBuilder(signature.Length);
        var state = new CSharpLexState();
        var lineStart = 0;
        while (lineStart <= signature.Length)
        {
            var lineEnd = signature.IndexOf('\n', lineStart);
            if (lineEnd < 0)
                lineEnd = signature.Length;

            var lexed = LexCSharpLine(signature[lineStart..lineEnd], state);
            if (sanitized.Length > 0)
                sanitized.Append('\n');
            sanitized.Append(lexed.SanitizedLine);
            state = lexed.EndState;

            if (lineEnd == signature.Length)
                break;
            lineStart = lineEnd + 1;
        }

        return sanitized.ToString();
    }

    private static string ExtractCSharpDeclarationHeader(string declaration)
    {
        var parenthesisDepth = 0;
        var bracketDepth = 0;
        for (var index = 0; index < declaration.Length; index++)
        {
            switch (declaration[index])
            {
                case '(':
                    parenthesisDepth++;
                    break;
                case ')' when parenthesisDepth > 0:
                    parenthesisDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    break;
                case '{' when parenthesisDepth == 0 && bracketDepth == 0:
                    return declaration[..index];
                case '=' when parenthesisDepth == 0
                    && bracketDepth == 0
                    && index + 1 < declaration.Length
                    && declaration[index + 1] == '>':
                    return declaration[..index];
            }
        }

        return declaration;
    }

    private static string ExtractCSharpTypeDeclarationModifierPrefix(
        string declarationHeader,
        string symbolName)
    {
        var header = declarationHeader.AsSpan();
        var name = symbolName.AsSpan().TrimStart('@');
        var bestKeywordIndex = -1;
        var bestKeywordDistance = int.MaxValue;
        foreach (var keyword in CSharpTypeDeclarationKeywords)
        {
            var searchStart = 0;
            while (searchStart < header.Length)
            {
                var keywordIndex = FindCSharpIdentifierToken(header, keyword, searchStart);
                if (keywordIndex < 0)
                    break;

                if (keyword is "class" or "struct"
                    && IsCSharpExplicitRecordSuffix(header, keywordIndex))
                {
                    searchStart = keywordIndex + keyword.Length;
                    continue;
                }

                var nameIndex = FindCSharpIdentifierToken(
                    header,
                    name,
                    keywordIndex + keyword.Length);
                if (nameIndex >= 0)
                {
                    var distance = nameIndex - keywordIndex - keyword.Length;
                    if (distance < bestKeywordDistance)
                    {
                        bestKeywordIndex = keywordIndex;
                        bestKeywordDistance = distance;
                    }
                }

                searchStart = keywordIndex + keyword.Length;
            }
        }

        if (bestKeywordIndex < 0)
            return declarationHeader;

        var prefix = declarationHeader[..bestKeywordIndex];
        var attributeDepth = 0;
        var lastAttributeEnd = -1;
        for (var index = 0; index < prefix.Length; index++)
        {
            if (prefix[index] == '[')
            {
                attributeDepth++;
            }
            else if (prefix[index] == ']' && attributeDepth > 0)
            {
                attributeDepth--;
                if (attributeDepth == 0)
                    lastAttributeEnd = index;
            }
        }

        return lastAttributeEnd >= 0
            ? prefix[(lastAttributeEnd + 1)..]
            : prefix;
    }

    internal static bool ContainsCSharpTypeBaseList(string declarationHeader)
    {
        var bracketDepth = 0;
        var parenthesisDepth = 0;
        var angleDepth = 0;
        var declarationKeywordSeen = false;
        var declarationNameSeen = false;
        var recordMayHaveExplicitKind = false;

        for (var index = 0; index < declarationHeader.Length;)
        {
            var character = declarationHeader[index];
            switch (character)
            {
                case '[':
                    bracketDepth++;
                    index++;
                    continue;
                case ']' when bracketDepth > 0:
                    bracketDepth--;
                    index++;
                    continue;
                case '(' when bracketDepth == 0:
                    parenthesisDepth++;
                    index++;
                    continue;
                case ')' when bracketDepth == 0 && parenthesisDepth > 0:
                    parenthesisDepth--;
                    index++;
                    continue;
                case '<' when bracketDepth == 0 && parenthesisDepth == 0:
                    angleDepth++;
                    index++;
                    continue;
                case '>' when bracketDepth == 0 && parenthesisDepth == 0 && angleDepth > 0:
                    angleDepth--;
                    index++;
                    continue;
                case ':' when declarationNameSeen
                              && bracketDepth == 0
                              && parenthesisDepth == 0
                              && angleDepth == 0:
                    return true;
            }

            if (bracketDepth != 0
                || parenthesisDepth != 0
                || angleDepth != 0
                || !(character is '@' or '_' || char.IsLetter(character)))
            {
                index++;
                continue;
            }

            var tokenStart = index;
            var tokenIsVerbatim = character == '@';
            index++;
            while (index < declarationHeader.Length && IsCSharpIdentifierPart(declarationHeader[index]))
                index++;
            var token = declarationHeader.AsSpan(tokenStart, index - tokenStart).TrimStart('@');

            if (!declarationKeywordSeen)
            {
                if (!tokenIsVerbatim
                    && (token.SequenceEqual("class")
                    || token.SequenceEqual("struct")
                    || token.SequenceEqual("interface")))
                {
                    declarationKeywordSeen = true;
                }
                else if (!tokenIsVerbatim && token.SequenceEqual("record"))
                {
                    declarationKeywordSeen = true;
                    recordMayHaveExplicitKind = true;
                }
                continue;
            }

            if (!declarationNameSeen)
            {
                if (recordMayHaveExplicitKind
                    && !tokenIsVerbatim
                    && (token.SequenceEqual("class") || token.SequenceEqual("struct")))
                {
                    recordMayHaveExplicitKind = false;
                    continue;
                }

                declarationNameSeen = true;
                continue;
            }

            if (!tokenIsVerbatim && token.SequenceEqual("where"))
                return false;
        }

        return false;
    }

    private static bool IsCSharpExplicitRecordSuffix(
        ReadOnlySpan<char> declarationHeader,
        int keywordIndex)
    {
        var cursor = keywordIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(declarationHeader[cursor]))
            cursor--;

        const string RecordKeyword = "record";
        var recordStart = cursor - RecordKeyword.Length + 1;
        return recordStart >= 0
            && declarationHeader.Slice(recordStart, RecordKeyword.Length).SequenceEqual(RecordKeyword)
            && (recordStart == 0 || !IsCSharpIdentifierPart(declarationHeader[recordStart - 1]));
    }

    private static string ExtractCSharpDeclarationModifierPrefix(
        string declarationHeader,
        SymbolRecord symbol)
        => symbol.Kind is "class" or "struct" or "interface" or "record" or "enum" or "delegate"
            ? ExtractCSharpTypeDeclarationModifierPrefix(declarationHeader, symbol.Name)
            : ExtractCSharpCallableDeclarationModifierPrefix(declarationHeader, symbol.Name);

    private static string ExtractCSharpCallableDeclarationModifierPrefix(
        string declarationHeader,
        string symbolName)
    {
        var header = declarationHeader.AsSpan();
        var name = symbolName.AsSpan().TrimStart('@');
        var searchStart = 0;
        while (searchStart < header.Length)
        {
            var nameColumn = FindCSharpIdentifierToken(header, name, searchStart);
            if (nameColumn < 0)
                break;

            if (IsOutsideCSharpAttributeList(header, nameColumn)
                && IsCSharpCallableNameOccurrence(header, nameColumn, name.Length))
            {
                return declarationHeader[..nameColumn];
            }

            searchStart = nameColumn + Math.Max(1, name.Length);
        }

        return declarationHeader;
    }

    private static bool ContainsCSharpLeadingModifier(
        string declarationPrefix,
        string modifier,
        bool requireTrailingDeclarationType = false)
    {
        var remaining = declarationPrefix.AsSpan().Trim();
        while (!remaining.IsEmpty && remaining[0] == '[')
        {
            var depth = 0;
            var attributeEnd = -1;
            for (var index = 0; index < remaining.Length; index++)
            {
                if (remaining[index] == '[')
                    depth++;
                else if (remaining[index] == ']' && --depth == 0)
                {
                    attributeEnd = index;
                    break;
                }
            }

            if (attributeEnd < 0)
                return false;
            remaining = remaining[(attributeEnd + 1)..].TrimStart();
        }

        var hasModifier = false;
        while (!remaining.IsEmpty)
        {
            if (remaining[0] == '@')
                return requireTrailingDeclarationType && hasModifier;

            var tokenLength = 0;
            while (tokenLength < remaining.Length
                && (remaining[tokenLength] == '_'
                    || char.IsLetterOrDigit(remaining[tokenLength])))
            {
                tokenLength++;
            }

            if (tokenLength == 0)
                return requireTrailingDeclarationType && hasModifier;
            var token = remaining[..tokenLength];
            if (!CSharpStandaloneDeclarationModifiers.Contains(token.ToString()))
                return requireTrailingDeclarationType && hasModifier;

            var trailing = remaining[tokenLength..].TrimStart();
            if (requireTrailingDeclarationType
                && (trailing.IsEmpty || IsCSharpTypeContinuation(trailing[0])))
            {
                // A contextual keyword at the end of a callable prefix, or followed by
                // type punctuation, is the return type rather than a declaration modifier.
                // callable prefix 末尾、または型 punctuation の直前にある contextual
                // keyword は declaration modifier ではなく return type とみなす。
                return hasModifier;
            }

            hasModifier |= token.SequenceEqual(modifier);
            remaining = trailing;
        }

        return !requireTrailingDeclarationType && hasModifier;
    }

    private static bool IsCSharpTypeContinuation(char value)
        => value is '.' or ':' or '<' or '[' or '?' or '*';

    internal static bool ContainsCSharpPartialDeclarationModifier(
        string? signature,
        string? kind,
        string? symbolName)
    {
        if (string.IsNullOrWhiteSpace(signature)
            || string.IsNullOrWhiteSpace(kind)
            || string.IsNullOrWhiteSpace(symbolName)
            || kind is not ("function" or "test.method" or "class" or "struct" or "interface" or "record"))
        {
            return false;
        }

        var declarationHeader = ExtractCSharpDeclarationHeader(
            SanitizeCSharpDeclarationEvidence(signature));
        var modifierPrefix = kind is "class" or "struct" or "interface" or "record"
            ? ExtractCSharpTypeDeclarationModifierPrefix(declarationHeader, symbolName)
            : ExtractCSharpCallableDeclarationModifierPrefix(declarationHeader, symbolName);
        return ContainsCSharpLeadingModifier(
            modifierPrefix,
            "partial",
            requireTrailingDeclarationType: kind is "function" or "test.method");
    }

    private static bool ContainsCSharpAttributeEvidence(string declarationHeader)
    {
        for (var index = 0; index < declarationHeader.Length; index++)
        {
            if (declarationHeader[index] != '[')
                continue;

            var previous = index - 1;
            while (previous >= 0 && char.IsWhiteSpace(declarationHeader[previous]))
                previous--;
            if ((previous < 0 || declarationHeader[previous] is '(' or ',' or '<')
                && !IsCSharpGlobalAttributeTarget(declarationHeader.AsSpan(index + 1)))
                return true;
        }

        return false;
    }

    private static bool IsCSharpGlobalAttributeTarget(ReadOnlySpan<char> text)
    {
        var remaining = text.TrimStart();
        if (!remaining.IsEmpty && remaining[0] == '[')
            remaining = remaining[1..].TrimStart();

        foreach (var target in new[] { "assembly", "module" })
        {
            if (!remaining.StartsWith(target, StringComparison.Ordinal))
                continue;

            var cursor = target.Length;
            if (cursor < remaining.Length && IsCSharpIdentifierPart(remaining[cursor]))
                continue;
            while (cursor < remaining.Length && char.IsWhiteSpace(remaining[cursor]))
                cursor++;
            if (cursor < remaining.Length && remaining[cursor] == ':')
                return true;
        }

        return false;
    }

    private static bool ContainsCSharpModifier(string declaration, string modifier)
    {
        var searchStart = 0;
        while (searchStart <= declaration.Length - modifier.Length)
        {
            var relative = declaration.AsSpan(searchStart).IndexOf(modifier, StringComparison.Ordinal);
            if (relative < 0)
                return false;

            var index = searchStart + relative;
            var beforeIsIdentifier = index > 0 && IsCSharpIdentifierPart(declaration[index - 1]);
            var afterIndex = index + modifier.Length;
            var afterIsIdentifier = afterIndex < declaration.Length && IsCSharpIdentifierPart(declaration[afterIndex]);
            if (!beforeIsIdentifier && !afterIsIdentifier)
                return true;

            searchStart = index + Math.Max(1, modifier.Length);
        }

        return false;
    }

    internal static bool ContainsCSharpWhereConstraint(string declarationHeader)
        => ContainsCSharpModifier(declarationHeader, "where");

    private static CSharpLeadingDeclarationEvidence ReadCSharpLeadingDeclarationEvidence(
        IReadOnlyList<string> lines,
        SymbolRecord symbol,
        IReadOnlyList<CSharpLexState>? lineStartStates,
        bool consumePreviousLineEvidence)
    {
        var declarationStartLine = symbol.StartLine;
        var lineIndex = Math.Min(lines.Count, Math.Max(1, declarationStartLine)) - 2;
        var minimumLineIndex = Math.Max(0, lineIndex - CSharpLeadingDeclarationLookbackLines + 1);
        if (lineStartStates != null)
        {
            // Keep the ordinary evidence scan bounded, but if its boundary lands inside
            // one delimited comment, extend only to that comment's lexer-confirmed opener.
            // This preserves adjacent long `/** ... */` documentation without allowing
            // unrelated modifiers or attributes arbitrarily far above the declaration.
            // 通常の evidence scan は上限を維持する。ただし境界が delimited comment
            // 内なら lexer が確認した opener までだけ延長し、離れた modifier / attribute
            // を拾わずに長い `/** ... */` documentation の隣接性を保持する。
            while (minimumLineIndex > 0
                   && minimumLineIndex < lineStartStates.Count
                   && lineStartStates[minimumLineIndex].Mode == CSharpLexMode.BlockComment)
            {
                minimumLineIndex--;
            }
        }
        var hasPartialModifier = false;
        var hasFileModifier = false;
        var hasAttribute = HasCSharpDeclarationLineLeadingAttribute(
            lines,
            symbol,
            lineStartStates);
        var hasDocumentation = HasCSharpDeclarationLineLeadingDocumentation(
            lines,
            symbol,
            lineStartStates);
        var documentationEvidenceAdjacent = true;
        var attributeDepth = 0;
        var pendingAttributeEvidence = false;
        var pendingAttributeIsGlobal = false;
        var closedConditionalDirectiveDepth = 0;
        var skippingConditionalSiblingBranch = false;

        // Standalone modifiers, attributes, and documentation on preceding lines bind to
        // the first declaration occurrence on the next line. Later same-line declarations
        // must derive evidence only from their own declaration text.
        // preceding line の standalone modifier・attribute・documentation は次行の最初の
        // declaration occurrence にだけ属する。同一行の後続宣言は自身の宣言 text だけを使う。
        if (!consumePreviousLineEvidence)
        {
            return new CSharpLeadingDeclarationEvidence(
                HasPartialModifier: false,
                HasFileModifier: false,
                HasAttribute: hasAttribute,
                HasDocumentation: hasDocumentation);
        }

        for (; lineIndex >= minimumLineIndex; lineIndex--)
        {
            var raw = lines[lineIndex].AsSpan().Trim();
            var lineStartState = lineStartStates != null && lineIndex < lineStartStates.Count
                ? lineStartStates[lineIndex]
                : new CSharpLexState();
            if (raw.IsEmpty)
            {
                // Whitespace is valid declaration trivia between standalone modifiers
                // and the declaration. Outside an active delimited comment it detaches
                // XML documentation from the declaration for representative ranking;
                // inside `/** ... */` it remains part of the same documentation comment.
                // standalone modifier と宣言の間の空行は有効な declaration trivia だが、
                // active な `/** ... */` の外側なら XML documentation の representative
                // rank 上の隣接性を切り、内側なら同じ documentation comment として維持する。
                if (lineStartState.Mode != CSharpLexMode.BlockComment)
                    documentationEvidenceAdjacent = false;
                continue;
            }

            var startsInDeclarationCode = lineStartState.Mode == CSharpLexMode.Code
                && lineStartState.InterpolationBraceDepth == 0;
            var sanitizedLine = LexCSharpLine(lines[lineIndex], lineStartState).SanitizedLine;
            var trimmed = sanitizedLine.AsSpan().Trim();
            if (startsInDeclarationCode
                && TryReadCSharpDirectiveKeyword(trimmed, out var directiveKeyword))
            {
                if (directiveKeyword == "endif"
                    && closedConditionalDirectiveDepth == 0
                    && TryReadClosedCSharpConditionalDeclarationEvidence(
                        lines,
                        lineStartStates,
                        minimumLineIndex,
                        lineIndex,
                        out var conditionalEvidence))
                {
                    if (conditionalEvidence.HasCodeBoundary)
                        break;

                    // A completed conditional contributes `partial` and declaration-attribute
                    // evidence only when every possible branch contributes it. Conversely, any
                    // branch-local `file` is retained because grouping that declaration across
                    // files would be unsafe in that compilation. Without an explicit `#else`,
                    // include an implicit empty branch in all decisions.
                    // 完了した conditional の `partial` と declaration attribute は全分岐が
                    // 供給する場合だけ採用する。一方、branch-local な `file` は、その
                    // compilation で別ファイルと grouping すると危険なため一分岐だけでも
                    // 保持する。明示的な `#else` がなければ全判定に暗黙の空分岐を含める。
                    hasPartialModifier |= conditionalEvidence.HasPartialModifier;
                    hasFileModifier |= conditionalEvidence.HasFileModifier;
                    hasAttribute |= conditionalEvidence.HasAttribute;
                    lineIndex = conditionalEvidence.OpeningDirectiveLineIndex;
                    continue;
                }

                switch (directiveKeyword)
                {
                    case "endif":
                        closedConditionalDirectiveDepth++;
                        break;
                    case "if" when closedConditionalDirectiveDepth > 0:
                        closedConditionalDirectiveDepth--;
                        if (closedConditionalDirectiveDepth == 0)
                            skippingConditionalSiblingBranch = false;
                        break;
                    case "else" or "elif" when closedConditionalDirectiveDepth == 0:
                        // Skip the sibling branch, then resume before its matching `#if`.
                        // A standalone modifier before that conditional still belongs to
                        // every declaration alternative inside it.
                        // 兄弟分岐を読み飛ばし、対応する `#if` より前から走査を再開する。
                        // conditional より前の standalone modifier は各宣言候補に属する。
                        closedConditionalDirectiveDepth = 1;
                        skippingConditionalSiblingBranch = true;
                        break;
                }
                continue;
            }

            if (closedConditionalDirectiveDepth > 0)
            {
                // A sibling branch must be ignored through its matching `#if`, but code in
                // a completed conditional before the declaration is still a declaration
                // boundary. This keeps an outer modifier bound to the declaration inside
                // that completed block instead of lending it to the following declaration.
                // 兄弟分岐は対応する `#if` まで無視する一方、宣言前に完了した conditional
                // 内の code は declaration 境界とする。外側 modifier を block 内の宣言から
                // 後続宣言へ貸し出さない。
                if (skippingConditionalSiblingBranch || trimmed.IsEmpty)
                    continue;
                break;
            }

            if (startsInDeclarationCode && raw.StartsWith("///", StringComparison.Ordinal))
            {
                hasDocumentation |= documentationEvidenceAdjacent;
                continue;
            }

            if (startsInDeclarationCode && raw.StartsWith("/**", StringComparison.Ordinal))
            {
                hasDocumentation |= documentationEvidenceAdjacent;
                continue;
            }

            if (trimmed.IsEmpty)
                continue;

            var lastAttributeClose = trimmed.LastIndexOf(']');
            var trailingModifiers = lastAttributeClose >= 0
                ? trimmed[(lastAttributeClose + 1)..].Trim()
                : ReadOnlySpan<char>.Empty;
            var trailingHasPartial = false;
            var trailingHasFile = false;
            var hasTrailingModifiers = !trailingModifiers.IsEmpty
                && TryReadStandaloneCSharpModifiers(
                    trailingModifiers,
                    out trailingHasPartial,
                    out trailingHasFile);
            var isAttributeLine = attributeDepth > 0
                || trimmed[0] == '['
                || trimmed[^1] == ']'
                || hasTrailingModifiers;
            if (isAttributeLine)
            {
                if (!trailingModifiers.IsEmpty)
                {
                    if (!hasTrailingModifiers)
                        break;

                    hasPartialModifier |= trailingHasPartial;
                    hasFileModifier |= trailingHasFile;
                }
                pendingAttributeEvidence = true;
                pendingAttributeIsGlobal |= IsCSharpGlobalAttributeTarget(trimmed);
                attributeDepth += CountCharacter(trimmed, ']') - CountCharacter(trimmed, '[');
                attributeDepth = Math.Max(0, attributeDepth);
                if (attributeDepth == 0)
                {
                    hasAttribute |= pendingAttributeEvidence && !pendingAttributeIsGlobal;
                    pendingAttributeEvidence = false;
                    pendingAttributeIsGlobal = false;
                }
                continue;
            }

            if (!TryReadStandaloneCSharpModifiers(trimmed, out var hasPartial, out var hasFile))
                break;

            hasPartialModifier |= hasPartial;
            hasFileModifier |= hasFile;
        }

        return new CSharpLeadingDeclarationEvidence(
            hasPartialModifier,
            hasFileModifier,
            hasAttribute,
            hasDocumentation);
    }

    private static bool TryReadClosedCSharpConditionalDeclarationEvidence(
        IReadOnlyList<string> lines,
        IReadOnlyList<CSharpLexState>? lineStartStates,
        int minimumLineIndex,
        int closingDirectiveLineIndex,
        out CSharpClosedConditionalDeclarationEvidence evidence)
    {
        evidence = default;
        var depth = 0;
        for (var lineIndex = closingDirectiveLineIndex;
             lineIndex >= minimumLineIndex;
             lineIndex--)
        {
            if (!TryReadCSharpDirectiveKeywordAtLine(
                    lines,
                    lineStartStates,
                    lineIndex,
                    out var directiveKeyword))
            {
                continue;
            }

            if (directiveKeyword == "endif")
            {
                depth++;
                continue;
            }

            if (directiveKeyword != "if")
                continue;

            depth--;
            if (depth != 0)
                continue;

            var branchEvidence = ReadCSharpConditionalBranchEvidence(
                lines,
                lineStartStates,
                lineIndex,
                closingDirectiveLineIndex);
            evidence = new CSharpClosedConditionalDeclarationEvidence(
                lineIndex,
                branchEvidence.HasCodeBoundary,
                branchEvidence.HasPartialModifier,
                branchEvidence.HasFileModifier,
                branchEvidence.HasAttribute);
            return true;
        }

        return false;
    }

    private static CSharpConditionalBranchEvidence ReadCSharpConditionalBranchEvidence(
        IReadOnlyList<string> lines,
        IReadOnlyList<CSharpLexState>? lineStartStates,
        int openingDirectiveLineIndex,
        int closingDirectiveLineIndex)
    {
        var branches = new List<CSharpConditionalBranchEvidence>();
        var current = new CSharpConditionalBranchEvidence();
        var hasExplicitElse = false;

        for (var lineIndex = openingDirectiveLineIndex + 1;
             lineIndex < closingDirectiveLineIndex;
             lineIndex++)
        {
            if (TryReadCSharpDirectiveKeywordAtLine(
                    lines,
                    lineStartStates,
                    lineIndex,
                    out var directiveKeyword))
            {
                if (directiveKeyword == "if")
                {
                    if (!TryFindMatchingCSharpEndifDirective(
                            lines,
                            lineStartStates,
                            lineIndex,
                            closingDirectiveLineIndex,
                            out var nestedClosingDirectiveLineIndex))
                    {
                        return current with { HasCodeBoundary = true };
                    }

                    var nested = ReadCSharpConditionalBranchEvidence(
                        lines,
                        lineStartStates,
                        lineIndex,
                        nestedClosingDirectiveLineIndex);
                    current = current with
                    {
                        HasCodeBoundary = current.HasCodeBoundary || nested.HasCodeBoundary,
                        HasPartialModifier = current.HasPartialModifier || nested.HasPartialModifier,
                        HasFileModifier = current.HasFileModifier || nested.HasFileModifier,
                        HasAttribute = current.HasAttribute || nested.HasAttribute,
                    };
                    lineIndex = nestedClosingDirectiveLineIndex;
                    continue;
                }

                if (directiveKeyword is "else" or "elif")
                {
                    branches.Add(CompleteCSharpConditionalBranchEvidence(current));
                    current = new CSharpConditionalBranchEvidence();
                    hasExplicitElse |= directiveKeyword == "else";
                }

                // Non-conditional directives are declaration trivia. Nested `#endif`
                // directives are consumed together with their matching `#if` above.
                // conditional 以外の directive は declaration trivia とする。nested
                // `#endif` は対応する `#if` と一緒に上で消費済みである。
                continue;
            }

            current = AccumulateCSharpConditionalBranchLineEvidence(
                lines,
                lineStartStates,
                lineIndex,
                current);
        }

        branches.Add(CompleteCSharpConditionalBranchEvidence(current));
        if (!hasExplicitElse)
            branches.Add(new CSharpConditionalBranchEvidence());

        return new CSharpConditionalBranchEvidence(
            HasCodeBoundary: branches.Any(branch => branch.HasCodeBoundary),
            HasPartialModifier: branches.All(branch => branch.HasPartialModifier),
            HasFileModifier: branches.Any(branch => branch.HasFileModifier),
            HasAttribute: branches.All(branch => branch.HasAttribute));
    }

    private static CSharpConditionalBranchEvidence CompleteCSharpConditionalBranchEvidence(
        CSharpConditionalBranchEvidence evidence)
        => evidence.AttributeDepth == 0
            ? evidence
            : evidence with { HasCodeBoundary = true };

    private static CSharpConditionalBranchEvidence AccumulateCSharpConditionalBranchLineEvidence(
        IReadOnlyList<string> lines,
        IReadOnlyList<CSharpLexState>? lineStartStates,
        int lineIndex,
        CSharpConditionalBranchEvidence evidence)
    {
        var lineStartState = lineStartStates != null && lineIndex < lineStartStates.Count
            ? lineStartStates[lineIndex]
            : new CSharpLexState();
        var trimmed = LexCSharpLine(lines[lineIndex], lineStartState).SanitizedLine.AsSpan().Trim();
        if (trimmed.IsEmpty)
            return evidence;

        if (evidence.AttributeDepth > 0 || trimmed[0] == '[')
        {
            var pendingAttributeIsGlobal = evidence.PendingAttributeIsGlobal
                || IsCSharpGlobalAttributeTarget(trimmed);
            var attributeDepth = Math.Max(
                0,
                evidence.AttributeDepth
                + CountCharacter(trimmed, '[')
                - CountCharacter(trimmed, ']'));
            var updated = evidence with
            {
                AttributeDepth = attributeDepth,
                PendingAttributeIsGlobal = pendingAttributeIsGlobal,
            };
            if (attributeDepth > 0)
                return updated;

            updated = updated with
            {
                HasAttribute = updated.HasAttribute || !pendingAttributeIsGlobal,
                PendingAttributeIsGlobal = false,
            };

            var lastAttributeClose = trimmed.LastIndexOf(']');
            var trailing = lastAttributeClose >= 0
                ? trimmed[(lastAttributeClose + 1)..].Trim()
                : ReadOnlySpan<char>.Empty;
            if (trailing.IsEmpty)
                return updated;
            if (!TryReadStandaloneCSharpModifiers(
                    trailing,
                    out var trailingHasPartial,
                    out var trailingHasFile))
            {
                return updated with { HasCodeBoundary = true };
            }

            return updated with
            {
                HasPartialModifier = updated.HasPartialModifier || trailingHasPartial,
                HasFileModifier = updated.HasFileModifier || trailingHasFile,
            };
        }

        if (!TryReadStandaloneCSharpModifiers(
                trimmed,
                out var hasPartial,
                out var hasFile))
        {
            return evidence with { HasCodeBoundary = true };
        }

        return evidence with
        {
            HasPartialModifier = evidence.HasPartialModifier || hasPartial,
            HasFileModifier = evidence.HasFileModifier || hasFile,
        };
    }

    private static bool TryFindMatchingCSharpEndifDirective(
        IReadOnlyList<string> lines,
        IReadOnlyList<CSharpLexState>? lineStartStates,
        int openingDirectiveLineIndex,
        int exclusiveMaximumLineIndex,
        out int closingDirectiveLineIndex)
    {
        var depth = 0;
        for (var lineIndex = openingDirectiveLineIndex;
             lineIndex < exclusiveMaximumLineIndex;
             lineIndex++)
        {
            if (!TryReadCSharpDirectiveKeywordAtLine(
                    lines,
                    lineStartStates,
                    lineIndex,
                    out var directiveKeyword))
            {
                continue;
            }

            if (directiveKeyword == "if")
            {
                depth++;
                continue;
            }

            if (directiveKeyword != "endif")
                continue;

            depth--;
            if (depth == 0)
            {
                closingDirectiveLineIndex = lineIndex;
                return true;
            }
        }

        closingDirectiveLineIndex = -1;
        return false;
    }

    private static bool TryReadCSharpDirectiveKeywordAtLine(
        IReadOnlyList<string> lines,
        IReadOnlyList<CSharpLexState>? lineStartStates,
        int lineIndex,
        out string directiveKeyword)
    {
        directiveKeyword = string.Empty;
        var lineStartState = lineStartStates != null && lineIndex < lineStartStates.Count
            ? lineStartStates[lineIndex]
            : new CSharpLexState();
        if (lineStartState.Mode != CSharpLexMode.Code
            || lineStartState.InterpolationBraceDepth != 0)
        {
            return false;
        }

        var trimmed = LexCSharpLine(lines[lineIndex], lineStartState).SanitizedLine.AsSpan().Trim();
        return TryReadCSharpDirectiveKeyword(trimmed, out directiveKeyword);
    }

    private static bool TryReadCSharpDirectiveKeyword(
        ReadOnlySpan<char> line,
        out string keyword)
    {
        keyword = string.Empty;
        if (line.IsEmpty || line[0] != '#')
            return false;

        var directive = line[1..].TrimStart();
        foreach (var candidate in new[] { "endif", "elif", "else", "if" })
        {
            if (!directive.StartsWith(candidate, StringComparison.Ordinal)
                || (directive.Length > candidate.Length
                    && IsCSharpIdentifierPart(directive[candidate.Length])))
            {
                continue;
            }

            keyword = candidate;
            return true;
        }

        // Other directives (`#nullable`, `#pragma`, `#line`, and so on) are also
        // declaration trivia, but do not change the conditional-branch depth.
        // `#nullable`、`#pragma`、`#line` などのほかの directive も declaration
        // trivia だが、条件分岐の深さは変更しない。
        return true;
    }

    private static bool HasCSharpDeclarationLineLeadingAttribute(
        IReadOnlyList<string> lines,
        SymbolRecord symbol,
        IReadOnlyList<CSharpLexState>? lineStartStates)
    {
        var declarationLine = symbol.Line > 0 ? symbol.Line : symbol.StartLine;
        if (declarationLine <= 0 || declarationLine > lines.Count)
            return false;

        var lineIndex = declarationLine - 1;
        var lineStartState = lineStartStates != null && lineIndex < lineStartStates.Count
            ? lineStartStates[lineIndex]
            : new CSharpLexState();
        if (lineStartState.Mode != CSharpLexMode.Code || lineStartState.InterpolationBraceDepth != 0)
            return false;

        var declarationStartColumn = FindCSharpDeclarationOccurrenceStartColumn(
            lines[lineIndex],
            symbol,
            lineStartState);
        if (declarationStartColumn <= 0)
            return false;

        var sanitizedLine = LexCSharpLine(lines[lineIndex], lineStartState).SanitizedLine.AsSpan();
        var cursor = Math.Min(declarationStartColumn, sanitizedLine.Length) - 1;
        while (cursor >= 0 && char.IsWhiteSpace(sanitizedLine[cursor]))
            cursor--;

        // An attribute belongs to this declaration only when its closing bracket is the
        // last code token before this declaration occurrence. Assembly/module targets are
        // compilation-unit metadata, not semantic evidence for the following declaration.
        // attribute の閉じ括弧がこの宣言 occurrence 直前の最後の code token である場合だけ、
        // この宣言の attribute とみなす。assembly/module target は compilation-unit の
        // metadata であり、後続宣言の semantic evidence ではない。
        if (cursor < 0 || sanitizedLine[cursor] != ']')
            return false;

        var attributeStart = FindCSharpAttributeStart(sanitizedLine, cursor);
        return attributeStart >= 0
               && !IsCSharpGlobalAttributeTarget(sanitizedLine[attributeStart..(cursor + 1)]);
    }

    private static int FindCSharpAttributeStart(ReadOnlySpan<char> line, int closingBracket)
    {
        var depth = 0;
        for (var cursor = closingBracket; cursor >= 0; cursor--)
        {
            if (line[cursor] == ']')
            {
                depth++;
                continue;
            }

            if (line[cursor] != '[')
                continue;

            depth--;
            if (depth == 0)
                return cursor;
        }

        return -1;
    }

    private static bool HasCSharpDeclarationLineLeadingDocumentation(
        IReadOnlyList<string> lines,
        SymbolRecord symbol,
        IReadOnlyList<CSharpLexState>? lineStartStates)
    {
        var declarationLine = symbol.Line > 0 ? symbol.Line : symbol.StartLine;
        if (declarationLine <= 0 || declarationLine > lines.Count)
            return false;

        var lineIndex = declarationLine - 1;
        var lineStartState = lineStartStates != null && lineIndex < lineStartStates.Count
            ? lineStartStates[lineIndex]
            : new CSharpLexState();
        if (lineStartState.Mode != CSharpLexMode.Code || lineStartState.InterpolationBraceDepth != 0)
            return false;

        var rawLine = lines[lineIndex];
        var declarationStartColumn = FindCSharpDeclarationOccurrenceStartColumn(
            rawLine,
            symbol,
            lineStartState);
        if (declarationStartColumn <= 0)
            return false;

        var cursor = Math.Min(declarationStartColumn, rawLine.Length) - 1;
        while (cursor >= 0 && char.IsWhiteSpace(rawLine[cursor]))
            cursor--;
        if (cursor < 1 || rawLine[cursor - 1] != '*' || rawLine[cursor] != '/')
            return false;

        var expectedCommentEnd = cursor - 1;
        var commentStart = rawLine.LastIndexOf("/**", expectedCommentEnd, StringComparison.Ordinal);
        while (commentStart >= 0)
        {
            // Re-lex the prefix so a `/**` sequence inside a normal block comment,
            // string, character literal, or interpolation hole cannot become documentation.
            // prefix を再 lex し、通常 block comment・string・character literal・
            // interpolation hole 内の `/**` を documentation と誤認しない。
            var stateAtCommentStart = LexCSharpLine(rawLine[..commentStart], lineStartState).EndState;
            if (stateAtCommentStart.Mode == CSharpLexMode.Code
                && stateAtCommentStart.InterpolationReturnMode == CSharpLexMode.Code
                && stateAtCommentStart.InterpolationBraceDepth == 0
                && rawLine.IndexOf("*/", commentStart + 2, StringComparison.Ordinal) == expectedCommentEnd)
            {
                return true;
            }

            commentStart = commentStart == 0
                ? -1
                : rawLine.LastIndexOf("/**", commentStart - 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static int FindCSharpDeclarationOccurrenceStartColumn(
        string rawLine,
        SymbolRecord symbol,
        CSharpLexState lineStartState)
    {
        if (!string.IsNullOrEmpty(symbol.Signature))
        {
            var signatureColumn = FindSignatureOccurrenceStartColumn(
                rawLine,
                symbol.Signature,
                symbol.SameLineSignatureOccurrenceIndex ?? 0,
                lineStartState);
            if (signatureColumn >= 0)
                return signatureColumn;
        }

        return symbol.StartColumn ?? -1;
    }

    private static bool TryReadStandaloneCSharpModifiers(
        ReadOnlySpan<char> line,
        out bool hasPartial,
        out bool hasFile)
    {
        hasPartial = false;
        hasFile = false;
        var remaining = line;
        var found = false;
        while (!remaining.IsEmpty)
        {
            var separator = remaining.IndexOfAny(' ', '\t');
            var token = separator < 0 ? remaining : remaining[..separator];
            if (!CSharpStandaloneDeclarationModifiers.Contains(token.ToString()))
                return false;

            found = true;
            hasPartial |= token.SequenceEqual("partial");
            hasFile |= token.SequenceEqual("file");
            if (separator < 0)
                break;
            remaining = remaining[(separator + 1)..].TrimStart();
        }
        return found;
    }

    private static int? FindCSharpDeclarationIdentifierColumn(
        IReadOnlyList<string> lines,
        SymbolRecord symbol,
        IReadOnlyList<CSharpLexState>? lineStartStates)
    {
        if (symbol.Line <= 0 || symbol.Line > lines.Count || string.IsNullOrWhiteSpace(symbol.Name))
            return null;

        var lineIndex = symbol.Line - 1;
        var lineStartState = lineStartStates != null && lineIndex < lineStartStates.Count
            ? lineStartStates[lineIndex]
            : new CSharpLexState();
        var line = LexCSharpLine(lines[lineIndex], lineStartState).SanitizedLine.AsSpan();
        var declarationOccurrenceStart = FindCSharpDeclarationOccurrenceStartColumn(
            lines[lineIndex],
            symbol,
            lineStartState);
        var declarationSearchStart = Math.Max(0, declarationOccurrenceStart);
        var name = symbol.Name.AsSpan().TrimStart('@');
        if (name.IsEmpty)
            return null;

        if (symbol.Kind == "class")
        {
            // Plain records use the existing class kind. Resolve their declaration
            // keyword before the class-kind lookup can fall through to a later
            // same-name occurrence in a base list.
            // plain record は既存の class kind を使うため、base list 内の同名参照へ
            // fallback する前に record declaration keyword から宣言名を解決する。
            var recordKeywordColumn = FindCSharpIdentifierToken(
                line,
                "record".AsSpan(),
                declarationSearchStart);
            if (recordKeywordColumn >= 0)
            {
                var recordNameColumn = FindCSharpIdentifierToken(
                    line,
                    name,
                    recordKeywordColumn + "record".Length);
                if (recordNameColumn >= 0)
                    return recordNameColumn;
            }
        }

        if (symbol.Kind is "class" or "struct" or "interface" or "record")
        {
            var keywordColumn = FindCSharpIdentifierToken(
                line,
                symbol.Kind.AsSpan(),
                declarationSearchStart);
            if (keywordColumn >= 0)
            {
                var nameColumn = FindCSharpIdentifierToken(
                    line,
                    name,
                    keywordColumn + symbol.Kind.Length);
                if (nameColumn >= 0)
                    return nameColumn;
            }
        }

        var searchStart = declarationSearchStart;
        int? fallback = null;
        while (searchStart < line.Length)
        {
            var nameColumn = FindCSharpIdentifierToken(line, name, searchStart);
            if (nameColumn < 0)
                break;

            fallback = nameColumn;
            if (symbol.Kind is "function" or "test.method"
                && IsOutsideCSharpAttributeList(line, nameColumn)
                && IsCSharpCallableNameOccurrence(line, nameColumn, name.Length))
                return nameColumn;
            searchStart = nameColumn + Math.Max(1, name.Length);
        }

        return fallback;
    }

    private static bool IsOutsideCSharpAttributeList(ReadOnlySpan<char> line, int column)
    {
        var depth = 0;
        for (var i = 0; i < column; i++)
        {
            if (line[i] == '[')
                depth++;
            else if (line[i] == ']' && depth > 0)
                depth--;
        }

        return depth == 0;
    }

    private static int FindCSharpIdentifierToken(
        ReadOnlySpan<char> line,
        ReadOnlySpan<char> token,
        int startIndex)
    {
        var searchIndex = Math.Clamp(startIndex, 0, line.Length);
        while (searchIndex <= line.Length - token.Length)
        {
            var relativeIndex = line[searchIndex..].IndexOf(token, StringComparison.Ordinal);
            if (relativeIndex < 0)
                return -1;

            var index = searchIndex + relativeIndex;
            var tokenStart = index > 0 && line[index - 1] == '@' ? index - 1 : index;
            var beforeIsIdentifier = tokenStart > 0 && IsCSharpIdentifierPart(line[tokenStart - 1]);
            var afterIndex = index + token.Length;
            var afterIsIdentifier = afterIndex < line.Length && IsCSharpIdentifierPart(line[afterIndex]);
            if (!beforeIsIdentifier && !afterIsIdentifier)
                return index;

            searchIndex = index + Math.Max(1, token.Length);
        }

        return -1;
    }

    private static bool IsCSharpCallableNameOccurrence(
        ReadOnlySpan<char> line,
        int nameColumn,
        int nameLength)
    {
        var cursor = nameColumn;
        if (cursor < line.Length && line[cursor] == '@')
            cursor++;
        cursor += nameLength;
        while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
            cursor++;

        if (cursor < line.Length && line[cursor] == '<')
        {
            var depth = 0;
            do
            {
                if (line[cursor] == '<')
                    depth++;
                else if (line[cursor] == '>')
                    depth--;
                cursor++;
            }
            while (cursor < line.Length && depth > 0);

            while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
                cursor++;
        }

        return cursor < line.Length && line[cursor] == '(';
    }

    private static int CountCharacter(ReadOnlySpan<char> text, char value)
    {
        var count = 0;
        foreach (var character in text)
        {
            if (character == value)
                count++;
        }
        return count;
    }

    private readonly record struct CSharpLeadingDeclarationEvidence(
        bool HasPartialModifier,
        bool HasFileModifier,
        bool HasAttribute,
        bool HasDocumentation);

    private readonly record struct CSharpClosedConditionalDeclarationEvidence(
        int OpeningDirectiveLineIndex,
        bool HasCodeBoundary,
        bool HasPartialModifier,
        bool HasFileModifier,
        bool HasAttribute);

    private readonly record struct CSharpConditionalBranchEvidence(
        bool HasCodeBoundary = false,
        bool HasPartialModifier = false,
        bool HasFileModifier = false,
        bool HasAttribute = false,
        bool PendingAttributeIsGlobal = false,
        int AttributeDepth = 0);
}
