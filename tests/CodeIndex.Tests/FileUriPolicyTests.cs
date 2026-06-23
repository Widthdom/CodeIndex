using CodeIndex;
using CodeIndex.Database;
using CodeIndex.Lsp;

namespace CodeIndex.Tests;

public class FileUriPolicyTests
{
    [Fact]
    public void PathToFileUri_EscapesPathCharacters_Issue3995()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdidx uri root");
        var uri = FileUriPolicy.PathToFileUri(Path.Combine("src", "a file#name.cs"), root);

        Assert.StartsWith("file:", uri, StringComparison.Ordinal);
        Assert.Contains("a%20file%23name.cs", uri, StringComparison.Ordinal);
    }

    [Fact]
    public void LspPathToUri_UsesSharedFileUriPolicy_Issue3995()
    {
        var root = Path.Combine(Path.GetTempPath(), "cdidx lsp uri");
        var uri = LspServer.PathToUri(Path.Combine("src", "query #1.cs"), root);

        Assert.Contains("query%20%231.cs", uri, StringComparison.Ordinal);
        Assert.Equal(Path.GetFullPath(Path.Combine(root, "src", "query #1.cs")), LspServer.UriToPath(uri));
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
}
