using CodeIndex.Models;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static partial class SwiftReferenceExtractor
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
    private static readonly IReadOnlySet<string> EmptyTypeParameters = new HashSet<string>(StringComparer.Ordinal);
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
        ReferenceDedupeSet seen,
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
        List<TypeAliasBinding>? aliases = null;
        int[]? braceDepths = null;
        for (var index = 0; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            if (line.IndexOf("typealias", StringComparison.Ordinal) < 0
                || line.IndexOf('=') < 0)
            {
                continue;
            }

            var match = TypeAliasRegex.Match(line);
            if (!match.Success)
                continue;

            var target = match.Groups["target"].Value.Trim();
            if (target.Length > 0)
            {
                var sharedBraceDepths = braceDepths ??= BuildBraceDepthsBeforeLine(preparedLines);
                var alias = TrimSwiftBackticks(match.Groups["alias"].Value);
                (aliases ??= []).Add(new TypeAliasBinding(
                    alias,
                    target,
                    index + 1,
                    FindScopedAliasEndLine(preparedLines, sharedBraceDepths, index),
                    sharedBraceDepths[index],
                    BuildTypeAliasShadowRanges(preparedLines, sharedBraceDepths, alias),
                    ExtractGenericTypeParameters(match.Groups["params"].Value)));
            }
        }

        return aliases is null ? Array.Empty<TypeAliasBinding>() : aliases;
    }

    public static void EmitAliasTargetReferences(
        string preparedLine,
        IReadOnlyList<TypeAliasBinding> aliases,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        if (aliases.Count == 0
            || (preparedLine.IndexOf("typealias", StringComparison.Ordinal) >= 0
                && preparedLine.IndexOf('=') >= 0
                && TypeAliasRegex.IsMatch(preparedLine)))
        {
            return;
        }

        HashSet<string>? emittedAliases = null;
        foreach (var bindingCandidate in aliases)
        {
            var alias = bindingCandidate.Alias;
            var index = preparedLine.IndexOf(alias, StringComparison.Ordinal);
            if (index < 0)
                continue;

            if (aliases.Count > 1
                && !(emittedAliases ??= new HashSet<string>(StringComparer.Ordinal)).Add(alias))
            {
                continue;
            }

            while (true)
            {
                if (!HasIdentifierBoundaries(preparedLine, index, alias.Length))
                {
                    if (!TryAdvanceToNextAliasOccurrence(preparedLine, alias, ref index))
                        break;
                    continue;
                }
                var binding = FindActiveTypeAliasBinding(aliases, alias, lineNumber);
                if (binding is null)
                {
                    if (!TryAdvanceToNextAliasOccurrence(preparedLine, alias, ref index))
                        break;
                    continue;
                }

                var column = index + 1;
                var container = resolveContainerForColumn(index);
                if (!seen.Contains(ReferenceExtractor.CreateReferenceDedupeKey(
                        fileId,
                        "swift",
                        lineNumber,
                        column,
                        "type_reference",
                        alias,
                        container)))
                {
                    if (!TryAdvanceToNextAliasOccurrence(preparedLine, alias, ref index))
                        break;
                    continue;
                }

                TypedLanguageReferenceExtractor.EmitTypeExpressionReferences(
                    binding.Value.Target,
                    index,
                    "swift",
                    references,
                    seen,
                    fileId,
                    context,
                    lineNumber,
                    container,
                    binding.Value.TypeParameters);

                if (!TryAdvanceToNextAliasOccurrence(preparedLine, alias, ref index))
                    break;
            }
        }
    }

    private static bool TryAdvanceToNextAliasOccurrence(string preparedLine, string alias, ref int index)
    {
        index = preparedLine.IndexOf(alias, index + alias.Length, StringComparison.Ordinal);
        return index >= 0;
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
        List<LineRange>? ranges = null;
        for (var index = 0; index < preparedLines.Count; index++)
        {
            var line = preparedLines[index];
            if (!line.Contains(alias, StringComparison.Ordinal))
                continue;

            var typeDeclaration = TypeDeclarationShadowRegex.Match(line);
            if (typeDeclaration.Success && string.Equals(TrimSwiftBackticks(typeDeclaration.Groups["name"].Value), alias, StringComparison.Ordinal))
            {
                (ranges ??= []).Add(new LineRange(index + 1, FindScopedAliasEndLine(preparedLines, braceDepths, index) ?? preparedLines.Count));
                continue;
            }

            if (DeclaresGenericTypeParameter(line, alias))
                (ranges ??= []).Add(new LineRange(index + 1, FindScopedAliasEndLine(preparedLines, braceDepths, index) ?? index + 1));
        }

        return ranges is null ? Array.Empty<LineRange>() : ranges;
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

        var parameters = line.AsSpan(openAngle + 1, closeAngle - openAngle - 1);
        var start = 0;
        while (TryReadSwiftGenericParameterName(parameters, ref start, out var name))
        {
            if (name.Equals(alias.AsSpan(), StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IReadOnlySet<string> ExtractGenericTypeParameters(string parameters)
    {
        var openAngle = parameters.IndexOf('<');
        var closeAngle = parameters.LastIndexOf('>');
        if (openAngle < 0 || closeAngle <= openAngle)
            return EmptyTypeParameters;

        HashSet<string>? names = null;
        var genericParameters = parameters.AsSpan(openAngle + 1, closeAngle - openAngle - 1);
        var start = 0;
        while (TryReadSwiftGenericParameterName(genericParameters, ref start, out var name))
        {
            (names ??= new HashSet<string>(StringComparer.Ordinal)).Add(name.ToString());
        }

        return names ?? EmptyTypeParameters;
    }

    private static bool TryReadSwiftGenericParameterName(
        ReadOnlySpan<char> parameters,
        ref int start,
        out ReadOnlySpan<char> name)
    {
        while (start <= parameters.Length)
        {
            var remaining = parameters[start..];
            var commaIndex = remaining.IndexOf(',');
            ReadOnlySpan<char> parameter;
            if (commaIndex < 0)
            {
                parameter = remaining;
                start = parameters.Length + 1;
            }
            else
            {
                parameter = remaining[..commaIndex];
                start += commaIndex + 1;
            }

            parameter = parameter.Trim();
            if (parameter.IsEmpty)
                continue;

            var delimiterIndex = parameter.IndexOfAny(' ', ':', '=');
            name = delimiterIndex < 0 ? parameter : parameter[..delimiterIndex].TrimEnd();
            if (name.Length >= 2 && name[0] == '`' && name[^1] == '`')
                name = name[1..^1];
            if (!name.IsEmpty)
                return true;
        }

        name = default;
        return false;
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
        ReferenceDedupeSet seen,
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

        foreach (Match attributeMatch in Regex.EnumerateMatches(
                     PropertyWrapperAttributeRegex,
                     attributes.Value))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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
        ReferenceDedupeSet seen,
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

}
