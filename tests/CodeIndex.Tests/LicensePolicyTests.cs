using CodeIndex.Cli;

namespace CodeIndex.Tests;

/// <summary>
/// Guards licensing and distribution metadata from silently drifting back to
/// permissive productization defaults.
/// </summary>
[Collection("Console sensitive")]
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
        var license = RepositoryTestPaths.ReadText("LICENSE");

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
        var project = RepositoryTestPaths.ReadText("src", "CodeIndex", "CodeIndex.csproj");

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
        var readme = RepositoryTestPaths.ReadText("README.md");

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
        var workflow = RepositoryTestPaths.ReadReleaseWorkflow();

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
        var commercial = RepositoryTestPaths.ReadText("COMMERCIAL_LICENSE.md");
        var trademarks = RepositoryTestPaths.ReadText("TRADEMARKS.md");

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
        var project = RepositoryTestPaths.ReadText("src", "CodeIndex", "CodeIndex.csproj");
        var installer = RepositoryTestPaths.ReadText("install_modules", "20-installer.sh");
        var uninstaller = RepositoryTestPaths.ReadText("install_modules", "40-uninstall.sh");
        var releaseWorkflow = RepositoryTestPaths.ReadReleaseWorkflow();
        var policyWorkflow = RepositoryTestPaths.ReadWorkflow("license-policy.yml");
        var readme = RepositoryTestPaths.ReadText("README.md");
        var userGuide = RepositoryTestPaths.ReadText("USER_GUIDE.md");
        var distribution = RepositoryTestPaths.ReadText("DISTRIBUTION.md");
        var nugetReadme = RepositoryTestPaths.ReadText("docs", "NUGET_README.md");

        foreach (var legalNoticeFile in CanonicalLegalNoticeFiles)
        {
            var msbuildPath = legalNoticeFile.Replace("/", "\\", StringComparison.Ordinal);
            Assert.Contains($@"<None Include=""..\..\{msbuildPath}"" Pack=""true""", project);
            Assert.Contains(legalNoticeFile, releaseWorkflow);
            Assert.Contains(legalNoticeFile, policyWorkflow);
        }

        foreach (var legalNoticeFile in RootLegalNoticeFiles)
        {
            Assert.Contains($"\n        {legalNoticeFile} \\", installer);
            Assert.Contains($@"""${{INSTALL_DIR}}/{legalNoticeFile}""", uninstaller);
        }

        Assert.Contains("\n        LICENSES/FSL-1.1-ALv2.txt \\", installer);
        Assert.Contains("\n        LICENSES/Apache-2.0.txt; do", installer);
        Assert.Contains("verify_installed_manifest_asset \"$manifest_path\" \"$required_asset\" || return 1", installer);
        Assert.Contains(@"local optional_assets=""LICENSE COMMERCIAL_LICENSE.md INTEGRATION_POLICY.md TRADEMARKS.md LICENSES MANIFEST.sha256""", installer);
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
        Assert.Contains("cache: true", policyWorkflow);
        Assert.Contains("cache-dependency-path: '**/packages.lock.json'", policyWorkflow);
        Assert.Contains("dotnet restore tests/CodeIndex.Tests/CodeIndex.Tests.csproj -p:RestoreTargetFrameworks=net8.0 --locked-mode", policyWorkflow);
        Assert.Contains("dotnet test tests/CodeIndex.Tests/CodeIndex.Tests.csproj --configuration Release --framework net8.0 --filter FullyQualifiedName~LicensePolicyTests --no-restore --nologo", policyWorkflow);

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

}
