using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class KotlinReferenceExtractor
{
    // Kotlin secondary constructor delegation: `constructor(x: Int) : this(x)` / `: super(x)`.
    // Kotlin セカンダリコンストラクタ委譲。
    private static readonly Regex CtorDelegationRegex = new(@":\s*(?<kind>this|super)\s*\(", RegexOptions.Compiled);

    // Kotlin class literals: `User::class` / `User::class.java`. The final segment must look
    // type-like so expression receivers such as `value::class` do not become type references.
    // Kotlin class literal。末尾セグメントを型名らしい形に絞り、`value::class` のような
    // 式レシーバーを type_reference 化しない。
    private static readonly Regex ClassLiteralRegex = new(
        @"(?<![\w$])(?<type>(?:(?:[_\p{L}][\w$]*|`[^`\r\n]+`)\.)*(?:[_\p{Lu}][\w$]*|`[^`\r\n]+`))\s*::\s*class\b",
        RegexOptions.Compiled);
    private static readonly Regex BacktickConstructorCallRegex = new(
        @"(?<![\w$])(?<name>`[^`\r\n]+`)(?:\s*<[^()\r\n]+>)?\s*\(",
        RegexOptions.Compiled);
    private static readonly Regex InfixFunctionDeclarationRegex = new(
        @"(?<![\w$])infix\s+fun\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex InfixFunctionNameRegex = new(
        @"(?<![\w$])infix\s+fun\s+(?:<[^()\r\n]+>\s*)?(?:(?:[_\p{L}][\w$]*|`[^`\r\n]+`)(?:\s*<[^>\r\n]+>)?\s*\.\s*)*(?<name>[_\p{L}][\w$]*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IdentifierRegex = new(
        @"(?<![\w$])(?<name>[_\p{L}][\w$]*)(?![\w$])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] DeclarationKeywords = ["val", "var"];
    private static readonly string[] TypeOperatorKeywords = ["is", "as"];
    private static readonly HashSet<string> BuiltInInfixFunctionNames = new(StringComparer.Ordinal)
    {
        "and", "downTo", "or", "shl", "shr", "step", "to", "until", "ushr", "xor",
    };

    public static HashSet<string> BuildConstructorTypeNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (language != "kotlin")
            return names;

        var callableNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in symbols)
        {
            if (symbol.Kind == "function" && !string.IsNullOrWhiteSpace(symbol.Name))
                callableNames.Add(symbol.Name);
        }

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "class" || string.IsNullOrWhiteSpace(symbol.Name))
                continue;
            if (!IsConstructableClassSymbol(symbol))
                continue;
            if (callableNames.Contains(symbol.Name))
                continue;

            names.Add(symbol.Name);
        }

        return names;
    }

    public static bool IsConstructorCallName(string name, IReadOnlySet<string> constructorTypeNames)
        => constructorTypeNames.Contains(name);

    public static HashSet<string> BuildInfixFunctionNames(string language, IReadOnlyList<SymbolRecord> symbols)
    {
        var names = new HashSet<string>(BuiltInInfixFunctionNames, StringComparer.Ordinal);
        if (language != "kotlin")
            return names;

        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function" || string.IsNullOrWhiteSpace(symbol.Name) || string.IsNullOrWhiteSpace(symbol.Signature))
                continue;

            if (InfixFunctionDeclarationRegex.IsMatch(symbol.Signature))
            {
                names.Add(symbol.Name);
                var dotIndex = symbol.Name.LastIndexOf('.');
                if (dotIndex >= 0 && dotIndex + 1 < symbol.Name.Length)
                    names.Add(symbol.Name[(dotIndex + 1)..]);
            }
        }

        return names;
    }

    public static void AddDeclaredInfixFunctionNames(string language, IEnumerable<string> lines, HashSet<string> names)
    {
        if (language != "kotlin")
            return;

        foreach (var line in lines)
        {
            var match = InfixFunctionNameRegex.Match(line);
            if (match.Success)
                names.Add(match.Groups["name"].Value);
        }
    }

    private static bool IsConstructableClassSymbol(SymbolRecord symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol.Signature))
            return false;

        var tokens = symbol.Signature.Split(
            [' ', '\t', '\r', '\n', '('],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var index = 0;
        while (index < tokens.Length && tokens[index] is "public" or "private" or "protected" or "internal" or "expect" or "actual" or "abstract" or "sealed" or "data" or "open" or "final" or "value" or "inner")
            index++;

        if (index >= tokens.Length)
            return true;

        if (tokens[index] == "annotation" && index + 1 < tokens.Length && tokens[index + 1] == "class")
            return false;

        return tokens[index] is not ("object" or "companion");
    }

    private static bool IsBacktickConstructorDeclarationSite(string line, int nameIndex)
    {
        var cursor = nameIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(line[cursor]))
            cursor--;
        if (cursor < 0)
            return false;

        var end = cursor + 1;
        while (cursor >= 0 && ReferenceExtractor.IsJavaIdentifierPart(line[cursor]))
            cursor--;
        var start = cursor + 1;
        if (start >= end)
            return false;

        return line.Substring(start, end - start) is "class" or "interface" or "object" or "fun" or "constructor";
    }

    private static string StripBacktickIdentifier(string name)
    {
        if (name.Length >= 2 && name[0] == '`' && name[^1] == '`')
            return name[1..^1];
        return name;
    }

    public static void EmitTrailingLambdaReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference)
        => TrailingLambdaReferenceExtractor.EmitReferences(preparedLine, addCallLikeReference);

    public static void EmitInfixCallReferences(
        string preparedLine,
        string originalLine,
        IReadOnlySet<string> infixFunctionNames,
        Action<string, int> addCallLikeReference)
    {
        foreach (Match match in IdentifierRegex.Matches(originalLine))
        {
            var nameGroup = match.Groups["name"];
            var name = nameGroup.Value;
            if (!infixFunctionNames.Contains(name))
                continue;
            if (!IsUnmaskedSpan(preparedLine, nameGroup.Index, nameGroup.Length))
                continue;
            if (IsLikelyDeclarationOrImport(preparedLine, match.Index))
                continue;
            if (!HasLikelyInfixOperandBefore(originalLine, nameGroup.Index)
                || !HasLikelyInfixOperandAfter(originalLine, nameGroup.Index + nameGroup.Length))
            {
                continue;
            }

            addCallLikeReference(name, nameGroup.Index);
        }
    }

    public static bool IsInfixFunctionDeclarationSite(string preparedLine, int nameIndex)
    {
        var prefix = preparedLine[..Math.Max(0, nameIndex)];
        return InfixFunctionDeclarationRegex.IsMatch(prefix);
    }

    private static bool IsUnmaskedSpan(string preparedLine, int start, int length)
    {
        if (start < 0 || length <= 0 || start + length > preparedLine.Length)
            return false;

        for (var i = 0; i < length; i++)
        {
            if (char.IsWhiteSpace(preparedLine[start + i]))
                return false;
        }

        return true;
    }

    private static bool IsLikelyDeclarationOrImport(string preparedLine, int expressionIndex)
    {
        var prefix = preparedLine[..Math.Max(0, expressionIndex)].TrimStart();
        if (InfixFunctionDeclarationRegex.IsMatch(prefix))
            return true;

        return prefix.StartsWith("import ", StringComparison.Ordinal)
               || prefix.StartsWith("package ", StringComparison.Ordinal)
               || prefix.StartsWith("class ", StringComparison.Ordinal)
               || prefix.StartsWith("interface ", StringComparison.Ordinal)
               || prefix.StartsWith("object ", StringComparison.Ordinal)
               || prefix.StartsWith("fun ", StringComparison.Ordinal)
               || prefix.StartsWith("infix fun ", StringComparison.Ordinal)
               || (prefix.StartsWith("val ", StringComparison.Ordinal) && !prefix.Contains('='))
               || (prefix.StartsWith("var ", StringComparison.Ordinal) && !prefix.Contains('='));
    }

    private static bool HasLikelyInfixOperandBefore(string preparedLine, int nameIndex)
    {
        var cursor = nameIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(preparedLine[cursor]))
            cursor--;
        if (cursor < 0 || preparedLine[cursor] is '=' or ',' or '(' or '[' or '{' or ':' or ';')
            return false;

        if (ReferenceExtractor.IsJavaIdentifierPart(preparedLine[cursor]))
        {
            var end = cursor + 1;
            while (cursor >= 0 && ReferenceExtractor.IsJavaIdentifierPart(preparedLine[cursor]))
                cursor--;
            var previousWord = preparedLine.Substring(cursor + 1, end - cursor - 1);
            if (previousWord is "return" or "throw" or "if" or "while" or "when" or "for" or "val" or "var" or "fun" or "class")
                return false;
        }

        return true;
    }

    private static bool HasLikelyInfixOperandAfter(string preparedLine, int afterNameIndex)
    {
        var cursor = afterNameIndex;
        while (cursor < preparedLine.Length && char.IsWhiteSpace(preparedLine[cursor]))
            cursor++;
        return cursor < preparedLine.Length && preparedLine[cursor] is not (',' or ')' or ']' or '}' or ':' or ';');
    }

    public static void EmitMethodReferenceReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
        => JvmMethodReferenceExtractor.EmitMethodReferenceReferences(
            "kotlin",
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

    public static void EmitClassLiteralReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var genericParameterNames = CollectGenericParameterNames(preparedLine);
        foreach (Match match in ClassLiteralRegex.Matches(preparedLine))
        {
            var typeGroup = match.Groups["type"];
            ReferenceExtractor.AddTypeExpressionSegments(
                references,
                seen,
                fileId,
                typeGroup.Value,
                typeGroup.Index,
                context,
                lineNumber,
                container,
                "kotlin",
                genericParameterNames);
        }
    }

    public static void EmitBacktickConstructorReferences(
        string preparedLine,
        IReadOnlySet<string> constructorTypeNames,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (constructorTypeNames.Count == 0)
            return;

        foreach (Match match in BacktickConstructorCallRegex.Matches(preparedLine))
        {
            var nameGroup = match.Groups["name"];
            if (IsBacktickConstructorDeclarationSite(preparedLine, nameGroup.Index))
                continue;

            var name = StripBacktickIdentifier(nameGroup.Value);
            if (!constructorTypeNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index,
                "instantiate",
                context,
                lineNumber,
                resolveContainerForColumn(nameGroup.Index));
        }
    }

    public static void EmitCtorDelegationReferences(
        string preparedLine,
        IReadOnlyList<SymbolRecord> enclosingTypeCandidates,
        IReadOnlyList<SymbolRecord> symbols,
        string[] structuralLines,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var matches = CtorDelegationRegex.Matches(preparedLine);
        if (matches.Count == 0)
            return;

        var enclosingType = ReferenceExtractor.FindInnermostClassLike(enclosingTypeCandidates, lineNumber);
        if (enclosingType == null)
            return;

        var ctorContainer = container;
        if (ctorContainer == null
            || ctorContainer.Kind != "function"
            || !string.Equals(ctorContainer.Name, enclosingType.Name, StringComparison.Ordinal))
        {
            ctorContainer = FindEnclosingKotlinConstructor(symbols, enclosingType, lineNumber) ?? ctorContainer;
        }

        foreach (Match match in matches)
        {
            var kindToken = match.Groups["kind"].Value;
            string? target;
            if (kindToken == "this")
            {
                target = enclosingType.Name;
            }
            else
            {
                // Secondary constructors call `super(...)` from the constructor line, while the
                // superclass type lives on the enclosing class header. Reconstruct the header so
                // multi-line signatures can resolve the same way C# and Java constructor chains do.
                // `super(...)` の呼び先は外側クラスヘッダ上の superclass なので、
                // 複数行ヘッダも拾えるよう structuralLines から再構築する。
                var (_, _, headerText) = ReferenceExtractor.CollectCSharpRecordHeader(
                    structuralLines,
                    enclosingType.StartLine);
                target = ParseKotlinBaseType(headerText);
                if (string.IsNullOrWhiteSpace(target))
                    target = ParseKotlinBaseType(enclosingType.Signature);
                if (string.IsNullOrWhiteSpace(target))
                    continue;
            }

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                target!,
                match.Groups["kind"].Index,
                "call",
                context,
                lineNumber,
                ctorContainer);
        }
    }

    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var genericParameterNames = CollectGenericParameterNames(preparedLine);
        EmitCallableSignatureTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        EmitPrimaryConstructorTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        EmitHeritageTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        EmitGenericBoundReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        EmitExtensionPropertyReceiverTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn, genericParameterNames);
        TypedLanguageReferenceExtractor.EmitColonVariableTypeReferences(
            preparedLine,
            DeclarationKeywords,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            genericParameterNames);
        if (!preparedLine.TrimStart().StartsWith("import ", StringComparison.Ordinal))
        {
            TypedLanguageReferenceExtractor.EmitKeywordFollowingTypeReferences(
                preparedLine,
                TypeOperatorKeywords,
                "kotlin",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                genericParameterNames);
        }
    }

    private static void EmitCallableSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        var funIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "fun");
        if (funIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', funIndex + "fun".Length);
        if (openParen <= funIndex)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        EmitExtensionFunctionReceiverTypeReferences(
            preparedLine,
            funIndex,
            openParen,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            ignoredSegments);

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            ignoredSegments);

        var returnColon = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, closeParen + 1);
        if (returnColon >= preparedLine.Length || preparedLine[returnColon] != ':')
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, returnColon + 1);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart),
            ignoredSegments);
    }

    private static void EmitExtensionFunctionReceiverTypeReferences(
        string preparedLine,
        int funIndex,
        int openParen,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        var headStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, funIndex + "fun".Length);
        if (headStart >= openParen)
            return;

        if (preparedLine[headStart] == '<')
        {
            var genericClose = ReferenceExtractor.FindMatchingChar(preparedLine, headStart, '<', '>');
            if (genericClose < 0 || genericClose >= openParen)
                return;
            headStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, genericClose + 1);
            if (headStart >= openParen)
                return;
        }

        var receiverDot = FindLastTopLevelChar(preparedLine, '.', headStart, openParen);
        if (receiverDot <= headStart)
            return;

        var receiverEnd = receiverDot;
        while (receiverEnd > headStart && char.IsWhiteSpace(preparedLine[receiverEnd - 1]))
            receiverEnd--;
        if (receiverEnd <= headStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(headStart, receiverEnd - headStart),
            headStart,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(headStart),
            ignoredSegments);
    }

    private static void EmitExtensionPropertyReceiverTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        foreach (var keyword in DeclarationKeywords)
        {
            foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, keyword))
            {
                var declarationStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, keywordIndex + keyword.Length);
                if (declarationStart >= preparedLine.Length)
                    continue;

                var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', declarationStart);
                var assignmentIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', declarationStart);
                var declarationEnd = preparedLine.Length;
                if (colonIndex >= 0)
                    declarationEnd = Math.Min(declarationEnd, colonIndex);
                if (assignmentIndex >= 0)
                    declarationEnd = Math.Min(declarationEnd, assignmentIndex);
                if (declarationEnd <= declarationStart)
                    continue;

                var receiverDot = FindLastTopLevelChar(preparedLine, '.', declarationStart, declarationEnd);
                if (receiverDot <= declarationStart)
                    continue;

                var receiverEnd = receiverDot;
                while (receiverEnd > declarationStart && char.IsWhiteSpace(preparedLine[receiverEnd - 1]))
                    receiverEnd--;
                if (receiverEnd <= declarationStart)
                    continue;

                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    preparedLine.Substring(declarationStart, receiverEnd - declarationStart),
                    declarationStart,
                    "kotlin",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(declarationStart),
                    ignoredSegments);
            }
        }
    }

    private static int FindLastTopLevelChar(string text, char target, int startIndex, int endIndex)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        var braceDepth = 0;
        var last = -1;
        var end = Math.Min(text.Length, endIndex);
        for (var i = Math.Max(0, startIndex); i < end; i++)
        {
            var ch = text[i];
            if (angleDepth == 0 && parenDepth == 0 && squareDepth == 0 && braceDepth == 0 && ch == target)
                last = i;

            switch (ch)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0) angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0) squareDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0) braceDepth--;
                    break;
            }
        }

        return last;
    }

    private static void EmitHeritageTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        var trimmed = preparedLine.TrimStart();
        if (!(trimmed.StartsWith("class ", StringComparison.Ordinal)
              || trimmed.StartsWith("data class ", StringComparison.Ordinal)
              || trimmed.StartsWith("sealed class ", StringComparison.Ordinal)
              || trimmed.StartsWith("interface ", StringComparison.Ordinal)
              || trimmed.StartsWith("object ", StringComparison.Ordinal)
              || trimmed.StartsWith("enum class ", StringComparison.Ordinal)))
        {
            return;
        }

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':');
        if (colonIndex < 0)
            return;

        var listStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        var listEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, listStart, stopAtComma: false);
        if (listEnd <= listStart)
            return;

        TypedLanguageReferenceExtractor.EmitCommaSeparatedTypeListReferences(
            preparedLine,
            listStart,
            listEnd,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            trimTopLevelCallArguments: true,
            ignoredSegments: ignoredSegments);
    }

    private static void EmitPrimaryConstructorTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? ignoredSegments)
    {
        var trimmed = preparedLine.TrimStart();
        if (!(trimmed.StartsWith("class ", StringComparison.Ordinal)
              || trimmed.StartsWith("data class ", StringComparison.Ordinal)
              || trimmed.StartsWith("sealed class ", StringComparison.Ordinal)
              || trimmed.StartsWith("enum class ", StringComparison.Ordinal)))
        {
            return;
        }

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(');
        if (openParen < 0)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            ignoredSegments);
    }

    private static void EmitGenericBoundReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        IReadOnlySet<string>? genericParameterNames)
    {
        var genericOpenIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '<');
        if (genericOpenIndex >= 0)
        {
            TypedLanguageReferenceExtractor.EmitGenericColonBoundReferences(
                preparedLine,
                genericOpenIndex,
                "kotlin",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn,
                genericParameterNames);
        }

        TypedLanguageReferenceExtractor.EmitWhereClauseTypeReferences(
            preparedLine,
            "kotlin",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            genericParameterNames);
    }

    private static HashSet<string> CollectGenericParameterNames(string preparedLine)
    {
        foreach (var funIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "fun"))
        {
            var genericOpenIndex = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, funIndex + "fun".Length);
            if (genericOpenIndex < preparedLine.Length && preparedLine[genericOpenIndex] == '<')
                return CollectGenericParameterNamesFromClause(preparedLine, genericOpenIndex);
        }

        foreach (var keyword in new[] { "class", "interface", "typealias" })
        {
            foreach (var keywordIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, keyword))
            {
                var nameStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, keywordIndex + keyword.Length);
                var nameEnd = ConsumeKotlinDeclarationName(preparedLine, nameStart);
                if (nameEnd <= nameStart)
                    continue;

                var genericOpenIndex = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, nameEnd);
                if (genericOpenIndex < preparedLine.Length && preparedLine[genericOpenIndex] == '<')
                    return CollectGenericParameterNamesFromClause(preparedLine, genericOpenIndex);
            }
        }

        return [];
    }

    private static HashSet<string> CollectGenericParameterNamesFromClause(string preparedLine, int genericOpenIndex)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var genericCloseIndex = ReferenceExtractor.FindMatchingChar(preparedLine, genericOpenIndex, '<', '>');
        if (genericCloseIndex <= genericOpenIndex)
            return names;

        var clause = preparedLine.Substring(genericOpenIndex + 1, genericCloseIndex - genericOpenIndex - 1);
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
        {
            var fragment = clause.Substring(segmentStart, segmentLength);
            if (TryReadGenericParameterName(fragment, out var name))
                names.Add(name);
        }

        return names;
    }

    private static int ConsumeKotlinDeclarationName(string preparedLine, int startIndex)
    {
        if (startIndex >= preparedLine.Length)
            return startIndex;

        if (preparedLine[startIndex] == '`')
        {
            var close = preparedLine.IndexOf('`', startIndex + 1);
            return close < 0 ? startIndex : close + 1;
        }

        var index = startIndex;
        while (index < preparedLine.Length && ReferenceExtractor.IsJavaIdentifierPart(preparedLine[index]))
            index++;

        return index;
    }

    private static bool TryReadGenericParameterName(string fragment, out string name)
    {
        name = string.Empty;
        var index = 0;
        while (index < fragment.Length)
        {
            while (index < fragment.Length && char.IsWhiteSpace(fragment[index]))
                index++;

            if (index >= fragment.Length)
                return false;

            if (fragment[index] == '@')
            {
                index = ReferenceExtractor.SkipJavaAnnotation(fragment, index);
                continue;
            }

            var tokenStart = index;
            if (!ReferenceExtractor.IsJavaIdentifierPart(fragment[index]))
                return false;

            index++;
            while (index < fragment.Length && ReferenceExtractor.IsJavaIdentifierPart(fragment[index]))
                index++;

            var token = fragment.Substring(tokenStart, index - tokenStart);
            if (token is "reified" or "in" or "out")
                continue;

            name = token;
            return true;
        }

        return false;
    }

    private static SymbolRecord? FindEnclosingKotlinConstructor(
        IReadOnlyList<SymbolRecord> symbols,
        SymbolRecord enclosingType,
        int lineNumber)
    {
        SymbolRecord? best = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind != "function")
                continue;
            if (!string.Equals(symbol.ContainerName, enclosingType.Name, StringComparison.Ordinal)
                && !IsWithinSymbolRange(enclosingType, symbol.StartLine))
            {
                continue;
            }

            var signature = symbol.Signature?.TrimStart();
            var isSecondaryConstructor = !string.IsNullOrWhiteSpace(signature)
                && (signature.StartsWith("constructor", StringComparison.Ordinal)
                    || signature.StartsWith("public constructor", StringComparison.Ordinal)
                    || signature.StartsWith("private constructor", StringComparison.Ordinal)
                    || signature.StartsWith("protected constructor", StringComparison.Ordinal)
                    || signature.StartsWith("internal constructor", StringComparison.Ordinal));
            if (!isSecondaryConstructor
                && !string.Equals(symbol.Name, enclosingType.Name, StringComparison.Ordinal))
            {
                continue;
            }

            if (symbol.StartLine > lineNumber)
                continue;
            var symbolEnd = symbol.BodyEndLine ?? symbol.EndLine;
            if (symbolEnd < lineNumber)
                continue;

            if (best == null || symbol.StartLine >= best.StartLine)
                best = symbol;
        }

        return best;
    }

    private static bool IsWithinSymbolRange(SymbolRecord container, int lineNumber)
    {
        var start = container.BodyStartLine ?? container.StartLine;
        var end = container.BodyEndLine ?? container.EndLine;
        return lineNumber >= start && lineNumber <= end;
    }

    private static string? ParseKotlinBaseType(string? signature)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return null;

        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(signature, ':');
        if (colonIndex < 0)
            return null;

        var listStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(signature, colonIndex + 1);
        var listEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(
            signature,
            listStart,
            stopAtComma: false);
        if (listEnd <= listStart)
            return null;

        var typeList = signature.Substring(listStart, listEnd - listStart);
        string? fallback = null;
        foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(typeList))
        {
            var segment = typeList.Substring(segmentStart, segmentLength).Trim();
            if (segment.Length == 0)
                continue;

            var typeName = ExtractKotlinBareTypeName(segment);
            if (string.IsNullOrWhiteSpace(typeName))
                continue;

            if (TypedLanguageReferenceExtractor.FindTopLevelChar(segment, '(') >= 0)
                return typeName;

            fallback ??= typeName;
        }

        return fallback;
    }

    private static string? ExtractKotlinBareTypeName(string segment)
    {
        var trimmed = TrimTopLevelByClause(segment.Trim());
        if (trimmed.Length == 0)
            return null;

        var callIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(trimmed, '(');
        if (callIndex > 0)
            trimmed = trimmed.Substring(0, callIndex).TrimEnd();

        var lastSegmentStart = 0;
        var endIndex = trimmed.Length;
        var angleDepth = 0;
        for (var i = 0; i < trimmed.Length; i++)
        {
            var ch = trimmed[i];
            if (ch == '<')
            {
                if (angleDepth == 0)
                    endIndex = Math.Min(endIndex, i);
                angleDepth++;
            }
            else if (ch == '>')
            {
                if (angleDepth > 0)
                    angleDepth--;
            }
            else if (angleDepth == 0 && ch == '.')
            {
                lastSegmentStart = i + 1;
            }
        }

        if (endIndex < lastSegmentStart)
            endIndex = trimmed.Length;

        var typeName = trimmed.Substring(lastSegmentStart, endIndex - lastSegmentStart).Trim();
        return typeName.Length > 0 ? typeName : null;
    }

    private static string TrimTopLevelByClause(string segment)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        for (var i = 0; i < segment.Length; i++)
        {
            var ch = segment[i];
            if (ch == '<')
            {
                angleDepth++;
            }
            else if (ch == '>')
            {
                if (angleDepth > 0)
                    angleDepth--;
            }
            else if (ch == '(')
            {
                parenDepth++;
            }
            else if (ch == ')')
            {
                if (parenDepth > 0)
                    parenDepth--;
            }
            else if (angleDepth == 0
                     && parenDepth == 0
                     && i + 4 <= segment.Length
                     && string.CompareOrdinal(segment, i, " by ", 0, 4) == 0)
            {
                return segment.Substring(0, i).TrimEnd();
            }
        }

        return segment;
    }
}
