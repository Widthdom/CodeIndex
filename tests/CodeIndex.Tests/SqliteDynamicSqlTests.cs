using System.Reflection;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

public class SqliteDynamicSqlTests
{
    [Fact]
    public void BuildParameterList_AllowsMaximumSqliteVariables_Issue3702()
    {
        var list = SqliteDynamicSql.BuildParameterList("p", SqliteDynamicSql.MaxSqlVariables);

        Assert.StartsWith("@p0, @p1", list, StringComparison.Ordinal);
        Assert.EndsWith("@p998", list, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildParameterList_RejectsListsOverSqliteVariableBudget_Issue3702()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => SqliteDynamicSql.BuildParameterList("p", SqliteDynamicSql.MaxSqlVariables + 1));

        Assert.Contains("exceeding the supported budget", ex.Message);
    }

    [Fact]
    public void PathFilterParameters_AllowLargeListNearSqliteVariableBudget_Issue3702()
    {
        var pathPatterns = Enumerable
            .Range(0, SqliteDynamicSql.MaxSqlVariables)
            .Select(i => $"src/{i}.cs")
            .ToList();
        var sql = "SELECT 1 FROM files f WHERE 1 = 1";

        DbReader.AppendPathFilters(ref sql, pathPatterns, excludePathPatterns: null, excludeTests: false);
        using var connection = CreateInMemoryConnection();
        using var cmd = connection.CreateCommand();
        DbReader.AddPathFilterParameters(cmd, pathPatterns, excludePathPatterns: null);

        Assert.Contains("@pathPattern998", sql);
        Assert.Equal(SqliteDynamicSql.MaxSqlVariables, cmd.Parameters.Count);
        Assert.Equal("%src/998.cs%", cmd.Parameters["@pathPattern998"].Value);
        Assert.Equal(SqliteType.Text, cmd.Parameters["@pathPattern998"].SqliteType);
    }

    [Fact]
    public void PathFilterParameters_RejectCombinedIncludeExcludeOverBudget_Issue3702()
    {
        var includePatterns = Enumerable.Range(0, 500).Select(i => $"src/{i}.cs").ToList();
        var excludePatterns = Enumerable.Range(0, 500).Select(i => $"tests/{i}.cs").ToList();
        var sql = "SELECT 1 FROM files f WHERE 1 = 1";

        var ex = Assert.Throws<ArgumentOutOfRangeException>(
            () => DbReader.AppendPathFilters(ref sql, includePatterns, excludePatterns, excludeTests: false));

        Assert.Contains("path filters", ex.Message);
    }

    [Fact]
    public void VisibilityFilterParameters_UseSharedBudgetAfterAliasExpansion_Issue3702()
    {
        using var connection = CreateInMemoryConnection();
        using var cmd = connection.CreateCommand();
        var method = typeof(DbReader).GetMethod(
            "AddVisibilityFilterParameters",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(DbReader), "AddVisibilityFilterParameters");

        method.Invoke(null, new object?[] { cmd, new[] { "public" }, new[] { "private" } });

        Assert.Equal(6, cmd.Parameters.Count);
        Assert.Equal("public", cmd.Parameters["@visibility0"].Value);
        Assert.Equal("export", cmd.Parameters["@visibility3"].Value);
        Assert.Equal("private", cmd.Parameters["@excludeVisibility0"].Value);
        Assert.Equal("fileprivate", cmd.Parameters["@excludeVisibility1"].Value);
        Assert.Equal(SqliteType.Text, cmd.Parameters["@visibility0"].SqliteType);
    }

    [Fact]
    public void SupportedLanguageParameters_UseSharedDynamicInListBuilder_Issue3702()
    {
        using var connection = CreateInMemoryConnection();
        using var cmd = connection.CreateCommand();
        var method = typeof(DbWriter).GetMethod(
            "BuildSupportedLanguageParameters",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(DbWriter), "BuildSupportedLanguageParameters");

        var names = Assert.IsType<List<string>>(method.Invoke(null, new object?[] { cmd, new[] { "csharp", "python" } }));

        Assert.Equal(new[] { "@lang0", "@lang1" }, names);
        Assert.Equal("csharp", cmd.Parameters["@lang0"].Value);
        Assert.Equal("python", cmd.Parameters["@lang1"].Value);
        Assert.Equal(SqliteType.Text, cmd.Parameters["@lang0"].SqliteType);
    }

    private static SqliteConnection CreateInMemoryConnection()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        return connection;
    }
}
