using System.Text.Json;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

[Collection("Console sensitive")]
public sealed class QueryCommandRunnerIssue4835Tests
{
    private readonly JsonSerializerOptions _jsonOptions = ProgramRunner.CreateDefaultJsonOptions();

    [Theory]
    [InlineData("symbols")]
    [InlineData("files")]
    public void DiscoveryRows_AgreeAcrossArrayNdjsonAndProjectedEnvelope_Issue4835(string command)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"discovery_json_shapes_4835_{command}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            for (var index = 1; index <= 3; index++)
            {
                TestProjectHelper.InsertIndexedFile(
                    dbPath,
                    $"src/SharedType{index}.cs",
                    "csharp",
                    "public sealed class SharedType { }\n");
            }

            var baseArgs = BuildQueryArgs(command, dbPath, "SharedType");
            var (ndjsonExitCode, ndjsonStdout, ndjsonStderr) = CaptureConsole(() =>
                ProgramRunner.Run([.. baseArgs, "--json"], _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, ndjsonExitCode);
            Assert.Equal(string.Empty, ndjsonStderr);
            var ndjsonRecords = ParseNdjson(ndjsonStdout);
            var terminal = ndjsonRecords[^1];
            var ndjsonRows = ndjsonRecords[..^1];
            Assert.Equal(2, ndjsonRows.Length);
            Assert.True(terminal.GetProperty("terminal_record").GetBoolean());
            Assert.Equal(2, terminal.GetProperty("count").GetInt32());
            Assert.Equal(3, terminal.GetProperty("total_count").GetInt32());
            Assert.True(terminal.GetProperty("truncated").GetBoolean());
            Assert.True(terminal.GetProperty("has_more").GetBoolean());

            var (arrayExitCode, arrayStdout, arrayStderr) = CaptureConsole(() =>
                ProgramRunner.Run([.. baseArgs, "--json=array"], _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, arrayExitCode);
            Assert.Equal(string.Empty, arrayStderr);
            using var arrayDocument = JsonDocument.Parse(arrayStdout);
            var arrayRows = arrayDocument.RootElement.EnumerateArray().Select(row => row.Clone()).ToArray();
            Assert.Equal(ndjsonRows.Select(row => row.GetRawText()), arrayRows.Select(row => row.GetRawText()));
            bool? expectedExactIndexAvailable = null;
            if (command == "symbols")
            {
                expectedExactIndexAvailable = ndjsonRows[0].GetProperty("exact_index_available").GetBoolean();
                Assert.All(
                    ndjsonRows,
                    row => Assert.Equal(expectedExactIndexAvailable, row.GetProperty("exact_index_available").GetBoolean()));
                Assert.All(
                    arrayRows,
                    row => Assert.Equal(expectedExactIndexAvailable, row.GetProperty("exact_index_available").GetBoolean()));
            }

            var (envelopeExitCode, envelopeStdout, envelopeStderr) = CaptureConsole(() =>
                ProgramRunner.Run([.. baseArgs, "--json-envelope"], _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, envelopeExitCode);
            Assert.Equal(string.Empty, envelopeStderr);
            using var envelopeDocument = JsonDocument.Parse(envelopeStdout);
            var envelopeRows = envelopeDocument.RootElement.GetProperty("results")
                .EnumerateArray()
                .Select(row => row.Clone())
                .ToArray();
            Assert.Equal(ndjsonRows.Select(row => row.GetRawText()), envelopeRows.Select(row => row.GetRawText()));
            var envelopeMetadata = envelopeDocument.RootElement.GetProperty("metadata");
            Assert.Equal(terminal.GetProperty("count").GetInt32(), envelopeMetadata.GetProperty("result_count").GetInt32());
            Assert.Equal(
                terminal.GetProperty("total_count").GetInt32(),
                envelopeMetadata.GetProperty("stream_terminal").GetProperty("total_count").GetInt32());
            Assert.Equal(
                terminal.GetProperty("truncated").GetBoolean(),
                envelopeMetadata.GetProperty("stream_terminal").GetProperty("truncated").GetBoolean());

            var fields = command == "symbols" ? "name,path" : "path,lang";
            var projectedArgs = baseArgs.Concat(["--json-envelope", "--fields", fields]).ToArray();
            var (projectedExitCode, projectedStdout, projectedStderr) = CaptureConsole(() =>
                ProgramRunner.Run(projectedArgs, _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, projectedExitCode);
            Assert.Equal(string.Empty, projectedStderr);
            using var projectedDocument = JsonDocument.Parse(projectedStdout);
            var projectedMetadata = projectedDocument.RootElement.GetProperty("metadata");
            AssertMetadataMatchesTerminal(projectedMetadata, terminal, command);
            var expectedFields = fields.Split(',');
            var projectedRows = projectedDocument.RootElement.GetProperty("results").EnumerateArray().ToArray();
            Assert.Equal(2, projectedRows.Length);
            Assert.All(
                projectedRows,
                row => Assert.Equal(expectedFields, row.EnumerateObject().Select(property => property.Name)));
            AssertResponseContextContainsNoRow(projectedMetadata);
            if (command == "symbols")
            {
                Assert.Equal(
                    expectedExactIndexAvailable,
                    projectedMetadata.GetProperty("response_context").GetProperty("exact_index_available").GetBoolean());
            }

            var cursor = Assert.IsType<string>(projectedMetadata.GetProperty("next_cursor").GetString());
            var (nextExitCode, nextStdout, nextStderr) = CaptureConsole(() =>
                ProgramRunner.Run([.. projectedArgs, "--cursor", cursor], _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, nextExitCode);
            Assert.Equal(string.Empty, nextStderr);
            using var nextDocument = JsonDocument.Parse(nextStdout);
            var nextMetadata = nextDocument.RootElement.GetProperty("metadata");
            Assert.Single(nextDocument.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal(1, nextMetadata.GetProperty("returned_count").GetInt32());
            Assert.Equal(3, nextMetadata.GetProperty("total_count").GetInt32());
            Assert.Equal(2, nextMetadata.GetProperty("cursor_offset").GetInt32());
            Assert.False(nextMetadata.GetProperty("has_more").GetBoolean());
            Assert.Equal(JsonValueKind.Null, nextMetadata.GetProperty("next_cursor").ValueKind);
            AssertResponseContextContainsNoRow(nextMetadata);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    [Theory]
    [InlineData("symbols")]
    [InlineData("files")]
    public void DiscoveryRows_EmptyShapesRemainGenuinelyEmpty_Issue4835(string command)
    {
        var projectRoot = TestProjectHelper.CreateTempProject($"discovery_json_empty_4835_{command}");
        try
        {
            var dbPath = TestProjectHelper.CreateProjectDb(projectRoot);
            TestProjectHelper.InsertIndexedFile(
                dbPath,
                "src/SharedType.cs",
                "csharp",
                "public sealed class SharedType { }\n");

            var baseArgs = BuildQueryArgs(command, dbPath, "Missing4835");
            var (ndjsonExitCode, ndjsonStdout, ndjsonStderr) = CaptureConsole(() =>
                ProgramRunner.Run([.. baseArgs, "--json"], _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, ndjsonExitCode);
            Assert.Equal(string.Empty, ndjsonStdout);
            Assert.Equal(string.Empty, ndjsonStderr);

            var (arrayExitCode, arrayStdout, arrayStderr) = CaptureConsole(() =>
                ProgramRunner.Run([.. baseArgs, "--json=array"], _jsonOptions, "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, arrayExitCode);
            Assert.Equal(string.Empty, arrayStderr);
            using (var arrayDocument = JsonDocument.Parse(arrayStdout))
                Assert.Empty(arrayDocument.RootElement.EnumerateArray());

            var fields = command == "symbols" ? "name,path" : "path,lang";
            var (envelopeExitCode, envelopeStdout, envelopeStderr) = CaptureConsole(() =>
                ProgramRunner.Run(
                    [.. baseArgs, "--json-envelope", "--fields", fields],
                    _jsonOptions,
                    "1.0.0-test"));

            Assert.Equal(CommandExitCodes.Success, envelopeExitCode);
            Assert.Equal(string.Empty, envelopeStderr);
            using var envelopeDocument = JsonDocument.Parse(envelopeStdout);
            var metadata = envelopeDocument.RootElement.GetProperty("metadata");
            Assert.Empty(envelopeDocument.RootElement.GetProperty("results").EnumerateArray());
            Assert.Equal(0, metadata.GetProperty("returned_count").GetInt32());
            Assert.Equal(0, metadata.GetProperty("total_count").GetInt32());
            Assert.Equal(0, metadata.GetProperty("omitted_count").GetInt32());
            Assert.False(metadata.GetProperty("truncated").GetBoolean());
            Assert.False(metadata.GetProperty("has_more").GetBoolean());
            Assert.Equal(JsonValueKind.Null, metadata.GetProperty("next_cursor").ValueKind);
            AssertResponseContextContainsNoRow(metadata);
        }
        finally
        {
            TestProjectHelper.DeleteDirectory(projectRoot);
        }
    }

    private static string[] BuildQueryArgs(string command, string dbPath, string query)
        => command == "symbols"
            ? [command, query, "--db", dbPath, "--exact-name", "--kind", "class", "--limit", "2"]
            : [command, query, "--db", dbPath, "--limit", "2"];

    private static void AssertMetadataMatchesTerminal(JsonElement metadata, JsonElement terminal, string command)
    {
        Assert.Equal(command, metadata.GetProperty("primary_collection").GetString());
        Assert.Equal(terminal.GetProperty("count").GetInt32(), metadata.GetProperty("returned_count").GetInt32());
        Assert.Equal(terminal.GetProperty("total_count").GetInt32(), metadata.GetProperty("total_count").GetInt32());
        Assert.Equal(terminal.GetProperty("truncated").GetBoolean(), metadata.GetProperty("truncated").GetBoolean());
        Assert.Equal(terminal.GetProperty("has_more").GetBoolean(), metadata.GetProperty("has_more").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(metadata.GetProperty("next_cursor").GetString()));
    }

    private static void AssertResponseContextContainsNoRow(JsonElement metadata)
    {
        if (!metadata.TryGetProperty("response_context", out var responseContext))
            return;
        Assert.False(responseContext.TryGetProperty("name", out _));
        Assert.False(responseContext.TryGetProperty("path", out _));
        Assert.False(responseContext.TryGetProperty("symbol_id", out _));
        Assert.False(responseContext.TryGetProperty("checksum", out _));
    }

    private static JsonElement[] ParseNdjson(string stdout)
        => stdout.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToArray();

    private static (int ExitCode, string Stdout, string Stderr) CaptureConsole(Func<int> action)
        => ConsoleCapture.Capture(action);
}
