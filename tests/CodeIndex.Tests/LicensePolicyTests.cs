using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Guards licensing and distribution metadata from silently drifting back to
/// permissive productization defaults.
/// </summary>
public class LicensePolicyTests
{
    private static readonly string[] CanonicalLegalNoticeFiles =
    [
        "LICENSE",
        "COMMERCIAL_LICENSE.md",
        "INTEGRATION_POLICY.md",
        "TRADEMARKS.md",
        "LICENSES/FSL-1.1-ALv2.txt",
        "LICENSES/Apache-2.0.txt",
    ];

    private static readonly string[] RootLegalNoticeFiles =
    [
        "LICENSE",
        "COMMERCIAL_LICENSE.md",
        "INTEGRATION_POLICY.md",
        "TRADEMARKS.md",
    ];

    private static readonly string[] LicensePolicyWorkflowTriggerPaths =
    [
        "LICENSE",
        "LICENSES/**",
        "COMMERCIAL_LICENSE.md",
        "INTEGRATION_POLICY.md",
        "TRADEMARKS.md",
        "README.md",
        "USER_GUIDE.md",
        "DEVELOPER_GUIDE.md",
        "DISTRIBUTION.md",
        "docs/NUGET_README.md",
        "MAINTAINERS.md",
        "CONTRIBUTING.md",
        "src/CodeIndex/CodeIndex.csproj",
        "src/CodeIndex/Cli/ConsoleUi.cs",
        "install.sh",
        "install_modules/20-installer.sh",
        "install_modules/40-uninstall.sh",
        ".github/workflows/release.yml",
        ".github/workflows/license-policy.yml",
        "tests/CodeIndex.Tests/LicensePolicyTests.cs",
        "tests/CodeIndex.Tests/InstallScriptTests.cs",
        "tests/CodeIndex.Tests/ReleaseWorkflowTests.cs",
    ];

