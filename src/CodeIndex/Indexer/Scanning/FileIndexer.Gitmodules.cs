using CodeIndex.Models;

namespace CodeIndex.Indexer;

public partial class FileIndexer
{
    // Parse <ignoreRuleRoot>/.gitmodules and return submodule working-tree paths (and
    // their ancestor directories) relative to projectRoot. Submodules outside projectRoot
    // are dropped silently. Absent or unreadable .gitmodules yields empty sets so callers
    // see the same shape as a non-submodule repository.
    // <ignoreRuleRoot>/.gitmodules を解析し、projectRoot 相対の submodule ワークツリーパスと
    // その祖先ディレクトリを返す。projectRoot 外の submodule は無視。.gitmodules が無い・
    // 読めない場合は空集合を返し、submodule の無いリポジトリと同じ形を保つ。
    private static (HashSet<string> Paths, HashSet<string> AncestorPaths, IReadOnlyList<ScanError> Warnings) LoadGitSubmodulePaths(
        string ignoreRuleRoot, string projectRoot, StringComparer pathComparer)
    {
        var submodulePaths = new HashSet<string>(pathComparer);
        var ancestorPaths = new HashSet<string>(pathComparer);
        var warnings = new List<ScanError>();

        var gitmodulesPath = Path.Combine(ignoreRuleRoot, ".gitmodules");
        var prefixedGitmodulesPath = LongPath.EnsureWindowsPrefix(gitmodulesPath);
        if (!File.Exists(prefixedGitmodulesPath))
            return (submodulePaths, ancestorPaths, warnings);
        var gitmodulesRelativePath = NormalizeIgnorePath(Path.GetRelativePath(projectRoot, gitmodulesPath));

        try
        {
            IReadOnlyList<string> lines;
            if (ReadGitmodulesLinesForTesting is { } readGitmodulesLines)
            {
                lines = readGitmodulesLines(prefixedGitmodulesPath);
            }
            else
            {
                if (!TryReadBoundedUtf8SidecarLines(
                        prefixedGitmodulesPath,
                        MaxGitmodulesBytes,
                        MaxGitmodulesLines,
                        out lines,
                        out var skippedReason,
                        out _))
                {
                    warnings.Add(new ScanError(
                        gitmodulesRelativePath,
                        $"Skipped .gitmodules because {skippedReason}.",
                        ScanIssueSeverity.Warning));
                    return (submodulePaths, ancestorPaths, warnings);
                }
            }

            var submodulePathCount = 0;
            foreach (var rawSubmodulePath in ParseSubmodulePathsFromGitmodules(lines))
            {
                string absolute;
                try
                {
                    absolute = Path.GetFullPath(Path.Combine(ignoreRuleRoot, rawSubmodulePath));
                }
                catch (ArgumentException)
                {
                    continue;
                }

                var relativeToProject = NormalizeIgnorePath(Path.GetRelativePath(projectRoot, absolute));
                if (relativeToProject.Length == 0
                    || relativeToProject == "."
                    || relativeToProject.StartsWith("../", StringComparison.Ordinal))
                {
                    continue;
                }

                if (submodulePathCount >= MaxGitmodulesSubmodulePaths)
                {
                    warnings.Add(new ScanError(
                        gitmodulesRelativePath,
                        $"Stopped parsing .gitmodules submodule paths after {MaxGitmodulesSubmodulePaths} entries.",
                        ScanIssueSeverity.Warning));
                    break;
                }

                submodulePathCount++;
                if (submodulePaths.Add(relativeToProject))
                {
                    var segments = relativeToProject.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 1; i < segments.Length; i++)
                        ancestorPaths.Add(string.Join('/', segments, 0, i));
                }
            }
        }
        catch (IOException ex)
        {
            AddGitmodulesDiscoveryWarning(warnings, gitmodulesRelativePath, ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            AddGitmodulesDiscoveryWarning(warnings, gitmodulesRelativePath, ex.GetType().Name);
        }

        return (submodulePaths, ancestorPaths, warnings);
    }

    private static void AddGitmodulesDiscoveryWarning(
        List<ScanError> warnings,
        string gitmodulesRelativePath,
        string exceptionType)
    {
        warnings.Add(new ScanError(
            gitmodulesRelativePath,
            $"Skipped .gitmodules because it could not be read ({exceptionType}).",
            ScanIssueSeverity.Warning));
    }

    private static bool TryReadBoundedUtf8SidecarLines(
        string path,
        int maxBytes,
        int maxLines,
        out IReadOnlyList<string> lines,
        out string skippedReason,
        out BoundedTextFileReadFailure failure)
    {
        var success = BoundedLineReader.TryReadUtf8File(
            path,
            maxBytes,
            maxLines,
            MaxGitmodulesLineChars,
            out lines,
            out failure);
        skippedReason = success ? string.Empty : failure.Reason;
        return success;
    }

    // Tolerant .gitmodules reader: yields each declared submodule's "path = ..." value.
    // Supports comments (# / ;), inline comments, surrounding double quotes, and
    // ignores absolute or empty values. Quoted-string escapes are not expanded since
    // submodule paths in practice are plain relative filesystem paths.
    // .gitmodules を寛容に読み、各 submodule の "path = ..." 値を返す。コメント(# / ;)、
    // インラインコメント、両端のダブルクオート、絶対パス・空値の除外をサポート。実用上の
    // submodule パスは通常のファイル名なのでクォート内のエスケープは展開しない。
    private static IEnumerable<string> ParseSubmodulePathsFromGitmodules(IEnumerable<string> lines)
    {
        var inSubmoduleSection = false;
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (line[0] == '#' || line[0] == ';')
                continue;

            if (line[0] == '[')
            {
                var endBracket = line.IndexOf(']');
                if (endBracket < 0)
                {
                    inSubmoduleSection = false;
                    continue;
                }

                var sectionHeader = line.Substring(1, endBracket - 1).Trim();
                inSubmoduleSection = sectionHeader.StartsWith("submodule", StringComparison.OrdinalIgnoreCase)
                    && sectionHeader.Length > "submodule".Length
                    && char.IsWhiteSpace(sectionHeader["submodule".Length]);
                continue;
            }

            if (!inSubmoduleSection)
                continue;

            var equalsIndex = line.IndexOf('=');
            if (equalsIndex < 0)
                continue;
            var key = line.Substring(0, equalsIndex).Trim();
            if (!string.Equals(key, "path", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = StripGitmodulesInlineComment(line[(equalsIndex + 1)..]);
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
                value = value[1..^1];
            if (value.Length == 0)
                continue;
            if (Path.IsPathRooted(value))
                continue;

            yield return value;
        }
    }

    private static string StripGitmodulesInlineComment(string value)
    {
        var inQuotes = false;
        var escaping = false;
        for (var i = 0; i < value.Length; i++)
        {
            var ch = value[i];
            if (escaping)
            {
                escaping = false;
                continue;
            }

            if (inQuotes && ch == '\\')
            {
                escaping = true;
                continue;
            }

            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (!inQuotes && ch is '#' or ';')
                return value[..i].Trim();
        }

        return value.Trim();
    }
}
