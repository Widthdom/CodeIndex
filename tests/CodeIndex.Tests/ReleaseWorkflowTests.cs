using CodeIndex.PackageNormalize;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeIndex.Tests;

public partial class ReleaseWorkflowTests
{
    [Fact]
    public void ReleaseWorkflow_CachesAndRestoresCuratedNotesToolOnce()
    {
        var workflow = ReadReleaseWorkflow();

        AssertContainsAll(
            workflow,
            "cache-dependency-path: tools/CodeIndex.Changelog/packages.lock.json",
            "dotnet restore tools/CodeIndex.Changelog/CodeIndex.Changelog.csproj --locked-mode",
            "dotnet run --project tools/CodeIndex.Changelog --no-restore -- release-notes");
    }

    [Fact]
    public void ReleaseWorkflow_ReusesLockedRestoreForTests()
    {
        var workflow = ReadReleaseWorkflow();

        AssertContainsAll(
            workflow,
            "dotnet restore CodeIndex.sln --locked-mode",
            "dotnet build CodeIndex.sln --configuration Release --no-restore",
            "dotnet test CodeIndex.sln --configuration Release --no-build --no-restore --nologo");
    }

    [Fact]
    public void ReleaseWorkflow_PublishesTrimmedSelfContainedBinariesAndVerifiesCliJson()
    {
        var workflow = ReadReleaseWorkflow();

        AssertContainsAll(
            workflow,
            "-p:PublishTrimmed=true",
            "status --json",
            "Expected status --json to exit 0",
            "status --json stdout did not include files",
            "status --json stdout did not include version");
        AssertDoesNotContainAny(
            workflow,
            "-p:PublishTrimmed=false",
            "Expected status --json to fail on the trimmed self-contained release",
            "Expected status --json to exit 4",
            "Error [E009_FEATURE_UNAVAILABLE]: --json is not available on this trimmed build.",
            "Hint: use `cdidx mcp` for structured output");
    }

    [Fact]
    public void ReleaseWorkflow_VerifiesPublishedInstallForTheCurrentRid()
    {
        var workflow = ReadReleaseWorkflow();

        AssertContainsAll(
            workflow,
            "expected_rids=\"linux-x64 linux-arm64 osx-arm64 win-x64 win-arm64\"",
            "asset=\"CodeIndex-${rid}.zip\"",
            "asset=\"CodeIndex-${rid}.tar.gz\"",
            "Missing release archive for ${rid}",
            "CodeIndex-osx-x64.*",
            "native_asset=\"libe_sqlite3.so\"",
            "for asset in \"$binary_name\" \"$native_asset\"");
    }

    // Issue #3077: the Homebrew formula installs from the same self-contained
    // release archives as install.sh, so it must place SQLitePCLRaw's native
    // e_sqlite3 library beside cdidx and run a SQLite-touching smoke test.
    // Issue #3077 対応: Homebrew formula は install.sh と同じ self-contained
    // release archive から導入するため、SQLitePCLRaw の native e_sqlite3
    // ライブラリを cdidx の隣へ配置し、SQLite に触る smoke test を実行する必要がある。
    [Fact]
    public void ReleaseWorkflow_HomebrewFormulaInstallsNativeSqliteAssetAndTouchesSqlite()
    {
        var workflow = ReadReleaseWorkflow();

        AssertContainsAll(
            workflow,
            "Download release artifacts for checksum calculation",
            "actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c # v8.0.1",
            "sha_for_artifact()",
            "find \"$artifact_root\" -type f -name \"$asset\" -print -quit",
            "credential_helper='!f() { echo username=x-access-token; echo \"password=${TAP_TOKEN}\"; }; f'",
            "trap cleanup EXIT",
            "native_sqlite_asset = OS.mac? ? \"libe_sqlite3.dylib\" : \"libe_sqlite3.so\"",
            "bin.install native_sqlite_asset",
            "assert_predicate bin/native_sqlite_asset, :exist?",
            "(testpath/\"Sample.cs\").write",
            "system \"#{bin}/cdidx\", testpath.to_s",
            "shell_output(\"#{bin}/cdidx status --json\")");
        AssertDoesNotContainAny(
            workflow,
            "CHECKSUMS_URL",
            "https://x-access-token:${TAP_TOKEN}@github.com/Widthdom/homebrew-tap.git");
    }

    // Issue #1553: releases must ship a CycloneDX SBOM so enterprise consumers
    // (SOC2/FedRAMP reviewers, Snyk/Trivy/Grype scanners) can verify transitive
    // dependencies and bundled SQLitePCLRaw native assets without re-deriving
    // them from .deps.json. The workflow contract is: generate one SBOM per
    // release on the linux-x64 lane (content is RID-independent), upload it as
    // an artifact, copy it into release-files so sha256sums.txt covers it, and
    // ship it alongside the tarballs/zips on the GitHub release.
    // Issue #1553 対応: リリースに CycloneDX SBOM を同梱し、SOC2/FedRAMP 等の
    // コンプライアンスレビューや Snyk/Trivy/Grype 等のスキャナーが .deps.json
    // から再構築せずに推移的依存と SQLitePCLRaw ネイティブアセットを検証できる
    // ようにする。workflow 契約は、RID 非依存内容のため linux-x64 lane で 1 回
    // だけ生成し、artifact として upload、release-files にコピーして
    // sha256sums.txt の対象に含め、tarball/zip と一緒に GitHub release に同梱、
    // という流れである。
    [Fact]
    public void ReleaseWorkflow_GeneratesCycloneDxSbomAndShipsItAsReleaseAsset()
    {
        var workflow = ReadReleaseWorkflow();

        // Pin the global tool to a known version so an upstream major release
        // cannot silently shift the CLI surface (flag renames have happened
        // between v4 -> v5 -> v6: `--json` was removed in favor of
        // `--output-format Json`). 6.x is the current stable major and
        // supports the modern `-o / -fn / -F / -t` flag surface.
        // upstream の major リリースで CLI フラグが silent に変わるのを防ぐ
        // ため、global tool をバージョン固定する (`--json` は v4→v5 で廃止され
        // `--output-format Json` に置き換わったように、major 間で flag が
        // 変更されている)。6.x は現行の安定 major で、`-o / -fn / -F / -t`
        // 系のモダンフラグを備える。
        AssertContainsAll(
            workflow,
            "dotnet tool install --global CycloneDX --version 6.2.0",
            "dotnet-CycloneDX src/CodeIndex/CodeIndex.csproj",
            "--output-format Json",
            "--exclude-test-projects",
            "cdidx.sbom.cdx.json",
            "CodeIndex-sbom",
            "matrix.rid == 'linux-x64'",
            "'*.cdx.json'");
    }

