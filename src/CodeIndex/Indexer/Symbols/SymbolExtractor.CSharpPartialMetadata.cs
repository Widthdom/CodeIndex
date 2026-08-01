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

    private static void PopulateCSharpPartialDeclarationMetadata(
        IReadOnlyList<string> lines,
        IReadOnlyList<SymbolRecord> symbols,
        Func<CSharpLexState[]>? getCSharpLineStartStates)
    {
        var lineStartStates = getCSharpLineStartStates?.Invoke();
        foreach (var symbol in symbols)
        {
            if (symbol.Kind is not ("function" or "class" or "struct" or "interface" or "record"))
                continue;

            var signature = symbol.Signature ?? string.Empty;
            var sanitizedSignature = SanitizeCSharpDeclarationEvidence(signature);
            var leading = ReadCSharpLeadingDeclarationEvidence(
                lines,
                symbol,
                lineStartStates);
            symbol.IsPartialDeclaration = PartialModifierRegex.IsMatch(sanitizedSignature) || leading.HasPartialModifier;
            symbol.IsFileLocalDeclaration =
                symbol.Kind is "class" or "struct" or "interface" or "record"
                && (ContainsCSharpModifier(sanitizedSignature, "file") || leading.HasFileModifier);
            if (symbol.IsPartialDeclaration == true)
            {
                symbol.IdentifierStartColumn = FindCSharpDeclarationIdentifierColumn(
                    lines,
                    symbol,
                    lineStartStates);
            }

            var semanticScore = 0;
            if (sanitizedSignature.Contains('[') || leading.HasAttribute)
                semanticScore += 2;
            if (leading.HasDocumentation)
                semanticScore += 1;
            if (symbol.Kind is "class" or "struct" or "interface" or "record"
                && sanitizedSignature.Contains(':', StringComparison.Ordinal))
            {
                semanticScore += 4;
            }
            if (sanitizedSignature.Contains(" where ", StringComparison.Ordinal))
                semanticScore += 1;
            symbol.DeclarationSemanticScore = semanticScore;
        }
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

    private static CSharpLeadingDeclarationEvidence ReadCSharpLeadingDeclarationEvidence(
        IReadOnlyList<string> lines,
        SymbolRecord symbol,
        IReadOnlyList<CSharpLexState>? lineStartStates)
    {
        var declarationStartLine = symbol.StartLine;
        var lineIndex = Math.Min(lines.Count, Math.Max(1, declarationStartLine)) - 2;
        var minimumLineIndex = Math.Max(0, lineIndex - CSharpLeadingDeclarationLookbackLines + 1);
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

        for (; lineIndex >= minimumLineIndex; lineIndex--)
        {
            var raw = lines[lineIndex].AsSpan().Trim();
            if (raw.IsEmpty)
            {
                // Whitespace is valid declaration trivia between standalone modifiers
                // and the declaration. It does, however, detach XML documentation from
                // the declaration for representative ranking.
                // standalone modifier と宣言の間の空行は有効な declaration trivia だが、
                // XML documentation の representative rank 上の隣接性はここで切れる。
                documentationEvidenceAdjacent = false;
                continue;
            }

            var lineStartState = lineStartStates != null && lineIndex < lineStartStates.Count
                ? lineStartStates[lineIndex]
                : new CSharpLexState();
            var startsInDeclarationCode = lineStartState.Mode == CSharpLexMode.Code
                && lineStartState.InterpolationBraceDepth == 0;
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

            var sanitizedLine = LexCSharpLine(lines[lineIndex], lineStartState).SanitizedLine;
            var trimmed = sanitizedLine.AsSpan().Trim();
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
                hasAttribute = true;
                if (!trailingModifiers.IsEmpty)
                {
                    if (!hasTrailingModifiers)
                        break;

                    hasPartialModifier |= trailingHasPartial;
                    hasFileModifier |= trailingHasFile;
                }
                attributeDepth += CountCharacter(trimmed, ']') - CountCharacter(trimmed, '[');
                attributeDepth = Math.Max(0, attributeDepth);
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
        // last code token before this declaration occurrence. This prevents an attribute
        // on an earlier same-line declaration from ranking every later symbol on the line.
        // attribute の閉じ括弧がこの宣言 occurrence 直前の最後の code token である場合だけ、
        // この宣言の attribute とみなす。前方の同一行宣言に付いた attribute を、後続の
        // 全 symbol の rank に誤適用しない。
        return cursor >= 0 && sanitizedLine[cursor] == ']';
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
            if (symbol.Kind == "function"
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
}
