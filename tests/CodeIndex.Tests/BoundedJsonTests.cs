using System.Text;
using System.Text.Json;
using CodeIndex.Diagnostics;

namespace CodeIndex.Tests;

public class BoundedJsonTests
{
    [Fact]
    public void ParseDocument_RejectsPayloadAboveUtf8Limit_Issue4127()
    {
        var json = """{"value":"あ"}""";

        Assert.True(Encoding.UTF8.GetByteCount(json) > json.Length);
        var ex = Assert.Throws<InvalidDataException>(
            () => BoundedJson.ParseDocument(json, json.Length, maxDepth: 8));

        Assert.Contains("JSON payload exceeds", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseDocument_RejectsPayloadAboveDepthLimit_Issue4127()
    {
        var ex = Assert.ThrowsAny<JsonException>(
            () => BoundedJson.ParseDocument("[[1]]", maxUtf8Bytes: 64, maxDepth: 1));

        Assert.Contains("maximum configured depth", BoundedJson.FormatExceptionDetail(ex), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseNode_AllowsConfiguredJsonRelaxations_Issue4127()
    {
        var node = BoundedJson.ParseNode(
            """
            {
              "value": 1, // comment
            }
            """,
            maxUtf8Bytes: 128,
            maxDepth: 8,
            JsonCommentHandling.Skip,
            allowTrailingCommas: true);

        Assert.Equal(1, node?["value"]?.GetValue<int>());
    }

    [Fact]
    public void Deserialize_RejectsPayloadAboveUtf8Limit_Issue4127()
    {
        var json = """{"value":"あ"}""";
        var options = new JsonSerializerOptions { MaxDepth = 8 };

        var ex = Assert.Throws<InvalidDataException>(
            () => BoundedJson.Deserialize<Dictionary<string, string>>(json, json.Length, options));

        Assert.Contains("JSON payload exceeds", ex.Message, StringComparison.Ordinal);
    }
}
