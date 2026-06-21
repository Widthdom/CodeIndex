using System.Text.Json;
using CodeIndex.Diagnostics;

namespace CodeIndex.Tests;

public class DiagnosticRedactorTests
{
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
