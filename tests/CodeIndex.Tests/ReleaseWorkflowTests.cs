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
}
