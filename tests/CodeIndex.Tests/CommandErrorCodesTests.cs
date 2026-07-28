using System.Text.Json;
using CodeIndex.Cli;
using CodeIndex.Database;
using CodeIndex.Diagnostics;

namespace CodeIndex.Tests;

/// <summary>
/// Tests for the stable machine-readable error-code taxonomy emitted by CLI runners (issue #1526).
/// CLI ランナーが出す機械可読エラーコード分類のテスト (issue #1526)。
/// </summary>
[Collection("Console sensitive")]
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
    public void DbIntegrityCheck_InvalidDatabase_StderrIncludesNotDatabaseCode_Issue4856()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"cdidx_codes_corrupt_{Guid.NewGuid():N}.db");
        try
        {
            // Keep a valid SQLite magic header but an invalid page layout. SQLite reports
            // primary result code 26, which the maintenance classifier maps without
            // inspecting exception wording.
            // SQLite magic header は保ちつつ page layout を不正にし、primary code 26 の分類を固定する。
            var header = System.Text.Encoding.ASCII.GetBytes("SQLite format 3\0");
            var bytes = new byte[4096];
            Array.Copy(header, bytes, header.Length);
            for (var i = header.Length; i < bytes.Length; i++)
                bytes[i] = 0xFF;
            File.WriteAllBytes(dbPath, bytes);

            var (exitCode, _, stderr) = RunDbIntegrityCheckCapturingStreams(["--integrity-check", "--db", dbPath]);

            Assert.Equal(CommandExitCodes.DatabaseError, exitCode);
            Assert.Contains("[E027_DB_NOT_DATABASE]", stderr);
        }
        finally
        {
            TestProjectHelper.DeleteFile(dbPath);
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

        Assert.Equal(CommandExitCodes.UsageError, exitCode);
        Assert.Contains("[E001_DB_NOT_FOUND]", stderr);
    }

    [Fact]
    public void Find_RegexTimeout_StderrIncludesBracketedCode_Issue3559()
    {
        var timeout = new System.Text.RegularExpressions.RegexMatchTimeoutException(
            "aaaaaaaaaaaaaaaa!",
            "^(a+)+$",
            TimeSpan.FromMilliseconds(25));

        var (exitCode, _, stderr) = CaptureStreams(() =>
            QueryCommandRunner.WriteFindRegexTimeoutError(timeout, _jsonOptions, json: false));

        Assert.Equal(CommandExitCodes.RuntimeError, exitCode);
        Assert.Contains("[E014_REGEX_MATCH_TIMEOUT]", stderr);
        Assert.Contains("regular expression timed out", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void Find_RegexTimeout_JsonIncludesRegexTimeoutCode_Issue3559()
    {
        var timeout = new System.Text.RegularExpressions.RegexMatchTimeoutException(
            "aaaaaaaaaaaaaaaa!",
            "^(a+)+$",
            TimeSpan.FromMilliseconds(25));

        var (exitCode, json) = RunFindRegexTimeoutCapturingJson(timeout);

        Assert.Equal(CommandExitCodes.RuntimeError, exitCode);
        Assert.Equal("error", json.GetProperty("status").GetString());
        Assert.Equal("E014_REGEX_MATCH_TIMEOUT", json.GetProperty("error_code").GetString());
        Assert.Equal(RegexTimeoutPolicy.RegexTimeoutCategory, json.GetProperty("category").GetString());
    }

    [Theory]
    [InlineData(CommandErrorCodes.QueryNotFound)]
    [InlineData(CommandErrorCodes.FileNotFound)]
    [InlineData(CommandErrorCodes.LineOutOfRange)]
    public void QueryLookupCodes_AppearInHumanAndJsonErrors_Issue4564(string errorCode)
    {
        var (humanExitCode, _, stderr) = CaptureStreams(() => CommandErrorWriter.WriteJsonOrHuman(
            false,
            _jsonOptions,
            "lookup failed",
            CommandExitCodes.NotFound,
            errorCode: errorCode));

        Assert.Equal(CommandExitCodes.NotFound, humanExitCode);
        Assert.Contains($"[{errorCode}]", stderr);

        using var capture = ConsoleCapture.Start(captureOut: true);
        var jsonExitCode = CommandErrorWriter.WriteJsonOrHuman(
            true,
            _jsonOptions,
            "lookup failed",
            CommandExitCodes.NotFound,
            errorCode: errorCode);
        using var document = JsonDocument.Parse(capture.Out!.ToString()!);

        Assert.Equal(CommandExitCodes.NotFound, jsonExitCode);
        Assert.Equal(errorCode, document.RootElement.GetProperty("error_code").GetString());
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
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var exitCode = DbCommandRunner.RunIntegrityCheck(args, _jsonOptions);
        return (exitCode, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }

    private (int ExitCode, JsonElement Json) RunDbIntegrityCheckCapturingJson(string[] args)
    {
        using var capture = ConsoleCapture.Start(captureOut: true);
        var exitCode = DbCommandRunner.RunIntegrityCheck(args, _jsonOptions);
        using var document = JsonDocument.Parse(capture.Out!.ToString()!);
        return (exitCode, document.RootElement.Clone());
    }

    private (int ExitCode, JsonElement Json) RunIndexCapturingJson(string[] args)
    {
        using var capture = ConsoleCapture.Start(captureOut: true);
        var exitCode = IndexCommandRunner.Run(args, _jsonOptions);
        using var document = JsonDocument.Parse(capture.Out!.ToString()!);
        return (exitCode, document.RootElement.Clone());
    }

    private (int ExitCode, string StdOut, string StdErr) RunSearchCapturingStreams(string[] args)
    {
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var exitCode = QueryCommandRunner.RunSearch(args, _jsonOptions);
        return (exitCode, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }

    private (int ExitCode, JsonElement Json) RunFindRegexTimeoutCapturingJson(System.Text.RegularExpressions.RegexMatchTimeoutException timeout)
    {
        using var capture = ConsoleCapture.Start(captureOut: true);
        var exitCode = QueryCommandRunner.WriteFindRegexTimeoutError(timeout, _jsonOptions, json: true);
        using var document = JsonDocument.Parse(capture.Out!.ToString()!);
        return (exitCode, document.RootElement.Clone());
    }

    private static (int ExitCode, string StdOut, string StdErr) CaptureStreams(Func<int> run)
    {
        using var capture = ConsoleCapture.Start(captureOut: true, captureError: true);
        var exitCode = run();
        return (exitCode, capture.Out!.ToString()!, capture.Error!.ToString()!);
    }
}
