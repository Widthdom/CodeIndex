using CodeIndex.Cli;
using System.Text.RegularExpressions;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class EnvironmentAccessTests
{
    private static readonly Regex DirectEnvironmentAccessPattern = new(
        @"(?<![A-Za-z0-9_])(?:System\.)?Environment\.GetEnvironmentVariables?\(",
        RegexOptions.CultureInvariant);

    [Fact]
    public void CdidxEnvironment_Push_DoesNotChangeProcessOnlyReads()
    {
        const string name = "CDIDX_ENV_ACCESS_TEST_4126";
        using var env = EnvironmentVariableScope.Capture(name);
        env.Set(name, "process");

        using (CdidxEnvironment.Push(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [name] = "scoped",
            }))
        {
            Assert.Equal("scoped", CdidxEnvironment.GetEnvironmentVariable(name));
            Assert.Equal("process", CdidxEnvironment.GetProcessEnvironmentVariable(name));
        }

        Assert.Equal("process", CdidxEnvironment.GetEnvironmentVariable(name));
    }

    [Fact]
    public void ProductionCode_UsesCentralEnvironmentAccessors()
    {
        var sourceRoot = RepositoryTestPaths.Combine("src", "CodeIndex");
        var allowedRelativePath = NormalizeRelativePath(Path.Combine("src", "CodeIndex", "EnvironmentAccess.cs"));
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories).OrderBy(static path => path, StringComparer.Ordinal))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(RepositoryTestPaths.Root, file));
            if (string.Equals(relative, allowedRelativePath, StringComparison.Ordinal))
                continue;

            var lineNumber = 0;
            foreach (var line in File.ReadLines(file))
            {
                lineNumber++;
                if (DirectEnvironmentAccessPattern.IsMatch(line))
                {
                    offenders.Add($"{relative}:{lineNumber}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Production environment variable access must go through EnvironmentAccess/CdidxEnvironment. Offenders:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    private static string NormalizeRelativePath(string path)
        => path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
}
