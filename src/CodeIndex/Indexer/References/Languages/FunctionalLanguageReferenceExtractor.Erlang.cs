using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitErlangReferences(
        long fileId,
        string line,
        string context,
        int lineNumber,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        FunctionalReferenceState state)
    {
        AddFunctionalMatchReference(ErlangImportRegex.Match(line), "import");
        AddFunctionalMatchReference(ErlangBehaviourRegex.Match(line), "type_reference");
        if (ErlangSpecificationAttributeRegex.IsMatch(line))
            state.ErlangSpecificationMode = true;
        if (state.ErlangSpecificationMode)
        {
            if (TrimmedFunctionalLineEndsWith(line, '.'))
                state.ErlangSpecificationMode = false;
            return;
        }

        var quotedAtomSpans = GetErlangQuotedAtomSpans(line);
        List<(int Start, int End)>? remoteCallSpans = null;
        foreach (Match match in ErlangRemoteCallRegex.Matches(line))
        {
            if (IsInsideQuotedAtom(match.Index))
                continue;
            (remoteCallSpans ??= []).Add(
                (match.Index, match.Index + match.Length));
            AddFunctionalReference(references, seen, fileId, match.Groups["module"], "reference", context, lineNumber, container, "erlang");
            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "erlang");
        }

        var definitionMatch = ErlangFunctionDefinitionRegex.Match(line);
        foreach (Match match in ErlangLocalCallRegex.Matches(line))
        {
            if (ContainsFunctionalSpan(remoteCallSpans, match.Index))
                continue;
            if (IsInsideQuotedAtom(match.Groups["name"].Index))
                continue;

            var name = match.Groups["name"].Value;
            if (ErlangIgnoredCalls.Contains(name))
                continue;

            if (definitionMatch.Success
                && match.Groups["name"].Index == definitionMatch.Groups["name"].Index)
            {
                continue;
            }

            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "erlang");
        }

        void AddFunctionalMatchReference(Match match, string kind)
        {
            if (match.Success)
                AddFunctionalReference(references, seen, fileId, match.Groups["name"], kind, context, lineNumber, container, "erlang");
        }

        bool IsInsideQuotedAtom(int index)
            => ContainsFunctionalSpanInterior(quotedAtomSpans, index);
    }

    private static List<(int Start, int End)>? GetErlangQuotedAtomSpans(string line)
    {
        List<(int Start, int End)>? spans = null;
        for (var index = 0; index < line.Length; index++)
        {
            if (line[index] != '\'')
                continue;

            var start = index;
            for (index++; index < line.Length; index++)
            {
                if (line[index] == '\\' && index + 1 < line.Length)
                {
                    index++;
                    continue;
                }

                if (line[index] != '\'')
                    continue;

                (spans ??= []).Add((start, index + 1));
                break;
            }
        }

        return spans;
    }

}
