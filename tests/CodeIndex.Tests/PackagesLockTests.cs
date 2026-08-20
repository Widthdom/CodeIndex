using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace CodeIndex.Tests;

public class PackagesLockTests
{
    private static readonly string[] ProjectFiles =
    [
        "examples/hooks/SamplePostExtractionHook.csproj",
        "src/CodeIndex/CodeIndex.csproj",
        "tests/CodeIndex.Tests/CodeIndex.Tests.csproj",
        "tools/CodeIndex.Changelog/CodeIndex.Changelog.csproj",
        "tools/CodeIndex.PackageNormalize/CodeIndex.PackageNormalize.csproj",
        "tools/CodeIndex.TestTelemetry/CodeIndex.TestTelemetry.csproj",
    ];

    private static readonly string[] SolutionRestoreLockFiles =
    [
        "src/CodeIndex/packages.lock.json",
        "tests/CodeIndex.HookIsolationFixture/packages.lock.json",
        "tests/CodeIndex.PluginIsolationFixture/packages.lock.json",
        "tests/CodeIndex.Tests/packages.lock.json",
        "tools/CodeIndex.Changelog/packages.lock.json",
        "tools/CodeIndex.PackageNormalize/packages.lock.json",
        "tools/CodeIndex.TestTelemetry/packages.lock.json",
    ];

    private const string SolutionRestoreLockHashExpression =
        "${{ hashFiles('src/CodeIndex/packages.lock.json', 'tests/CodeIndex.HookIsolationFixture/packages.lock.json', 'tests/CodeIndex.PluginIsolationFixture/packages.lock.json', 'tests/CodeIndex.Tests/packages.lock.json', 'tools/CodeIndex.Changelog/packages.lock.json', 'tools/CodeIndex.PackageNormalize/packages.lock.json', 'tools/CodeIndex.TestTelemetry/packages.lock.json') }}";

    private static readonly string SolutionRestoreCacheDependencyPath =
        "cache-dependency-path: |\n" +
        string.Join("\n", SolutionRestoreLockFiles.Select(static path => $"            {path}"));

    [Fact]
    public void DirectoryBuildProps_EnablesLockFilesWithoutForcingLocalLockedMode()
    {
        var props = XDocument.Load(RepositoryPath("Directory.Build.props"));

        Assert.Equal("true", ElementValue(props, "RestorePackagesWithLockFile"));
        Assert.Null(ElementValue(props, "RestoreLockedMode"));
        Assert.Equal("true", ElementValue(props, "TreatWarningsAsErrors"));
        Assert.Equal("false", ElementValue(props, "ILLinkTreatWarningsAsErrors"));
    }

