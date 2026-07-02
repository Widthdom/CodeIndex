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
                    AddSubmoduleAncestorPaths(relativeToProject, ancestorPaths);
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

    private static void AddSubmoduleAncestorPaths(string relativeToProject, HashSet<string> ancestorPaths)
    {
        var segmentCount = 0;
        var ancestorEnd = 0;
        var segmentStart = 0;
        while (segmentStart < relativeToProject.Length)
        {
            while (segmentStart < relativeToProject.Length && relativeToProject[segmentStart] == '/')
                segmentStart++;
            if (segmentStart >= relativeToProject.Length)
                break;

            var segmentEnd = relativeToProject.IndexOf('/', segmentStart);
            if (segmentEnd < 0)
                segmentEnd = relativeToProject.Length;

            if (segmentCount > 0)
                ancestorPaths.Add(relativeToProject[..ancestorEnd]);

            segmentCount++;
            ancestorEnd = segmentEnd;
            segmentStart = segmentEnd + 1;
        }
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
            if (TryParseSubmodulePathFromGitmodulesLine(rawLine, ref inSubmoduleSection, out var value))
                yield return value;
        }
    }

    private static bool TryParseSubmodulePathFromGitmodulesLine(
        string rawLine,
        ref bool inSubmoduleSection,
        out string value)
    {
        value = string.Empty;
        var line = rawLine.AsSpan().Trim();
        if (line.Length == 0)
            return false;
        if (line[0] == '#' || line[0] == ';')
            return false;

        if (line[0] == '[')
        {
            var endBracket = line.IndexOf(']');
            if (endBracket < 0)
            {
                inSubmoduleSection = false;
                return false;
            }

            var sectionHeader = line[1..endBracket].Trim();
            inSubmoduleSection = sectionHeader.StartsWith("submodule".AsSpan(), StringComparison.OrdinalIgnoreCase)
                && sectionHeader.Length > "submodule".Length
                && char.IsWhiteSpace(sectionHeader["submodule".Length]);
            return false;
        }

        if (!inSubmoduleSection)
            return false;

        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0)
            return false;
        var key = line[..equalsIndex].Trim();
        if (!key.Equals("path".AsSpan(), StringComparison.OrdinalIgnoreCase))
            return false;

        var valueSpan = StripGitmodulesInlineComment(line[(equalsIndex + 1)..]);
        if (valueSpan.Length >= 2 && valueSpan[0] == '"' && valueSpan[^1] == '"')
            valueSpan = valueSpan[1..^1];
        if (valueSpan.Length == 0)
            return false;
        if (Path.IsPathRooted(valueSpan))
            return false;

        value = valueSpan.ToString();
        return true;
    }

    private static ReadOnlySpan<char> StripGitmodulesInlineComment(ReadOnlySpan<char> value)
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
