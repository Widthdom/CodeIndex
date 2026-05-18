using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using Microsoft.Data.Sqlite;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for the stable machine-readable error-code taxonomy emitted by CLI runners (issue #1526).
/// CLI ランナーが出す機械可読エラーコード分類のテスト (issue #1526)。
/// </summary>
[Collection("SQLite pool sensitive")]
public class CommandErrorCodesTests
{
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [Fact]
    public void DbIntegrityCheck_MissingDb_JsonIncludesDbNotFoundCode()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_codes_missing_{Guid.NewGuid():N}.db");

        var (exitCode, json) = RunDbIntegrityCheckCapturingJson(["--integrity-check", "--db", missingDb, "--json"]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal("E001_DB_NOT_FOUND", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public void DbIntegrityCheck_MissingDb_StderrIncludesBracketedCode()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_codes_missing_{Guid.NewGuid():N}.db");

        var (exitCode, _, stderr) = RunDbIntegrityCheckCapturingStreams(["--integrity-check", "--db", missingDb]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Contains("[E001_DB_NOT_FOUND]", stderr);
    }

    [Fact]
    public void DbIntegrityCheck_NoModeFlag_JsonIncludesUsageErrorCode()
    {
        var (exitCode, json) = RunDbIntegrityCheckCapturingJson(["--json"]);

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Equal("E010_USAGE_ERROR", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public void DbIntegrityCheck_CorruptDb_StderrIncludesIntegrityFailedCode()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_codes_corrupt_{Guid.NewGuid():N}.db");
        try
        {
            // Write a file that opens as SQLite but fails `PRAGMA integrity_check` so the
            // human-output corruption branch runs. The same fixture is used in
            // DbCommandRunnerTests; we only assert the new bracketed-code prefix.
            // SQLite として開けるが integrity_check で破損が出るファイルを作る fixture。
            var header = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
            var bytes = new byte[4096];
            Array.Copy(header, bytes, header.Length);
            for (var i = header.Length; i < bytes.Length; i++)
                bytes[i] = 0xFF;
            File.WriteAllBytes(dbPath, bytes);

            var (exitCode, _, stderr) = RunDbIntegrityCheckCapturingStreams(["--integrity-check", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            // Either the integrity-failed branch (E005) or the open-failure branch (E008) ran;
            // both must carry a bracketed code so scripts can classify the failure without
            // substring-matching the prose.
            Assert.True(
                stderr.Contains("[E005_DB_INTEGRITY_FAILED]") || stderr.Contains("[E008_DB_ERROR]"),
                $"expected bracketed integrity code in stderr, got: {stderr}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
                File.Delete(dbPath);
        }
    }

    [Fact]
    public void Index_MissingProjectDirectory_JsonIncludesDirectoryNotFoundCode()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), $"cdidx_codes_dir_{Guid.NewGuid():N}");

        var (exitCode, json) = RunIndexCapturingJson([missingDir, "--json"]);

        Assert.Equal(CommandExitCodes.NotFound, exitCode);
        Assert.Equal("E011_DIRECTORY_NOT_FOUND", json.GetProperty("error_code").GetString());
    }

    [Fact]
    public void Search_MissingDb_StderrIncludesDbNotFoundCode()
    {
        var missingDb = Path.Combine(Path.GetTempPath(), $"cdidx_codes_search_{Guid.NewGuid():N}.db");

        var (exitCode, _, stderr) = RunSearchCapturingStreams(["foo", "--db", missingDb]);

        Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
        Assert.Contains("[E001_DB_NOT_FOUND]", stderr);
    }

    [Fact]
    public void Symbols_InvalidKind_ReturnsInvalidArgumentExitCode()
    {
        var (exitCode, _, stderr) = CaptureStreams(() => QueryCommandRunner.RunSymbols(["--kind", "invalid_kind"], _jsonOptions));

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Contains("invalid --kind", stderr);
    }

    [Theory]
    [InlineData("hook")]
    [InlineData("import")]
    [InlineData("property")]
    public void Symbols_KnownExtractorKinds_DoNotReturnInvalidArgument(string kind)
    {
        var (exitCode, _, stderr) = CaptureStreams(() => QueryCommandRunner.RunSymbols(["--kind", kind], _jsonOptions));

        Assert.NotEqual(CommandExitCodes.InvalidArgument, exitCode);
        Assert.DoesNotContain("invalid --kind", stderr);
    }

    [Theory]
    [InlineData("references")]
    [InlineData("callers")]
    [InlineData("callees")]
    public void KindFilteredCommands_InvalidKind_ReturnInvalidArgumentExitCode(string command)
    {
        var args = new[] { "Foo", "--kind", "invalid_kind" };

        var (exitCode, _, stderr) = CaptureStreams(() => command switch
        {
            "references" => QueryCommandRunner.RunReferences(args, _jsonOptions),
            "callers" => QueryCommandRunner.RunCallers(args, _jsonOptions),
            "callees" => QueryCommandRunner.RunCallees(args, _jsonOptions),
            _ => throw new ArgumentOutOfRangeException(nameof(command), command, null),
        });

        Assert.Equal(CommandExitCodes.InvalidArgument, exitCode);
        Assert.Contains("invalid --kind", stderr);
    }

    private (int ExitCode, string StdOut, string StdErr) RunDbIntegrityCheckCapturingStreams(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            try
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);
                var exitCode = DbCommandRunner.RunIntegrityCheck(args, _jsonOptions);
                return (exitCode, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    private (int ExitCode, JsonElement Json) RunDbIntegrityCheckCapturingJson(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                var exitCode = DbCommandRunner.RunIntegrityCheck(args, _jsonOptions);
                using var document = JsonDocument.Parse(writer.ToString());
                return (exitCode, document.RootElement.Clone());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private (int ExitCode, JsonElement Json) RunIndexCapturingJson(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            using var writer = new StringWriter();
            try
            {
                Console.SetOut(writer);
                var exitCode = IndexCommandRunner.Run(args, _jsonOptions);
                using var document = JsonDocument.Parse(writer.ToString());
                return (exitCode, document.RootElement.Clone());
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }
    }

    private (int ExitCode, string StdOut, string StdErr) RunSearchCapturingStreams(string[] args)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            try
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);
                var exitCode = QueryCommandRunner.RunSearch(args, _jsonOptions);
                return (exitCode, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }

    private static (int ExitCode, string StdOut, string StdErr) CaptureStreams(Func<int> run)
    {
        lock (TestConsoleLock.Gate)
        {
            var originalOut = Console.Out;
            var originalErr = Console.Error;
            using var outWriter = new StringWriter();
            using var errWriter = new StringWriter();
            try
            {
                Console.SetOut(outWriter);
                Console.SetError(errWriter);
                var exitCode = run();
                return (exitCode, outWriter.ToString(), errWriter.ToString());
            }
            finally
            {
                Console.SetOut(originalOut);
                Console.SetError(originalErr);
            }
        }
    }
}
