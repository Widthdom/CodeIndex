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
}
