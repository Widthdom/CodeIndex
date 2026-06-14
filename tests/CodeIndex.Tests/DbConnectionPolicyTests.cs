using System.Data;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public class DbConnectionPolicyTests
{
    [Fact]
    public void DbConnectionFactory_OpenWithRetry_OpensConnection()
    {
        using var connection = DbConnectionFactory.OpenWithRetry(
            () => new SqliteConnection("Data Source=:memory:"),
            static connection => connection.Open(),
            maxOpenAttempts: 1,
            dbPath: ":memory:");

        Assert.Equal(ConnectionState.Open, connection.State);
    }

    [Fact]
    public void DbConnectionFactory_ToReadOnlyUri_AppendsOnlyMissingReadOnlyFlags()
    {
        var uri = DbConnectionFactory.ToReadOnlyUri("file:///tmp/codeindex.db?mode=ro");

        Assert.Equal("file:///tmp/codeindex.db?mode=ro&immutable=1", uri);
    }

    [Fact]
    public void DbPragmaPolicy_ApplyConnectionPerformancePragmas_EmitsConfiguredStatements()
    {
        var statements = new List<string>();

        DbPragmaPolicy.ApplyConnectionPerformancePragmas(
            statements.Add,
            new DbConnectionPragmaSettings(CacheSizeKb: 4096, MmapSizeBytes: 8192));

        Assert.Equal(
            [
                "PRAGMA cache_size=-4096",
                "PRAGMA temp_store=MEMORY",
                "PRAGMA mmap_size=8192",
            ],
            statements);
    }

    [Fact]
    public void DbPragmaPolicy_ApplyConnectionPerformancePragmas_SkipsMmapWhenUnavailable()
    {
        var statements = new List<string>();

        DbPragmaPolicy.ApplyConnectionPerformancePragmas(
            statements.Add,
            new DbConnectionPragmaSettings(CacheSizeKb: 2048, MmapSizeBytes: null));

        Assert.Equal(
            [
                "PRAGMA cache_size=-2048",
                "PRAGMA temp_store=MEMORY",
            ],
            statements);
    }
}
