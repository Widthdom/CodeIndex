using CodeIndex.Models;
using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class ElixirReferenceExtractor
{
    private static readonly Regex PipeCallRegex = new(
        @"\|>\s*(?:(?:[A-Z_]\w*(?:\.[A-Z_]\w*)*)\.)?(?<name>[a-z_]\w*[!?]?)\s*(?=\(|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DefimplRegex = new(
        @"^\s*defimpl\s+(?<protocol>[\w.]+)\s*,\s*for:\s*(?<types>\[[^\]]+\]|[\w.{}]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DefimplTypeRegex = new(
        @"[\w.{}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> IgnoredPipeCallNames = new(StringComparer.Ordinal)
    {
        "and",
        "catch",
        "do",
        "else",
        "end",
        "fn",
        "in",
        "not",
        "or",
        "rescue",
        "when",
    };

    public static void EmitTypePositionReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        EmitDefimplReferences(preparedLine, references, seen, fileId, context, lineNumber, container);

        LanguageReferenceExtractionSupport.EmitTypePositionReferences(
            "elixir",
            preparedLine,
            preparedLine,
            references,
            seen,
            fileId,
            context,
            lineNumber,
            _ => container,
            container);
    }

    private static void EmitDefimplReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container)
    {
        var match = DefimplRegex.Match(preparedLine);
        if (!match.Success)
            return;

        AddDefimplGroupReference(match.Groups["protocol"]);

        var typesGroup = match.Groups["types"];
        foreach (Match typeMatch in DefimplTypeRegex.Matches(typesGroup.Value))
            AddDefimplTypeReference(typeMatch.Groups[0], typesGroup.Index + typeMatch.Index);

        void AddDefimplGroupReference(Group group)
            => AddDefimplTypeReference(group, group.Index);

        void AddDefimplTypeReference(Group group, int column)
        {
            var name = group.Value.Trim();
            if (name.Length == 0)
                return;

            var key = $"type_reference:{name}:{lineNumber}:{column}";
            if (!seen.Add(key))
                return;

            references.Add(new ReferenceRecord
            {
                FileId = fileId,
                SymbolName = name,
                ReferenceKind = "type_reference",
                Line = lineNumber,
                Column = column,
                Context = context.Trim(),
                ContainerKind = container?.Kind,
                ContainerName = container?.Name,
            });
        }
    }

    public static void EmitAdditionalCallReferences(
        string preparedLine,
        Action<string, int> addCallLikeReference,
        IReadOnlySet<string>? definitionNames)
    {
        foreach (Match match in PipeCallRegex.Matches(preparedLine))
        {
            var name = match.Groups["name"].Value;
            if (!IgnoredPipeCallNames.Contains(name))
                addCallLikeReference(name, match.Groups["name"].Index);
        }

        LanguageReferenceExtractionSupport.EmitAdditionalCallReferences(
            "elixir",
            preparedLine,
            preparedLine,
            addCallLikeReference,
            [],
            [],
            0,
            string.Empty,
            0,
            _ => null,
            definitionNames);
    }
}
