using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

[Collection("SQLite pool sensitive")]
public class DiffCommandRunnerTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void Run_JsonDiffReportsSyntheticDatabaseDrift_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: true);

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--json", "--limit", "5"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("different", root.GetProperty("status").GetString());
            Assert.False(root.GetProperty("identical").GetBoolean());
            Assert.Equal(50, root.GetProperty("summary").GetProperty("left_file_count").GetInt64());
            Assert.Equal(51, root.GetProperty("summary").GetProperty("right_file_count").GetInt64());
            Assert.Equal(1, root.GetProperty("summary").GetProperty("file_count_delta").GetInt64());
            Assert.Contains(
                root.GetProperty("files_only_in_right").EnumerateArray(),
                item => item.GetString() == "src/Extra.cs");
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_OversizedFileUriQueryReturnsBoundedErrorBeforeReadingHeaders_Issue3140()
    {
        var dbUri = "file:///tmp/codeindex.db?" + new string('a', SqliteFileUri.MaxQueryLength + 1);

        var (exitCode, stdout, stderr) = RunWithCapturedStreams([dbUri, "/tmp/missing-codeindex.db"]);

        Assert.Equal(3, exitCode);
        Assert.Equal(string.Empty, stdout);
        Assert.Contains("invalid database file URI", stderr);
        Assert.Contains($"SQLite file URI query length exceeds {SqliteFileUri.MaxQueryLength}", stderr);
        Assert.Contains("valid SQLite file URIs", stderr);
        Assert.DoesNotContain(new string('a', SqliteFileUri.MaxDiagnosticValueLength + 1), stderr);
    }

    [Fact]
    public void Run_LimitZeroStillDetectsDatabaseDrift_Issue2885()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_limit_zero_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_limit_zero_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: true);

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--json", "--limit", "0"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            Assert.Equal("different", root.GetProperty("status").GetString());
            Assert.False(root.GetProperty("identical").GetBoolean());
            Assert.Equal(0, root.GetProperty("files_only_in_left").GetArrayLength());
            Assert.Equal(0, root.GetProperty("files_only_in_right").GetArrayLength());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void ParseArgs_LimitAcceptsMaximum_Issue3162()
    {
        var options = DiffCommandRunner.ParseArgs(["left.db", "right.db", "--limit", $"{DiffCommandRunner.MaxDiffLimit}"]);

        Assert.Equal(DiffCommandRunner.MaxDiffLimit, options.Limit);
        Assert.Null(options.ParseError);
    }

    [Fact]
    public void ParseArgs_LimitRejectsValueAboveMaximum_Issue3162()
    {
        var aboveMaximum = $"{DiffCommandRunner.MaxDiffLimit + 1}";
        var options = DiffCommandRunner.ParseArgs(["left.db", "right.db", "--limit", aboveMaximum]);

        Assert.InRange(options.Limit, 0, DiffCommandRunner.MaxDiffLimit);
        Assert.Equal("--limit must be less than or equal to 10000", options.ParseError);
    }

    [Fact]
    public void Run_DetailedJsonReportsLimitedSymbolRows_Issue2885()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_detailed_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_detailed_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class LeftOnly { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class RightOnly { public void Run() { } }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--json", "--detailed", "--limit", "1"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var symbolsOnlyInLeft = root.GetProperty("symbols_only_in_left").EnumerateArray().ToList();
            var symbolsOnlyInRight = root.GetProperty("symbols_only_in_right").EnumerateArray().ToList();
            Assert.Single(symbolsOnlyInLeft);
            Assert.Single(symbolsOnlyInRight);
            Assert.Contains("LeftOnly", symbolsOnlyInLeft[0].GetString(), StringComparison.Ordinal);
            Assert.Contains("RightOnly", symbolsOnlyInRight[0].GetString(), StringComparison.Ordinal);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetailedJsonUsesSqlOrderForStreamingSymbolDiff_Issue2885()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_order_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_order_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/aa.cs", "csharp", "public class aa { }");
            TestProjectHelper.InsertIndexedFile(leftDb, "src/b.cs", "csharp", "public class b { }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/b.cs", "csharp", "public class b { }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--json", "--detailed", "--limit", "5"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var symbolsOnlyInLeft = root.GetProperty("symbols_only_in_left").EnumerateArray().ToList();
            var symbolsOnlyInRight = root.GetProperty("symbols_only_in_right").EnumerateArray().ToList();
            Assert.Contains(symbolsOnlyInLeft, item => item.GetString()?.Contains("src/aa.cs", StringComparison.Ordinal) == true);
            Assert.DoesNotContain(symbolsOnlyInLeft, item => item.GetString()?.Contains("src/b.cs", StringComparison.Ordinal) == true);
            Assert.Empty(symbolsOnlyInRight);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_ReturnsSuccessForSeparatelyBuiltIdenticalDatabases_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_identical_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_identical_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: false);

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("identical", document.RootElement.GetProperty("status").GetString());
            Assert.True(document.RootElement.GetProperty("identical").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_ReturnsSchemaMismatchExitCodeBeforeDriftExitCode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_schema_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_schema_right");
        try
        {
            var leftDb = SeedDb(leftRoot, includeExtraFile: false);
            var rightDb = SeedDb(rightRoot, includeExtraFile: false);
            SetUserVersion(rightDb, 999);

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.False(document.RootElement.GetProperty("summary").GetProperty("schema_versions_equal").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_ReturnsSchemaMismatchForLegacyDatabaseBeforeReadingMissingTables_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_legacy_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_legacy_right");
        try
        {
            var legacyDb = CreateLegacyDbWithoutGraphTables(leftRoot);
            var currentDb = SeedDb(rightRoot, includeExtraFile: false);

            var (exitCode, output) = RunWithCapturedOut([legacyDb, currentDb, "--summary-only"]);

            Assert.Equal(2, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("schema_mismatch", document.RootElement.GetProperty("status").GetString());
            Assert.False(document.RootElement.GetProperty("summary").GetProperty("schema_versions_equal").GetBoolean());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountSymbolDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_symbol_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_symbol_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class LeftOnly { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class RightOnly { public void Run() { } }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("file_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("symbol_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountReferenceDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_ref_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_ref_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public void Run() { Foo(); } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public void Run() { Bar(); } }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("file_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("reference_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountSignatureDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_signature_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_signature_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public string Convert(int value) => value.ToString(); }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public string Convert(int value) => value.ToString(); }");
            InsertSyntheticMethodSymbol(leftDb, "src/Same.cs", "Convert", "public string Convert(int value)");
            InsertSyntheticMethodSymbol(rightDb, "src/Same.cs", "Convert", "public string Convert(long value)");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("symbol_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountFoldedSymbolDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_fold_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_fold_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public void Run() { } }");
            UpdateFirstSymbolFoldedName(rightDb, "drifted");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("symbol_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountChunkDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_chunk_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_chunk_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public void Run() { } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public void Run() { } }");
            UpdateFirstChunkContent(rightDb, "public class Same { public void Drifted() { } }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("file_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("symbol_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("reference_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_DetectsSameCountReferenceLineDriftWithoutDetailedMode_Issue1724()
    {
        var leftRoot = TestProjectHelper.CreateTempProject("cdidx_diff_refline_left");
        var rightRoot = TestProjectHelper.CreateTempProject("cdidx_diff_refline_right");
        try
        {
            var leftDb = TestProjectHelper.CreateProjectDb(leftRoot);
            var rightDb = TestProjectHelper.CreateProjectDb(rightRoot);
            TestProjectHelper.InsertIndexedFile(leftDb, "src/Same.cs", "csharp", "public class Same { public void Run() { Foo(); } }");
            TestProjectHelper.InsertIndexedFile(rightDb, "src/Same.cs", "csharp", "public class Same { public void Run() { Foo(); } }");
            UpdateFirstReferenceLineContext(rightDb, "public class Same { public void Run() { Drifted(); } }");

            var (exitCode, output) = RunWithCapturedOut([leftDb, rightDb, "--summary-only"]);

            Assert.Equal(1, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("different", document.RootElement.GetProperty("status").GetString());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("file_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("symbol_count_delta").GetInt64());
            Assert.Equal(0, document.RootElement.GetProperty("summary").GetProperty("reference_count_delta").GetInt64());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(leftRoot);
            TestProjectHelper.DeleteDirectory(rightRoot);
        }
    }

    [Fact]
    public void Run_ReturnsUnreadableExitCodeForMissingDatabase_Issue1724()
    {
        var root = TestProjectHelper.CreateTempProject("cdidx_diff_missing");
        try
        {
            var db = SeedDb(root, includeExtraFile: false);
            var missing = Path.Combine(root, "missing.db");

            var (exitCode, output) = RunWithCapturedOut([db, missing, "--summary-only"]);

            Assert.Equal(3, exitCode);
            using var document = JsonDocument.Parse(output);
            Assert.Equal("error", document.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(root);
        }
    }

    private static string SeedDb(string projectRoot, bool includeExtraFile)
    {
        var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
        for (var i = 0; i < 50; i++)
        {
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                $"src/File{i:00}.cs",
                "csharp",
                $$"""
                public static class File{{i:00}}
                {
                    public static void Run() { }
                }
                """);
        }

        if (includeExtraFile)
        {
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/Extra.cs",
                "csharp",
                """
                public static class Extra
                {
                    public static void Run() { }
                }
                """);
        }

        return dbPath;
    }

    private static string CreateLegacyDbWithoutGraphTables(string projectRoot)
    {
        var dbPath = Path.Combine(projectRoot, ".cdidx", "legacy.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA user_version = 1;
            CREATE TABLE files (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                path TEXT NOT NULL UNIQUE,
                lang TEXT,
                size INTEGER,
                lines INTEGER,
                checksum TEXT,
                modified DATETIME,
                indexed_at DATETIME DEFAULT CURRENT_TIMESTAMP
            );
            INSERT INTO files (path, lang, size, lines, checksum, modified)
            VALUES ('src/Legacy.cs', 'csharp', 12, 1, 'legacy', '2026-01-01T00:00:00Z');
            """;
        command.ExecuteNonQuery();
        return dbPath;
    }

    private static void SetUserVersion(string dbPath, int version)
    {
        ExecuteNonQuery(dbPath, $"PRAGMA user_version = {version}");
    }

    private static void InsertSyntheticMethodSymbol(string dbPath, string path, string name, string signature)
    {
        ExecuteNonQuery(
            dbPath,
            """
            INSERT INTO symbols (
                file_id,
                kind,
                sub_kind,
                name,
                line,
                start_line,
                start_column,
                end_line,
                body_start_line,
                body_end_line,
                signature,
                container_kind,
                container_name,
                container_qualified_name,
                family_key,
                visibility,
                return_type,
                is_metadata_target
            )
            VALUES (
                (
                SELECT id
                FROM files
                WHERE path = $path
                LIMIT 1
                ),
                'method',
                'method',
                $name,
                1,
                1,
                42,
                1,
                1,
                1,
                $signature,
                'class',
                'Same',
                'Same',
                'Same.Convert',
                'public',
                'string',
                0
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("$path", path);
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$signature", signature);
            });
    }

    private static void UpdateFirstChunkContent(string dbPath, string content)
    {
        ExecuteNonQuery(
            dbPath,
            """
            UPDATE chunks
            SET content = $content
            WHERE id = (
                SELECT id
                FROM chunks
                ORDER BY id
                LIMIT 1
            )
            """,
            command => command.Parameters.AddWithValue("$content", content));
    }

    private static void UpdateFirstSymbolFoldedName(string dbPath, string nameFolded)
    {
        ExecuteNonQuery(
            dbPath,
            """
            UPDATE symbols
            SET name_folded = $nameFolded
            WHERE id = (
                SELECT id
                FROM symbols
                ORDER BY id
                LIMIT 1
            )
            """,
            command => command.Parameters.AddWithValue("$nameFolded", nameFolded));
    }

    private static void UpdateFirstReferenceLineContext(string dbPath, string context)
    {
        ExecuteNonQuery(
            dbPath,
            """
            UPDATE reference_lines
            SET context = $context
            WHERE id = (
                SELECT id
                FROM reference_lines
                ORDER BY id
                LIMIT 1
            )
            """,
            command => command.Parameters.AddWithValue("$context", context));
    }

    private static void ExecuteNonQuery(string dbPath, string sql, Action<SqliteCommand>? configure = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
        }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        configure?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private (int ExitCode, string Output) RunWithCapturedOut(string[] args)
    {
        var originalOut = Console.Out;
        using var writer = new StringWriter();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                Console.SetOut(writer);
                var exitCode = DiffCommandRunner.Run(args, _jsonOptions);
                return (exitCode, writer.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private (int ExitCode, string StdOut, string StdErr) RunWithCapturedStreams(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        lock (TestConsoleLock.Gate)
        {
            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);
                var exitCode = DiffCommandRunner.Run(args, _jsonOptions);
                return (exitCode, stdout.ToString(), stderr.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }
}
