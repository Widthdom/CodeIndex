using CodeIndex.Cli;
using CodeIndex.Diagnostics;
using CodeIndex.Indexer;
using CodeIndex.Mcp;
using System.Text.RegularExpressions;

namespace CodeIndex.Tests;

public sealed class RegexTimeoutPolicyTests
{
    private const string BoundedRegexAliasUsing = "using Regex = CodeIndex.Indexer.BoundedRegex";
    private const string RegexRegistryPath = "src/CodeIndex/Indexer/RegexRegistry.cs";
    private const string SearchAuditRecipesPath = "src/CodeIndex/Cli/SearchAuditRecipes.cs";

    [Fact]
    public void FormatFindTimeout_SharedByCliAndMcp_Issue3993()
    {
        var timeout = new RegexMatchTimeoutException(
            "aaaaaaaaaaaaaaaa!",
            "^(a+)+$",
            TimeSpan.FromMilliseconds(25));

        Assert.Equal("25ms", QueryCommandRunner.FormatRegexMatchTimeout(timeout.MatchTimeout));
        Assert.Equal(
            "regular expression timed out after 25ms while scanning indexed file contents.",
            RegexTimeoutPolicy.FormatFindTimeout(timeout));
        Assert.Equal(RegexTimeoutPolicy.RegexTimeoutCategory, McpErrorEnvelope.CategoryRegexTimeout);
        Assert.Equal("regex_timeout", RegexTimeoutPolicy.RegexTimeoutCategory);
    }

    [Fact]
    public void FormatIndexingTimeout_UsesSharedDurationFormatter_Issue3993()
    {
        var timeout = new RegexMatchTimeoutException(
            "aaaaaaaaaaaaaaaa!",
            "^(a+)+$",
            TimeSpan.FromMilliseconds(1500));

        var message = RuntimeSafety.FormatRegexTimeout(timeout);

        Assert.Contains("timed out after 1.5s", message, StringComparison.Ordinal);
        Assert.Contains("indexing this file", message, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactOrFallback_ForcedRegexTimeout_ReturnsSurfacePolicy_Issue3993()
    {
        AssertRedactionFallback(RegexRedactionSurface.DiagnosticText, "[REDACTED]");
        AssertRedactionFallback(RegexRedactionSurface.DiagnosticSanitizerMessage, RegexTimeoutPolicy.DiagnosticSanitizerTimeoutFallback);
        AssertRedactionFallback(RegexRedactionSurface.SuggestionText, RegexTimeoutPolicy.SuggestionTextTimeoutFallback);
        AssertRedactionFallback(RegexRedactionSurface.GlobalToolLogArgument, "[REDACTED]");
        AssertRedactionFallback(RegexRedactionSurface.GitHubApiResponseBody, RegexTimeoutPolicy.GitHubApiResponseBodyTimeoutFallback);
        AssertRedactionFallback(RegexRedactionSurface.AuditArgumentValue, "[REDACTED]");
    }

    private static void AssertRedactionFallback(RegexRedactionSurface surface, string expected)
    {
        var timeout = new RegexMatchTimeoutException(
            "secret",
            "secret",
            TimeSpan.FromMilliseconds(5));

        var redacted = RegexTimeoutPolicy.RedactOrFallback(
            surface,
            () => throw timeout,
            "[REDACTED]");

        Assert.Equal(expected, redacted);
    }

    [Fact]
    public void IsRedactionMatchOrFallback_ForcedRegexTimeout_FailsClosed_Issue3993()
    {
        var timeout = new RegexMatchTimeoutException(
            "secret",
            "secret",
            TimeSpan.FromMilliseconds(5));

        var isMatch = RegexTimeoutPolicy.IsRedactionMatchOrFallback(() => throw timeout);

        Assert.True(isMatch);
    }

    [Fact]
    public void RegexRegistry_FactoriesUseNamedTimeoutPolicy_Issue4149()
    {
        var find = RegexRegistry.CreateFindRegex("token", exact: false, BoundedRegex.DefaultMatchTimeout);
        var ignore = RegexRegistry.CreateFileIgnorePatternRegex("^generated/[^/]*$");
        var generated = RegexRegistry.CreateGeneratedCodePatternRegex("^.*\\.g\\.cs$", ignoreCase: true);

        Assert.Equal(BoundedRegex.DefaultMatchTimeout, find.MatchTimeout);
        Assert.True(find.Options.HasFlag(RegexOptions.CultureInvariant));
        Assert.True(find.Options.HasFlag(RegexOptions.IgnoreCase));

        Assert.Equal(TimeSpan.FromMilliseconds(100), RegexRegistry.FileIgnorePatternMatchTimeout);
        Assert.Equal(RegexRegistry.FileIgnorePatternMatchTimeout, ignore.MatchTimeout);
        Assert.True(ignore.Options.HasFlag(RegexOptions.CultureInvariant));
        Assert.True(ignore.Options.HasFlag(RegexOptions.Compiled));
        Assert.True(ignore.Options.HasFlag(RegexOptions.NonBacktracking));

        Assert.Equal(TimeSpan.FromMilliseconds(50), RegexRegistry.GeneratedCodePatternMatchTimeout);
        Assert.Equal(RegexRegistry.GeneratedCodePatternMatchTimeout, generated.MatchTimeout);
        Assert.True(generated.Options.HasFlag(RegexOptions.CultureInvariant));
        Assert.True(generated.Options.HasFlag(RegexOptions.IgnoreCase));
        Assert.True(generated.Options.HasFlag(RegexOptions.NonBacktracking));
    }

    [Fact]
    public void ProductionRawRegexConstruction_StaysCentralizedOrBounded_Issue4149()
    {
        var sourceRoot = RepositoryTestPaths.Combine("src", "CodeIndex");
        var violations = new List<string>();

        foreach (var path in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal))
        {
            var relativePath = RelativeRepositoryPath(path);
            if (relativePath == SearchAuditRecipesPath)
                continue;

            var text = File.ReadAllText(path);
            var hasBoundedRegexAlias = text.Contains(BoundedRegexAliasUsing, StringComparison.Ordinal);
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.Contains("new Regex(", StringComparison.Ordinal))
                {
                    if (relativePath == RegexRegistryPath || hasBoundedRegexAlias)
                        continue;

                    violations.Add($"{relativePath}:{index + 1}: {line.Trim()}");
                }

                if (line.Contains("new System.Text.RegularExpressions.Regex", StringComparison.Ordinal)
                    && relativePath != RegexRegistryPath)
                {
                    violations.Add($"{relativePath}:{index + 1}: {line.Trim()}");
                }
            }
        }

