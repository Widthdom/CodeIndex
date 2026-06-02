using CodeIndex.PackageNormalize;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace CodeIndex.Tests;

public class ReleaseWorkflowTests
{
    [Fact]
    public void ReleaseWorkflow_PublishesTrimmedSelfContainedBinariesAndVerifiesCliJson()
    {
        var workflow = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "release.yml"));

        Assert.Contains("-p:PublishTrimmed=true", workflow);
        Assert.DoesNotContain("-p:PublishTrimmed=false", workflow);
        Assert.Contains("status --json", workflow);
        Assert.Contains("Expected status --json to exit 0", workflow);
        Assert.Contains("'\"files\":'", workflow);
        Assert.Contains("'\"version\":'", workflow);
    }

    [Fact]
    public void ReleaseWorkflow_VerifiesPublishedInstallForTheCurrentRid()
    {
        var workflow = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "release.yml"));

        Assert.Contains("expected_rids=\"linux-x64 linux-arm64 osx-arm64 win-x64 win-arm64\"", workflow);
        Assert.Contains("asset=\"CodeIndex-${rid}.zip\"", workflow);
        Assert.Contains("asset=\"CodeIndex-${rid}.tar.gz\"", workflow);
        Assert.Contains("Missing release archive for ${rid}", workflow);
        Assert.Contains("CodeIndex-osx-x64.*", workflow);
        Assert.Contains("native_asset=\"libe_sqlite3.so\"", workflow);
        Assert.Contains("for asset in \"$binary_name\" \"$native_asset\"", workflow);
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
        var workflow = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "release.yml"));

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
        var workflow = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "release.yml"));

        Assert.Contains("Release tag must be a v-prefixed SemVer version", workflow);
        Assert.Contains("jq -r '.version // empty' version.json", workflow);
        Assert.Contains("does not match release tag", workflow);
        Assert.Contains("https://api.nuget.org/v3-flatcontainer/cdidx/${VERSION}/cdidx.${VERSION}.nupkg", workflow);
        Assert.Contains("NuGet package cdidx ${VERSION} is already published", workflow);
        Assert.Contains("Expected packed package ${expected_package} was not produced", workflow);
        Assert.DoesNotContain("--skip-duplicate", workflow);
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
        var workflow = File.ReadAllText(Path.Combine(GetRepositoryRoot(), ".github", "workflows", "release.yml"));

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
            Assert.False(File.Exists(packagePath + ".normalize-tmp"));
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
            Assert.False(File.Exists(packagePath + ".normalize-tmp"));
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
            Assert.False(File.Exists(packagePath + ".normalize-tmp"));
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
            Assert.False(File.Exists(packagePath + ".normalize-tmp"));
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
            Assert.False(File.Exists(packagePath + ".normalize-tmp"));
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
            Assert.Contains("duplicate destination name docs/readme.txt", exception.Message);
            Assert.False(File.Exists(packagePath + ".normalize-tmp"));
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public void ReleaseWorkflow_PublishesOfficialContainerImage()
    {
        var root = GetRepositoryRoot();
        var workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        var dockerfile = File.ReadAllText(Path.Combine(root, "Dockerfile"));

        Assert.Contains("publish-container:", workflow);
        Assert.Contains("needs: create-release", workflow);
        Assert.Contains("packages: write", workflow);
        Assert.Contains("docker/login-action@v3", workflow);
        Assert.Contains("docker/build-push-action@v6", workflow);
        Assert.Contains("platforms: linux/amd64,linux/arm64", workflow);
        Assert.Contains("ghcr.io/widthdom/codeindex:${version}", workflow);
        Assert.Contains("ghcr.io/widthdom/codeindex:latest", workflow);
        Assert.Contains("tags: ${{ steps.image-tags.outputs.tags }}", workflow);
        Assert.Contains("*-*) ;;", workflow);
        Assert.Contains("ARG TARGETARCH=amd64", dockerfile);
        Assert.Contains("linux-musl-x64", dockerfile);
        Assert.Contains("linux-musl-arm64", dockerfile);
        Assert.Contains("ENTRYPOINT [\"cdidx\"]", dockerfile);
    }

    private static string GetRepositoryRoot()
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

    private static void CreateMinimalNuGetPackage(string packagePath, string corePropertiesFileName)
    {
        var corePropertiesPath = $"package/services/metadata/core-properties/{corePropertiesFileName}";
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);

        WriteZipEntry(archive, "[Content_Types].xml", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
              <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
              <Override PartName="/{corePropertiesPath}" ContentType="application/vnd.openxmlformats-package.core-properties+xml" />
            </Types>
            """);
        WriteZipEntry(archive, "_rels/.rels", $"""
            <?xml version="1.0" encoding="utf-8"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="R1" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="/{corePropertiesPath}" />
            </Relationships>
            """);
        WriteZipEntry(archive, "cdidx.nuspec", """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata><id>cdidx</id><version>1.0.0</version></metadata>
            </package>
            """);
        WriteZipEntry(archive, corePropertiesPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <coreProperties xmlns="http://schemas.openxmlformats.org/package/2006/metadata/core-properties" />
            """);
    }

    private static void CreatePackageWithEntries(string packagePath, params (string EntryName, string Content)[] entries)
    {
        using var archive = ZipFile.Open(packagePath, ZipArchiveMode.Create);
        foreach (var entry in entries)
            WriteZipEntry(archive, entry.EntryName, entry.Content);
    }

    private static void WriteZipEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content.Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static string ReadZipEntryText(ZipArchive archive, string entryName)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidOperationException($"Missing ZIP entry: {entryName}");
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
