using CodeIndex.Models;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class CppReferenceExtractor
{
    private static readonly Regex FriendTypeRegex = new(
        @"\bfriend\s+(?:class|struct|union|typename|enum(?:\s+class)?)\s+(?<name>(?:[A-Za-z_]\w*::)*[A-Za-z_]\w*)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FriendFunctionRegex = new(
        @"\bfriend\s+(?!(?:class|struct|union|typename|enum)\b)[^;()]*?\b(?<name>(?:[A-Za-z_]\w*::)*(?:[A-Za-z_]\w*|operator\s*(?:new\[\]|delete\[\]|new|delete|\[\]|[^\s(]+)))(?:\s*<[^>]+>)?\s*\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void EmitTypePositionReferences(
        string preparedLine,
        string originalLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        EmitFriendReferences(preparedLine, references, seen, fileId, context, lineNumber, resolveContainerForColumn);

        LanguageReferenceExtractionSupport.EmitTypePositionReferences(
            "cpp",
            preparedLine,
            originalLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            resolveContainerForColumn,
            container: null);
    }

    private static void EmitFriendReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        Func<int, SymbolRecord?> resolveContainerForColumn)
    {
        foreach (Match match in FriendTypeRegex.Matches(preparedLine))
            AddFriendReference(match.Groups["name"]);

        foreach (Match match in FriendFunctionRegex.Matches(preparedLine))
            AddFriendReference(match.Groups["name"]);

        void AddFriendReference(Group group)
        {
            var name = LastQualifiedSegment(group.Value);
            var offset = group.Value.LastIndexOf(name, StringComparison.Ordinal);
            var nameIndex = group.Index + Math.Max(0, offset);
            ReferenceExtractor.AddReference(references, seen, fileId, name, nameIndex, "friend", context, lineNumber, resolveContainerForColumn(nameIndex));
        }
    }

    private static string LastQualifiedSegment(string value)
    {
        var text = value.Trim();
        var qualifierIndex = text.LastIndexOf("::", StringComparison.Ordinal);
        var leaf = qualifierIndex >= 0 ? text[(qualifierIndex + 2)..].Trim() : text;
        if (!leaf.StartsWith("operator", StringComparison.Ordinal))
        {
            var genericIndex = text.IndexOf('<');
            if (genericIndex >= 0)
                text = text[..genericIndex].TrimEnd();
        }

        return qualifierIndex >= 0 ? text[(qualifierIndex + 2)..].Trim() : text;
    }
}
