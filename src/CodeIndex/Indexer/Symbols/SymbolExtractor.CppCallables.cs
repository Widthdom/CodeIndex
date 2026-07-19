using System.Text;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private const int CppCallableScanLineLimit = 64;
    private const int CppCallableScanCharacterLimit = 32 * 1024;

    private static readonly HashSet<string> CppCallablePrefixModifiers = new(StringComparer.Ordinal)
    {
        "consteval",
        "constexpr",
        "explicit",
        "export",
        "extern",
        "friend",
        "inline",
        "static",
        "virtual",
    };

    private static readonly HashSet<string> CppNonCallableNames = new(StringComparer.Ordinal)
    {
        "alignof",
        "bool",
        "catch",
        "char",
        "decltype",
        "double",
        "float",
        "for",
        "if",
        "int",
        "long",
        "noexcept",
        "requires",
        "return",
        "short",
        "signed",
        "sizeof",
        "static_assert",
        "switch",
        "typedef",
        "unsigned",
        "void",
        "while",
    };

    private static readonly HashSet<string> CppInvalidReturnTypePrefixes = new(StringComparer.Ordinal)
    {
        "alignof",
        "catch",
        "for",
        "if",
        "noexcept",
        "requires",
        "return",
        "sizeof",
        "static_assert",
        "switch",
        "typedef",
        "while",
    };

    private static readonly HashSet<string> CppNamedOperatorOverloads = new(StringComparer.Ordinal)
    {
        "operator co_await",
        "operator delete",
        "operator delete[]",
        "operator new",
        "operator new[]",
    };

    private readonly record struct CppCallablePrefix(
        string Name,
        string? ReturnType,
        int NameOffset,
        string? QualifiedContainerName,
        string? QualifiedContainerKind);

    private readonly record struct CppCallableCandidate(
        string Name,
        string? ReturnType,
        string Signature,
        int NameLine,
        int StartLine,
        int StartColumn,
        int EndLine,
        int? BodyStartLine,
        int? BodyEndLine,
        string? ContainerName,
        string? ContainerKind);

    private sealed record CppCallableScanBuffer(
        string Raw,
        string Structural,
        int[] LineStarts,
        int StartLineIndex);

    private sealed record CppCallableTypeIndex(
        IReadOnlyDictionary<string, SymbolRecord> ByName,
        SymbolRecord?[] ByLine);

    private readonly record struct CppCallableBodyRange(int StartLine, int EndLine);

    private static void ExtractCppBalancedCallableSymbols(
        long fileId,
        string[] lines,
        string[] structuralLines,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        var typeIndex = BuildCppCallableTypeIndex(symbols, lines.Length);
        var callableBodyRanges = BuildCppCallableBodyRanges(symbols);
        var bodyRangeIndex = 0;

        for (var startLineIndex = 0; startLineIndex < lines.Length; startLineIndex++)
        {
            var lineNumber = startLineIndex + 1;
            while (bodyRangeIndex < callableBodyRanges.Count
                && callableBodyRanges[bodyRangeIndex].EndLine < lineNumber)
            {
                bodyRangeIndex++;
            }

            var structuralLine = structuralLines[startLineIndex].AsSpan().Trim();
            if (structuralLine.IsEmpty
                || structuralLine[0] is '#' or '}'
                || IsCppAccessLabel(structuralLine)
                || IsCppStandaloneMacroInvocation(structuralLine)
                || (bodyRangeIndex < callableBodyRanges.Count
                    && callableBodyRanges[bodyRangeIndex].StartLine <= lineNumber))
            {
                continue;
            }

            var buffer = BuildCppCallableScanBuffer(lines, structuralLines, startLineIndex);
            if (buffer == null)
                continue;

            int? discoveredBodyEndLine = null;
            if (TryParseCppCallableCandidate(buffer, structuralLines, typeIndex, out var candidate))
            {
                MergeCppCallableCandidate(fileId, candidate, symbols, extractionState);
                discoveredBodyEndLine = candidate.BodyEndLine;
            }

            if (TryParseCppQualifiedConstructorCandidate(
                buffer,
                structuralLines,
                typeIndex,
                out candidate))
            {
                MergeCppCallableCandidate(fileId, candidate, symbols, extractionState);
                if (candidate.BodyEndLine is { } qualifiedBodyEndLine)
                    discoveredBodyEndLine = Math.Max(discoveredBodyEndLine ?? 0, qualifiedBodyEndLine);
            }

            if (discoveredBodyEndLine is { } bodyEndLine)
                startLineIndex = Math.Max(startLineIndex, bodyEndLine - 1);
        }
    }

    private static bool IsCppAccessLabel(ReadOnlySpan<char> line) =>
        line is "public:" or "protected:" or "private:";

    private static bool IsCppStandaloneMacroInvocation(ReadOnlySpan<char> line)
    {
        var cursor = 0;
        var hasUppercase = false;
        while (cursor < line.Length && (char.IsUpper(line[cursor]) || char.IsDigit(line[cursor]) || line[cursor] == '_'))
        {
            hasUppercase |= char.IsUpper(line[cursor]);
            cursor++;
        }

        if (!hasUppercase || cursor == 0)
            return false;
        while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
            cursor++;
        if (cursor == line.Length)
            return true;
        if (line[cursor] != '(')
            return false;

        var depth = 0;
        for (; cursor < line.Length; cursor++)
        {
            if (line[cursor] == '(')
                depth++;
            else if (line[cursor] == ')' && --depth == 0)
            {
                cursor++;
                while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
                    cursor++;
                return cursor == line.Length;
            }
        }

        return false;
    }

    private static CppCallableTypeIndex BuildCppCallableTypeIndex(
        IReadOnlyList<SymbolRecord> symbols,
        int lineCount)
    {
        var byName = new Dictionary<string, SymbolRecord>(StringComparer.Ordinal);
        var byLine = new SymbolRecord?[lineCount + 1];
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("class" or "struct" or "union"))
                continue;

            byName.TryAdd(symbol.Name, symbol);
            if (symbol.BodyStartLine is not { } bodyStartLine
                || symbol.BodyEndLine is not { } bodyEndLine)
            {
                continue;
            }

            var startLine = Math.Max(1, bodyStartLine);
            var endLine = Math.Min(lineCount, bodyEndLine);
            for (var lineNumber = startLine; lineNumber <= endLine; lineNumber++)
            {
                var current = byLine[lineNumber];
                if (current == null
                    || symbol.StartLine > current.StartLine
                    || (symbol.StartLine == current.StartLine && symbol.EndLine < current.EndLine))
                {
                    byLine[lineNumber] = symbol;
                }
            }
        }

        return new CppCallableTypeIndex(byName, byLine);
    }

    private static List<CppCallableBodyRange> BuildCppCallableBodyRanges(IReadOnlyList<SymbolRecord> symbols)
    {
        var ranges = symbols
            .Where(static symbol => symbol.Kind is "function" or "specialization"
                && symbol.BodyStartLine.HasValue
                && symbol.BodyEndLine.HasValue
                && symbol.BodyEndLine.Value > symbol.BodyStartLine.Value)
            .Select(static symbol => new CppCallableBodyRange(
                symbol.BodyStartLine!.Value + 1,
                symbol.BodyEndLine!.Value))
            .OrderBy(static range => range.StartLine)
            .ThenByDescending(static range => range.EndLine)
            .ToList();
        if (ranges.Count < 2)
            return ranges;

        var writeIndex = 0;
        for (var readIndex = 1; readIndex < ranges.Count; readIndex++)
        {
            var current = ranges[writeIndex];
            var next = ranges[readIndex];
            if (next.StartLine <= current.EndLine + 1)
            {
                ranges[writeIndex] = current with { EndLine = Math.Max(current.EndLine, next.EndLine) };
                continue;
            }

            ranges[++writeIndex] = next;
        }

        if (writeIndex + 1 < ranges.Count)
            ranges.RemoveRange(writeIndex + 1, ranges.Count - writeIndex - 1);
        return ranges;
    }

    private static bool TryParseCppQualifiedConstructorCandidate(
        CppCallableScanBuffer buffer,
        string[] structuralLines,
        CppCallableTypeIndex typeIndex,
        out CppCallableCandidate candidate)
    {
        candidate = default;
        var declarator = buffer.Structural.AsSpan();
        var openParen = declarator.IndexOf('(');
        if (openParen <= 0)
            return false;

        var separator = declarator[..openParen].LastIndexOf("::", StringComparison.Ordinal);
        if (separator <= 0)
            return false;

        var nameSpan = declarator[(separator + 2)..openParen].Trim();
        if (nameSpan.IsEmpty || nameSpan[0] == '~')
            return false;
        foreach (var ch in nameSpan)
        {
            if (ch != '_' && !char.IsLetterOrDigit(ch))
                return false;
        }

        var qualifierEnd = separator;
        while (qualifierEnd > 0 && char.IsWhiteSpace(declarator[qualifierEnd - 1]))
            qualifierEnd--;
        var qualifierIdentifierEnd = SkipCppTemplateArgumentListBackward(declarator, qualifierEnd);
        var qualifierIdentifierStart = qualifierIdentifierEnd;
        while (qualifierIdentifierStart > 0
            && (char.IsLetterOrDigit(declarator[qualifierIdentifierStart - 1])
                || declarator[qualifierIdentifierStart - 1] == '_'))
        {
            qualifierIdentifierStart--;
        }
        if (qualifierIdentifierStart == qualifierIdentifierEnd)
            return false;

        var qualifierName = declarator[qualifierIdentifierStart..qualifierIdentifierEnd].ToString();
        var name = nameSpan.ToString();
        if (!string.Equals(name, qualifierName, StringComparison.Ordinal)
            || !string.IsNullOrWhiteSpace(StripCppCallableDeclaratorPrefixes(declarator[..qualifierIdentifierStart])))
        {
            return false;
        }

        var closeParen = FindCppMatchingDelimiter(buffer.Structural, openParen, '(', ')');
        if (closeParen < 0
            || !TryFindCppCallableHeaderTerminator(
                buffer.Structural,
                closeParen + 1,
                out var terminatorIndex,
                out var terminator)
            || !TryMapCppBufferOffset(buffer, separator + 2, out var nameLineIndex, out var nameColumn)
            || !TryMapCppBufferOffset(buffer, terminatorIndex, out var terminatorLineIndex, out var terminatorColumn))
        {
            return false;
        }

        var startLine = buffer.StartLineIndex + 1;
        var endLine = terminatorLineIndex + 1;
        int? bodyStartLine = null;
        int? bodyEndLine = null;
        if (terminator == '{')
        {
            var range = FindBraceRange(structuralLines, terminatorLineIndex, terminatorColumn, "cpp");
            endLine = Math.Max(endLine, range.EndLine);
            bodyStartLine = range.BodyStartLine;
            bodyEndLine = range.BodyEndLine;
        }

        typeIndex.ByName.TryGetValue(qualifierName, out var container);
        candidate = new CppCallableCandidate(
            name,
            ReturnType: null,
            BuildCppCallableSignature(buffer.Raw.AsSpan(0, terminatorIndex + 1)),
            NameLine: nameLineIndex + 1,
            StartLine: startLine,
            StartColumn: nameColumn,
            EndLine: endLine,
            BodyStartLine: bodyStartLine,
            BodyEndLine: bodyEndLine,
            ContainerName: qualifierName,
            ContainerKind: container?.Kind);
        return true;
    }

    private static CppCallableScanBuffer? BuildCppCallableScanBuffer(
        IReadOnlyList<string> lines,
        IReadOnlyList<string> structuralLines,
        int startLineIndex)
    {
        if (!HasCppCallableParenthesisLookahead(structuralLines, startLineIndex))
            return null;

        var raw = new StringBuilder();
        var structural = new StringBuilder();
        var lineStarts = new List<int>();
        var endLineIndex = Math.Min(lines.Count, startLineIndex + CppCallableScanLineLimit);

        for (var lineIndex = startLineIndex; lineIndex < endLineIndex; lineIndex++)
        {
            if (raw.Length > 0)
            {
                raw.Append('\n');
                structural.Append('\n');
            }

            lineStarts.Add(raw.Length);
            raw.Append(lines[lineIndex]);
            structural.Append(structuralLines[lineIndex]);
            if (raw.Length >= CppCallableScanCharacterLimit
                || HasCppCallableHeaderBoundary(structural))
            {
                break;
            }
        }

        return raw.Length == 0
            ? null
            : new CppCallableScanBuffer(raw.ToString(), structural.ToString(), lineStarts.ToArray(), startLineIndex);
    }

    private static bool HasCppCallableParenthesisLookahead(
        IReadOnlyList<string> structuralLines,
        int startLineIndex)
    {
        var characterCount = 0;
        var endLineIndex = Math.Min(structuralLines.Count, startLineIndex + CppCallableScanLineLimit);
        for (var lineIndex = startLineIndex; lineIndex < endLineIndex; lineIndex++)
        {
            var line = structuralLines[lineIndex];
            for (var column = 0; column < line.Length; column++)
            {
                if (++characterCount > CppCallableScanCharacterLimit)
                    return false;
                if (line[column] == '(')
                    return true;
                if (line[column] is ';' or '{' or '}')
                    return false;
            }
        }

        return false;
    }

    private static bool HasCppCallableHeaderBoundary(StringBuilder text)
    {
        var parenDepth = 0;
        var squareDepth = 0;
        var seenParen = false;
        for (var index = 0; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '(':
                    parenDepth++;
                    seenParen = true;
                    break;
                case ')' when parenDepth > 0:
                    parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']' when squareDepth > 0:
                    squareDepth--;
                    break;
                case ';' or '{' or '}' when seenParen && parenDepth == 0 && squareDepth == 0:
                    return true;
            }
        }

        return false;
    }

    private static bool TryParseCppCallableCandidate(
        CppCallableScanBuffer buffer,
        string[] structuralLines,
        CppCallableTypeIndex typeIndex,
        out CppCallableCandidate candidate)
    {
        candidate = default;
        var structural = buffer.Structural;
        var squareDepth = 0;
        var angleDepth = 0;

        for (var index = 0; index < structural.Length; index++)
        {
            var ch = structural[index];
            if (ch == '[')
            {
                squareDepth++;
                continue;
            }

            if (ch == ']' && squareDepth > 0)
            {
                squareDepth--;
                continue;
            }

            if (squareDepth > 0)
                continue;

            if (ch == '<' && !IsCppOperatorPunctuation(structural, index))
            {
                angleDepth++;
                continue;
            }

            if (ch == '>' && angleDepth > 0)
            {
                angleDepth--;
                continue;
            }

            if (angleDepth == 0 && ch is '{' or ';')
                return false;

            if (angleDepth != 0 || ch != '(')
                continue;

            var closeParenIndex = FindCppMatchingDelimiter(structural, index, '(', ')');
            if (closeParenIndex < 0)
                return false;

            if (!TryMapCppBufferOffset(buffer, index, out var parameterLineIndex, out _)
                || !TryParseCppCallablePrefix(
                    structural.AsSpan(0, index),
                    parameterLineIndex + 1,
                    typeIndex,
                    out var prefix))
            {
                index = closeParenIndex;
                continue;
            }

            if (!TryFindCppCallableHeaderTerminator(
                    structural,
                    closeParenIndex + 1,
                    out var terminatorIndex,
                    out var terminator))
            {
                return false;
            }

            var returnType = TryExtractCppTrailingReturnType(
                buffer.Raw,
                structural,
                closeParenIndex + 1,
                terminatorIndex) ?? prefix.ReturnType;

            if (!TryMapCppBufferOffset(buffer, prefix.NameOffset, out var nameLineIndex, out var startColumn)
                || !TryMapCppBufferOffset(buffer, terminatorIndex, out var terminatorLineIndex, out var terminatorColumn))
            {
                return false;
            }

            var startLine = buffer.StartLineIndex + 1;
            var nameLine = nameLineIndex + 1;
            var endLine = terminatorLineIndex + 1;
            int? bodyStartLine = null;
            int? bodyEndLine = null;
            if (terminator == '{')
            {
                var range = FindBraceRange(structuralLines, terminatorLineIndex, terminatorColumn, "cpp");
                endLine = Math.Max(endLine, range.EndLine);
                bodyStartLine = range.BodyStartLine;
                bodyEndLine = range.BodyEndLine;
            }

            var signature = BuildCppCallableSignature(buffer.Raw.AsSpan(0, terminatorIndex + 1));
            candidate = new CppCallableCandidate(
                prefix.Name,
                NormalizeCppCallableMetadata(returnType),
                signature,
                nameLine,
                startLine,
                startColumn,
                endLine,
                bodyStartLine,
                bodyEndLine,
                prefix.QualifiedContainerName,
                prefix.QualifiedContainerKind);
            return true;
        }

        return false;
    }

    private static bool IsCppOperatorPunctuation(string text, int index)
    {
        var wordEnd = index;
        while (wordEnd > 0 && IsCppOverloadOperatorPunctuation(text[wordEnd - 1]))
            wordEnd--;
        while (wordEnd > 0 && char.IsWhiteSpace(text[wordEnd - 1]))
            wordEnd--;
        var wordStart = wordEnd;
        while (wordStart > 0 && char.IsLetter(text[wordStart - 1]))
            wordStart--;
        return text.AsSpan(wordStart, wordEnd - wordStart).SequenceEqual("operator");
    }

    private static bool IsCppOverloadOperatorPunctuation(char ch) =>
        ch is '!' or '%' or '&' or '*' or '+' or '-' or '/' or '<' or '=' or '>' or '^' or '|' or '~';

    private static int FindCppMatchingDelimiter(string text, int openIndex, char open, char close)
    {
        var depth = 0;
        for (var index = openIndex; index < text.Length; index++)
        {
            if (text[index] == open)
                depth++;
            else if (text[index] == close && --depth == 0)
                return index;
        }

        return -1;
    }

    private static bool TryParseCppCallablePrefix(
        ReadOnlySpan<char> rawPrefix,
        int nameLine,
        CppCallableTypeIndex typeIndex,
        out CppCallablePrefix prefix)
    {
        prefix = default;
        var prefixEnd = rawPrefix.Length;
        while (prefixEnd > 0 && char.IsWhiteSpace(rawPrefix[prefixEnd - 1]))
            prefixEnd--;
        if (prefixEnd == 0)
            return false;

        var operatorIndex = FindLastCppOperatorKeyword(rawPrefix[..prefixEnd]);
        string name;
        int nameOffset;
        int qualifierEnd;
        if (operatorIndex >= 0)
        {
            var operatorSuffix = NormalizeCppOperatorName(rawPrefix[operatorIndex..prefixEnd]);
            if (operatorSuffix == null)
                return false;
            name = operatorSuffix;
            nameOffset = operatorIndex;
            qualifierEnd = operatorIndex;
        }
        else
        {
            var nameEnd = SkipCppTemplateArgumentListBackward(rawPrefix, prefixEnd);
            var nameStart = nameEnd;
            while (nameStart > 0 && (char.IsLetterOrDigit(rawPrefix[nameStart - 1]) || rawPrefix[nameStart - 1] == '_'))
                nameStart--;
            if (nameStart == nameEnd)
                return false;
            if (nameStart > 0 && rawPrefix[nameStart - 1] == '~')
                nameStart--;

            name = rawPrefix[nameStart..nameEnd].ToString();
            nameOffset = nameStart;
            qualifierEnd = nameStart;
        }

        if (CppNonCallableNames.Contains(name))
            return false;

        var prefixBoundary = qualifierEnd;
        var qualifiedContainerName = TryReadCppQualifiedContainerBackward(rawPrefix, ref prefixBoundary);
        var containingType = nameLine >= 0 && nameLine < typeIndex.ByLine.Length
            ? typeIndex.ByLine[nameLine]
            : null;
        SymbolRecord? containerSymbol = containingType;
        if (qualifiedContainerName != null)
            typeIndex.ByName.TryGetValue(qualifiedContainerName, out containerSymbol);
        var containerName = qualifiedContainerName ?? containingType?.Name;
        var containerKind = containerSymbol?.Kind ?? containingType?.Kind;

        if (ContainsCppKeyword(rawPrefix[..prefixBoundary], "friend"))
            return false;

        var declaratorPrefix = StripCppCallableDeclaratorPrefixes(rawPrefix[..prefixBoundary]);
        var normalizedPrefix = NormalizeCppCallableMetadata(declaratorPrefix);
        var unqualifiedName = name[0] == '~' ? name[1..] : name;
        var isConstructor = name[0] != '~'
            && !name.StartsWith("operator", StringComparison.Ordinal)
            && string.Equals(unqualifiedName, containerName, StringComparison.Ordinal);
        var isDestructor = name[0] == '~'
            && string.Equals(unqualifiedName, containerName, StringComparison.Ordinal);

        string? returnType;
        if (isConstructor || isDestructor)
        {
            if (!string.IsNullOrWhiteSpace(normalizedPrefix))
                return false;
            returnType = null;
        }
        else if (name.StartsWith("operator ", StringComparison.Ordinal)
            && !CppNamedOperatorOverloads.Contains(name))
        {
            if (containerName == null || !string.IsNullOrWhiteSpace(normalizedPrefix))
                return false;
            returnType = name["operator ".Length..];
        }
        else
        {
            if (string.IsNullOrWhiteSpace(normalizedPrefix)
                || !IsPlausibleCppReturnType(normalizedPrefix))
            {
                return false;
            }
            returnType = normalizedPrefix;
        }

        prefix = new CppCallablePrefix(
            name,
            returnType,
            nameOffset,
            containerName,
            containerKind);
        return true;
    }

    private static int FindLastCppOperatorKeyword(ReadOnlySpan<char> text)
    {
        for (var index = text.Length - "operator".Length; index >= 0; index--)
        {
            if (!text[index..].StartsWith("operator", StringComparison.Ordinal))
                continue;
            if (index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_'))
                continue;
            var after = index + "operator".Length;
            if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_'))
                continue;
            return index;
        }

        return -1;
    }

    private static string? NormalizeCppOperatorName(ReadOnlySpan<char> operatorText)
    {
        var normalized = BuildCppCallableSignature(operatorText);
        if (normalized == null || normalized == "operator")
            return null;

        var suffix = normalized.AsSpan("operator".Length).TrimStart();
        if (suffix.IsEmpty)
            return null;
        if (char.IsLetter(suffix[0]) || suffix[0] == '_')
            return "operator " + suffix.ToString();
        return "operator" + suffix.ToString().Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    private static int SkipCppTemplateArgumentListBackward(ReadOnlySpan<char> text, int end)
    {
        var cursor = end;
        while (cursor > 0 && char.IsWhiteSpace(text[cursor - 1]))
            cursor--;
        if (cursor == 0 || text[cursor - 1] != '>')
            return cursor;

        var depth = 0;
        for (var index = cursor - 1; index >= 0; index--)
        {
            if (text[index] == '>')
                depth++;
            else if (text[index] == '<' && --depth == 0)
                return index;
        }

        return cursor;
    }

    private static string? TryReadCppQualifiedContainerBackward(ReadOnlySpan<char> text, ref int prefixEnd)
    {
        var cursor = prefixEnd;
        while (cursor > 0 && char.IsWhiteSpace(text[cursor - 1]))
            cursor--;
        if (cursor < 2 || text[cursor - 1] != ':' || text[cursor - 2] != ':')
        {
            prefixEnd = cursor;
            return null;
        }

        string? closestQualifier = null;
        while (cursor >= 2 && text[cursor - 1] == ':' && text[cursor - 2] == ':')
        {
            cursor -= 2;
            while (cursor > 0 && char.IsWhiteSpace(text[cursor - 1]))
                cursor--;
            var identifierEnd = SkipCppTemplateArgumentListBackward(text, cursor);
            var identifierStart = identifierEnd;
            while (identifierStart > 0
                && (char.IsLetterOrDigit(text[identifierStart - 1]) || text[identifierStart - 1] == '_'))
            {
                identifierStart--;
            }
            if (identifierStart == identifierEnd)
                break;

            closestQualifier ??= text[identifierStart..identifierEnd].ToString();
            cursor = identifierStart;
            while (cursor > 0 && char.IsWhiteSpace(text[cursor - 1]))
                cursor--;
        }

        prefixEnd = cursor;
        return closestQualifier;
    }

    private static string StripCppCallableDeclaratorPrefixes(ReadOnlySpan<char> text)
    {
        var normalized = BuildCppCallableSignature(text);
        var cursor = 0;
        while (cursor < normalized.Length)
        {
            while (cursor < normalized.Length && char.IsWhiteSpace(normalized[cursor]))
                cursor++;
            if (normalized.AsSpan(cursor).StartsWith("template", StringComparison.Ordinal))
            {
                var templateStart = cursor + "template".Length;
                while (templateStart < normalized.Length && char.IsWhiteSpace(normalized[templateStart]))
                    templateStart++;
                if (templateStart < normalized.Length && normalized[templateStart] == '<')
                {
                    var templateEnd = FindCppMatchingDelimiter(normalized, templateStart, '<', '>');
                    if (templateEnd < 0)
                        break;
                    cursor = templateEnd + 1;
                    continue;
                }
            }

            if (normalized.AsSpan(cursor).StartsWith("[[", StringComparison.Ordinal))
            {
                var attributeEnd = normalized.IndexOf("]]", cursor, StringComparison.Ordinal);
                if (attributeEnd < 0)
                    break;
                cursor = attributeEnd + 2;
                continue;
            }

            var tokenEnd = cursor;
            while (tokenEnd < normalized.Length
                && (char.IsLetterOrDigit(normalized[tokenEnd]) || normalized[tokenEnd] == '_'))
            {
                tokenEnd++;
            }
            if (tokenEnd == cursor)
                break;
            var token = normalized[cursor..tokenEnd];
            if (!CppCallablePrefixModifiers.Contains(token))
                break;
            cursor = tokenEnd;
            if (token == "explicit")
            {
                while (cursor < normalized.Length && char.IsWhiteSpace(normalized[cursor]))
                    cursor++;
                if (cursor < normalized.Length && normalized[cursor] == '(')
                {
                    var conditionEnd = FindCppMatchingDelimiter(normalized, cursor, '(', ')');
                    if (conditionEnd < 0)
                        break;
                    cursor = conditionEnd + 1;
                }
            }
        }

        return normalized[cursor..].Trim();
    }

    private static bool ContainsCppKeyword(ReadOnlySpan<char> text, ReadOnlySpan<char> keyword)
    {
        for (var index = 0; index + keyword.Length <= text.Length; index++)
        {
            if (!text[index..].StartsWith(keyword, StringComparison.Ordinal))
                continue;
            var beforeIsIdentifier = index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_');
            var after = index + keyword.Length;
            var afterIsIdentifier = after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_');
            if (!beforeIsIdentifier && !afterIsIdentifier)
                return true;
        }

        return false;
    }

    private static bool IsPlausibleCppReturnType(string returnType)
    {
        if (returnType.IndexOfAny(['=', ';', '{', '}']) >= 0)
            return false;
        var firstTokenEnd = 0;
        while (firstTokenEnd < returnType.Length
            && (char.IsLetterOrDigit(returnType[firstTokenEnd]) || returnType[firstTokenEnd] == '_'))
        {
            firstTokenEnd++;
        }
        return firstTokenEnd > 0
            && !CppInvalidReturnTypePrefixes.Contains(returnType[..firstTokenEnd]);
    }

    private static bool TryFindCppCallableHeaderTerminator(
        string text,
        int startIndex,
        out int terminatorIndex,
        out char terminator)
    {
        terminatorIndex = -1;
        terminator = default;
        var parenDepth = 0;
        var squareDepth = 0;
        var angleDepth = 0;
        var constructorInitializerSeen = false;

        for (var index = startIndex; index < text.Length; index++)
        {
            switch (text[index])
            {
                case '(':
                    parenDepth++;
                    break;
                case ')' when parenDepth > 0:
                    parenDepth--;
                    break;
                case '[':
                    squareDepth++;
                    break;
                case ']' when squareDepth > 0:
                    squareDepth--;
                    break;
                case '<' when !IsCppOperatorPunctuation(text, index):
                    angleDepth++;
                    break;
                case '>' when angleDepth > 0:
                    angleDepth--;
                    break;
                case ':' when parenDepth == 0 && squareDepth == 0 && angleDepth == 0:
                    if ((index == 0 || text[index - 1] != ':')
                        && (index + 1 >= text.Length || text[index + 1] != ':'))
                    {
                        constructorInitializerSeen = true;
                    }
                    break;
                case ';' when parenDepth == 0 && squareDepth == 0 && angleDepth == 0:
                    terminatorIndex = index;
                    terminator = ';';
                    return true;
                case '{' when parenDepth == 0 && squareDepth == 0 && angleDepth == 0:
                    if (constructorInitializerSeen)
                    {
                        var initializerEnd = FindCppMatchingDelimiter(text, index, '{', '}');
                        if (initializerEnd > index)
                        {
                            var next = initializerEnd + 1;
                            while (next < text.Length && char.IsWhiteSpace(text[next]))
                                next++;
                            if (next < text.Length && text[next] is ',' or '{')
                            {
                                index = initializerEnd;
                                continue;
                            }
                        }
                    }

                    terminatorIndex = index;
                    terminator = '{';
                    return true;
            }
        }

        return false;
    }

    private static string? TryExtractCppTrailingReturnType(
        string raw,
        string structural,
        int startIndex,
        int endIndex)
    {
        var parenDepth = 0;
        var squareDepth = 0;
        var angleDepth = 0;
        for (var index = startIndex; index + 1 < endIndex; index++)
        {
            var ch = structural[index];
            if (ch == '(')
                parenDepth++;
            else if (ch == ')' && parenDepth > 0)
                parenDepth--;
            else if (ch == '[')
                squareDepth++;
            else if (ch == ']' && squareDepth > 0)
                squareDepth--;
            else if (ch == '<' && !IsCppOperatorPunctuation(structural, index))
                angleDepth++;
            else if (ch == '>' && angleDepth > 0)
                angleDepth--;
            else if (ch == '-'
                && structural[index + 1] == '>'
                && parenDepth == 0
                && squareDepth == 0
                && angleDepth == 0)
            {
                var returnTypeStart = index + 2;
                while (returnTypeStart < endIndex && char.IsWhiteSpace(structural[returnTypeStart]))
                    returnTypeStart++;
                var returnTypeEnd = FindCppTrailingReturnTypeEnd(structural, returnTypeStart, endIndex);
                return returnTypeEnd > returnTypeStart
                    ? raw[returnTypeStart..returnTypeEnd]
                    : null;
            }
        }

        return null;
    }

    private static int FindCppTrailingReturnTypeEnd(string text, int startIndex, int endIndex)
    {
        var parenDepth = 0;
        var squareDepth = 0;
        var angleDepth = 0;
        for (var index = startIndex; index < endIndex; index++)
        {
            var ch = text[index];
            if (ch == '(')
                parenDepth++;
            else if (ch == ')' && parenDepth > 0)
                parenDepth--;
            else if (ch == '[')
                squareDepth++;
            else if (ch == ']' && squareDepth > 0)
                squareDepth--;
            else if (ch == '<')
                angleDepth++;
            else if (ch == '>' && angleDepth > 0)
                angleDepth--;
            else if (parenDepth == 0 && squareDepth == 0 && angleDepth == 0
                && (ch == '='
                    || StartsWithCppKeyword(text, index, "requires")
                    || StartsWithCppKeyword(text, index, "override")
                    || StartsWithCppKeyword(text, index, "final")))
            {
                return index;
            }
        }

        return endIndex;
    }

    private static bool StartsWithCppKeyword(string text, int index, string keyword)
    {
        if (!text.AsSpan(index).StartsWith(keyword, StringComparison.Ordinal))
            return false;
        var beforeIsIdentifier = index > 0 && (char.IsLetterOrDigit(text[index - 1]) || text[index - 1] == '_');
        var after = index + keyword.Length;
        var afterIsIdentifier = after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_');
        return !beforeIsIdentifier && !afterIsIdentifier;
    }

    private static bool TryMapCppBufferOffset(
        CppCallableScanBuffer buffer,
        int offset,
        out int lineIndex,
        out int column)
    {
        lineIndex = buffer.StartLineIndex;
        column = 0;
        if (offset < 0 || offset > buffer.Raw.Length)
            return false;

        var relativeLineIndex = Array.BinarySearch(buffer.LineStarts, offset);
        if (relativeLineIndex < 0)
            relativeLineIndex = ~relativeLineIndex - 1;
        if (relativeLineIndex < 0 || relativeLineIndex >= buffer.LineStarts.Length)
            return false;

        lineIndex = buffer.StartLineIndex + relativeLineIndex;
        column = offset - buffer.LineStarts[relativeLineIndex];
        return true;
    }

    private static string BuildCppCallableSignature(ReadOnlySpan<char> text)
    {
        var builder = new StringBuilder(text.Length);
        var pendingWhitespace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingWhitespace = builder.Length > 0;
                continue;
            }

            if (pendingWhitespace
                && builder.Length > 0
                && builder[^1] is not '<' and not ':'
                && ch is not '>' and not ',' and not ';' and not ':' and not ')')
            {
                builder.Append(' ');
            }

            pendingWhitespace = false;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    private static string? NormalizeCppCallableMetadata(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return BuildCppCallableSignature(value.AsSpan());
    }

    private static void MergeCppCallableCandidate(
        long fileId,
        CppCallableCandidate candidate,
        List<SymbolRecord> symbols,
        SymbolExtractionState extractionState)
    {
        SymbolRecord? existing = null;
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is ("function" or "specialization")
                && symbol.Line == candidate.NameLine
                && string.Equals(symbol.Name, candidate.Name, StringComparison.Ordinal))
            {
                existing = symbol;
                break;
            }
        }

        if (existing != null)
        {
            extractionState.Remove(existing);
            if (existing.Kind == "specialization"
                && candidate.ContainerName != null
                && string.Equals(candidate.Name, candidate.ContainerName, StringComparison.Ordinal))
            {
                existing.Kind = "function";
            }
            existing.ReturnType = candidate.ReturnType;
            if (candidate.StartLine < existing.StartLine
                || candidate.Signature.Length > (existing.Signature?.Length ?? 0))
            {
                existing.Signature = candidate.Signature;
                existing.StartLine = candidate.StartLine;
                existing.StartColumn = candidate.StartColumn;
            }
            existing.EndLine = Math.Max(existing.EndLine, candidate.EndLine);
            existing.BodyStartLine ??= candidate.BodyStartLine;
            existing.BodyEndLine ??= candidate.BodyEndLine;
            existing.ContainerName ??= candidate.ContainerName;
            existing.ContainerKind ??= candidate.ContainerKind;
            extractionState.Record(existing);
            return;
        }

        var candidateSymbol = new SymbolRecord
        {
            FileId = fileId,
            Kind = "function",
            Name = candidate.Name,
            Line = candidate.NameLine,
            StartLine = candidate.StartLine,
            StartColumn = candidate.StartColumn,
            EndLine = candidate.EndLine,
            BodyStartLine = candidate.BodyStartLine,
            BodyEndLine = candidate.BodyEndLine,
            Signature = candidate.Signature,
            ReturnType = candidate.ReturnType,
            ContainerName = candidate.ContainerName,
            ContainerKind = candidate.ContainerKind,
        };
        AddSymbolRecord(
            symbols,
            extractionState,
            cssSeenSymbols: null,
            candidate.NameLine,
            candidateSymbol);
    }
}
