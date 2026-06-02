using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static class GoReferenceExtractor
{
    private static readonly Regex GoroutineCallRegex = new(
        @"\bgo\s+(?<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChannelSendRegex = new(
        @"(?<!<)(?<name>[A-Za-z_]\w*)\s*<-",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ChannelReceiveRegex = new(
        @"(?<!<)<-\s*(?<name>[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool[] BuildImportBlockLineMap(IReadOnlyList<string> originalLines)
        => LanguageReferenceExtractionSupport.BuildGoImportBlockLineMap(originalLines);

    public static void EmitConcurrencyReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in GoroutineCallRegex.Matches(preparedLine))
        {
            var group = match.Groups["name"];
            var rawName = group.Value;
            var dot = rawName.LastIndexOf('.');
            var name = dot >= 0 && dot + 1 < rawName.Length ? rawName[(dot + 1)..] : rawName;
            var nameIndex = dot >= 0 && dot + 1 < rawName.Length ? group.Index + dot + 1 : group.Index;
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameIndex,
                "goroutine_spawn",
                context,
                lineNumber,
                resolveContainerForColumn(nameIndex));
        }

        foreach (Match match in ChannelSendRegex.Matches(preparedLine))
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                match.Groups["name"].Value,
                match.Groups["name"].Index,
                "channel_send",
                context,
                lineNumber,
                resolveContainerForColumn(match.Groups["name"].Index));
        }

        foreach (Match match in ChannelReceiveRegex.Matches(preparedLine))
        {
            if (!IsGoChannelReceiveArrow(preparedLine, match.Index))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                match.Groups["name"].Value,
                match.Groups["name"].Index,
                "channel_receive",
                context,
                lineNumber,
                resolveContainerForColumn(match.Groups["name"].Index));
        }
    }

    private static bool IsGoChannelReceiveArrow(string line, int arrowIndex)
    {
        var cursor = arrowIndex - 1;
        while (cursor >= 0 && char.IsWhiteSpace(line[cursor]))
            cursor--;
        if (cursor < 0)
            return true;

        if (!IsGoExpressionEnd(line[cursor]))
            return true;

        var tokenEnd = cursor + 1;
        while (cursor >= 0 && IsGoIdentifierPart(line[cursor]))
            cursor--;
        var token = cursor + 1 < tokenEnd ? line[(cursor + 1)..tokenEnd] : string.Empty;
        return token is "case" or "return" or "select" or "if" or "for" or "switch";
    }

    private static bool IsGoExpressionEnd(char ch)
        => ch == '_' || char.IsLetterOrDigit(ch) || ch is ')' or ']';

    private static bool IsGoIdentifierPart(char ch)
        => ch == '_' || char.IsLetterOrDigit(ch);

    public static void EmitTypePositionReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn,
        bool isImportBlockLine)
    {
        LanguageReferenceExtractionSupport.EmitTypePositionReferences(
            "go",
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            container: null,
            isGoImportBlockLine: isImportBlockLine);
    }
}
