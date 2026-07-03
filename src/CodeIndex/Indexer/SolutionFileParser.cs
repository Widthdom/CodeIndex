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

    internal static List<SolutionProjectEntry> ExtractProjects(string[] lines)
    {
        var entries = new List<SolutionProjectEntry>();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var match = ProjectLineRegex.Match(line);
            if (!match.Success)
                continue;

            if (IsSolutionFolderTypeGuid(match.Groups["typeGuid"].ValueSpan))
                continue;

            var nameSpan = match.Groups["name"].ValueSpan.Trim();
            var projectPathSpan = match.Groups["path"].ValueSpan.Trim();
            if (nameSpan.IsEmpty || projectPathSpan.IsEmpty)
                continue;

            var projectPath = projectPathSpan.ToString();
            var normalizedPath = NormalizeProjectPath(projectPath);
            if (!IsProjectPath(normalizedPath))
                continue;

            entries.Add(new SolutionProjectEntry(
                nameSpan.ToString(),
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
        => projectPath.IndexOf('\\') >= 0 ? projectPath.Replace('\\', '/') : projectPath;

    private static bool IsSolutionFolderTypeGuid(ReadOnlySpan<char> guid)
    {
        guid = guid.Trim();
        while (!guid.IsEmpty && guid[0] == '{')
            guid = guid[1..];
        while (!guid.IsEmpty && guid[^1] == '}')
            guid = guid[..^1];

        return guid.Equals("2150E333-8FDC-42A3-9474-1A3956D46DE8", StringComparison.OrdinalIgnoreCase)
            || guid.Equals("66A26720-8FB5-11D2-AA7E-00C04F688DDE", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProjectPath(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            return false;

        var fileName = Path.GetFileName(projectPath);
        return !string.IsNullOrWhiteSpace(fileName)
            && !string.IsNullOrWhiteSpace(Path.GetExtension(fileName));
    }
}
