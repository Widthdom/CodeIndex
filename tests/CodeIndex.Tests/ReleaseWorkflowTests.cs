using CodeIndex.PackageNormalize;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public partial class ReleaseWorkflowTests
{
    [Fact]
    public void ReleaseWorkflow_PublishesTrimmedSelfContainedBinariesAndVerifiesCliJson()
    {
        var workflow = ReadReleaseWorkflow();

        Assert.Contains("-p:PublishTrimmed=true", workflow);
        Assert.DoesNotContain("-p:PublishTrimmed=false", workflow);
        Assert.Contains("status --json", workflow);
        Assert.Contains("Expected status --json to exit 0", workflow);
        Assert.Contains("status --json stdout did not include files", workflow);
        Assert.Contains("status --json stdout did not include version", workflow);
        Assert.DoesNotContain("Expected status --json to fail on the trimmed self-contained release", workflow);
        Assert.DoesNotContain("Expected status --json to exit 4", workflow);
        Assert.DoesNotContain("Error [E009_FEATURE_UNAVAILABLE]: --json is not available on this trimmed build.", workflow);
        Assert.DoesNotContain("Hint: use `cdidx mcp` for structured output", workflow);
    }

    [Fact]
    public void ReleaseWorkflow_VerifiesPublishedInstallForTheCurrentRid()
    {
        var workflow = ReadReleaseWorkflow();

        Assert.Contains("expected_rids=\"linux-x64 linux-arm64 osx-arm64 win-x64 win-arm64\"", workflow);
        Assert.Contains("asset=\"CodeIndex-${rid}.zip\"", workflow);
        Assert.Contains("asset=\"CodeIndex-${rid}.tar.gz\"", workflow);
        Assert.Contains("Missing release archive for ${rid}", workflow);
        Assert.Contains("CodeIndex-osx-x64.*", workflow);
        Assert.Contains("native_asset=\"libe_sqlite3.so\"", workflow);
        Assert.Contains("for asset in \"$binary_name\" \"$native_asset\"", workflow);
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

        Assert.Contains("Download release artifacts for checksum calculation", workflow);
        Assert.Contains("actions/download-artifact@3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c # v8.0.1", workflow);
        Assert.Contains("sha_for_artifact()", workflow);
        Assert.Contains("find \"$artifact_root\" -type f -name \"$asset\" -print -quit", workflow);
        Assert.DoesNotContain("CHECKSUMS_URL", workflow);
        Assert.DoesNotContain("https://x-access-token:${TAP_TOKEN}@github.com/Widthdom/homebrew-tap.git", workflow);
        Assert.Contains("credential_helper='!f() { echo username=x-access-token; echo \"password=${TAP_TOKEN}\"; }; f'", workflow);
        Assert.Contains("trap cleanup EXIT", workflow);
        Assert.Contains("native_sqlite_asset = OS.mac? ? \"libe_sqlite3.dylib\" : \"libe_sqlite3.so\"", workflow);
        Assert.Contains("bin.install native_sqlite_asset", workflow);
        Assert.Contains("assert_predicate bin/native_sqlite_asset, :exist?", workflow);
        Assert.Contains("(testpath/\"Sample.cs\").write", workflow);
        Assert.Contains("system \"#{bin}/cdidx\", testpath.to_s", workflow);
        Assert.Contains("shell_output(\"#{bin}/cdidx status --json\")", workflow);
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
        Assert.Contains("dotnet tool install --global CycloneDX --version 6.2.0", workflow);
        Assert.Contains("dotnet-CycloneDX src/CodeIndex/CodeIndex.csproj", workflow);
        Assert.Contains("--output-format Json", workflow);
        Assert.Contains("--exclude-test-projects", workflow);
        Assert.Contains("cdidx.sbom.cdx.json", workflow);
        Assert.Contains("CodeIndex-sbom", workflow);
        Assert.Contains("matrix.rid == 'linux-x64'", workflow);
        Assert.Contains("'*.cdx.json'", workflow);
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

        Assert.Contains("Release tag must be a v-prefixed SemVer version", workflow);
        Assert.Contains("jq -r '.version // empty' version.json", workflow);
        Assert.Contains("does not match release tag", workflow);
        Assert.Contains("https://api.nuget.org/v3-flatcontainer/cdidx/${VERSION}/cdidx.${VERSION}.nupkg", workflow);
        Assert.Contains("response_headers=\"$(mktemp \"${RUNNER_TEMP:-/tmp}/cdidx-nuget-head.XXXXXX\")\"", workflow);
        Assert.Contains("cat \"$response_headers\"", workflow);
        Assert.DoesNotContain("/tmp/cdidx-nuget-head", workflow);
        Assert.Contains("NuGet package cdidx ${VERSION} is already published", workflow);
        Assert.Contains("Expected packed package ${expected_package} was not produced", workflow);
        Assert.Contains("Attest NuGet package artifacts", workflow);
        Assert.Contains("nupkg/*.nupkg", workflow);
        Assert.Contains("nupkg/*.snupkg", workflow);
        Assert.Contains("Resolve NuGet trusted publishing user", workflow);
        Assert.Contains("NUGET_TRUSTED_PUBLISHING_USER: ${{ vars.NUGET_TRUSTED_PUBLISHING_USER }}", workflow);
        Assert.Contains("GitHub Actions variable NUGET_TRUSTED_PUBLISHING_USER must be set to the NuGet.org username that created the trusted publishing policy", workflow);
        Assert.Contains("NuGet trusted publishing matches the policy creator, not the package owner", workflow);
        Assert.Contains("NuGet/login@ebc737b6fc418a6ca0073cf116ec8dc156d8b81e # v1", workflow);
        Assert.Contains("user: ${{ steps.nuget-user.outputs.user }}", workflow);
        Assert.Contains("steps.nuget-login.outputs.NUGET_API_KEY", workflow);
        Assert.DoesNotContain("user: Widthdom", workflow);
        Assert.DoesNotContain("secrets.NUGET_API_KEY", workflow);
        Assert.DoesNotContain("--skip-duplicate", workflow);
    }

    [Fact]
    public void ReleaseWorkflow_ValidatesReleaseTagBeforePrivilegedJobs()
    {
        var workflow = ReadReleaseWorkflow();

        Assert.Contains("preflight:", workflow);
        Assert.Contains("name: Validate release tag", workflow);
        Assert.Contains("permissions:\n      contents: read", workflow);
        Assert.Contains("ref=refs/tags/${tag}", workflow);
        Assert.Contains("ref: ${{ needs.preflight.outputs.ref }}", workflow);
        Assert.Contains("needs: [preflight, release]", workflow);
        Assert.Contains("needs: [preflight, create-release]", workflow);
        Assert.Contains("needs: [preflight, verify-release-install]", workflow);
        Assert.DoesNotContain("ref: ${{ inputs.tag_name || github.ref }}", workflow);
    }

    [Fact]
    public void ReleaseWorkflow_SplitsReleasePayloadPreparationFromPrivilegedPublishing_Issue4147()
    {
        var workflow = ReadReleaseWorkflow();
        var releaseJob = ExtractWorkflowJob(workflow, "release");
        var prepareJob = ExtractWorkflowJob(workflow, "prepare-release-files");
        var createJob = ExtractWorkflowJob(workflow, "create-release");
        var verifyJob = ExtractWorkflowJob(workflow, "verify-release-install");

        Assert.Contains("needs: [preflight, release]", prepareJob);
        Assert.Contains("permissions:\n      contents: read", prepareJob);
        Assert.Contains("name: Collect release files", prepareJob);
        Assert.Contains("name: Write release install notes", prepareJob);
        Assert.Contains("name: Write curated release notes", prepareJob);
        Assert.Contains("name: Upload prepared release payload", prepareJob);
        Assert.Contains("name: release-payload", prepareJob);
        Assert.Contains("retention-days: 1", prepareJob);
        Assert.DoesNotContain("RELEASE_GPG_PRIVATE_KEY", prepareJob);
        Assert.DoesNotContain("actions/attest-build-provenance", prepareJob);
        Assert.DoesNotContain("contents: write", prepareJob);
        Assert.DoesNotContain("environment: release-production", prepareJob);

        Assert.Contains("needs: [preflight, prepare-release-files]", createJob);
        Assert.Contains("environment: release-production", createJob);
        Assert.Contains("permissions:\n      contents: write\n      id-token: write\n      attestations: write", createJob);
        Assert.Contains("name: Download prepared release payload", createJob);
        Assert.Contains("name: release-payload", createJob);
        Assert.Contains("name: Import release GPG key", createJob);
        Assert.Contains("name: Sign release checksum manifest", createJob);
        Assert.Contains("GNUPGHOME: ${{ runner.temp }}/release-gnupg", createJob);
        Assert.Contains("name: Remove release GPG material", createJob);
        Assert.Contains("rm -rf \"$GNUPGHOME\"", createJob);
        Assert.Contains("name: Attest release artifacts", createJob);
        Assert.Contains("name: Create GitHub release", createJob);
        Assert.Contains("GH_REPO: ${{ github.repository }}", createJob);
        Assert.DoesNotContain("name: Checkout", createJob);
        Assert.DoesNotContain("bash install.sh", createJob);

        Assert.Contains("needs: [preflight, create-release]", verifyJob);
        Assert.Contains("permissions:\n      contents: read", verifyJob);
        Assert.Contains("name: Verify install.sh against the published release", verifyJob);
        Assert.Contains("releases/download/${TAG_NAME}/install.sh", verifyJob);
        Assert.Contains("curl -fsSL", verifyJob);
        Assert.Contains("bash install.sh \"${TAG_NAME}\"", verifyJob);
        Assert.DoesNotContain("secrets.", verifyJob);
        Assert.DoesNotContain("environment:", verifyJob);

        Assert.Contains("name: Sign Windows executable if configured", releaseJob);
        Assert.Contains("WIN_SIGNING_CERT_BASE64: ${{ secrets.WIN_SIGNING_CERT_BASE64 }}", releaseJob);
        Assert.DoesNotContain("name: Warn when Windows Authenticode signing is not configured", releaseJob);
        Assert.DoesNotContain(
            "\n    env:\n      WIN_SIGNING_CERT_BASE64: ${{ secrets.WIN_SIGNING_CERT_BASE64 }}\n      WIN_SIGNING_CERT_PASSWORD: ${{ secrets.WIN_SIGNING_CERT_PASSWORD }}",
            releaseJob);
    }

    [Fact]
    public void ReleaseWorkflow_UsesChangelogToolForTemplatedReleaseNotes()
    {
        var workflow = ReadReleaseWorkflow();

        Assert.Contains("gh release list", workflow);
        Assert.Contains("--exclude-drafts", workflow);
        Assert.Contains("--exclude-pre-releases", workflow);
        Assert.Contains("select(.tagName != \\\"${TAG_NAME}\\\")", workflow);
        Assert.Contains("No previous non-draft, non-prerelease GitHub release was found", workflow);
        Assert.Contains("Latest GitHub release tag is not a v-prefixed SemVer version", workflow);
        Assert.Contains("dotnet run --project tools/CodeIndex.Changelog -- release-notes", workflow);
        Assert.Contains("--previous-version \"${previous_version}\"", workflow);
        Assert.Contains("--notes-file release-notes.md", workflow);
        Assert.Contains("--notes-file release-install-notes.md", workflow);
        Assert.DoesNotContain("cat release-install-notes.md >> release-notes.md", workflow);
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

        Assert.Contains("Normalize NuGet package metadata part names", workflow);
        Assert.Contains("dotnet run --project tools/CodeIndex.PackageNormalize --", workflow);
        Assert.Contains("nupkg/*.nupkg nupkg/*.snupkg", workflow);
        Assert.Contains("core-properties/core-properties.psmdcp", workflow);
    }

    [Fact]
    public void PackageNormalizer_RewritesRandomCorePropertiesPartDeterministically()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RewritesRandomCorePropertiesPartDeterministically));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RemovesExistingLegacyTempNeighborAndUsesRandomTempPath_Issue3996()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RemovesExistingLegacyTempNeighborAndUsesRandomTempPath_Issue3996));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_ParentDirectoryFlushFailureReportsPackageAlreadyReplaced_Issue3961()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_ParentDirectoryFlushFailureReportsPackageAlreadyReplaced_Issue3961));
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
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizeCli_ParentDirectoryFlushFailureReportsPostReplaceStateJson_Issue3961()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizeCli_ParentDirectoryFlushFailureReportsPostReplaceStateJson_Issue3961));
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
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_CancellationAfterTempCreationDeletesTempAndLeavesPackage_Issue3961()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_CancellationAfterTempCreationDeletesTempAndLeavesPackage_Issue3961));
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
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizeCli_DryRunDoesNotRewritePackage()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizeCli_DryRunDoesNotRewritePackage));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizeCli_CancellationReportsFailureJson_Issue3961()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizeCli_CancellationReportsFailureJson_Issue3961));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizeCli_JsonContinueOnErrorReportsAggregateSummary()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizeCli_JsonContinueOnErrorReportsAggregateSummary));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
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
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizeCli_JsonReportsBoundedFriendlyFailure));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizeCli_JsonBoundsZipEntryDiagnostics()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizeCli_JsonBoundsZipEntryDiagnostics));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_ReportsCleanupWarningsWhenTempDeleteFails()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_ReportsCleanupWarningsWhenTempDeleteFails));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RemovesPreexistingLegacyTempFileBeforeRewrite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RemovesPreexistingLegacyTempFileBeforeRewrite));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RejectsLockedLegacyTempFileBeforeRewrite()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RejectsLockedLegacyTempFileBeforeRewrite));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RemovesReadOnlyLegacyTempFileOnUnixBeforeRewrite()
    {
        if (OperatingSystem.IsWindows())
            return;

        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RemovesReadOnlyLegacyTempFileOnUnixBeforeRewrite));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RejectsPackageThatExceedsEntryCountLimit()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RejectsPackageThatExceedsEntryCountLimit));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RejectsEntryThatExceedsPerEntryLimit()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RejectsEntryThatExceedsPerEntryLimit));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RejectsPackageThatExceedsTotalUncompressedLimit()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RejectsPackageThatExceedsTotalUncompressedLimit));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RejectsXmlEntryThatExceedsTextLimit()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RejectsXmlEntryThatExceedsTextLimit));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
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
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RejectsUnsafeZipEntryNames));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RejectsDestinationNamesThatNormalizeToDuplicates()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RejectsDestinationNamesThatNormalizeToDuplicates));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_ScrubsSafeExternalAttributes()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_ScrubsSafeExternalAttributes));
        try
        {
            var packagePath = Path.Combine(projectRoot, "external-attributes.nupkg");
            CreatePackageWithAttributedEntries(
                packagePath,
                ("package/services/metadata/core-properties/random.psmdcp", "", UnixRegularFileAttributes(493)),
                ("payload.bin", "payload", 0x20));

            PackageCorePropertiesNormalizer.NormalizePackage(packagePath);

            using var archive = ZipFile.OpenRead(packagePath);
            Assert.All(archive.Entries, entry => Assert.Equal(0, entry.ExternalAttributes));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RejectsPosixSymlinkExternalAttributes()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RejectsPosixSymlinkExternalAttributes));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void PackageNormalizer_RejectsUnsafeDosExternalAttributes()
    {
        var projectRoot = TestProjectHelper.CreateTempProject(nameof(PackageNormalizer_RejectsUnsafeDosExternalAttributes));
        try
        {
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
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ReleaseWorkflow_PublishesOfficialContainerImage()
    {
        var workflow = ReadReleaseWorkflow();
        var dockerfile = RepositoryTestPaths.ReadText("Dockerfile");
        var dockerignore = RepositoryTestPaths.ReadText(".dockerignore");
        var entrypoint = RepositoryTestPaths.ReadText("scripts", "docker-entrypoint.sh");
        var project = RepositoryTestPaths.ReadText("src", "CodeIndex", "CodeIndex.csproj");

        Assert.Contains("publish-container:", workflow);
        Assert.Contains("needs: [preflight, verify-release-install]", workflow);
        Assert.Contains("packages: write", workflow);
        Assert.Contains("docker/setup-buildx-action@d7f5e7f509e45cec5c76c4d5afdd7de93d0b3df5 # v4", workflow);
        Assert.Contains("docker/login-action@c94ce9fb468520275223c153574b00df6fe4bcc9 # v3", workflow);
        Assert.Contains("docker/build-push-action@10e90e3645eae34f1e60eeb005ba3a3d33f178e8 # v6", workflow);
        Assert.Contains("platforms: linux/amd64,linux/arm64", workflow);
        Assert.Contains("ghcr.io/widthdom/codeindex:${version}", workflow);
        Assert.Contains("ghcr.io/widthdom/codeindex:latest", workflow);
        Assert.Contains("tags: ${{ steps.image-tags.outputs.tags }}", workflow);
        Assert.Contains("Extract container build metadata", workflow);
        Assert.Contains("git rev-parse --short=7 HEAD", workflow);
        Assert.Contains("git show -s --format=%cd --date=format:%Y-%m-%d HEAD", workflow);
        Assert.Contains("CDIDX_BUILD_COMMIT=${{ steps.container-metadata.outputs.commit }}", workflow);
        Assert.Contains("CDIDX_BUILD_DATE=${{ steps.container-metadata.outputs.date }}", workflow);
        Assert.Contains("CDIDX_BUILD_DIRTY=${{ steps.container-metadata.outputs.dirty }}", workflow);
        Assert.Contains("*-*) ;;", workflow);
        Assert.Contains("docker buildx imagetools inspect mcr.microsoft.com/dotnet/<image>:9.0.301-alpine3.22", dockerfile);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/sdk:9.0.301-alpine3.22@sha256:bdd1c9e2215a71e43d2f0c6978ace0a0652d7ecc21bf6f659d42d840500e1c44 AS build", dockerfile);
        Assert.Contains("FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine@sha256:7ec14bf41e70f3ca60f7b369b077636f642a0e6867caf28677d970e0abd9c6e6 AS runtime", dockerfile);
        Assert.DoesNotContain("FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build", dockerfile);
        Assert.DoesNotContain("FROM mcr.microsoft.com/dotnet/runtime-deps:8.0-alpine AS runtime", dockerfile);
        Assert.Contains("COPY scripts/docker-entrypoint.sh /usr/local/bin/cdidx-entrypoint", dockerfile);
        Assert.Contains("apk add --no-cache ca-certificates su-exec", dockerfile);
        Assert.Contains("addgroup -S -g 10001 cdidx", dockerfile);
        Assert.Contains("adduser -S -D -H -u 10001 -G cdidx -h /repo cdidx", dockerfile);
        Assert.Contains("chown cdidx:cdidx /repo", dockerfile);
        Assert.DoesNotContain("USER cdidx:cdidx", dockerfile);
        Assert.Contains("stat -c '%u' /repo", entrypoint);
        Assert.Contains("stat -c '%g' /repo", entrypoint);
        Assert.Contains("su-exec \"${target_uid}:${target_gid}\" cdidx \"$@\"", entrypoint);
        Assert.DoesNotContain("COPY . .", dockerfile);
        Assert.Contains("COPY Directory.Build.props nuget.config version.json ./", dockerfile);
        Assert.Contains("COPY src/CodeIndex/CodeIndex.csproj src/CodeIndex/packages.lock.json src/CodeIndex/", dockerfile);
        Assert.Contains("COPY src/CodeIndex/ src/CodeIndex/", dockerfile);
        Assert.Contains("ARG CDIDX_BUILD_COMMIT=unknown", dockerfile);
        Assert.Contains("-p:CdidxBuildCommitOverride=\"$CDIDX_BUILD_COMMIT\"", dockerfile);
        Assert.Contains("-p:CdidxBuildDateOverride=\"$build_date\"", dockerfile);
        Assert.Contains("-p:CdidxBuildDirtyOverride=\"$CDIDX_BUILD_DIRTY\"", dockerfile);
        Assert.Contains(".git/", dockerignore);
        Assert.Contains("tests/", dockerignore);
        Assert.Contains("tools/", dockerignore);
        Assert.Contains("docs/", dockerignore);
        Assert.Contains("changelog.d/", dockerignore);
        Assert.Contains("*.md", dockerignore);
        Assert.Contains("!COMMERCIAL_LICENSE.md", dockerignore);
        Assert.Contains("ARG TARGETARCH=amd64", dockerfile);
        Assert.Contains("linux-musl-x64", dockerfile);
        Assert.Contains("linux-musl-arm64", dockerfile);
        Assert.Contains("dotnet restore src/CodeIndex/CodeIndex.csproj", dockerfile);
        Assert.Contains("--locked-mode", dockerfile);
        Assert.Contains("--no-restore", dockerfile);
        Assert.Contains("ENTRYPOINT [\"/usr/local/bin/cdidx-entrypoint\"]", dockerfile);
        Assert.Contains("CdidxBuildCommitOverride", project);
        Assert.Contains("CdidxBuildDateOverride", project);
        Assert.Contains("CdidxBuildDirtyOverride", project);
        Assert.Contains("Microsoft.NET.ILLink.Tasks\" Version=\"8.", project);
        Assert.DoesNotContain("Microsoft.NET.ILLink.Tasks\" Version=\"10.", project);
    }

    [Fact]
    public void Dependabot_DoesNotBumpIlLinkPastReleaseSdkMajor()
    {
        var dependabot = RepositoryTestPaths.ReadText(".github", "dependabot.yml");

        Assert.Contains("dependency-name: Microsoft.NET.ILLink.Tasks", dependabot);
        Assert.Contains("version-update:semver-major", dependabot);
    }

    [Fact]
    public void MutationWorkflow_PinsActionsByCommitSha()
    {
        var workflow = RepositoryTestPaths.ReadWorkflow("mutation-testing.yml");

        Assert.Contains("actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd # v6.0.2", workflow);
        Assert.Contains("actions/setup-dotnet@9a946fdbd5fb07b82b2f5a4466058b876ab72bb2 # v5.3.0", workflow);
        Assert.Contains("actions/cache@27d5ce7f107fe9357f9df03efb73ab90386fccae # v5.0.5", workflow);
        Assert.DoesNotContain("actions/checkout@v6", workflow);
        Assert.DoesNotContain("actions/setup-dotnet@v5", workflow);
        Assert.DoesNotContain("actions/cache@v5", workflow);
    }

    private static string ReadReleaseWorkflow() => RepositoryTestPaths.ReadWorkflow("release.yml");

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
                    job.AppendLine(line);
                }

                continue;
            }

            if (line.StartsWith("  ", StringComparison.Ordinal)
                && !line.StartsWith("    ", StringComparison.Ordinal)
                && line.EndsWith(':'))
            {
                break;
            }

            job.AppendLine(line);
        }

        var text = job.ToString();
        Assert.False(string.IsNullOrEmpty(text), $"Could not find workflow job '{jobName}'.");
        return text;
    }
}
