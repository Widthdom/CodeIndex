using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class GradleReferenceExtractor
{
    // Gradle/Groovy block and command-style DSL calls such as `plugins { ... }`,
    // `task buildJar(type: Jar) { ... }`, `apply plugin: 'java'`, and `println 'x'`
    // do not use the shared `foo(...)` shape. Keep the matcher narrow to known DSL
    // call forms so ordinary assignment lines stay out of the graph.
    private static readonly Regex BlockCallRegex = new(
        @"(?<![\w$@])(?<name>[A-Za-z_]\w*)\b(?:\s+[^\r\n{]+?)?\s*\{",
        RegexOptions.Compiled);

    private static readonly Regex CommandCallRegex = new(
        @"(?<![\w$@])(?<name>[A-Za-z_]\w*)\s+(?=(?:['""]|[_\p{L}]|\d|\.|:))",
        RegexOptions.Compiled);

    public static void EmitDslCallReferences(
        string preparedLine,
        Action<string, int> addDslReference,
        List<ReferenceRecord> references)
    {
        if (preparedLine.IndexOf('{') >= 0)
        {
            foreach (Match match in Regex.EnumerateMatches(
                         BlockCallRegex,
                         preparedLine))
            {
                if (ReferenceExtractor.ReferenceLimitReached(references))
                    return;

                addDslReference(match.Groups["name"].Value, match.Groups["name"].Index);
            }
        }

        if (!ContainsWhitespace(preparedLine))
            return;

        foreach (Match match in Regex.EnumerateMatches(
                     CommandCallRegex,
                     preparedLine))
        {
            if (ReferenceExtractor.ReferenceLimitReached(references))
                return;

            addDslReference(match.Groups["name"].Value, match.Groups["name"].Index);
        }
    }

    private static bool ContainsWhitespace(string line)
    {
        foreach (var ch in line)
        {
            if (char.IsWhiteSpace(ch))
                return true;
        }

        return false;
    }
}
