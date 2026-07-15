using CodeIndex;
using CodeIndex.Database;
using CodeIndex.Lsp;

namespace CodeIndex.Tests;

public class FileUriPolicyTests
{
    [Fact]
    public void PathAndLspFileUris_ShareEscapingAndRoundTrip_Issue3995()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdidx uri root");
        var relativePath = Path.Combine("src", "a file#name.cs");
        var uri = FileUriPolicy.PathToFileUri(relativePath, root);
        var lspUri = LspServer.PathToUri(relativePath, root);

        Assert.StartsWith("file:", uri, StringComparison.Ordinal);
        Assert.Contains("a%20file%23name.cs", uri, StringComparison.Ordinal);
        Assert.Equal(uri, lspUri);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, relativePath)), LspServer.UriToPath(lspUri));
    }

    [Fact]
    public void AbsoluteFileUriToPath_RejectsRelativeFileUri_Issue3995()
    {
        var ex = Assert.Throws<ArgumentException>(() => LspServer.UriToPath("file:relative/path.cs"));

        Assert.Contains(FileUriPolicy.AbsoluteFileUriRequiredMessage, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DbConnectionFactory_ToReadOnlyUri_UsesSharedFileUriEscaping_Issue3995()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "cdidx db #1.sqlite");

        var uri = DbConnectionFactory.ToReadOnlyUri(dbPath);

        Assert.StartsWith("file:", uri, StringComparison.Ordinal);
        Assert.Contains("cdidx%20db%20%231.sqlite", uri, StringComparison.Ordinal);
        Assert.EndsWith("?immutable=1&mode=ro", uri, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(dbPath), DbConnectionFactory.TryGetLocalPath(uri));
    }

    [Theory]
    [InlineData("file:///tmp/codeindex.db?immutable=1", true)]
    [InlineData("file:///tmp/codeindex.db?immutable=1&mode=ro", true)]
    [InlineData("file:///tmp/codeindex.db?mode=ro&immutable=1", true)]
    [InlineData("file:///tmp/codeindex.db?mode=ro", false)]
    [InlineData("file:///tmp/codeindex.db?Immutable=1&mode=ro", false)]
    [InlineData("file:///tmp/codeindex.db?%69mmutable=1&mode=ro", false)]
    [InlineData("file:///tmp/codeindex.db?immutable=1&immutable=0&mode=ro", false)]
    [InlineData("file:///tmp/codeindex.db?immutable=1&immutable=1&mode=ro", false)]
    [InlineData("file:///tmp/codeindex.db? immutable=1&mode=ro", false)]
    [InlineData("file:///tmp/codeindex.db?immutable=1&mode=ro&cache=shared", false)]
    public void SqliteFileUri_ImmutableSnapshotTrustRequiresCanonicalUnambiguousQuery_Issue4541(
        string uri,
        bool expected)
    {
        Assert.Equal(expected, SqliteFileUri.RequestsUnambiguousImmutableSnapshot(uri));
    }
}
