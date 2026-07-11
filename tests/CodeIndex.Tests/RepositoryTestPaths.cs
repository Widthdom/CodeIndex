using System.Collections.Concurrent;

namespace CodeIndex.Tests;

internal static class RepositoryTestPaths
{
    private static readonly ConcurrentDictionary<string, string> TextCache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> NormalizedTextCache = new(StringComparer.Ordinal);
    private static readonly Lazy<IReadOnlyList<(string FileName, string Content)>> NormalizedWorkflows =
        new(ReadNormalizedWorkflowsCore, LazyThreadSafetyMode.ExecutionAndPublication);

    internal static string Root { get; } = LocateRoot();

    internal static string Combine(params string[] relativeSegments)
    {
        var segments = new string[relativeSegments.Length + 1];
        segments[0] = Root;
        Array.Copy(relativeSegments, 0, segments, 1, relativeSegments.Length);
        return Path.Combine(segments);
    }

    internal static string ReadText(params string[] relativeSegments)
    {
        var path = Combine(relativeSegments);
        return TextCache.GetOrAdd(path, static filePath => File.ReadAllText(filePath));
    }

    internal static string ReadWorkflow(string fileName) => ReadText(".github", "workflows", fileName);

    internal static string ReadReleaseWorkflow() => ReadWorkflow("release.yml");

    internal static string ReadDotnetWorkflow() => ReadWorkflow("dotnet.yml");

    internal static string ReadNormalizedReleaseWorkflow() => ReadNormalizedWorkflow("release.yml");

    internal static string ReadNormalizedDotnetWorkflow() => ReadNormalizedWorkflow("dotnet.yml");

    internal static string ReadNormalizedWorkflow(string fileName)
        => ReadNormalizedText(".github", "workflows", fileName);

    internal static string ReadNormalizedText(params string[] relativeParts)
    {
        var path = Combine(relativeParts);
        return NormalizedTextCache.GetOrAdd(
            path,
            static filePath => TextCache.GetOrAdd(filePath, static path => File.ReadAllText(path)).ReplaceLineEndings("\n"));
    }

    internal static string[] ReadNormalizedLines(params string[] relativeParts)
        => ReadNormalizedText(relativeParts).Split('\n');

    internal static string ReadDockerfile() => ReadText("Dockerfile");

    internal static string ReadDockerIgnore() => ReadText(".dockerignore");

    internal static IReadOnlyList<(string FileName, string Content)> ReadNormalizedWorkflows()
        => NormalizedWorkflows.Value;

    private static IReadOnlyList<(string FileName, string Content)> ReadNormalizedWorkflowsCore()
    {
        var workflowsDirectory = Combine(".github", "workflows");
        return Directory
            .EnumerateFiles(workflowsDirectory, "*.yml")
            .OrderBy(static path => Path.GetFileName(path), StringComparer.Ordinal)
            .Select(static path => (Path.GetFileName(path), NormalizedTextCache.GetOrAdd(
                path,
                static filePath => TextCache.GetOrAdd(filePath, static textPath => File.ReadAllText(textPath)).ReplaceLineEndings("\n"))))
            .ToArray();
    }

    private static string LocateRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeIndex.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root / リポジトリルートを特定できませんでした");
    }
}
