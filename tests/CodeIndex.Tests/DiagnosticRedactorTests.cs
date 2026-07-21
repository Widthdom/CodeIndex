using System.Text.Json;
using CodeIndex.Diagnostics;

namespace CodeIndex.Tests;

public class DiagnosticRedactorTests
{
    [Fact]
    public void RedactSuggestionText_UsesSharedTypedPolicy_Issue3933()
    {
        var awsKey = "AKIA" + new string('A', 16);
        var bearer = "Bearer " + new string('b', 20);
        var highEntropy = "Abcdefghijklmnopqrstuvwxyz123456";
        var privateKeyValue = "visible4175";
        var text = $"key={awsKey} {bearer} api_key=secret private-key={privateKeyValue} {highEntropy}";

        var redacted = DiagnosticRedactor.RedactSuggestionText(text, out var redactedTypes);

        Assert.Contains(DiagnosticRedactor.SuggestionRedactedAwsAccessKey, redacted);
        Assert.Contains(DiagnosticRedactor.SuggestionRedactedBearerToken, redacted);
        Assert.Contains("api_key=" + DiagnosticRedactor.SuggestionRedactedCredential, redacted);
        Assert.Contains("private-key=" + DiagnosticRedactor.SuggestionRedactedCredential, redacted);
        Assert.Contains(DiagnosticRedactor.SuggestionRedactedHighEntropyToken, redacted);
        Assert.DoesNotContain(awsKey, redacted);
        Assert.DoesNotContain("secret", redacted);
        Assert.DoesNotContain(privateKeyValue, redacted);
        Assert.Equal(
            ["aws_access_key", "bearer_token", "credential", "high_entropy_token"],
            redactedTypes.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("PersistSuggestionsAtomically writes 12 records")]
    [InlineData("Recipe risky-code/environment-secret-source; representative result count 3")]
    public void RedactSuggestionText_DoesNotBorrowEntropySignalsFromLaterText_Issue4403(string text)
    {
        var redacted = DiagnosticRedactor.RedactSuggestionText(text, out var redactedTypes);

        Assert.Equal(text, redacted);
        Assert.Empty(redactedTypes);
    }

    [Theory]
    [InlineData("--github-token")]
    [InlineData("github_token")]
    [InlineData("bearer")]
    [InlineData("BearerToken")]
    [InlineData("api-token")]
    [InlineData("access_token")]
    [InlineData("AuthorizationHeader")]
    [InlineData("CDIDX_ACCESS_KEY")]
    [InlineData("serviceCredential")]
    [InlineData("authorization")]
    [InlineData("private-key")]
    [InlineData("session_cookie")]
    public void IsSensitiveName_CoversSharedCredentialNames_Issue3933_Issue4175(string name)
    {
        Assert.True(DiagnosticRedactor.IsSensitiveName(name));
    }

    [Theory]
    [InlineData("--workspace")]
    [InlineData("query")]
    [InlineData("session_id")]
    public void IsSensitiveName_LeavesNonCredentialNamesVisible_Issue4175(string name)
    {
        Assert.False(DiagnosticRedactor.IsSensitiveName(name));
    }

    [Theory]
    [InlineData("--private-key=visible4175", "--private-key=<redacted>")]
    [InlineData("accesskey=visible4175", "accesskey=<redacted>")]
    [InlineData("session_cookie=visible4175", "session_cookie=<redacted>")]
    [InlineData("authorization=visible4175", "authorization=<redacted>")]
    [InlineData("--private-key visible4175", "--private-key <redacted>")]
    public void RedactSensitiveText_RedactsSharedSensitiveNameAssignments_Issue4175(
        string input,
        string expected)
    {
        var redacted = DiagnosticRedactor.RedactSensitiveText(input);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("visible4175", redacted);
    }

    [Theory]
    [InlineData("Bearer bearer-secret-4299", "Bearer <redacted>", "bearer-secret-4299")]
    [InlineData("github_token=ghp_abcdefghijklmnopqrstuvwxyz", "github_token=<redacted>", "ghp_abcdefghijklmnopqrstuvwxyz")]
    [InlineData("api-token=hunter2", "api-token=<redacted>", "hunter2")]
    [InlineData("access_token=hunter2", "access_token=<redacted>", "hunter2")]
    [InlineData("AuthorizationHeader=hunter2", "AuthorizationHeader=<redacted>", "hunter2")]
    public void RedactSensitiveText_RedactsTokenAndAuthorizationVariants_Issue4299(
        string input,
        string expected,
        string secret)
    {
        var redacted = DiagnosticRedactor.RedactSensitiveText(input);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain(secret, redacted);
    }

    [Theory]
    [InlineData("https://host.test/path?token=hunter2", "https://host.test/path?token=<redacted>")]
    [InlineData("--workspace --token hunter2", "--workspace --token <redacted>")]
    public void RedactSensitiveText_DoesNotLetBenignPrefixesHideSharedSecrets_Issue4175(
        string input,
        string expected)
    {
        var redacted = DiagnosticRedactor.RedactSensitiveText(input);

        Assert.Equal(expected, redacted);
        Assert.DoesNotContain("hunter2", redacted);
    }

    [Fact]
    public void FormatExceptionMessage_RedactsPathsAndSecrets_Issue4124()
    {
        var exception = new InvalidOperationException("failed at /tmp/private/repo with --token=ghp_abcdefghijklmnopqrstuvwxyz");

        var message = DiagnosticRedactor.FormatExceptionMessage(exception);

        Assert.Contains("<path>", message);
        Assert.Contains("--token=<redacted>", message);
        Assert.DoesNotContain("/tmp/private", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ghp_", message, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatExceptionDetail_IncludesTypeWithoutLeakingRawMessage_Issue4124()
    {
        var exception = new IOException("cannot open C:/Users/me/secret.db because password=hunter2");

        var detail = DiagnosticRedactor.FormatExceptionDetail(exception);

        Assert.StartsWith("IOException: ", detail, StringComparison.Ordinal);
        Assert.Contains("<path>", detail);
        Assert.Contains("password=<redacted>", detail);
        Assert.DoesNotContain("C:/Users/me", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", detail, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatExceptionStackLine_LongSignaturePreservesRedactedSourceLocation()
    {
        var stackLine = $"at CodeIndex.Cli.IndexCommandRunner.RunFullScan({new string('x', 900)}) in C:\\private\\CodeIndex\\IndexCommandRunner.FullScan.cs:line 1880";

        var formatted = DiagnosticRedactor.FormatExceptionStackLine(stackLine, maxChars: 128);

        Assert.Contains("... <truncated; original length ", formatted, StringComparison.Ordinal);
        Assert.Contains(" in <redacted>:line 1880", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\private", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactSensitiveText_RedactsAuthorizationBearerHeader_Issue4134()
    {
        const string token = "secret-token-4134";

        var redacted = DiagnosticRedactor.RedactSensitiveText($"Authorization: Bearer {token}", "[redacted]");

        Assert.Equal("Authorization: Bearer [redacted]", redacted);
        Assert.DoesNotContain(token, redacted);
    }

    [Fact]
    public void RedactReportLogLine_JsonLineRedactsStrings_Issue3724()
    {
        var token = "ghp_" + new string('a', 24);
        var line = $$"""{"msg":"failed token={{token}} path=/tmp/private/repo","args":"--token {{token}} --path /tmp/private/repo","level":"error"}""";

        var redacted = DiagnosticRedactor.RedactReportLogLine(line, includeArgs: false);

        Assert.StartsWith("{", redacted);
        using var document = JsonDocument.Parse(redacted);
        Assert.Equal("error", document.RootElement.GetProperty("level").GetString());
        Assert.Equal("<redacted>", document.RootElement.GetProperty("args").GetString());
        Assert.Contains("<redacted>", document.RootElement.GetProperty("msg").GetString());
        Assert.DoesNotContain(token, redacted);
        Assert.DoesNotContain("/tmp/private/repo", redacted);
    }

    [Fact]
    public void RedactReportLogLine_DeepJsonFallsBackGracefully_Issue3724()
    {
        var token = "ghp_" + new string('b', 24);
        var depth = DiagnosticRedactor.MaxReportLogJsonDepth + 4;
        var nested = new string('[', depth) + "\"" + token + "\"" + new string(']', depth);
        var line = $"{{\"msg\":\"token={token}\",\"nested\":{nested}}}";

        var redacted = DiagnosticRedactor.RedactReportLogLine(line, includeArgs: false);

        Assert.Contains("<redacted>", redacted);
        Assert.DoesNotContain(token, redacted);
    }

    [Fact]
    public void RedactReportLogLine_OversizedJsonReturnsMarker_Issue3724()
    {
        var token = "ghp_" + new string('c', 24);
        var line = $"{{\"msg\":\"token={token}\",\"padding\":\"{new string('x', DiagnosticRedactor.MaxReportLogJsonLineChars)}\"}}";

        var redacted = DiagnosticRedactor.RedactReportLogLine(line, includeArgs: false);

        using var document = JsonDocument.Parse(redacted);
        Assert.Equal("json_line_too_large", document.RootElement.GetProperty("redaction").GetString());
        Assert.Equal(DiagnosticRedactor.MaxReportLogJsonLineChars, document.RootElement.GetProperty("max_length").GetInt32());
        Assert.DoesNotContain(token, redacted);
    }
}
