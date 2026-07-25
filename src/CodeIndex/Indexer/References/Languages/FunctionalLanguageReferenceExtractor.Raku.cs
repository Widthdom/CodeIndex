using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitRakuReferences(
        long fileId,
        string line,
        string context,
        int lineNumber,
        SymbolRecord? definition,
        SymbolRecord? typeDefinition,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen)
    {
        var importMatch = RakuImportRegex.Match(line);
        if (importMatch.Success)
        {
            AddFunctionalReference(references, seen, fileId, importMatch.Groups["name"], "import", context, lineNumber, container, "raku");
            if (importMatch.Groups["angleAlias"].Success || importMatch.Groups["alias"].Success)
                AddFunctionalReference(references, seen, fileId, importMatch.Groups["name"], "alias", context, lineNumber, container, "raku");
        }

        if (typeDefinition != null)
        {
            foreach (Match match in RakuTypeRelationRegex.Matches(line))
                AddFunctionalReference(references, seen, fileId, match.Groups["name"], "type_reference", context, lineNumber, typeDefinition, "raku");
        }
        foreach (Match match in RakuReturnTypeRegex.Matches(line))
            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "type_reference", context, lineNumber, container, "raku");
        if (typeDefinition != null)
            return;

        var qualifiedCallSpans = new List<(int Start, int End)>();
        foreach (Match match in RakuQualifiedCallRegex.Matches(line))
        {
            qualifiedCallSpans.Add((match.Index, match.Index + match.Length));
            AddFunctionalReference(references, seen, fileId, match.Groups["module"], "reference", context, lineNumber, container, "raku");
            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "raku");
        }
        foreach (Match match in RakuMethodCallRegex.Matches(line))
        {
            qualifiedCallSpans.Add((match.Index, match.Index + match.Length));
            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "raku");
        }

        var skippedDefinition = false;
        foreach (Match match in RakuBareCallRegex.Matches(line))
        {
            if (qualifiedCallSpans.Any(span => match.Index >= span.Start && match.Index < span.End))
                continue;

            var name = match.Groups["name"].Value;
            if (RakuIgnoredCalls.Contains(name))
                continue;

            if (!skippedDefinition
                && definition != null
                && string.Equals(definition.Name, name, StringComparison.Ordinal))
            {
                skippedDefinition = true;
                continue;
            }

            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "raku");
        }
    }

    private static void AddFunctionalReference(
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        Group group,
        string referenceKind,
        string context,
        int lineNumber,
        SymbolRecord? container,
        string language)
    {
        if (!group.Success || ReferenceLimitReached(references))
            return;

        AddReference(
            references,
            seen,
            fileId,
            group.Value,
            group.Index,
            referenceKind,
            context,
            lineNumber,
            container,
            language);
    }
}
