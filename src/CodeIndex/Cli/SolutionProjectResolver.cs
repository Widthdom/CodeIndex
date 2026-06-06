using CodeIndex.Indexer;

namespace CodeIndex.Cli;

internal sealed record DotNetProjectInfo(string Name, string ProjectPath, string DirectoryPath);

internal readonly record struct SolutionProjectResolverLimits(
    int MaxAutomaticSolutionCandidates,
    int MaxFallbackDiscoveryDirectories,
    int MaxFallbackDiscoveryFiles,
    int MaxProjectExpansionFilesPerProject,
    int MaxProjectExpansionFilesTotal,
    int MaxTraversalDiagnostics)
{
    internal const int DefaultMaxAutomaticSolutionCandidates = 128;
    internal const int DefaultMaxFallbackDiscoveryDirectories = 4096;
    internal const int DefaultMaxFallbackDiscoveryFiles = 65536;
    internal const int DefaultMaxProjectExpansionFilesPerProject = 65536;
    internal const int DefaultMaxProjectExpansionFilesTotal = 131072;
    internal const int DefaultMaxTraversalDiagnostics = 8;

    public static SolutionProjectResolverLimits Default { get; } = new(
        DefaultMaxAutomaticSolutionCandidates,
        DefaultMaxFallbackDiscoveryDirectories,
        DefaultMaxFallbackDiscoveryFiles,
        DefaultMaxProjectExpansionFilesPerProject,
        DefaultMaxProjectExpansionFilesTotal,
        DefaultMaxTraversalDiagnostics);

    public void Validate()
    {
        if (MaxAutomaticSolutionCandidates <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxAutomaticSolutionCandidates), MaxAutomaticSolutionCandidates, "Limit must be positive.");
        if (MaxFallbackDiscoveryDirectories <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxFallbackDiscoveryDirectories), MaxFallbackDiscoveryDirectories, "Limit must be positive.");
        if (MaxFallbackDiscoveryFiles <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxFallbackDiscoveryFiles), MaxFallbackDiscoveryFiles, "Limit must be positive.");
        if (MaxProjectExpansionFilesPerProject <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxProjectExpansionFilesPerProject), MaxProjectExpansionFilesPerProject, "Limit must be positive.");
        if (MaxProjectExpansionFilesTotal <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxProjectExpansionFilesTotal), MaxProjectExpansionFilesTotal, "Limit must be positive.");
        if (MaxTraversalDiagnostics <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaxTraversalDiagnostics), MaxTraversalDiagnostics, "Limit must be positive.");
    }
}

internal static class SolutionProjectResolver
{
    internal const long MaxSolutionFileBytes = 8L * 1024 * 1024;
    internal const int MaxSolutionLineChars = 16 * 1024;
    internal const int MaxSolutionProjectReferences = 4096;

    public static IReadOnlyList<DotNetProjectInfo> ResolveProjects(string workspaceRoot, string? solutionPath = null)
        => ResolveProjects(workspaceRoot, solutionPath, SolutionProjectResolverLimits.Default);

    internal static IReadOnlyList<DotNetProjectInfo> ResolveProjects(
        string workspaceRoot,
        string? solutionPath,
        SolutionProjectResolverLimits limits)
        => ResolveProjects(workspaceRoot, solutionPath, limits, traversalDiagnostics: null);

    internal static IReadOnlyList<DotNetProjectInfo> ResolveProjects(
        string workspaceRoot,
        string? solutionPath,
        SolutionProjectResolverLimits limits,
        IList<string>? traversalDiagnostics)
    {
        limits.Validate();
        var root = Path.GetFullPath(workspaceRoot);
        var indexer = CreateIndexerWithWorkspacePolicy(root);
        return ResolveProjects(root, solutionPath, indexer, limits, traversalDiagnostics);
    }

