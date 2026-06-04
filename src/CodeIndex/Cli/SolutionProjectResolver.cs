using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal sealed record DotNetProjectInfo(string Name, string ProjectPath, string DirectoryPath);

internal static class SolutionProjectResolver
{
    internal const long MaxSolutionFileBytes = 8L * 1024 * 1024;
    internal const int MaxSolutionLineChars = 16 * 1024;
    internal const int MaxSolutionProjectReferences = 4096;

    public static IReadOnlyList<DotNetProjectInfo> ResolveProjects(string workspaceRoot, string? solutionPath = null)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var indexer = CreateIndexerWithWorkspacePolicy(root);
        return ResolveProjects(root, solutionPath, indexer);
    }

    private static IReadOnlyList<DotNetProjectInfo> ResolveProjects(
        string workspaceRoot,
        string? solutionPath,
        FileIndexer indexer)
    {
        var solution = ResolveSolutionPath(workspaceRoot, solutionPath);
        if (solution != null)
            return ParseSolution(solution, workspaceRoot, indexer);

        return EnumerateFilesUsingIndexerPolicy(workspaceRoot, workspaceRoot, indexer)
            .Where(IsDotNetProjectFile)
            .Select(path => BuildProjectInfo(path, workspaceRoot))
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static IReadOnlyList<string> ResolveProjectDirectoryGlobs(
        string workspaceRoot,
        IReadOnlyList<string> requestedProjects,
        string? solutionPath = null)
    {
        if (requestedProjects.Count == 0)
            return [];

        var projects = ResolveProjects(workspaceRoot, solutionPath);
        var globs = new List<string>();
        foreach (var requested in requestedProjects)
        {
            var match = MatchProject(projects, requested);
            if (match == null)
                throw new InvalidOperationException($"project not found in solution/workspace: {requested}");

            var relativeDir = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), match.DirectoryPath)
                .Replace(Path.DirectorySeparatorChar, '/')
                .Replace(Path.AltDirectorySeparatorChar, '/');
            globs.Add(relativeDir == "." ? "*" : $"{relativeDir.TrimEnd('/')}/*");
        }

        return globs;
    }

    public static IReadOnlyList<string> ResolveProjectFiles(
        string workspaceRoot,
        IReadOnlyList<string> requestedProjects,
        string? solutionPath = null)
    {
        if (requestedProjects.Count == 0)
            return [];

        var root = Path.GetFullPath(workspaceRoot);
        var indexer = CreateIndexerWithWorkspacePolicy(root);
        var projects = ResolveProjects(root, solutionPath, indexer);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var requested in requestedProjects)
        {
            var match = MatchProject(projects, requested);
            if (match == null)
                throw new InvalidOperationException($"project not found in solution/workspace: {requested}");

            foreach (var file in EnumerateFilesUsingIndexerPolicy(root, match.DirectoryPath, indexer))
            {
                var relative = Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                files.Add(relative);
            }
        }

        return files.ToList();
    }

    private static FileIndexer CreateIndexerWithWorkspacePolicy(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var ignoreCase = GitHelper.ResolveIgnoreCase(root);
        var ignoreRuleRoot = GitHelper.TryGetRepositoryRoot(root) ?? root;
        return new FileIndexer(root, ignoreCase, ignoreRuleRoot);
    }

    private static string? ResolveSolutionPath(string workspaceRoot, string? solutionPath)
    {
        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            var path = Path.IsPathRooted(solutionPath)
                ? solutionPath
                : Path.Combine(workspaceRoot, solutionPath);
            return File.Exists(path) ? Path.GetFullPath(path) : throw new FileNotFoundException($"solution not found: {solutionPath}", path);
        }

        var solutions = Directory.EnumerateFiles(workspaceRoot, "*.sln", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return solutions.Count == 1 ? solutions[0] : null;
    }

    private static IReadOnlyList<DotNetProjectInfo> ParseSolution(
        string solutionPath,
        string workspaceRoot,
        FileIndexer indexer)
    {
        RejectOversizedSolutionFile(solutionPath);
        var root = Path.GetFullPath(workspaceRoot);
        var solutionDir = Path.GetDirectoryName(solutionPath) ?? workspaceRoot;
        var projects = new List<DotNetProjectInfo>();
        var lineNumber = 0;
        var projectReferenceCount = 0;
        foreach (var line in File.ReadLines(solutionPath))
        {
            lineNumber++;
            if (line.Length > MaxSolutionLineChars)
            {
                throw new InvalidOperationException(
                    $"solution line is too long at {solutionPath}:{lineNumber}: {line.Length} characters exceeds limit {MaxSolutionLineChars}.");
            }

            if (!TryParseSolutionProjectLine(line, out var name, out var projectPath))
                continue;

            if (!IsDotNetProjectFile(projectPath))
                continue;

            projectReferenceCount++;
            if (projectReferenceCount > MaxSolutionProjectReferences)
            {
                throw new InvalidOperationException(
                    $"solution contains too many .NET project references: limit {MaxSolutionProjectReferences} exceeded in {solutionPath}.");
            }

            projectPath = projectPath.Replace('\\', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(solutionDir, projectPath));
            if (!IsPathEqualOrParent(root, fullPath))
                continue;

            if (File.Exists(fullPath) && !indexer.EvaluatePathFilter(fullPath).ShouldSkip)
                projects.Add(BuildProjectInfo(fullPath, root, name));
        }

        return projects
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(project => project.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static DotNetProjectInfo BuildProjectInfo(string fullProjectPath, string workspaceRoot, string? solutionName = null)
    {
        var name = !string.IsNullOrWhiteSpace(solutionName)
            ? solutionName
            : Path.GetFileNameWithoutExtension(fullProjectPath);
        var relativeProject = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), fullProjectPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return new DotNetProjectInfo(name, relativeProject, Path.GetDirectoryName(fullProjectPath) ?? Path.GetFullPath(workspaceRoot));
    }

    private static DotNetProjectInfo? MatchProject(IReadOnlyList<DotNetProjectInfo> projects, string requested)
    {
        var trimmed = requested.Trim();
        return projects.FirstOrDefault(project =>
            string.Equals(project.Name, trimmed, StringComparison.OrdinalIgnoreCase)
            || string.Equals(project.ProjectPath, trimmed.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(project.ProjectPath), trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EnumerateFilesUsingIndexerPolicy(
        string workspaceRoot,
        string startDirectory,
        FileIndexer indexer)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var start = Path.GetFullPath(startDirectory);
        if (!IsPathEqualOrParent(root, start) || indexer.EvaluatePathFilter(start, isDirectory: true).ShouldSkip)
            yield break;
        if (!PathCasing.PathsEqual(root, start) && indexer.ShouldSkipDirectoryTraversal(start))
            yield break;

        var pending = new Stack<string>();
        pending.Push(start);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if (indexer.ShouldSkipDirectoryTraversal(childDirectory))
                    continue;
                if (!indexer.EvaluatePathFilter(childDirectory, isDirectory: true).ShouldSkip)
                    pending.Push(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (!indexer.EvaluatePathFilter(file).ShouldSkip)
                    yield return file;
            }
        }
    }

    private static bool IsPathEqualOrParent(string parentPath, string childPath)
    {
        var parent = Path.GetFullPath(parentPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var child = Path.GetFullPath(childPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return PathCasing.IsPathEqualOrParent(parent, child);
    }

    private static bool IsDotNetProjectFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectOversizedSolutionFile(string solutionPath)
    {
        var length = new FileInfo(solutionPath).Length;
        if (length > MaxSolutionFileBytes)
        {
            throw new InvalidOperationException(
                $"solution file is too large: {solutionPath} is {length} bytes; limit is {MaxSolutionFileBytes} bytes.");
        }
    }

    private static bool TryParseSolutionProjectLine(string line, out string name, out string projectPath)
    {
        name = string.Empty;
        projectPath = string.Empty;
        if (!line.StartsWith("Project(", StringComparison.OrdinalIgnoreCase))
            return false;

        var equalsIndex = line.IndexOf('=');
        if (equalsIndex < 0)
            return false;

        var cursor = equalsIndex + 1;
        if (!TryReadQuotedValue(line, ref cursor, out name))
            return false;

        cursor = SkipWhitespace(line, cursor);
        if (cursor >= line.Length || line[cursor] != ',')
            return false;

        cursor++;
        return TryReadQuotedValue(line, ref cursor, out projectPath);
    }

    private static bool TryReadQuotedValue(string line, ref int cursor, out string value)
    {
        value = string.Empty;
        cursor = SkipWhitespace(line, cursor);
        if (cursor >= line.Length || line[cursor] != '"')
            return false;

        var start = cursor + 1;
        var end = line.IndexOf('"', start);
        if (end < 0)
            return false;

        value = line[start..end];
        cursor = end + 1;
        return true;
    }

    private static int SkipWhitespace(string line, int cursor)
    {
        while (cursor < line.Length && char.IsWhiteSpace(line[cursor]))
            cursor++;
        return cursor;
    }
}
