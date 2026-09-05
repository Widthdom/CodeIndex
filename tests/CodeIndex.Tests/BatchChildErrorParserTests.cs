using System.Text;
using System.Text.Json.Nodes;
using CodeIndex.Cli;

namespace CodeIndex.Tests;

public class BatchChildErrorParserTests
{
    private const string ErrorJson = """
        {"status":"error","error_code":"E028_RESPONSE_BUDGET_TOO_SMALL","category":"response_budget",
         "message":"Increase the output budget.","hint":"Retry with more bytes.","command":"search","exit_code":1,
         "requested_bytes":512,"effective_bytes":512,"minimum_required_bytes":2048,
         "minimum_required_bytes_known":true,"minimum_required_bytes_unavailable_reason":null,
         "minimum_required_bytes_uncertain":false,"minimum_required_bytes_uncertainty_reason":null,
         "retry":{"action":"increase_max_json_bytes","option":"--max-json-bytes","recommended_bytes":3072,
                  "maximum_effective_bytes":null,"command":"search"}}
        """;

    [Fact]
    public void Parse_PreservesAllowlistedFieldsAndSanitizesUntrustedText()
    {
        var source = JsonNode.Parse(ErrorJson)!.AsObject();
        source["message"] = "Failed\u001b\n at /private/secret/file.cs password=hunter2 Bearer abcdefghijklmnopqrstuvwxyz";
        source["hint"] = @"Retry C:\private\secret.cs --token hidden-value";
        source["path"] = "/private/secret/file.cs";
        source["unknown"] = new JsonObject { ["password"] = "unlisted-secret" };
        source["scope"] = "batch";
        source["retry"]!["unknown"] = "unlisted-secret";
        var parsed = Assert.IsType<JsonObject>(BatchChildErrorParser.Parse(source.ToJsonString(), "search", 1));
        Assert.Equal("command", parsed["scope"]!.GetValue<string>());
        Assert.Equal(2048, parsed["minimum_required_bytes"]!.GetValue<long>());
        Assert.Equal(3072, parsed["retry"]!["recommended_bytes"]!.GetValue<long>());
        Assert.False(parsed.ContainsKey("path"));
        Assert.False(parsed.ContainsKey("unknown"));
        Assert.False(parsed["retry"]!.AsObject().ContainsKey("unknown"));
        var serialized = parsed.ToJsonString();
        foreach (var secret in new[] { "hunter2", "abcdefghijklmnopqrstuvwxyz", "hidden-value", "private", "unlisted-secret" })
            Assert.DoesNotContain(secret, serialized);
        Assert.DoesNotContain(parsed["message"]!.GetValue<string>(), char.IsControl);

        source["message"] = new string('x', 3000);
        parsed = Assert.IsType<JsonObject>(BatchChildErrorParser.Parse(source.ToJsonString(), "search", 1));
        Assert.True(parsed["message"]!.GetValue<string>().Length <= BatchChildErrorParser.MaxTextChars);

        source = JsonNode.Parse(ErrorJson)!.AsObject();
        source.Remove("status");
        var envelope = new JsonObject
        {
            ["metadata"] = new JsonObject { ["command"] = "search", ["exit_code"] = 1, ["error"] = source },
            ["results"] = new JsonArray(),
        };
        var parsedEnvelope = BatchChildErrorParser.Parse(envelope.ToJsonString(), "search", 1);
        Assert.True(parsedEnvelope is not null, envelope.ToJsonString());
        parsed = parsedEnvelope;
        Assert.Equal("response_budget", parsed["category"]!.GetValue<string>());

        source["minimum_required_bytes"] = null;
        source["minimum_required_bytes_known"] = false;
        source["minimum_required_bytes_unavailable_reason"] = "normal_payload_not_materialized";
        source["retry"]!["action"] = "reduce_response_size";
        source["retry"]!["option"] = null;
        source["retry"]!["recommended_bytes"] = null;
        source["retry"]!["maximum_effective_bytes"] = 4096;
        parsed = Assert.IsType<JsonObject>(BatchChildErrorParser.Parse(envelope.ToJsonString(), "search", 1));
        Assert.Null(parsed["minimum_required_bytes"]);
        Assert.False(parsed["minimum_required_bytes_known"]!.GetValue<bool>());
        Assert.Equal("normal_payload_not_materialized", parsed["minimum_required_bytes_unavailable_reason"]!.GetValue<string>());
        Assert.Equal(4096, parsed["retry"]!["maximum_effective_bytes"]!.GetValue<long>());
    }

    [Fact]
    public void Parse_RejectsMalformedMismatchedAndOverBudgetOutput()
    {
        foreach (var invalid in new[]
                 {
                     "", "Error: password=secret", "{", "[]", "null", "42",
                     ErrorJson + "\n" + ErrorJson,
                     ErrorJson.Replace("\"status\":\"error\"", "\"status\":\"ok\""),
                     ErrorJson.Replace("\"category\":\"response_budget\"", "\"category\":{}"),
                     ErrorJson.Replace("\"exit_code\":1", "\"exit_code\":2"),
                     ErrorJson.Replace("\"command\":\"search\"", "\"command\":\"status\""),
                     ErrorJson.Replace("\"requested_bytes\":512", "\"requested_bytes\":-1"),
                     ErrorJson.Replace("\"recommended_bytes\":3072", "\"recommended_bytes\":1e100"),
                     ErrorJson.Replace("\"minimum_required_bytes_known\":true", "\"minimum_required_bytes_known\":\"true\""),
                     ErrorJson.Replace("\"message\":", "\"message\":\"duplicate\",\"message\":"),
                     ErrorJson.Replace("\"action\":", "\"action\":\"duplicate\",\"action\":"),
                     ErrorJson.Replace("\"status\":\"error\"", "\"status\":\"error\",\"unknown\":"
                         + new string('[', BatchChildErrorParser.MaxDepth) + "0" + new string(']', BatchChildErrorParser.MaxDepth)),
                     """{"metadata":{"exit_code":"1","error":{}}}""",
                 })
            Assert.Null(BatchChildErrorParser.Parse(invalid, "search", 1));

        Assert.Null(BatchChildErrorParser.Parse(ErrorJson, "search", 0));
        var exactFit = ErrorJson + new string(' ', BatchChildErrorParser.MaxUtf8Bytes - Encoding.UTF8.GetByteCount(ErrorJson));
        Assert.NotNull(BatchChildErrorParser.Parse(exactFit, "search", 1));
        Assert.Null(BatchChildErrorParser.Parse(exactFit + " ", "search", 1));
        var unicode = JsonNode.Parse(ErrorJson)!.AsObject();
        unicode["ignored"] = new string('日', BatchChildErrorParser.MaxUtf8Bytes / 2);
        var unicodeJson = unicode.ToJsonString().Replace("\\u65E5", "日", StringComparison.OrdinalIgnoreCase);
        Assert.True(unicodeJson.Length < BatchChildErrorParser.MaxUtf8Bytes);
        Assert.Null(BatchChildErrorParser.Parse(unicodeJson, "search", 1));
    }
}
