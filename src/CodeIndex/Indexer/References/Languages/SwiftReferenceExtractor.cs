using CodeIndex.Models;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class SwiftReferenceExtractor
{
    internal readonly record struct LineRange(int StartLine, int EndLine);
    internal readonly record struct TypeAliasBinding(
        string Alias,
        string Target,
        int BindingLine,
        int? EndLine,
        int BraceDepth,
        IReadOnlyList<LineRange> ShadowRanges,
        IReadOnlySet<string> TypeParameters);

    private static readonly string[] DeclarationKeywords = ["let", "var"];
    private static readonly string[] TypeOperatorKeywords = ["is", "as"];
    private static readonly Regex PropertyWrapperDeclarationRegex = new(
        @"^\s*(?<attributes>(?:@[A-Z]\w*(?:\.[A-Z]\w*)?(?:\([^)]*\))?\s+)*)?(?:(?:public|private|internal|open|fileprivate|package)(?:\s*\(\s*set\s*\))?\s+)?(?:(?:lazy|weak|unowned|final|static|class|nonisolated)\s+)*(?:let|var)\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex PropertyWrapperAttributeRegex = new(
        @"@(?<name>[A-Z]\w*(?:\.[A-Z]\w*)?)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TypeAliasRegex = new(
        @"^\s*(?:(?:public|private|internal|open|fileprivate|package)\s+)?typealias\s+(?<alias>`[^`]+`|\w+)(?<params>\s*<[^=]+>)?\s*=\s*(?<target>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TypeDeclarationShadowRegex = new(
        @"^\s*(?:(?:public|private|internal|open|fileprivate|package)\s+)?(?:final\s+)?(?:class|struct|enum|protocol)\s+(?<name>`[^`]+`|\w+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> NonWrapperPropertyAttributes = new(StringComparer.Ordinal)
    {
        "IBOutlet",
        "IBOutletCollection",
        "IBInspectable",
        "NSManaged",
        "GKInspectable",
    };

    public static void EmitTrailingClosureReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference)
        => TrailingLambdaReferenceExtractor.EmitReferences(preparedLine, addCallLikeReference);

    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        Func<int, SymbolRecord?>? resolvePropertyWrapperContainerForColumn = null)
    {
        EmitCallableSignatureTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitClosureSignatureTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitHeritageTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitExtensionTargetReference(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGenericBoundReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitTypealiasRhsTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitAssociatedTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitKeyPathRootTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitMacroGenericArgumentReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGenericInvocationArgumentReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitGenericStaticMemberTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitCatchPatternTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitCollectionShorthandConstructorTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitSelfMetatypeExpressionReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitPropertyWrapperTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolvePropertyWrapperContainerForColumn ?? resolveContainerForColumn);
        EmitCompilerDirectiveRootTypeReferences("selector", preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitCompilerDirectiveRootTypeReferences("keyPath", preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        EmitAttributeGenericArgumentReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
        TypedLanguageReferenceExtractor.EmitColonVariableTypeReferences(
            preparedLine,
            DeclarationKeywords,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        TypedLanguageReferenceExtractor.EmitKeywordFollowingTypeReferences(
            preparedLine,
            TypeOperatorKeywords,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }

    public static IReadOnlyList<TypeAliasBinding> BuildTypeAliasTargets(IReadOnlyList<string> preparedLines)
    {
        var aliases = new List<TypeAliasBinding>();
        var braceDepths = BuildBraceDepthsBeforeLine(preparedLines);
        for (var index = 0; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            var match = TypeAliasRegex.Match(line);
            if (!match.Success)
                continue;

            var target = match.Groups["target"].Value.Trim();
            if (target.Length > 0)
                aliases.Add(new TypeAliasBinding(
                    TrimSwiftBackticks(match.Groups["alias"].Value),
                    target,
                    index + 1,
                    FindScopedAliasEndLine(preparedLines, braceDepths, index),
                    braceDepths[index],
                    BuildTypeAliasShadowRanges(preparedLines, braceDepths, TrimSwiftBackticks(match.Groups["alias"].Value)),
                    ExtractGenericTypeParameters(match.Groups["params"].Value)));
        }

        return aliases;
    }

    public static void EmitAliasTargetReferences(
        string preparedLine,
        IReadOnlyList<TypeAliasBinding> aliases,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (aliases.Count == 0 || TypeAliasRegex.IsMatch(preparedLine))
            return;

        foreach (var alias in aliases.Select(binding => binding.Alias).Distinct(StringComparer.Ordinal))
        {
            var searchStart = 0;
            while (searchStart < preparedLine.Length)
            {
                var index = preparedLine.IndexOf(alias, searchStart, StringComparison.Ordinal);
                if (index < 0)
                    break;

                searchStart = index + alias.Length;
                if (!HasIdentifierBoundaries(preparedLine, index, alias.Length))
                    continue;
                var column = index + 1;
                if (!references.Any(reference =>
                        reference.FileId == fileId
                        && reference.Line == lineNumber
                        && reference.Column == column
                        && reference.ReferenceKind == "type_reference"
                        && string.Equals(reference.SymbolName, alias, StringComparison.Ordinal)))
                {
                    continue;
                }

                var binding = FindActiveTypeAliasBinding(aliases, alias, lineNumber);
                if (binding is null)
                    continue;

                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    binding.Value.Target,
                    index,
                    "swift",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(index),
                    binding.Value.TypeParameters);
            }
        }
    }

    private static TypeAliasBinding? FindActiveTypeAliasBinding(
        IReadOnlyList<TypeAliasBinding> aliases,
        string alias,
        int lineNumber)
    {
        TypeAliasBinding? best = null;
        foreach (var binding in aliases)
        {
            if (!string.Equals(binding.Alias, alias, StringComparison.Ordinal)
                || lineNumber <= binding.BindingLine
                || (binding.EndLine is int endLine && lineNumber > endLine)
                || IsInsideScopedShadow(binding.ShadowRanges, lineNumber))
            {
                continue;
            }

            if (best is null
                || binding.BraceDepth > best.Value.BraceDepth
                || (binding.BraceDepth == best.Value.BraceDepth && binding.BindingLine > best.Value.BindingLine))
            {
                best = binding;
            }
        }

        return best;
    }

    private static IReadOnlyList<LineRange> BuildTypeAliasShadowRanges(
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<int> braceDepths,
        string alias)
    {
        var ranges = new List<LineRange>();
        for (var index = 0; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            var typeDeclaration = TypeDeclarationShadowRegex.Match(line);
            if (typeDeclaration.Success && string.Equals(TrimSwiftBackticks(typeDeclaration.Groups["name"].Value), alias, StringComparison.Ordinal))
            {
                ranges.Add(new LineRange(index + 1, FindScopedAliasEndLine(preparedLines, braceDepths, index) ?? preparedLines.Count));
                continue;
            }

            if (DeclaresGenericTypeParameter(line, alias))
                ranges.Add(new LineRange(index + 1, FindScopedAliasEndLine(preparedLines, braceDepths, index) ?? index + 1));
        }

        return ranges;
    }

    private static bool DeclaresGenericTypeParameter(string line, string alias)
    {
        var openAngle = line.IndexOf('<');
        if (openAngle < 0)
            return false;

        var closeAngle = line.IndexOf('>', openAngle + 1);
        if (closeAngle <= openAngle)
            return false;

        var prefix = line[..openAngle];
        if (!ContainsKeyword(prefix, "class")
            && !ContainsKeyword(prefix, "struct")
            && !ContainsKeyword(prefix, "enum")
            && !ContainsKeyword(prefix, "protocol")
            && !ContainsKeyword(prefix, "func")
            && !ContainsKeyword(prefix, "typealias"))
        {
            return false;
        }

        foreach (var parameter in line.Substring(openAngle + 1, closeAngle - openAngle - 1).Split(','))
        {
            var name = parameter.Trim().Split([' ', ':', '='], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (string.Equals(TrimSwiftBackticks(name ?? string.Empty), alias, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlySet<string> ExtractGenericTypeParameters(string parameters)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var openAngle = parameters.IndexOf('<');
        var closeAngle = parameters.LastIndexOf('>');
        if (openAngle < 0 || closeAngle <= openAngle)
            return names;

        foreach (var parameter in parameters.Substring(openAngle + 1, closeAngle - openAngle - 1).Split(','))
        {
            var name = parameter.Trim().Split([' ', ':', '='], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(TrimSwiftBackticks(name));
        }

        return names;
    }

    private static bool ContainsKeyword(string text, string keyword)
    {
        var searchStart = 0;
        while (searchStart < text.Length)
        {
            var index = text.IndexOf(keyword, searchStart, StringComparison.Ordinal);
            if (index < 0)
                return false;

            if (HasIdentifierBoundaries(text, index, keyword.Length))
                return true;

            searchStart = index + keyword.Length;
        }

        return false;
    }

    private static bool IsInsideScopedShadow(IReadOnlyList<LineRange> ranges, int lineNumber)
    {
        foreach (var range in ranges)
        {
            if (lineNumber >= range.StartLine && lineNumber <= range.EndLine)
                return true;
        }

        return false;
    }

    private static int? FindScopedAliasEndLine(
        IReadOnlyList<string> preparedLines,
        IReadOnlyList<int> braceDepths,
        int bindingLineIndex)
    {
        var bindingDepth = braceDepths[bindingLineIndex];
        if (bindingDepth <= 0)
            return null;

        for (var index = bindingLineIndex + 1; index < preparedLines.Count; index++)
        {
            if (braceDepths[index] < bindingDepth)
                return index;
        }

        return preparedLines.Count;
    }

    private static int[] BuildBraceDepthsBeforeLine(IReadOnlyList<string> preparedLines)
    {
        var depths = new int[preparedLines.Count];
        var depth = 0;
        for (var index = 0; index < preparedLines.Count; index++)
        {
            depths[index] = depth;
            foreach (var ch in preparedLines[index])
            {
                if (ch == '{')
                    depth++;
                else if (ch == '}' && depth > 0)
                    depth--;
            }
        }

        return depths;
    }

    private static string TrimSwiftBackticks(string value) =>
        value.Length >= 2 && value[0] == '`' && value[^1] == '`'
            ? value[1..^1]
            : value;

    private static bool HasIdentifierBoundaries(string line, int start, int length)
    {
        var before = start == 0 ? '\0' : line[start - 1];
        var afterIndex = start + length;
        var after = afterIndex >= line.Length ? '\0' : line[afterIndex];
        return !IsIdentifierPart(before) && !IsIdentifierPart(after);
    }

    private static bool IsIdentifierPart(char c) =>
        c == '_' || char.IsLetterOrDigit(c);

    private static void EmitPropertyWrapperTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var declarationMatch = PropertyWrapperDeclarationRegex.Match(preparedLine);
        if (!declarationMatch.Success)
            return;

        var attributes = declarationMatch.Groups["attributes"];
        if (!attributes.Success || attributes.Length == 0)
            return;

        foreach (Match attributeMatch in PropertyWrapperAttributeRegex.Matches(attributes.Value))
        {
            var nameGroup = attributeMatch.Groups["name"];
            if (!nameGroup.Success)
                continue;

            var name = nameGroup.Value;
            var shortNameStart = name.LastIndexOf('.') + 1;
            var emittedName = shortNameStart > 0 ? name[shortNameStart..] : name;
            if (NonWrapperPropertyAttributes.Contains(emittedName))
                continue;

            var column = attributes.Index + nameGroup.Index + shortNameStart;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                emittedName,
                column,
                "type_reference",
                context,
                lineNumber,
                resolveContainerForColumn(column));
        }
    }

    private static void EmitKeyPathRootTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (int slashIndex = 0; slashIndex < preparedLine.Length; slashIndex++)
        {
            if (preparedLine[slashIndex] != '\\')
                continue;

            var rootStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, slashIndex + 1);
            if (rootStart >= preparedLine.Length || preparedLine[rootStart] == '.')
                continue;

            var rootEnd = FindSwiftKeyPathRootEnd(preparedLine, rootStart);
            if (rootEnd <= rootStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(rootStart, rootEnd - rootStart),
                rootStart,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(rootStart));
            slashIndex = rootEnd;
        }
    }

    private static void EmitAttributeGenericArgumentReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (var atIndex = 0; atIndex < preparedLine.Length; atIndex++)
        {
            if (preparedLine[atIndex] != '@')
                continue;

            var nameStart = atIndex + 1;
            if (nameStart >= preparedLine.Length || !IsSwiftIdentifierStart(preparedLine[nameStart]))
                continue;

            var nameEnd = ReadSwiftQualifiedIdentifierEnd(preparedLine, nameStart);
            var openAngle = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, nameEnd);
            if (openAngle >= preparedLine.Length || preparedLine[openAngle] != '<')
                continue;

            var closeAngle = ReferenceExtractor.FindMatchingChar(preparedLine, openAngle, '<', '>');
            if (closeAngle < 0)
                continue;

            TypedLanguageReferenceExtractor.EmitCommaSeparatedTypeListReferences(
                preparedLine,
                openAngle + 1,
                closeAngle,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            atIndex = closeAngle;
        }
    }

    private static int ReadSwiftQualifiedIdentifierEnd(string preparedLine, int start)
    {
        var index = start;
        while (index < preparedLine.Length)
        {
            if (IsSwiftIdentifierPart(preparedLine[index]))
            {
                index++;
                continue;
            }

            if (preparedLine[index] == '.'
                && index + 1 < preparedLine.Length
                && IsSwiftIdentifierStart(preparedLine[index + 1]))
            {
                index += 2;
                while (index < preparedLine.Length && IsSwiftIdentifierPart(preparedLine[index]))
                    index++;
                continue;
            }

            break;
        }

        return index;
    }

    private static void EmitCompilerDirectiveRootTypeReferences(
        string directiveName,
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var marker = "#" + directiveName;
        for (var markerIndex = 0; markerIndex < preparedLine.Length; markerIndex++)
        {
            if (!preparedLine.AsSpan(markerIndex).StartsWith(marker, StringComparison.Ordinal))
                continue;

            var openParen = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, markerIndex + marker.Length);
            if (openParen >= preparedLine.Length || preparedLine[openParen] != '(')
                continue;

            var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
            if (closeParen < 0)
                continue;

            var rootStart = SkipSwiftDirectiveArgumentLabel(preparedLine, openParen + 1, closeParen);
            if (rootStart >= closeParen || !LooksLikeSwiftTypeExpressionStart(preparedLine[rootStart]))
            {
                markerIndex = closeParen;
                continue;
            }

            var rootEnd = Math.Min(FindSwiftKeyPathRootEnd(preparedLine, rootStart), closeParen);
            if (rootEnd <= rootStart)
            {
                markerIndex = closeParen;
                continue;
            }

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(rootStart, rootEnd - rootStart),
                rootStart,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(rootStart));
            markerIndex = closeParen;
        }
    }

    private static int SkipSwiftDirectiveArgumentLabel(string preparedLine, int argumentStart, int argumentEnd)
    {
        var rootStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, argumentStart);
        foreach (var label in SwiftDirectiveArgumentLabels)
        {
            if (!StartsWithSwiftWord(preparedLine, rootStart, label))
                continue;

            var colonIndex = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, rootStart + label.Length);
            if (colonIndex < argumentEnd && preparedLine[colonIndex] == ':')
                return TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
        }

        return rootStart;
    }

    private static readonly string[] SwiftDirectiveArgumentLabels = ["getter", "setter"];

    private static void EmitSelfMetatypeExpressionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (var dotIndex = 0; dotIndex + ".self".Length <= preparedLine.Length; dotIndex++)
        {
            if (preparedLine[dotIndex] != '.'
                || !StartsWithSwiftWord(preparedLine, dotIndex + 1, "self"))
            {
                continue;
            }

            var rootStart = FindSwiftMetatypeRootStart(preparedLine, dotIndex);
            while (rootStart >= 0 && rootStart < dotIndex && char.IsWhiteSpace(preparedLine[rootStart]))
                rootStart++;
            if (rootStart < 0 || !LooksLikeSwiftTypeExpressionStart(preparedLine[rootStart]))
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(rootStart, dotIndex - rootStart),
                rootStart,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(rootStart));
            dotIndex += ".self".Length - 1;
        }
    }

    private static int FindSwiftMetatypeRootStart(string preparedLine, int rootEnd)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;

        for (var index = rootEnd - 1; index >= 0; index--)
        {
            var ch = preparedLine[index];
            switch (ch)
            {
                case '>':
                    angleDepth++;
                    continue;
                case '<':
                    if (angleDepth > 0)
                    {
                        angleDepth--;
                        continue;
                    }

                    break;
                case ')':
                    parenDepth++;
                    continue;
                case '(':
                    if (parenDepth > 0)
                    {
                        parenDepth--;
                        continue;
                    }

                    break;
                case ']':
                    squareDepth++;
                    continue;
                case '[':
                    if (squareDepth > 0)
                    {
                        squareDepth--;
                        continue;
                    }

                    break;
            }

            if (angleDepth > 0 || parenDepth > 0 || squareDepth > 0)
                continue;

            if (IsSwiftIdentifierPart(ch) || ch == '.' || char.IsWhiteSpace(ch))
                continue;

            return index + 1;
        }

        return 0;
    }

    private static bool LooksLikeSwiftTypeExpressionStart(char ch)
        => char.IsUpper(ch) || ch == '[' || ch == '(';

    private static void EmitCollectionShorthandConstructorTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (var openBracket = 0; openBracket < preparedLine.Length; openBracket++)
        {
            if (preparedLine[openBracket] != '[' || IsSwiftSubscriptLikeOpenBracket(preparedLine, openBracket))
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, openBracket + 1);
            if (typeStart >= preparedLine.Length || !IsSwiftIdentifierStart(preparedLine[typeStart]))
                continue;

            var closeBracket = ReferenceExtractor.FindMatchingChar(preparedLine, openBracket, '[', ']');
            if (closeBracket < 0 || closeBracket <= typeStart)
                continue;

            var afterBracket = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, closeBracket + 1);
            if (afterBracket >= preparedLine.Length || preparedLine[afterBracket] != '(')
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, closeBracket - typeStart),
                typeStart,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
            openBracket = closeBracket;
        }
    }

    private static bool IsSwiftSubscriptLikeOpenBracket(string preparedLine, int openBracket)
    {
        var previous = openBracket - 1;
        while (previous >= 0 && char.IsWhiteSpace(preparedLine[previous]))
            previous--;

        return previous >= 0 && (IsSwiftIdentifierPart(preparedLine[previous]) || preparedLine[previous] == ']');
    }

    private static void EmitCatchPatternTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var catchIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "catch"))
        {
            var patternStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, catchIndex + "catch".Length);
            if (patternStart >= preparedLine.Length
                || preparedLine[patternStart] == '{'
                || StartsWithSwiftWord(preparedLine, patternStart, "let")
                || StartsWithSwiftWord(preparedLine, patternStart, "var"))
            {
                continue;
            }

            if (StartsWithSwiftWord(preparedLine, patternStart, "is"))
            {
                patternStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, patternStart + "is".Length);
                if (patternStart >= preparedLine.Length || preparedLine[patternStart] == '{')
                    continue;
            }

            var typeEnd = FindSwiftCatchPatternTypeEnd(preparedLine, patternStart);
            if (typeEnd <= patternStart)
                continue;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(patternStart, typeEnd - patternStart),
                patternStart,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(patternStart));
        }
    }

    private static int FindSwiftCatchPatternTypeEnd(string preparedLine, int patternStart)
    {
        for (var index = patternStart; index < preparedLine.Length; index++)
        {
            var ch = preparedLine[index];
            if (ch == '.'
                || ch == '{'
                || ch == ','
                || ch == '('
                || StartsWithSwiftWord(preparedLine, index, "where"))
            {
                return index;
            }
        }

        return preparedLine.Length;
    }

    private static void EmitGenericInvocationArgumentReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (var index = 0; index < preparedLine.Length; index++)
        {
            if (!IsSwiftIdentifierStart(preparedLine[index]))
                continue;

            var nameStart = index;
            index++;
            while (index < preparedLine.Length && IsSwiftIdentifierPart(preparedLine[index]))
                index++;

            if (HasSwiftDeclarationKeywordBefore(preparedLine, nameStart)
                || index >= preparedLine.Length
                || preparedLine[index] != '<')
            {
                index--;
                continue;
            }

            var closeAngle = ReferenceExtractor.FindMatchingChar(preparedLine, index, '<', '>');
            if (closeAngle < 0)
            {
                index--;
                continue;
            }

            var afterGeneric = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, closeAngle + 1);
            if (afterGeneric >= preparedLine.Length || preparedLine[afterGeneric] is not ('(' or '{'))
            {
                index = closeAngle;
                continue;
            }

            TypedLanguageReferenceExtractor.EmitCommaSeparatedTypeListReferences(
                preparedLine,
                index + 1,
                closeAngle,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            index = closeAngle;
        }
    }

    private static void EmitGenericStaticMemberTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (var index = 0; index < preparedLine.Length; index++)
        {
            if (!LooksLikeSwiftTypeExpressionStart(preparedLine[index]))
                continue;

            var nameStart = index;
            index++;
            while (index < preparedLine.Length && IsSwiftIdentifierPart(preparedLine[index]))
                index++;

            if (HasSwiftDeclarationKeywordBefore(preparedLine, nameStart)
                || index >= preparedLine.Length
                || preparedLine[index] != '<')
            {
                index--;
                continue;
            }

            var closeAngle = ReferenceExtractor.FindMatchingChar(preparedLine, index, '<', '>');
            if (closeAngle < 0)
            {
                index--;
                continue;
            }

            var afterGeneric = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, closeAngle + 1);
            if (afterGeneric >= preparedLine.Length || preparedLine[afterGeneric] != '.')
            {
                index = closeAngle;
                continue;
            }

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(nameStart, closeAngle - nameStart + 1),
                nameStart,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(nameStart));
            index = closeAngle;
        }
    }

    private static bool HasSwiftDeclarationKeywordBefore(string preparedLine, int nameStart)
    {
        var previous = nameStart - 1;
        while (previous >= 0 && char.IsWhiteSpace(preparedLine[previous]))
            previous--;
        if (previous < 0)
            return false;

        var wordEnd = previous + 1;
        while (previous >= 0 && IsSwiftIdentifierPart(preparedLine[previous]))
            previous--;
        var wordStart = previous + 1;
        if (wordStart >= wordEnd)
            return false;

        var word = preparedLine[wordStart..wordEnd];
        return word is "associatedtype" or "class" or "enum" or "extension" or "func" or "macro"
            or "protocol" or "struct" or "typealias";
    }

    private static void EmitMacroGenericArgumentReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        for (int hashIndex = 0; hashIndex < preparedLine.Length; hashIndex++)
        {
            if (preparedLine[hashIndex] != '#')
                continue;

            var nameStart = hashIndex + 1;
            if (nameStart >= preparedLine.Length || !IsSwiftIdentifierStart(preparedLine[nameStart]))
                continue;

            var nameEnd = nameStart + 1;
            while (nameEnd < preparedLine.Length && IsSwiftIdentifierPart(preparedLine[nameEnd]))
                nameEnd++;

            var openAngle = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, nameEnd);
            if (openAngle >= preparedLine.Length || preparedLine[openAngle] != '<')
                continue;

            var closeAngle = ReferenceExtractor.FindMatchingChar(preparedLine, openAngle, '<', '>');
            if (closeAngle < 0)
                continue;

            TypedLanguageReferenceExtractor.EmitCommaSeparatedTypeListReferences(
                preparedLine,
                openAngle + 1,
                closeAngle,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
            hashIndex = closeAngle;
        }
    }

    private static bool IsSwiftIdentifierStart(char ch)
        => ch == '_' || char.IsLetter(ch);

    private static bool IsSwiftIdentifierPart(char ch)
        => ch == '_' || char.IsLetterOrDigit(ch);

    private static bool StartsWithSwiftWord(string text, int index, string word)
    {
        if (index < 0 || index + word.Length > text.Length)
            return false;
        if (!string.Equals(text.Substring(index, word.Length), word, StringComparison.Ordinal))
            return false;

        var beforeOk = index == 0 || !IsSwiftIdentifierPart(text[index - 1]);
        var after = index + word.Length;
        var afterOk = after >= text.Length || !IsSwiftIdentifierPart(text[after]);
        return beforeOk && afterOk;
    }

    private static int FindSwiftKeyPathRootEnd(string preparedLine, int rootStart)
    {
        var angleDepth = 0;
        var parenDepth = 0;
        var squareDepth = 0;
        for (int index = rootStart; index < preparedLine.Length; index++)
        {
            var ch = preparedLine[index];
            switch (ch)
            {
                case '<':
                    angleDepth++;
                    break;
                case '>':
                    if (angleDepth > 0)
                        angleDepth--;
                    break;
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    else
                        return index;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    if (squareDepth > 0)
                        squareDepth--;
                    else
                        return index;
                    break;
                case '.':
                    if (angleDepth == 0
                        && parenDepth == 0
                        && squareDepth == 0
                        && index + 1 < preparedLine.Length
                        && (char.IsLower(preparedLine[index + 1]) || preparedLine[index + 1] == '_'))
                    {
                        return index;
                    }

                    break;
                case ',':
                case ';':
                case '{':
                case '}':
                    if (angleDepth == 0 && parenDepth == 0 && squareDepth == 0)
                        return index;
                    break;
            }
        }

        return preparedLine.Length;
    }

    private static void EmitExtensionTargetReference(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var extensionIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "extension");
        if (extensionIndex < 0)
            return;

        var targetStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, extensionIndex + "extension".Length);
        var targetEnd = FindSwiftExtensionTargetEnd(preparedLine, targetStart);
        if (targetEnd <= targetStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(targetStart, targetEnd - targetStart),
            targetStart,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(targetStart));
    }

    private static int FindSwiftExtensionTargetEnd(string preparedLine, int targetStart)
    {
        var expressionEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, targetStart, stopAtComma: false);
        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', targetStart);
        if (colonIndex >= 0 && colonIndex < expressionEnd)
            return colonIndex;

        return expressionEnd;
    }

    private static void EmitGenericBoundReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var genericOpenIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '<');
        if (genericOpenIndex >= 0)
        {
            TypedLanguageReferenceExtractor.EmitGenericColonBoundReferences(
                preparedLine,
                genericOpenIndex,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn);
        }

        TypedLanguageReferenceExtractor.EmitWhereClauseTypeReferences(
            preparedLine,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
        EmitWhereClauseSameTypeReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);
    }

    private static void EmitCallableSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var funcIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "func");
        if (funcIndex < 0)
            return;

        var openParen = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '(', funcIndex + "func".Length);
        if (openParen <= funcIndex)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        EmitTypedThrowsReferences(preparedLine, closeParen + 1, references, seen, fileId, context, lineNumber, resolveContainerForColumn);

        var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(preparedLine, "->", closeParen + 1);
        if (arrowIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, arrowIndex + 2);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static void EmitClosureSignatureTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var braceIndex = preparedLine.IndexOf('{', StringComparison.Ordinal);
        if (braceIndex < 0)
            return;

        var openParen = preparedLine.IndexOf('(', braceIndex + 1);
        if (openParen < 0)
            return;

        var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
        if (closeParen < 0)
            return;

        var inIndex = FindSwiftClosureInKeyword(preparedLine, closeParen + 1);
        if (inIndex < 0)
            return;

        TypedLanguageReferenceExtractor.EmitColonParameterTypeReferences(
            preparedLine,
            openParen + 1,
            closeParen,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);

        var arrowIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(preparedLine, "->", closeParen + 1);
        if (arrowIndex < 0 || arrowIndex >= inIndex)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, arrowIndex + 2);
        if (typeStart >= inIndex)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, inIndex - typeStart),
            typeStart,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static int FindSwiftClosureInKeyword(string preparedLine, int startIndex)
    {
        foreach (var inIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "in", startIndex))
            return inIndex;

        return -1;
    }

    private static void EmitWhereClauseSameTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var whereIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "where"))
        {
            var clauseStart = whereIndex + "where".Length;
            var clauseEnd = FindSwiftWhereClauseEnd(preparedLine, clauseStart);

            var clause = preparedLine.Substring(clauseStart, clauseEnd - clauseStart);
            foreach (var (segmentStart, segmentLength) in ReferenceExtractor.SplitTopLevelCommaSpans(clause))
            {
                var fragment = clause.Substring(segmentStart, segmentLength);
                var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelSequence(fragment, "==");
                if (equalsIndex < 0)
                    continue;

                var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(fragment, equalsIndex + 2);
                var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(fragment, typeStart, stopAtComma: false, stopAtArrow: false);
                if (typeEnd <= typeStart)
                    continue;

                var absoluteStart = clauseStart + segmentStart + typeStart;
                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    fragment.Substring(typeStart, typeEnd - typeStart),
                    absoluteStart,
                    "swift",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(absoluteStart));
            }
        }
    }

    private static int FindSwiftWhereClauseEnd(string preparedLine, int clauseStart)
    {
        var clauseEnd = preparedLine.Length;
        var braceIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '{', clauseStart);
        if (braceIndex >= 0)
            clauseEnd = Math.Min(clauseEnd, braceIndex);

        var semicolonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ';', clauseStart);
        if (semicolonIndex >= 0)
            clauseEnd = Math.Min(clauseEnd, semicolonIndex);

        return Math.Max(clauseStart, clauseEnd);
    }

    private static void EmitTypealiasRhsTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var typealiasIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "typealias");
        if (typealiasIndex < 0)
            return;

        var equalsIndex = FindTopLevelAssignmentEquals(preparedLine, typealiasIndex + "typealias".Length);
        if (equalsIndex < 0)
            return;

        var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, equalsIndex + 1);
        var typeEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, typeStart, stopAtComma: false, stopAtArrow: false);
        if (typeEnd <= typeStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(typeStart, typeEnd - typeStart),
            typeStart,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(typeStart));
    }

    private static void EmitAssociatedTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var associatedTypeIndex = ReferenceExtractor.FindTopLevelKeyword(preparedLine, "associatedtype");
        if (associatedTypeIndex < 0)
            return;

        var declarationStart = associatedTypeIndex + "associatedtype".Length;
        var equalsIndex = FindTopLevelAssignmentEquals(preparedLine, declarationStart);
        var colonIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, ':', declarationStart);

        if (colonIndex >= 0 && (equalsIndex < 0 || colonIndex < equalsIndex))
        {
            var constraintStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, colonIndex + 1);
            var constraintEnd = equalsIndex >= 0
                ? equalsIndex
                : TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, constraintStart, stopAtComma: false);
            if (constraintEnd > constraintStart)
            {
                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    preparedLine.Substring(constraintStart, constraintEnd - constraintStart),
                    constraintStart,
                    "swift",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    resolveContainerForColumn(constraintStart));
            }
        }

        if (equalsIndex < 0)
            return;

        var defaultStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, equalsIndex + 1);
        var defaultEnd = TypedLanguageReferenceExtractor.FindTypeExpressionEnd(preparedLine, defaultStart, stopAtComma: false, stopAtArrow: false);
        if (defaultEnd <= defaultStart)
            return;

        TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
            preparedLine.Substring(defaultStart, defaultEnd - defaultStart),
            defaultStart,
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn(defaultStart));
    }

    private static int FindTopLevelAssignmentEquals(string preparedLine, int startIndex)
    {
        var searchStart = startIndex;
        while (searchStart < preparedLine.Length)
        {
            var equalsIndex = TypedLanguageReferenceExtractor.FindTopLevelChar(preparedLine, '=', searchStart);
            if (equalsIndex < 0)
                return -1;

            var previous = equalsIndex > 0 ? preparedLine[equalsIndex - 1] : '\0';
            var next = equalsIndex + 1 < preparedLine.Length ? preparedLine[equalsIndex + 1] : '\0';
            if (previous is not ('=' or '!' or '<' or '>') && next != '=')
                return equalsIndex;

            searchStart = equalsIndex + 1;
        }

        return -1;
    }

    private static void EmitTypedThrowsReferences(
        string preparedLine,
        int searchStart,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (var throwsIndex in TypedLanguageReferenceExtractor.EnumerateTopLevelKeywordIndices(preparedLine, "throws", searchStart))
        {
            var openParen = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, throwsIndex + "throws".Length);
            if (openParen >= preparedLine.Length || preparedLine[openParen] != '(')
                continue;

            var closeParen = ReferenceExtractor.FindMatchingChar(preparedLine, openParen, '(', ')');
            if (closeParen < 0)
                continue;

            var typeStart = TypedLanguageReferenceExtractor.SkipTypePrefixTrivia(preparedLine, openParen + 1);
            if (typeStart >= closeParen)
                return;

            TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                preparedLine.Substring(typeStart, closeParen - typeStart),
                typeStart,
                "swift",
                references,
                seen,
                fileId,
                context,
                lineNumber,
                resolveContainerForColumn(typeStart));
            return;
        }
    }

    private static void EmitHeritageTypeReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        var trimmed = preparedLine.TrimStart();
        if (!(trimmed.StartsWith("class ", StringComparison.Ordinal)
              || trimmed.StartsWith("struct ", StringComparison.Ordinal)
              || trimmed.StartsWith("protocol ", StringComparison.Ordinal)
              || trimmed.StartsWith("enum ", StringComparison.Ordinal)
              || trimmed.StartsWith("extension ", StringComparison.Ordinal)))
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
            "swift",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn);
    }
}