    // Issue #2042: NuGet publishing must fail before pack/push when the tag,
    // version.json, or NuGet package state is inconsistent. A duplicate NuGet
    // version is not a harmless re-run condition because it can mask tagging or
    // version-sync mistakes.
    // Issue #2042 対応: tag / version.json / NuGet package 状態が不整合な場合、
    // pack/push 前に失敗させる。NuGet の duplicate version は harmless な再実行
    // 条件ではなく、tag や version sync の誤りを隠し得る。
    [Fact]
    public void ReleaseWorkflow_ValidatesNuGetVersionBeforePublishing()
    {
        var workflow = ReadReleaseWorkflow();

        AssertContainsAll(
            workflow,
            "Release tag must be a v-prefixed SemVer version",
            "jq -r '.version // empty' version.json",
            "does not match release tag",
            "https://api.nuget.org/v3-flatcontainer/cdidx/${VERSION}/cdidx.${VERSION}.nupkg",
            "response_headers=\"$(mktemp \"${RUNNER_TEMP:-/tmp}/cdidx-nuget-head.XXXXXX\")\"",
            "cat \"$response_headers\"",
            "NuGet package cdidx ${VERSION} is already published",
            "Expected packed package ${expected_package} was not produced",
            "Attest NuGet package artifacts",
            "nupkg/*.nupkg",
            "nupkg/*.snupkg",
            "Resolve NuGet trusted publishing user",
            "NUGET_TRUSTED_PUBLISHING_USER: ${{ vars.NUGET_TRUSTED_PUBLISHING_USER }}",
            "GitHub Actions variable NUGET_TRUSTED_PUBLISHING_USER must be set to the NuGet.org username that created the trusted publishing policy",
            "NuGet trusted publishing matches the policy creator, not the package owner",
            "NuGet/login@ebc737b6fc418a6ca0073cf116ec8dc156d8b81e # v1",
            "user: ${{ steps.nuget-user.outputs.user }}",
            "steps.nuget-login.outputs.NUGET_API_KEY");
        AssertDoesNotContainAny(
            workflow,
            "/tmp/cdidx-nuget-head",
            "user: Widthdom",
            "secrets.NUGET_API_KEY",
            "--skip-duplicate");
    }

    [Fact]
    public void ReleaseWorkflow_ValidatesReleaseTagBeforePrivilegedJobs()
    {
        var workflow = ReadReleaseWorkflow();

        AssertContainsAll(
            workflow,
            "preflight:",
            "name: Validate release tag",
            "permissions:\n      contents: read",
            "ref=refs/tags/${tag}",
            "ref: ${{ needs.preflight.outputs.ref }}",
            "needs: [preflight, release]",
            "needs: [preflight, create-release]",
            "needs: [preflight, verify-release-install]");
        AssertDoesNotContainAny(workflow, "ref: ${{ inputs.tag_name || github.ref }}");
    }

    [Fact]
    public void ReleaseWorkflow_SplitsReleasePayloadPreparationFromPrivilegedPublishing_Issue4147()
    {
        var workflow = ReadReleaseWorkflow();
        var releaseJob = ExtractWorkflowJob(workflow, "release");
        var prepareJob = ExtractWorkflowJob(workflow, "prepare-release-files");
        var createJob = ExtractWorkflowJob(workflow, "create-release");
        var verifyJob = ExtractWorkflowJob(workflow, "verify-release-install");

        AssertWorkflowJobsUseLfLineEndings(prepareJob, createJob, verifyJob);
        AssertPrepareReleaseFilesJobContract(prepareJob);
        AssertCreateReleaseJobContract(createJob);
        AssertVerifyReleaseInstallJobContract(verifyJob);
        AssertReleaseSigningJobContract(releaseJob);
    }

    [Fact]
    public void ReleaseWorkflow_UsesChangelogToolForTemplatedReleaseNotes()
    {
        var workflow = ReadReleaseWorkflow();

        AssertContainsAll(
            workflow,
            "gh release list",
            "--exclude-drafts",
            "--exclude-pre-releases",
            "select(.tagName != \\\"${TAG_NAME}\\\")",
            "No previous non-draft, non-prerelease GitHub release was found",
            "Latest GitHub release tag is not a v-prefixed SemVer version",
            "dotnet run --project tools/CodeIndex.Changelog --no-restore -- release-notes",
            "--previous-version \"${previous_version}\"",
            "--notes-file release-notes.md",
            "--notes-file release-install-notes.md");
        AssertDoesNotContainAny(workflow, "cat release-install-notes.md >> release-notes.md");
    }

    // Issue #2756: NuGet emits the core-properties OPC part with a random
    // *.psmdcp entry name, so two otherwise identical pack runs can produce
    // different .nupkg/.snupkg bytes. The release workflow normalizes that
    // implementation detail before hashing and publishing.
    // Issue #2756 対応: NuGet は core-properties の OPC part をランダムな
    // *.psmdcp entry 名で生成するため、他が同一でも .nupkg/.snupkg の bytes が
    // 揺れる。release workflow は hash / publish 前にその実装詳細を正規化する。
    [Fact]
    public void ReleaseWorkflow_NormalizesNuGetCorePropertiesBeforePublishing()
    {
        var workflow = ReadReleaseWorkflow();

        AssertContainsAll(
            workflow,
            "Normalize NuGet package metadata part names",
            "dotnet run --project tools/CodeIndex.PackageNormalize --",
            "nupkg/*.nupkg nupkg/*.snupkg",
            "core-properties/core-properties.psmdcp");
    }

