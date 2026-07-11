using System.Text;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class SymbolExtractor
{
    private static void ExtractJavaScriptTypeScriptDestructuredNamedExports(
        long fileId,
        string lang,
        string[] rawLines,
        string[] sanitizedLines,
        List<SymbolRecord> symbols,
        JavaScriptScopePrivacyFlags[][] privateScopeColumns)
    {
        for (int i = 0; i < sanitizedLines.Length; i++)
        {
            var sanitizedLine = sanitizedLines[i];
            var statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, 0);
            while (statementStart >= 0)
            {
                var statementSlice = sanitizedLine[statementStart..];
                var match = JavaScriptTypeScriptDestructuredNamedExportRegex.Match(statementSlice);
                if (!match.Success)
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var absoluteMatchIndex = statementStart + match.Index;
                if (IsJavaScriptTypeScriptMatchInPrivateScope(privateScopeColumns, i, absoluteMatchIndex, sanitizedLine, includeBlockScope: false))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                var openBraceColumn = absoluteMatchIndex + match.Value.LastIndexOf('{');
                if (!TryCollectJavaScriptTypeScriptDestructuredExportPattern(
                        lang,
                        rawLines,
                        sanitizedLines,
                        i,
                        absoluteMatchIndex,
                        openBraceColumn,
                        out var endLineIndex,
                        out var closeBraceColumn,
                        out var pattern,
                        out var signature))
                {
                    statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLine, statementStart + 1);
                    continue;
                }

                foreach (var exportedName in ParseJavaScriptTypeScriptDestructuredBindingNames(pattern))
                {
                    AddSymbolRecord(
                        symbols,
                        cssSeenSymbols: null,
                        i + 1,
                        new SymbolRecord
                        {
                            FileId = fileId,
                            Kind = "property",
                            Name = exportedName,
                            Line = i + 1,
                            StartLine = i + 1,
                            StartColumn = absoluteMatchIndex,
                            EndLine = endLineIndex + 1,
                            Signature = signature,
                            Visibility = "export",
                        },
                        rawLines[i]);
                }

                if (endLineIndex > i)
                    i = endLineIndex;
                statementStart = FindNextJavaScriptTypeScriptStatementStart(sanitizedLines[i], closeBraceColumn + 1);
                sanitizedLine = sanitizedLines[i];
            }
        }
    }

    private static bool TryCollectJavaScriptTypeScriptDestructuredExportPattern(
        string lang,
        string[] rawLines,
        string[] sanitizedLines,
        int startLineIndex,
        int exportStartColumn,
        int openBraceColumn,
        out int endLineIndex,
        out int closeBraceColumn,
        out string pattern,
        out string signature)
    {
        endLineIndex = startLineIndex;
        closeBraceColumn = -1;
        pattern = string.Empty;
        signature = string.Empty;

        var builderCapacity = EstimateJavaScriptTypeScriptStatementCapacity(sanitizedLines, startLineIndex);
        var patternBuilder = new StringBuilder(builderCapacity);
        var signatureBuilder = new StringBuilder(builderCapacity);
        var braceDepth = 0;

        for (int lineIndex = startLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var rawLine = rawLines[lineIndex];
            var column = lineIndex == startLineIndex ? openBraceColumn : 0;
            if (column < 0 || column >= sanitizedLine.Length)
                return false;

            var signatureStartColumn = lineIndex == startLineIndex ? exportStartColumn : 0;
            var signatureSliceStart = Math.Min(signatureStartColumn, rawLine.Length);

            for (; column < sanitizedLine.Length; column++)
            {
                var ch = sanitizedLine[column];
                if (ch == '{')
                {
                    braceDepth++;
                    if (braceDepth > 1)
                        patternBuilder.Append(ch);
                    continue;
                }

                if (ch == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0)
                    {
                        var rawSliceEnd = Math.Min(rawLine.Length, column + 1);
                        AppendTrimmedJavaScriptTypeScriptSignatureSlice(
                            signatureBuilder,
                            rawLine,
                            signatureSliceStart,
                            rawSliceEnd);

                        if (!HasJavaScriptTypeScriptDestructuredExportInitializer(
                                sanitizedLines,
                                lang,
                                lineIndex,
                                column + 1))
                        {
                            return false;
                        }

                        endLineIndex = lineIndex;
                        closeBraceColumn = column;
                        pattern = patternBuilder.ToString();
                        signature = signatureBuilder.ToString();
                        return true;
                    }

                    if (braceDepth < 0)
                        return false;

                    patternBuilder.Append(ch);
                    continue;
                }

                if (braceDepth > 0)
                    patternBuilder.Append(ch);
            }

            if (braceDepth > 0)
                patternBuilder.Append('\n');

            AppendTrimmedJavaScriptTypeScriptSignatureSlice(
                signatureBuilder,
                rawLine,
                signatureSliceStart,
                rawLine.Length);
        }

        return false;
    }

    private static void AppendTrimmedJavaScriptTypeScriptSignatureSlice(
        StringBuilder builder,
        string line,
        int start,
        int endExclusive)
    {
        start = Math.Clamp(start, 0, line.Length);
        endExclusive = Math.Clamp(endExclusive, start, line.Length);

        while (start < endExclusive && char.IsWhiteSpace(line[start]))
            start++;

        while (endExclusive > start && char.IsWhiteSpace(line[endExclusive - 1]))
            endExclusive--;

        if (endExclusive <= start)
            return;

        if (builder.Length > 0)
            builder.Append(' ');
        builder.Append(line, start, endExclusive - start);
    }

    private static bool HasJavaScriptTypeScriptDestructuredExportInitializer(
        string[] sanitizedLines,
        string lang,
        int startLineIndex,
        int startColumn)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        for (int lineIndex = startLineIndex; lineIndex < sanitizedLines.Length; lineIndex++)
        {
            var sanitizedLine = sanitizedLines[lineIndex];
            var column = lineIndex == startLineIndex ? Math.Max(0, startColumn) : 0;
            if (column >= sanitizedLine.Length)
                continue;

            var statementEndColumn = FindJavaScriptTypeScriptSameLineStatementEndColumn(sanitizedLine, column, lang);
            var endExclusive = statementEndColumn >= column
                ? statementEndColumn + 1
                : sanitizedLine.Length;

            for (; column < endExclusive; column++)
            {
                var ch = sanitizedLine[column];
                if (parenDepth == 0
                    && bracketDepth == 0
                    && braceDepth == 0
                    && ch == '='
                    && (column + 1 >= sanitizedLine.Length || sanitizedLine[column + 1] != '>')
                    && (column == 0 || sanitizedLine[column - 1] is not ('=' or '!' or '<' or '>')))
                {
                    return true;
                }

                switch (ch)
                {
                    case '(':
                        parenDepth++;
                        break;
                    case ')':
                        if (parenDepth > 0)
                            parenDepth--;
                        break;
                    case '[':
                        bracketDepth++;
                        break;
                    case ']':
                        if (bracketDepth > 0)
                            bracketDepth--;
                        break;
                    case '{':
                        braceDepth++;
                        break;
                    case '}':
                        if (braceDepth > 0)
                            braceDepth--;
                        break;
                }
            }

            if (statementEndColumn >= 0)
                return false;
        }

        return false;
    }

    private static IReadOnlyList<string> ParseJavaScriptTypeScriptDestructuredBindingNames(string pattern)
    {
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectJavaScriptTypeScriptObjectBindingNames(pattern, 0, pattern.Length, names, seen);
        return names;
    }

    private static void CollectJavaScriptTypeScriptObjectBindingNames(
        string text,
        int start,
        int end,
        List<string> names,
        HashSet<string> seen)
    {
        var index = start;
        while (index < end)
        {
            index = SkipJavaScriptTypeScriptDestructuredSeparators(text, index, end);
            if (index >= end)
                return;

            if (StartsJavaScriptTypeScriptDestructuredRest(text, index))
            {
                index += 3;
                TryCollectJavaScriptTypeScriptDestructuredIdentifier(text, ref index, end, names, seen);
                index = SkipJavaScriptTypeScriptDestructuredSegment(text, index, end);
                continue;
            }

            var keyStart = index;
            string? shorthandName = null;
            if (TryReadJavaScriptTypeScriptDestructuredPropertyKey(text, ref index, end, out var keyName))
                shorthandName = keyName;
            else
                index = SkipJavaScriptTypeScriptDestructuredSegment(text, index + 1, end);

            var afterKey = SkipJavaScriptTypeScriptDestructuredWhitespace(text, index, end);
            if (afterKey < end && text[afterKey] == ':')
            {
                index = SkipJavaScriptTypeScriptDestructuredWhitespace(text, afterKey + 1, end);
                CollectJavaScriptTypeScriptBindingNamesFromPattern(text, ref index, end, names, seen);
            }
            else
            {
                if (shorthandName != null)
                    AddJavaScriptTypeScriptDestructuredBindingName(shorthandName, names, seen);
                index = SkipJavaScriptTypeScriptDestructuredDefaultValue(text, afterKey, end);
            }

            if (index == keyStart)
                index++;
        }
    }

    private static void CollectJavaScriptTypeScriptArrayBindingNames(
        string text,
        int start,
        int end,
        List<string> names,
        HashSet<string> seen)
    {
        var index = start;
        while (index < end)
        {
            index = SkipJavaScriptTypeScriptDestructuredSeparators(text, index, end);
            if (index >= end)
                return;

            CollectJavaScriptTypeScriptBindingNamesFromPattern(text, ref index, end, names, seen);
        }
    }

    private static void CollectJavaScriptTypeScriptBindingNamesFromPattern(
        string text,
        ref int index,
        int end,
        List<string> names,
        HashSet<string> seen)
    {
        index = SkipJavaScriptTypeScriptDestructuredWhitespace(text, index, end);
        if (index >= end)
            return;

        if (StartsJavaScriptTypeScriptDestructuredRest(text, index))
        {
            index += 3;
            TryCollectJavaScriptTypeScriptDestructuredIdentifier(text, ref index, end, names, seen);
            index = SkipJavaScriptTypeScriptDestructuredSegment(text, index, end);
            return;
        }

        if (text[index] == '{'
            && TryFindJavaScriptTypeScriptDestructuredBalancedClose(text, index, end, '{', '}', out var objectClose))
        {
            CollectJavaScriptTypeScriptObjectBindingNames(text, index + 1, objectClose, names, seen);
            index = SkipJavaScriptTypeScriptDestructuredDefaultValue(text, objectClose + 1, end);
            return;
        }

        if (text[index] == '['
            && TryFindJavaScriptTypeScriptDestructuredBalancedClose(text, index, end, '[', ']', out var arrayClose))
        {
            CollectJavaScriptTypeScriptArrayBindingNames(text, index + 1, arrayClose, names, seen);
            index = SkipJavaScriptTypeScriptDestructuredDefaultValue(text, arrayClose + 1, end);
            return;
        }

        if (TryCollectJavaScriptTypeScriptDestructuredIdentifier(text, ref index, end, names, seen))
        {
            index = SkipJavaScriptTypeScriptDestructuredDefaultValue(text, index, end);
            return;
        }

        index = SkipJavaScriptTypeScriptDestructuredSegment(text, index + 1, end);
    }

    private static bool TryReadJavaScriptTypeScriptDestructuredPropertyKey(
        string text,
        ref int index,
        int end,
        out string? keyName)
    {
        keyName = null;
        index = SkipJavaScriptTypeScriptDestructuredWhitespace(text, index, end);
        if (index >= end)
            return false;

        if (IsJavaScriptTypeScriptIdentifierStart(text[index]))
        {
            var start = index;
            index++;
            while (index < end && IsJavaScriptTypeScriptIdentifierPart(text[index]))
                index++;
            keyName = text[start..index];
            return true;
        }

        if (text[index] is '\'' or '"' or '`')
            return TrySkipJavaScriptTypeScriptDestructuredQuotedLiteral(text, ref index, end);

        if (text[index] == '['
            && TryFindJavaScriptTypeScriptDestructuredBalancedClose(text, index, end, '[', ']', out var close))
        {
            index = close + 1;
            return true;
        }

        return false;
    }

    private static bool TryCollectJavaScriptTypeScriptDestructuredIdentifier(
        string text,
        ref int index,
        int end,
        List<string> names,
        HashSet<string> seen)
    {
        index = SkipJavaScriptTypeScriptDestructuredWhitespace(text, index, end);
        if (index >= end || !IsJavaScriptTypeScriptIdentifierStart(text[index]))
            return false;

        var start = index;
        index++;
        while (index < end && IsJavaScriptTypeScriptIdentifierPart(text[index]))
            index++;

        AddJavaScriptTypeScriptDestructuredBindingName(text[start..index], names, seen);
        return true;
    }

    private static void AddJavaScriptTypeScriptDestructuredBindingName(
        string name,
        List<string> names,
        HashSet<string> seen)
    {
        if (name.Length > 0 && seen.Add(name))
            names.Add(name);
    }

    private static int SkipJavaScriptTypeScriptDestructuredWhitespace(string text, int index, int end)
    {
        while (index < end && char.IsWhiteSpace(text[index]))
            index++;
        return index;
    }

    private static int SkipJavaScriptTypeScriptDestructuredSeparators(string text, int index, int end)
    {
        while (index < end && (char.IsWhiteSpace(text[index]) || text[index] == ','))
            index++;
        return index;
    }

    private static bool StartsJavaScriptTypeScriptDestructuredRest(string text, int index) =>
        index + 2 < text.Length
        && text[index] == '.'
        && text[index + 1] == '.'
        && text[index + 2] == '.';

    private static int SkipJavaScriptTypeScriptDestructuredDefaultValue(string text, int index, int end)
    {
        index = SkipJavaScriptTypeScriptDestructuredWhitespace(text, index, end);
        if (index >= end || text[index] != '=')
            return index;

        return SkipJavaScriptTypeScriptDestructuredSegment(text, index + 1, end);
    }

    private static int SkipJavaScriptTypeScriptDestructuredSegment(string text, int index, int end)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;

        while (index < end)
        {
            var ch = text[index];
            if (parenDepth == 0 && bracketDepth == 0 && braceDepth == 0 && ch == ',')
                return index + 1;

            switch (ch)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                        parenDepth--;
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                        bracketDepth--;
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                        braceDepth--;
                    break;
                case '\'':
                case '"':
                case '`':
                    TrySkipJavaScriptTypeScriptDestructuredQuotedLiteral(text, ref index, end);
                    continue;
            }

            index++;
        }

        return index;
    }

    private static bool TryFindJavaScriptTypeScriptDestructuredBalancedClose(
        string text,
        int openIndex,
        int end,
        char open,
        char close,
        out int closeIndex)
    {
        closeIndex = -1;
        var depth = 0;
        for (var index = openIndex; index < end; index++)
        {
            var ch = text[index];
            if (ch == open)
            {
                depth++;
                continue;
            }

            if (ch == close)
            {
                depth--;
                if (depth == 0)
                {
                    closeIndex = index;
                    return true;
                }

                continue;
            }

            if (ch is '\'' or '"' or '`')
            {
                TrySkipJavaScriptTypeScriptDestructuredQuotedLiteral(text, ref index, end);
                index--;
            }
        }

        return false;
    }

    private static bool TrySkipJavaScriptTypeScriptDestructuredQuotedLiteral(string text, ref int index, int end)
    {
        if (index >= end || text[index] is not ('\'' or '"' or '`'))
            return false;

        var delimiter = text[index];
        index++;
        var escapeNext = false;
        while (index < end)
        {
            var ch = text[index];
            if (escapeNext)
            {
                escapeNext = false;
                index++;
                continue;
            }

            if (ch == '\\')
            {
                escapeNext = true;
                index++;
                continue;
            }

            if (ch == delimiter)
            {
                index++;
                return true;
            }

            index++;
        }

        return false;
    }
}
