using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class SqliteConnectionPolicyTests
{
    [Theory]
    [InlineData("file:///tmp/codeindex.db", "file:///tmp/codeindex.db?immutable=1&mode=ro")]
    [InlineData("file:///tmp/codeindex.db?immutable=1", "file:///tmp/codeindex.db?immutable=1&mode=ro")]
    [InlineData("file:///tmp/codeindex.db?mode=ro", "file:///tmp/codeindex.db?mode=ro&immutable=1")]
    [InlineData("file:///tmp/codeindex.db?foo=bar&immutable=1", "file:///tmp/codeindex.db?foo=bar&immutable=1&mode=ro")]
    [InlineData("file:///tmp/code;index.db?immutable=1", "file:///tmp/code%3Bindex.db?immutable=1&mode=ro")]
    public void ToReadOnlyUri_AppendsMissingFlagsWithoutSeparatorDrift_Issue3983(string input, string expected)
    {
        var actual = SqliteConnectionPolicy.ToReadOnlyUri(input);

        Assert.Equal(expected, actual);
        Assert.DoesNotContain("immutable=1?mode=ro", actual, StringComparison.Ordinal);
        Assert.DoesNotContain("mode=ro?immutable=1", actual, StringComparison.Ordinal);
    }

    [Fact]
    public void ToReadOnlyUri_EncodesFilesystemPaths_Issue3983()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "cdidx policy ; spaced path.db");

        var uri = SqliteConnectionPolicy.ToReadOnlyUri(dbPath);

        Assert.StartsWith("file:", uri, StringComparison.Ordinal);
        Assert.Contains("immutable=1", uri, StringComparison.Ordinal);
        Assert.Contains("mode=ro", uri, StringComparison.Ordinal);
        Assert.Contains("%20", uri, StringComparison.Ordinal);
        Assert.Contains("%3B", uri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildConnectionString_StabilizesModeAndPooling_Issue3983()
    {
        const string dbPath = "fixture ; database.db";

        var readOnly = new SqliteConnectionStringBuilder(
            SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ReadOnly));
        var readWrite = new SqliteConnectionStringBuilder(
            SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ReadWrite));
        var unpooled = new SqliteConnectionStringBuilder(
            SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.Unpooled));
        var readOnlyUnpooled = new SqliteConnectionStringBuilder(
            SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ReadOnlyUnpooled));

        Assert.Equal(dbPath, readOnly.DataSource);
        Assert.Equal(SqliteOpenMode.ReadOnly, readOnly.Mode);
        Assert.True(readOnly.Pooling);
        Assert.Equal(SqliteOpenMode.ReadWrite, readWrite.Mode);
        Assert.False(unpooled.Pooling);
        Assert.Equal(SqliteOpenMode.ReadOnly, readOnlyUnpooled.Mode);
        Assert.False(readOnlyUnpooled.Pooling);

        var immutable = SqliteConnectionPolicy.BuildConnectionString(dbPath, SqliteConnectionPolicyMode.ImmutableReadOnlyUri);
        Assert.StartsWith("Data Source=file:", immutable, StringComparison.Ordinal);
        Assert.Contains("immutable=1", immutable, StringComparison.Ordinal);
        Assert.Contains("mode=ro", immutable, StringComparison.Ordinal);
        Assert.Contains("Mode=ReadOnly", immutable, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateCommand_AppliesDefaultTimeout_Issue3983()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        using var command = SqliteConnectionPolicy.CreateCommand(connection, "SELECT 1");

        Assert.Equal("SELECT 1", command.CommandText);
        Assert.Equal(SqliteConnectionPolicy.DefaultCommandTimeoutSeconds, command.CommandTimeout);
    }

    [Fact]
    public void BuildStatus_SurfacesFallbackAndTimeoutDiagnostics_Issue3983()
    {
        var status = SqliteConnectionPolicy.BuildStatus(
            isReadOnly: true,
            readOnlyFallback: true,
            walCheckpointAttempted: true,
            walCheckpointSucceeded: false,
            readOnlyImmutableFallback: true,
            walCheckpointSkippedReason: "uri_path_parse_failed",
            walCheckpointFailureReason: "sqlite_error_14",
            walStaleSnapshotRisk: true,
            walStaleSnapshotReason: "immutable_fallback_may_skip_wal");

        Assert.Equal(SqliteConnectionPolicy.ImmutableReadOnlyUriModeName, status.ActiveMode);
        Assert.True(status.ReadOnlyFallback);
        Assert.True(status.ImmutableUri);
        Assert.Equal(SqliteConnectionPolicy.DefaultCommandTimeoutSeconds, status.CommandTimeoutSeconds);
        Assert.True(status.LongRunningCommandsRequireCancellation);
        Assert.Equal("uri_path_parse_failed", status.WalCheckpointSkippedReason);
        Assert.Equal("sqlite_error_14", status.WalCheckpointFailureReason);
        Assert.True(status.WalStaleSnapshotRisk);
        Assert.Equal("immutable_fallback_may_skip_wal", status.WalStaleSnapshotReason);
    }
}
