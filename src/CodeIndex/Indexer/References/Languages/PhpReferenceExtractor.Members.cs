using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class PhpReferenceExtractor
{
    private static bool IsPhpCallAfterStaticMember(string line, int index)
    {
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        return index < line.Length && line[index] == '(';
    }

    private static bool IsPhpBuiltinTypeName(string name)
        => !name.Contains('\\', StringComparison.Ordinal)
           && BuiltinTypeNames.Contains(name);

    private static void AddPhpTypeReferenceFromQualifiedName(
        Capture nameGroup,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
        => AddPhpTypeReferenceFromName(
            nameGroup.Value,
            nameGroup.Index,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container);

    private static void AddPhpTypeReferenceFromName(
        string rawName,
        int nameIndex,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        int? shortNameIndexOverride = null)
        => AddPhpReferenceFromName(
            rawName,
            nameIndex,
            "type_reference",
            references,
            seen,
            fileId,
            context,
            lineNumber,
            container,
            shortNameIndexOverride);

    private static void AddPhpReferenceFromName(
        string rawName,
        int nameIndex,
        string referenceKind,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        int? shortNameIndexOverride = null)
    {
        var leadingBackslashCount = 0;
        while (leadingBackslashCount < rawName.Length && rawName[leadingBackslashCount] == '\\')
            leadingBackslashCount++;
        if (leadingBackslashCount == rawName.Length)
            return;

        var trimmedName = rawName.Substring(leadingBackslashCount);
        var qualifiedNameIndex = nameIndex + leadingBackslashCount;
        if (trimmedName.Contains('\\', StringComparison.Ordinal))
        {
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                trimmedName,
                qualifiedNameIndex,
                referenceKind,
                context,
                lineNumber,
                container);
        }

        var shortNameStart = trimmedName.LastIndexOf('\\') + 1;
        var shortName = trimmedName[shortNameStart..];
        if (shortName.Length == 0)
            return;

        ReferenceExtractor.AddReference(
            references,
            seen,
            fileId,
            shortName,
            shortNameIndexOverride ?? qualifiedNameIndex + shortNameStart,
            referenceKind,
            context,
            lineNumber,
            container);
    }

    public static void EmitObjectMemberAccessReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        if (preparedLine.IndexOf("->", StringComparison.Ordinal) < 0)
        {
            return;
        }

        foreach (Match match in ReferenceExtractor.EnumerateReferenceMatches(
                     ObjectMemberAccessRegex,
                     preparedLine,
                     references))
        {
            var nameGroup = match.Groups["name"];
            ReferenceExtractor.AddReference(
                references,
                seen,
                fileId,
                nameGroup.Value,
                nameGroup.Index,
                "reference",
                context,
                lineNumber,
                container);
        }
    }
}
