using System.Text.RegularExpressions;
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
        Action<string, int> addDslReference)
    {
        foreach (Match match in BlockCallRegex.Matches(preparedLine))
            addDslReference(match.Groups["name"].Value, match.Groups["name"].Index);

        foreach (Match match in CommandCallRegex.Matches(preparedLine))
            addDslReference(match.Groups["name"].Value, match.Groups["name"].Index);
    }
}
