using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitOcamlReferences(
        long fileId,
        string line,
        string context,
        int lineNumber,
        SymbolRecord? definition,
        SymbolRecord? typeDefinition,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        FunctionalReferenceState state)
    {
        if (state.OcamlTypeDeclarationMode
            && Regex.IsMatch(
                line,
                @"^\s*(?:let|module|class|exception|external|open|include)\b",
                RegexOptions.CultureInvariant,
                ExtractionRegexTimeout))
        {
            state.OcamlTypeDeclarationMode = false;
            state.OcamlActiveTypeDefinition = null;
        }

        var startsTypeDeclaration = Regex.IsMatch(
            line,
            @"^\s*type\b",
            RegexOptions.CultureInvariant,
            ExtractionRegexTimeout);
        if (startsTypeDeclaration)
        {
            state.OcamlTypeDeclarationMode = true;
            state.OcamlActiveTypeDefinition = typeDefinition;
        }

        var typeReferenceSpans = new List<(int Start, int End)>();
        AddMatch(OcamlImportRegex.Match(line), "import");
        AddMatch(OcamlModuleAliasRegex.Match(line), "alias");
        var typeAliasTarget = OcamlTypeAliasTargetRegex.Match(line);
        if (typeAliasTarget.Success)
        {
            AddOcamlTypeReference(typeAliasTarget.Groups["name"]);
        }
        foreach (Match match in OcamlTypeReferenceRegex.Matches(line))
            AddOcamlTypeReference(match.Groups["name"]);
        if (state.OcamlTypeDeclarationMode)
            return;

        if (Regex.IsMatch(
                line,
                @"^\s*(?:module|type|class|open|include|val|external)\b",
                RegexOptions.CultureInvariant,
                ExtractionRegexTimeout))
        {
            return;
        }

        var qualifiedCallSpans = new List<(int Start, int End)>(typeReferenceSpans);
        foreach (Match match in OcamlQualifiedCallRegex.Matches(line))
        {
            if (qualifiedCallSpans.Any(span => RangesOverlap(span.Start, span.End, match.Index, match.Index + match.Length)))
                continue;
            qualifiedCallSpans.Add((match.Index, match.Index + match.Length));
            AddFunctionalReference(references, seen, fileId, match.Groups["module"], "reference", context, lineNumber, container, "ocaml");
            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "ocaml");
        }

        var skippedDefinition = false;
        foreach (Match match in OcamlBareCallRegex.Matches(line))
        {
            if (qualifiedCallSpans.Any(span => match.Index >= span.Start && match.Index < span.End))
                continue;

            var name = match.Groups["name"].Value;
            if (OcamlIgnoredCalls.Contains(name))
                continue;

            if (!skippedDefinition
                && definition != null
                && string.Equals(definition.Name, name, StringComparison.Ordinal))
            {
                skippedDefinition = true;
                continue;
            }

            AddFunctionalReference(references, seen, fileId, match.Groups["name"], "call", context, lineNumber, container, "ocaml");
        }

        void AddMatch(Match match, string kind)
        {
            if (match.Success)
                AddFunctionalReference(references, seen, fileId, match.Groups["name"], kind, context, lineNumber, container, "ocaml");
        }

        void AddOcamlTypeReference(Group group)
        {
            if (!group.Success)
                return;

            typeReferenceSpans.Add((group.Index, group.Index + group.Length));
            if (!OcamlIgnoredTypeReferences.Contains(group.Value))
            {
                AddFunctionalReference(
                    references,
                    seen,
                    fileId,
                    group,
                    "type_reference",
                    context,
                    lineNumber,
                    typeDefinition ?? state.OcamlActiveTypeDefinition ?? container,
                    "ocaml");
            }
        }

        static bool RangesOverlap(int leftStart, int leftEnd, int rightStart, int rightEnd)
            => leftStart < rightEnd && rightStart < leftEnd;
    }

}