        Assert.Empty(violations);
    }

    [Fact]
    public void ExtractionAndGeneratedCodeSurfacesUseRegexRegistry_Issue4149()
    {
        var dbReader = RepositoryTestPaths.ReadText("src", "CodeIndex", "Database", "DbReader.FilesStatus.cs");
        var fileIndexer = RepositoryTestPaths.ReadText("src", "CodeIndex", "Indexer", "Scanning", "FileIndexer.cs");
        var generatedCodeMatcher = RepositoryTestPaths.ReadText("src", "CodeIndex", "Indexer", "Scanning", "GeneratedCodePatternMatcher.cs");

        Assert.Contains("RegexRegistry.CreateFindRegex", dbReader, StringComparison.Ordinal);
        Assert.Contains("RegexRegistry.CreateFileIgnorePatternRegex", fileIndexer, StringComparison.Ordinal);
        Assert.Contains("RegexRegistry.CreateGeneratedCodePatternRegex", generatedCodeMatcher, StringComparison.Ordinal);
        Assert.DoesNotContain("new Regex(", dbReader, StringComparison.Ordinal);
        Assert.DoesNotContain("new Regex(", fileIndexer, StringComparison.Ordinal);
        Assert.DoesNotContain("new Regex(", generatedCodeMatcher, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticRegexPatternsUseRedactionTimeoutPolicy_Issue4149()
    {
        var checkedPatterns = 0;
        var violations = new List<string>();

        foreach (var relativePath in new[]
                 {
                     "src/CodeIndex/Diagnostics/DiagnosticRedactor.cs",
                     "src/CodeIndex/Mcp/AuditLogSink.cs"
                 })
        {
            var lines = File.ReadAllLines(RepositoryTestPaths.Combine(relativePath.Split('/')));
            for (var index = 0; index < lines.Length; index++)
            {
                if (!lines[index].Contains("static readonly Regex", StringComparison.Ordinal)
                    || !lines[index].Contains("= new(", StringComparison.Ordinal))
                {
                    continue;
                }

                checkedPatterns++;
                var block = string.Join('\n', lines.Skip(index).Take(6));
                if (!block.Contains("RegexTimeout", StringComparison.Ordinal)
                    && !block.Contains("RegexTimeoutPolicy.RedactionRegexTimeout", StringComparison.Ordinal))
                {
                    violations.Add($"{relativePath}:{index + 1}: {lines[index].Trim()}");
                }
            }
        }

        Assert.True(checkedPatterns > 0);
        Assert.Empty(violations);
    }

    [Fact]
    public void SearchAuditRegexRecipesDocumentRegistryException_Issue4149()
    {
        foreach (var recipeName in new[] { "risky-code", "dotnet-risk-patterns" })
        {
            var recipe = SearchAuditRecipes.All.Single(item => item.Name == recipeName);
            var query = recipe.Queries.Single(item => item.Name == "regex-construction");

            Assert.Contains(RegexRegistryPath, query.ExcludePaths);
            Assert.Contains(query.RiskEvidence, evidence => evidence.Contains("RegexRegistry.cs", StringComparison.Ordinal));
            Assert.Contains(query.RiskEvidence, evidence => evidence.Contains("bounded-regex-alias", StringComparison.Ordinal));
        }
    }

    private static string RelativeRepositoryPath(string path) =>
        Path.GetRelativePath(RepositoryTestPaths.Root, path).Replace('\\', '/');
}
