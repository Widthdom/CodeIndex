using System.Reflection;
using CodeIndex.Database;
using CodeIndex.Models;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public class DbReaderUtilityTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("./install.sh", "install.sh")]
    [InlineData("/install.sh", "install.sh")]
    [InlineData("src/", "src")]
    [InlineData("src/Services", "src/Services")]
    [InlineData("*.py", "%.py")]
    [InlineData("src/*.py", "src/%.py")]
    [InlineData("foo?bar", "foo_bar")]
    [InlineData(@"literal\*.py", "literal*.py")]
    [InlineData(@"literal\?.py", "literal?.py")]
    [InlineData(@"literal\[name\].py", "literal[name].py")]
    [InlineData(@"src\Foo.cs", @"src\\Foo.cs")]
    public void BuildPathLikePattern_TreatsGlobTokensAsWildcards(string input, string expected)
    {
        Assert.Equal(expected, DbReader.BuildPathLikePattern(input));
    }

    [Theory]
    [InlineData("tools", "tools/%")]
    [InlineData("./install.sh", "install.sh/%")]
    [InlineData("/src/", "src/%")]
    public void BuildPathSubtreeLikePattern_NormalizesRepoRelativePrefixes_Issue4163(string input, string expected)
    {
        Assert.Equal(expected, DbReader.BuildPathSubtreeLikePattern(input));
    }

    [Fact]
    public void SqliteIdentifier_Quote_AllowsUnusualTableNamesForSchemaPragmas()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE \"odd \"\" table\" (\"odd col\" INTEGER)";
            cmd.ExecuteNonQuery();
        }

        var columns = DbSchemaCache.LoadColumns(connection, "odd \" table");

        Assert.Contains("odd col", columns);
        Assert.Equal("\"odd \"\" table\"", SqliteIdentifier.Quote("odd \" table"));
    }

    [Theory]
    [InlineData("page_count")]
    [InlineData("_pragma1")]
    public void SqliteIdentifier_ValidatePragmaName_AllowsBarePragmaNames(string name)
    {
        Assert.Equal(name, SqliteIdentifier.ValidatePragmaName(name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("page-count")]
    [InlineData("page_count;VACUUM")]
    [InlineData("1page_count")]
    public void SqliteIdentifier_ValidatePragmaName_RejectsUnsafePragmaNames(string name)
    {
        Assert.Throws<ArgumentException>(() => SqliteIdentifier.ValidatePragmaName(name));
    }

    [Fact]
    public void DegradationReasonCodes_AllCodesHaveActionableMetadata()
    {
        foreach (var code in DegradationReasonCodes.All)
        {
            var metadata = DegradationReasonCodes.GetMetadata(code);

            Assert.Equal(code, metadata.Code);
            Assert.False(string.IsNullOrWhiteSpace(metadata.HumanText));
            Assert.Contains("cdidx", metadata.RecommendedAction, StringComparison.Ordinal);
            Assert.Contains("cdidx", metadata.AlternativeAction, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(DegradationReasonCodes.MissingFoldBackfill, "--exact falls back")]
    [InlineData(DegradationReasonCodes.StaleFoldKeyVersion, "older fold-key version")]
    [InlineData(DegradationReasonCodes.StaleFoldKeyFingerprint, "older runtime fingerprint")]
    [InlineData(DegradationReasonCodes.FoldRowsNotRestamped, "not restamped")]
    public void DegradationReasonCodes_BuildsFoldExplanationFromCode(string code, string expectedText)
    {
        var explanation = DegradationReasonCodes.BuildFoldNotReadyExplanation(code);

        Assert.Contains(expectedText, explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void DatabaseSizeAttribution_UnavailableDoesNotReportZeroObjectSizes_Issue4888()
    {
        var attribution = DbReader.BuildUnavailableDatabaseSizeAttribution(
            "dbstat_unavailable",
            new StatusDbPragmaSettings
            {
                PageSize = 4096,
                PageCount = 10,
                FreelistCount = 2,
            },
            logicalDatabaseBytes: 40960,
            mainFileBytes: 40960,
            walFileBytes: 0,
            shmFileBytes: 0,
            physicalFileSetBytes: 40960,
            freelistBytes: 8192);

        Assert.False(attribution.Available);
        Assert.Equal("unavailable", attribution.Measurement);
        Assert.Equal("dbstat_unavailable", attribution.UnavailableReason);
        Assert.Null(attribution.AllocatedObjectBytes);
        Assert.Null(attribution.TableBytes);
        Assert.Null(attribution.IndexBytes);
        Assert.Null(attribution.UnexplainedResidualBytes);
        Assert.Null(attribution.TopObjects);
    }

    [Fact]
    public void AnalyzeFtsQuery_AllTokensTooLong_ReturnsDegradedReason()
    {
        var query = new string('x', DbReader.FtsUnicode61MaxTokenLength + 1);

        var diagnostics = DbReader.AnalyzeFtsQuery(query);

        Assert.Equal(DbReader.AllTokensFilteredByLengthReason, diagnostics.QueryDegradedReason);
        Assert.Equal([query], diagnostics.TokensDropped);
    }

    [Fact]
    public void NormalizeSymbolSearchQueries_SkipsAlreadyNormalizedInput()
    {
        var method = typeof(DbReader).GetMethod(
            "NormalizeSymbolSearchQueries",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var normalized = Assert.IsAssignableFrom<IReadOnlyList<string>>(method!.Invoke(null, [new[] { "module.exports.fetchData", "module.exports.fetchData" }, "javascript", false]));
        var secondPass = Assert.IsAssignableFrom<IReadOnlyList<string>>(method.Invoke(null, [normalized, "javascript", false]));

        Assert.Same(normalized, secondPass);
        Assert.Equal(["fetchData"], normalized);
    }
}