    private static IReadOnlyList<DotNetProjectInfo> ResolveProjects(
        string workspaceRoot,
        string? solutionPath,
        FileIndexer indexer,
        SolutionProjectResolverLimits limits,
        IList<string>? traversalDiagnostics)
    {
        var solution = ResolveSolutionPath(workspaceRoot, solutionPath, limits);
        if (solution != null)
            return ParseSolution(solution, workspaceRoot, indexer);

        var budget = ProjectTraversalBudget.ForFallbackDiscovery(limits);
        return EnumerateFilesUsingIndexerPolicy(workspaceRoot, workspaceRoot, indexer, limits, budget, traversalDiagnostics)
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

        var traversalDiagnostics = new List<string>();
        var projects = ResolveProjects(workspaceRoot, solutionPath, SolutionProjectResolverLimits.Default, traversalDiagnostics);
        var globs = new List<string>();
        foreach (var requested in requestedProjects)
        {
            var match = MatchProject(projects, requested);
            if (match == null)
            {
                throw new InvalidOperationException(AppendTraversalDiagnostics(
                    $"project not found in solution/workspace: {requested}",
                    traversalDiagnostics));
            }

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
        => ResolveProjectFiles(workspaceRoot, requestedProjects, solutionPath, SolutionProjectResolverLimits.Default);

    internal static IReadOnlyList<string> ResolveProjectFiles(
        string workspaceRoot,
        IReadOnlyList<string> requestedProjects,
        string? solutionPath,
        SolutionProjectResolverLimits limits)
    {
        if (requestedProjects.Count == 0)
            return [];

        limits.Validate();
        var root = Path.GetFullPath(workspaceRoot);
        var indexer = CreateIndexerWithWorkspacePolicy(root);
        var traversalDiagnostics = new List<string>();
        var projects = ResolveProjects(root, solutionPath, indexer, limits, traversalDiagnostics);
        var files = new SortedSet<string>(StringComparer.Ordinal);
        var totalExpandedFiles = 0;
        foreach (var requested in requestedProjects)
        {
            var match = MatchProject(projects, requested);
            if (match == null)
            {
                throw new InvalidOperationException(AppendTraversalDiagnostics(
                    $"project not found in solution/workspace: {requested}",
                    traversalDiagnostics));
            }

            var projectExpandedFiles = 0;
            foreach (var file in EnumerateFilesUsingIndexerPolicy(root, match.DirectoryPath, indexer, limits, budget: null, traversalDiagnostics))
            {
                var relative = Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/')
                    .Replace(Path.AltDirectorySeparatorChar, '/');
                projectExpandedFiles++;
                if (projectExpandedFiles > limits.MaxProjectExpansionFilesPerProject)
                    ThrowProjectExpansionPerProjectLimitExceeded(limits, requested, match);

                if (files.Add(relative))
                {
                    totalExpandedFiles++;
                    if (totalExpandedFiles > limits.MaxProjectExpansionFilesTotal)
                        ThrowProjectExpansionTotalLimitExceeded(limits);
                }
            }
        }

        return files.ToList();
    }

    private static void ThrowProjectExpansionPerProjectLimitExceeded(
        SolutionProjectResolverLimits limits,
        string requested,
        DotNetProjectInfo match)
    {
        throw new InvalidOperationException(
            $"project filter expansion for {requested} ({match.ProjectPath}) materialized more than {limits.MaxProjectExpansionFilesPerProject} files; narrow --project/--solution or pass explicit --files.");
    }

    private static void ThrowProjectExpansionTotalLimitExceeded(SolutionProjectResolverLimits limits)
    {
        throw new InvalidOperationException(
            $"project filter expansion materialized more than {limits.MaxProjectExpansionFilesTotal} unique files across requested projects; narrow --project/--solution or pass explicit --files.");
    }

    private static FileIndexer CreateIndexerWithWorkspacePolicy(string workspaceRoot)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var ignoreCase = GitHelper.ResolveIgnoreCase(root);
        var ignoreRuleRoot = GitHelper.TryGetRepositoryRoot(root) ?? root;
        return new FileIndexer(root, ignoreCase, ignoreRuleRoot);
    }

    private static string? ResolveSolutionPath(
        string workspaceRoot,
        string? solutionPath,
        SolutionProjectResolverLimits limits)
    {
        if (!string.IsNullOrWhiteSpace(solutionPath))
        {
            var path = Path.IsPathRooted(solutionPath)
                ? solutionPath
                : Path.Combine(workspaceRoot, solutionPath);
            return File.Exists(LongPath.EnsureWindowsPrefix(path)) ? Path.GetFullPath(path) : throw new FileNotFoundException($"solution not found: {solutionPath}", path);
        }

        var solutions = new List<string>();
        foreach (var solution in Directory.EnumerateFiles(LongPath.EnsureWindowsPrefix(workspaceRoot), "*.sln", SearchOption.TopDirectoryOnly))
        {
            if (solutions.Count >= limits.MaxAutomaticSolutionCandidates)
            {
                throw new InvalidOperationException(
                    $"automatic solution discovery found more than {limits.MaxAutomaticSolutionCandidates} .sln files at {workspaceRoot}; pass --solution <path> to select a solution explicitly.");
            }

            solutions.Add(LongPath.RemoveWindowsPrefix(solution));
        }

        solutions.Sort(StringComparer.OrdinalIgnoreCase);
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
        foreach (var line in File.ReadLines(LongPath.EnsureWindowsPrefix(solutionPath)))
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

            if (File.Exists(LongPath.EnsureWindowsPrefix(fullPath)) && !indexer.EvaluatePathFilter(fullPath).ShouldSkip)
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
        FileIndexer indexer,
        SolutionProjectResolverLimits limits,
        ProjectTraversalBudget? budget,
        IList<string>? traversalDiagnostics)
    {
        var root = Path.GetFullPath(workspaceRoot);
        var start = Path.GetFullPath(startDirectory);
        if (!IsPathEqualOrParent(root, start) || ShouldSkipDirectoryForTraversal(root, start, indexer, limits, traversalDiagnostics))
            yield break;

        var pending = new Stack<string>();
        budget?.RecordDirectory(root, start);
        pending.Push(start);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var childDirectory in EnumerateChildDirectories(root, directory, limits, budget, traversalDiagnostics))
            {
                if (!ShouldSkipDirectoryForTraversal(root, childDirectory, indexer, limits, traversalDiagnostics))
                    pending.Push(childDirectory);
            }

            foreach (var file in EnumerateDirectoryFiles(root, directory, limits, budget, traversalDiagnostics))
            {
                if (ShouldIncludeFileForTraversal(root, file, indexer, limits, traversalDiagnostics))
                    yield return file;
            }
        }
    }

    private static bool ShouldSkipDirectoryForTraversal(
        string workspaceRoot,
        string directory,
        FileIndexer indexer,
        SolutionProjectResolverLimits limits,
        IList<string>? traversalDiagnostics)
    {
        try
        {
            if (!PathCasing.PathsEqual(Path.GetFullPath(workspaceRoot), Path.GetFullPath(directory))
                && indexer.ShouldSkipDirectoryTraversal(directory))
            {
                return true;
            }

            return indexer.EvaluatePathFilter(directory, isDirectory: true).ShouldSkip;
        }
        catch (UnauthorizedAccessException)
        {
            AddTraversalDiagnostic(workspaceRoot, directory, "directory filters", "permissions", limits, traversalDiagnostics);
        }
        catch (IOException)
        {
            AddTraversalDiagnostic(workspaceRoot, directory, "directory filters", "an I/O error", limits, traversalDiagnostics);
        }

        return true;
    }

    private static bool ShouldIncludeFileForTraversal(
        string workspaceRoot,
        string file,
        FileIndexer indexer,
        SolutionProjectResolverLimits limits,
        IList<string>? traversalDiagnostics)
    {
        try
        {
            return !indexer.EvaluatePathFilter(file).ShouldSkip;
        }
        catch (UnauthorizedAccessException)
        {
            AddTraversalDiagnostic(workspaceRoot, file, "file filters", "permissions", limits, traversalDiagnostics);
        }
        catch (IOException)
        {
            AddTraversalDiagnostic(workspaceRoot, file, "file filters", "an I/O error", limits, traversalDiagnostics);
        }

        return false;
    }

    private static IEnumerable<string> EnumerateChildDirectories(
        string workspaceRoot,
        string directory,
        SolutionProjectResolverLimits limits,
        ProjectTraversalBudget? budget,
        IList<string>? traversalDiagnostics)
        => EnumerateDirectoryEntries(
            workspaceRoot,
            directory,
            "subdirectories",
            ProjectTraversalEntryKind.Directory,
            Directory.EnumerateDirectories,
            limits,
            budget,
            traversalDiagnostics);

    private static IEnumerable<string> EnumerateDirectoryFiles(
        string workspaceRoot,
        string directory,
        SolutionProjectResolverLimits limits,
        ProjectTraversalBudget? budget,
        IList<string>? traversalDiagnostics)
        => EnumerateDirectoryEntries(
            workspaceRoot,
            directory,
            "files",
            ProjectTraversalEntryKind.File,
            Directory.EnumerateFiles,
            limits,
            budget,
            traversalDiagnostics);

    private static IEnumerable<string> EnumerateDirectoryEntries(
        string workspaceRoot,
        string directory,
        string entryKind,
        ProjectTraversalEntryKind budgetKind,
        Func<string, IEnumerable<string>> enumerate,
        SolutionProjectResolverLimits limits,
        ProjectTraversalBudget? budget,
        IList<string>? traversalDiagnostics)
    {
        IEnumerable<string> entries;
        try
        {
            entries = enumerate(LongPath.EnsureWindowsPrefix(directory));
        }
        catch (UnauthorizedAccessException)
        {
            AddTraversalDiagnostic(workspaceRoot, directory, entryKind, "permissions", limits, traversalDiagnostics);
            yield break;
        }
        catch (IOException)
        {
            AddTraversalDiagnostic(workspaceRoot, directory, entryKind, "an I/O error", limits, traversalDiagnostics);
            yield break;
        }

        using var enumerator = entries.GetEnumerator();
        while (true)
        {
            string entry;
            try
            {
                if (!enumerator.MoveNext())
                    yield break;
                entry = LongPath.RemoveWindowsPrefix(enumerator.Current);
            }
            catch (UnauthorizedAccessException)
            {
                AddTraversalDiagnostic(workspaceRoot, directory, entryKind, "permissions", limits, traversalDiagnostics);
                yield break;
            }
            catch (IOException)
            {
                AddTraversalDiagnostic(workspaceRoot, directory, entryKind, "an I/O error", limits, traversalDiagnostics);
                yield break;
            }

            budget?.RecordEntry(workspaceRoot, entry, budgetKind);
            yield return entry;
        }
    }

    private enum ProjectTraversalEntryKind
    {
        Directory,
        File,
    }

    private sealed class ProjectTraversalBudget
    {
        private readonly int _maxDirectories;
        private readonly int _maxFiles;
        private readonly string _context;
        private readonly string _recoveryHint;
        private int _directoriesTraversed;
        private int _filesTraversed;

        private ProjectTraversalBudget(int maxDirectories, int maxFiles, string context, string recoveryHint)
        {
            _maxDirectories = maxDirectories;
            _maxFiles = maxFiles;
            _context = context;
            _recoveryHint = recoveryHint;
        }

        public static ProjectTraversalBudget ForFallbackDiscovery(SolutionProjectResolverLimits limits)
            => new(
                limits.MaxFallbackDiscoveryDirectories,
                limits.MaxFallbackDiscoveryFiles,
                "fallback project discovery",
                "pass --solution <path> to avoid fallback workspace discovery");

        public void RecordDirectory(string workspaceRoot, string directory)
        {
            _directoriesTraversed++;
            if (_directoriesTraversed > _maxDirectories)
                ThrowExceeded(workspaceRoot, directory, "directories", _maxDirectories);
        }

        public void RecordEntry(string workspaceRoot, string path, ProjectTraversalEntryKind kind)
        {
            if (kind == ProjectTraversalEntryKind.Directory)
            {
                RecordDirectory(workspaceRoot, path);
                return;
            }

            _filesTraversed++;
            if (_filesTraversed > _maxFiles)
                ThrowExceeded(workspaceRoot, path, "files", _maxFiles);
        }

        private void ThrowExceeded(string workspaceRoot, string path, string unit, int limit)
        {
            throw new InvalidOperationException(
                $"{_context} traversed more than {limit} {unit} under {workspaceRoot}; last path: {FormatRelativePathForDiagnostic(workspaceRoot, path)}; {_recoveryHint}.");
        }
    }

    private static void AddTraversalDiagnostic(
        string workspaceRoot,
        string directory,
        string entryKind,
        string reason,
        SolutionProjectResolverLimits limits,
        IList<string>? traversalDiagnostics)
    {
        if (traversalDiagnostics == null)
            return;

        if (traversalDiagnostics.Count < limits.MaxTraversalDiagnostics)
        {
            traversalDiagnostics.Add(
                $"Could not enumerate {entryKind} in {FormatRelativePathForDiagnostic(workspaceRoot, directory)} due to {reason}.");
        }
        else if (traversalDiagnostics.Count == limits.MaxTraversalDiagnostics)
        {
            traversalDiagnostics.Add($"Additional traversal diagnostics omitted after {limits.MaxTraversalDiagnostics} entries.");
        }
    }

    private static string AppendTraversalDiagnostics(string message, IReadOnlyList<string> traversalDiagnostics)
    {
        if (traversalDiagnostics.Count == 0)
            return message;

        return $"{message}. Traversal diagnostics: {string.Join(" ", traversalDiagnostics)}";
    }

    private static string FormatRelativePathForDiagnostic(string workspaceRoot, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(workspaceRoot), Path.GetFullPath(path))
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return relative == "." ? "." : relative;
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
        var length = new FileInfo(LongPath.EnsureWindowsPrefix(solutionPath)).Length;
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
