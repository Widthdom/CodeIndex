using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;
using CodeIndex.Models;

namespace CodeIndex.Indexer;

internal static partial class PythonReferenceExtractor
{
    public static void EmitClassBaseReferences(
        string preparedLine,
        List<ReferenceRecord> references,
        ReferenceDedupeSet seen,
        long fileId,
        string context,
        int lineNumber,
        SymbolRecord? container,
        Func<int, SymbolRecord?> resolveContainerForReference,
        Func<string, bool> isIgnoredName)
    {
        if (preparedLine.IndexOf("class", StringComparison.Ordinal) < 0
            || preparedLine.IndexOf('(') < 0)
        {
            return;
        }

        if (preparedLine.IndexOf("metaclass", StringComparison.Ordinal) >= 0)
        {
            foreach (Match match in ClassMetaclassTypeRegex.Matches(preparedLine))
            {
                var name = match.Groups["name"].Value;
                if (isIgnoredName(name))
                    continue;

                var nameIndex = match.Groups["name"].Index;
                ReferenceExtractor.AddTypeReferenceSegments(
                    references,
                    seen,
                    fileId,
                    name,
                    nameIndex,
                    context,
                    lineNumber,
                    resolveContainerForReference(nameIndex) ?? container,
                    "python");
            }
        }

        if (preparedLine.IndexOf(',') >= 0)
        {
            foreach (Match match in MultipleClassBaseTypesRegex.Matches(preparedLine))
            {
                var typesGroup = match.Groups["types"];
                foreach (Match typeMatch in TypeNameRegex.Matches(typesGroup.Value))
                {
                    var name = typeMatch.Groups["name"].Value;
                    if (isIgnoredName(name))
                        continue;
                    if (IsPythonClassHeaderKeywordArgument(typesGroup.Value, typeMatch.Groups["name"].Index))
                        continue;

                    var nameIndex = typesGroup.Index + typeMatch.Groups["name"].Index;
                    ReferenceExtractor.AddTypeReferenceSegments(
                        references,
                        seen,
                        fileId,
                        name,
                        nameIndex,
                        context,
                        lineNumber,
                        resolveContainerForReference(nameIndex) ?? container,
                        "python");
                }
            }
        }

        foreach (Match match in SingleClassBaseTypeRegex.Matches(preparedLine))
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
                resolveContainerForReference(match.Groups["name"].Index) ?? container,
                "python");
        }
    }

    private static bool IsPythonClassHeaderKeywordArgument(string headerArguments, int nameIndex)
    {
        for (var i = nameIndex - 1; i >= 0; i--)
        {
            var ch = headerArguments[i];
            if (char.IsWhiteSpace(ch))
                continue;
            if (ch == '=')
                return true;
            break;
        }

        for (var i = nameIndex; i < headerArguments.Length; i++)
        {
            var ch = headerArguments[i];
            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '.')
                continue;
            if (char.IsWhiteSpace(ch))
                continue;
            return ch == '=';
        }

        return false;
    }

}
