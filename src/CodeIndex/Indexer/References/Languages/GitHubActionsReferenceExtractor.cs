using System.Text.RegularExpressions;
using CodeIndex.Models;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal static class GitHubActionsReferenceExtractor
{
    private static readonly Regex MappingRegex = new(
        @"^(?<indent> *)(?:-\s+)?(?<key>[A-Za-z0-9_.-]+):(?:\s*(?<value>.*))?$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex LocalPathRegex = new(
        @"(?<![A-Za-z0-9_.-])(?<path>(?:\./)?[A-Za-z0-9_.-]+(?:/[A-Za-z0-9_.-]+)+\.(?:sh|ps1|csproj|fsproj|vbproj|sln))(?![A-Za-z0-9_.-])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(2));

    internal static List<ReferenceRecord> Extract(
        long fileId,
        string[] lines,
        IReadOnlyList<SymbolRecord> symbols,
        int? maxReferenceCount)
    {
        var references = ReferenceExtractor.CreateReferenceList(maxReferenceCount, Math.Min(lines.Length, 64));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var jobsIndent = -1;
        var currentJobIndent = -1;
        string? currentJob = null;
        SymbolRecord? currentJobSymbol = null;
        int? runIndent = null;

        for (var index = 0; index < lines.Length && !ReferenceExtractor.ReferenceLimitReached(references); index++)
        {
            var line = lines[index];
            var indent = CountLeadingSpaces(line);
            var mapping = MappingRegex.Match(line);

            if (runIndent.HasValue && indent > runIndent.Value)
                EmitLocalPaths(fileId, line, index + 1, currentJobSymbol, references, seen);
            else if (runIndent.HasValue)
                runIndent = null;

            if (!mapping.Success)
                continue;

            var key = mapping.Groups["key"].Value;
            var value = StripValue(mapping.Groups["value"].Value);
            if (key == "jobs")
            {
                jobsIndent = indent;
                currentJob = null;
                continue;
            }

            if (jobsIndent >= 0 && indent <= jobsIndent)
            {
                jobsIndent = -1;
                currentJob = null;
                currentJobSymbol = null;
                currentJobIndent = -1;
                continue;
            }

            if (jobsIndent >= 0 && indent > jobsIndent && (currentJob == null || indent <= currentJobIndent))
            {
                currentJob = key;
                currentJobIndent = indent;
                currentJobSymbol = symbols.FirstOrDefault(symbol => symbol.Name == $"jobs.{currentJob}");
                continue;
            }

            if (currentJob == null || indent <= currentJobIndent)
                continue;

            if (key == "needs")
            {
                foreach (var need in value.Trim('[', ']').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var target = need.Trim('\'', '"');
                    if (target.Length > 0)
                        Add(fileId, target, line.IndexOf(target, StringComparison.Ordinal), "call", line, index + 1, currentJobSymbol, references, seen);
                }
            }
            else if (key == "uses")
            {
                var at = value.LastIndexOf('@');
                if (at > 0 && !value.StartsWith("./", StringComparison.Ordinal))
                    Add(fileId, value[..at], line.IndexOf(value, StringComparison.Ordinal), "import", line, index + 1, currentJobSymbol, references, seen);
            }
            else if (key == "run")
            {
                EmitLocalPaths(fileId, value, index + 1, currentJobSymbol, references, seen, line.IndexOf(value, StringComparison.Ordinal));
                if (value.Length == 0 || value[0] is '|' or '>')
                    runIndent = indent;
            }
        }

        return references;
    }

    private static void EmitLocalPaths(
        long fileId,
        string text,
        int lineNumber,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        HashSet<string> seen,
        int baseIndex = 0)
    {
        foreach (Match match in LocalPathRegex.Matches(text))
        {
            var rawPath = match.Groups["path"].Value;
            var path = rawPath.StartsWith("./", StringComparison.Ordinal) ? rawPath[2..] : rawPath;
            Add(fileId, path, Math.Max(0, baseIndex) + match.Groups["path"].Index, "project_reference", text, lineNumber, container, references, seen);
        }
    }

    private static void Add(
        long fileId,
        string name,
        int index,
        string kind,
        string context,
        int line,
        SymbolRecord? container,
        List<ReferenceRecord> references,
        HashSet<string> seen) =>
        ReferenceExtractor.AddReference(references, seen, fileId, name, Math.Max(0, index), kind, context, line, container, "yaml");

    private static string StripValue(string value)
    {
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return (comment >= 0 ? value[..comment] : value).Trim().Trim('\'', '"');
    }

    private static int CountLeadingSpaces(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
            count++;
        return count;
    }
}