    [Fact]
    public void PackageReferences_UseExplicitPinnedVersions()
    {
        foreach (var projectFile in ProjectFiles)
        {
            var document = XDocument.Load(RepositoryPath(projectFile));
            foreach (var packageReference in document.Descendants("PackageReference"))
            {
                var include = packageReference.Attribute("Include")?.Value;
                var version = packageReference.Attribute("Version")?.Value ?? packageReference.Element("Version")?.Value;

                Assert.False(string.IsNullOrWhiteSpace(include), $"{projectFile} has a PackageReference without Include.");
                Assert.False(string.IsNullOrWhiteSpace(version), $"{projectFile} PackageReference '{include}' must use an explicit Version.");
                Assert.DoesNotContain("*", version!, StringComparison.Ordinal);
                Assert.DoesNotContain("[", version!, StringComparison.Ordinal);
                Assert.DoesNotContain("]", version!, StringComparison.Ordinal);
                Assert.DoesNotContain(",", version!, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void CodeIndexProject_PinsRuntimeTrimAotAndPackageMetadataPolicy()
    {
        var project = XDocument.Load(RepositoryPath("src/CodeIndex/CodeIndex.csproj"));

        Assert.Equal("net8.0", ElementValue(project, "TargetFramework"));
        Assert.Equal("linux-musl-x64;linux-musl-arm64", ElementValue(project, "RuntimeIdentifiers"));
        Assert.Equal("true", ElementValue(project, "Deterministic"));
        Assert.Equal("true", ElementValue(project, "PublishRepositoryUrl"));
        Assert.Contains("version.json", RequiredElementValue(project, "VersionJsonPath"), StringComparison.Ordinal);
        Assert.Contains("Regex", RequiredElementValue(project, "Version"), StringComparison.Ordinal);

        var ilLink = PackageReference(project, "Microsoft.NET.ILLink.Tasks");
        Assert.StartsWith("8.", RequiredAttributeValue(ilLink, "Version"), StringComparison.Ordinal);
        Assert.Equal("All", RequiredAttributeValue(ilLink, "PrivateAssets"));

        var sourceLink = PackageReference(project, "Microsoft.SourceLink.GitHub");
        Assert.Equal("All", RequiredAttributeValue(sourceLink, "PrivateAssets"));

        Assert.Equal("true", ElementValue(project, "IsTrimmable"));
        Assert.Equal("'$(PublishTrimmed)' == 'true' or '$(PublishAot)' == 'true'", RequiredAttributeValue(Element(project, "IsTrimmable"), "Condition"));
        Assert.Equal("false", ElementValue(project, "EnableTrimAnalyzer"));
        Assert.Equal("'$(PublishTrimmed)' == 'true'", RequiredAttributeValue(Element(project, "EnableTrimAnalyzer"), "Condition"));
        Assert.Equal("'$(PublishAot)' == 'true'", RequiredAttributeValue(Element(project, "EnableAotAnalyzer"), "Condition"));
    }

    [Fact]
    public void RestoreSurfaces_UseLockedModeExactCacheKeysAndDockerRidRestore()
    {
        var dotnetWorkflow = RepositoryTestPaths.ReadNormalizedDotnetWorkflow();
        var releaseWorkflow = RepositoryTestPaths.ReadNormalizedWorkflow("release.yml");
        var codeqlWorkflow = RepositoryTestPaths.ReadNormalizedWorkflow("codeql.yml");
        var licenseWorkflow = RepositoryTestPaths.ReadNormalizedWorkflow("license-policy.yml");
        var mutationWorkflow = RepositoryTestPaths.ReadNormalizedWorkflow("mutation-testing.yml");
        var dockerfile = RepositoryTestPaths.ReadNormalizedText("Dockerfile");

        Assert.Contains("dotnet restore CodeIndex.sln --locked-mode", dotnetWorkflow, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet restore tests/CodeIndex.Tests/CodeIndex.Tests.csproj -p:RestoreTargetFrameworks=${{ matrix.test-framework }} --locked-mode",
            dotnetWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet restore tests/CodeIndex.Tests/CodeIndex.Tests.csproj -p:RestoreTargetFrameworks=net8.0 --locked-mode",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet restore src/CodeIndex/CodeIndex.csproj --locked-mode",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("dotnet restore CodeIndex.sln --locked-mode", codeqlWorkflow, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet restore tests/CodeIndex.Tests/CodeIndex.Tests.csproj -p:RestoreTargetFrameworks=net8.0 --locked-mode",
            licenseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains("dotnet restore CodeIndex.sln --locked-mode", mutationWorkflow, StringComparison.Ordinal);

        foreach (var setupDotnetCachedWorkflow in new[] { codeqlWorkflow, licenseWorkflow })
        {
            Assert.Contains("cache: true", setupDotnetCachedWorkflow, StringComparison.Ordinal);
            Assert.Contains(SolutionRestoreCacheDependencyPath, setupDotnetCachedWorkflow, StringComparison.Ordinal);
        }
        Assert.Contains(
            "key: ${{ runner.os }}-dotnet-nuget-" + SolutionRestoreLockHashExpression,
            dotnetWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "path: ~/.nuget/packages\n" +
            "          key: ${{ runner.os }}-mutation-nuget-" + SolutionRestoreLockHashExpression,
            mutationWorkflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Cache Stryker tool and NuGet packages", mutationWorkflow, StringComparison.Ordinal);
        Assert.Contains(
            "- name: Cache test-lane NuGet packages\n" +
            "        if: matrix.run_tests",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "key: ${{ runner.os }}-release-nuget-" + SolutionRestoreLockHashExpression,
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "- name: Cache publish-only NuGet packages\n" +
            "        if: ${{ !matrix.run_tests }}",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "key: ${{ runner.os }}-release-publish-nuget-${{ hashFiles('src/CodeIndex/packages.lock.json') }}",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "cache-dependency-path: tools/CodeIndex.Changelog/packages.lock.json",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "dotnet restore tools/CodeIndex.Changelog/CodeIndex.Changelog.csproj --locked-mode",
            releaseWorkflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "cache-dependency-path: |\n" +
            "            src/CodeIndex/packages.lock.json\n" +
            "            tools/CodeIndex.PackageNormalize/packages.lock.json",
            releaseWorkflow,
            StringComparison.Ordinal);

        foreach (var workflow in new[]
        {
            dotnetWorkflow,
            releaseWorkflow,
            codeqlWorkflow,
            licenseWorkflow,
            mutationWorkflow,
        })
        {
            Assert.DoesNotContain("'**/packages.lock.json'", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("global.json", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("examples/hooks/packages.lock.json", workflow, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("dotnet restore src/CodeIndex/CodeIndex.csproj \\\n      --runtime \"$rid\"", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("--runtime \"$rid\" \\\n      --no-restore", dockerfile, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet restore src/CodeIndex/CodeIndex.csproj \\\n      -p:RuntimeIdentifier=\"$rid\" \\\n      --locked-mode",
            dockerfile,
            StringComparison.Ordinal);
        Assert.Contains("-p:RuntimeIdentifier=\"$rid\" \\\n      --no-restore", dockerfile, StringComparison.Ordinal);
        Assert.Contains("linux-musl-x64", dockerfile, StringComparison.Ordinal);
        Assert.Contains("linux-musl-arm64", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void TestProject_TargetsNet8AndNet9WithLockedCompatibilityReferences()
    {
        var project = XDocument.Load(RepositoryPath("tests/CodeIndex.Tests/CodeIndex.Tests.csproj"));

        Assert.Equal("net8.0;net9.0", ElementValue(project, "TargetFrameworks"));
        Assert.Equal("4.3.4", RequiredAttributeValue(PackageReference(project, "System.Net.Http"), "Version"));
        Assert.Equal("4.3.1", RequiredAttributeValue(PackageReference(project, "System.Text.RegularExpressions"), "Version"));
    }

    [Theory]
    [InlineData("System.Net.Http", "4.3.4")]
    [InlineData("System.Text.RegularExpressions", "4.3.1")]
    public void CodeIndexTests_Net9LockFile_IncludesDirectCompatibilityReferences(string packageName, string version)
    {
        var projectDirectory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../.."));
        var lockFilePath = Path.Combine(projectDirectory, "packages.lock.json");

        using var document = JsonDocument.Parse(File.ReadAllText(lockFilePath));
        var net9Dependencies = document.RootElement
            .GetProperty("dependencies")
            .GetProperty("net9.0");

        Assert.True(net9Dependencies.TryGetProperty(packageName, out var package), $"{packageName} is missing from net9.0 lock dependencies.");
        Assert.Equal("Direct", package.GetProperty("type").GetString());
        Assert.Equal($"[{version}, )", package.GetProperty("requested").GetString());
        Assert.Equal(version, package.GetProperty("resolved").GetString());
    }

    private static string RepositoryPath(string relativePath) =>
        RepositoryTestPaths.Combine(relativePath.Split('/'));

    private static XElement Element(XDocument document, string name)
    {
        var element = document.Descendants(name).SingleOrDefault();
        return element ?? throw new InvalidOperationException($"Element '{name}' is missing.");
    }

    private static string? ElementValue(XDocument document, string name) =>
        document.Descendants(name).SingleOrDefault()?.Value.Trim();

    private static string RequiredElementValue(XDocument document, string name)
    {
        var value = ElementValue(document, name);
        Assert.False(string.IsNullOrWhiteSpace(value), $"Element '{name}' must have a value.");
        return value!;
    }

    private static string RequiredAttributeValue(XElement element, string name)
    {
        var value = element.Attribute(name)?.Value;
        Assert.False(string.IsNullOrWhiteSpace(value), $"Attribute '{name}' must have a value.");
        return value!;
    }

    private static XElement PackageReference(XDocument document, string packageName)
    {
        var element = document.Descendants("PackageReference")
            .SingleOrDefault(package => string.Equals(package.Attribute("Include")?.Value, packageName, StringComparison.Ordinal));
        return element ?? throw new InvalidOperationException($"PackageReference '{packageName}' is missing.");
    }
}
