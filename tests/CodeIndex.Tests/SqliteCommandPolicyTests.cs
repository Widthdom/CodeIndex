using CodeIndex.Database;
using System.Data;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class SqliteCommandPolicyTests
{
    [Fact]
    public void AddTypedParameters_StabilizesSqliteTypes_Issue3982()
    {
        using var command = new SqliteCommand();

        var text = SqliteCommandPolicy.AddText(command, "@text", "value");
        var nullableText = SqliteCommandPolicy.AddNullableText(command, "@nullable_text", null);
        var number = SqliteCommandPolicy.AddInt64(command, "$number", 42);
        var flag = SqliteCommandPolicy.AddBoolean(command, ":flag", true);
        var limit = SqliteCommandPolicy.AddLimit(command, "@limit", 10);

        Assert.Equal(SqliteType.Text, text.SqliteType);
        Assert.Equal(DbType.String, text.DbType);
        Assert.Equal(5, text.Size);
        Assert.Equal("value", text.Value);
        Assert.Equal(SqliteType.Text, nullableText.SqliteType);
        Assert.Equal(DbType.String, nullableText.DbType);
        Assert.Same(DBNull.Value, nullableText.Value);
        Assert.Equal(SqliteType.Integer, number.SqliteType);
        Assert.Equal(DbType.Int64, number.DbType);
        Assert.Equal(42L, number.Value);
        Assert.Equal(SqliteType.Integer, flag.SqliteType);
        Assert.Equal(DbType.Int32, flag.DbType);
        Assert.Equal(1, flag.Value);
        Assert.Equal(SqliteType.Integer, limit.SqliteType);
        Assert.Equal(DbType.Int32, limit.DbType);
        Assert.Equal(10, limit.Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => SqliteCommandPolicy.AddOffset(command, "@offset", -1));
    }

    [Fact]
    public void AddDateTime_UsesStableSqliteTextFormat_Issue3907()
    {
        using var command = new SqliteCommand();

        var parameter = SqliteCommandPolicy.AddDateTime(
            command,
            "@since",
            new DateTime(2026, 6, 23, 4, 5, 6, 789, DateTimeKind.Utc).AddTicks(1234));

        Assert.Equal(SqliteType.Text, parameter.SqliteType);
        Assert.Equal(DbType.String, parameter.DbType);
        Assert.Equal("2026-06-23 04:05:06.7891234", parameter.Value);
        Assert.Equal(27, parameter.Size);
    }

    [Fact]
    public void AddCopy_PreservesExplicitParameterShape_Issue3907()
    {
        using var sourceCommand = new SqliteCommand();
        var source = SqliteCommandPolicy.AddText(sourceCommand, "@path", "src/app.cs");

        using var targetCommand = new SqliteCommand();
        var copy = SqliteCommandPolicy.AddCopy(targetCommand, source);

        Assert.Equal(source.ParameterName, copy.ParameterName);
        Assert.Equal(source.SqliteType, copy.SqliteType);
        Assert.Equal(source.DbType, copy.DbType);
        Assert.Equal(source.Size, copy.Size);
        Assert.Equal(source.Value, copy.Value);
    }

    [Fact]
    public void ReadInt32Scalar_RejectsOverflowWithDiagnostic_Issue3982()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 2147483648";

        var ex = Assert.Throws<InvalidDataException>(
            () => SqliteCommandPolicy.ReadInt32Scalar(command, "fixture row count"));

        Assert.Contains("fixture row count", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Int32", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadNullableScalars_HandleNullAndStrictRequiredDiagnostics_Issue3982()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT NULL";

        Assert.Null(SqliteCommandPolicy.ReadNullableInt64Scalar(command, "nullable count"));
        var ex = Assert.Throws<InvalidDataException>(
            () => SqliteCommandPolicy.ReadInt64Scalar(command, "required count"));

        Assert.Contains("required count", ex.Message, StringComparison.Ordinal);
        Assert.Contains("NULL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SqlConstructionHelpers_QuoteIdentifiersAndParameterizeLimits_Issue3982()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE \"odd \"\" table\" (id INTEGER); CREATE INDEX \"odd \"\" index\" ON \"odd \"\" table\" (id)";
            create.ExecuteNonQuery();
        }

        using (var tableInfo = connection.CreateCommand())
        {
            tableInfo.CommandText = SqliteCommandPolicy.TableInfoPragmaSql("odd \" table");
            using var reader = tableInfo.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("id", reader.GetString(1));
        }

        using (var indexList = connection.CreateCommand())
        {
            indexList.CommandText = SqliteCommandPolicy.IndexListPragmaSql("odd \" table");
            using var reader = indexList.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal("odd \" index", reader.GetString(1));
        }

        Assert.Equal(
            "SELECT COUNT(*) FROM (SELECT 1 FROM \"odd \"\" table\" LIMIT $limit)",
            SqliteCommandPolicy.CountRowsWithLimitSql("odd \" table", "$limit"));
        Assert.Throws<ArgumentException>(() => SqliteCommandPolicy.CountRowsWithLimitSql("files", "limit"));
    }
}
