using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class RReferenceExtractor
{
    public static void EmitDollarMemberReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        if (preparedLine.IndexOf('$') < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(
                     DollarMemberReferenceRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var backtickReceiverGroup = match.Groups["backtickReceiver"];
            var receiverGroup = backtickReceiverGroup.Success ? backtickReceiverGroup : match.Groups["receiver"];
            var receiver = receiverGroup.Value;
            var backtickNameGroup = match.Groups["backtickName"];
            var nameGroup = backtickNameGroup.Success ? backtickNameGroup : match.Groups["name"];
            var name = nameGroup.Value;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                $"{receiver}${name}",
                receiverGroup.Index,
                "reference",
                context,
                lineNumber,
                container);

            if (definitionNames != null && definitionNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitBracketMemberReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        if (!preparedLine.Contains("[[", StringComparison.Ordinal))
            return;

        if (!ContainsRQuotedArgument(originalLine))
            return;

        var line = StripRNamespaceDirectiveComment(originalLine);
        foreach (Match match in Regex.EnumerateMatches(
                     BracketMemberReferenceRegex,
                     line))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var backtickReceiverGroup = match.Groups["backtickReceiver"];
            var receiverGroup = backtickReceiverGroup.Success ? backtickReceiverGroup : match.Groups["receiver"];
            var receiver = receiverGroup.Value;
            var nameGroup = match.Groups["name"];
            var name = nameGroup.Value;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                $"{receiver}${name}",
                receiverGroup.Index,
                "reference",
                context,
                lineNumber,
                container);

            if (definitionNames != null && definitionNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    public static void EmitSlotMemberReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        HashSet<string>? definitionNames)
    {
        if (preparedLine.IndexOf('@') < 0)
            return;

        foreach (Match match in Regex.EnumerateMatches(
                     SlotMemberReferenceRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                break;

            var backtickReceiverGroup = match.Groups["backtickReceiver"];
            var receiverGroup = backtickReceiverGroup.Success ? backtickReceiverGroup : match.Groups["receiver"];
            var receiver = receiverGroup.Value;
            var backtickNameGroup = match.Groups["backtickName"];
            var nameGroup = backtickNameGroup.Success ? backtickNameGroup : match.Groups["name"];
            var name = nameGroup.Value;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                $"{receiver}@{name}",
                receiverGroup.Index,
                "reference",
                context,
                lineNumber,
                container);

            if (definitionNames != null && definitionNames.Contains(name))
                continue;

            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                name,
                nameGroup.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }

    private static IEnumerable<(string Name, int Index)> EnumerateNamespaceDirectiveNames(string value, int baseIndex)
    {
        foreach (Match match in Regex.EnumerateMatches(
                     NamespaceDirectiveNameRegex,
                     value))
        {
            var backtickNameGroup = match.Groups["backtickName"];
            var nameGroup = backtickNameGroup.Success ? backtickNameGroup : match.Groups["name"];
            yield return (nameGroup.Value, baseIndex + nameGroup.Index + (backtickNameGroup.Success ? 1 : 0));
        }
    }

    private static (string Name, int Index)? GetNamespaceDirectiveToken(Match match, params string[] groupNames)
    {
        foreach (var groupName in groupNames)
        {
            var group = match.Groups[groupName];
            if (group.Success)
                return (group.Value, group.Index);
        }

        return null;
    }

    private static string StripRNamespaceDirectiveComment(string line)
    {
        var inBacktickIdentifier = false;
        var quote = '\0';
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (quote != '\0')
            {
                if (ch == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (ch == quote)
                    quote = '\0';
                continue;
            }

            if (inBacktickIdentifier)
            {
                if (ch == '\\' && i + 1 < line.Length)
                {
                    i++;
                    continue;
                }

                if (ch == '`')
                    inBacktickIdentifier = false;
                continue;
            }

            if (ch is '\'' or '"')
            {
                quote = ch;
                continue;
            }

            if (ch == '`')
            {
                inBacktickIdentifier = true;
                continue;
            }

            if (ch == '#')
                return line[..i];
        }

        return line;
    }
}