    [Fact]
    public void PackageNormalizer_RewritesRandomCorePropertiesPartDeterministically()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RewritesRandomCorePropertiesPartDeterministically));
        var projectRoot = project.Root;
        var packageA = Path.Combine(projectRoot, "a.nupkg");
        var packageB = Path.Combine(projectRoot, "b.nupkg");

        CreateMinimalNuGetPackage(packageA, "a1b2c3.psmdcp");
        CreateMinimalNuGetPackage(packageB, "f9e8d7.psmdcp");

        PackageCorePropertiesNormalizer.NormalizePackage(packageA);
        PackageCorePropertiesNormalizer.NormalizePackage(packageB);

        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packageA))),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packageB))));

        using var archive = ZipFile.OpenRead(packageA);
        Assert.Contains(archive.Entries, entry => entry.FullName == PackageCorePropertiesNormalizer.CanonicalCorePropertiesPath);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("a1b2c3.psmdcp", StringComparison.Ordinal));

        var contentTypes = ReadZipEntryText(archive, "[Content_Types].xml");
        var relationships = ReadZipEntryText(archive, "_rels/.rels");
        Assert.Contains("/package/services/metadata/core-properties/core-properties.psmdcp", contentTypes);
        Assert.Contains("/package/services/metadata/core-properties/core-properties.psmdcp", relationships);
    }

    [Fact]
    public void PackageNormalizer_RemovesExistingLegacyTempNeighborAndUsesRandomTempPath_Issue3996()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RemovesExistingLegacyTempNeighborAndUsesRandomTempPath_Issue3996));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "rewrite.nupkg");
        var legacyTempPath = packagePath + ".normalize-tmp";
        CreateMinimalNuGetPackage(packagePath, "random.psmdcp");
        File.WriteAllText(legacyTempPath, "stale temp", Encoding.UTF8);

        PackageCorePropertiesNormalizer.NormalizePackage(packagePath);

        Assert.False(File.Exists(legacyTempPath));
        Assert.Empty(Directory.GetFiles(projectRoot, ".cdidx-normalize-*.tmp"));

        using var archive = ZipFile.OpenRead(packagePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == PackageCorePropertiesNormalizer.CanonicalCorePropertiesPath);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("random.psmdcp", StringComparison.Ordinal));
    }

    [Fact]
    public void PackageNormalizer_ParentDirectoryFlushFailureReportsPackageAlreadyReplaced_Issue3961()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_ParentDirectoryFlushFailureReportsPackageAlreadyReplaced_Issue3961));
        var projectRoot = project.Root;
        try
        {
            var packagePath = Path.Combine(projectRoot, "flush-failure.nupkg");
            CreateMinimalNuGetPackage(packagePath, "random.psmdcp");
            PackageNormalizeRewriteFile.FlushParentDirectoryForTesting = _ => throw new IOException("flush failed");

            var exception = Assert.Throws<PackageNormalizeReplaceDurabilityException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath));

            Assert.Contains("Package replacement completed", exception.Message);
            Assert.Contains("target package was already replaced", exception.Message);
            Assert.Contains("parent directory could not be flushed", exception.Message);
            AssertNoNormalizeTempFiles(projectRoot, packagePath);

            using var archive = ZipFile.OpenRead(packagePath);
            Assert.Contains(archive.Entries, entry => entry.FullName == PackageCorePropertiesNormalizer.CanonicalCorePropertiesPath);
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName.EndsWith("random.psmdcp", StringComparison.Ordinal));
        }
        finally
        {
            PackageNormalizeRewriteFile.FlushParentDirectoryForTesting = null;
        }
    }

    [Fact]
    public void PackageNormalizeCli_ParentDirectoryFlushFailureReportsPostReplaceStateJson_Issue3961()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizeCli_ParentDirectoryFlushFailureReportsPostReplaceStateJson_Issue3961));
        var projectRoot = project.Root;
        try
        {
            var packagePath = Path.Combine(projectRoot, "flush-failure-cli.nupkg");
            CreateMinimalNuGetPackage(packagePath, "random.psmdcp");
            PackageNormalizeRewriteFile.FlushParentDirectoryForTesting = _ => throw new IOException("flush failed at /private/path");
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();

            var exitCode = PackageNormalizeCli.Run(["--json", packagePath], stdout, stderr);

            Assert.Equal(1, exitCode);
            Assert.Empty(stderr.ToString());
            using var doc = JsonDocument.Parse(stdout.ToString());
            var package = doc.RootElement.GetProperty("packages").EnumerateArray().Single();
            var error = package.GetProperty("error").GetString();
            Assert.Contains("Package replacement completed", error);
            Assert.Contains("target package was already replaced", error);
            Assert.Contains("parent directory could not be flushed", error);
            Assert.DoesNotContain(projectRoot, error);
            Assert.DoesNotContain("/private/path", error);
            AssertNoNormalizeTempFiles(projectRoot, packagePath);

            using var archive = ZipFile.OpenRead(packagePath);
            Assert.Contains(archive.Entries, entry => entry.FullName == PackageCorePropertiesNormalizer.CanonicalCorePropertiesPath);
        }
        finally
        {
            PackageNormalizeRewriteFile.FlushParentDirectoryForTesting = null;
        }
    }

    [Fact]
    public void PackageNormalizer_CancellationAfterTempCreationDeletesTempAndLeavesPackage_Issue3961()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_CancellationAfterTempCreationDeletesTempAndLeavesPackage_Issue3961));
        var projectRoot = project.Root;
        try
        {
            var packagePath = Path.Combine(projectRoot, "cancelled.nupkg");
            CreateMinimalNuGetPackage(packagePath, "random.psmdcp");
            using var cancellation = new CancellationTokenSource();
            string? tempPath = null;
            PackageNormalizeRewriteFile.TempFileCreatedForTesting = path =>
            {
                tempPath = path;
                cancellation.Cancel();
            };

            Assert.Throws<OperationCanceledException>(() =>
                PackageCorePropertiesNormalizer.NormalizePackage(
                    packagePath,
                    PackageNormalizeLimits.Default,
                    warnings: null,
                    cancellation.Token));

            Assert.NotNull(tempPath);
            Assert.False(File.Exists(tempPath));
            AssertNoNormalizeTempFiles(projectRoot, packagePath);

            using var archive = ZipFile.OpenRead(packagePath);
            Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("random.psmdcp", StringComparison.Ordinal));
            Assert.DoesNotContain(archive.Entries, entry => entry.FullName == PackageCorePropertiesNormalizer.CanonicalCorePropertiesPath);
        }
        finally
        {
            PackageNormalizeRewriteFile.TempFileCreatedForTesting = null;
        }
    }

    [Fact]
    public void PackageNormalizeCli_DryRunDoesNotRewritePackage()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizeCli_DryRunDoesNotRewritePackage));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "dry-run.nupkg");
        CreateMinimalNuGetPackage(packagePath, "random.psmdcp");
        var beforeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));

        var (exitCode, stdout, stderr) = RunPackageNormalizeCli(["--dry-run", "--summary", packagePath]);

        var afterHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(packagePath)));
        Assert.Equal(0, exitCode);
        Assert.Empty(stderr);
        Assert.Contains($"Would normalize {packagePath}", stdout);
        Assert.Contains("Summary: inspected=1 normalized=0 unchanged=0 failed=0 skipped=1", stdout);
        Assert.Equal(beforeHash, afterHash);
        AssertNoNormalizeTempFiles(projectRoot, packagePath);

        using var archive = ZipFile.OpenRead(packagePath);
        Assert.Contains(archive.Entries, entry => entry.FullName.EndsWith("random.psmdcp", StringComparison.Ordinal));
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == PackageCorePropertiesNormalizer.CanonicalCorePropertiesPath);
    }

    [Fact]
    public void PackageNormalizeCli_CancellationReportsFailureJson_Issue3961()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizeCli_CancellationReportsFailureJson_Issue3961));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "cancelled-cli.nupkg");
        CreateMinimalNuGetPackage(packagePath, "random.psmdcp");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var exitCode = PackageNormalizeCli.Run(["--json", packagePath], stdout, stderr, cancellation.Token);

        Assert.Equal(1, exitCode);
        Assert.Empty(stderr.ToString());
        using var doc = JsonDocument.Parse(stdout.ToString());
        Assert.Equal(1, doc.RootElement.GetProperty("failed").GetInt32());
        var package = doc.RootElement.GetProperty("packages").EnumerateArray().Single();
        Assert.Equal("failed", package.GetProperty("status").GetString());
        Assert.Equal("Package normalization was cancelled.", package.GetProperty("error").GetString());
        AssertNoNormalizeTempFiles(projectRoot, packagePath);
    }

    [Fact]
    public void PackageNormalizeCli_JsonContinueOnErrorReportsAggregateSummary()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizeCli_JsonContinueOnErrorReportsAggregateSummary));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "good.nupkg");
        var missingPackagePath = Path.Combine(projectRoot, "missing.nupkg");
        CreateMinimalNuGetPackage(packagePath, "random.psmdcp");

        var (exitCode, stdout, stderr) = RunPackageNormalizeCli(["--dry-run", "--json", "--continue-on-error", missingPackagePath, packagePath]);

        Assert.Equal(1, exitCode);
        Assert.Empty(stderr);
        using var doc = JsonDocument.Parse(stdout);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("dry_run").GetBoolean());
        Assert.True(root.GetProperty("continue_on_error").GetBoolean());
        Assert.Equal(2, root.GetProperty("inspected").GetInt32());
        Assert.Equal(0, root.GetProperty("normalized").GetInt32());
        Assert.Equal(0, root.GetProperty("unchanged").GetInt32());
        Assert.Equal(1, root.GetProperty("failed").GetInt32());
        Assert.Equal(1, root.GetProperty("skipped").GetInt32());

        var packages = root.GetProperty("packages").EnumerateArray().ToArray();
        Assert.Equal("failed", packages[0].GetProperty("status").GetString());
        Assert.Equal(missingPackagePath, packages[0].GetProperty("path").GetString());
        Assert.Equal("would_normalize", packages[1].GetProperty("status").GetString());
        Assert.Equal(packagePath, packages[1].GetProperty("path").GetString());
    }

    [Fact]
    public void PackageNormalizeCli_RejectsTooManyPackageArguments()
    {
        var args = Enumerable
            .Range(0, PackageNormalizeOptions.MaxPackageArgumentCount + 1)
            .Select(index => $"package-{index}.nupkg")
            .ToArray();

        var (exitCode, stdout, stderr) = RunPackageNormalizeCli(args);

        Assert.Equal(1, exitCode);
        Assert.Empty(stdout);
        Assert.Contains($"at most {PackageNormalizeOptions.MaxPackageArgumentCount} package paths", stderr);
    }

    [Fact]
    public void PackageNormalizeCli_JsonReportsBoundedFriendlyFailure()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizeCli_JsonReportsBoundedFriendlyFailure));
        var projectRoot = project.Root;
        var missingPackagePath = Path.Combine(projectRoot, "missing.nupkg");

        var (exitCode, stdout, stderr) = RunPackageNormalizeCli(["--json", missingPackagePath]);

        Assert.Equal(1, exitCode);
        Assert.Empty(stderr);
        using var doc = JsonDocument.Parse(stdout);
        var package = doc.RootElement.GetProperty("packages").EnumerateArray().Single();
        var error = package.GetProperty("error").GetString();
        Assert.Contains("missing.nupkg", error);
        Assert.DoesNotContain(projectRoot, error);
        Assert.True(error!.Length <= 512);
        Assert.Empty(package.GetProperty("warnings").EnumerateArray());
    }

    [Fact]
    public void PackageNormalizeCli_JsonBoundsZipEntryDiagnostics()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizeCli_JsonBoundsZipEntryDiagnostics));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "unsafe-entry.nupkg");
        var longEntryName = new string('a', 260) + "\\payload.txt";
        CreatePackageWithEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", ""),
            (longEntryName, "payload"));

        var (exitCode, stdout, stderr) = RunPackageNormalizeCli(["--json", packagePath]);

        Assert.Equal(1, exitCode);
        Assert.Empty(stderr);
        using var doc = JsonDocument.Parse(stdout);
        var error = doc.RootElement.GetProperty("packages").EnumerateArray().Single().GetProperty("error").GetString();
        Assert.Contains("aaa", error);
        Assert.Contains("...", error);
        Assert.DoesNotContain(longEntryName, error);
        Assert.True(error!.Length <= 512);
    }

    [Fact]
    public void PackageNormalizer_ReportsCleanupWarningsWhenTempDeleteFails()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_ReportsCleanupWarningsWhenTempDeleteFails));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "cleanup-warning.nupkg");
        CreateMinimalNuGetPackage(packagePath, "random.psmdcp");
        var limits = PackageNormalizeLimits.Default with { MaxXmlTextChars = 5 };
        var warnings = new List<string>();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PackageCorePropertiesNormalizer.NormalizePackage(
                packagePath,
                limits,
                warnings,
                _ => throw new IOException("delete failed at /private/path")));

        Assert.Contains("[Content_Types].xml", exception.Message);
        var warning = Assert.Single(warnings);
        Assert.Contains("Could not delete temporary normalized package", warning);
        Assert.Contains(".cdidx-normalize-cleanup-warning.", warning);
        Assert.DoesNotContain(projectRoot, warning);
        Assert.DoesNotContain("/private/path", warning);
    }

    [Fact]
    public void PackageNormalizer_RemovesPreexistingLegacyTempFileBeforeRewrite()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RemovesPreexistingLegacyTempFileBeforeRewrite));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "legacy-temp.nupkg");
        var legacyTempPath = packagePath + ".normalize-tmp";
        CreateMinimalNuGetPackage(packagePath, "random.psmdcp");
        File.WriteAllText(legacyTempPath, "stale temp");

        PackageCorePropertiesNormalizer.NormalizePackage(packagePath);

        Assert.False(File.Exists(legacyTempPath));
        Assert.Empty(Directory.EnumerateFiles(projectRoot, "*.normalize-tmp.*"));

        using var archive = ZipFile.OpenRead(packagePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == PackageCorePropertiesNormalizer.CanonicalCorePropertiesPath);
    }

    [Fact]
    public void PackageNormalizer_RejectsLockedLegacyTempFileBeforeRewrite()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RejectsLockedLegacyTempFileBeforeRewrite));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "locked-legacy.nupkg");
        var legacyTempPath = packagePath + ".normalize-tmp";
        CreateMinimalNuGetPackage(packagePath, "random.psmdcp");
        File.WriteAllText(legacyTempPath, "active legacy temp");

        using var lockedLegacyTemp = new FileStream(
            legacyTempPath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.ReadWrite,
                Share = FileShare.None,
            });

        var exception = Assert.Throws<InvalidOperationException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath));

        Assert.Contains("could not be locked and removed", exception.Message);
        Assert.Contains("locked-legacy.nupkg.normalize-tmp", exception.Message);
        Assert.DoesNotContain(projectRoot, exception.Message);
        Assert.True(File.Exists(legacyTempPath));
        Assert.Empty(Directory
            .EnumerateFiles(projectRoot)
            .Where(path => Path.GetFileName(path).Contains(".normalize-tmp.", StringComparison.Ordinal)));

        using var archive = ZipFile.OpenRead(packagePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == "package/services/metadata/core-properties/random.psmdcp");
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == PackageCorePropertiesNormalizer.CanonicalCorePropertiesPath);
    }

    [Fact]
    public void PackageNormalizer_RemovesReadOnlyLegacyTempFileOnUnixBeforeRewrite()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RemovesReadOnlyLegacyTempFileOnUnixBeforeRewrite));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "readonly-legacy.nupkg");
        var legacyTempPath = packagePath + ".normalize-tmp";
        CreateMinimalNuGetPackage(packagePath, "random.psmdcp");
        File.WriteAllText(legacyTempPath, "read-only stale temp");
        File.SetUnixFileMode(legacyTempPath, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        PackageCorePropertiesNormalizer.NormalizePackage(packagePath);

        Assert.False(File.Exists(legacyTempPath));
        Assert.Empty(Directory.EnumerateFiles(projectRoot, "*.normalize-tmp.*"));

        using var archive = ZipFile.OpenRead(packagePath);
        Assert.Contains(archive.Entries, entry => entry.FullName == PackageCorePropertiesNormalizer.CanonicalCorePropertiesPath);
    }

    [Fact]
    public void PackageNormalizer_RejectsPackageThatExceedsEntryCountLimit()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RejectsPackageThatExceedsEntryCountLimit));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "too-many-entries.nupkg");
        CreatePackageWithEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", ""),
            ("payload.txt", "ok"));

        var limits = PackageNormalizeLimits.Default with { MaxEntryCount = 1 };

        var exception = Assert.Throws<InvalidOperationException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath, limits));
        Assert.Contains("2 ZIP entries", exception.Message);
        Assert.Contains("limit of 1", exception.Message);
        AssertNoNormalizeTempFiles(projectRoot, packagePath);
    }

    [Fact]
    public void PackageNormalizer_RejectsEntryThatExceedsPerEntryLimit()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RejectsEntryThatExceedsPerEntryLimit));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "large-entry.nupkg");
        CreatePackageWithEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", ""),
            ("payload.bin", "123456"));

        var limits = PackageNormalizeLimits.Default with
        {
            MaxEntryUncompressedBytes = 5,
            MaxTotalUncompressedBytes = 100,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath, limits));
        Assert.Contains("payload.bin", exception.Message);
        Assert.Contains("per-entry limit of 5 bytes", exception.Message);
        AssertNoNormalizeTempFiles(projectRoot, packagePath);
    }

    [Fact]
    public void PackageNormalizer_RejectsPackageThatExceedsTotalUncompressedLimit()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RejectsPackageThatExceedsTotalUncompressedLimit));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "large-total.nupkg");
        CreatePackageWithEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", ""),
            ("a.txt", "1234"),
            ("b.txt", "5678"));

        var limits = PackageNormalizeLimits.Default with
        {
            MaxEntryUncompressedBytes = 10,
            MaxTotalUncompressedBytes = 6,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath, limits));
        Assert.Contains("b.txt", exception.Message);
        Assert.Contains("uncompressed size exceed the limit of 6 bytes", exception.Message);
        AssertNoNormalizeTempFiles(projectRoot, packagePath);
    }

    [Fact]
    public void PackageNormalizer_RejectsXmlEntryThatExceedsTextLimit()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RejectsXmlEntryThatExceedsTextLimit));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "large-xml.nupkg");
        CreatePackageWithEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", ""),
            ("[Content_Types].xml", "123456"));

        var limits = PackageNormalizeLimits.Default with
        {
            MaxEntryUncompressedBytes = 100,
            MaxTotalUncompressedBytes = 100,
            MaxXmlTextChars = 5,
        };

        var exception = Assert.Throws<InvalidOperationException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath, limits));
        Assert.Contains("[Content_Types].xml", exception.Message);
        Assert.Contains("text limit of 5 characters", exception.Message);
        AssertNoNormalizeTempFiles(projectRoot, packagePath);
    }

    [Theory]
    [InlineData("/payload.txt", "must be a relative path")]
    [InlineData("C:/payload.txt", "must be a relative path")]
    [InlineData("./C:/payload.txt", "must be a relative path")]
    [InlineData("../payload.txt", "must not contain parent-directory segments")]
    [InlineData("folder\\payload.txt", "must use '/' separators")]
    [InlineData("folder//payload.txt", "must not contain empty path segments")]
    public void PackageNormalizer_RejectsUnsafeZipEntryNames(string unsafeEntryName, string expectedMessage)
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RejectsUnsafeZipEntryNames));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "unsafe-name.nupkg");
        CreatePackageWithEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", ""),
            (unsafeEntryName, "payload"));

        var exception = Assert.Throws<InvalidOperationException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath));
        Assert.Contains(unsafeEntryName, exception.Message);
        Assert.Contains(expectedMessage, exception.Message);
        AssertNoNormalizeTempFiles(projectRoot, packagePath);
    }

    [Fact]
    public void PackageNormalizer_RejectsDestinationNamesThatNormalizeToDuplicates()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RejectsDestinationNamesThatNormalizeToDuplicates));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "duplicate-normalized-name.nupkg");
        CreatePackageWithEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", ""),
            ("docs/readme.txt", "one"),
            ("docs/./readme.txt", "two"));

        var exception = Assert.Throws<InvalidOperationException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath));
        Assert.Contains("docs/./readme.txt", exception.Message);
        Assert.Contains("duplicate destination name 'docs/readme.txt'", exception.Message);
        AssertNoNormalizeTempFiles(projectRoot, packagePath);
    }

    [Fact]
    public void PackageNormalizer_RejectsDuplicateZipEntryNames()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RejectsDuplicateZipEntryNames));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "duplicate-entry-name.nupkg");
        CreatePackageWithEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", ""),
            ("payload.txt", "one"),
            ("payload.txt", "two"));

        var exception = Assert.Throws<InvalidOperationException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath));
        Assert.Contains("payload.txt", exception.Message);
        Assert.Contains("duplicate destination name 'payload.txt'", exception.Message);
        AssertNoNormalizeTempFiles(projectRoot, packagePath);
    }

    [Fact]
    public void PackageNormalizer_ScrubsSafeExternalAttributes()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_ScrubsSafeExternalAttributes));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "external-attributes.nupkg");
        CreatePackageWithAttributedEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", "", UnixRegularFileAttributes(493)),
            ("payload.bin", "payload", 0x20));

        PackageCorePropertiesNormalizer.NormalizePackage(packagePath);

        using var archive = ZipFile.OpenRead(packagePath);
        Assert.All(archive.Entries, entry => Assert.Equal(0, entry.ExternalAttributes));
    }

    [Fact]
    public void PackageNormalizer_RejectsPosixSymlinkExternalAttributes()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RejectsPosixSymlinkExternalAttributes));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "symlink-attributes.nupkg");
        CreatePackageWithAttributedEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", "", 0),
            ("payload.bin", "payload", UnixSymlinkAttributes()));

        var exception = Assert.Throws<InvalidOperationException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath));
        Assert.Contains("payload.bin", exception.Message);
        Assert.Contains("unsafe POSIX file type symlink", exception.Message);
        AssertNoNormalizeTempFiles(projectRoot, packagePath);
    }

    [Fact]
    public void PackageNormalizer_RejectsUnsafeDosExternalAttributes()
    {
        using var project = TestProjectHelper.CreateTempProjectScope(nameof(PackageNormalizer_RejectsUnsafeDosExternalAttributes));
        var projectRoot = project.Root;
        var packagePath = Path.Combine(projectRoot, "dos-attributes.nupkg");
        CreatePackageWithAttributedEntries(
            packagePath,
            ("package/services/metadata/core-properties/random.psmdcp", "", 0),
            ("payload.bin", "payload", 0x04));

        var exception = Assert.Throws<InvalidOperationException>(() => PackageCorePropertiesNormalizer.NormalizePackage(packagePath));
        Assert.Contains("payload.bin", exception.Message);
        Assert.Contains("unsafe DOS attributes 0x04", exception.Message);
        AssertNoNormalizeTempFiles(projectRoot, packagePath);
    }

    [Fact]
    public void ReleaseWorkflow_PublishesOfficialContainerImage()
    {
        var workflow = ReadReleaseWorkflow();
        var dockerfile = RepositoryTestPaths.ReadDockerfile();
        var dockerignore = RepositoryTestPaths.ReadDockerIgnore();
        var entrypoint = RepositoryTestPaths.ReadText("scripts", "docker-entrypoint.sh");
        var project = RepositoryTestPaths.ReadText("src", "CodeIndex", "CodeIndex.csproj");

        AssertOfficialContainerWorkflowContract(workflow);
        AssertOfficialContainerBaseImageContract(dockerfile);
        AssertOfficialContainerRuntimeUserContract(dockerfile, entrypoint);
        AssertOfficialContainerBuildContextContract(dockerfile, dockerignore);
        AssertOfficialContainerProjectMetadataContract(project);
    }

    [Fact]
    public void ReleaseWorkflow_SecretAndTokenScopesStayOfficialAndMinimal_Issue4331()
    {
        var workflow = RepositoryTestPaths.ReadNormalizedReleaseWorkflow();

        AssertContainsAll(
            workflow,
            "permissions:\n  contents: read",
            "publish-container:\n    if: github.repository == 'Widthdom/CodeIndex'",
            "publish-nuget:\n    if: github.repository == 'Widthdom/CodeIndex'",
            "permissions:\n      contents: read\n      id-token: write\n      attestations: write",
            "NUGET_TRUSTED_PUBLISHING_USER: ${{ vars.NUGET_TRUSTED_PUBLISHING_USER }}",
            "--api-key \"${{ steps.nuget-login.outputs.NUGET_API_KEY }}\"");
        Assert.Equal(1, CountOccurrences(workflow, "packages: write"));
        AssertDoesNotContainAny(workflow, "secrets.NUGET_API_KEY");

        var secretLines = FindSecretLinesInUngatedReleaseJobs(workflow);
        Assert.Empty(secretLines);
    }

    [Fact]
    public void Dependabot_DoesNotBumpIlLinkPastReleaseSdkMajor()
    {
        var dependabot = RepositoryTestPaths.ReadText(".github", "dependabot.yml");

        AssertContainsAll(
            dependabot,
            "dependency-name: Microsoft.NET.ILLink.Tasks",
            "version-update:semver-major");
    }

    [Fact]
    public void MutationWorkflow_PinsActionsByCommitSha()
    {
        var workflow = RepositoryTestPaths.ReadWorkflow("mutation-testing.yml");

        AssertContainsAll(
            workflow,
            "actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2",
            "actions/setup-dotnet@9a946fdbd5fb07b82b2f5a4466058b876ab72bb2 # v5.3.0",
            "actions/cache@27d5ce7f107fe9357f9df03efb73ab90386fccae # v5.0.5");
        AssertDoesNotContainAny(
            workflow,
            "actions/checkout@v6",
            "actions/setup-dotnet@v5",
            "actions/cache@v5");
    }

    private static void AssertContainsAll(string text, params string[] expectedValues)
    {
        foreach (var expected in expectedValues)
            Assert.Contains(expected, text);
    }

    private static void AssertDoesNotContainAny(string text, params string[] excludedValues)
    {
        foreach (var excluded in excludedValues)
            Assert.DoesNotContain(excluded, text);
    }

    private static void AssertWorkflowJobsUseLfLineEndings(params string[] jobs)
    {
        foreach (var job in jobs)
            Assert.DoesNotContain("\r\n", job);
    }

    private static void AssertPrepareReleaseFilesJobContract(string prepareJob)
    {
        AssertContainsAll(
            prepareJob,
            "needs: [preflight, release]",
            "permissions:\n      contents: read",
            "name: Collect release files",
            "name: Write release install notes",
            "name: Write curated release notes",
            "name: Upload prepared release payload",
            "name: release-payload",
            "retention-days: 1");
        AssertDoesNotContainAny(
            prepareJob,
            "RELEASE_GPG_PRIVATE_KEY",
            "actions/attest-build-provenance",
            "contents: write",
            "environment: release-production");
    }

    private static void AssertCreateReleaseJobContract(string createJob)
    {
        AssertContainsAll(
            createJob,
            "needs: [preflight, prepare-release-files]",
            "environment: release-production",
            "permissions:\n      contents: write\n      id-token: write\n      attestations: write",
            "name: Download prepared release payload",
            "pattern: release-payload",
            "path: .",
            "merge-multiple: true",
            "name: Import release GPG key",
            "name: Sign release checksum manifest",
            "GNUPGHOME: ${{ runner.temp }}/release-gnupg",
            "name: Remove release GPG material",
            "rm -rf \"$GNUPGHOME\"",
            "name: Attest release artifacts",
            "name: Create GitHub release",
            "GH_REPO: ${{ github.repository }}");
        AssertDoesNotContainAny(createJob, "name: Checkout", "bash install.sh");
    }

    private static void AssertVerifyReleaseInstallJobContract(string verifyJob)
    {
        AssertContainsAll(
            verifyJob,
            "needs: [preflight, create-release]",
            "permissions:\n      contents: read",
            "name: Verify install.sh against the published release",
            "releases/download/${TAG_NAME}/install.sh",
            "curl -fsSL",
            "bash install.sh \"${TAG_NAME}\"");
        AssertDoesNotContainAny(verifyJob, "secrets.", "environment:");
    }

    private static void AssertReleaseSigningJobContract(string releaseJob)
    {
        AssertContainsAll(
            releaseJob,
            "name: Sign Windows executable if configured",
            "WIN_SIGNING_CERT_BASE64: ${{ secrets.WIN_SIGNING_CERT_BASE64 }}");
        AssertDoesNotContainAny(
            releaseJob,
            "name: Warn when Windows Authenticode signing is not configured",
            "\n    env:\n      WIN_SIGNING_CERT_BASE64: ${{ secrets.WIN_SIGNING_CERT_BASE64 }}\n      WIN_SIGNING_CERT_PASSWORD: ${{ secrets.WIN_SIGNING_CERT_PASSWORD }}");
    }

    private static void AssertOfficialContainerWorkflowContract(string workflow)
    {
        AssertContainsAll(
            workflow,
            "publish-container:",
            "needs: [preflight, verify-release-install]",
            "packages: write",
            "docker/setup-buildx-action@d7f5e7f509e45cec5c76c4d5afdd7de93d0b3df5 # v4",
            "docker/login-action@c94ce9fb468520275223c153574b00df6fe4bcc9 # v3",
            "docker/build-push-action@10e90e3645eae34f1e60eeb005ba3a3d33f178e8 # v6",
            "platforms: linux/amd64,linux/arm64",
            "ghcr.io/widthdom/codeindex:${version}",
            "ghcr.io/widthdom/codeindex:latest",
            "tags: ${{ steps.image-tags.outputs.tags }}",
            "Extract container build metadata",
            "git rev-parse --short=7 HEAD",
            "git show -s --format=%cd --date=format:%Y-%m-%d HEAD",
            "CDIDX_BUILD_COMMIT=${{ steps.container-metadata.outputs.commit }}",
            "CDIDX_BUILD_DATE=${{ steps.container-metadata.outputs.date }}",
            "CDIDX_BUILD_DIRTY=${{ steps.container-metadata.outputs.dirty }}",
            "*-*) ;;");
    }

    private static void AssertOfficialContainerBaseImageContract(string dockerfile)
    {
        AssertContainsAll(
            dockerfile,
            "docker buildx imagetools inspect mcr.microsoft.com/dotnet/<image>:9.0.301-alpine3.22",
            "FROM mcr.microsoft.com/dotnet/sdk:9.0.301-alpine3.22@sha256:bdd1c9e2215a71e43d2f0c6978ace0a0652d7ecc21bf6f659d42d840500e1c44 AS build",
            "FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine@sha256:7ec14bf41e70f3ca60f7b369b077636f642a0e6867caf28677d970e0abd9c6e6 AS runtime");
        AssertDoesNotContainAny(
            dockerfile,
            "FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build",
            "FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine AS runtime");
    }

    private static void AssertOfficialContainerRuntimeUserContract(string dockerfile, string entrypoint)
    {
        AssertContainsAll(
            dockerfile,
            "COPY scripts/docker-entrypoint.sh /usr/local/bin/cdidx-entrypoint",
            "apk add --no-cache ca-certificates su-exec",
            "addgroup -S -g 10001 cdidx",
            "adduser -S -D -H -u 10001 -G cdidx -h /repo cdidx",
            "chown cdidx:cdidx /repo");
        AssertDoesNotContainAny(dockerfile, "USER cdidx:cdidx");
        AssertContainsAll(
            entrypoint,
            "stat -c '%u' /repo",
            "stat -c '%g' /repo",
            "su-exec \"${target_uid}:${target_gid}\" cdidx \"$@\"");
    }

    private static void AssertOfficialContainerBuildContextContract(string dockerfile, string dockerignore)
    {
        AssertDoesNotContainAny(dockerfile, "COPY . .");
        AssertContainsAll(
            dockerfile,
            "COPY Directory.Build.props nuget.config version.json ./",
            "COPY src/CodeIndex/CodeIndex.csproj src/CodeIndex/packages.lock.json src/CodeIndex/",
            "COPY src/CodeIndex/ src/CodeIndex/",
            "ARG CDIDX_BUILD_COMMIT=unknown",
            "-p:CdidxBuildCommitOverride=\"$CDIDX_BUILD_COMMIT\"",
            "-p:CdidxBuildDateOverride=\"$build_date\"",
            "-p:CdidxBuildDirtyOverride=\"$CDIDX_BUILD_DIRTY\"",
            "ARG TARGETARCH=amd64",
            "linux-musl-x64",
            "linux-musl-arm64",
            "dotnet restore src/CodeIndex/CodeIndex.csproj",
            "--locked-mode",
            "--no-restore",
            "ENTRYPOINT [\"/usr/local/bin/cdidx-entrypoint\"]");
        AssertContainsAll(
            dockerignore,
            ".git/",
            "tests/",
            "tools/",
            "docs/",
            "changelog.d/",
            "*.md",
            "!COMMERCIAL_LICENSE.md");
    }

    private static void AssertOfficialContainerProjectMetadataContract(string project)
    {
        AssertContainsAll(
            project,
            "CdidxBuildCommitOverride",
            "CdidxBuildDateOverride",
            "CdidxBuildDirtyOverride",
            "Microsoft.NET.ILLink.Tasks\" Version=\"8.");
        AssertDoesNotContainAny(project, "Microsoft.NET.ILLink.Tasks\" Version=\"10.");
    }

    private static string ReadReleaseWorkflow() => RepositoryTestPaths.ReadReleaseWorkflow();

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static IReadOnlyList<string> FindSecretLinesInUngatedReleaseJobs(string workflow)
    {
        var failures = new List<string>();
        var inJobs = false;
        string? jobName = null;
        var jobHasOfficialRepositoryGate = false;
        var lines = workflow.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line == "jobs:")
            {
                inJobs = true;
                continue;
            }

            if (!inJobs)
                continue;

            if (line.StartsWith("  ", StringComparison.Ordinal)
                && !line.StartsWith("    ", StringComparison.Ordinal)
                && line.EndsWith(':'))
            {
                jobName = line.Trim().TrimEnd(':');
                jobHasOfficialRepositoryGate = false;
                continue;
            }

            if (jobName == null)
                continue;

            if (line.Trim() == "if: github.repository == 'Widthdom/CodeIndex'")
                jobHasOfficialRepositoryGate = true;

            if (line.Contains("secrets.", StringComparison.Ordinal) && !jobHasOfficialRepositoryGate)
                failures.Add($"{i + 1}:{jobName}:{line.Trim()}");
        }

        return failures;
    }

    private static string ExtractWorkflowJob(string workflow, string jobName)
    {
        var marker = $"  {jobName}:";
        using var reader = new StringReader(workflow);
        var job = new StringBuilder();
        var inJob = false;

        while (reader.ReadLine() is { } line)
        {
            if (!inJob)
            {
                if (line == marker)
                {
                    inJob = true;
                    job.Append(line).Append('\n');
                }

                continue;
            }

            if (line.StartsWith("  ", StringComparison.Ordinal)
                && !line.StartsWith("    ", StringComparison.Ordinal)
                && line.EndsWith(':'))
            {
                break;
            }

            job.Append(line).Append('\n');
        }

        var text = job.ToString();
        Assert.False(string.IsNullOrEmpty(text), $"Could not find workflow job '{jobName}'.");
        return text;
    }
}
