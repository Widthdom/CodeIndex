using CodeIndex.Cli;
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

    [Fact]
    public void DbPragmaPolicy_PragmaSqlHelpers_ConstrainValues_Issue4070()
    {
        Assert.Equal("PRAGMA temp_store=MEMORY", DbPragmaPolicy.TempStoreMemoryPragmaSql);
        Assert.Equal("PRAGMA auto_vacuum=INCREMENTAL", DbPragmaPolicy.AutoVacuumIncrementalPragmaSql);
        Assert.Equal("PRAGMA cache_size=-4096", DbPragmaPolicy.CacheSizePragmaSql(4096));
        Assert.Equal("PRAGMA mmap_size=8192", DbPragmaPolicy.MmapSizePragmaSql(8192));
        Assert.Equal("PRAGMA busy_timeout=5000", DbPragmaPolicy.BusyTimeoutPragmaSql(5000));

        Assert.Throws<ArgumentOutOfRangeException>(() => DbPragmaPolicy.CacheSizePragmaSql(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DbPragmaPolicy.MmapSizePragmaSql(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DbPragmaPolicy.BusyTimeoutPragmaSql(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DbPragmaPolicy.BusyTimeoutPragmaSql(DbPragmaPolicy.MaxBusyTimeoutMs + 1));
    }

    [Fact]
    public void DbPragmaPolicy_ReadBusyTimeoutPragmaSql_UsesBoundedEnvironmentValue_Issue4070()
    {
        using var scope = CdidxEnvironment.Push(new Dictionary<string, string>
        {
            ["CDIDX_TEST_BUSY_TIMEOUT_MS"] = "250",
        });

        Assert.Equal(
            "PRAGMA busy_timeout=250",
            DbPragmaPolicy.ReadBusyTimeoutPragmaSql("CDIDX_TEST_BUSY_TIMEOUT_MS"));
    }

    [Fact]
    public void DbPragmaPolicy_ReadConnectionPragmaSettings_UsesScopedEnvironmentParser()
    {
        using var scope = CdidxEnvironment.Push(new Dictionary<string, string>
        {
            ["CDIDX_TEST_CACHE_SIZE_KB"] = "2048",
            ["CDIDX_TEST_MMAP_SIZE_BYTES"] = "4096",
        });

        var settings = DbPragmaPolicy.ReadConnectionPragmaSettings(
            "CDIDX_TEST_CACHE_SIZE_KB",
            defaultCacheSizeKb: 1024,
            maxCacheSizeKb: 8192,
            "CDIDX_TEST_MMAP_SIZE_BYTES",
            defaultMmapSizeBytes: 0,
            maxMmapSizeBytes: 8192,
            is64BitProcess: true);

        Assert.Equal(2048, settings.CacheSizeKb);
        Assert.Equal(4096, settings.MmapSizeBytes);
    }

    [Theory]
    [InlineData(null, EnvironmentOptionParser.StatusUnset)]
    [InlineData("", EnvironmentOptionParser.StatusInvalid)]
    [InlineData("abc", EnvironmentOptionParser.StatusInvalid)]
    [InlineData("0", EnvironmentOptionParser.StatusBelowMinimum)]
    [InlineData("101", EnvironmentOptionParser.StatusAboveMaximum)]
    public void EnvironmentOptionParser_ReadInt32_ReportsFallbackReason(string? value, string expectedStatus)
    {
        var values = value is null
            ? new Dictionary<string, string>()
            : new Dictionary<string, string> { ["CDIDX_TEST_INT"] = value };
        using var scope = CdidxEnvironment.Push(values);

        var result = EnvironmentOptionParser.ReadInt32(
            "CDIDX_TEST_INT",
            fallback: 10,
            minimum: 1,
            maximum: 100);

        Assert.Equal(10, result.Value);
        Assert.Equal(expectedStatus, result.Status);
        Assert.True(result.UsedFallback);
    }

    [Fact]
    public void EnvironmentOptionParser_ReadInt32_PreservesConfigSourceMetadata()
    {
        using var scope = CdidxEnvironment.Push(
            new Dictionary<string, string> { ["CDIDX_TEST_INT"] = "42" },
            new Dictionary<string, string> { ["CDIDX_TEST_INT"] = ".cdidx/config.json" });

        var result = EnvironmentOptionParser.ReadInt32(
            "CDIDX_TEST_INT",
            fallback: 10,
            minimum: 1,
            maximum: 100);

        Assert.Equal(42, result.Value);
        Assert.Equal(EnvironmentOptionParser.StatusParsed, result.Status);
        Assert.Equal(EnvironmentOptionParser.SourceKindConfig, result.SourceKind);
        Assert.Equal(".cdidx/config.json", result.Source);
        Assert.Equal(".cdidx/config.json", result.SourceDetail);
        Assert.False(result.UsedFallback);
    }
}
