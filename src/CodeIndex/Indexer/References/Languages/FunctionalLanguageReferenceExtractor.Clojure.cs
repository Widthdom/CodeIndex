using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Indexer.Extensibility;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

public static partial class ReferenceExtractor
{
    private static void EmitClojureReferences(
        long fileId,
        string line,
        string context,
        int lineNumber,
        SymbolRecord? typeDefinition,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        FunctionalReferenceState state)
    {
        if (line.Contains(":require", StringComparison.Ordinal))
            state.ClojureRequireMode = true;

        if (state.ClojureRequireMode)
        {
            EmitClojureRequireEntries();
            if (line.Contains(')'))
            {
                state.ClojureRequireMode = false;
                state.ClojureRequireBracketDepth = 0;
            }
        }

        var relationMatch = ClojureTypeRelationRegex.Match(line);
        if (relationMatch.Success)
        {
            state.ClojureTypeBodyMode = true;
            state.ClojureTypeBodyBaseDepth = state.ClojureParenDepth;
            state.ClojureActiveTypeDefinition = typeDefinition;
            var typeContainer = typeDefinition ?? container;
            var types = relationMatch.Groups["types"];
            foreach (Match typeMatch in Regex.Matches(
                         types.Value,
                         @"(?<name>[A-Z][\w.*+!?<>=-]*)",
                         RegexOptions.CultureInvariant,
                         ExtractionRegexTimeout))
            {
                AddReference(
                    references,
                    seen,
                    fileId,
                    typeMatch.Groups["name"].Value,
                    types.Index + typeMatch.Groups["name"].Index,
                    "type_reference",
                    context,
                    lineNumber,
                    typeContainer,
                    "clojure");
            }
        }

        var isProtocolHeader = Regex.IsMatch(
            line,
            @"^\s*\(\s*defprotocol\b",
            RegexOptions.CultureInvariant,
            ExtractionRegexTimeout);
        if (isProtocolHeader)
        {
            state.ClojureProtocolMode = true;
            state.ClojureProtocolBaseDepth = state.ClojureParenDepth;
        }

        if (state.ClojureProtocolMode
            || Regex.IsMatch(
                line,
                @"^\s*\(\s*(?:ns|defrecord|deftype|extend-type)\b",
                RegexOptions.CultureInvariant,
                ExtractionRegexTimeout))
        {
            return;
        }

        var callLine = MaskClojureSuppressedForms(line, state);
        var methodHeader = state.ClojureTypeBodyMode
                           && state.ClojureParenDepth == state.ClojureTypeBodyBaseDepth + 1
            ? ClojureCallHeadRegex.Match(callLine)
            : Match.Empty;
        foreach (Match match in ClojureCallHeadRegex.Matches(callLine))
        {
            var fullName = match.Groups["name"].Value;
            var separator = fullName.LastIndexOf('/');
            var name = separator >= 0 ? fullName[(separator + 1)..] : fullName;
            if (ClojureIgnoredCallHeads.Contains(name))
                continue;
            if (methodHeader.Success
                && match.Groups["name"].Index == methodHeader.Groups["name"].Index)
            {
                continue;
            }

            AddReference(
                references,
                seen,
                fileId,
                name,
                match.Groups["name"].Index + Math.Max(0, separator + 1),
                "call",
                context,
                lineNumber,
                state.ClojureActiveTypeDefinition ?? container,
                "clojure");
        }

        void EmitClojureRequireEntries()
        {
            for (var index = 0; index < line.Length; index++)
            {
                if (line[index] == '[')
                {
                    if (state.ClojureRequireBracketDepth == 0)
                    {
                        var match = ClojureRequireEntryRegex.Match(line, index);
                        if (match.Success && match.Index == index)
                        {
                            AddFunctionalReference(
                                references,
                                seen,
                                fileId,
                                match.Groups["name"],
                                "import",
                                context,
                                lineNumber,
                                container,
                                "clojure");
                            if (match.Groups["alias"].Success)
                            {
                                AddFunctionalReference(
                                    references,
                                    seen,
                                    fileId,
                                    match.Groups["name"],
                                    "alias",
                                    context,
                                    lineNumber,
                                    container,
                                    "clojure");
                            }
                        }
                    }

                    state.ClojureRequireBracketDepth++;
                }
                else if (line[index] == ']' && state.ClojureRequireBracketDepth > 0)
                {
                    state.ClojureRequireBracketDepth--;
                }
            }
        }
    }

    private static string MaskClojureSuppressedForms(string line, FunctionalReferenceState state)
    {
        var masked = line.ToCharArray();
        for (var index = 0; index < masked.Length; index++)
        {
            if (state.ClojureSuppressedFormDepth > 0)
            {
                masked[index] = ' ';
                UpdateDepth(line[index]);
                continue;
            }

            var isNamedSuppressedForm =
                (line.IndexOf("(quote", index, StringComparison.Ordinal) == index
                 && IsClojureFormBoundary(line, index + 6))
                || (line.IndexOf("(comment", index, StringComparison.Ordinal) == index
                    && IsClojureFormBoundary(line, index + 8));
            if (isNamedSuppressedForm)
            {
                state.ClojureSuppressedFormDepth = 1;
                masked[index] = ' ';
                continue;
            }

            var prefixLength = line[index] == '\''
                ? 1
                : line[index] == '#' && index + 1 < line.Length && line[index + 1] == '_'
                    ? 2
                    : 0;
            if (prefixLength == 0 || !IsClojureReaderPrefixPosition(line, index))
                continue;

            var formStart = index + prefixLength;
            while (formStart < line.Length && char.IsWhiteSpace(line[formStart]))
                formStart++;
            Array.Fill(masked, ' ', index, formStart - index);
            if (formStart >= line.Length)
                continue;

            if (line[formStart] is '(' or '[' or '{')
            {
                state.ClojureSuppressedFormDepth = 1;
                masked[formStart] = ' ';
                index = formStart;
                continue;
            }

            var tokenEnd = formStart;
            while (tokenEnd < line.Length
                   && !char.IsWhiteSpace(line[tokenEnd])
                   && line[tokenEnd] is not ('(' or ')' or '[' or ']' or '{' or '}' or ',' or ';'))
            {
                masked[tokenEnd++] = ' ';
            }
            index = Math.Max(index, tokenEnd - 1);
        }

        return new string(masked);

        void UpdateDepth(char character)
        {
            if (character is '(' or '[' or '{')
                state.ClojureSuppressedFormDepth++;
            else if (character is ')' or ']' or '}')
                state.ClojureSuppressedFormDepth--;
        }
    }

    private static bool IsClojureReaderPrefixPosition(string line, int index)
        => index == 0
           || char.IsWhiteSpace(line[index - 1])
           || line[index - 1] is '(' or '[' or '{' or ',';

    private static bool IsClojureFormBoundary(string line, int index)
        => index >= line.Length
           || char.IsWhiteSpace(line[index])
           || line[index] is '(' or '[' or '{' or ')' or ']' or '}';

}