    [Fact]
    public void LicenseFile_UsesFslWithFutureApacheLicenseNotice()
    {
        var license = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "LICENSE"));

        Assert.Contains("CodeIndex is source-available under a Fair Source-style license.", license);
        Assert.Contains("Functional Source License, Version 1.1, ALv2 Future License (FSL-1.1-ALv2).", license);
        Assert.Contains("Copyright 2026 Widthdom.", license);
        Assert.Contains("LICENSES/FSL-1.1-ALv2.txt", license);
        Assert.Contains("LICENSES/Apache-2.0.txt", license);
        Assert.DoesNotContain("PolyForm Perimeter", license);
        Assert.DoesNotContain("MIT License", license);
    }

    [Fact]
    public void NuGetPackage_EmbedsCustomLicenseAndRequiresAcceptance()
    {
        var project = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "src", "CodeIndex", "CodeIndex.csproj"));

        Assert.Contains("fair-source", project);
        Assert.Contains("<PackageLicenseFile>LICENSE</PackageLicenseFile>", project);
        Assert.Contains("<PackageRequireLicenseAcceptance>true</PackageRequireLicenseAcceptance>", project);
        Assert.Contains(@"<None Include=""..\..\LICENSE"" Pack=""true"" PackagePath=""\""", project);
        Assert.Contains(@"<None Include=""..\..\COMMERCIAL_LICENSE.md"" Pack=""true"" PackagePath=""\""", project);
        Assert.Contains(@"<None Include=""..\..\INTEGRATION_POLICY.md"" Pack=""true"" PackagePath=""\""", project);
        Assert.Contains(@"<None Include=""..\..\TRADEMARKS.md"" Pack=""true"" PackagePath=""\""", project);
        Assert.Contains(@"<None Include=""..\..\LICENSES\FSL-1.1-ALv2.txt"" Pack=""true"" PackagePath=""LICENSES\""", project);
        Assert.Contains(@"<None Include=""..\..\LICENSES\Apache-2.0.txt"" Pack=""true"" PackagePath=""LICENSES\""", project);
        Assert.Equal(6, CountOccurrences(project, @"CopyToPublishDirectory=""PreserveNewest"""));
        Assert.DoesNotContain("<PackageLicenseExpression>MIT</PackageLicenseExpression>", project);
    }

    [Fact]
    public void Readme_AdvertisesFairSourceLicenseInBothLanguageSections()
    {
        var readme = File.ReadAllText(Path.Combine(GetRepositoryRoot(), "README.md"));

        Assert.Equal(2, CountOccurrences(readme, "License-FSL--1.1--ALv2-orange"));
        Assert.Contains("License and Fair Source Use", readme);
        Assert.Contains("ライセンスと Fair Source の扱い", readme);
        Assert.Equal(2, CountOccurrences(readme, "Fair Source-style software"));
        Assert.Contains("INTEGRATION_POLICY.md", readme);
        Assert.Contains("LICENSES/Apache-2.0.txt", readme);
        Assert.DoesNotContain("License-MIT", readme);
    }

    [Fact]
    public void ReleaseWorkflow_IsLimitedToCanonicalRepository()
    {
        var workflow = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "release.yml"));

        Assert.True(CountOccurrences(workflow, "if: github.repository == 'Widthdom/CodeIndex'") >= 3);
        Assert.Contains("environment: release-production", workflow);
        Assert.Contains("environment: nuget-production", workflow);
        Assert.Contains("LICENSE COMMERCIAL_LICENSE.md INTEGRATION_POLICY.md TRADEMARKS.md", workflow);
        Assert.Contains("LICENSES/FSL-1.1-ALv2.txt", workflow);
        Assert.Contains("LICENSES/Apache-2.0.txt", workflow);
    }

    [Fact]
    public void TrademarkAndCommercialPolicies_BlockCompetingDerivativeBranding()
    {
        var root = GetRepositoryRoot();
        var commercial = File.ReadAllText(Path.Combine(root, "COMMERCIAL_LICENSE.md"));
        var trademarks = File.ReadAllText(Path.Combine(root, "TRADEMARKS.md"));

        Assert.Contains("Allowed Without a Separate Agreement", commercial);
        Assert.Contains("commercial product or service", commercial);
        Assert.Contains("substitutes for CodeIndex", commercial);
        Assert.Contains("AI coding agents", commercial);
        Assert.Contains("compatible with CodeIndex", trademarks);
        Assert.Contains("confusingly similar name", trademarks);
    }

    [Fact]
    public void LicenseDistributionSurfacesStayAligned_Issue4172()
    {
        var root = GetRepositoryRoot();
        var project = ReadRepositoryFile(root, "src/CodeIndex/CodeIndex.csproj");
        var installer = ReadRepositoryFile(root, "install_modules/20-installer.sh");
        var uninstaller = ReadRepositoryFile(root, "install_modules/40-uninstall.sh");
        var releaseWorkflow = ReadRepositoryFile(root, ".github/workflows/release.yml");
        var policyWorkflow = ReadRepositoryFile(root, ".github/workflows/license-policy.yml");
        var readme = ReadRepositoryFile(root, "README.md");
        var userGuide = ReadRepositoryFile(root, "USER_GUIDE.md");
        var distribution = ReadRepositoryFile(root, "DISTRIBUTION.md");
        var nugetReadme = ReadRepositoryFile(root, "docs/NUGET_README.md");

        foreach (var legalNoticeFile in CanonicalLegalNoticeFiles)
        {
            var msbuildPath = legalNoticeFile.Replace("/", "\\", StringComparison.Ordinal);
            Assert.Contains($@"<None Include=""..\..\{msbuildPath}"" Pack=""true""", project);
            Assert.Contains(legalNoticeFile, releaseWorkflow);
            Assert.Contains(legalNoticeFile, policyWorkflow);
        }

        foreach (var legalNoticeFile in RootLegalNoticeFiles)
        {
            Assert.Contains($@"[ -f ""${{INSTALL_DIR}}/{legalNoticeFile}"" ] || return 1", installer);
            Assert.Contains($@"""${{INSTALL_DIR}}/{legalNoticeFile}""", uninstaller);
        }

        Assert.Contains(@"[ -f ""${INSTALL_DIR}/LICENSES/FSL-1.1-ALv2.txt"" ] || return 1", installer);
        Assert.Contains(@"[ -f ""${INSTALL_DIR}/LICENSES/Apache-2.0.txt"" ] || return 1", installer);
        Assert.Contains(@"local optional_assets=""LICENSE COMMERCIAL_LICENSE.md INTEGRATION_POLICY.md TRADEMARKS.md LICENSES""", installer);
        Assert.Contains(@"""${INSTALL_DIR}/LICENSES""", uninstaller);

        var (_, licenseSummary, licenseStderr) = ConsoleCapture.Capture(() =>
        {
            ConsoleUi.PrintLicenseSummary();
            return 0;
        });
        Assert.Equal(string.Empty, licenseStderr);
        AssertContainsAll(licenseSummary, CanonicalLegalNoticeFiles);
        Assert.Contains("distribution are allowed for non-competing purposes", licenseSummary);
        Assert.Contains("separate written agreement with Widthdom", licenseSummary);

        foreach (var triggerPath in LicensePolicyWorkflowTriggerPaths)
            Assert.Equal(2, CountOccurrences(policyWorkflow, $"- '{triggerPath}'"));
        Assert.Contains("actions/setup-dotnet@9a946fdbd5fb07b82b2f5a4466058b876ab72bb2 # v5.3.0", policyWorkflow);
        Assert.Contains("8.0.413", policyWorkflow);
        Assert.Contains("9.0.301", policyWorkflow);
        Assert.Contains("dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj --configuration Release --framework net8.0 --filter FullyQualifiedName~LicensePolicyTests --nologo", policyWorkflow);

        AssertContainsAll(readme, new[]
        {
            "License and Fair Source Use",
            "ライセンスと Fair Source の扱い",
            "FSL-1.1-ALv2",
            "COMMERCIAL_LICENSE.md",
            "INTEGRATION_POLICY.md",
            "TRADEMARKS.md",
        });
        AssertContainsAll(userGuide, CanonicalLegalNoticeFiles);
        AssertContainsAll(distribution, new[]
        {
            "Package metadata preserves license",
            "INTEGRATION_POLICY.md",
            "COMMERCIAL_LICENSE.md",
        });
        Assert.Contains("Integration Policy", nugetReadme);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }

    private static void AssertContainsAll(string haystack, IEnumerable<string> needles)
    {
        foreach (var needle in needles)
            Assert.Contains(needle, haystack);
    }

    private static string ReadRepositoryFile(string root, string relativePath)
        => File.ReadAllText(GetRepositoryPath(root, relativePath));

    private static string GetRepositoryPath(string root, string relativePath)
    {
        var path = root;
        foreach (var part in relativePath.Split('/'))
            path = Path.Combine(path, part);
        return path;
    }

    private static string GetRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodeIndex.sln")) || Directory.Exists(Path.Combine(dir.FullName, "src", "CodeIndex")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root / リポジトリルートを特定できませんでした");
    }
}
