namespace CodeIndex.Tests;

internal static class RepositoryTestPaths
{
    internal static string Root { get; } = LocateRoot();

    internal static string Combine(params string[] relativeSegments)
    {
        var segments = new string[relativeSegments.Length + 1];
        segments[0] = Root;
        Array.Copy(relativeSegments, 0, segments, 1, relativeSegments.Length);
        return Path.Combine(segments);
    }

    internal static string ReadText(params string[] relativeSegments) => File.ReadAllText(Combine(relativeSegments));

    internal static string ReadWorkflow(string fileName) => ReadText(".github", "workflows", fileName);

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
