using System.Text.RegularExpressions;
using Regex = CodeIndex.Indexer.BoundedRegex;

namespace CodeIndex.Indexer;

internal readonly record struct SolutionProjectEntry(
    string Name,
    string ProjectPath,
    string NormalizedProjectPath,
    int LineNumber,
    int NameIndex,
    int PathIndex,
    string Context);

internal static class SolutionFileParser
{
    private static readonly Regex ProjectLineRegex = new(
        @"^\s*Project\(""(?<typeGuid>[^""]+)""\)\s*=\s*""(?<name>[^""]+)""\s*,\s*""(?<path>[^""]+)""\s*,\s*""(?<projectGuid>[^""]+)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SolutionFolderTypeGuids = new(StringComparer.OrdinalIgnoreCase)
    {
        "2150E333-8FDC-42A3-9474-1A3956D46DE8",
        "66A26720-8FB5-11D2-AA7E-00C04F688DDE",
    };

    internal static List<SolutionProjectEntry> ExtractProjects(string[] lines)
    {
        var entries = new List<SolutionProjectEntry>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = ProjectLineRegex.Match(line);
            if (!match.Success)
                continue;

            var typeGuid = NormalizeGuid(match.Groups["typeGuid"].Value);
            if (SolutionFolderTypeGuids.Contains(typeGuid))
                continue;

            var name = match.Groups["name"].Value.Trim();
            var projectPath = match.Groups["path"].Value.Trim();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(projectPath))
                continue;

            var normalizedPath = NormalizeProjectPath(projectPath);
            if (!IsProjectPath(normalizedPath))
                continue;

            entries.Add(new SolutionProjectEntry(
                name,
                projectPath,
                normalizedPath,
                i + 1,
                match.Groups["name"].Index,
                match.Groups["path"].Index,
                line.Trim()));
        }

        return entries;
    }

    private static string NormalizeProjectPath(string projectPath)
        => projectPath.Replace('\\', '/').Trim();

    private static string NormalizeGuid(string guid)
        => guid.Trim().Trim('{', '}');

    private static bool IsProjectPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return false;

        var fileName = Path.GetFileName(projectPath);
        return !string.IsNullOrWhiteSpace(fileName)
            && !string.IsNullOrWhiteSpace(Path.GetExtension(fileName));
    }
}
