using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodeIndex.Cli;
using CodeIndex.Database;

namespace CodeIndex.Tests;

public sealed class DocumentationDriftTests
{
    private static readonly Regex WorkflowPathReferenceRegex = new(
        @"(?<path>\.codex/workflows/[A-Za-z0-9_.-]+\.md)",
        RegexOptions.Compiled);

    private static readonly Regex MarkdownFileReferenceRegex = new(
        @"`(?<file>[A-Za-z0-9_.-]+\.md)`",
        RegexOptions.Compiled);

    private static readonly Regex CdidxCommandLineRegex = new(
        @"^\s*(?:[$>]\s*)?(?:[A-Z_][A-Z0-9_]*=\S+\s+)*(?<prefix>cdidx|dotnet\s+\./src/CodeIndex/bin/Debug/net8\.0/cdidx\.dll|dotnet\s+run\s+--project\s+src/CodeIndex\s+--)\s+(?<token>[^\s`|;&]+)",
        RegexOptions.Compiled);

    private static readonly Regex InlineCdidxCommandReferenceRegex = new(
        @"`(?:[A-Z_][A-Z0-9_]*=\S+\s+)*(?<prefix>cdidx|dotnet\s+\./src/CodeIndex/bin/Debug/net8\.0/cdidx\.dll|dotnet\s+run\s+--project\s+src/CodeIndex\s+--)\s+(?<token>[^\s`|;&]+)[^`]*`",
        RegexOptions.Compiled);

    private static readonly Regex LocalRepositoryCdidxInvocationRegex = new(
        @"dotnet\s+\./src/CodeIndex/bin/Debug/net8\.0/cdidx\.dll(?:\s+(?<token>[^\s`|;&]+))?",
        RegexOptions.Compiled);

    private static readonly Regex ErrorCodeTableRowRegex = new(
        @"^\| `(?<code>E[0-9]{3}_[A-Z0-9_]+)` \|",
        RegexOptions.Compiled | RegexOptions.Multiline);

    private static readonly HashSet<string> KnownCdidxEntrypointTokens = new(StringComparer.Ordinal)
    {
        "--completions",
        "--check-updates",
        "--debug-unsafe",
        "--help",
        "--help-all",
        "--help-flags",
        "--strict-version",
        "--sushi",
        "--version",
        "audit",
        "backfill-fold",
        "batch",
        "callees",
        "callers",
        "completions",
        "config",
        "db",
        "definition",
        "deps",
        "diff",
        "doctor",
        "excerpt",
        "export",
        "files",
        "find",
        "goto",
        "help",
        "hooks",
        "hotspots",
        "impact",
        "import",
        "index",
        "inspect",
        "languages",
        "license",
        "lsp",
        "map",
        "mcp",
        "optimize",
        "outline",
        "recipes",
        "references",
        "report",
        "search",
        "status",
        "suggestions",
        "symbols",
        "test-extractor",
        "unused",
        "upgrade",
        "vacuum",
        "validate",
        "validate-config",
        "workspace",
    };

    [Fact]
    public void WorkflowIndexAndDirectoryMap_StaySynchronized_Issue4160()
    {
        var workflowPaths = Directory
            .EnumerateFiles(RepositoryTestPaths.Combine(".codex", "workflows"), "*.md")
            .Select(ToRepositoryRelativePath)
            .Where(path => !StringComparer.Ordinal.Equals(path, ".codex/workflows/README.md"))
            .Order(StringComparer.Ordinal)
            .ToArray();
        var workflowFileNames = workflowPaths
            .Select(path => Path.GetFileName(path) ?? throw new InvalidOperationException($"Missing file name for {path}."))
            .ToHashSet(StringComparer.Ordinal);

        var guideReferences = ExtractWorkflowPathReferences("AGENT_GUIDE.md");
        var workflowReadmeReferences = ExtractWorkflowReadmeReferences(workflowFileNames);

        foreach (var reference in guideReferences.Concat(workflowReadmeReferences).Distinct(StringComparer.Ordinal))
        {
            var absolutePath = RepositoryTestPaths.Combine(reference.Split('/'));
            Assert.True(File.Exists(absolutePath), $"{reference} is referenced but does not exist.");
            Assert.NotEqual(0, new FileInfo(absolutePath).Length);
        }

        Assert.Empty(workflowPaths.Except(guideReferences, StringComparer.Ordinal));
        Assert.Empty(workflowPaths.Except(workflowReadmeReferences, StringComparer.Ordinal));
    }

