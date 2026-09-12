using System.Text.Json.Nodes;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class BatchChildPartialResultParserTests
{
    private const string Terminal = """{"terminal_record":true,"partial_result":true,"returned_count":1,"scan_complete":false,"authoritative_rows":false,"next_cursor":"opaque-cursor"}""";

    [Fact]
    public void Parse_PreservesValidatedPartialContractsAndRejectsUnrelatedOutput_Issue5344()
    {
        const string row = """{"path":"src/file.cs","line":1,"column":1,"content":"alpha"}""";
        var stream = row + "\n" + Terminal + "\n";
        var parsed = Assert.IsType<JsonArray>(BatchChildPartialResultParser.Parse(stream, "find", ndjson: true));
        Assert.Equal(2, parsed.Count);
        Assert.Equal("opaque-cursor", parsed[^1]!["next_cursor"]!.GetValue<string>());
        var zeroTerminal = Terminal.Replace("\"returned_count\":1", "\"returned_count\":0", StringComparison.Ordinal);
        Assert.NotNull(BatchChildPartialResultParser.Parse(zeroTerminal, "find", ndjson: true));
        Assert.NotNull(BatchChildPartialResultParser.Parse("{\"count\":0,\"results\":[]}\n" + zeroTerminal, "search", ndjson: true));
        var count = Terminal.Replace("\"returned_count\":1", "\"count\":1,\"returned_count\":1", StringComparison.Ordinal);
        Assert.NotNull(BatchChildPartialResultParser.Parse(count, "find", ndjson: false));
        var envelope = "{\"metadata\":{\"command\":\"find\",\"exit_code\":11,\"result_count\":1,\"stream_terminal\":"
            + Terminal + "},\"results\":[" + row + "]}";
        Assert.NotNull(BatchChildPartialResultParser.Parse(envelope, "find", ndjson: false));
        var trimmedEnvelope = envelope.Replace(row, "", StringComparison.Ordinal)
            .Replace("\"result_count\":1", "\"result_count\":0,\"returned_count\":0,\"truncated\":true", StringComparison.Ordinal);
        Assert.NotNull(BatchChildPartialResultParser.Parse(trimmedEnvelope, "find", ndjson: false));
        Assert.NotNull(BatchChildPartialResultParser.Parse(
            stream.Replace("\"partial_result\":true", "\"interrupted\":true", StringComparison.Ordinal), "search", ndjson: true));

        foreach (var invalid in new[]
        {
            "", "plain text", "null", "[]", "{}", row, Terminal, Terminal[..^1],
            stream.Replace("\"returned_count\":1", "\"returned_count\":2", StringComparison.Ordinal),
            "{\"unrelated\":true}\n" + Terminal,
            "{\"count\":0,\"results\":[]}\n" + stream,
            row + "\n{", stream + row, Terminal + "\n" + Terminal,
            "42\n" + Terminal, "null\n" + Terminal, "[]\n" + Terminal,
            row + "\n" + Terminal.Replace("true", "false", StringComparison.Ordinal),
            row + "\n" + Terminal.Replace("\"partial_result\":true", "\"partial_result\":\"true\"", StringComparison.Ordinal),
            Terminal.Replace("\"returned_count\":1", "\"returned_count\":-1", StringComparison.Ordinal),
            Terminal.Replace("\"returned_count\":1,", "", StringComparison.Ordinal),
            row + "\n" + Terminal.Replace("\"terminal_record\":true", "\"terminal_record\":true,\"terminal_record\":false", StringComparison.Ordinal),
            """{"status":"error","error_code":"E022_INDEX_PARTIAL"}""" + "\n" + Terminal,
            """{"command":"definition","exit_code":11}""" + "\n" + Terminal,
            """{"command":"find","exit_code":1}""" + "\n" + Terminal,
            """{"text":"\uD800"}""" + "\n" + Terminal,
            """{"\uDC00":1}""" + "\n" + Terminal,
            "{\"nested\":" + new string('[', QueryCommandRunner.BatchMaxJsonDepth) + "0"
                + new string(']', QueryCommandRunner.BatchMaxJsonDepth) + "}\n" + Terminal,
        })
            Assert.Null(BatchChildPartialResultParser.Parse(invalid, "find", ndjson: true));

        foreach (var invalid in new[]
        {
            stream, Terminal, "{}", "[]", "{\"partial_result\":true}",
            count.Replace("\"count\":1", "\"count\":2", StringComparison.Ordinal),
            envelope.Replace(row, "", StringComparison.Ordinal),
            envelope.Replace("\"result_count\":1", "\"result_count\":2", StringComparison.Ordinal),
            envelope.Replace("\"returned_count\":1", "\"returned_count\":2", StringComparison.Ordinal),
            envelope.Replace("\"exit_code\":11", "\"exit_code\":0", StringComparison.Ordinal),
            envelope.Replace("\"command\":\"find\"", "\"command\":\"search\"", StringComparison.Ordinal),
            envelope.Replace("\"exit_code\":11,", "", StringComparison.Ordinal),
            envelope.Replace("\"results\":[" + row + "]", "\"results\":null", StringComparison.Ordinal),
            envelope.Replace("\"exit_code\":11", "\"exit_code\":11,\"error\":{}", StringComparison.Ordinal),
            envelope.Replace("\"terminal_record\":true", "\"command\":\"search\",\"terminal_record\":true", StringComparison.Ordinal),
            envelope.Replace("\"stream_terminal\"", "\"unrelated\"", StringComparison.Ordinal),
        })
            Assert.Null(BatchChildPartialResultParser.Parse(invalid, "find", ndjson: false));

        var exactFit = count + new string(' ', JsonEnvelopeWrapper.MaxCapturedOutputChars - count.Length);
        Assert.NotNull(BatchChildPartialResultParser.Parse(exactFit, "find", ndjson: false));
        Assert.Null(BatchChildPartialResultParser.Parse(exactFit + " ", "find", ndjson: false));
    }
}
