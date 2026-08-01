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
                symbol.StartLine,
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
        int declarationStartLine,
        IReadOnlyList<CSharpLexState>? lineStartStates)
    {
        var lineIndex = Math.Min(lines.Count, Math.Max(1, declarationStartLine)) - 2;
        var minimumLineIndex = Math.Max(0, lineIndex - CSharpLeadingDeclarationLookbackLines + 1);
        var hasPartialModifier = false;
        var hasFileModifier = false;
        var hasAttribute = false;
        var hasDocumentation = false;
        var attributeDepth = 0;

        for (; lineIndex >= minimumLineIndex; lineIndex--)
        {
            var raw = lines[lineIndex].AsSpan().Trim();
            if (raw.IsEmpty)
                break;

            var lineStartState = lineStartStates != null && lineIndex < lineStartStates.Count
                ? lineStartStates[lineIndex]
                : new CSharpLexState();
            var startsInDeclarationCode = lineStartState.Mode == CSharpLexMode.Code
                && lineStartState.InterpolationBraceDepth == 0;
            if (startsInDeclarationCode && raw.StartsWith("///", StringComparison.Ordinal))
            {
                hasDocumentation = true;
                continue;
            }

            if (startsInDeclarationCode && raw.StartsWith("/**", StringComparison.Ordinal))
            {
                hasDocumentation = true;
                continue;
            }

            var sanitizedLine = LexCSharpLine(lines[lineIndex], lineStartState).SanitizedLine;
            var trimmed = sanitizedLine.AsSpan().Trim();
            if (trimmed.IsEmpty)
                continue;

            if (attributeDepth > 0 || trimmed[0] == '[' || trimmed[^1] == ']')
            {
                hasAttribute = true;
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
        var line = (lineStartStates != null && lineIndex < lineStartStates.Count
                ? LexCSharpLine(lines[lineIndex], lineStartStates[lineIndex]).SanitizedLine
                : LexCSharpLine(lines[lineIndex], new CSharpLexState()).SanitizedLine)
            .AsSpan();
        var name = symbol.Name.AsSpan().TrimStart('@');
        if (name.IsEmpty)
            return null;

        if (symbol.Kind is "class" or "struct" or "interface" or "record")
        {
            var keywordColumn = FindCSharpIdentifierToken(line, symbol.Kind.AsSpan(), 0);
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

        var searchStart = 0;
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
                return tokenStart;

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
