using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class PythonReferenceExtractor
{
    public static void EmitDataclassesFieldsReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf("fields", StringComparison.Ordinal) < 0)
            return;

        foreach (Match match in DataclassesFieldsTargetRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (isIgnoredName(name))
                continue;

            ReferenceExtractor.AddTypeReferenceSegments(
                references,
                seen,
                fileId,
                name,
                match.Groups["name"].Index,
                context,
                lineNumber,
                container,
                "python");
        }
    }

    public static void EmitDataclassFieldReferences(
        string[] preparedLines,
        string[] originalLines,
        int lineIndex,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        var preparedLine = preparedLines[lineIndex];
        if (preparedLine.IndexOf("field", StringComparison.Ordinal) < 0)
            return;
        if (preparedLine.IndexOf('=') < 0 || preparedLine.IndexOf('(') < 0)
            return;
        if (!DataclassFieldCallRegex.IsMatch(preparedLine))
            return;

        var depth = 0;
        var sawFieldCall = false;
        var inString = false;
        var quoteChar = '\0';

        for (var currentLineIndex = lineIndex; currentLineIndex < preparedLines.Length; currentLineIndex++)
        {
            var currentPreparedLine = preparedLines[currentLineIndex];
            var currentOriginalLine = originalLines[currentLineIndex];
            var currentLineNumber = currentLineIndex + 1;

            EmitDataclassFieldDefaultFactoryReferences(
                currentPreparedLine,
                currentOriginalLine,
                references,
                seen,
                fileId,
                currentLineNumber,
                container,
                isIgnoredName);
            EmitDataclassFieldMetadataReferences(
                originalLines,
                currentLineIndex,
                references,
                seen,
                fileId,
                container,
                isIgnoredName);

            for (var column = 0; column < currentPreparedLine.Length; column++)
            {
                var ch = currentPreparedLine[column];
                if (inString)
                {
                    if (ch == '\\')
                    {
                        column++;
                        continue;
                    }

                    if (ch == quoteChar)
                        inString = false;
                    continue;
                }

                if (ch == '#')
                    break;
                if (ch is '\'' or '"')
                {
                    inString = true;
                    quoteChar = ch;
                    continue;
                }

                if (ch == '(')
                {
                    depth++;
                    sawFieldCall = true;
                }
                else if (ch == ')' && depth > 0)
                {
                    depth--;
                    if (sawFieldCall && depth == 0)
                        return;
                }
            }

            if (sawFieldCall && depth <= 0)
                return;
        }
    }

    private static void EmitDataclassFieldDefaultFactoryReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        int lineNumber,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf("default_factory", StringComparison.Ordinal) < 0)
            return;
        if (preparedLine.IndexOf('=') < 0)
            return;

        string? context = null;
        foreach (Match match in DataclassFieldDefaultFactoryRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (isIgnoredName(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                match.Groups["name"].Index,
                "call",
                context ??= originalLine.Trim(),
                lineNumber,
                container,
                "python");
        }
    }

    private static void EmitDataclassFieldMetadataReferences(
        string[] originalLines,
        int lineIndex,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        SymbolRecord? container,
        Func<string, bool> isIgnoredName)
    {
        var originalLine = originalLines[lineIndex];
        if (originalLine.IndexOf("metadata", StringComparison.Ordinal) < 0)
            return;
        if (originalLine.IndexOf('=') < 0 || originalLine.IndexOf('{') < 0)
            return;

        var metadataMatch = DataclassFieldMetadataRegex.Match(originalLine);
        if (!metadataMatch.Success)
            return;

        var currentLineIndex = lineIndex;
        var currentColumn = metadataMatch.Groups["values"].Index;
        var depth = 0;
        var inString = false;
        var quoteChar = '\0';
        var stringStartColumn = -1;

        while (currentLineIndex < originalLines.Length)
        {
            var currentLine = originalLines[currentLineIndex];
            if (currentColumn >= currentLine.Length)
            {
                if (depth <= 0 && !inString)
                    break;

                currentLineIndex++;
                currentColumn = 0;
                continue;
            }

            var ch = currentLine[currentColumn];
            if (inString)
            {
                if (ch == '\\' && currentColumn + 1 < currentLine.Length)
                {
                    currentColumn += 2;
                    continue;
                }

                if (ch == quoteChar)
                {
                    var afterStringColumn = currentColumn + 1;
                    while (afterStringColumn < currentLine.Length && char.IsWhiteSpace(currentLine[afterStringColumn]))
                        afterStringColumn++;

                    if (afterStringColumn < currentLine.Length && currentLine[afterStringColumn] == ':')
                    {
                        var name = currentLine[stringStartColumn..currentColumn].Trim();
                        if (name.Length > 0 && !isIgnoredName(name))
                        {
                            ReferenceExtractor.AddReference(
                                references,
                                seen,
                                fileId,
                                name,
                                stringStartColumn,
                                "annotation",
                                currentLine.Trim(),
                                currentLineIndex + 1,
                                container,
                                "python");
                        }
                    }

                    inString = false;
                    quoteChar = '\0';
                    stringStartColumn = -1;
                    currentColumn++;
                    continue;
                }

                currentColumn++;
                continue;
            }

            if (ch == '#')
                break;

            if (ch is '\'' or '"')
            {
                inString = true;
                quoteChar = ch;
                stringStartColumn = currentColumn + 1;
                currentColumn++;
                continue;
            }

            if (ch is '{' or '[' or '(')
            {
                depth++;
                currentColumn++;
                continue;
            }

            if (ch is '}' or ']' or ')')
            {
                if (depth > 0)
                    depth--;
                currentColumn++;
                if (depth <= 0)
                    break;
                continue;
            }

            currentColumn++;
        }
    }

}