    [Fact]
    public void DocumentedCdidxExamples_UseKnownEntrypointTokens_Issue4160()
    {
        var failures = new List<string>();

        foreach (var relativePath in EnumerateDocumentationCommandReferenceFiles())
        {
            var lines = RepositoryTestPaths.ReadNormalizedLines(relativePath.Split('/'));
            var inFencedCodeBlock = false;

            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                var line = lines[lineNumber];
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inFencedCodeBlock = !inFencedCodeBlock;
                    continue;
                }

                foreach (var match in EnumerateCdidxCommandMatches(line, inFencedCodeBlock))
                {
                    var token = NormalizeCdidxToken(match.Groups["token"].Value);
                    if (ShouldSkipCdidxEntrypointToken(token))
                        continue;

                    if (!KnownCdidxEntrypointTokens.Contains(token))
                    {
                        failures.Add(
                            $"{relativePath}:{lineNumber + 1}: unknown cdidx command/reference token '{token}' in '{match.Value.Trim()}'.");
                    }
                }
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void RepositoryDogfoodManifestAndGuidance_StaySynchronized_Issue5062()
    {
        using var manifest = JsonDocument.Parse(RepositoryTestPaths.ReadText("cdidx.workspace.json"));
        var root = manifest.RootElement;

        Assert.Equal("single", root.GetProperty("index_strategy").GetString());
        Assert.Equal("codeindex.db", root.GetProperty("default_db_name").GetString());
        Assert.Equal(
            ["src/CodeIndex", "tests/CodeIndex.Tests"],
            root.GetProperty("members").EnumerateArray().Select(member => member.GetString()).ToArray());

        const string canonicalDb = ".cdidx/codeindex.db";
        const string rootStatus = "dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll status --check --db .cdidx/codeindex.db --json";
        const string workspaceStatus = "dotnet ./src/CodeIndex/bin/Debug/net8.0/cdidx.dll workspace status --check --json";
        var guidancePaths = new[]
        {
            "AGENT_GUIDE.md",
            "SELF_IMPROVEMENT.md",
            "DEVELOPER_GUIDE.md",
            ".codex/workflows/issue-fix.md",
        };

        foreach (var relativePath in guidancePaths)
        {
            var content = RepositoryTestPaths.ReadText(relativePath.Split('/'));
            Assert.Contains(canonicalDb, content, StringComparison.Ordinal);
            Assert.Contains(rootStatus, content, StringComparison.Ordinal);
            Assert.Contains(workspaceStatus, content, StringComparison.Ordinal);
        }

        var unpinnedInvocations = new List<string>();
        foreach (var relativePath in EnumerateDocumentationCommandReferenceFiles())
        {
            var lines = RepositoryTestPaths.ReadNormalizedLines(relativePath.Split('/'));
            for (var lineNumber = 0; lineNumber < lines.Length; lineNumber++)
            {
                var line = lines[lineNumber];
                foreach (Match match in LocalRepositoryCdidxInvocationRegex.Matches(line))
                {
                    var token = NormalizeCdidxToken(match.Groups["token"].Value);
                    if (string.IsNullOrWhiteSpace(token) || RepositoryDogfoodCommandDoesNotSelectDatabase(token))
                        continue;

                    if (!line.Contains("--db .cdidx/codeindex.db", StringComparison.Ordinal)
                        && !line.Contains("--db=.cdidx/codeindex.db", StringComparison.Ordinal))
                    {
                        unpinnedInvocations.Add(
                            $"{relativePath}:{lineNumber + 1}: repository dogfood command does not select {canonicalDb}: {line.Trim()}");
                    }
                }
            }
        }

        Assert.Empty(unpinnedInvocations);
    }

    [Fact]
    public void PreparedCommandCacheDefault_DocumentationMatchesRuntime()
    {
        var content = RepositoryTestPaths.ReadText("DEVELOPER_GUIDE.md");
        var documentedRow =
            $"| `CDIDX_PREPARED_COMMAND_CACHE_CAPACITY` | `{PreparedCommandCache.DefaultCapacity}` |";

        Assert.Equal(2, content.Split(documentedRow, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void UserGuide_ErrorCodeTablesMatchCommandErrorCodes_Issue4644()
    {
        var content = RepositoryTestPaths.ReadNormalizedText("USER_GUIDE.md");
        var expectedCodes = typeof(CommandErrorCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var englishCodes = ExtractErrorCodesFromSection(content, "### Error codes", "### Debugging reader errors");
        var japaneseCodes = ExtractErrorCodesFromSection(content, "### エラーコード", "### reader エラーのデバッグ");

        Assert.Equal(expectedCodes, englishCodes);
        Assert.Equal(expectedCodes, japaneseCodes);
    }

    [Theory]
    [InlineData("README.md", "## Quick Start", "## すぐに試す")]
    [InlineData("USER_GUIDE.md", "## Why cdidx", "## なぜ cdidx なのか")]
    [InlineData("DEVELOPER_GUIDE.md", "## Build & Test", "## ビルド・テスト")]
    [InlineData("TESTING_GUIDE.md", "## Test Layout", "## テスト構成")]
    [InlineData("docs/large-file-decomposition-plan.md", "## Baseline", "## ベースライン")]
    [InlineData("docs/test-doc-maintenance-plan.md", "## Baseline", "## ベースライン")]
    public void BilingualGuides_KeepRepresentativeEnglishAndJapaneseSections_Issue4160(
        string relativePath,
        string englishHeading,
        string japaneseHeading)
    {
        var content = RepositoryTestPaths.ReadText(relativePath.Split('/'));

        Assert.Contains("日本語版はこちら / Japanese version", content, StringComparison.Ordinal);
        Assert.Contains(englishHeading, content, StringComparison.Ordinal);
        Assert.Contains(japaneseHeading, content, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseAndChangelogWorkflowDocs_KeepFragmentContractsVisible_Issue4160()
    {
        var issueFix = RepositoryTestPaths.ReadText(".codex", "workflows", "issue-fix.md");
        var fragment = RepositoryTestPaths.ReadText(".codex", "workflows", "changelog-fragment.md");
        var release = RepositoryTestPaths.ReadText(".codex", "workflows", "release-changelog.md");

        Assert.Contains("changelog.d/unreleased/", issueFix, StringComparison.Ordinal);
        Assert.Contains("changelog.d/unreleased/", fragment, StringComparison.Ordinal);
        Assert.Contains("## English", fragment, StringComparison.Ordinal);
        Assert.Contains("## 日本語", fragment, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet run --project tools/CodeIndex.Changelog -- check",
            fragment,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet run --project tools/CodeIndex.Changelog -- prepare",
            release,
            StringComparison.Ordinal);
        Assert.Contains("aggregate fragments into both the English and 日本語 sections", release, StringComparison.Ordinal);
        Assert.Contains("CHANGELOG.md", release, StringComparison.Ordinal);
    }

    private static HashSet<string> ExtractWorkflowPathReferences(string relativePath)
    {
        var content = RepositoryTestPaths.ReadText(relativePath.Split('/'));
        return WorkflowPathReferenceRegex
            .Matches(content)
            .Select(match => match.Groups["path"].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string[] ExtractErrorCodesFromSection(string content, string heading, string nextHeading)
    {
        var sectionStart = content.IndexOf(heading, StringComparison.Ordinal);
        if (sectionStart < 0)
            throw new InvalidOperationException($"Missing documentation heading '{heading}'.");

        var sectionEnd = content.IndexOf(nextHeading, sectionStart + heading.Length, StringComparison.Ordinal);
        if (sectionEnd < 0)
            throw new InvalidOperationException($"Missing documentation heading '{nextHeading}'.");

        return ErrorCodeTableRowRegex
            .Matches(content[sectionStart..sectionEnd])
            .Select(match => match.Groups["code"].Value)
            .ToArray();
    }

    private static HashSet<string> ExtractWorkflowReadmeReferences(HashSet<string> workflowFileNames)
    {
        var content = RepositoryTestPaths.ReadText(".codex", "workflows", "README.md");
        return MarkdownFileReferenceRegex
            .Matches(content)
            .Select(match => match.Groups["file"].Value)
            .Where(workflowFileNames.Contains)
            .Select(fileName => $".codex/workflows/{fileName}")
            .ToHashSet(StringComparer.Ordinal);
    }

    private static IEnumerable<string> EnumerateDocumentationCommandReferenceFiles()
    {
        var rootFiles = new[]
        {
            ".codex/README.md",
            "AGENT_GUIDE.md",
            "README.md",
            "USER_GUIDE.md",
            "DEVELOPER_GUIDE.md",
            "TESTING_GUIDE.md",
            "SELF_IMPROVEMENT.md",
            "INTEGRATION_POLICY.md",
            "DISTRIBUTION.md",
            "CLOUD_BOOTSTRAP_PROMPT.md",
        };

        foreach (var relativePath in rootFiles)
            yield return relativePath;

        foreach (var workflowPath in Directory.EnumerateFiles(RepositoryTestPaths.Combine(".codex", "workflows"), "*.md"))
            yield return ToRepositoryRelativePath(workflowPath);

        foreach (var docsPath in Directory.EnumerateFiles(RepositoryTestPaths.Combine("docs"), "*.md"))
            yield return ToRepositoryRelativePath(docsPath);
    }

    private static IEnumerable<Match> EnumerateCdidxCommandMatches(string line, bool includeCommandLineMatches)
    {
        if (includeCommandLineMatches)
        {
            var lineMatch = CdidxCommandLineRegex.Match(line);
            if (lineMatch.Success)
                yield return lineMatch;
        }

        foreach (Match match in InlineCdidxCommandReferenceRegex.Matches(line))
            yield return match;
    }

    private static string NormalizeCdidxToken(string token)
    {
        return token.Trim().TrimEnd('.', ',', ':', ')', ']');
    }

    private static bool ShouldSkipCdidxEntrypointToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
            return true;

        if (token.StartsWith('v') && token.Length > 1 && char.IsDigit(token[1]))
            return true;

        return token[0] is '.' or '/' or '\\' or '~' or '$' or '%' or '<' or '[' or '{' or '"';
    }

    private static bool RepositoryDogfoodCommandDoesNotSelectDatabase(string token)
    {
        return token is
            "--check-updates" or
            "--completions" or
            "--help" or
            "--help-all" or
            "--help-flags" or
            "--sushi" or
            "--version" or
            "completions" or
            "config" or
            "help" or
            "languages" or
            "license" or
            "test-extractor" or
            "upgrade" or
            "validate-config" or
            "workspace";
    }

    private static string ToRepositoryRelativePath(string absolutePath)
    {
        return Path.GetRelativePath(RepositoryTestPaths.Root, absolutePath)
            .Replace(Path.DirectorySeparatorChar, '/');
    }
}
