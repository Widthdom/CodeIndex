using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodeIndex.Mcp;

namespace CodeIndex.Tests;

public sealed class McpRequestIdTelemetryTests
{
    [Fact]
    public void Create_CredentialShapedStringIsOpaqueStableAndTyped_Issue4551()
    {
        const string requestId = "github_pat_4551_abcdefghijklmnopqrstuvwxyz012345";
        var provider = CreateProvider();

        var first = provider.Create(JsonValue.Create(requestId));
        var second = provider.Create(JsonValue.Create(requestId));

        Assert.Equal(first, second);
        Assert.Equal("string", first.Type);
        Assert.Equal(requestId.Length, first.Length);
        Assert.StartsWith(McpRequestIdTelemetry.TokenPrefix, first.Token, StringComparison.Ordinal);
        Assert.Equal(McpRequestIdTelemetry.TokenLength, first.Token.Length);
        Assert.DoesNotContain(requestId, first.Token, StringComparison.Ordinal);
        Assert.All(first.Token[McpRequestIdTelemetry.TokenPrefix.Length..], character =>
            Assert.True(char.IsAsciiHexDigit(character) && !char.IsUpper(character)));
    }

    [Fact]
    public void Create_CanonicalizesEscapedStringsAndSeparatesJsonTypes_Issue4551()
    {
        var provider = CreateProvider();
        var nodeString = JsonNode.Parse("""{"id":"1"}""")!["id"];
        using var escapedDocument = JsonDocument.Parse("""{"id":"\u0031"}""");
        using var surrogatePairDocument = JsonDocument.Parse("""{"id":"\uD83D\uDE00"}""");
        using var numberDocument = JsonDocument.Parse("""{"id":1}""");

        var fromNode = provider.Create(nodeString);
        var fromElement = provider.Create(escapedDocument.RootElement.GetProperty("id"));
        var surrogatePair = provider.Create(surrogatePairDocument.RootElement.GetProperty("id"));
        var number = provider.Create(numberDocument.RootElement.GetProperty("id"));
        var nullLikeString = provider.Create(JsonValue.Create("null"));
        var explicitNull = provider.Create(id: (JsonNode?)null);

        Assert.Equal(fromNode.Token, fromElement.Token);
        Assert.Equal("string", fromElement.Type);
        Assert.Equal(1, fromElement.Length);
        Assert.Equal("string", surrogatePair.Type);
        Assert.Equal(2, surrogatePair.Length);
        Assert.Equal("number", number.Type);
        Assert.Equal(1, number.Length);
        Assert.NotEqual(fromElement.Token, number.Token);
        Assert.NotEqual(nullLikeString.Token, explicitNull.Token);
    }

    [Fact]
    public void Create_HighCardinalityIdsCollapseAfterDistinctTokenBudget_Issue4551()
    {
        const int distinctTokenLimit = 8;
        var provider = CreateProvider(maxDistinctTokens: distinctTokenLimit);
        var telemetry = new List<McpRequestIdTelemetryData>();

        for (var index = 0; index < 256; index++)
        {
            var requestId = $"unique-client-request-{index:D4}-" + new string('x', 64);
            var item = provider.Create(JsonValue.Create(requestId));

            Assert.Equal(McpRequestIdTelemetry.TokenLength, item.Token.Length);
            Assert.DoesNotContain(requestId, item.Token, StringComparison.Ordinal);
            telemetry.Add(item);
        }

        Assert.Equal(
            distinctTokenLimit + 1,
            telemetry.Select(static item => item.Token).Distinct(StringComparer.Ordinal).Count());
        var overflowToken = telemetry[distinctTokenLimit].Token;
        Assert.All(telemetry.Skip(distinctTokenLimit), item => Assert.Equal(overflowToken, item.Token));
        Assert.Equal(
            telemetry[0].Token,
            provider.Create(JsonValue.Create("unique-client-request-0000-" + new string('x', 64))).Token);
    }

    [Fact]
    public void Create_ConcurrentDistinctIdsCannotExceedTokenCardinalityBudget_Issue4551()
    {
        const int distinctTokenLimit = 8;
        var provider = CreateProvider(maxDistinctTokens: distinctTokenLimit);
        var telemetry = new ConcurrentBag<McpRequestIdTelemetryData>();

        Parallel.For(0, 256, index =>
        {
            telemetry.Add(provider.Create(JsonValue.Create($"parallel-request-{index:D4}")));
        });

        Assert.Equal(
            distinctTokenLimit + 1,
            telemetry.Select(static item => item.Token).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Create_DifferentProcessSaltsProduceDifferentTokens_Issue4551()
    {
        var first = CreateProvider(saltFill: 0x11, maxDistinctTokens: 1);
        var second = CreateProvider(saltFill: 0x22, maxDistinctTokens: 1);
        var id = JsonValue.Create("github_pat_4551_cross_process_salt");

        var firstToken = first.Create(id).Token;
        var secondToken = second.Create(id).Token;
        var firstOverflowToken = first.Create(JsonValue.Create("unseen-after-budget")).Token;
        var secondOverflowToken = second.Create(JsonValue.Create("unseen-after-budget")).Token;

        Assert.NotEqual(firstToken, secondToken);
        Assert.NotEqual(firstOverflowToken, secondOverflowToken);
    }

    [Fact]
    public void Create_ExplicitNullHasTypedZeroLengthToken_Issue4551()
    {
        var telemetry = CreateProvider().Create(id: (JsonNode?)null);

        Assert.Equal("null", telemetry.Type);
        Assert.Equal(0, telemetry.Length);
        Assert.Equal(McpRequestIdTelemetry.TokenLength, telemetry.Token.Length);
    }

    [Fact]
    public void Create_OversizedIdsUseSeparateHashDomainAndBoundedLength_Issue4551()
    {
        var provider = CreateProvider();
        var validLiteral = provider.Create(JsonValue.Create("oversized"));
        var firstOversized = provider.Create(JsonValue.Create(new string('x', McpServer.MaxRequestIdCharacterCount + 1)));
        var muchLarger = provider.Create(JsonValue.Create(new string('y', McpServer.MaxRequestIdCharacterCount * 16)));

        Assert.NotEqual(validLiteral.Token, firstOversized.Token);
        Assert.Equal(firstOversized.Token, muchLarger.Token);
        Assert.Equal("string", firstOversized.Type);
        Assert.Equal(McpServer.MaxRequestIdCharacterCount + 1, firstOversized.Length);
        Assert.Equal(McpServer.MaxRequestIdCharacterCount + 1, muchLarger.Length);
    }

    private static McpRequestIdTelemetryProvider CreateProvider(
        byte saltFill = 0x5a,
        int maxDistinctTokens = 1024)
        => new(
            Enumerable.Repeat(saltFill, McpRequestIdTelemetry.ProcessSaltBytes).ToArray(),
            maxDistinctTokens);
}
