using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class KotlinReferenceExtractor
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
    private static readonly string[] GenericOwnerKeywords = ["class", "interface", "typealias"];
    private static readonly HashSet<string> BuiltInInfixFunctionNames = new(StringComparer.Ordinal)
    {
        "and", "downTo", "or", "shl", "shr", "step", "to", "until", "ushr", "xor",
    };
    private static readonly HashSet<string> EmptyKotlinNameSet = new(StringComparer.Ordinal);
    private static readonly IReadOnlySet<string> EmptyGenericParameterNames = new HashSet<string>(StringComparer.Ordinal);

    public static (HashSet<string> ConstructorTypeNames, HashSet<string> InfixFunctionNames) BuildNameSets(
        string language,
        IReadOnlyList<SymbolRecord> symbols)
    {
        if (language != "kotlin")
            return (EmptyKotlinNameSet, EmptyKotlinNameSet);

        var constructorTypeNames = new HashSet<string>(StringComparer.Ordinal);
        var infixFunctionNames = new HashSet<string>(BuiltInInfixFunctionNames, StringComparer.Ordinal);

        var callableNames = new HashSet<string>(StringComparer.Ordinal);
        List<string>? constructableClassNames = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind == "function" && !string.IsNullOrWhiteSpace(symbol.Name))
            {
                callableNames.Add(symbol.Name);
                if (!string.IsNullOrWhiteSpace(symbol.Signature)
                    && InfixFunctionDeclarationRegex.IsMatch(symbol.Signature))
                {
                    infixFunctionNames.Add(symbol.Name);
                    var dotIndex = symbol.Name.LastIndexOf('.');
                    if (dotIndex >= 0 && dotIndex + 1 < symbol.Name.Length)
                        infixFunctionNames.Add(symbol.Name[(dotIndex + 1)..]);
                }

                continue;
            }

            if (symbol.Kind != "class"
                || string.IsNullOrWhiteSpace(symbol.Name)
                || !IsConstructableClassSymbol(symbol))
            {
                continue;
            }

            constructableClassNames ??= [];
            constructableClassNames.Add(symbol.Name);
        }

        if (constructableClassNames != null)
        {
            foreach (var name in constructableClassNames)
                if (!callableNames.Contains(name))
                    constructorTypeNames.Add(name);
        }

        return (constructorTypeNames, infixFunctionNames);
    }

    public static bool IsConstructorCallName(string name, IReadOnlySet<string> constructorTypeNames)
        => constructorTypeNames.Contains(name);

    public static void AddDeclaredInfixFunctionNames(IEnumerable<string> lines, HashSet<string> names)
    {
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

        var cursor = 0;
        var tokenStart = 0;
        var tokenLength = 0;
        while (TryReadNextKotlinSignatureToken(symbol.Signature, ref cursor, out tokenStart, out tokenLength)
               && IsKotlinConstructabilityModifier(symbol.Signature, tokenStart, tokenLength))
        {
        }

        if (tokenLength == 0)
            return true;

        if (KotlinSignatureTokenEquals(symbol.Signature, tokenStart, tokenLength, "annotation")
            && TryReadNextKotlinSignatureToken(symbol.Signature, ref cursor, out var nextTokenStart, out var nextTokenLength)
            && KotlinSignatureTokenEquals(symbol.Signature, nextTokenStart, nextTokenLength, "class"))
            return false;

        return !KotlinSignatureTokenEquals(symbol.Signature, tokenStart, tokenLength, "object")
               && !KotlinSignatureTokenEquals(symbol.Signature, tokenStart, tokenLength, "companion");
    }

    private static bool TryReadNextKotlinSignatureToken(string signature, ref int cursor, out int tokenStart, out int tokenLength)
    {
        while (cursor < signature.Length && (char.IsWhiteSpace(signature[cursor]) || signature[cursor] == '('))
            cursor++;

        var start = cursor;
        while (cursor < signature.Length && !char.IsWhiteSpace(signature[cursor]) && signature[cursor] != '(')
            cursor++;

        tokenStart = start;
        tokenLength = cursor - start;
        return tokenLength > 0;
    }

    private static bool IsKotlinConstructabilityModifier(string signature, int tokenStart, int tokenLength)
        => KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "public")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "private")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "protected")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "internal")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "expect")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "actual")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "abstract")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "sealed")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "data")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "open")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "final")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "value")
           || KotlinSignatureTokenEquals(signature, tokenStart, tokenLength, "inner");

    private static bool KotlinSignatureTokenEquals(string signature, int tokenStart, int tokenLength, string value)
        => tokenLength == value.Length
           && string.CompareOrdinal(signature, tokenStart, value, 0, value.Length) == 0;

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
        foreach (Match match in Regex.EnumerateMatches(
                     IdentifierRegex,
                     originalLine))
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var genericParameterNames = CollectGenericParameterNames(preparedLine);
        foreach (Match match in Regex.EnumerateMatches(
                     ClassLiteralRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

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
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (constructorTypeNames.Count == 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(
                     BacktickConstructorCallRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

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
        Func<IReadOnlyList<SymbolRecord>> getEnclosingTypeCandidates,
        IReadOnlyList<SymbolRecord> symbols,
        string[] structuralLines,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        using var matches = Regex
            .EnumerateMatches(CtorDelegationRegex, preparedLine)
            .GetEnumerator();
        if (!matches.MoveNext())
            return;

        var enclosingType = ReferenceExtractor.FindInnermostClassLike(getEnclosingTypeCandidates(), lineNumber);
        if (enclosingType == null)
            return;

        var ctorContainer = container;
        if (ctorContainer == null
            || ctorContainer.Kind != "function"
            || !string.Equals(ctorContainer.Name, enclosingType.Name, StringComparison.Ordinal))
        {
            ctorContainer = FindEnclosingKotlinConstructor(symbols, enclosingType, lineNumber) ?? ctorContainer;
        }

        do
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var match = matches.Current;
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
        while (matches.MoveNext());
    }

}
